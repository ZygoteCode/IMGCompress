// Carbonado — animated film grain.
// Compile: fxc /T ps_2_0 /E main /Fo Grain.ps Grain.fx

sampler2D input : register(s0);
float time : register(c0);    // seconds; changes the noise pattern every frame
float amount : register(c1);  // grain strength 0..1 (0.06 is subtle)
float2 size : register(c2);   // element size in pixels (grain cell = 1 px)

float hash(float2 p)
{
    p = frac(p * float2(0.1031, 0.1030));
    p += dot(p, p.yx + 33.33);
    return frac((p.x + p.y) * p.x);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 c = tex2D(input, uv);
    float2 cell = floor(uv * size);
    float n = hash(cell + frac(time * 7.123) * 413.0) - 0.5;
    c.rgb = saturate(c.rgb + n * amount * c.a);
    return c;
}
