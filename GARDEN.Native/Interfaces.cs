// =============================================================================
// Interfaces.cs — Contrats publics de la couche Native GARDEN (GELÉS)
// Projet : GARDEN — Système de Conscience Temporelle
// Couche : src/GARDEN.Domain/Interfaces/
//
// CES INTERFACES SONT LE SEUL POINT DE CONTACT entre Dev 3 et les autres
// couches. Dev 1 (UI) consomme IComputeEngine ; Dev 2 (Data) consomme
// IJsonCodec. Toute méthode est asynchrone là où un travail hors thread UI
// est requis (appels Chaquopy), conformément à la contrainte de non-blocage
// du thread principal.
//
// Principe de découplage : les couches dépendent de ces ABSTRACTIONS, jamais
// des implémentations concrètes (MoteurPython, CodecJsonNatif). C'est la
// pierre angulaire de l'inversion de dépendance pour ce projet.
// =============================================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GARDEN.Domain.Dtos;
using GARDEN.Domain.Entities;

namespace GARDEN.Domain.Interfaces
{
    /// <summary>
    /// Moteur d'intelligence analytique (implémenté par le runtime Python via
    /// Chaquopy). Toutes les méthodes s'exécutent hors du thread principal.
    /// </summary>
    public interface IComputeEngine
    {
        /// <summary>
        /// Calcule le score de conscience temporelle journalier [0–100].
        /// </summary>
        /// <param name="creneaux">Créneaux de la journée (au plus 24).</param>
        /// <param name="ct">Jeton d'annulation.</param>
        /// <returns>Score entier [0–100].</returns>
        Task<int> CalculerScoreAsync(
            IReadOnlyList<Creneau> creneaux,
            CancellationToken ct = default);

        /// <summary>
        /// Calcule la progression brute de la journée [0.0–1.0]
        /// (barre « JOURNÉE 62% »).
        /// </summary>
        Task<double> CalculerProgressionAsync(
            IReadOnlyList<Creneau> creneaux,
            CancellationToken ct = default);

        /// <summary>
        /// Retourne la décomposition détaillée du score (écran Statistiques).
        /// </summary>
        Task<ScoreDetaille> DecomposerScoreAsync(
            IReadOnlyList<Creneau> creneaux,
            CancellationToken ct = default);

        /// <summary>
        /// Calcule la série courante d'une habitude.
        /// </summary>
        /// <param name="joursAccomplis">Dates d'accomplissement.</param>
        /// <param name="dateReference">
        /// Jour logique de référence (injectable pour gérer fuseaux et gardes
        /// de nuit). Si null, le moteur utilise la date du jour côté Python.
        /// </param>
        Task<int> CalculerStreakAsync(
            IReadOnlyList<System.DateOnly> joursAccomplis,
            System.DateOnly? dateReference = null,
            CancellationToken ct = default);

        /// <summary>
        /// Analyse complète des séries (courante, record, état du jour).
        /// </summary>
        Task<AnalyseStreaks> AnalyserStreaksAsync(
            IReadOnlyList<System.DateOnly> joursAccomplis,
            System.DateOnly? dateReference = null,
            CancellationToken ct = default);

        /// <summary>
        /// Analyse le temps d'écran et détecte les dépassements de quota.
        /// </summary>
        Task<AnalyseTempsEcran> AnalyserTempsEcranAsync(
            IReadOnlyDictionary<string, (int Secondes, string Categorie)> applications,
            int quotaGlobalSecondes,
            IReadOnlyDictionary<string, int>? quotasParCategorie = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Codec de sérialisation/chiffrement du planning (implémenté par la
    /// bibliothèque C native via le NDK). Produit la trame émise par BLE.
    /// </summary>
    public interface IJsonCodec
    {
        /// <summary>
        /// Sérialise et chiffre le planning (≤ 72 créneaux) pour envoi BLE.
        /// </summary>
        /// <param name="creneaux">Créneaux à transmettre.</param>
        /// <param name="timestampUtc">Horodatage epoch (s) de la trame.</param>
        /// <returns>Trame binaire prête à l'émission, ou null en cas d'échec.</returns>
        byte[]? EncoderPlanning(IReadOnlyList<Creneau> creneaux, long timestampUtc);

        /// <summary>
        /// Calcule le CRC-32 d'un tampon (vérification d'intégrité côté tests).
        /// </summary>
        uint CalculerCrc32(byte[] data);
    }
}
