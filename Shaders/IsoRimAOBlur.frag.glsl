#version 450

// IsoRim ambient occlusion, second pass (AmbientOcclusion.cs; scene grab IsoRimAOBlur, queue 2451, right after
// IsoRimAO). The first pass left 1 - occlusion in the alpha channel, with its sample directions turned per pixel in
// a 4x4 pattern; averaging over the same 4x4 block removes the pattern. Only pixels at about the same view depth as
// this one are averaged (no bleeding across silhouettes). Then strength and steps (smooth, or 2 or 3 flat levels
// with anti-aliased edges) are applied, the colour is multiplied and the alpha is put back to 1.

layout(location = 0) out vec4 outColor;

// The same block as IsoRimAO.frag.glsl (both materials get the same values).
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _AOProj;   // @default = vec4(0.1, 0.1, 0.01, 0.5)   projection m00, m11, m22, m23
    vec4 _AOProjB;  // @default = vec4(0.0, 0.0, 0.0, 0.0)    m03, m13, perspective (1), ground receives
    vec4 _AOPlane;  // @default = vec4(0.0, 1.0, 0.0, 0.0)    ground plane in camera space: n.xyz, c (n.p + c = 0)
    vec4 _AOParams; // @default = vec4(0.6, 0.45, 0.0, 0.0)   strength, radius (cells), steps (0 smooth, 2, 3), debug
    vec4 _AOTune;   // @default = vec4(3.0, 0.1, 96.0, 0.0)   gain, angle bias (sine), largest radius (px), unused
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabIsoRimAOBlur;
layout(set = 2, binding = 2) uniform sampler2D _IsoSceneDepth;

// View distance from depth; nothing drawn (ground or sky) = one far value, so the ground blurs with itself.
float ViewZ(float d)
{
    if (d <= 0.0)
    {
        return 1e6;
    }
    if (u._AOProjB.z > 0.5)
    {
        return u._AOProj.w / (d + u._AOProj.z);
    }
    return -(d - u._AOProj.w) / u._AOProj.z;
}

float AAStep(float t, float x, float w)
{
    return smoothstep(t - w, t + w, x);
}

void main()
{
    ivec2 size = textureSize(_GrabIsoRimAOBlur, 0);
    ivec2 dsize = textureSize(_IsoSceneDepth, 0);
    ivec2 ip = ivec2(gl_FragCoord.xy);
    vec4 col = texelFetch(_GrabIsoRimAOBlur, ip, 0);
    int debug = int(u._AOParams.w + 0.5);
    if (debug >= 2)
    {
        outColor = vec4(col.rgb, 1.0);
        return;
    }

    float zc = ViewZ(texelFetch(_IsoSceneDepth, clamp(ip, ivec2(0), dsize - 1), 0).r);
    float tol = max(0.12, zc * 0.01);
    float sum = 0.0, wsum = 0.0;
    for (int y = -2; y <= 1; y++)
    {
        for (int x = -2; x <= 1; x++)
        {
            ivec2 q = clamp(ip + ivec2(x, y), ivec2(0), min(size, dsize) - 1);
            float z = ViewZ(texelFetch(_IsoSceneDepth, q, 0).r);
            float w = abs(z - zc) < tol ? 1.0 : 0.0;
            sum += (1.0 - texelFetch(_GrabIsoRimAOBlur, q, 0).a) * w;
            wsum += w;
        }
    }
    float occ = wsum > 0.0 ? sum / wsum : 1.0 - col.a;

    float strength = u._AOParams.x;
    int steps = int(u._AOParams.z + 0.5);
    float dark;
    if (steps == 2)
    {
        float w = clamp(fwidth(occ) * 0.75, 0.02, 0.2);
        dark = 0.7 * AAStep(0.3, occ, w);
    }
    else if (steps == 3)
    {
        float w = clamp(fwidth(occ) * 0.75, 0.02, 0.2);
        dark = 0.45 * AAStep(0.2, occ, w) + 0.45 * AAStep(0.5, occ, w);
    }
    else
    {
        dark = occ;
    }
    float ao = 1.0 - strength * dark;
    if (debug == 1)
    {
        outColor = vec4(vec3(ao), 1.0);
        return;
    }
    outColor = vec4(col.rgb * ao, 1.0);
}
