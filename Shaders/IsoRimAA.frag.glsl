#version 450

// IsoRim anti-aliasing (FXAA, after Timothy Lottes' simple version): the engine runs without MSAA,
// so jagged edges are softened in one full-screen pass over the picture drawn so far (scene grab
// IsoRimAA, queue 4890, before the colour grade). Only pixels on a contrast edge are changed: the
// edge direction is found from the luma of the four diagonal neighbours and the pixel is blended
// along it. The grab is read with texelFetch and filtered by hand, as its sampler state is unknown.
// The same pass also sharpens (Graphics tab option, _Sharpen > 0), with or without the FXAA part.

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    float _AAStrength; // @default = 1.0    0 = off, 1 = full
    float _Sharpen;    // @default = 0.0    sharpening (contrast-adaptive, after AMD CAS): 0 = off, 1 = strongest
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabIsoRimAA;

const float ReduceMin = 1.0 / 128.0;
const float ReduceMul = 1.0 / 8.0;
const float SpanMax = 8.0;
const float EdgeMin = 0.04;   // contrast below this is left alone (flat areas, textures)

vec3 Fetch(ivec2 p, ivec2 size)
{
    return texelFetch(_GrabIsoRimAA, clamp(p, ivec2(0), size - 1), 0).rgb;
}

// Bilinear sample at a position in pixels (pixel centres at .5).
vec3 Sample(vec2 pos, ivec2 size)
{
    vec2 f = pos - 0.5;
    ivec2 i = ivec2(floor(f));
    vec2 t = fract(f);
    vec3 a = mix(Fetch(i, size), Fetch(i + ivec2(1, 0), size), t.x);
    vec3 b = mix(Fetch(i + ivec2(0, 1), size), Fetch(i + ivec2(1, 1), size), t.x);
    return mix(a, b, t.y);
}

float Luma(vec3 c)
{
    return dot(c, vec3(0.299, 0.587, 0.114));
}

void main()
{
    ivec2 size = textureSize(_GrabIsoRimAA, 0);
    ivec2 ip = ivec2(gl_FragCoord.xy);
    vec2 pos = gl_FragCoord.xy;

    vec3 rgbM = Fetch(ip, size);
    float lumaM = Luma(rgbM);
    float lumaNW = Luma(Fetch(ip + ivec2(-1, -1), size));
    float lumaNE = Luma(Fetch(ip + ivec2(1, -1), size));
    float lumaSW = Luma(Fetch(ip + ivec2(-1, 1), size));
    float lumaSE = Luma(Fetch(ip + ivec2(1, 1), size));

    // Sharpening on the cross neighbours, scaled down where the local contrast is already high (no halos).
    // Pixels FXAA treats as an edge take the FXAA result instead, so edges are not made jagged again.
    vec3 base = rgbM;
    if (u._Sharpen > 0.0)
    {
        vec3 n = Fetch(ip + ivec2(0, -1), size);
        vec3 s = Fetch(ip + ivec2(0, 1), size);
        vec3 e = Fetch(ip + ivec2(1, 0), size);
        vec3 w = Fetch(ip + ivec2(-1, 0), size);
        vec3 mn = min(rgbM, min(min(n, s), min(e, w)));
        vec3 mx = max(rgbM, max(max(n, s), max(e, w)));
        vec3 amp = sqrt(clamp(min(mn, 1.0 - mx) / max(mx, vec3(1e-4)), 0.0, 1.0));
        // CAS uses -1/mix(8, 5, sharpness); here 0 = none and 1 = CAS at full (-1/5), about CAS's middle at 0.5.
        vec3 wgt = amp * (-0.2 * sqrt(clamp(u._Sharpen, 0.0, 1.0)));
        base = clamp((rgbM + (n + s + e + w) * wgt) / (1.0 + 4.0 * wgt), 0.0, 1.0);
    }

    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
    if (lumaMax - lumaMin < max(EdgeMin, lumaMax * 0.125) || u._AAStrength <= 0.0)
    {
        outColor = vec4(base, 1.0);
        return;
    }

    vec2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y = ((lumaNW + lumaSW) - (lumaNE + lumaSE));
    float reduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * ReduceMul), ReduceMin);
    float rcpMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + reduce);
    dir = clamp(dir * rcpMin, vec2(-SpanMax), vec2(SpanMax));

    vec3 rgbA = 0.5 * (Sample(pos + dir * (1.0 / 3.0 - 0.5), size) + Sample(pos + dir * (2.0 / 3.0 - 0.5), size));
    vec3 rgbB = rgbA * 0.5 + 0.25 * (Sample(pos + dir * -0.5, size) + Sample(pos + dir * 0.5, size));
    float lumaB = Luma(rgbB);
    vec3 aa = (lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB;
    outColor = vec4(mix(base, aa, clamp(u._AAStrength, 0.0, 1.0)), 1.0);
}
