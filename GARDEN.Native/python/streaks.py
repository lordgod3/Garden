# =============================================================================
# streaks.py — Moteur de Séries (Streaks) d'Habitudes
# Projet  : GARDEN — Système de Conscience Temporelle
# Couche  : src/GARDEN.Native/python/streaks.py
# Runtime : Python 3.8+  (Chaquopy, thread background C#)
# Auteur  : Dev 3 — Couche Native
# Version : 1.0.0
#
# API publique (appelée par MoteurPython.cs via Chaquopy) :
#   calculer_streak(payload_json: str) -> int
#   calculer_meilleur_streak(payload_json: str) -> int
#   analyser_streaks(payload_json: str) -> str   [JSON détaillé]
#
# Format du payload JSON attendu :
#   {
#     "jours_accomplis": ["YYYY-MM-DD", ...],   (dates ISO 8601)
#     "date_reference":  "YYYY-MM-DD"           (optionnel ; défaut = aujourd'hui)
#   }
#
# HYPOTHÈSES DOCUMENTÉES :
#   H1. La "série courante" se compte à rebours depuis la date de référence.
#       Une habitude faite hier mais pas aujourd'hui garde sa série intacte
#       (la journée courante n'est pas encore "perdue" tant qu'elle dure).
#       => On tolère un démarrage de comptage à `date_reference` OU la veille.
#   H2. `date_reference` est injectable pour rendre le calcul déterministe et
#       testable, et pour gérer les fuseaux/jours logiques décidés par la
#       couche C# (ex. gardes de nuit). Si absent, on retombe sur date.today(),
#       avec l'avertissement que cela dépend du fuseau du device.
#   H3. Les doublons de dates sont dédupliqués ; les dates futures (au-delà de
#       la référence) sont ignorées (anti-triche / données incohérentes).
# =============================================================================

from __future__ import annotations

import json
from datetime import date, timedelta

from _garden_common import (
    charger_objet_json,
    en_chaine,
    extraire_liste,
)

#: Garde-fou DoS : on ne traite jamais plus de ~10 ans d'historique.
_MAX_JOURS = 4000


def _parser_date_iso(valeur: str) -> "date | None":
    """Parse une date ISO 'YYYY-MM-DD'. Retourne None si invalide (sans lever)."""
    valeur = valeur.strip()
    if len(valeur) != 10:
        return None
    try:
        return date.fromisoformat(valeur)
    except ValueError:
        return None


def _extraire_jours(payload_json: str) -> "tuple[list[date], date]":
    """
    Désérialise les jours accomplis (dédupliqués, triés) et la date de réf.

    Returns:
        (jours_uniques_tries_desc, date_reference)

    Raises:
        GardenPayloadError: si le JSON racine est invalide.
    """
    data = charger_objet_json(payload_json)

    # Date de référence (H2) : injectable pour déterminisme et fuseaux.
    ref_brut = en_chaine(data.get("date_reference"))
    reference = _parser_date_iso(ref_brut) or date.today()

    bruts = extraire_liste(data, "jours_accomplis", _MAX_JOURS)
    jours: "set[date]" = set()
    for valeur in bruts:
        d = _parser_date_iso(en_chaine(valeur))
        if d is not None and d <= reference:  # H3 : ignorer le futur
            jours.add(d)

    return sorted(jours, reverse=True), reference


def _streak_courant(jours_desc: "list[date]", reference: date) -> int:
    """
    Longueur de la série courante se terminant à `reference` (ou la veille).

    H1 : la série reste valide si la dernière complétion est aujourd'hui OU
    hier (la journée courante n'est pas encore manquée tant qu'elle dure).
    """
    if not jours_desc:
        return 0

    plus_recent = jours_desc[0]
    ecart_initial = (reference - plus_recent).days
    if ecart_initial > 1:
        return 0  # dernière complétion trop ancienne : série rompue

    streak = 0
    curseur = plus_recent
    for j in jours_desc:
        if j == curseur:
            streak += 1
            curseur -= timedelta(days=1)
        elif j < curseur:
            break  # trou dans la séquence : fin de la série
        # j > curseur impossible (liste triée desc, dédupliquée)
    return streak


def _meilleur_streak(jours_desc: "list[date]") -> int:
    """Plus longue série historique de jours consécutifs."""
    if not jours_desc:
        return 0
    meilleur = courant = 1
    for i in range(1, len(jours_desc)):
        if jours_desc[i] == jours_desc[i - 1] - timedelta(days=1):
            courant += 1
            meilleur = max(meilleur, courant)
        else:
            courant = 1
    return meilleur


# -----------------------------------------------------------------------------
# API publique (frontière Chaquopy)
# -----------------------------------------------------------------------------

def calculer_streak(payload_json: str) -> int:
    """
    Série courante (jours consécutifs accomplis) jusqu'à la date de référence.

    Returns:
        Entier >= 0.

    Raises:
        GardenPayloadError: si le JSON est invalide.
    """
    jours, reference = _extraire_jours(payload_json)
    return _streak_courant(jours, reference)


def calculer_meilleur_streak(payload_json: str) -> int:
    """Plus longue série jamais réalisée (record historique). Entier >= 0."""
    jours, _ = _extraire_jours(payload_json)
    return _meilleur_streak(jours)


def analyser_streaks(payload_json: str) -> str:
    """
    Analyse complète des séries, au format JSON.

    Returns:
        JSON : {"streak_courant", "meilleur_streak", "total_jours",
                "actif_aujourdhui"}.
    """
    jours, reference = _extraire_jours(payload_json)
    actif = bool(jours) and (reference - jours[0]).days == 0
    return json.dumps(
        {
            "streak_courant": _streak_courant(jours, reference),
            "meilleur_streak": _meilleur_streak(jours),
            "total_jours": len(jours),
            "actif_aujourdhui": actif,
        },
        ensure_ascii=False,
        separators=(",", ":"),
    )


__all__ = [
    "calculer_streak",
    "calculer_meilleur_streak",
    "analyser_streaks",
]
