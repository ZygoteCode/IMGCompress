// Carbonado — radial chromatic aberration (prismatic dispersion).
// Compile: fxc /T ps_2_0 /E main /Fo ChromaticAberration.ps ChromaticAberration.fx
// Colors are premultiplied (WPF convention).

sampler2D input : register(s0);
float amount : register(c0);   // 0..1; 1 = max dispersion (2% of the distance from center)
float2 center : register(c1);  // dispersion origin in UV space

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 offset = (uv - center) * amount * 0.02;
    float4 c = tex2D(input, uv);
    float4 cr = tex2D(input, uv + offset);
    float4 cb = tex2D(input, uv - offset);
    float a = max(c.a, max(cr.a, cb.a));
    return float4(cr.r, c.g, cb.b, a);
}
