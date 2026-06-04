// =============================================================================
// Stubs.cs — Implémentations en mémoire des contrats (développement autonome)
// Projet : GARDEN — Système de Conscience Temporelle
// Couche : src/GARDEN.Native/Interop/Stubs/Stubs.cs
//
// Ces stubs permettent à Dev 1 et Dev 2 de coder contre IComputeEngine /
// IJsonCodec AVANT que les vraies implémentations natives ne soient prêtes,
// et à Dev 3 de tester sa logique sans émulateur. Ils n'appellent NI Python,
// NI le C natif : pure logique C# déterministe.
//
// À n'enregistrer qu'en configuration de développement / test (voir
// NativeServiceRegistration.AddNativeStubs).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GARDEN.Domain.Dtos;
using GARDEN.Domain.Entities;
using GARDEN.Domain.Interfaces;

namespace GARDEN.Native.Interop.Stubs
{
    /// <summary>
    /// Faux moteur de calcul, déterministe, sans dépendance Python.
    /// Reproduit fidèlement les invariants (bornes, ratios) pour des tests UI
    /// crédibles côté Dev 1.
    /// </summary>
    public sealed class StubComputeEngine : IComputeEngine
    {
        public Task<int> CalculerScoreAsync(IReadOnlyList<Creneau> creneaux, CancellationToken ct = default)
        {
            if (creneaux is null) throw new ArgumentNullException(nameof(creneaux));
            if (creneaux.Count == 0) return Task.FromResult(0);

            int completes = 0;
            foreach (var c in creneaux) if (c.Completed) completes++;
            int score = (int)Math.Round(100.0 * completes / creneaux.Count);
            return Task.FromResult(Math.Clamp(score, 0, 100));
        }

        public Task<double> CalculerProgressionAsync(IReadOnlyList<Creneau> creneaux, CancellationToken ct = default)
        {
            if (creneaux is null) throw new ArgumentNullException(nameof(creneaux));
            if (creneaux.Count == 0) return Task.FromResult(0.0);
            int completes = 0;
            foreach (var c in creneaux) if (c.Completed) completes++;
            return Task.FromResult(Math.Round((double)completes / creneaux.Count, 4));
        }

        public async Task<ScoreDetaille> DecomposerScoreAsync(IReadOnlyList<Creneau> creneaux, CancellationToken ct = default)
        {
            int score = await CalculerScoreAsync(creneaux, ct).ConfigureAwait(false);
            int completes = 0, focus = 0;
            foreach (var c in creneaux)
            {
                if (c.Completed) completes++;
                if (c.Type == SlotType.Focus && c.Completed) focus++;
            }
            double taux = creneaux.Count == 0 ? 0 : (double)completes / creneaux.Count;
            return new ScoreDetaille(
                ScoreFinal: score, ScoreBrut: score, BonusPlanification: 10,
                PointsCompletion: completes * 4, Penalites: 0,
                NbCreneaux: creneaux.Count, NbCompletes: completes,
                NbManques: creneaux.Count - completes, NbFocusCompletes: focus,
                NbPausesIgnorees: 0, TauxCompletion: Math.Round(taux, 4));
        }

        public Task<int> CalculerStreakAsync(IReadOnlyList<DateOnly> joursAccomplis, DateOnly? dateReference = null, CancellationToken ct = default)
        {
            if (joursAccomplis is null) throw new ArgumentNullException(nameof(joursAccomplis));
            var set = new HashSet<DateOnly>(joursAccomplis);
            var reference = dateReference ?? DateOnly.FromDateTime(DateTime.UtcNow);
            int streak = 0;
            var curseur = reference;
            if (!set.Contains(curseur) && set.Contains(curseur.AddDays(-1))) curseur = curseur.AddDays(-1);
            while (set.Contains(curseur)) { streak++; curseur = curseur.AddDays(-1); }
            return Task.FromResult(streak);
        }

        public async Task<AnalyseStreaks> AnalyserStreaksAsync(IReadOnlyList<DateOnly> joursAccomplis, DateOnly? dateReference = null, CancellationToken ct = default)
        {
            int courant = await CalculerStreakAsync(joursAccomplis, dateReference, ct).ConfigureAwait(false);
            var reference = dateReference ?? DateOnly.FromDateTime(DateTime.UtcNow);
            bool actif = new HashSet<DateOnly>(joursAccomplis).Contains(reference);
            return new AnalyseStreaks(courant, Math.Max(courant, 0), joursAccomplis.Count, actif);
        }

        public Task<AnalyseTempsEcran> AnalyserTempsEcranAsync(
            IReadOnlyDictionary<string, (int Secondes, string Categorie)> applications,
            int quotaGlobalSecondes,
            IReadOnlyDictionary<string, int>? quotasParCategorie = null,
            CancellationToken ct = default)
        {
            if (applications is null) throw new ArgumentNullException(nameof(applications));
            int total = 0, social = 0;
            foreach (var kv in applications)
            {
                total += Math.Max(0, kv.Value.Secondes);
                if (string.Equals(kv.Value.Categorie, "social", StringComparison.OrdinalIgnoreCase))
                    social += Math.Max(0, kv.Value.Secondes);
            }
            bool depasse = quotaGlobalSecondes > 0 && total > quotaGlobalSecondes;
            int depassement = quotaGlobalSecondes > 0 ? Math.Max(0, total - quotaGlobalSecondes) : 0;
            return Task.FromResult(new AnalyseTempsEcran(total, social, quotaGlobalSecondes, depasse, depassement));
        }
    }

    /// <summary>
    /// Faux codec : produit une trame JSON UTF-8 lisible (NON chiffrée), utile
    /// pour valider la chaîne d'appel de Dev 2 sans la bibliothèque native.
    /// </summary>
    public sealed class StubJsonCodec : IJsonCodec
    {
        public byte[]? EncoderPlanning(IReadOnlyList<Creneau> creneaux, long timestampUtc)
        {
            if (creneaux is null) throw new ArgumentNullException(nameof(creneaux));
            if (creneaux.Count > 72)
                throw new ArgumentException("Maximum 72 créneaux.", nameof(creneaux));

            var sb = new System.Text.StringBuilder();
            sb.Append("{\"v\":1,\"ts\":").Append(timestampUtc).Append(",\"n\":").Append(creneaux.Count).Append(",\"slots\":[");
            for (int i = 0; i < creneaux.Count; i++)
            {
                var c = creneaux[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(c.Id.Replace("\"", "\\\"")).Append("\",\"t\":\"")
                  .Append(MapType(c.Type)).Append("\"}");
            }
            sb.Append("]}");
            return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        }

        public uint CalculerCrc32(byte[] data)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                {
                    uint mask = (uint)-(int)(crc & 1u);
                    crc = (crc >> 1) ^ (0xEDB88320u & mask);
                }
            }
            return ~crc;
        }

        private static char MapType(SlotType t) => t switch
        {
            SlotType.Focus => 'F',
            SlotType.Reunion => 'R',
            SlotType.Pause => 'P',
            SlotType.Habitude => 'H',
            SlotType.BienEtre => 'W',
            _ => '?'
        };
    }

    /// <summary>
    /// Générateur de données factices : 72 créneaux répartis sur 3 jours,
    /// couvrant les 5 types. Source unique pour les tests et les démos UI.
    /// </summary>
    public static class StubData
    {
        public static IReadOnlyList<Creneau> Generer72Creneaux()
        {
            var types = new[]
            {
                SlotType.Focus, SlotType.Reunion, SlotType.Pause,
                SlotType.Habitude, SlotType.BienEtre
            };
            var liste = new List<Creneau>(72);
            for (int jour = 0; jour < 3; jour++)
            {
                for (int bloc = 0; bloc < 24; bloc++)
                {
                    var debut = new TimeOnly(6 + bloc / 2, (bloc % 2) * 30);
                    var fin = debut.Add(TimeSpan.FromMinutes(30));
                    liste.Add(new Creneau(
                        Id: $"stub-j{jour}-b{bloc}",
                        Label: $"Bloc {bloc + 1}",
                        Type: types[bloc % types.Length],
                        Start: debut,
                        End: fin,
                        DayIndex: jour,
                        Completed: bloc % 4 != 0,
                        BlockNotif: bloc % 3 == 0));
                }
            }
            return liste;
        }
    }
}
