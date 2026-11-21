# Third-party notices

Carbonado is free software released under the GNU General Public License v3.0 (see `LICENSE`).
It bundles or derives from the following third-party components. Each remains under its own license.

## Encoders and decoders (separate executables in `tools/`, run as child processes)

| Component | Version | License | Copyright |
|---|---|---|---|
| libjxl (cjxl, djxl, jxlinfo, ssimulacra2) | 0.13.0 | BSD-3-Clause | © the JPEG XL Project Authors |
| libavif (avifenc, avifdec) | 1.4.2 | BSD-2-Clause | © Joe Drago and the libavif authors |
| libaom (inside avifenc) | 3.14.1 | BSD-2-Clause + AOMedia Patent License 1.0 | © Alliance for Open Media |
| dav1d (inside avifdec) | 1.5.3 | BSD-2-Clause | © VideoLAN and dav1d authors |
| libwebp (cwebp, dwebp, gif2webp) | 1.6.0 | BSD-3-Clause | © Google Inc. |
| oxipng | 10.2.1 | MIT | © Joshua Holmer and oxipng contributors |
| Efficient Compression Tool (ect) | 0.9.5 | Apache-2.0 | © Felix Hanau |
| pngquant | 2.17.0 | GPL-3.0-or-later | © Kornel Lesiński and contributors |
| FFmpeg (gyan.dev full build) | 2025-11-06 | GPL-3.0-or-later (this build) | © the FFmpeg developers |

The libjxl static build also carries notices for brotli (MIT), giflib (MIT), Highway (Apache-2.0/BSD-3-Clause),
libjpeg-turbo (IJG/BSD-3-Clause/zlib), libpng (libpng license), sjpeg (Apache-2.0), skcms (BSD-3-Clause) and
zlib (zlib license); the full texts are in `tools/libjxl/licenses/`.

Exact archive and file SHA-256 hashes of every binary are pinned in `tools/manifest.json`; `build/fetch-tools.ps1`
downloads them from the official release pages and verifies them.

## Algorithms

- **SSIMULACRA2** perceptual metric — Carbonado contains an independent C# implementation of the published
  algorithm and its parameters (weights, XYB transform, recursive Gaussian by Charalampidis 2016) as found in
  libjxl `tools/ssimulacra2.cc`, `tools/gauss_blur.cc`, `lib/jxl/cms/transfer_functions` and `lib/jxl/cms/opsin_params.h`
  (BSD-3-Clause, © the JPEG XL Project Authors; SSIMULACRA2 designed by Jon Sneyers, Cloudinary). The port in
  `src/Carbonado.Core/Metrics/` reproduces libjxl constants (the 108 weights, the TF_SRGB rational-polynomial
  coefficients, the opsin absorbance matrix/bias and cube-root constants, the recursive-Gaussian derivation) and is
  distributed under the BSD-3-Clause terms of libjxl for those parts
  (full text from `tools/libjxl/licenses/LICENSE.libjxl`):

```text
Copyright (c) the JPEG XL Project Authors.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Fonts

- **Inter** (Regular, Medium, SemiBold) — SIL Open Font License 1.1, © 2020 The Inter Project Authors
  (https://github.com/rsms/inter). License text: `src/Carbonado.UI/Assets/Fonts/OFL.txt`.

## Libraries

- **CommunityToolkit.Mvvm** 8.4.2 — MIT, © .NET Foundation and Contributors.
- **.NET / WPF** — MIT, © .NET Foundation and Contributors.
- Test-only: **xUnit.net v3** (Apache-2.0), **FlaUI** (MIT).
