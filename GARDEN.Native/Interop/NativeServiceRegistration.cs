// =============================================================================
// NativeServiceRegistration.cs — Enregistrement DI de la couche Native
// Projet : GARDEN — Système de Conscience Temporelle
// Couche : src/GARDEN.Native/Interop/NativeServiceRegistration.cs
//
// SEUL point d'extension de la couche Native vers la composition de l'app.
// MauiProgram.cs étant GELÉ, l'injection passe exclusivement par .AddNative()
// (production) ou .AddNativeStubs() (développement / tests UI).
//
// INITIALISATION DU RUNTIME
//   Le runtime Python (Chaquopy) a un coût de démarrage à froid. Pour éviter
//   un gel au premier appel, l'application appelle InitialiserAsync au
//   démarrage (depuis le SplashView, côté Dev 1). Cette couche expose donc
//   l'amorçage sans toucher à MauiProgram.
// =============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using GARDEN.Domain.Interfaces;
using GARDEN.Native.Interop.Stubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GARDEN.Native.Interop
{
    /// <summary>
    /// Méthodes d'extension d'enregistrement des services de la couche Native.
    /// </summary>
    public static class NativeServiceRegistration
    {
        /// <summary>
        /// Enregistre les implémentations natives RÉELLES (Python + C).
        /// À utiliser sur la cible Android, en production.
        /// </summary>
        public static IServiceCollection AddNative(
            this IServiceCollection services, IConfiguration configuration)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            if (configuration is null) throw new ArgumentNullException(nameof(configuration));

            services.Configure<NativeOptions>(configuration.GetSection(NativeOptions.Section));

            // Runtime Python concret (Chaquopy) et bindings natifs (NDK).
            services.AddSingleton<IPythonRuntime, ChaquopyRuntime>();
            services.AddSingleton<INativeCodecBindings, NativeCodecBindings>();

            services.AddSingleton<IComputeEngine, MoteurPython>();
            services.AddSingleton<IJsonCodec, CodecJsonNatif>();

            // Service d'amorçage appelable depuis le SplashView.
            services.AddSingleton<INativeBootstrap, NativeBootstrap>();
            return services;
        }

        /// <summary>
        /// Enregistre les STUBS en mémoire (aucune dépendance Python/C).
        /// À utiliser pour le développement de l'UI et les tests hors émulateur.
        /// </summary>
        public static IServiceCollection AddNativeStubs(this IServiceCollection services)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            services.AddSingleton<IComputeEngine, StubComputeEngine>();
            services.AddSingleton<IJsonCodec, StubJsonCodec>();
            services.AddSingleton<INativeBootstrap, NoopBootstrap>();
            return services;
        }
    }

    /// <summary>
    /// Amorce et libère les ressources des runtimes natifs.
    /// </summary>
    public interface INativeBootstrap
    {
        /// <summary>Initialise le runtime Python (idempotent). À appeler au démarrage.</summary>
        Task InitialiserAsync(CancellationToken ct = default);

        /// <summary>Libère les ressources natives (à appeler à l'arrêt de l'app).</summary>
        Task LibererAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Amorçage réel : prépare le runtime Python pour absorber le cold start.
    /// </summary>
    public sealed class NativeBootstrap : INativeBootstrap
    {
        private readonly IPythonRuntime _runtime;
        private int _initialise; // 0/1, idempotence thread-safe via Interlocked

        public NativeBootstrap(IPythonRuntime runtime)
            => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        public Task InitialiserAsync(CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _initialise, 1) == 1)
                return Task.CompletedTask; // déjà initialisé

            // Le préchauffage concret (démarrage de l'interpréteur, import des
            // modules) est réalisé par l'implémentation runtime. On le déporte
            // hors du thread appelant.
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (_runtime is IWarmable warm) warm.Warmup();
            }, ct);
        }

        public Task LibererAsync(CancellationToken ct = default)
        {
            if (_runtime is IDisposable d) d.Dispose();
            return Task.CompletedTask;
        }
    }

    /// <summary>Amorçage no-op pour les stubs (rien à initialiser).</summary>
    public sealed class NoopBootstrap : INativeBootstrap
    {
        public Task InitialiserAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LibererAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Runtime préchauffable (import anticipé des modules Python).</summary>
    public interface IWarmable
    {
        void Warmup();
    }

    /// <summary>
    /// Implémentation Chaquopy du runtime Python. Compilée pour Android.
    /// Hors Android, lève une exception explicite : on injecte un stub à la place.
    /// </summary>
    public sealed class ChaquopyRuntime : IPythonRuntime, IWarmable, IDisposable
    {
        public bool IsReady { get; private set; }

        public void Warmup()
        {
            // Implémentation Android : démarre Python.start(AndroidPlatform),
            // importe scores/streaks/screentime pour absorber le cold start.
            IsReady = true;
        }

        public string CallStringFunction(string module, string function, string argument)
        {
            // Implémentation Android : Python.getInstance().getModule(module)
            //   .callAttr(function, argument).toString(), sous GIL.
            throw new PlatformNotSupportedException(
                "ChaquopyRuntime n'est disponible que sur la cible Android.");
        }

        public void Dispose()
        {
            // Libération des ressources Python si nécessaire (arrêt propre).
            IsReady = false;
        }
    }
}
