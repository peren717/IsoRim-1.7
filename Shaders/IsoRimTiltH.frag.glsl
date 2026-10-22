#version 450

// Tilt-shift (TiltShift.cs), pass H of 2 (scene grab TiltA, queue 4880, before FXAA).
// A Gaussian blur along x whose sigma depends on the screen row only: zero in a horizontal band,
// growing smoothly above and below it. 15 taps at a spacing that scales with sigma (constant weights),
// each a hand-made linear sample, so the blur changes continuously with the row (no levels, no steps).

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    float _TiltSigma;  // @default = 0.0    sigma in px at full blur, for 1080 lines (scaled by the height)
    float _TiltCentre; // @default = 0.5    band centre, fraction of the height from the top
    float _TiltHalf;   // @default = 0.15   band half width, fraction of the height
    float _TiltRamp;   // @default = 0.3    band edge to full blur, fraction of the height
    float _TiltPop;    // @default = 0.0    saturation and contrast lift (second pass only)
    float _TiltDebug;  // @default = 0.0    1 = tint by blur amount (second pass only)
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabTiltA;

const int K = 7;
// exp(-0.5 * (3 i / K)^2), i = 1..K: taps out to 3 sigma.
const float W[K] = float[K](0.9123, 0.6926, 0.4376, 0.2301, 0.1007, 0.0367, 0.0111);

vec3 Fetch(ivec2 p, ivec2 size)
{
    return texelFetch(_GrabTiltA, clamp(p, ivec2(0), size - 1), 0).rgb;
}

float Amount(float y)
{
    float t = clamp((abs(y - u._TiltCentre) - u._TiltHalf) / max(u._TiltRamp, 1e-3), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

void main()
{
    ivec2 size = textureSize(_GrabTiltA, 0);
    ivec2 ip = ivec2(gl_FragCoord.xy);
    vec3 c = Fetch(ip, size);
    float amount = Amount(gl_FragCoord.y / float(size.y));
    float sigma = u._TiltSigma * float(size.y) / 1080.0 * amount;
    if (sigma < 0.25)
    {
        outColor = vec4(c, 1.0);
        return;
    }
    float stepPx = 3.0 * sigma / float(K);
    vec3 sum = c;
    float wsum = 1.0;
    for (int i = 1; i <= K; i++)
    {
        float d = stepPx * float(i);
        int n = int(floor(d));
        float t = d - float(n);
        vec3 r = mix(Fetch(ip + ivec2(n, 0), size), Fetch(ip + ivec2(n + 1, 0), size), t);
        vec3 l = mix(Fetch(ip - ivec2(n, 0), size), Fetch(ip - ivec2(n + 1, 0), size), t);
        sum += (r + l) * W[i - 1];
        wsum += 2.0 * W[i - 1];
    }
    outColor = vec4(sum / wsum, 1.0);
}
