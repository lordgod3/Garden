# Architecture — Couche Native GARDEN (Dev 3)

## Vue d'ensemble

La couche Native expose deux capacités derrière deux interfaces de Domaine.
Les couches UI (Dev 1) et Data (Dev 2) dépendent des **abstractions**, jamais
des implémentations concrètes — inversion de dépendance stricte.

```mermaid
flowchart TD
    UI["Dev 1 — UI / ViewModels"]
    DATA["Dev 2 — Data / BLE"]
    ICE["IComputeEngine (Domaine)"]
    IJC["IJsonCodec (Domaine)"]
    MP["MoteurPython"]
    CJN["CodecJsonNatif"]
    PY["Modules Python (Chaquopy)"]
    C["json_codec (NDK)"]

    UI --> ICE
    DATA --> IJC
    ICE --> MP
    IJC --> CJN
    MP --> PY
    CJN --> C
```

## Flux d'un calcul de score

```mermaid
sequenceDiagram
    participant VM as DashboardViewModel (Dev 1)
    participant ME as MoteurPython
    participant RT as IPythonRuntime
    participant PY as scores.py

    VM->>ME: CalculerScoreAsync(creneaux)
    ME->>ME: serialiser en JSON
    ME->>RT: Task.Run + GIL (hors thread UI)
    RT->>PY: calculer_score(payload)
    PY-->>RT: entier 0-100
    RT-->>ME: chaine resultat
    ME-->>VM: int (ou NativeComputeException)
```

## Flux d'émission d'une trame BLE

```mermaid
sequenceDiagram
    participant SVC as ServiceBle (Dev 2)
    participant CJN as CodecJsonNatif
    participant BIND as INativeCodecBindings
    participant C as json_codec.c
    participant CIPHER as Cipher injecte (mbedTLS)

    SVC->>CJN: EncoderPlanning(creneaux, ts)
    CJN->>BIND: EncoderFrame(creneaux, ts)
    BIND->>C: garden_encode_frame(ctx, slots, n)
    C->>C: serialiser JSON + CRC32
    C->>CIPHER: chiffrer(json)
    CIPHER-->>C: octets chiffres
    C-->>BIND: GARDEN_OK + trame
    BIND-->>CJN: octets
    CJN-->>SVC: byte[] prete pour BLE
```

## Frontières et découplage

```mermaid
flowchart LR
    subgraph NATIVE["GARDEN.Native"]
        subgraph PYZONE["python/"]
            S["scores.py"]
            ST["streaks.py"]
            SC["screentime.py"]
        end
        subgraph CZONE["c/"]
            H["json_codec.h"]
            IMPL["json_codec.c"]
        end
        subgraph INTEROP["Interop/"]
            MP2["MoteurPython"]
            CJN2["CodecJsonNatif"]
            REG["NativeServiceRegistration"]
        end
    end

    MP2 --> S
    MP2 --> ST
    MP2 --> SC
    CJN2 --> IMPL
    REG --> MP2
    REG --> CJN2
```

Les trois sous-modules (`python/`, `c/`, `Interop/`) ne dépendent pas les uns
des autres : `scores.py` ignore `json_codec.c`, et réciproquement. Cette
indépendance autorise un développement et des tests en parallèle.

## Risques principaux

1. **Format JSON ESP32 non figé** — bloque `c/` ; à valider avec le firmware.
2. **GIL Chaquopy** — résolu par `Task.Run` + `SemaphoreSlim` (hors thread UI).
3. **Sécurité mémoire C** — écritures bornées, réentrant, validé ASan/UBSan.
4. **Cold start Chaquopy** — `InitialiserAsync()` depuis le SplashView.
5. **Crash inter-langage** — toute erreur convertie en exception C# typée.
6. **Clé AES** — injectée (Keystore), jamais codée en dur.

## Stratégie de test

- **Python** : `unittest`/`pytest`, sur PC, sans Android (boucle rapide).
- **C** : harnais autonome + AddressSanitizer/UBSan (et Valgrind en CI).
- **C# Interop** : xUnit avec faux `IPythonRuntime` / `INativeCodecBindings`
  (hôte), puis tests instrumentés sur émulateur API 26+.
- **Régression de trame** : golden file comparant la sortie binaire à une
  trame de référence, une fois le format ESP32 figé.
