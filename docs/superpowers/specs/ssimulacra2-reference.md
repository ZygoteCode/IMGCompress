# SSIMULACRA2 — reference parameters for the C# port

Source: libjxl (BSD-3-Clause) `tools/ssimulacra2.cc`, `tools/ssimulacra2_main.cc`, `tools/gauss_blur.cc`,
`lib/jxl/enc_xyb.cc`, `lib/jxl/cms/opsin_params.h` (main branch, read 2026-11-21). Algorithm by Jon Sneyers
(Cloudinary). Carbonado reimplements it in C#; attribution goes in THIRD-PARTY-NOTICES.md.
Ground truth for tests: `tools/libjxl/ssimulacra2.exe` (libjxl v0.13.0), output format `%.8f`.

## Pipeline
1. Inputs must be same size, ≥ 8×8.
2. Alpha (only if the image has alpha): blend on the *encoded* (non-linear sRGB, 0..1) values:
   `c = a*c + (1-a)*bg`, run twice with bg = 0.1 and bg = 0.9; final score = min of the two scores.
3. Convert encoded sRGB → linear sRGB. For sRGB/untagged input libjxl does NOT use the exact EOTF or skcms: it applies
   `TF_SRGB::DisplayFromEncoded`, a rational polynomial (x ≤ 0.04045 → x/12.92, else P(x)/Q(x) with
   p = {2.200248328e-4, 1.043637593e-2, 1.624820318e-1, 7.961564959e-1, 8.210152774e-1},
   q = {2.631846970e-1, 1.076976492, 4.987528350e-1, −5.512498495e-2, 6.521209011e-3}, FMA Horner, true divide).
   8-bit samples decode as `v * (1.0f/255)`. Non-sRGB ICC → linear sRGB via the CMS (Carbonado: matrix/TRC parser).
4. For scale s = 0..5 (kNumScales = 6):
   - the "< 8 → stop" check is applied to the PREVIOUS scale before downsampling (an 8×8 image has 2 scales: 8×8, 4×4).
   - if s > 0: downsample the LINEAR images 2×2 box: out = ceil(in/2); sum of the 2×2 block with
     coordinates clamped to the last row/column, × 0.25.
   - XYB from linear (below) + MakePositiveXYB, for both images.
   - mu1 = blur(img1), mu2 = blur(img2), s11 = blur(img1²), s22 = blur(img2²), s12 = blur(img1·img2) — per plane.
   - SSIMMap → avg_ssim[6]; EdgeDiffMap → avg_edgediff[12].
5. Score() (below).

## XYB
Opsin matrix (row-major, multiplied by intensity_target/255 = 1 for SDR):
```
m00 = 0.30                  m01 = 0.622                 m02 = 0.078
m10 = 0.23                  m11 = 0.692                 m12 = 0.078
m20 = 0.24342268924547819   m21 = 0.20476744424496821   m22 = 1 - m20 - m21 (= 0.55180986650955360)
bias = 0.0037930732552754493 (all three)
```
`mixed_k = max(0, row_k · (r,g,b) + bias)`; `mixed_k = cbrt(mixed_k) - cbrt(bias)`;
`X = 0.5*(mixed0 - mixed1)`, `Y = 0.5*(mixed0 + mixed1)`, `B = mixed2`.
MakePositiveXYB: `B' = (B - Y) + 0.55`, `X' = X*14 + 0.42`, `Y' = Y + 0.01` (B uses the original Y).

## SSIMMap (per plane c, N = w*h)
`kC2 = 0.0009f`. Per pixel: `num_m = 1 - (mu1-mu2)²`; `num_s = 2*(s12 - mu1*mu2) + kC2`;
`denom_s = (s11 - mu1²) + (s22 - mu2²) + kC2`; `d = max(0, 1 - num_m*num_s/denom_s)` (double).
`avg_ssim[c*2+0] = Σd / N`; `avg_ssim[c*2+1] = (Σd⁴ / N)^(1/4)`.

## EdgeDiffMap (per plane c)
`d1 = (1 + |img2 - mu2|) / (1 + |img1 - mu1|) - 1` (double); `artifact = max(d1,0)`; `lost = max(-d1,0)`.
`[c*4+0] = mean(artifact)`, `[c*4+1] = mean(artifact⁴)^(1/4)`, `[c*4+2] = mean(lost)`, `[c*4+3] = mean(lost⁴)^(1/4)`.

## Score
Note: the loop runs over the scales actually computed (`scale < scales.size()`); with fewer than 6 scales the weight
index is shifted accordingly — missing scales do not simply contribute 0.
```
i = 0; ssim = 0
for c in 0..2: for scale in 0..5: for n in 0..1:
    ssim += w[i++] * |avg_ssim[c*2+n]|
    ssim += w[i++] * |avg_edgediff[c*4+n]|
    ssim += w[i++] * |avg_edgediff[c*4+n+2]|
ssim *= 0.9562382616834844
ssim = 2.326765642916932*ssim - 0.020884521182843837*ssim² + 6.248496625763138e-05*ssim³
score = ssim > 0 ? 100 - 10*ssim^0.6276336467831387 : 100
```
Weights w[0..107]:
```
0.0, 0.0007376606707406586, 0.0, 0.0, 0.0007793481682867309, 0.0, 0.0, 0.0004371155730107379, 0.0,
1.1041726426657346, 0.00066284834129271, 0.00015231632783718752, 0.0, 0.0016406437456599754, 0.0,
1.8422455520539298, 11.441172603757666, 0.0, 0.0007989109436015163, 0.000176816438078653, 0.0,
1.8787594979546387, 10.94906990605142, 0.0, 0.0007289346991508072, 0.9677937080626833, 0.0,
0.00014003424285435884, 0.9981766977854967, 0.00031949755934435053, 0.0004550992113792063, 0.0, 0.0,
0.0013648766163243398, 0.0, 0.0, 0.0, 0.0, 0.0, 7.466890328078848, 0.0, 17.445833984131262,
0.0006235601634041466, 0.0, 0.0, 6.683678146179332, 0.00037724407979611296, 1.027889937768264,
225.20515300849274, 0.0, 0.0, 19.213238186143016, 0.0011401524586618361, 0.001237755635509985,
176.39317598450694, 0.0, 0.0, 24.43300999870476, 0.28520802612117757, 0.0004485436923833408, 0.0, 0.0,
0.0, 34.77906344483772, 44.835625328877896, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0,
0.0008680556573291698, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0005313191874358747, 0.0, 0.00016533814161379112, 0.0,
0.0, 0.0, 0.0, 0.0, 0.0004179171803251336, 0.0017290828234722833, 0.0, 0.0020827005846636437, 0.0, 0.0,
8.826982764996862, 23.19243343998926, 0.0, 95.1080498811086, 0.9863978034400682, 0.9834382792465353,
0.0012286405048278493, 171.2667255897307, 0.9807858872435379, 0.0, 0.0, 0.0, 0.0005130064588990679, 0.0,
0.00010854057858411537
```

## Blur — recursive Gaussian (Charalampidis 2016), σ = 1.5, separable (rows then columns), zero padding
Setup (double):
```
N = round(3.2795*σ + 0.2546)            // = 5 for σ = 1.5
w = π/(2N); omega = {w, 3w, 5w}
p1 = +1/tan(omega0/2); p3 = -1/tan(omega1/2); p5 = +1/tan(omega2/2)
r1 = +p1²/sin(omega0); r3 = -p3²/sin(omega1); r5 = +p5²/sin(omega2)
rho_i = exp(-0.5*σ²*omega_i²) / N
D13 = p1*r3 - r1*p3; D35 = p3*r5 - r3*p5; D51 = p5*r1 - r5*p1
zeta15 = D35/D13; zeta35 = D51/D13
A = [[p1,p3,p5],[r1,r3,r5],[zeta15,zeta35,1]]; gamma = [1, N² - σ², zeta15*rho0 + zeta35*rho1 + rho2]
beta = inverse(A) * gamma                // check: beta·(p1,p3,p5) == 1
n2_i = -beta_i * cos(omega_i*(N+1)); d1_i = -2*cos(omega_i)          // stored as float
```
1-D pass over a line `in[0..len)` (states prev/prev2 per i start at 0):
```
for n = -N+1 .. len-1:
    sum = (n-N-1 >= 0 ? in[n-N-1] : 0) + (n+N-1 < len ? in[n+N-1] : 0)
    for i in 0..2: y_i = n2_i*sum - d1_i*prev_i - prev2_i; prev2_i = prev_i; prev_i = y_i
    if n >= 0: out[n] = y_0 + y_1 + y_2
```
Vertical pass = the same 1-D filter along columns of the horizontal result.

Precision note: the float IIR drift is part of the reference scores (an exact Gaussian scores up to +2.7 higher at
8 MP). A bit-exact port must reproduce libjxl's 4-lane unrolled horizontal formulation (mul_in/mul_prev/mul_prev2)
and the vertical `MulAdd(n2, sum, NegMulSub(d1, y1, y2))` in float. AlphaBlend and MakePositiveXYB are uncontracted
float operations (no FMA).

Validation (Carbonado port, 29 pairs vs ssimulacra2.exe v0.13.0): sRGB pairs |Δ| ≤ 5e-9 (bit-exact), Display-P3 ICC
pair |Δ| = 7.2e-3 (own matrix/TRC CMS vs skcms).
