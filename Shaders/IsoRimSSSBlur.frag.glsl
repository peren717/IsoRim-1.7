#version 450

// IsoRim screen-space sun shadows, second pass (SunShadowsSS.cs; scene grab IsoRimSSSBlur, queue 2453).
//
// Upsampling: the first pass marched only the samples (both pixel coordinates even) and left 0.98 * (1 - shadow) in their
// alpha (1 = not a receiver). Each pixel takes the samples of a 4 x 4 grid of blocks around it (8 x 8 pixels; the march
// starts are turned per block in a 4 x 4 pattern, so the grid averages the pattern out), weighted by distance and kept
// only when the sample is a receiver at about this pixel's view depth: no bleeding across silhouettes, and pixels that
// were not receivers themselves (a pawn's feet, counted as ground by the first pass) take their neighbours' value
// instead of a light fringe. Pixels with no depth (ground, floors) are never darkened.
//
// AO: when the AO passes ran this frame (_SSProjB.w = 1), AO's second pass left its factor in the alpha of every pixel;
// the other pixels of each block still carry it (the first pass only rewrote the samples). Where both apply the darker
// one is used, not the product: colour / ao * min(ao, sun shadow).
// The colour is multiplied toward the game's sun shadow colour (_SSColor) by strength; the alpha is put back to 1.

layout(location = 0) out vec4 outColor;

// The same block as IsoRimSSS.frag.glsl (both materials get the same values).
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _SSProj;   // @default = vec4(0.1, 0.1, 0.01, 0.5)
    vec4 _SSProjB;  // @default = vec4(0.0, 0.0, 0.0, 0.0)
    vec4 _SSPlane;  // @default = vec4(0.0, 1.0, 0.0, 0.0)
    vec4 _SSLight;  // @default = vec4(0.0, 1.0, 0.0, 0.0)
    vec4 _SSParams; // @default = vec4(1.0, 40.0, 1.0, 0.0)
    vec4 _SSTune;   // @default = vec4(3.0, 0.5, 24.0, 0.05)
    vec4 _SSColor;  // @default = vec4(0.5, 0.5, 0.6, 1.0)
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabIsoRimSSSBlur;
layout(set = 2, binding = 2) uniform sampler2D _IsoSceneDepth;

const float Lit = 0.98;

float ViewZ(float d)
{
    if (d <= 0.0)
    {
        return 1e6;
    }
    if (u._SSProjB.z > 0.5)
    {
        return u._SSProj.w / (d + u._SSProj.z);
    }
    return -(d - u._SSProj.w) / u._SSProj.z;
}

void main()
{
    ivec2 size = min(textureSize(_GrabIsoRimSSSBlur, 0), textureSize(_IsoSceneDepth, 0));
    ivec2 ip = ivec2(gl_FragCoord.xy);
    vec4 col = texelFetch(_GrabIsoRimSSSBlur, ip, 0);
    int debug = int(u._SSParams.w + 0.5);
    if (debug == 4)
    {
        // raw first-pass value at the samples (grey 0.5 = not a receiver), the AO factor elsewhere
        bool sample0 = ((ip.x | ip.y) & 1) == 0;
        outColor = vec4(sample0 ? vec3(col.a > 0.99 ? 0.5 : col.a / Lit) : vec3(0.0, col.a, 0.0), 1.0);
        return;
    }
    if (debug >= 2)
    {
        outColor = vec4(col.rgb, 1.0);
        return;
    }

    float zc = ViewZ(texelFetch(_IsoSceneDepth, clamp(ip, ivec2(0), size - 1), 0).r);
    float sh = 0.0;
    if (zc < 1e5)
    {
        float tol = max(0.15, zc * 0.01);
        ivec2 base = (ip >> 1) << 1;
        vec2 c = vec2(ip) + 0.5;
        float sum = 0.0, wsum = 0.0;
        for (int y = -1; y <= 2; y++)
        {
            for (int x = -1; x <= 2; x++)
            {
                ivec2 q = clamp(base + ivec2(x, y) * 2, ivec2(0), size - 1);
                float a = texelFetch(_GrabIsoRimSSSBlur, q, 0).a;
                if (a > 0.99)
                {
                    continue;   // not a receiver
                }
                float z = ViewZ(texelFetch(_IsoSceneDepth, q, 0).r);
                float dz = abs(z - zc);
                if (dz >= tol)
                {
                    continue;
                }
                vec2 dd = (vec2(q) + 0.5 - c) / 3.0;
                float w = exp(-dot(dd, dd)) * (1.0 - dz / tol);
                sum += (1.0 - a / Lit) * w;
                wsum += w;
            }
        }
        sh = wsum > 1e-4 ? clamp(sum / wsum, 0.0, 1.0) : 0.0;
    }

    float k = clamp(sh * u._SSParams.x, 0.0, 2.0);
    vec3 mul = max(mix(vec3(1.0), u._SSColor.rgb, k * u._SSColor.a), vec3(0.0));
    if (debug == 1)
    {
        outColor = vec4(mul, 1.0);
        return;
    }
    if (u._SSProjB.w > 0.5 && sh > 0.0)
    {
        // AO factor of this pixel (a sample pixel carries the shadow, so take a neighbour's: AO is smooth)
        bool sample0 = ((ip.x | ip.y) & 1) == 0;
        float ao = sample0 ? texelFetch(_GrabIsoRimSSSBlur, clamp(ip + ivec2(1, 0), ivec2(0), size - 1), 0).a : col.a;
        ao = clamp(ao, 0.05, 1.0);
        mul = min(vec3(ao), mul) / ao;
    }
    outColor = vec4(col.rgb * mul, 1.0);
}
