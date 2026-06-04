# =============================================================================
# scores.py — Moteur de Score de Conscience Temporelle
# Projet  : GARDEN — Système de Conscience Temporelle
# Couche  : src/GARDEN.Native/python/scores.py
# Runtime : Python 3.8+  (Chaquopy, thread background C#)
# Auteur  : Dev 3 — Couche Native
# Version : 1.0.0
#
# API publique (appelée par MoteurPython.cs via Chaquopy) :
#   calculer_score(payload_json: str) -> int               [0–100]
#   calculer_progression_journee(payload_json: str) -> float  [0.0–1.0]
#   decomposer_score(payload_json: str) -> str             [JSON détaillé]
#
# Format du payload JSON attendu :
#   {
#     "creneaux": [
#       {"id": str, "type": "Focus|Reunion|Pause|Habitude|BienEtre",
#        "start": "HH:MM", "end": "HH:MM",
#        "completed": bool, "block_notif": bool},
#       ...   (tronqué à 24 — contrainte 72 blocs / 3 jours)
#     ],
#     "date": "YYYY-MM-DD"   (optionnel, contextuel)
#   }
#
# HYPOTHÈSES DOCUMENTÉES (à valider avec l'équipe produit) :
#   H1. "Focus" est le créneau le plus précieux (charte : Garden Teal,
#       "moment le plus précieux") => pondération maximale.
#   H2. Une "Pause" planifiée mais ignorée traduit du surmenage => pénalité
#       (la technique Pomodoro est explicitement citée au cahier des charges).
#   H3. Le score affiché et la progression de journée sont DEUX mesures
#       distinctes : le score est pondéré ; la progression est un simple
#       ratio complétés/total (alimente la barre "JOURNÉE 62%").
# =============================================================================

from __future__ import annotations

import json
from dataclasses import dataclass

from _garden_common import (
    GardenPayloadError,
    MAX_CRENEAUX_PAR_JOUR,
    TYPES_CRENEAU_VALIDES,
    charger_objet_json,
    en_bool,
    en_chaine,
    extraire_liste,
    iter_dictionnaires,
)

# -----------------------------------------------------------------------------
# Constantes métier (toutes ajustables sans changer la structure du calcul)
# -----------------------------------------------------------------------------

#: Points gagnés par bloc de 30 min complété, par type de créneau.
_POIDS_COMPLETION = {
    "Focus":    4.0,   # H1 — Deep Work, le plus précieux
    "Reunion":  2.5,
    "Habitude": 2.0,
    "BienEtre": 2.0,
    "Pause":    1.5,   # respecter une pause planifiée = bonne hygiène
}

#: Pénalité par bloc de 30 min manqué (completed=False), par type.
_PENALITE_MANQUE = {
    "Focus":    3.0,
    "Pause":    2.0,   # H2 — pause ignorée = signal de surmenage
    "Reunion":  1.5,
    "Habitude": 1.0,
    "BienEtre": 0.5,
}

#: Bonus accordé dès qu'au moins un créneau existe (planifier = agir).
_BONUS_PLANIFICATION = 10.0

#: Durée d'un bloc standard, en minutes (cahier des charges).
_DUREE_BLOC_MINUTES = 30

#: Bornes dures de l'échelle de score exposée par l'interface IComputeEngine.
_SCORE_MIN = 0
_SCORE_MAX = 100


# -----------------------------------------------------------------------------
# Structures internes
# -----------------------------------------------------------------------------

@dataclass(frozen=True)
class _Creneau:
    """Créneau validé et normalisé (immuable)."""
    type: str
    completed: bool
    duree_blocs: int


@dataclass(frozen=True)
class _Decomposition:
    """Détail complet d'un calcul de score (pour l'écran Statistiques)."""
    score_final: int
    score_brut: float
    bonus_planification: float
    points_completion: float
    penalites: float
    nb_creneaux: int
    nb_completes: int
    nb_manques: int
    nb_focus_completes: int
    nb_pauses_ignorees: int
    taux_completion: float


# -----------------------------------------------------------------------------
# Parsing des heures et durées (défensif)
# -----------------------------------------------------------------------------

def _minutes_depuis_hhmm(hhmm: str) -> "int | None":
    """
    Convertit 'HH:MM' en minutes depuis minuit. Retourne None si invalide.

    Ne lève jamais : un format erroné dégrade vers une durée par défaut en
    amont plutôt que de propager une exception.
    """
    parts = hhmm.strip().split(":")
    if len(parts) != 2:
        return None
    try:
        h, m = int(parts[0]), int(parts[1])
    except ValueError:
        return None
    if not (0 <= h <= 23 and 0 <= m <= 59):
        return None
    return h * 60 + m


def _calculer_duree_blocs(start: str, end: str) -> int:
    """
    Nombre de blocs de 30 min entre start et end. Minimum garanti : 1.

    Gère le passage de minuit (end < start) en considérant la fin le
    lendemain — utile pour les gardes de nuit en milieu hospitalier.
    """
    debut = _minutes_depuis_hhmm(start)
    fin = _minutes_depuis_hhmm(end)
    if debut is None or fin is None:
        return 1
    delta = fin - debut
    if delta <= 0:
        delta += 24 * 60  # franchissement de minuit (contexte garde de nuit)
    return max(1, round(delta / _DUREE_BLOC_MINUTES))


# -----------------------------------------------------------------------------
# Désérialisation -> créneaux validés
# -----------------------------------------------------------------------------

def _parser_creneaux(payload_json: str) -> "list[_Creneau]":
    """
    Désérialise et valide la liste de créneaux du payload.

    Raises:
        GardenPayloadError: si le JSON racine est invalide.
    """
    data = charger_objet_json(payload_json)
    bruts = extraire_liste(data, "creneaux", MAX_CRENEAUX_PAR_JOUR)

    creneaux: "list[_Creneau]" = []
    for raw in iter_dictionnaires(bruts):
        type_creneau = en_chaine(raw.get("type")).strip()
        if type_creneau not in TYPES_CRENEAU_VALIDES:
            continue  # type inconnu : ignoré (tolérance aux évolutions)
        creneaux.append(
            _Creneau(
                type=type_creneau,
                completed=en_bool(raw.get("completed"), defaut=True),
                duree_blocs=_calculer_duree_blocs(
                    en_chaine(raw.get("start"), "00:00"),
                    en_chaine(raw.get("end"), "00:30"),
                ),
            )
        )
    return creneaux


# -----------------------------------------------------------------------------
# Cœur du calcul
# -----------------------------------------------------------------------------

def _calculer(creneaux: "list[_Creneau]") -> _Decomposition:
    """Calcule la décomposition complète. Pur, sans effet de bord."""
    if not creneaux:
        return _Decomposition(0, 0.0, 0.0, 0.0, 0.0, 0, 0, 0, 0, 0, 0.0)

    bonus = _BONUS_PLANIFICATION
    score_max = bonus + sum(
        _POIDS_COMPLETION[c.type] * c.duree_blocs for c in creneaux
    )

    points = 0.0
    penalites = 0.0
    nb_completes = nb_manques = nb_focus = nb_pauses_ignorees = 0

    for c in creneaux:
        if c.completed:
            points += _POIDS_COMPLETION[c.type] * c.duree_blocs
            nb_completes += 1
            if c.type == "Focus":
                nb_focus += 1
        else:
            penalites += _PENALITE_MANQUE[c.type] * c.duree_blocs
            nb_manques += 1
            if c.type == "Pause":
                nb_pauses_ignorees += 1

    score_brut = bonus + points - penalites
    normalise = (score_brut / score_max) * 100.0 if score_max > 0 else 0.0
    score_final = int(round(min(float(_SCORE_MAX), max(float(_SCORE_MIN), normalise))))
    taux = nb_completes / len(creneaux)

    return _Decomposition(
        score_final=score_final,
        score_brut=round(score_brut, 4),
        bonus_planification=bonus,
        points_completion=round(points, 4),
        penalites=round(penalites, 4),
        nb_creneaux=len(creneaux),
        nb_completes=nb_completes,
        nb_manques=nb_manques,
        nb_focus_completes=nb_focus,
        nb_pauses_ignorees=nb_pauses_ignorees,
        taux_completion=round(taux, 4),
    )


# -----------------------------------------------------------------------------
# API publique (frontière Chaquopy)
# -----------------------------------------------------------------------------

def calculer_score(payload_json: str) -> int:
    """
    Calcule le score de conscience temporelle journalier [0–100].

    Args:
        payload_json: planning journalier sérialisé (voir en-tête de fichier).

    Returns:
        Entier [0–100]. 0 = aucun créneau ; 100 = journée parfaitement tenue.

    Raises:
        GardenPayloadError: si le JSON est invalide (sous-classe de ValueError,
            interceptée par MoteurPython.cs et convertie en exception C# typée).
    """
    return _calculer(_parser_creneaux(payload_json)).score_final


def calculer_progression_journee(payload_json: str) -> float:
    """
    Ratio de complétion brut de la journée [0.0–1.0] (barre "JOURNÉE 62%").

    Distinct du score : ici, ratio simple complétés/total, sans pondération.
    """
    return _calculer(_parser_creneaux(payload_json)).taux_completion


def decomposer_score(payload_json: str) -> str:
    """
    Décomposition détaillée du score, au format JSON (écran Statistiques).

    Returns:
        JSON stringifié contenant tous les champs de _Decomposition.
        Le format string est le contrat le plus robuste à travers Chaquopy
        (évite le marshaling d'objets complexes).
    """
    d = _calculer(_parser_creneaux(payload_json))
    return json.dumps(
        {
            "score_final": d.score_final,
            "score_brut": d.score_brut,
            "bonus_planification": d.bonus_planification,
            "points_completion": d.points_completion,
            "penalites": d.penalites,
            "nb_creneaux": d.nb_creneaux,
            "nb_completes": d.nb_completes,
            "nb_manques": d.nb_manques,
            "nb_focus_completes": d.nb_focus_completes,
            "nb_pauses_ignorees": d.nb_pauses_ignorees,
            "taux_completion": d.taux_completion,
        },
        ensure_ascii=False,
        separators=(",", ":"),
    )


__all__ = [
    "calculer_score",
    "calculer_progression_journee",
    "decomposer_score",
    "GardenPayloadError",
]
