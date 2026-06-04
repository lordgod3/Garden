# =============================================================================
# screentime.py — Moteur d'Analyse du Temps d'Écran & Dépassements de Quota
# Projet  : GARDEN — Système de Conscience Temporelle
# Couche  : src/GARDEN.Native/python/screentime.py
# Runtime : Python 3.8+  (Chaquopy, thread background C#)
# Auteur  : Dev 3 — Couche Native
# Version : 1.0.0
#
# API publique (appelée par MoteurPython.cs via Chaquopy) :
#   quota_depasse(payload_json: str) -> bool
#   analyser_temps_ecran(payload_json: str) -> str   [JSON détaillé]
#
# Format du payload JSON attendu :
#   {
#     "applications": [
#       {"nom": str, "secondes": int, "categorie": "social|...|autre"},
#       ...
#     ],
#     "quota_secondes": int,                 (quota global journalier)
#     "quotas_par_categorie": {"social": int, ...}   (optionnel)
#   }
#
# HYPOTHÈSES DOCUMENTÉES :
#   H1. Le cahier des charges cible explicitement les réseaux sociaux
#       ("détection des dépassements de quotas sur les applications de
#       réseaux sociaux"). La catégorie "social" est donc traitée à part.
#   H2. Le dépassement global est prioritaire : si le total dépasse le quota
#       global, quota_depasse() renvoie True quelle que soit la répartition.
#   H3. Les durées négatives, non numériques ou aberrantes sont neutralisées
#       (coercition vers 0) plutôt que de fausser ou faire planter le calcul.
# =============================================================================

from __future__ import annotations

import json

from _garden_common import (
    charger_objet_json,
    en_chaine,
    en_entier_positif,
    extraire_liste,
    iter_dictionnaires,
)

#: Catégorie ciblée en priorité par GARDEN (cahier des charges).
_CATEGORIE_SOCIALE = "social"

#: Garde-fou DoS : nombre maximal d'applications analysées.
_MAX_APPLICATIONS = 1000

#: Plafond de durée par application : 24 h en secondes (anti-données aberrantes).
_MAX_SECONDES_APP = 24 * 3600


def _parser(payload_json: str) -> "tuple[list[tuple[str, int, str]], int, dict[str, int]]":
    """
    Désérialise applications, quota global et quotas par catégorie.

    Returns:
        (apps, quota_global, quotas_categorie) où apps est une liste de
        triplets (nom, secondes, categorie).

    Raises:
        GardenPayloadError: si le JSON racine est invalide.
    """
    data = charger_objet_json(payload_json)

    quota_global = en_entier_positif(data.get("quota_secondes"), defaut=0)

    quotas_cat_brut = data.get("quotas_par_categorie", {})
    quotas_categorie: "dict[str, int]" = {}
    if isinstance(quotas_cat_brut, dict):
        for cle, val in quotas_cat_brut.items():
            quotas_categorie[en_chaine(cle).strip().lower()] = en_entier_positif(val)

    apps: "list[tuple[str, int, str]]" = []
    for raw in iter_dictionnaires(extraire_liste(data, "applications", _MAX_APPLICATIONS)):
        secondes = min(en_entier_positif(raw.get("secondes")), _MAX_SECONDES_APP)  # H3
        nom = en_chaine(raw.get("nom"), "inconnue")
        categorie = en_chaine(raw.get("categorie"), "autre").strip().lower()
        apps.append((nom, secondes, categorie))

    return apps, quota_global, quotas_categorie


def quota_depasse(payload_json: str) -> bool:
    """
    Indique si un quota (global OU catégoriel) est dépassé.

    H2 : le dépassement global prime. À défaut, on vérifie chaque catégorie
    disposant d'un quota défini.

    Returns:
        True si au moins un quota est dépassé, False sinon.
        Retourne False si aucun quota n'est défini (rien à faire respecter).

    Raises:
        GardenPayloadError: si le JSON est invalide.
    """
    apps, quota_global, quotas_categorie = _parser(payload_json)
    total = sum(s for _, s, _ in apps)

    if quota_global > 0 and total > quota_global:
        return True

    if quotas_categorie:
        cumul_par_cat: "dict[str, int]" = {}
        for _, secondes, categorie in apps:
            cumul_par_cat[categorie] = cumul_par_cat.get(categorie, 0) + secondes
        for categorie, quota in quotas_categorie.items():
            if quota > 0 and cumul_par_cat.get(categorie, 0) > quota:
                return True

    return False


def analyser_temps_ecran(payload_json: str) -> str:
    """
    Analyse détaillée du temps d'écran, au format JSON.

    Returns:
        JSON : {"total_secondes", "social_secondes", "quota_global_secondes",
                "depasse", "depassement_secondes", "categories": {...}}.
    """
    apps, quota_global, quotas_categorie = _parser(payload_json)

    total = sum(s for _, s, _ in apps)
    social = sum(s for _, s, c in apps if c == _CATEGORIE_SOCIALE)

    categories: "dict[str, int]" = {}
    for _, secondes, categorie in apps:
        categories[categorie] = categories.get(categorie, 0) + secondes

    depasse = quota_depasse(payload_json)
    depassement = max(0, total - quota_global) if quota_global > 0 else 0

    return json.dumps(
        {
            "total_secondes": total,
            "social_secondes": social,
            "quota_global_secondes": quota_global,
            "depasse": depasse,
            "depassement_secondes": depassement,
            "categories": categories,
        },
        ensure_ascii=False,
        separators=(",", ":"),
    )


__all__ = ["quota_depasse", "analyser_temps_ecran"]
