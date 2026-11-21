# Carbonado — Design (full rewrite of IMGCompress)

Date: 2026-11-21 · Status: approved by owner (autonomous mode) · Target: .NET 11 (SDK 11.0.100), WPF, Windows 10/11 x64

## 1. Intent

Rewrite IMGCompress from scratch as **Carbonado**, a WPF desktop app whose two pillars are:

1. **The strongest practical image compression on Windows**: smallest files at a *guaranteed* perceptual
   quality, plus truly lossless paths that are verified bit-exact.
2. **A cinematic, premium experience**: new brand identity, epic keyframed intro, rich composition and
   transition animations — while startup and shutdown stay near-instant and the UI never stutters.

Plus: many convenience features, migration from the legacy app, and full testing (logic, function, visuals,
performance).

### Why the old app must go
- WinForms .NET 4.8 + external MetroSuite/Guna DLLs referenced from outside the repo (does not build).
- Compression = a chain of 4–7 lossy generations (pngquant 8-bit palette → JPEG → GDI+ JPEG q90 → JPEG →
  WebP → AVIF → JXL). Files are small because quality is destroyed (banding, palette posterization), not
  because the encoding is efficient.
- Temp files in `C:\Temp` (leaked on failure), RealTime process priority, one unbounded thread per file,
  busy-wait loop on the UI thread, no settings persistence, no verification, outputs can be larger.

### Calibration (img0.jpg, 3840×2400, 1.6 MB JPEG)
- JPEG→JXL lossless transcode: −31 %, original JPEG reconstructed bit-exact (SHA-256 equal).
- AVIF q70 (aom 3.14, tune iq default): 143 KB (−91 %), SSIMULACRA2 = 87.4 ("excellent").

## 2. Goals / non-goals / anti-goals

Goals
- G1 Single-generation encoding from the original decoded pixels with best-in-class encoders.
- G2 Perceptual quality targeting: search encoder parameters so that SSIMULACRA2 ≥ target with minimal bytes.
- G3 Verified lossless modes: pixel-exact (decode & compare) and bit-exact JPEG reconstruction (JPEG→JXL).
- G4 "Auto" format: race candidate formats and keep the smallest that satisfies the target.
- G5 Never-worse: if no candidate beats the original, keep (copy) the original.
- G6 Safety of user files: originals untouched by default, atomic writes, private temp, no orphans.
- G7 Startup to first frame < 350 ms warm (ReadyToRun), shutdown < 200 ms even with running jobs.
- G8 Cinematic brand experience (intro ≈3.4 s, skippable; short/off modes; reduced-motion respected).
- G9 Bilingual UI (Italiano / English), runtime switch.
- G10 Migration from legacy IMGCompress (folders, levels, tools, orphan temp cleanup).
- G11 Tested: unit, integration (real encoders), UI automation, visual snapshots, performance budgets.

Non-goals (v1): video, cloud/upload, telemetry or any runtime network access, macOS/Linux, light theme,
installer (portable folder + publish script instead).

Anti-goals (failure even if it "works"): generational re-encoding; output larger than input without
explicit opt-in; silent quality drops (every lossy output is measured); UI thread blocking; orphan
child processes or temp files; animations that delay the user (everything skippable, work starts at once).

## 3. Brand identity — "Carbonado"

Carbonado is the black diamond, the toughest natural diamond: carbon compressed under immense pressure
until it shines. Story: *infinite pressure, flawless brilliance*.

- Name: **Carbonado** · Tagline IT: "Pressione infinita. Brillantezza intatta." · EN: "Infinite pressure. Flawless brilliance."
- Mark: faceted brilliant-cut diamond (top view, octagonal crown) with a prismatic edge highlight.
- Wordmark: custom geometric letterforms with chamfered "facet" cuts (vector geometry, not a font).
- Voice: precise, confident, a touch poetic. Quality tiers borrow diamond clarity grades:
  FL (lossless), VVS (visually lossless 90), VS (excellent 85), SI (balanced 78), I (maximum reduction 65).

### Palette (dark only, tokens)
| Token | Hex | Use |
|---|---|---|
| Obsidian950 | #05060A | window background |
| Obsidian900 | #0A0B12 | app background layer |
| Obsidian850 | #0F1019 | surface |
| Obsidian800 | #151724 | raised surface / cards |
| Obsidian750 | #1C1F30 | hover |
| Obsidian700 | #252940 | strong border / pressed |
| Obsidian600 | #33385A | border |
| Diamond50 | #F4F2FF | primary text |
| Mist300 | #B4B2CC | secondary text |
| Mist500 | #77758F | tertiary text |
| Mist700 | #4A4860 | disabled |
| Ice | #7DF9FF | highlight |
| Aqua | #38E1FF | primary accent / info |
| Iris | #8B7BFF | secondary accent |
| Orchid | #C86BFF | spectrum |
| Rose | #FF5FA8 | spectrum |
| Ember | #FFB86B | spectrum spark |
| Emerald | #2BF0A1 | success |
| Topaz | #FFC857 | warning |
| Ruby | #FF4D6D | danger |

Signature gradient **Prisma**: 120° #38E1FF → #8B7BFF → #FF5FA8. Spectrum (dispersion) gradient:
Ice → Aqua → Iris → Orchid → Rose → Ember.

### Typography
- UI: Inter (embedded if license allows, else Segoe UI Variable), tabular numerals for metrics.
- Display: Inter SemiBold, tight tracking. Wordmark: custom geometry. Logs/paths: Cascadia Mono.

### Motion language — "Pressure & Refraction"
- Easings: Crystallize = cubic-bezier(0.16, 1, 0.3, 1); Implode = (0.7, 0, 0.84, 0); Settle = back-out 1.15;
  Glide = (0.4, 0, 0.2, 1).
- Durations: micro 90 ms, quick 160 ms, standard 240 ms, page 420 ms, hero 720 ms, cinematic 3.4 s.
- Stagger 35 ms. Enter = rise 12 px + scale 0.985→1 + blur 6→0 + opacity. Exit = 120 ms fade + 4 px drift.
- Sheen sweeps on hover, prismatic ripple on press, gem progress rings, odometer counters.
- Reduced motion (system setting or app setting): transforms collapse to fades, intro → short.

## 4. Architecture

```
Carbonado.sln
src/Carbonado.Core      net11.0-windows  engine, codecs, metrics, tools, settings, history, migration (no UI)
src/Carbonado.UI        net11.0-windows  WPF library: theme, controls, cinematics, effects (no Core dep)
src/Carbonado.App       net11.0-windows  WPF exe: shell, pages, view-models, services
src/Carbonado.Cli       net11.0-windows  console front-end on Core
tests/Carbonado.Core.Tests   xUnit: unit + integration (real tools)
tests/Carbonado.UI.Tests     xUnit (STA): control/intro render snapshots, timeline determinism
tests/Carbonado.App.Tests    xUnit: view-models, services, page render snapshots
tests/Carbonado.UITests      xUnit + FlaUI (UIA3): end-to-end on the real exe, startup/shutdown budgets
tools/                  encoder binaries (git-ignored) + manifest.json (tracked: versions, URLs, SHA-256)
build/                  fetch-tools.ps1, publish.ps1, compile-shaders.ps1
```

Dependencies: CommunityToolkit.Mvvm (App), xUnit + Microsoft.NET.Test.Sdk (tests), FlaUI.UIA3 (UITests).
Nothing else at runtime. System.Text.Json source generation for persistence.

### 4.1 Tools (external encoders, sandboxed child processes)
| Tool | Version | Role |
|---|---|---|
| cjxl / djxl | libjxl 0.13.0 | JXL lossy/lossless, JPEG lossless transcode + reconstruction; djxl also JPEG output (`--use_sjpeg`, `-q`) |
| ssimulacra2 | libjxl 0.13.0 | reference metric (tests / fallback) |
| avifenc / avifdec | libavif 1.4.2 (aom 3.14.1, dav1d) | AVIF |
| cwebp / dwebp / gif2webp | libwebp 1.6.0 | WebP lossy/lossless, animated GIF→WebP |
| oxipng | 10.2.1 | lossless PNG |
| ect | 0.9.5 | lossless PNG/JPEG (strongest) |
| pngquant | 2.17 (legacy) | lossy palette PNG |
| ffmpeg | 2025-11 full | universal decoder fallback (HEIC, TGA, EXR, PSD, …) |

`ToolLocator` searches `<app>/tools/<pkg>/<exe>`, then `<repo>/tools`, then legacy folders (ffmpeg/pngquant next to an
old IMGCompress.exe), then PATH. Integrity: SHA-256 checked against `tools/manifest.json` (badge "verified").
`ProcessRunner`: argument lists only (no shell), BelowNormal priority, timeouts, stdout/stderr capture, all
children assigned to a Windows **Job Object** with KILL_ON_JOB_CLOSE (no orphans, instant shutdown).

### 4.2 Imaging primitives (Core.Imaging)
- `PixelBuffer`: RGBA interleaved, non-premultiplied, 8 or 16 bit per sample, native little-endian,
  unmanaged memory (NativeMemory), row spans, IDisposable (deterministic release; no LOH churn).
- `ImageMetadata`: ICC, EXIF, XMP, orientation, DPI. `DecodedImage` = pixels (upright) + metadata + source info.
- `FormatSniffer`: magic-byte detection (PNG/APNG, JPEG, GIF, BMP, TIFF, WebP, AVIF/HEIC (ftyp brands), JXL
  (codestream + container), ICO, QOI, PNM, PSD, EXR, HDR, JP2, DDS, JXR, TGA by extension).
- Decoders: managed PNG + PNM; WIC (via WPF imaging, off-UI-thread, frozen) for JPEG/BMP/GIF/TIFF/ICO/JXR;
  tools for WebP (dwebp→PAM), AVIF/HEIC (avifdec→PNG; HEIC via ffmpeg), JXL (djxl→PAM/PNG), others via ffmpeg
  (rawvideo rgba64le pipe). EXIF orientation applied to pixels.
- Analysis: alpha usage (none/binary/full), grayscale, unique colors (exact up to 65 536), palette fit,
  meaningful 16-bit, content kind (photo / graphic / mixed) from color count, gradient smoothness and edge stats.
- Encoders (managed): PNG (all color types, 1–16 bit, palette + tRNS, 5 filters + adaptive, zlib SmallestSize),
  PAM/PPM/PGM. Built-in `PngOptimizer` (color-type/bit-depth/palette reduction, invisible-pixel cleanup option,
  multi-strategy filtering) = fallback when oxipng/ECT are missing and "intermediate" writer (fast mode).
- `Resampler`: separable Lanczos3 / Mitchell in linear light, alpha-aware (premultiplied), parallel + SIMD.

### 4.3 Metrics (Core.Metrics)
- **SSIMULACRA2** implemented in C# (port of the published algorithm: linear sRGB → XYB (positive), 6 scales
  with 2×2 box downsampling, recursive Gaussian σ=1.5, SSIM map without luma denominator, edge-diff maps
  (artifact / detail-lost), L1 + L4 norms, 108-weight polynomial). Alpha: blend on 0.1 and 0.9 grey, min score.
  `Ssimulacra2Reference` caches reference-side pyramid & blurred statistics to speed repeated scoring during search.
  Validated against `ssimulacra2.exe` (|Δ| ≤ 0.5 required, target ≤ 0.1). Optional per-pixel error heat-map.
- Pixel identity: exact compare (strict RGBA) and visible compare (alpha=0 pixels' RGB ignored); SHA-256 pixel hash.
- PSNR for diagnostics.

### 4.4 Engine (Core.Engine)
Pipeline per file:
1. **Probe** (sniff, size, frames). Animated → animated paths (GIF→WebP anim / JXL lossless) or first-frame policy.
2. **Decode** to `DecodedImage`; apply orientation; read ICC/EXIF.
3. **Analyze**; optional **resize** (fit within W×H, never upscale).
4. **Prepare**: alpha cleanup (lossy or "visible-lossless"), drop opaque alpha, metadata policy (strip all /
   keep ICC (default) / keep all minus GPS / keep all). Write a *single* intermediate (PNG fast, 8/16-bit, with
   iCCP) + PAM if needed, in a private session temp dir.
5. **Plan candidates** from profile + analysis: formats allowed × modes:
   - Lossy target-quality: JXL (cjxl -d), AVIF (avifenc -q, 10-bit, 444 at high targets), WebP (cwebp -q -m 6
     -sharp_yuv -af), JPEG (djxl --use_sjpeg -q from a lossless JXL of the intermediate; then ECT -strip -progressive).
   - Lossless: JXL modular (-d 0 -e 9/10), WebP lossless (-z 9 / -lossless -m 6 -q 100), PNG (oxipng -o max -a +
     ECT -9 --allfilters-b at Max effort; pngquant never here), AVIF lossless only if requested.
   - Lossless transcode: JPEG → JXL (`--lossless_jpeg=1`), verified by `djxl` reconstruction hash.
   - Optimize same format: JPEG → ECT (pixels identical), PNG → oxipng/ECT.
   - Lossy PNG (palette): pngquant → oxipng, scored like any lossy candidate.
6. **Quality search** (lossy): monotone parameter space per encoder; initial guess from a per-encoder model,
   then bracketing + secant/bisection on (param, score); stop when target ≤ score ≤ target + tolerance (default
   0.8) or iteration cap (Fast 3 / Balanced 5 / Max 7 / Insane 9). Keep the smallest passing candidate; candidates
   of different formats run in parallel under the thread budget.
7. **Verify** every winner: decode back and (a) score ≥ target, or (b) pixel-identical, or (c) bit-exact
   reconstruction. Failed verification ⇒ candidate rejected.
8. **Decide**: smallest verified candidate; if none smaller than original (and NeverLarger) ⇒ keep original.
9. **Commit**: atomic write (temp in destination dir → `File.Move(overwrite)`), conflict policy, optional
   timestamp preservation, optional replace-original with backup to `<dir>/.carbonado-backup/` (restorable).
10. **Report** `CompressionResult` (sizes, format, encoder, score, verification kind, candidates, timings).

Target-size mode: same search with "bytes ≤ budget" as the predicate, maximizing score.

Effort levels: Fast (cjxl e5, avif s8, cwebp m4), Balanced (e7, s6, m6), Max (e9, s3, m6 + pass 10), Insane
(e10, s1, + extra candidates: 420/444, 8/10-bit).

Batch scheduler: bounded channel of jobs; worker count from a **thread budget** (logical cores) and a
**memory budget** (≤ 25 % of physical RAM, per-job estimate from pixels); many files → single-threaded encoders
in parallel, few large files → multi-threaded encoders. Pause/resume/cancel; progress events throttled to 20 Hz.

### 4.5 Profiles (built-in)
| Id | Name IT / EN | Grade | Mode | Target | Formats |
|---|---|---|---|---|---|
| flawless | Purezza assoluta / Absolute purity | FL | Lossless verified | — | Auto (JXL, WebP, PNG) |
| vvs | Invisibile / Invisible | VVS | Quality | 90 | Auto max density |
| vs | Eccellente / Excellent | VS | Quality | 85 | Auto max density (default) |
| si | Equilibrio / Balanced | SI | Quality | 78 | Auto max density |
| i | Massima riduzione / Maximum reduction | I | Quality | 65 | Auto max density |
| web | Web universale / Universal web | VS | Quality | 85 | AVIF, WebP, JPEG (+PNG lossless) |
| keep | Stesso formato / Same format | VS | Quality / optimize | 85 | Keep |
| jpeg-archive | Archivio JPEG→JXL / JPEG→JXL archive | FL | Lossless transcode | — | JXL |
| social | Social (2048 px) | SI | Quality + resize | 80 | AVIF, WebP, JPEG |
| email | Email (≤ 300 KB, 1600 px) | — | Target size | 300 KB | JPEG, WebP |
| legacy-1..4 | IMGCompress livello 1–4 | — | Quality | 80 | JPEG / WebP / AVIF / JXL |

Custom profiles: duplicate & edit (target slider 30–99, formats, effort, metadata, resize, naming).

### 4.6 Output
Locations: subfolder "Carbonado" next to each source (default) · same folder · single chosen folder (mirror tree
optional). Pattern tokens `{name} {ext} {format} {profile} {score} {date}`. Conflicts: rename `(1)` (default) /
overwrite / skip. Never touches originals unless "replace originals" (with backup).

### 4.7 Persistence (no network, all local)
- `%APPDATA%\Carbonado\settings.json` (source-gen JSON; debounced save; atomic).
- `%LOCALAPPDATA%\Carbonado\history.jsonl` (append-only) + aggregated lifetime stats.
- `%LOCALAPPDATA%\Carbonado\temp\<session>\` (wiped on exit; stale sessions wiped at next start).
- `--data-dir <dir>` override (tests, portable mode: `portable.flag` next to exe ⇒ data in `<app>\data`).

### 4.8 Migration from IMGCompress (legacy)
Legacy had no config file; its "data" was the workflow. `LegacyMigrator` scans the app folder, its parent, the
working directory and any user-picked folder for: `IMGCompress.exe`, `inputs/`, `outputs/`, `ffmpeg.exe`,
`pngquant.exe`; and scans `<SystemDrive>:\Temp` for legacy orphans matching `^[A-Za-z0-9]{28}(-or8)?\.(png|jpg|webp|avif)$`.
Wizard (first run if found, and Settings → Migration): import `inputs` images into the queue; optionally use
`outputs` as destination; map legacy level (1 JPG, 2 WEBP, 3 AVIF, 4 JXL; transparent-PNG checkbox ⇒ keep alpha
path) to `legacy-n` profile; register legacy ffmpeg/pngquant as fallback tools; optionally create a watch-folder
rule `inputs → outputs` that reproduces the old workflow; clean orphan temps (explicit confirmation listing count
and size; files sent to Recycle Bin). Migration report shown and stored.

### 4.9 App features
- Forge (main): drop zone (files/folders, recursive), paste (Ctrl+V files/images), add buttons, virtualized
  queue with async thumbnails, per-item status ring, sizes, savings, format chip, score/verification badge,
  context menu (open, show in folder, compare, re-run with profile, remove), profile chips, format selector,
  effort, start / pause / cancel, totals with odometer counters and radial savings gauge, taskbar progress,
  completion toast + optional chime.
- Compare: before/after split slider, side-by-side, flicker (A/B), difference heat-map (SSIMULACRA2 error map),
  synchronized zoom (wheel, 1:1, fit) & pan, pixel inspector.
- Profiles: list + editor with live explanation of the quality grade.
- Archive: lifetime stats (files, bytes saved, ratio), savings timeline, format distribution donut, session list.
- Engines: tool table (version, path, SHA-256 verified badge, capabilities), rescan, open folder.
- Settings: language, intro (cinematic / short / off), reduced motion, concurrency (auto / N), priority,
  output defaults, metadata defaults, notifications, Explorer "Compress with Carbonado" (HKCU, opt-in),
  watch folders, migration, data folder, reset.
- About: version, startup timing, licenses (THIRD-PARTY-NOTICES), credits.
- Command palette (Ctrl+K), shortcuts (Ctrl+O, Ctrl+Shift+O, Ctrl+Enter, Esc, Ctrl+1…7, F11 compare full-screen).
- Single instance: second launch forwards its files to the running window (named pipe).
- CLI (`carbonado.exe` / `Carbonado.Cli`): `carbonado <paths…> [-o dir] [-p profile] [-f auto|jxl|avif|webp|jpeg|png]
  [-q 85] [--lossless] [-r] [--effort fast|balanced|max|insane] [--json] [--dry-run]`.

### 4.10 Cinematic intro storyboard (3.4 s, deterministic, skippable)
Driven by a custom `Timeline` (pure function of t ⇒ snapshot-testable frames), rendered with
`CompositionTarget.Rendering`. Tracks:
- 0.00–0.45 Void: obsidian black, vignette, film grain fades in, sparse carbon dust drifting.
- 0.35–1.25 Implosion: ~420 particles spiral inward (Implode easing), motion streaks, pressure rings converge,
  micro camera shake ramps up, chromatic aberration grows.
- 1.25–1.45 Ignition: white-hot core flash, shockwave ring + ripple distortion, 24 god-rays burst.
- 1.40–2.30 Crystallization: 3D brilliant-cut diamond (Viewport3D, faceted mesh) scales in with Settle easing,
  spins, moving lights produce facet glints; spectral dispersion fan (rainbow rays) sweeps.
- 2.10–2.90 Wordmark: CARBONADO letters cut in with stagger (blur→sharp, tracking 0.6em→0.18em, rise), sheen
  sweep; tagline types in.
- 2.90–3.40 Hand-off: diamond flies to the title-bar logo slot (shared-element morph), intro layers dissolve,
  shell reveals with staggered nav and cards.
Short mode (0.9 s): crystallize + wordmark + hand-off. Off: shell directly. Click / Esc / Space skip.
The shell is created and data loads in parallel during the intro; skipping never waits for anything.

### 4.11 Page transitions & composition
`TransitionHost`: outgoing page snapshot drifts and dissolves with chromatic-aberration shader; incoming page
rises with blur→sharp and staggered children (attached `Stagger.Index`). Pixel shaders (HLSL ps_3_0, precompiled
`.ps` via fxc, sources committed): ChromaticAberration, Ripple, Grain. Fallback when render tier < 2: shaders off.

## 5. Performance plan
- ReadyToRun + TieredPGO for Release; minimal App.xaml; frozen brushes; StaticResource by default;
  pages created lazily; settings/history/tool discovery off the UI thread during the intro.
- Startup profiler records process start → first frame → shell interactive (shown in About, asserted in UITests).
- Shutdown: cancel token, job-object kill, settings save (tiny, sync), temp wipe in background; no joins.
- Thumbnails via WIC `DecodePixelWidth` on background threads, LRU cache.

## 6. Safety & integrity (the "security" pillar of this app)
Originals never modified by default · atomic commits · verification of every output · tool binaries pinned by
SHA-256 · child processes sandboxed (no shell, job object, BelowNormal priority, timeouts) · private temp with
crash-safe cleanup · privacy: metadata stripping with GPS removal by default in "keep all minus GPS" · no network.

## 7. Testing
- Core.Tests: sniffing, PixelBuffer, PNG/PNM round-trips (all color types/bit depths), optimizer exactness,
  SSIMULACRA2 vs `ssimulacra2.exe` on a corpus (photos from `C:\Windows\Web`, synthetic graphics, alpha),
  resampler, quality search (fake encoders + real), each encoder adapter, lossless verification (pixel / bit-exact),
  never-worse, atomic writer, path resolver, scheduler (cancel, budgets), settings round-trip, history, migration
  (synthetic legacy tree), CLI parsing, process runner (timeout, kill on dispose).
- UI.Tests: timeline evaluation determinism, easing curves, intro frames at key times rendered to PNG,
  every control style rendered (normal/hover/pressed/disabled) to PNG; baseline comparison.
- App.Tests: view-models (queue, progress aggregation, profiles editing, settings), page renders to PNG.
- UITests (FlaUI): launch real exe with isolated `--data-dir`; intro appears and hands off; skip works; navigate
  all pages; add files via CLI args; run compression; outputs exist and UI shows results; startup < budget;
  shutdown < budget with running jobs and no orphan processes.
- Manual visual review of rendered snapshots and live app screenshots.

## 8. Left to the builder
Exact visual details within the brand rules, internal class layout, heuristics constants (initial guesses,
content classification thresholds), icon drawings, copy text, which shader effects ship if the GPU tier is low.
