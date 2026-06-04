# GARDEN.Native — Couche d'Intelligence Native (Dev 3)

Couche native du projet **GARDEN** (Système de Conscience Temporelle).
Responsable : **Dev 3**. Périmètre exclusif : `src/GARDEN.Native/`.

Cette couche fournit deux capacités, isolées derrière deux interfaces de
Domaine, et développables/testables indépendamment de Dev 1 (UI) et Dev 2 (Data).

| Capacité | Runtime | Interface | Implémentation |
|---|---|---|---|
| Analytique (scores, séries, temps d'écran) | Python via Chaquopy | `IComputeEngine` | `MoteurPython` → `python/` |
| Sérialisation + chiffrement de trame BLE | C via NDK | `IJsonCodec` | `CodecJsonNatif` → `c/` |

## Arborescence

```
GARDEN.Native/
├── python/         Modules analytiques purs (zéro dépendance Android)
│   ├── _garden_common.py   Validation/parsing défensif partagé
│   ├── scores.py           Score [0–100] + progression journée
│   ├── streaks.py          Séries d'habitudes
│   ├── screentime.py       Quotas de temps d'écran
│   └── tests/              Suite unittest (55 tests)
├── c/              Codec de trame ESP32
│   ├── json_codec.h        API publique ANSI
│   ├── json_codec.c        Implémentation bornée et réentrante
│   ├── CMakeLists.txt      Build NDK (arm64-v8a / x86_64)
│   └── tests/              Harnais autonome (31 vérifications)
├── Interop/        Ponts C# (P/Invoke + Chaquopy)
│   ├── MoteurPython.cs
│   ├── CodecJsonNatif.cs
│   ├── NativeOptions.cs
│   ├── NativeServiceRegistration.cs   AddNative() / AddNativeStubs()
│   └── Stubs/                          Implémentations en mémoire
├── Domain/         Contrats proposés (copie locale pour dev autonome)
└── docs/ARCHITECTURE.md
```

## Démarrage rapide

```bash
# Tests Python (PC, sans Android)
python3 -m unittest discover -s python/tests

# Tests C (PC)
cd c && gcc -std=c89 -Wall -Wextra -Werror -I. tests/test_json_codec.c json_codec.c -o test_codec && ./test_codec
```

## État de validation

- Python : **55/55** tests verts (`unittest`, compatibles `pytest`).
- C : **31/31** vérifications vertes ; compile sous `-std=c89 -Wall -Wextra -Werror` ;
  propre sous AddressSanitizer + UBSan (aucune fuite, aucun comportement indéfini).
- C# : revu (non compilé dans cet environnement ; nécessite `dotnet` + cible Android).

## Intégration (MauiProgram.cs est GELÉ)

```csharp
// Production (cible Android)
services.AddNative(configuration);

// Développement / tests UI (aucune dépendance native)
services.AddNativeStubs();
```

Au démarrage, appeler `INativeBootstrap.InitialiserAsync()` depuis le `SplashView`
(Dev 1) pour absorber le cold start de Chaquopy. À l'arrêt, `LibererAsync()`.

## Bloqueur externe à lever

Le format exact de la trame JSON consommée par le firmware **ESP32** doit être
validé avec l'équipe firmware avant de figer `IJsonCodec` et `json_codec.c`
(champ de version `"v":1` prévu pour la compatibilité ascendante).

## Hypothèses documentées

Voir les en-têtes de `scores.py`, `streaks.py`, `screentime.py` (H1–H3 chacun)
et `c/README.md` pour la stratégie de chiffrement.
