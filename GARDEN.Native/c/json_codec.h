/* ============================================================================
 * json_codec.h - Codec de trame de planning GARDEN (C ANSI / C89)
 * Projet  : GARDEN - Systeme de Conscience Temporelle
 * Couche  : src/GARDEN.Native/c/json_codec.h
 * Cible   : bibliotheque native Android (NDK) ; format partage avec le
 *           firmware ESP32 (C/C++).
 * Auteur  : Dev 3 - Couche Native
 * Version : 1.0.0
 *
 * ROLE
 *   Serialise une liste de creneaux de planning en trame JSON compacte,
 *   calcule un controle d'integrite (CRC-32), et applique un chiffrement
 *   FOURNI PAR L'APPELANT avant emission BLE vers l'horloge ESP32.
 *
 * DECISION DE SECURITE (importante)
 *   Ce module NE CONTIENT PAS d'implementation cryptographique. Rouler sa
 *   propre AES est une faute de securite classique. Le chiffrement est
 *   delegue via un pointeur de fonction (garden_cipher_fn) que l'integrateur
 *   branche sur une bibliotheque eprouvee :
 *     - cote Android : mbedTLS (deja disponible) ou l'AES du systeme ;
 *     - cote ESP32   : l'accelerateur AES materiel (esp_aes).
 *   Le codec garantit la serialisation, le cadrage et l'integrite ; la
 *   confidentialite est la responsabilite du cipher injecte.
 *
 * THREAD-SAFETY
 *   Toutes les fonctions sont reentrantes : aucun etat global mutable. Le
 *   contexte est passe explicitement par l'appelant. Plusieurs threads
 *   peuvent encoder en parallele avec des buffers distincts (utile pour la
 *   file d'ecriture BLE de Dev 2).
 *
 * GESTION MEMOIRE
 *   Aucune allocation dynamique interne. L'appelant fournit tous les buffers
 *   et leur capacite ; le codec ecrit de facon bornee (jamais de depassement)
 *   et renvoie la taille reellement ecrite via un parametre de sortie.
 * ==========================================================================*/

#ifndef GARDEN_JSON_CODEC_H
#define GARDEN_JSON_CODEC_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* --------------------------------------------------------------------------
 * Versionnement du protocole de trame (DOIT rester synchronise avec le
 * firmware ESP32). Tout changement incompatible incremente la version
 * majeure ; un decodeur firmware rejette une version majeure inconnue.
 * ------------------------------------------------------------------------ */
#define GARDEN_PROTOCOL_VERSION 1

/* Contrainte du cahier des charges : 72 blocs de 30 min sur 3 jours. */
#define GARDEN_MAX_SLOTS 72

/* Longueurs maximales bornees (anti-overflow) pour les champs texte. */
#define GARDEN_MAX_LABEL_LEN 64
#define GARDEN_MAX_ID_LEN    40

/* --------------------------------------------------------------------------
 * Codes de retour. Toute fonction publique renvoie un garden_status_t.
 * GARDEN_OK == 0 ; toute valeur non nulle est une erreur.
 * ------------------------------------------------------------------------ */
typedef enum garden_status_e {
    GARDEN_OK                 = 0,
    GARDEN_ERR_NULL_ARG       = 1,  /* pointeur requis NULL                 */
    GARDEN_ERR_TOO_MANY_SLOTS = 2,  /* count > GARDEN_MAX_SLOTS             */
    GARDEN_ERR_BUFFER_TOO_SMALL = 3,/* capacite de sortie insuffisante      */
    GARDEN_ERR_INVALID_SLOT   = 4,  /* champ de creneau invalide            */
    GARDEN_ERR_CIPHER_FAILED  = 5,  /* le cipher injecte a echoue           */
    GARDEN_ERR_OVERFLOW       = 6,  /* depassement de taille detecte        */
    GARDEN_ERR_BAD_VERSION    = 7   /* version de protocole non supportee   */
} garden_status_t;

/* --------------------------------------------------------------------------
 * Types de creneaux, encodes sur 1 caractere dans la trame (compacite BLE).
 * Doit rester aligne avec l'enum SlotType cote C# et le firmware.
 * ------------------------------------------------------------------------ */
typedef enum garden_slot_type_e {
    GARDEN_SLOT_FOCUS    = 'F',
    GARDEN_SLOT_REUNION  = 'R',
    GARDEN_SLOT_PAUSE    = 'P',
    GARDEN_SLOT_HABITUDE = 'H',
    GARDEN_SLOT_BIENETRE = 'W'  /* W = Wellbeing (bien-etre) */
} garden_slot_type_t;

/* --------------------------------------------------------------------------
 * Representation C d'un creneau. Structure plate, copiable, sans pointeur
 * proprietaire : l'appelant possede la memoire.
 *   start_min / end_min : minutes depuis minuit [0..1439].
 *   day_index           : 0, 1 ou 2 (jour dans la fenetre de 3 jours).
 * ------------------------------------------------------------------------ */
typedef struct garden_slot_s {
    char               id[GARDEN_MAX_ID_LEN];
    garden_slot_type_t type;
    uint16_t           start_min;
    uint16_t           end_min;
    uint8_t            day_index;
    uint8_t            block_notif; /* 0 ou 1 */
} garden_slot_t;

/* --------------------------------------------------------------------------
 * Contrat du chiffreur injecte (secure-by-design : crypto externe et eprouvee).
 *
 *   in / in_len    : texte clair a chiffrer.
 *   out            : buffer de sortie fourni par le codec.
 *   out_cap        : capacite de `out`.
 *   out_len        : (sortie) nombre d'octets ecrits.
 *   user_ctx       : contexte opaque (cle, IV, handle mbedTLS...).
 *
 * Retour : 0 en cas de succes, non nul en cas d'echec.
 *
 * Un cipher "identite" de TEST est fourni dans les tests ; il ne DOIT JAMAIS
 * etre utilise en production.
 * ------------------------------------------------------------------------ */
typedef int (*garden_cipher_fn)(const uint8_t *in, size_t in_len,
                                uint8_t *out, size_t out_cap, size_t *out_len,
                                void *user_ctx);

/* --------------------------------------------------------------------------
 * Contexte d'encodage (reentrant). L'appelant l'initialise puis le passe a
 * chaque appel. `cipher` peut etre NULL : la trame est alors emise en clair
 * (utile en developpement ; A PROSCRIRE en production - voir README).
 * ------------------------------------------------------------------------ */
typedef struct garden_codec_ctx_s {
    garden_cipher_fn cipher;       /* peut etre NULL (clair, dev seulement) */
    void            *cipher_ctx;   /* contexte opaque passe au cipher       */
    uint32_t         timestamp;    /* epoch s ; horodatage de la trame      */
} garden_codec_ctx_t;

/* ==========================================================================
 * API PUBLIQUE
 * ========================================================================*/

/*
 * Serialise les creneaux en JSON compact (sans chiffrement).
 *
 * Ecrit dans `out` une chaine JSON terminee par '\0' de la forme :
 *   {"v":1,"ts":<ts>,"n":<count>,"slots":[
 *      {"id":"...","t":"F","d":0,"s":360,"e":390,"b":1}, ...],
 *    "crc":<crc32_hex>}
 *
 * Parametres
 *   ctx     : contexte (timestamp utilise) ; non NULL.
 *   slots   : tableau de `count` creneaux ; non NULL si count > 0.
 *   count   : nombre de creneaux [0..GARDEN_MAX_SLOTS].
 *   out     : buffer de sortie ; non NULL.
 *   out_cap : capacite de `out` (octets, '\0' inclus).
 *   out_len : (sortie) longueur ecrite hors '\0' ; non NULL.
 *
 * Retour : GARDEN_OK ou un code d'erreur. En cas d'erreur, `out` peut
 *          contenir des donnees partielles mais reste toujours termine par
 *          '\0' si out_cap > 0.
 */
garden_status_t garden_encode_json(const garden_codec_ctx_t *ctx,
                                   const garden_slot_t *slots, size_t count,
                                   char *out, size_t out_cap, size_t *out_len);

/*
 * Encode puis chiffre la trame complete prete pour l'emission BLE.
 *
 * Etapes : serialisation JSON -> (si ctx->cipher != NULL) chiffrement via le
 * cipher injecte. Si ctx->cipher est NULL, copie le JSON clair dans `out`.
 *
 * Parametres analogues a garden_encode_json ; `out` recoit des octets
 * binaires (PAS forcement une chaine C apres chiffrement).
 *
 * Retour : GARDEN_OK ou code d'erreur (dont GARDEN_ERR_CIPHER_FAILED).
 */
garden_status_t garden_encode_frame(const garden_codec_ctx_t *ctx,
                                    const garden_slot_t *slots, size_t count,
                                    uint8_t *out, size_t out_cap, size_t *out_len);

/*
 * Calcule le CRC-32 (polynome IEEE 802.3, reflechi) d'un tampon.
 * Expose pour permettre au firmware/aux tests de verifier l'integrite.
 */
uint32_t garden_crc32(const uint8_t *data, size_t len);

/*
 * Retourne une chaine statique decrivant un code de statut (diagnostics/logs).
 * La memoire renvoyee est statique : ne pas liberer.
 */
const char *garden_status_str(garden_status_t status);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* GARDEN_JSON_CODEC_H */
