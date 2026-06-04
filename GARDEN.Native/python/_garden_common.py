# =============================================================================
# _garden_common.py — Utilitaires partagés de la couche analytique GARDEN
# Projet  : GARDEN — Système de Conscience Temporelle
# Couche  : src/GARDEN.Native/python/_garden_common.py
# Runtime : Python 3.8+  (exécuté via Chaquopy dans la JVM Android)
# Auteur  : Dev 3 — Couche Native
# Version : 1.0.0
#
# Rôle : centralise la désérialisation défensive, la validation et la
#        normalisation des données brutes envoyées par la couche C#.
#        Aucun des trois modules analytiques (scores, streaks, screentime)
#        ne réimplémente cette logique — principe DRY + surface d'attaque
#        unique pour le durcissement (validation, anti-injection JSON).
#
# Contrat de robustesse : AUCUNE fonction de ce module ne doit propager une
# exception non documentée. Un payload corrompu venant de C# ne doit JAMAIS
# faire planter le runtime Python embarqué (un crash Python = crash de l'.apk).
# =============================================================================

from __future__ import annotations

import json
from typing import Any, Iterable

# -----------------------------------------------------------------------------
# Constantes métier partagées (cahier des charges GARDEN)
# -----------------------------------------------------------------------------

#: Types de créneaux officiels. Toute valeur hors de cet ensemble est rejetée.
TYPES_CRENEAU_VALIDES: "frozenset[str]" = frozenset(
    {"Focus", "Reunion", "Pause", "Habitude", "BienEtre"}
)

#: Contrainte projet : 72 blocs de 30 min sur 3 jours => 24 blocs / jour.
MAX_CRENEAUX_PAR_JOUR: int = 24

#: Garde-fou anti-déni-de-service : on n'accepte jamais un payload démesuré
#: même si la couche C# en envoyait un (taille en caractères du JSON brut).
MAX_TAILLE_PAYLOAD_OCTETS: int = 256 * 1024  # 256 Kio, très au-delà du besoin réel


class GardenPayloadError(ValueError):
    """
    Erreur de validation d'un payload reçu de la couche C#.

    Sous-classe de ValueError pour que les wrappers Interop existants qui
    interceptent ValueError continuent de fonctionner sans changement de
    contrat. Le message est volontairement générique côté production pour
    éviter toute divulgation d'information (information disclosure).
    """


# -----------------------------------------------------------------------------
# Parsing JSON défensif
# -----------------------------------------------------------------------------

def charger_objet_json(payload_json: str) -> "dict[str, Any]":
    """
    Désérialise un payload JSON en dictionnaire, de façon défensive.

    Args:
        payload_json: chaîne JSON brute fournie par la couche C# (Chaquopy).

    Returns:
        Le dictionnaire racine désérialisé.

    Raises:
        GardenPayloadError: si l'entrée n'est pas une chaîne, dépasse la
            taille maximale autorisée, n'est pas un JSON valide, ou ne
            représente pas un objet JSON.
    """
    if not isinstance(payload_json, str):
        raise GardenPayloadError("Le payload doit être une chaîne JSON.")

    # Garde-fou DoS : refuser les entrées démesurées avant tout parsing.
    if len(payload_json) > MAX_TAILLE_PAYLOAD_OCTETS:
        raise GardenPayloadError("Payload trop volumineux.")

    try:
        data = json.loads(payload_json)
    except json.JSONDecodeError as exc:
        # On ne réexpose pas l'offset/détail de l'erreur en production
        # (limite la divulgation d'information sur les données internes).
        raise GardenPayloadError("Payload JSON invalide.") from exc

    if not isinstance(data, dict):
        raise GardenPayloadError("La racine du payload doit être un objet JSON.")

    return data


# -----------------------------------------------------------------------------
# Coercition de types sûre
# -----------------------------------------------------------------------------

def en_bool(valeur: Any, defaut: bool = False) -> bool:
    """Coerce une valeur JSON en booléen, sans jamais lever d'exception."""
    if isinstance(valeur, bool):
        return valeur
    if isinstance(valeur, (int, float)):
        return valeur != 0
    if isinstance(valeur, str):
        return valeur.strip().lower() in {"true", "1", "yes", "oui"}
    return defaut


def en_entier_positif(valeur: Any, defaut: int = 0) -> int:
    """
    Coerce une valeur JSON en entier >= 0.

    Bloque les valeurs négatives, NaN, infinis et types non numériques.
    Protège contre les dépassements d'entier en aval (durées, quotas).
    """
    try:
        if isinstance(valeur, bool):  # bool est sous-type d'int : on l'exclut
            return defaut
        if isinstance(valeur, (int, float)):
            n = int(valeur)
            return n if n >= 0 else defaut
        if isinstance(valeur, str):
            n = int(valeur.strip())
            return n if n >= 0 else defaut
    except (ValueError, TypeError, OverflowError):
        pass
    return defaut


def en_chaine(valeur: Any, defaut: str = "") -> str:
    """Coerce une valeur JSON en chaîne bornée (anti-DoS sur la longueur)."""
    if isinstance(valeur, str):
        # Borne défensive : un label de créneau ne dépasse jamais 256 car.
        return valeur[:256]
    if isinstance(valeur, (int, float, bool)):
        return str(valeur)
    return defaut


# -----------------------------------------------------------------------------
# Extraction de listes bornées
# -----------------------------------------------------------------------------

def extraire_liste(
    data: "dict[str, Any]",
    cle: str,
    limite: int,
) -> "list[Any]":
    """
    Extrait une liste depuis le dictionnaire racine, tronquée à `limite`.

    Args:
        data: dictionnaire racine désérialisé.
        cle: nom du champ contenant la liste.
        limite: nombre maximal d'éléments retenus (garde-fou DoS).

    Returns:
        Une liste d'au plus `limite` éléments. Liste vide si le champ est
        absent ou n'est pas une liste (dégradation gracieuse).
    """
    brut = data.get(cle, [])
    if not isinstance(brut, list):
        return []
    if limite > 0:
        return brut[:limite]
    return brut


def iter_dictionnaires(elements: Iterable[Any]) -> "Iterable[dict[str, Any]]":
    """Filtre un itérable pour ne conserver que les éléments de type dict."""
    for el in elements:
        if isinstance(el, dict):
            yield el
