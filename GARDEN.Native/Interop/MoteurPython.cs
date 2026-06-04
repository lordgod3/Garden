// =============================================================================
// MoteurPython.cs — Pont Chaquopy (C# ↔ Python) implémentant IComputeEngine
// Projet : GARDEN — Système de Conscience Temporelle
// Couche : src/GARDEN.Native/Interop/MoteurPython.cs
// Cible  : Android (Chaquopy). Hors Android, garde-fous + erreurs explicites.
//
// RESPONSABILITÉS
//   - Sérialiser les entités C# en JSON pour le runtime Python.
//   - Invoquer les modules Python (scores, streaks, screentime) HORS du thread
//     UI (Task.Run + acquisition du GIL), avec timeout et annulation.
//   - Désérialiser et typer les résultats en DTO du Domaine.
//   - Convertir toute erreur Python en exception C# typée (jamais de crash).
//
// THREAD-SAFETY & GIL
//   Chaque appel acquiert le GIL Python le temps strict de l'appel, dans un
//   thread de pool (jamais le thread principal). Le verrou interne sérialise
//   les accès au runtime, Chaquopy n'étant pas conçu pour des appels
//   réentrants concurrents.
//
// NOTE D'INTÉGRATION
//   L'accès concret au runtime Chaquopy est isolé derrière IPythonRuntime,
//   afin que MoteurPython soit testable sur l'hôte (xUnit) avec un faux
//   runtime, sans émulateur. L'implémentation Chaquopy réelle
//   (ChaquopyRuntime) est fournie séparément et n'est compilée que pour la
//   cible Android.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GARDEN.Domain.Dtos;
using GARDEN.Domain.Entities;
using GARDEN.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GARDEN.Native.Interop
{
    /// <summary>
    /// Abstraction minimale du runtime Python. Permet de tester MoteurPython
    /// sans Chaquopy ni émulateur (injection d'un faux runtime).
    /// </summary>
    public interface IPythonRuntime
    {
        /// <summary>
        /// Appelle <paramref name="function"/> du module <paramref name="module"/>
        /// avec un unique argument chaîne, sous GIL, et retourne le résultat
        /// converti en chaîne. Implémentations attendues : thread-safe.
        /// </summary>
        string CallStringFunction(string module, string function, string argument);

        /// <summary>Vrai si le runtime est initialisé et prêt.</summary>
        bool IsReady { get; }
    }

    /// <summary>
    /// Implémentation de <see cref="IComputeEngine"/> déléguant au runtime
    /// Python via <see cref="IPythonRuntime"/>.
    /// </summary>
    public sealed class MoteurPython : IComputeEngine
    {
        private readonly IPythonRuntime _runtime;
        private readonly NativeOptions _options;
        private readonly ILogger<MoteurPython> _logger;

        // Sérialise les accès au runtime Python (GIL non réentrant côté Chaquopy).
        private readonly SemaphoreSlim _verrou = new SemaphoreSlim(1, 1);

        private static readonly JsonSerializerOptions JsonOpts =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public MoteurPython(
            IPythonRuntime runtime,
            IOptions<NativeOptions> options,
            ILogger<MoteurPython> logger)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<int> CalculerScoreAsync(
            IReadOnlyList<Creneau> creneaux, CancellationToken ct = default)
        {
            if (creneaux is null) throw new ArgumentNullException(nameof(creneaux));
            string payload = SerialiserCreneaux(creneaux);
            return InvoquerAsync(_options.ScoresModule, "calculer_score", payload,
                resultat => int.Parse(resultat.Trim(), CultureInfo.InvariantCulture), ct);
        }

        public Task<double> CalculerProgressionAsync(
            IReadOnlyList<Creneau> creneaux, CancellationToken ct = default)
        {
            if (creneaux is null) throw new ArgumentNullException(nameof(creneaux));
            string payload = SerialiserCreneaux(creneaux);
            return InvoquerAsync(_options.ScoresModule, "calculer_progression_journee", payload,
                resultat => double.Parse(resultat.Trim(), CultureInfo.InvariantCulture), ct);
        }

        public Task<ScoreDetaille> DecomposerScoreAsync(
            IReadOnlyList<Creneau> creneaux, CancellationToken ct = default)
        {
            if (creneaux is null) throw new ArgumentNullException(nameof(creneaux));
            string payload = SerialiserCreneaux(creneaux);
            return InvoquerAsync(_options.ScoresModule, "decomposer_score", payload,
                json => JsonSerializer.Deserialize<ScoreDetaille>(json, JsonOpts)
                        ?? throw new NativeComputeException("Décomposition de score vide."),
                ct);
        }

        public Task<int> CalculerStreakAsync(
            IReadOnlyList<DateOnly> joursAccomplis,
            DateOnly? dateReference = null, CancellationToken ct = default)
        {
            if (joursAccomplis is null) throw new ArgumentNullException(nameof(joursAccomplis));
            string payload = SerialiserJours(joursAccomplis, dateReference);
            return InvoquerAsync(_options.StreaksModule, "calculer_streak", payload,
                resultat => int.Parse(resultat.Trim(), CultureInfo.InvariantCulture), ct);
        }

        public Task<AnalyseStreaks> AnalyserStreaksAsync(
            IReadOnlyList<DateOnly> joursAccomplis,
            DateOnly? dateReference = null, CancellationToken ct = default)
        {
            if (joursAccomplis is null) throw new ArgumentNullException(nameof(joursAccomplis));
            string payload = SerialiserJours(joursAccomplis, dateReference);
            return InvoquerAsync(_options.StreaksModule, "analyser_streaks", payload,
                json => JsonSerializer.Deserialize<AnalyseStreaks>(json, JsonOpts)
                        ?? throw new NativeComputeException("Analyse de streaks vide."),
                ct);
        }

        public Task<AnalyseTempsEcran> AnalyserTempsEcranAsync(
            IReadOnlyDictionary<string, (int Secondes, string Categorie)> applications,
            int quotaGlobalSecondes,
            IReadOnlyDictionary<string, int>? quotasParCategorie = null,
            CancellationToken ct = default)
        {
            if (applications is null) throw new ArgumentNullException(nameof(applications));
            string payload = SerialiserTempsEcran(applications, quotaGlobalSecondes, quotasParCategorie);
            return InvoquerAsync(_options.ScreenTimeModule, "analyser_temps_ecran", payload,
                json => JsonSerializer.Deserialize<AnalyseTempsEcran>(json, JsonOpts)
                        ?? throw new NativeComputeException("Analyse temps d'écran vide."),
                ct);
        }

        // ------------------------------------------------------------------
        // Invocation générique : hors thread UI, sous verrou, avec timeout.
        // ------------------------------------------------------------------
        private async Task<T> InvoquerAsync<T>(
            string module, string fonction, string payload,
            Func<string, T> projeter, CancellationToken ct)
        {
            if (!_runtime.IsReady)
                throw new NativeComputeException(
                    "Le runtime Python n'est pas initialisé. Appeler InitialiserAsync au démarrage.");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutAppelPythonMs);

            await _verrou.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    cts.Token.ThrowIfCancellationRequested();
                    string brut;
                    try
                    {
                        // L'implémentation runtime acquiert/relâche le GIL ici.
                        brut = _runtime.CallStringFunction(module, fonction, payload);
                    }
                    catch (Exception ex)
                    {
                        // Toute exception Python est convertie : jamais de crash.
                        _logger.LogError(ex, "Échec de l'appel Python {Module}.{Fonction}", module, fonction);
                        throw new NativeComputeException(
                            $"Échec de l'appel Python {module}.{fonction}.", ex);
                    }

                    try
                    {
                        return projeter(brut);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Résultat Python illisible pour {Module}.{Fonction}", module, fonction);
                        throw new NativeComputeException(
                            $"Résultat Python illisible pour {module}.{fonction}.", ex);
                    }
                }, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _verrou.Release();
            }
        }

        // ------------------------------------------------------------------
        // Sérialisation des entrées vers le format JSON attendu par Python.
        // ------------------------------------------------------------------
        private static string SerialiserCreneaux(IReadOnlyList<Creneau> creneaux)
        {
            var liste = new List<object>(creneaux.Count);
            foreach (var c in creneaux)
            {
                liste.Add(new
                {
                    id = c.Id,
                    type = c.Type.ToString(),
                    start = c.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
                    end = c.End.ToString("HH:mm", CultureInfo.InvariantCulture),
                    completed = c.Completed,
                    block_notif = c.BlockNotif
                });
            }
            return JsonSerializer.Serialize(new { creneaux = liste });
        }

        private static string SerialiserJours(
            IReadOnlyList<DateOnly> jours, DateOnly? reference)
        {
            var isoJours = new List<string>(jours.Count);
            foreach (var j in jours)
                isoJours.Add(j.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            return JsonSerializer.Serialize(new
            {
                jours_accomplis = isoJours,
                date_reference = reference?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            });
        }

        private static string SerialiserTempsEcran(
            IReadOnlyDictionary<string, (int Secondes, string Categorie)> apps,
            int quotaGlobal,
            IReadOnlyDictionary<string, int>? quotasCategorie)
        {
            var liste = new List<object>(apps.Count);
            foreach (var kv in apps)
                liste.Add(new { nom = kv.Key, secondes = kv.Value.Secondes, categorie = kv.Value.Categorie });

            return JsonSerializer.Serialize(new
            {
                applications = liste,
                quota_secondes = quotaGlobal,
                quotas_par_categorie = quotasCategorie
            });
        }
    }

    /// <summary>
    /// Exception typée pour tout échec de la couche de calcul native.
    /// Convertit les erreurs Python en un type stable pour les couches appelantes.
    /// </summary>
    public sealed class NativeComputeException : Exception
    {
        public NativeComputeException(string message) : base(message) { }
        public NativeComputeException(string message, Exception inner) : base(message, inner) { }
    }
}
