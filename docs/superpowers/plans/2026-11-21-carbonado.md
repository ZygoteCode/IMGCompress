# Carbonado Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the WinForms IMGCompress with Carbonado: a .NET 11 WPF app with a verified, perceptually-targeted
compression engine and a cinematic brand experience.

**Architecture:** `Carbonado.Core` (engine, no UI) + `Carbonado.UI` (WPF theme/controls/cinematics, no Core dep) +
`Carbonado.App` (shell, pages, view-models) + `Carbonado.Cli`. External encoders run as sandboxed child processes;
pixels live in unmanaged `PixelBuffer`s; quality is measured with an in-process SSIMULACRA2 port.

**Tech Stack:** .NET 11 (SDK 11.0.100), C# latest, WPF, CommunityToolkit.Mvvm, System.Text.Json source-gen, xUnit,
FlaUI.UIA3; tools: libjxl 0.13.0, libavif 1.4.2, libwebp 1.6.0, oxipng 10.2.1, ECT 0.9.5, pngquant 2.17, ffmpeg.

**Spec:** `docs/superpowers/specs/2026-11-21-carbonado-design.md` (+ `ssimulacra2-reference.md` for Task 5).

## Global Constraints
- All projects: `net11.0-windows`, x64, `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`,
  `AllowUnsafeBlocks=true` where needed; `src/*` builds with `TreatWarningsAsErrors=true`.
- Namespaces `Carbonado.Core.*`, `Carbonado.UI.*`, `Carbonado.App.*`, `Carbonado.Cli`.
- No runtime network access, no telemetry. Opening the GitHub URL on explicit user click is the only exception.
- Child processes only through `ProcessRunner` (argument lists, Job Object, BelowNormal, timeout).
- Temp files only under the session temp dir from `DataPaths.TempRoot`; originals are never written unless
  `OutputPlan.ReplaceOriginals` is true.
- UI text only through localization keys (`L.*` in `Strings.it.xaml` / `Strings.en.xaml`); colors only through
  `Tokens.xaml` keys (`C.*` colors, `B.*` brushes); no literal hex outside `Tokens.xaml`.
- UI thread never blocks: engine work on the thread pool, progress marshalled at ≤ 20 Hz.
- Every animation honours `MotionSettings.ReducedMotion` and is skippable.
- Integration tests needing a tool use `TestTools.Require(ToolId)` which skips when the tool is missing.
- Commits are deferred to the owner (repository rule: commit only when asked). Each task ends with a green
  build + test checkpoint instead.

## Review Focus
1. Misnamed, corrupt, zero-byte or truncated inputs (PNG named `.jpg`, half-downloaded JPEG): content sniffing
   decides; the job fails alone with a readable message; the batch continues. → Task 10 `Engine_CorruptInputs_FailPerFile`.
2. Unicode/space/very long (> 260 chars) paths and read-only destinations: outputs written or a per-file error,
   never a crash; tools receive paths safely. → Task 6 `Runner_PassesUnicodePathsVerbatim`, Task 10 `Engine_LongUnicodePath_Roundtrip`.
3. Non-sRGB ICC, CMYK JPEG, 16-bit PNG, gray+alpha, palette+tRNS, EXIF orientation 6/8: colors and orientation
   preserved. → Task 7 `Loader_AppliesExifOrientation`, `Loader_CmykJpeg_ConvertsToRgb`; Task 10 `Engine_KeepsIccProfile`.
4. Animated GIF/APNG/WebP: animation preserved (GIF→animated WebP/JXL) or original kept, never silently
   flattened. → Task 10 `Engine_AnimatedGif_StaysAnimated`.
5. Cancel or app close mid-encode: no partial outputs, no orphan child processes, temp cleaned.
   → Task 6 `Runner_DisposeKillsChildren`, Task 11 `Scheduler_CancelMidJob_LeavesNoOutputAndNoProcess`.

## Execution map (parallel workstreams)
| Phase | Workstream | Tasks | Owner |
|---|---|---|---|
| 0 | Scaffold + shared contracts | 0 | lead |
| 1 | core-io | 1, 2, 3, 6, 7, 8 | subagent (own workspace copy) |
| 1 | core-pixels | 4, 5 | subagent (own workspace copy) |
| 1 | ui-brand | 12, 13, 14 | subagent (own workspace copy) |
| 1 | ui-cinematics | 15, 16 | subagent (own workspace copy) |
| 2 | core-engine | 9, 10, 11, 17, 20a | subagent after phase-1 merge |
| 2 | app | 18, 19, 20b | lead (in parallel with core-engine, against contracts) |
| 3 | finish | 21, 22, 23 | lead |

Workspace copies: `<scratchpad>/ws/<name>` = robocopy of the repo without bin/obj/tools + junction `tools` → repo
`tools`. Each workstream only creates/edits files inside the folders it owns (listed per task); shared files
(csproj, Tokens.xaml, contracts) are frozen after Task 0 — needed changes are reported to the lead.

---

### Task 0: Scaffold, shared contracts, test support (lead)

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Carbonado.sln`, `.gitignore` (add bin/obj/.vs/tools binaries/tests/artifacts),
  `tools/manifest.json`, `build/fetch-tools.ps1`, all project files under `src/` and `tests/`.
- Create contracts: `src/Carbonado.Core/Imaging/{SampleDepth,Rgba32,Rgba64,PixelBuffer,ImageFormat,OutputFormat,FormatInfo,ImageMetadata,DecodedImage}.cs`,
  `src/Carbonado.Core/Analysis/{AlphaUsage,ContentKind,ImageAnalysis}.cs`,
  `src/Carbonado.Core/Encoding/{EncodeKind,EffortLevel,ChromaMode,MetadataPolicy}.cs`,
  `src/Carbonado.Core/Tools/{ToolId,ToolIntegrity,ToolInfo}.cs`,
  `src/Carbonado.Core/Engine/Contracts/*.cs`, `src/Carbonado.Core/Persistence/Contracts/*.cs`,
  `src/Carbonado.Core/Migration/Contracts/*.cs`,
  `src/Carbonado.UI/Theme/Tokens.xaml`, `src/Carbonado.UI/Motion/MotionSettings.cs`.
- Create test support: `tests/Carbonado.Core.Tests/TestSupport/{TestTools,TestImages,TempDir}.cs`,
  `tests/Carbonado.UI.Tests/TestSupport/{Sta,Snapshot}.cs`.
- Delete legacy: `MainForm.*`, `ImageCompressor.cs`, `ProtoRandom.cs`, `Program.cs`, `IMGCompress.csproj`, `IMGCompress.sln`, `App.config`.

**Interfaces (Produces):**
- `PixelBuffer : IDisposable` — `static PixelBuffer Create(int width, int height, SampleDepth depth, bool clear = true)`;
  `int Width, Height`; `SampleDepth Depth`; `int BytesPerPixel` (4 or 8); `long Stride`; `long ByteLength`; `nint Pointer`;
  `Span<byte> RowBytes(int y)`; `Span<Rgba32> Row32(int y)`; `Span<Rgba64> Row64(int y)`; `PixelBuffer Clone()`;
  `PixelBuffer ToDepth(SampleDepth depth)` (8→16: v·257; 16→8: (v·255 + 32895) >> 16); RGBA, non-premultiplied, LE.
- `enum SampleDepth : byte { Eight = 8, Sixteen = 16 }`; `struct Rgba32 { byte R, G, B, A }`; `struct Rgba64 { ushort R, G, B, A }`.
- `enum ImageFormat { Unknown, Png, Apng, Jpeg, Gif, Bmp, Tiff, WebP, Avif, Heic, Jxl, Ico, Qoi, Pnm, Psd, Exr, Hdr, Jp2, Dds, Jxr, Tga }`.
- `enum OutputFormat { Jxl, Avif, WebP, Jpeg, Png, Gif }`; `FormatInfo.Extension(OutputFormat) → ".jxl"…`,
  `FormatInfo.FromImageFormat(ImageFormat) → OutputFormat?`, `FormatInfo.InputExtensions` (string set).
- `ImageMetadata { byte[]? Icc; byte[]? Exif; byte[]? Xmp; ushort Orientation = 1; double DpiX = 96, DpiY = 96; ImageMetadata Clone(); }`.
- `DecodedImage : IDisposable { PixelBuffer Pixels; ImageMetadata Metadata; ImageFormat SourceFormat; int FrameCount; int SignificantBits; }`.
- `enum AlphaUsage { Opaque, Binary, Translucent }`, `enum ContentKind { Photo, Graphic, Mixed }`,
  `record ImageAnalysis(int Width, int Height, AlphaUsage Alpha, bool IsGrayscale, int UniqueColors, bool FitsPalette, bool Has16BitPrecision, ContentKind Kind, double EdgeDensity, double FlatRatio)` (`UniqueColors = -1` when > 65 536).
- `enum EncodeKind { Lossy, Lossless, JpegTranscode, Optimize }`, `enum EffortLevel { Fast, Balanced, Max, Insane }`,
  `enum ChromaMode { Auto, Yuv444, Yuv420 }`, `enum MetadataPolicy { StripAll, KeepColorProfile, KeepAllButGps, KeepAll }`.
- `enum ToolId { Cjxl, Djxl, Jxlinfo, Ssimulacra2, Avifenc, Avifdec, Cwebp, Dwebp, Gif2webp, Oxipng, Ect, Pngquant, Ffmpeg }`,
  `enum ToolIntegrity { Verified, Unknown, Mismatch }`, `record ToolInfo(ToolId Id, string Path, string Package, string? Version, ToolIntegrity Integrity, string Sha256)`.
- Engine contracts (namespace `Carbonado.Core.Engine`): `enum QualityMode { TargetScore, Lossless, TargetSize }`,
  `enum FormatStrategy { Auto, KeepSource, Fixed }`, `record ResizeSpec(int MaxWidth, int MaxHeight)`,
  `record CompressionProfile` (init props: `Id, Name, NameIt, Grade, Description, DescriptionIt, Mode, TargetScore = 85, TargetBytes, Strategy, Formats, Effort = Balanced, Metadata = KeepColorProfile, Resize, NeverLarger = true, AllowJpegTranscode = true, CleanInvisiblePixels = true, BuiltIn`),
  `enum OutputLocation { SubfolderNextToSource, SameFolder, CustomFolder }`, `enum ConflictPolicy { Rename, Overwrite, Skip }`,
  `record OutputPlan(OutputLocation Location = SubfolderNextToSource, string? CustomFolder = null, string SubfolderName = "Carbonado", string NamePattern = "{name}", ConflictPolicy Conflict = Rename, bool MirrorTree = false, bool PreserveTimestamps = true, bool ReplaceOriginals = false)`,
  `enum JobStage { Queued, Probing, Decoding, Analyzing, Preparing, Encoding, Verifying, Writing, Done }`,
  `record JobProgress(Guid JobId, JobStage Stage, double Fraction, string? Detail = null, string? CandidateId = null, double? CandidateScore = null, long? CandidateBytes = null)`,
  `enum JobOutcome { Compressed, KeptOriginal, Skipped, Failed, Cancelled }`,
  `enum VerificationKind { None, PerceptualScore, PixelExact, BitExactReconstruction }`,
  `record CandidateReport(string EncoderId, OutputFormat Format, EncodeKind Kind, double? Parameter, long Bytes, double? Score, bool Passed, TimeSpan Elapsed)`,
  `record CompressionResult(...)` with `Guid JobId, string InputPath, string? OutputPath, JobOutcome Outcome, long OriginalBytes, long OutputBytes, OutputFormat? Format, string? EncoderId, double? Score, VerificationKind Verification, TimeSpan Elapsed, IReadOnlyList<CandidateReport> Candidates, string? Error, int Width, int Height` and computed `double SavedFraction`,
  `record CompressionJob(Guid Id, string InputPath, string? InputRoot, CompressionProfile Profile, OutputPlan Output)`,
  `record BatchSummary(int Total, int Compressed, int Kept, int Failed, int Cancelled, long OriginalBytes, long OutputBytes, TimeSpan Elapsed)`,
  `enum BatchState { Idle, Running, Paused, Cancelling }`,
  `interface IBatchProcessor : IAsyncDisposable { BatchState State; void Enqueue(CompressionJob job); void Start(); void Pause(); void Resume(); void Cancel(Guid jobId); void CancelAll(); event EventHandler<JobProgress>? Progress; event EventHandler<CompressionResult>? JobCompleted; event EventHandler<BatchSummary>? BatchCompleted; }`.
- Persistence contracts: `AppSettings` (mutable class: `Language` ("auto"|"it"|"en"), `IntroMode` {Cinematic, Short, Off},
  `ReducedMotion` bool?, `ActiveProfileId = "vs"`, `List<CompressionProfile> CustomProfiles`, `OutputPlan Output`, `int? MaxConcurrency`,
  `bool PlayChime = true`, `bool ShowToasts = true`, `bool ExplorerIntegration`, `List<WatchRule> WatchRules`,
  `bool LegacyScanDone`, `bool FirstRunDone`, `WindowPlacement? Window`), `record WatchRule(string Id, string Folder, bool Recursive, string ProfileId, OutputPlan Output, bool Enabled)`,
  `record HistoryEntry(DateTimeOffset At, string Input, string? Output, JobOutcome Outcome, long OriginalBytes, long OutputBytes, OutputFormat? Format, double? Score, string ProfileId, double Seconds)`,
  `record LifetimeStats(long Files, long OriginalBytes, long OutputBytes, IReadOnlyDictionary<OutputFormat,int> ByFormat)`.
- Migration contracts: `record LegacyInstall(string Folder, string? ExePath, string? InputsDir, int InputImageCount, string? OutputsDir, int OutputImageCount, string? FfmpegPath, string? PngquantPath)`,
  `record LegacyOrphan(string Path, long Bytes, DateTime LastWriteUtc)`, `record LegacyScanResult(IReadOnlyList<LegacyInstall> Installs, IReadOnlyList<LegacyOrphan> Orphans)` (+ `long OrphanBytes`),
  `record MigrationOptions(bool ImportInputs, bool UseOutputsAsDestination, int LegacyLevel, bool RegisterLegacyTools, bool CreateWatchRule, bool CleanOrphans)`,
  `record MigrationReport(int ImportedFiles, string? Destination, string ProfileId, int ToolsRegistered, bool WatchRuleCreated, int OrphansRecycled, long BytesFreed, IReadOnlyList<string> Notes)`.
- `Tokens.xaml`: every palette token from spec §3 as `Color x:Key="C.<Name>"` and frozen `SolidColorBrush x:Key="B.<Name>"`; `B.Prisma`, `B.Spectrum`;
  `FontFamily x:Key="Font.UI"` / `Font.Mono`; sizes `FontSize.Caption 11, Body 13, BodyLarge 15, Subtitle 18, Title 24, Display 40, Hero 64`;
  `Duration.Micro 0:0:0.09, Quick 0.16, Standard 0.24, Page 0.42, Hero 0.72`; `CornerRadius.Small 6, Medium 10, Large 16, Pill 999`.
- `MotionSettings` (static): `bool ReducedMotion`, `double Speed = 1`, `event Changed`, `TimeSpan Scale(TimeSpan)`.
- Test support: `TestTools.Root` (env `CARBONADO_TOOLS_DIR` else walk up to `tools/manifest.json`), `TestTools.Path(ToolId)`,
  `TestTools.Require(ToolId)` (xUnit skip); `TestImages` synthetic generators (`Gradient`, `Photo-like noise+edges`, `Graphic`, `Alpha`, `Palette`, `Gray`, 16-bit),
  `TestImages.SystemPhotos` (C:\Windows\Web JPEGs); `TempDir : IDisposable`; `Sta.Run(Action)`; `Snapshot.Render(FrameworkElement, w, h, path)`.

- [ ] **Step 1:** Create the files above; `dotnet build Carbonado.sln -c Debug` → `Build succeeded, 0 Warning(s), 0 Error(s)`.
- [ ] **Step 2:** Write `PixelBufferTests` (`Create_ClearsMemory`, `RowSpans_HaveExactLength`, `ToDepth_RoundTrip8_16_8_IsExact`, `Dispose_Twice_IsSafe`, `Create_RejectsNonPositiveSizes`) → `dotnet test tests/Carbonado.Core.Tests` → all pass.
- [ ] **Step 3:** `tools/manifest.json` lists every extracted file with SHA-256 (values recorded at extraction); `build/fetch-tools.ps1 -Verify` → "all tools verified".

---

### Task 1: Format sniffing (core-io)
**Files:** Create `src/Carbonado.Core/Imaging/FormatSniffer.cs`; Test `tests/Carbonado.Core.Tests/Imaging/FormatSnifferTests.cs`.
**Interfaces:** Produces `static ImageFormat FormatSniffer.Detect(ReadOnlySpan<byte> header, string? extensionHint = null)`,
`static ImageFormat FormatSniffer.DetectFile(string path)` (reads ≤ 64 bytes; APNG detected by `acTL` before `IDAT` → needs up to 4 KB),
`static bool FormatSniffer.IsAnimated(string path, ImageFormat f)` (GIF > 1 image descriptor, APNG, WebP `ANIM`).
- [ ] Tests `Detect_RecognizesEveryMagic` (Theory over PNG `89 50 4E 47`, JPEG `FF D8 FF`, GIF87a/89a, BMP `BM`, TIFF II*/MM*, WebP RIFF..WEBP,
  AVIF ftyp `avif`/`avis`, HEIC ftyp `heic|heix|mif1+heic`, JXL `FF 0A` and container `00 00 00 0C 4A 58 4C 20`, ICO `00 00 01 00`, QOI `qoif`,
  PNM `P1`–`P7`, PSD `8BPS`, EXR `76 2F 31 01`, HDR `#?RADIANCE`, JP2 `00 00 00 0C 6A 50 20 20`, DDS `DDS `, JXR `II BC`),
  `Detect_TgaOnlyByExtension`, `Detect_ContentWinsOverWrongExtension` (PNG bytes + ".jpg" hint → Png), `Detect_EmptyOrShort_IsUnknown`,
  `IsAnimated_GifApngWebp` (generated multi-frame files).
- [ ] Implement; run `dotnet test --filter FullyQualifiedName~FormatSniffer` → PASS.

### Task 2: PNG + PNM codecs (core-io)
**Files:** Create `src/Carbonado.Core/Codecs/{Crc32,Adler32,PngChunks,PngDecoder,PngEncoder,PngEncodeOptions,PnmCodec}.cs`; Tests `tests/Carbonado.Core.Tests/Codecs/{Crc32Tests,PngCodecTests,PnmCodecTests}.cs`.
**Interfaces:** Produces `Crc32.Compute(ReadOnlySpan<byte>) → uint`, `Crc32.Append(uint, ReadOnlySpan<byte>) → uint` (slice-by-8);
`PngDecoder.Decode(Stream) → DecodedImage` (color types 0/2/3/4/6, depths 1/2/4/8/16, PLTE+tRNS, Adam7, iCCP (zlib) → `Metadata.Icc`, eXIf → `Metadata.Exif`, pHYs → DPI, APNG → first frame + `FrameCount`; output depth 16 only when source is 16-bit);
`enum PngColorType : byte { Gray = 0, Rgb = 2, Palette = 3, GrayAlpha = 4, Rgba = 6 }`,
`enum PngFilterStrategy { None, Sub, Up, Average, Paeth, MinSum, Entropy }`,
`record PngEncodeOptions(PngColorType? ColorType = null, int? BitDepth = null, System.IO.Compression.CompressionLevel Level = SmallestSize, PngFilterStrategy Filter = MinSum, bool WriteIcc = true, bool WriteExif = false, bool Interlace = false)`,
`PngEncoder.Encode(Stream output, PixelBuffer pixels, ImageMetadata? metadata, PngEncodeOptions options)`, `PngEncoder.EncodeFast(Stream, PixelBuffer, ImageMetadata?)` (filter None/Sub, `CompressionLevel.Fastest`) for intermediates;
`PnmCodec.Decode(Stream) → DecodedImage` (P5/P6/P7, maxval 255/65535, TUPLTYPE GRAY/GRAY_ALPHA/RGB/RGB_ALPHA), `PnmCodec.WritePam(Stream, PixelBuffer)`, `PnmCodec.WritePpm(Stream, PixelBuffer)` (drops alpha).
- [ ] Tests: `Crc32_KnownVectors` ("123456789" → 0xCBF43926), `Png_RoundTrip_AllColorTypesAndDepths` (Theory: every legal type/depth; decode(encode(x)) pixel-identical),
  `Png_DecodesInterlaced` (encode Interlace=true → decode equals), `Png_PreservesIccAndExif`, `Png_Palette_WithTrns_DecodesAlpha`,
  `Png_RejectsBadCrc_WithInvalidDataException`, `Png_Truncated_Throws`, `Png_DecodesWpfEncodedFiles` (PngBitmapEncoder output equals WIC decode),
  `Png_ApngFirstFrameAndCount`, `Pnm_RoundTrip_8And16bit`, `Pam_GrayAlpha_Decodes`.
- [ ] Implement; run filter `~Codecs` → PASS; tools cross-check `Png_OutputAcceptedByOxipngAndCjxl` (TestTools.Require) → PASS.

### Task 3: Built-in PNG optimizer (core-io)
**Files:** Create `src/Carbonado.Core/Codecs/PngOptimizer.cs`; Test `tests/Carbonado.Core.Tests/Codecs/PngOptimizerTests.cs`.
**Interfaces:** Produces `record PngOptimizeOptions(bool CleanInvisiblePixels = true, bool KeepIcc = true, bool KeepExif = false, int Effort = 2 /*1..3: number of filter strategies × levels*/)`,
`static byte[] PngOptimizer.Optimize(PixelBuffer pixels, ImageMetadata? metadata, PngOptimizeOptions options)` — exact reductions only:
16→8 when every sample is v·257; RGBA→RGB when opaque; →Gray when R=G=B; →Palette (with tRNS) when ≤ 256 RGBA colors, palette sorted by
luminance/alpha; bit-depth packing 1/2/4 for small palettes/gray; tries strategies (None, MinSum, Entropy, Paeth) and keeps smallest.
- [ ] Tests: `Optimize_IsPixelExact` (Theory over TestImages; decode(optimized) vs input via visible comparison when CleanInvisiblePixels else strict),
  `Optimize_ChoosesPaletteFor256Colors`, `Optimize_Gray_UsesGrayType`, `Optimize_16bitExact_DropsTo8`, `Optimize_NeverLargerThanFastEncode`.
- [ ] Implement; run filter `~PngOptimizer` → PASS.

### Task 4: Analysis, comparison, alpha ops, resampler (core-pixels)
**Files:** Create `src/Carbonado.Core/Analysis/{ImageAnalyzer,PixelComparer}.cs`, `src/Carbonado.Core/Imaging/{AlphaOps,Resampler,ResampleFilter,ColorSpace}.cs`;
Tests `tests/Carbonado.Core.Tests/Analysis/*`, `tests/Carbonado.Core.Tests/Imaging/ResamplerTests.cs`.
**Interfaces:** Produces `ImageAnalyzer.Analyze(PixelBuffer) → ImageAnalysis` (Kind: Graphic when UniqueColors ≤ 4096 or FlatRatio ≥ 0.55;
Photo when UniqueColors = -1 or > 20 000 and FlatRatio < 0.35; else Mixed; FlatRatio = share of pixels equal to their left neighbour;
EdgeDensity = share of |∇| > 32);
`PixelComparer.Identical(PixelBuffer a, PixelBuffer b, bool ignoreInvisibleRgb) → bool`, `PixelComparer.Hash(PixelBuffer, bool ignoreInvisibleRgb) → byte[]` (SHA-256 over canonical RGBA16),
`PixelComparer.Psnr(PixelBuffer a, PixelBuffer b) → double` (∞ when identical);
`AlphaOps.IsOpaque(PixelBuffer) → bool`, `AlphaOps.CleanInvisible(PixelBuffer)` (RGB := 0 where A = 0), `AlphaOps.Flatten(PixelBuffer, Rgba32 background) → PixelBuffer`;
`enum ResampleFilter { Lanczos3, Mitchell, Box }`, `Resampler.Resize(PixelBuffer src, int width, int height, ResampleFilter filter = Lanczos3) → PixelBuffer` (linear light, premultiplied alpha, Parallel.For rows),
`Resampler.FitWithin(int w, int h, int maxW, int maxH) → (int W, int H)` (never upscales, keeps aspect, ≥ 1);
`ColorSpace.SrgbToLinear(float)`, `ColorSpace.LinearToSrgb(float)`, `ColorSpace.SrgbToLinearLut8` (256 floats), `ColorSpace.TryCreateLinearizer(byte[]? icc) → Func<…>?` (matrix/TRC ICC → linear sRGB; null = treat as sRGB).
- [ ] Tests: `Analyze_ClassifiesSyntheticKinds`, `Analyze_DetectsAlphaUsage`, `Analyze_UniqueColorsCapped`, `Identical_IgnoresInvisibleRgbWhenAsked`,
  `Hash_EqualForEqualVisiblePixels`, `Psnr_KnownNoise`, `Resize_ConstantImageStaysConstant`, `Resize_AlphaEdgesNoDarkHalo`, `FitWithin_NeverUpscales`,
  `Srgb_LinearRoundTrip`, `IccLinearizer_DisplayP3_DiffersFromSrgb` (synthetic matrix/TRC profile bytes).
- [ ] Implement; run filter `~Analysis|~Resampler` → PASS.

### Task 5: SSIMULACRA2 (core-pixels)
**Files:** Create `src/Carbonado.Core/Metrics/{RecursiveGaussian,XybImage,Ssimulacra2,Ssimulacra2Reference,Ssimulacra2Map}.cs`; Test `tests/Carbonado.Core.Tests/Metrics/Ssimulacra2Tests.cs`.
**Interfaces:** Produces `Ssimulacra2.Compute(PixelBuffer reference, PixelBuffer distorted, byte[]? icc = null, CancellationToken ct = default) → double`;
`sealed class Ssimulacra2Reference : IDisposable` with `ctor(PixelBuffer reference, byte[]? icc = null)`, `double Score(PixelBuffer distorted, CancellationToken ct = default)`,
`Ssimulacra2Map ScoreWithMap(PixelBuffer distorted, CancellationToken ct = default)`; `sealed class Ssimulacra2Map { int Width, Height; float[] Error; double Score; }` (Error ∈ [0,1], scale-0 SSIM+edge error, for heat-map).
Algorithm and constants: exactly `docs/superpowers/specs/ssimulacra2-reference.md`. float planes, double accumulators; per-plane processing to bound memory;
`Ssimulacra2Reference` caches reference XYB/mu/sigma per scale; rows parallelised; vertical blur pass vectorised across columns (`Vector256<float>`).
- [ ] Tests: `Identical_Is100`, `MatchesReferenceExe` (Theory: ≥ 12 pairs — system photos re-encoded at JPEG q 30/60/90 and JXL d 0.5/2/6 via tools, synthetic
  graphics, alpha images; `|ours − ssimulacra2.exe| ≤ 0.5`, report max/mean Δ), `MonotoneWithNoise`, `Alpha_UsesWorstOfTwoBackgrounds`,
  `Reference_CachedEqualsUncached`, `RejectsSmallerThan8x8`, `Map_HighErrorWhereDistorted`, `Performance_12MP_Under1500ms` (Release only, trait Perf).
- [ ] Implement; run filter `~Ssimulacra2` → PASS with max Δ printed.

### Task 6: Tools, manifest, process runner, job object (core-io)
**Files:** Create `src/Carbonado.Core/Tools/{ToolManifest,ToolLocator,ToolLocatorOptions,ProcessSpec,ProcessResult,ProcessRunner,JobObject}.cs`; Tests `tests/Carbonado.Core.Tests/Tools/*`.
**Interfaces:** Produces `ToolManifest.Load(string path) → ToolManifest` (`IReadOnlyList<ToolManifestPackage> Packages`; package: `Name, Version, Source, ArchiveSha256, IReadOnlyDictionary<string,string> Files`);
`record ToolLocatorOptions(IReadOnlyList<string> SearchRoots, string? ManifestPath = null, bool VerifyHashes = true, IReadOnlyList<string>? LegacyDirs = null)`,
`sealed class ToolLocator { ctor(ToolLocatorOptions); IReadOnlyList<ToolInfo> Discover(); ToolInfo? Get(ToolId id); bool Has(ToolId id); static ToolLocatorOptions DefaultOptions(string appBaseDir); }`
(layout `<root>/<package>/<exe>`: libjxl/{cjxl,djxl,jxlinfo,ssimulacra2}, libavif/{avifenc,avifdec}, libwebp/{cwebp,dwebp,gif2webp}, oxipng, ect, pngquant, ffmpeg; then legacy dirs, then PATH; hashes computed lazily once and cached by (path, size, mtime));
`record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null, TimeSpan? Timeout = null, bool CaptureStdout = false, byte[]? Stdin = null, ProcessPriorityClass Priority = ProcessPriorityClass.BelowNormal)`,
`record ProcessResult(int ExitCode, byte[] Stdout, string Stderr, TimeSpan Elapsed, bool TimedOut) { bool Success; }`,
`sealed class ProcessRunner : IDisposable { static ProcessRunner Shared; Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct); int ActiveCount; void KillAll(); }` (every process assigned to a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; cancellation/timeout kills the tree; stderr capped at 64 KB).
- [ ] Tests: `Manifest_LoadsRepoManifest`, `Locator_FindsAllRepoTools`, `Locator_FlagsHashMismatch` (copy exe to temp + manifest with wrong hash), `Locator_UsesLegacyDirAsFallback`,
  `Runner_CapturesExitCodeAndStderr` (cjxl bad args), `Runner_StdoutBinary` (ffmpeg rawvideo), `Runner_Timeout_KillsProcess`, `Runner_Cancel_KillsProcess`,
  `Runner_DisposeKillsChildren` (no process with that PID alive after Dispose), `Runner_PassesUnicodePathsVerbatim` (input "àèìòù 日本 x y.png").
- [ ] Implement; run filter `~Tools` → PASS.

### Task 7: Decoding & loader (core-io)
**Files:** Create `src/Carbonado.Core/Decoding/{LoadOptions,IImageLoader,ImageLoader,WicDecoder,ToolDecoders,ExifReader,Orientation}.cs`; Tests `tests/Carbonado.Core.Tests/Decoding/*`.
**Interfaces:** Produces `record LoadOptions(bool ApplyOrientation = true, bool FirstFrameOnly = true)`,
`interface IImageLoader { Task<DecodedImage> LoadAsync(string path, LoadOptions options, CancellationToken ct); Task<PixelBuffer> DecodeOutputAsync(string path, OutputFormat format, CancellationToken ct); }`,
`sealed class ImageLoader : IImageLoader { ctor(ToolLocator tools, ProcessRunner runner, string workDir); }` routing: Png→PngDecoder, Pnm→PnmCodec, Jpeg/Bmp/Gif/Tiff/Ico/Jxr/Dds→WicDecoder,
WebP→dwebp `-pam`, Avif→avifdec PNG (`-d 16` when > 8 bit), Jxl→djxl PAM (`--bits_per_sample=-1`), Heic and everything else→ffmpeg `-f rawvideo -pix_fmt rgba64le` (+ ffprobe-free size from stderr parse `-f null`);
`WicDecoder.Decode(string path) → DecodedImage` (WPF imaging on the calling thread, `BitmapCacheOption.OnLoad`, `IgnoreColorProfile`, CMYK→RGB conversion, ICC from ColorContexts, EXIF APP1 bytes);
`ExifReader.ReadOrientation(ReadOnlySpan<byte> tiff) → ushort`, `ExifReader.StripGps(byte[] tiff) → byte[]`, `ExifReader.WithOrientation(byte[] tiff, ushort value) → byte[]`;
`Orientation.Apply(PixelBuffer src, ushort orientation) → PixelBuffer` (1..8; returns src when 1).
- [ ] Tests: `Loader_LoadsEveryFormat` (Theory: files produced by the tools from one synthetic image → all decode to the same size; lossless ones pixel-identical),
  `Loader_AppliesExifOrientation` (JPEG with orientation 6 and 8 → rotated dims and pixel checks), `Loader_CmykJpeg_ConvertsToRgb`,
  `Loader_16bitPng_Keeps16`, `Loader_ReadsIccFromJpegAndPng`, `Exif_StripGps_RemovesGpsIfd`, `Loader_CorruptFile_ThrowsInvalidData`,
  `DecodeOutput_RoundTripsEachOutputFormat`.
- [ ] Implement; run filter `~Decoding` → PASS.

### Task 8: Encoder adapters & registry (core-io)
**Files:** Create `src/Carbonado.Core/Encoding/{PreparedSource,EncodeRequest,EncodeOutcome,ParameterScale,IEncoderAdapter,EncoderRegistry,OutputVerifier}.cs`, `src/Carbonado.Core/Encoding/Adapters/{CjxlAdapter,AvifencAdapter,CwebpAdapter,SjpegAdapter,OxipngAdapter,EctAdapter,PngquantAdapter,Gif2WebpAdapter,BuiltinPngAdapter,FfmpegAdapters}.cs`; Tests `tests/Carbonado.Core.Tests/Encoding/*`.
**Interfaces:** Produces
`sealed class PreparedSource : IDisposable { string OriginalPath; ImageFormat OriginalFormat; PixelBuffer Pixels; ImageMetadata Metadata; ImageAnalysis Analysis; string WorkDir; Task<string> GetPngAsync(CancellationToken); Task<string> GetPamAsync(CancellationToken); Task<string?> GetIccPathAsync(); Task<string?> GetExifPathAsync(); static PreparedSource Create(string originalPath, ImageFormat format, PixelBuffer pixels, ImageMetadata metadata, ImageAnalysis analysis, string workDir); }` (intermediates written once, lazily, thread-safe);
`record EncodeRequest(PreparedSource Source, EncodeKind Kind, double? Parameter, EffortLevel Effort, ChromaMode Chroma, int Threads, string OutputPath)`,
`record EncodeOutcome(bool Success, string OutputPath, long Bytes, TimeSpan Elapsed, string CommandLine, string? Error)`,
`record ParameterScale(double Min, double Max, bool HigherIsBetter, double Resolution)`,
`interface IEncoderAdapter { string Id; OutputFormat Format; IReadOnlyList<ToolId> RequiredTools; bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source); ParameterScale Scale; double InitialGuess(double targetScore, ImageAnalysis a); IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath); Task<EncodeOutcome> EncodeAsync(EncodeRequest r, CancellationToken ct); }`,
`sealed class EncoderRegistry { ctor(ToolLocator tools, ProcessRunner runner); IReadOnlyList<IEncoderAdapter> Available; IEnumerable<IEncoderAdapter> For(OutputFormat f, EncodeKind k); }`,
`sealed class OutputVerifier { ctor(IImageLoader loader, ToolLocator tools, ProcessRunner runner); Task<bool> VerifyJpegReconstructionAsync(string jxlPath, string originalJpegPath, CancellationToken ct); }`.
Adapter specifics (ids / args; effort map Fast/Balanced/Max/Insane):
- `jxl.cjxl`: lossy `-d <p>` (scale 0.05–15, lower better, res 0.01), `-e 5/7/9/10` (10 adds `--allow_expert_options`? no: 10 is allowed), `--num_threads=<t>`; lossless `-d 0 -e 7/9/10/10 -I 100 -g 3` at Insane; transcode `--lossless_jpeg=1 -e 7/9/9/10`; input PAM (ICC via `-x icc_pathname=` when present).
- `avif.avifenc`: `-q <p>` (0–100, higher better, res 1), `-s 8/6/3/1`, `-d 10` (8 for lossless), `-y 444` when target ≥ 80 else `420 --sharpyuv`, `-j <t>`, `--ignore-exif --ignore-xmp` per policy; lossless `-l`.
- `webp.cwebp`: `-q <p> -m 4/6/6/6 -sharp_yuv -af -mt` (+`-pass 10` Max/Insane), alpha `-alpha_q 100`; lossless `-lossless -z 6/9/9/9 -exact`(when not cleaning) ; input PAM; `-metadata icc|none|all`.
- `jpeg.sjpeg`: intermediate → `cjxl -d 0 -e 1` temp JXL → `djxl --use_sjpeg -q <p>` (0–100 higher better) → `ect -strip -progressive -<1/3/6/9>` ; Optimize kind: ECT on a copy of the original JPEG.
- `png.oxipng`: `-o 2/4/6/max --strip safe|all [-a] [-z at Insane]`; `png.ect`: `-<3/5/9/9> --strict|default --allfilters-b at Insane`; `png.builtin`: PngOptimizer; `png.pngquant` (lossy): `--quality 0-<p> --speed 3/1/1/1 --strip` then oxipng `-o 2`.
- `webp.gif2webp` (animated GIF): lossless `-min_size -m 6`, lossy `-lossy -q <p> -m 6`; `jxl.cjxl` also accepts GIF for lossless animation.
- `jxl.ffmpeg`, `avif.ffmpeg`, `webp.ffmpeg`: used only when the primary tool is missing.
- [ ] Tests: `Args_*` per adapter (exact argument lists for each effort/kind), `Encode_RealToolsProduceDecodableFiles` (Theory adapters × synthetic photo/graphic/alpha; output decodes, size > 0),
  `Lossless_IsPixelExact` (cjxl lossless, cwebp lossless, oxipng, ect, builtin → identical visible pixels), `JpegTranscode_ReconstructsBitExact` (system photo → jxl → `VerifyJpegReconstructionAsync` true),
  `PreparedSource_WritesIntermediatesOnce`, `Registry_FallsBackToFfmpegWhenPrimaryMissing`.
- [ ] Implement; run filter `~Encoding` → PASS.

### Task 9: Quality search & candidate planner (core-engine)
**Files:** Create `src/Carbonado.Core/Engine/{QualitySearch,Probe,SearchResult,CandidatePlanner,CandidatePlan}.cs`; Tests `tests/Carbonado.Core.Tests/Engine/{QualitySearchTests,CandidatePlannerTests}.cs`.
**Interfaces:** Consumes Task 8 `ParameterScale`, `IEncoderAdapter`. Produces
`record Probe(double Parameter, long Bytes, double Score, string Path)`, `record SearchResult(Probe? Best, IReadOnlyList<Probe> Probes)`,
`static Task<SearchResult> QualitySearch.ForScoreAsync(Func<double, CancellationToken, Task<Probe>> evaluate, ParameterScale scale, double target, double initialGuess, int maxIterations, double tolerance, CancellationToken ct)`,
`static Task<SearchResult> QualitySearch.ForSizeAsync(Func<double, CancellationToken, Task<Probe>> evaluate, ParameterScale scale, long maxBytes, double initialGuess, int maxIterations, CancellationToken ct)`,
`record CandidatePlan(IEncoderAdapter Encoder, EncodeKind Kind, ChromaMode Chroma)`, `static IReadOnlyList<CandidatePlan> CandidatePlanner.Plan(CompressionProfile p, ImageAnalysis a, ImageFormat source, bool animated, EncoderRegistry registry)`.
Search algorithm (score mode): keep `pass` (best passing, smallest bytes) and bracket `[lo, hi]` in "quality direction";
probe initial guess; if score < target move toward quality by an adaptive step (double each time) until passing or bound; then secant
interpolation on (param, score) between nearest failing/passing probes, clamped to the bracket interior and rounded to `Resolution`;
stop when pass.Score − target ≤ tolerance, bracket width ≤ Resolution, iterations = max, or a parameter repeats. Size mode mirrors it with bytes.
Planner rules: Lossless → JXL lossless, WebP lossless, PNG (oxipng/ect/builtin) filtered by allowed formats; JPEG source + JXL allowed + AllowJpegTranscode → add JpegTranscode;
KeepSource → same-format Optimize + same-format Lossy; Graphic content → also lossless candidates in lossy modes; alpha excludes JPEG; animated → gif2webp / cjxl lossless only;
Insane → extra AVIF chroma variant.
- [ ] Tests: `Search_FindsMinimalPassingParam` (synthetic monotone score fn; ≤ maxIterations; result within Resolution of analytic optimum), `Search_HonoursDirection` (higher-is-better and lower-is-better),
  `Search_UnreachableTarget_ReturnsBestEffortNullPass`, `Search_Cancellation`, `SizeSearch_MaximizesQualityUnderBudget`, `Planner_*` per rule above.
- [ ] Implement; run filter `~QualitySearch|~CandidatePlanner` → PASS.

### Task 10: Compression engine (core-engine)
**Files:** Create `src/Carbonado.Core/Engine/{EngineContext,EngineOptions,CompressionEngine,OutputPathResolver,AtomicFile,BuiltInProfiles,MetadataProcessor}.cs`; Tests `tests/Carbonado.Core.Tests/Engine/{CompressionEngineTests,OutputPathResolverTests,AtomicFileTests,BuiltInProfilesTests}.cs`.
**Interfaces:** Consumes Tasks 1–9. Produces
`record EngineOptions(int? MaxConcurrency = null, double MemoryBudgetFraction = 0.25, double ScoreTolerance = 0.8, string? TempRoot = null)`,
`sealed class EngineContext : IDisposable { static EngineContext Create(ToolLocator tools, EngineOptions options); ToolLocator Tools; ProcessRunner Runner; IImageLoader Loader; EncoderRegistry Encoders; OutputVerifier Verifier; string SessionTemp; }`,
`sealed class CompressionEngine { ctor(EngineContext ctx); Task<CompressionResult> RunAsync(CompressionJob job, IProgress<JobProgress>? progress, int threadBudget, CancellationToken ct); }` (pipeline = spec §4.4 steps 1–10),
`static string OutputPathResolver.Resolve(string inputPath, string? inputRoot, OutputPlan plan, OutputFormat format, string profileId, double? score)` (tokens `{name} {ext} {format} {profile} {score} {date}`; invalid chars replaced),
`static string? AtomicFile.Commit(string tempPath, string finalPath, ConflictPolicy policy)` (returns final path or null when skipped; rename → "name (1).ext"),
`static class BuiltInProfiles { IReadOnlyList<CompressionProfile> All; CompressionProfile Default; CompressionProfile? Find(string id); CompressionProfile Legacy(int level); }` (table spec §4.5),
`static class MetadataProcessor { ImageMetadata Apply(ImageMetadata m, MetadataPolicy p); }`.
- [ ] Tests: `Engine_Photo_MeetsTargetAndShrinks` (system photo, profile vs → Outcome Compressed, Score ≥ 85, Verification PerceptualScore, OutputBytes < OriginalBytes),
  `Engine_Lossless_IsVerifiedPixelExact`, `Engine_JpegToJxl_BitExact`, `Engine_NeverLarger_KeepsOriginal` (already-optimal tiny PNG → KeptOriginal, output = copy or none per plan),
  `Engine_CorruptInputs_FailPerFile` (zero-byte, truncated JPEG, PNG named .jpg → Failed with message / correct decode), `Engine_LongUnicodePath_Roundtrip`,
  `Engine_KeepsIccProfile` (P3-tagged PNG → output carries ICC), `Engine_StripAll_RemovesExif`, `Engine_AnimatedGif_StaysAnimated`, `Engine_Resize_FitsWithin`,
  `Engine_TargetSize_UnderBudget`, `Engine_ReplaceOriginal_BacksUp`, `Resolver_*`, `AtomicFile_*` (no temp left, conflict policies), `Profiles_AllValid`.
- [ ] Implement; run filter `~Engine` → PASS.

### Task 11: Batch scheduler (core-engine)
**Files:** Create `src/Carbonado.Core/Engine/{BatchScheduler,ResourceBudget}.cs`; Test `tests/Carbonado.Core.Tests/Engine/BatchSchedulerTests.cs`.
**Interfaces:** Produces `sealed class BatchScheduler : IBatchProcessor { ctor(CompressionEngine engine, EngineOptions options); int Pending; int Running; }`;
`sealed class ResourceBudget { ctor(int threads, long memoryBytes); ValueTask<Lease> AcquireAsync(int threads, long bytes, CancellationToken ct); }` (estimate = W·H·64 bytes);
workers = min(MaxConcurrency ?? logical cores, jobs); threadBudget per job = max(1, cores / running); Progress throttled to 20 Hz per job + immediate stage changes.
- [ ] Tests: `Scheduler_RunsAllJobs_RaisesBatchCompleted`, `Scheduler_PauseResume`, `Scheduler_CancelOne`, `Scheduler_CancelMidJob_LeavesNoOutputAndNoProcess`,
  `Scheduler_RespectsMemoryBudget` (fake engine with large estimates → concurrency 1), `Scheduler_ProgressIsThrottled`.
- [ ] Implement; run filter `~BatchScheduler` → PASS.

### Task 12: Theme & control styles (ui-brand)
**Files:** Create `src/Carbonado.UI/Theme/{Theme.xaml,Typography.xaml,Icons.xaml,Styles/*.xaml}`, `src/Carbonado.UI/Controls/IconPresenter.cs`; Tests `tests/Carbonado.UI.Tests/Theme/ThemeSnapshotTests.cs`.
**Interfaces:** Consumes `Tokens.xaml`, `MotionSettings`. Produces pack URI `/Carbonado.UI;component/Theme/Theme.xaml` (merges Tokens, Typography, Icons, Styles) with implicit styles for
Button, RepeatButton, ToggleButton, CheckBox, RadioButton, ComboBox(+Item), TextBox, PasswordBox, Slider, ScrollBar, ScrollViewer, ListBox(+Item), ListView(+Item), ToolTip, ContextMenu, MenuItem, ProgressBar, Separator, Expander, GroupBox, Window;
keyed styles `Button.Primary` (Prisma gradient + sheen), `Button.Secondary`, `Button.Ghost`, `Button.Icon`, `Button.Danger`, `Toggle.Switch`, `Radio.Chip`, `Text.Hero`, `Text.Display`, `Text.Title`,
`Text.Subtitle`, `Text.Body`, `Text.Caption`, `Text.Mono`, `Border.Card`, `Border.Glass`; geometry icons `Icon.*` (list in spec §4.9 + chrome icons) on a 20×20 grid;
`IconPresenter : FrameworkElement` (`Geometry Icon`, `Brush Foreground`, `double StrokeThickness = 1.6`, `bool Filled`).
- [ ] Tests: `EveryStyle_RendersInAllStates` (Theory: control × {Normal, Hover (VisualStateManager.GoToState), Pressed, Disabled, Checked}; PNG under `tests/artifacts/ui/theme/`; asserts non-empty and background ≠ transparent where expected),
  `Icons_AllKeysResolveToGeometry`, `Tokens_NoLiteralHexOutsideTokens` (scan XAML files), `Theme_LoadsUnder50ms`.
- [ ] Implement; run `dotnet test tests/Carbonado.UI.Tests --filter ~Theme` → PASS; review snapshots visually.

### Task 13: Brand controls (ui-brand)
**Files:** Create `src/Carbonado.UI/Controls/{PrismButton,GemProgressRing,CountUpText,RadialGauge,BeforeAfterViewer,CompareMode,DropZone,ParticleField,NavRail,NavItem,StatCard,BarChart,DonutChart,DonutSlice,ToastHost,Toast,ModalHost,Chip}.cs` + `Theme/Styles/Controls.Brand.xaml`; Tests `tests/Carbonado.UI.Tests/Controls/*`.
**Interfaces:** Produces (dependency properties):
`PrismButton : Button { Geometry Icon; bool IsBusy; }` (hover sheen sweep 600 ms, press ripple from click point);
`GemProgressRing : Control { double Value; bool IsIndeterminate; GemState State {Idle, Working, Done, Kept, Failed}; }`;
`CountUpText : FrameworkElement { double Value; CountFormat Format {Integer, Bytes, Percent, Score}; Duration Duration; Brush Foreground; double FontSize; }` (odometer roll, tabular digits);
`RadialGauge : Control { double Value /*0..1*/; string Caption; string ValueText; }`;
`BeforeAfterViewer : Control { ImageSource Before, After, Heatmap; CompareMode Mode {Split, SideBySide, Flicker, Heatmap}; double Split; double Zoom; Point Pan; ICommand ZoomToFitCommand, ZoomToActualCommand; }` (wheel zoom around cursor, drag pan, synchronized);
`DropZone : ContentControl { bool IsDragOver; event EventHandler<FilesDroppedEventArgs> FilesDropped; }` (`FilesDroppedEventArgs.Paths : IReadOnlyList<string>`);
`ParticleField : FrameworkElement { int Count; double Intensity; Point? Attractor; bool IsRunning; }`;
`NavRail : ListBox` with `NavItem { Geometry Icon; string Label; string Shortcut; }`;
`StatCard : ContentControl { string Title; object Value; string Caption; Geometry Icon; }`; `BarChart { IList<double> Values; IList<string> Labels; }`;
`DonutChart { IList<DonutSlice> Slices; }` (`DonutSlice(string Label, double Value, Brush Brush)`);
`ToastHost : ItemsControl` + `Toast { string Title; string Message; ToastKind Kind {Info, Success, Warning, Error}; }`; `ModalHost : ContentControl { object Dialog; bool IsOpen; }`; `Chip : RadioButton { string Badge; }`.
- [ ] Tests: snapshot per control (several property states) + behaviour: `CountUpText_ReachesTargetValue`, `Gauge_ClampsValue`, `Viewer_ZoomAroundCursorKeepsPointFixed`,
  `DropZone_RaisesFilesDropped`, `Particles_DeterministicForSeed`, `Donut_SlicesSumTo360`.
- [ ] Implement; run filter `~Controls` → PASS; review snapshots.

### Task 14: Effects & transitions (ui-brand)
**Files:** Create `src/Carbonado.UI/Effects/{ChromaticAberration.fx,Ripple.fx,Grain.fx}` + compiled `.ps`, `src/Carbonado.UI/Effects/{ChromaticAberrationEffect,RippleEffect,GrainEffect,EffectSupport}.cs`,
`src/Carbonado.UI/Transitions/{TransitionHost,TransitionKind,Stagger}.cs`, `build/compile-shaders.ps1`; Tests `tests/Carbonado.UI.Tests/Transitions/*`.
**Interfaces:** Produces `ChromaticAberrationEffect : ShaderEffect { double Amount; Point Center; }`, `RippleEffect { Point Center; double Radius; double Amplitude; double Phase; }`, `GrainEffect { double Time; double Amount; }`,
`EffectSupport.IsHardwareAccelerated` (RenderCapability.Tier >> 16 ≥ 2);
`TransitionHost : ContentControl { TransitionKind Transition {Refraction, Fade, None}; }` (on Content change: outgoing snapshot drifts −8 px, fades 180 ms with aberration 0→0.6; incoming rises 12 px, blur 8→0, 420 ms Crystallize; staggered children);
`Stagger` attached: `Stagger.Index` (int), `Stagger.Step` (TimeSpan, default 35 ms) — children animate in order on host reveal.
- [ ] Tests: `Shaders_LoadFromPackUri`, `TransitionHost_SwapsContent_AndEndsWithOnlyNewContent`, `TransitionHost_ReducedMotion_IsFadeOnly`, `Stagger_OrdersDelays`.
- [ ] Implement; `pwsh build/compile-shaders.ps1` regenerates `.ps`; run filter `~Transitions` → PASS.

### Task 15: Timeline engine (ui-cinematics)
**Files:** Create `src/Carbonado.UI/Cinematics/{Easing,Easings,CubicBezier,Keyframe,Track,Timeline,CinematicClock}.cs`; Tests `tests/Carbonado.UI.Tests/Cinematics/{EasingsTests,TimelineTests}.cs`.
**Interfaces:** Produces `delegate double Easing(double t)`; `Easings.Linear, Crystallize (0.16,1,0.3,1), Implode (0.7,0,0.84,0), Glide (0.4,0,0.2,1), Settle (back-out 1.15), ExpoOut, ExpoIn, SineInOut, ElasticOut`, `Easings.Bezier(double x1, double y1, double x2, double y2) → Easing` (Newton + bisection, ≤ 1e-6);
`sealed class Track<T> { ctor(Func<T,T,double,T> lerp); Track<T> Key(double time, T value, Easing? ease = null); T Evaluate(double t); }`;
`sealed class Timeline { double Duration; Timeline Bind<T>(Track<T> track, Action<T> apply); Timeline At(double time, Action action); void Evaluate(double t); }` (pure: same t ⇒ same state; At-actions fire once when crossing);
`sealed class CinematicClock { ctor(Timeline timeline); void Play(); void Skip(); void Seek(double t); double Time; double Speed; bool IsRunning; event EventHandler? Completed; }` (CompositionTarget.Rendering; Speed ×MotionSettings).
- [ ] Tests: `Bezier_MatchesKnownPoints`, `Easings_StartAtZeroEndAtOne`, `Track_InterpolatesAndClamps`, `Timeline_EvaluateIsDeterministic`, `Timeline_AtActionsFireOnce`, `Clock_SkipJumpsToEndAndCompletes`.
- [ ] Implement; run filter `~Cinematics` → PASS.

### Task 16: Intro composition & brand mark (ui-cinematics)
**Files:** Create `src/Carbonado.UI/Brand/{DiamondMark,WordmarkGeometry}.cs`, `src/Carbonado.UI/Cinematics/Intro/{IntroOverlay,IntroMode,IntroTimelineFactory,Diamond3D,BrilliantCutMesh,ImplosionField,LightRays,Shockwave,FilmGrainLayer,WordmarkReveal}.cs` (+ XAML if useful); Tests `tests/Carbonado.UI.Tests/Cinematics/IntroTests.cs`.
**Interfaces:** Produces `DiamondMark : FrameworkElement { double Glint; }` (vector logo for title bar, 16–256 px), `WordmarkGeometry.Letters → IReadOnlyList<Geometry>` ("CARBONADO", chamfered facets),
`enum IntroMode { Cinematic, Short, Off }`, `IntroOverlay : Control { IntroMode Mode; FrameworkElement? LogoTarget; event EventHandler? Completed; void Play(); void Skip(); void RenderAt(double seconds); double Duration; }`
(Duration 3.4 / 0.9 / 0; storyboard spec §4.10; hand-off flies the 3D diamond to `LogoTarget` bounds; `RenderAt` sets the full composition state for time t — used by tests);
`BrilliantCutMesh.Create(int segments = 16) → MeshGeometry3D` (table, crown, girdle, pavilion, culet; flat-shaded).
- [ ] Tests: `Intro_RenderAtKeyTimes_Snapshots` (t = 0.2, 0.8, 1.3, 1.6, 2.2, 2.6, 3.0, 3.35 → PNGs under `tests/artifacts/ui/intro/`, each non-uniform), `Intro_SameTimeSameFrame` (pixel-identical renders),
  `Intro_SkipCompletesImmediately`, `Intro_ShortModeDuration`, `Intro_ReducedMotion_UsesShort`, `Mesh_IsClosedAndOutwardNormals`, `Wordmark_HasNineLetters`.
- [ ] Implement; run filter `~Intro` → PASS; review snapshots frame by frame.

### Task 17: Persistence (core-engine)
**Files:** Create `src/Carbonado.Core/Persistence/{DataPaths,SettingsJsonContext,SettingsStore,HistoryStore,SessionTemp}.cs`, `src/Carbonado.Core/Diagnostics/Log.cs`; Tests `tests/Carbonado.Core.Tests/Persistence/*`.
**Interfaces:** Produces `sealed class DataPaths { static DataPaths Resolve(string? overrideDir, string appBaseDir); string Root; string SettingsFile; string HistoryFile; string TempRoot; string LogsDir; bool IsPortable; }` (portable when `portable.flag` beside exe);
`sealed class SettingsStore { ctor(DataPaths p); AppSettings Load(); void Save(AppSettings s); void SaveDebounced(AppSettings s, TimeSpan? delay = null); void Flush(); }` (atomic write, corrupt file → backup `.bad` + defaults);
`sealed class HistoryStore { ctor(DataPaths p); void Append(CompressionResult r, string profileId); IReadOnlyList<HistoryEntry> ReadRecent(int max); LifetimeStats Stats(); void Clear(); }`;
`static class SessionTemp { string Create(string tempRoot); void CleanupStale(string tempRoot, TimeSpan olderThan); }`; `static class Log { void Init(string dir); void Info(string m); void Error(string m, Exception? e = null); }`.
- [ ] Tests: `Settings_RoundTrip`, `Settings_CorruptFile_FallsBackToDefaults`, `Settings_DebounceCoalesces`, `History_AppendAndStats`, `DataPaths_PortableFlag`, `SessionTemp_CleansStale`.
- [ ] Implement; run filter `~Persistence` → PASS.

### Task 18: App shell (lead)
**Files:** Create `src/Carbonado.App/{App.xaml,App.xaml.cs,AppServices.cs}`, `Startup/{StartupProfiler,CommandLine,SingleInstance}.cs`, `Shell/{MainWindow.xaml(.cs),ShellViewModel,PageKind,CommandPalette*}.cs`,
`Localization/{Strings.it.xaml,Strings.en.xaml,Loc.cs}`, `Services/{DialogService,ToastService,SoundService}.cs`; Tests `tests/Carbonado.App.Tests/Shell/*`.
**Interfaces:** Consumes UI Theme + IntroOverlay + TransitionHost + NavRail + ToastHost + ModalHost; Core persistence. Produces `ShellViewModel { PageKind Current; object CurrentPage; IRelayCommand<PageKind> NavigateCommand; }`,
`enum PageKind { Forge, Compare, Profiles, Archive, Engines, Settings, About }`, `Loc.Get(string key)`, `Loc.SetLanguage(string code)`; command line: `Carbonado.exe [files…] [--data-dir d] [--intro cinematic|short|off] [--profile id] [--start] [--snapshot dir]`.
- [ ] Tests: `Shell_NavigatesAllPages`, `Loc_EveryKeyExistsInBothLanguages`, `CommandLine_Parses`, `SingleInstance_ForwardsArgs`.
- [ ] Implement; run `dotnet test tests/Carbonado.App.Tests` → PASS; launch app, intro → shell.

### Task 19: Pages & view-models (lead)
**Files:** Create `src/Carbonado.App/Pages/{Forge,Compare,Profiles,Archive,Engines,Settings,About}/*View.xaml(.cs)` + `*ViewModel.cs`, `Pages/Forge/QueueItemViewModel.cs`,
`Services/{EngineService,ThumbnailService,ClipboardService}.cs`; Tests `tests/Carbonado.App.Tests/Pages/*`.
**Interfaces:** Consumes `IBatchProcessor`, profiles, history, tools. Produces `EngineService { IBatchProcessor Batch; ToolLocator Tools; IReadOnlyList<CompressionProfile> Profiles; }`,
`ForgeViewModel { ObservableCollection<QueueItemViewModel> Items; CompressionProfile Profile; IAsyncRelayCommand StartCommand; … totals }` (dedupe by full path; folders expanded recursively by `FormatInfo.InputExtensions`).
- [ ] Tests: `Forge_AddFiles_DedupesAndExpandsFolders`, `Forge_ProgressAggregation`, `Forge_Totals`, `Profiles_DuplicateAndEdit`, `Settings_ChangesPersist`, `Archive_StatsFromHistory`, page snapshots (each page with sample data).
- [ ] Implement; run tests → PASS; manual run.

### Task 20: Migration, watch folders, Explorer integration (20a core-engine, 20b lead)
**Files:** 20a Create `src/Carbonado.Core/Migration/{LegacyMigrator,LegacyLevels,IRecycleBin,RecycleBin}.cs`, `src/Carbonado.Core/Watch/FolderWatcher.cs`; Tests `tests/Carbonado.Core.Tests/Migration/*`, `Watch/*`.
20b Create `src/Carbonado.App/Pages/Settings/MigrationWizard*.cs`, `Services/{WatchFolderService,ShellIntegrationService}.cs`.
**Interfaces:** Produces `LegacyMigrator { LegacyScanResult Scan(IEnumerable<string> roots, string? legacyTempDir = null); MigrationReport Apply(LegacyScanResult scan, MigrationOptions o, AppSettings s, Action<IEnumerable<string>> enqueue); }`,
orphan regex `^[A-Za-z0-9]{28}(-or8)?\.(png|jpg|webp|avif)$`; `IRecycleBin.Send(string path) → bool` (SHFileOperation FOF_ALLOWUNDO|FOF_NOCONFIRMATION|FOF_SILENT);
`FolderWatcher : IDisposable { ctor(WatchRule rule); event EventHandler<string>? FileReady; }` (debounce 750 ms + size-stable check);
`ShellIntegrationService { bool IsRegistered; void Register(string exePath); void Unregister(); }` (HKCU\Software\Classes\SystemFileAssociations\image\shell\Carbonado).
- [ ] Tests: `Scan_FindsLegacyTree` (synthetic `IMGCompress.exe`, inputs/outputs, ffmpeg/pngquant stubs), `Scan_OrphanRegexIsStrict`, `Apply_ImportsAndMapsLevel`, `Apply_CleanOrphans_UsesRecycleBin` (fake bin),
  `Watcher_RaisesOncePerStableFile`, `ShellIntegration_WritesUnderTestKey` (registry root injectable).
- [ ] Implement; run filters → PASS.

### Task 21: CLI (lead)
**Files:** Create `src/Carbonado.Cli/{Program,CliOptions,CliRunner}.cs`; Tests `tests/Carbonado.Core.Tests/Cli/*` (reference Cli project).
**Interfaces:** `carbonado <paths…> [-o dir] [-p profile] [-f auto|jxl|avif|webp|jpeg|png] [-q score] [--lossless] [-r] [--effort fast|balanced|max|insane] [--json] [--dry-run] [--data-dir d]`; exit codes 0 ok, 1 some failed, 2 usage.
- [ ] Tests: `Cli_ParsesAllOptions`, `Cli_UsageErrorExit2`, `Cli_EndToEnd_CompressesFolderJson`.
- [ ] Implement; run → PASS.

### Task 22: End-to-end UI automation & budgets (lead)
**Files:** Create `tests/Carbonado.UITests/{AppSession,StartupTests,NavigationTests,CompressionFlowTests,ShutdownTests}.cs`.
- [ ] Tests: `Startup_FirstFrameUnderBudget` (Release R2R, warm, < 350 ms, from app's own profiler log), `Intro_PlaysAndHandsOff`, `Intro_SkipWithEsc`, `Navigate_AllPages_ByAutomationId`,
  `Compress_FilesFromArgs_ShowsResults` (outputs exist, rows show savings), `Close_WithRunningJobs_Under200ms_NoOrphans`.
- [ ] Run `dotnet test tests/Carbonado.UITests -c Release` → PASS.

### Task 23: Packaging & docs (lead)
**Files:** Create `build/publish.ps1` (Release, R2R, self-contained false, copies tools), `README.md`, `THIRD-PARTY-NOTICES.md`, app icon `src/Carbonado.App/Assets/Carbonado.ico` (rendered from DiamondMark at 16–256 px).
- [ ] `pwsh build/publish.ps1` → `artifacts/Carbonado/Carbonado.exe` runs; full `dotnet test` → all green; final visual review of live app screenshots.
