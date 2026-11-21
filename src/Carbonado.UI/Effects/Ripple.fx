// Carbonado — expanding shockwave ring distortion with a light rim.
// Compile: fxc /T ps_2_0 /E main /Fo Ripple.ps Ripple.fx

sampler2D input : register(s0);
float2 center : register(c0);    // ring center (UV)
float radius : register(c1);     // ring radius (UV units, horizontal)
float amplitude : register(c2);  // max displacement (UV units), e.g. 0.02
float thickness : register(c3);  // ring half-width (UV units), e.g. 0.06
float aspect : register(c4);     // element width / height
float highlight : register(c5);  // rim brightening 0..1

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 d = uv - center;
    d.y /= aspect;
    float dist = length(d) + 0.00001;
    float diff = dist - radius;
    float band = saturate(1 - abs(diff) / max(thickness, 0.0001));
    float wave = sin(band * 3.14159265);
    float2 dir = d / dist;
    dir.y *= aspect;
    float2 uv2 = uv - dir * amplitude * wave;
    float4 c = tex2D(input, uv2);
    c.rgb += wave * highlight * c.a * 0.35;
    return c;
}
