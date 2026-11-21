# Carbonado

**Infinite pressure. Flawless brilliance.** — the successor of IMGCompress, rebuilt from scratch in .NET 11 WPF.

Carbonado is the black diamond: carbon compressed under immense pressure until it shines. Carbonado (the app)
squeezes your images to their smallest possible size **without losing their light** — and proves it.

## Why it compresses better

IMGCompress 6 re-encoded every image through a chain of 4–7 lossy steps (8-bit palette → JPEG → JPEG → WebP → AVIF →
JXL). Files got small because quality was destroyed. Carbonado does the opposite:

- **One generation only.** Every candidate is encoded once, directly from the original pixels.
- **Perceptual quality targeting.** An in-process C# implementation of **SSIMULACRA2** (validated against the
  reference `ssimulacra2` from libjxl) measures every encode; a secant/bisection search finds the *smallest* file that
  still meets the chosen clarity grade.
- **Format race.** JPEG XL, AVIF, WebP, JPEG and PNG candidates compete; the smallest *verified* result wins.
- **Verified lossless paths.** Pixel-exact lossless (decoded back and compared), and **reversible JPEG → JPEG XL**
  (about 20–30 % smaller; the original JPEG is rebuilt bit for bit and checked by SHA-256).
- **Best-in-class encoders**: libjxl 0.13, libavif 1.4 (aom 3.14 with `tune=iq`), libwebp 1.6, oxipng 10, ECT 0.9,
  pngquant, with FFmpeg as universal fallback decoder — all pinned by SHA-256.
- **Never worse.** If nothing beats the original, the original is kept.

| Clarity grade | Profile | SSIMULACRA2 target | Meaning |
|---|---|---|---|
| FL | Absolute purity | lossless | identical pixels, verified |
| VVS | Invisible | 90 | indistinguishable even flipping images at 1:1 |
| VS | Excellent (default) | 85 | no visible difference side by side at 1:1 |
| SI | Balanced | 78 | artifacts only when hunting for them |
| I | Maximum reduction | 65 | good quality, tiny files |

Plus: Universal web (AVIF/WebP/JPEG), Same format, JPEG → JXL archive, Social (2048 px), Email (≤ 300 KB), custom
profiles, and the four IMGCompress legacy levels.

## Features

- Cinematic keyframed intro (skippable, short/off modes, reduced-motion aware) and a full "Pressure & Refraction"
  motion language — while the window appears in about 0.6 s (first frame, ReadyToRun build) and the UI never blocks.
  Start-up overlaps everything WPF allows: the render engine boots on a helper thread from `Main`, settings load on a
  worker, fonts and the intro's 3D assets warm up in the background. Closing takes well under 100 ms; encoder processes
  live in a kill-on-close Job Object, so none can outlive the app.
- Fully bilingual UI (English and Italian, following Windows by default, switchable live from Settings or Ctrl+K).
- Forge: drag & drop files/folders, paste, live per-image progress, savings odometer and gauge, pause/resume/cancel.
- Compare: split slider, side by side, flicker, SSIMULACRA2 error heat-map, synchronized zoom and pan.
- Archive: lifetime statistics, 14-day savings chart, format distribution, recent jobs.
- Engines: every encoder with version, location and SHA-256 integrity badge.
- Settings: Italian/English, intro, motion, output location and naming, conflicts, timestamps, replace-with-backup,
  parallelism, notifications, Explorer "Compress with Carbonado", watched folders, data folder.
- Migration from IMGCompress: finds old `inputs`/`outputs` folders, maps the legacy levels, reuses ffmpeg/pngquant,
  can recreate the old folder workflow as a watched folder, and moves leaked temp files to the Recycle Bin.
- Command palette (Ctrl+K), single instance (Explorer multi-select lands in one window), portable mode, CLI.

## Command line

```
Carbonado.exe [files/folders…] [--profile id] [--start] [--intro cinematic|short|off] [--lang it|en] [--data-dir dir]
carbonado-cli.exe <files/folders…> [-o dir] [-p profile] [-f auto|jxl|avif|webp|jpeg|png] [-q score] [--lossless]
                  [-r] [--effort fast|balanced|max|insane] [--json] [--dry-run]
```

## Build, test, publish

Requirements: Windows 10/11 x64, .NET 11 SDK.

```powershell
powershell -ExecutionPolicy Bypass -File build/fetch-tools.ps1      # download + verify encoders (SHA-256)
dotnet build Carbonado.sln -c Release
dotnet test --solution Carbonado.sln -c Release                      # unit, integration, render, end-to-end
powershell -ExecutionPolicy Bypass -File build/publish.ps1          # artifacts\Carbonado (ReadyToRun)
powershell -ExecutionPolicy Bypass -File build/make-icon.ps1        # regenerate the app icon from the brand mark
```

Startup milestones of the last run are written to `logs\startup-last.txt` in the data folder and shown
in About.

Project layout: `src/Carbonado.Core` (engine, codecs, SSIMULACRA2, tools, persistence, migration), `src/Carbonado.UI`
(brand theme, controls, cinematics, shaders), `src/Carbonado.App` (shell, pages), `src/Carbonado.Cli`, `tests/*`.
Design and plan: `docs/superpowers/`.

## Italiano

Carbonado è l'erede di IMGCompress, riscritto da zero in .NET 11 WPF con una nuova identità. Comprime ogni immagine
**una sola volta** dai pixel originali, misura la qualità percepita con SSIMULACRA2 e sceglie il file più leggero che
rispetta il grado di purezza scelto (FL, VVS, VS, SI, I). I percorsi senza perdita sono verificati: pixel identici o
JPEG → JPEG XL ricostruibile bit per bit. Se nulla batte l'originale, l'originale resta com'è.
La migrazione da IMGCompress ritrova le vecchie cartelle `inputs`/`outputs`, i livelli usati, ffmpeg/pngquant e i file
temporanei abbandonati in `C:\Temp`.

## License

GPL-3.0 (see `LICENSE`). Third-party components: see `THIRD-PARTY-NOTICES.md`.
