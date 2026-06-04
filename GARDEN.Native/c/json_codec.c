/* ============================================================================
 * json_codec.c - Implementation du codec de trame de planning GARDEN
 * Projet  : GARDEN - Systeme de Conscience Temporelle
 * Couche  : src/GARDEN.Native/c/json_codec.c
 * Norme   : C89/ANSI, portable (NDK arm64-v8a / x86_64, et hote PC pour tests)
 * Auteur  : Dev 3 - Couche Native
 * Version : 1.0.0
 *
 * GARANTIES
 *   - Aucune allocation dynamique (pas de malloc/free).
 *   - Aucun etat global mutable -> reentrant et thread-safe.
 *   - Ecritures bornees uniquement (jamais de depassement de buffer).
 *   - Verification systematique des arguments et des capacites.
 *   - Echappement JSON des chaines (anti-injection dans la trame).
 * ==========================================================================*/

#include "json_codec.h"

#include <string.h>

/* --------------------------------------------------------------------------
 * Table CRC-32 (IEEE 802.3, reflechi) calculee a la volee une seule fois par
 * appel sans etat global mutable. On utilise l'algorithme bit-a-bit pour
 * eviter toute table statique partagee entre threads (reentrance stricte).
 * ------------------------------------------------------------------------ */
uint32_t garden_crc32(const uint8_t *data, size_t len)
{
    uint32_t crc = 0xFFFFFFFFu;
    size_t i;
    int b;

    if (data == NULL) {
        return 0u;
    }
    for (i = 0; i < len; ++i) {
        crc ^= (uint32_t)data[i];
        for (b = 0; b < 8; ++b) {
            uint32_t mask = (uint32_t)-(int32_t)(crc & 1u);
            crc = (crc >> 1) ^ (0xEDB88320u & mask);
        }
    }
    return ~crc;
}

const char *garden_status_str(garden_status_t status)
{
    switch (status) {
        case GARDEN_OK:                  return "OK";
        case GARDEN_ERR_NULL_ARG:        return "argument NULL";
        case GARDEN_ERR_TOO_MANY_SLOTS:  return "trop de creneaux (> 72)";
        case GARDEN_ERR_BUFFER_TOO_SMALL:return "buffer de sortie trop petit";
        case GARDEN_ERR_INVALID_SLOT:    return "creneau invalide";
        case GARDEN_ERR_CIPHER_FAILED:   return "echec du chiffreur";
        case GARDEN_ERR_OVERFLOW:        return "depassement detecte";
        case GARDEN_ERR_BAD_VERSION:     return "version de protocole invalide";
        default:                         return "erreur inconnue";
    }
}

/* --------------------------------------------------------------------------
 * Ecrivain de tampon borne. Toute ecriture passe par lui ; il refuse
 * silencieusement de depasser la capacite et signale le debordement via le
 * drapeau `overflow`. Aucune ecriture hors limites n'est jamais possible.
 * ------------------------------------------------------------------------ */
typedef struct buf_writer_s {
    char  *dst;
    size_t cap;       /* capacite totale */
    size_t len;       /* octets ecrits */
    int    overflow;  /* 1 si une ecriture a ete tronquee */
} buf_writer_t;

static void bw_init(buf_writer_t *w, char *dst, size_t cap)
{
    w->dst = dst;
    w->cap = cap;
    w->len = 0;
    w->overflow = 0;
}

static void bw_putc(buf_writer_t *w, char c)
{
    if (w->len + 1u >= w->cap) { /* +1 pour reserver le '\0' final */
        w->overflow = 1;
        return;
    }
    w->dst[w->len++] = c;
}

static void bw_puts(buf_writer_t *w, const char *s)
{
    while (*s != '\0') {
        bw_putc(w, *s++);
        if (w->overflow) {
            return;
        }
    }
}

/* Ecrit un entier non signe en base 10, sans printf (portabilite/securite). */
static void bw_put_uint(buf_writer_t *w, uint32_t value)
{
    char tmp[10];
    int n = 0;
    if (value == 0u) {
        bw_putc(w, '0');
        return;
    }
    while (value > 0u && n < (int)sizeof(tmp)) {
        tmp[n++] = (char)('0' + (value % 10u));
        value /= 10u;
    }
    while (n > 0) {
        bw_putc(w, tmp[--n]);
    }
}

/* Ecrit un entier en hexadecimal 8 chiffres (CRC). */
static void bw_put_hex8(buf_writer_t *w, uint32_t value)
{
    static const char hexd[] = "0123456789abcdef";
    int shift;
    for (shift = 28; shift >= 0; shift -= 4) {
        bw_putc(w, hexd[(value >> shift) & 0xFu]);
    }
}

/* Echappe et ecrit une chaine JSON entre guillemets (anti-injection). */
static void bw_put_json_string(buf_writer_t *w, const char *s, size_t max_len)
{
    size_t i;
    bw_putc(w, '"');
    for (i = 0; i < max_len && s[i] != '\0'; ++i) {
        unsigned char c = (unsigned char)s[i];
        switch (c) {
            case '"':  bw_puts(w, "\\\""); break;
            case '\\': bw_puts(w, "\\\\"); break;
            case '\n': bw_puts(w, "\\n");  break;
            case '\r': bw_puts(w, "\\r");  break;
            case '\t': bw_puts(w, "\\t");  break;
            default:
                if (c < 0x20u) {
                    /* Caractere de controle : echappement \u00XX. */
                    static const char hexd[] = "0123456789abcdef";
                    bw_puts(w, "\\u00");
                    bw_putc(w, hexd[(c >> 4) & 0xFu]);
                    bw_putc(w, hexd[c & 0xFu]);
                } else {
                    bw_putc(w, (char)c);
                }
                break;
        }
        if (w->overflow) {
            return;
        }
    }
    bw_putc(w, '"');
}

/* Valide qu'un type de creneau est l'un des cinq types officiels. */
static int slot_type_valide(garden_slot_type_t t)
{
    return t == GARDEN_SLOT_FOCUS || t == GARDEN_SLOT_REUNION ||
           t == GARDEN_SLOT_PAUSE || t == GARDEN_SLOT_HABITUDE ||
           t == GARDEN_SLOT_BIENETRE;
}

/* Valide les bornes temporelles d'un creneau. */
static int slot_valide(const garden_slot_t *s)
{
    if (!slot_type_valide(s->type)) {
        return 0;
    }
    if (s->start_min > 1439u || s->end_min > 1439u) {
        return 0;
    }
    if (s->day_index > 2u) {
        return 0;
    }
    if (s->block_notif > 1u) {
        return 0;
    }
    return 1;
}

garden_status_t garden_encode_json(const garden_codec_ctx_t *ctx,
                                   const garden_slot_t *slots, size_t count,
                                   char *out, size_t out_cap, size_t *out_len)
{
    buf_writer_t w;
    size_t i;
    uint32_t crc;

    if (ctx == NULL || out == NULL || out_len == NULL) {
        return GARDEN_ERR_NULL_ARG;
    }
    if (count > 0 && slots == NULL) {
        return GARDEN_ERR_NULL_ARG;
    }
    if (count > (size_t)GARDEN_MAX_SLOTS) {
        return GARDEN_ERR_TOO_MANY_SLOTS;
    }
    if (out_cap == 0u) {
        return GARDEN_ERR_BUFFER_TOO_SMALL;
    }

    /* Valider tous les creneaux AVANT d'ecrire quoi que ce soit. */
    for (i = 0; i < count; ++i) {
        if (!slot_valide(&slots[i])) {
            out[0] = '\0';
            *out_len = 0;
            return GARDEN_ERR_INVALID_SLOT;
        }
    }

    bw_init(&w, out, out_cap);

    bw_puts(&w, "{\"v\":");
    bw_put_uint(&w, (uint32_t)GARDEN_PROTOCOL_VERSION);
    bw_puts(&w, ",\"ts\":");
    bw_put_uint(&w, ctx->timestamp);
    bw_puts(&w, ",\"n\":");
    bw_put_uint(&w, (uint32_t)count);
    bw_puts(&w, ",\"slots\":[");

    for (i = 0; i < count; ++i) {
        const garden_slot_t *s = &slots[i];
        if (i > 0) {
            bw_putc(&w, ',');
        }
        bw_puts(&w, "{\"id\":");
        bw_put_json_string(&w, s->id, (size_t)GARDEN_MAX_ID_LEN);
        bw_puts(&w, ",\"t\":\"");
        bw_putc(&w, (char)s->type);
        bw_puts(&w, "\",\"d\":");
        bw_put_uint(&w, (uint32_t)s->day_index);
        bw_puts(&w, ",\"s\":");
        bw_put_uint(&w, (uint32_t)s->start_min);
        bw_puts(&w, ",\"e\":");
        bw_put_uint(&w, (uint32_t)s->end_min);
        bw_puts(&w, ",\"b\":");
        bw_put_uint(&w, (uint32_t)s->block_notif);
        bw_putc(&w, '}');
        if (w.overflow) {
            break;
        }
    }
    bw_puts(&w, "]");

    /* CRC sur tout ce qui precede (hors champ crc lui-meme). */
    crc = garden_crc32((const uint8_t *)w.dst, w.len);
    bw_puts(&w, ",\"crc\":\"");
    bw_put_hex8(&w, crc);
    bw_puts(&w, "\"}");

    /* Terminaison garantie : il reste toujours 1 octet (voir bw_putc). */
    w.dst[w.len] = '\0';
    *out_len = w.len;

    if (w.overflow) {
        return GARDEN_ERR_BUFFER_TOO_SMALL;
    }
    return GARDEN_OK;
}

garden_status_t garden_encode_frame(const garden_codec_ctx_t *ctx,
                                    const garden_slot_t *slots, size_t count,
                                    uint8_t *out, size_t out_cap, size_t *out_len)
{
    /* Tampon de travail pour le JSON clair. Dimensionne pour le pire cas :
     * en-tete + 72 creneaux pleins + crc. ~120 octets/creneau majore. */
    char json[GARDEN_MAX_SLOTS * 128 + 128];
    size_t json_len = 0;
    garden_status_t st;

    if (ctx == NULL || out == NULL || out_len == NULL) {
        return GARDEN_ERR_NULL_ARG;
    }

    st = garden_encode_json(ctx, slots, count, json, sizeof(json), &json_len);
    if (st != GARDEN_OK) {
        *out_len = 0;
        return st;
    }

    if (ctx->cipher == NULL) {
        /* Mode clair (developpement uniquement). */
        if (json_len > out_cap) {
            *out_len = 0;
            return GARDEN_ERR_BUFFER_TOO_SMALL;
        }
        memcpy(out, json, json_len);
        *out_len = json_len;
        return GARDEN_OK;
    }

    /* Chiffrement delegue (mbedTLS / AES materiel ESP32). */
    {
        int rc = ctx->cipher((const uint8_t *)json, json_len,
                             out, out_cap, out_len, ctx->cipher_ctx);
        if (rc != 0) {
            *out_len = 0;
            return GARDEN_ERR_CIPHER_FAILED;
        }
    }
    return GARDEN_OK;
}
