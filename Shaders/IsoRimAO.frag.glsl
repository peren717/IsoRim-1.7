#version 450

// IsoRim ambient occlusion (AmbientOcclusion.cs): one full-screen pass over the map drawn so far (scene grab IsoRimAO,
// queue 2450), reading the scene depth copied at the same grab (_IsoSceneDepth, reverse-Z: 1 near, 0 nothing drawn).
//
// Positions are rebuilt in camera space from depth and the camera's projection (ortho or perspective). The ground
// (terrain and floors write no depth) is the plane y = 0 put under every pixel: where the scene depth is behind it or
// empty, the pixel is ground. Horizon AO: in 8 directions the highest point within the radius (cells, camera space,
// so the same at every zoom) seen above the pixel's tangent plane darkens it. Halos at depth jumps are stopped by a
// 3D distance falloff (a sample further away than the radius counts for nothing), so a wall top in front of the ground
// far behind it, a tree card or a sprite does not darken what it only overlaps on screen. The ground receives
// _AOProjB.w of the AO (GroundShadows already darkens it along walls). The directions are turned per pixel in a fixed
// 4x4 pattern; this pass only stores 1 - occlusion in the alpha channel (1 everywhere until now), and the next pass
// (IsoRimAOBlur, queue 2451) averages it over the same 4x4 block, depth-aware, applies strength and steps, and
// multiplies the colour.

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _AOProj;   // @default = vec4(0.1, 0.1, 0.01, 0.5)   projection m00, m11, m22, m23
    vec4 _AOProjB;  // @default = vec4(0.0, 0.0, 0.0, 0.0)    m03, m13, perspective (1), ground receives
    vec4 _AOPlane;  // @default = vec4(0.0, 1.0, 0.0, 0.0)    ground plane in camera space: n.xyz, c (n.p + c = 0)
    vec4 _AOParams; // @default = vec4(0.6, 0.45, 0.0, 0.0)   strength, radius (cells), steps (0 smooth, 2, 3), debug
    vec4 _AOTune;   // @default = vec4(3.0, 0.1, 96.0, 0.0)   gain, angle bias (sine), largest radius (px), unused
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabIsoRimAO;
layout(set = 2, binding = 2) uniform sampler2D _IsoSceneDepth;

const int Dirs = 8;
const int Steps = 4;
const float GroundEps = 0.03;   // cells: scene points this close above the plane still count as ground (flat sprites, floor edges)

ivec2 gSize;
bool gPersp;

// Camera-space position at pixel q; ground = the plane was used.
vec3 PosAt(ivec2 q, out bool ground)
{
    q = clamp(q, ivec2(0), gSize - 1);
    float d = texelFetch(_IsoSceneDepth, q, 0).r;
    vec2 ndc = (vec2(q) + 0.5) / vec2(gSize) * 2.0 - 1.0;
    vec3 o, dir, ps;
    bool has = d > 0.0;
    if (gPersp)
    {
        o = vec3(0.0);
        dir = vec3(ndc.x / u._AOProj.x, ndc.y / u._AOProj.y, -1.0);
        float zv = has ? -u._AOProj.w / (d + u._AOProj.z) : -1e6;
        ps = dir * -zv;
    }
    else
    {
        o = vec3((ndc.x - u._AOProjB.x) / u._AOProj.x, (ndc.y - u._AOProjB.y) / u._AOProj.y, 0.0);
        dir = vec3(0.0, 0.0, -1.0);
        float zv = has ? (d - u._AOProj.w) / u._AOProj.z : -1e6;
        ps = vec3(o.xy, zv);
    }
    float dn = dot(u._AOPlane.xyz, dir);
    ground = false;
    if (dn < -1e-5)
    {
        float t = -(dot(u._AOPlane.xyz, o) + u._AOPlane.w) / dn;
        if (t > 0.0)
        {
            vec3 pg = o + dir * t;
            // camera looks down -z: nearer = larger z
            if (ps.z <= pg.z + GroundEps)
            {
                ground = true;
                return pg;
            }
        }
    }
    return ps;
}

vec3 PosAt(ivec2 q)
{
    bool g;
    return PosAt(q, g);
}

float AAStep(float t, float x, float w)
{
    return smoothstep(t - w, t + w, x);
}

void main()
{
    gSize = textureSize(_IsoSceneDepth, 0);
    gPersp = u._AOProjB.z > 0.5;
    ivec2 ip = ivec2(gl_FragCoord.xy);
    vec4 col = texelFetch(_GrabIsoRimAO, ip, 0);
    int debug = int(u._AOParams.w + 0.5);
    if (debug == 4)
    {
        outColor = vec4(col.a < 0.999 ? vec3(1.0, 0.0, col.a) : vec3(0.0, 1.0, 0.0), 1.0);
        return;
    }

    bool ground;
    vec3 P = PosAt(ip, ground);
    float depth = texelFetch(_IsoSceneDepth, clamp(ip, ivec2(0), gSize - 1), 0).r;
    if (!ground && depth <= 0.0)
    {
        // Nothing drawn and no ground under it (sky, void past the map).
        outColor = debug == 2 ? vec4(0.0, 0.0, 0.35, 1.0) : vec4(col.rgb, 1.0);
        return;
    }

    // Normal: the plane's for ground, else from the nearer-in-depth neighbour on each axis (no smearing across edges).
    vec3 N;
    if (ground)
    {
        N = u._AOPlane.xyz;
    }
    else
    {
        vec3 l = PosAt(ip + ivec2(-1, 0)), r = PosAt(ip + ivec2(1, 0));
        vec3 b = PosAt(ip + ivec2(0, -1)), t = PosAt(ip + ivec2(0, 1));
        vec3 dx = abs(r.z - P.z) < abs(P.z - l.z) ? r - P : P - l;
        vec3 dy = abs(t.z - P.z) < abs(P.z - b.z) ? t - P : P - b;
        N = normalize(cross(dx, dy));
        vec3 toCam = gPersp ? -P : vec3(0.0, 0.0, 1.0);
        if (dot(N, toCam) < 0.0)
        {
            N = -N;
        }
    }

    if (debug == 2)
    {
        outColor = ground ? vec4(0.15, 0.6, 0.15, 1.0) : vec4(vec3(clamp(depth, 0.0, 1.0)), 1.0);
        return;
    }
    if (debug == 3)
    {
        outColor = vec4(N * 0.5 + 0.5, 1.0);
        return;
    }

    float radius = u._AOParams.y;
    float viewZ = max(-P.z, 1e-3);
    float pxPerCell = abs(u._AOProj.y) * float(gSize.y) * 0.5 / (gPersp ? viewZ : 1.0);
    float rPx = min(radius * pxPerCell, u._AOTune.z);
    float occ = 0.0;
    if (rPx >= 1.5)
    {
        float r2 = radius * radius;
        // A fixed 4x4 pattern turns the directions per pixel (no noise that would need a blur).
        int k = (ip.x & 3) + ((ip.y & 3) << 2);
        const int bayer[16] = int[16](0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
        float jit = (float(bayer[k]) + 0.5) / 16.0;
        float jit2 = (float(bayer[(k * 5 + 7) & 15]) + 0.5) / 16.0;   // step offset, another order of the same pattern
        for (int i = 0; i < Dirs; i++)
        {
            float a = (float(i) + jit) * (6.2831853 / float(Dirs));
            vec2 dir = vec2(cos(a), sin(a));
            float best = 0.0;
            for (int s = 0; s < Steps; s++)
            {
                float f = (float(s) + jit2) / float(Steps);
                vec2 off = dir * max(1.0, f * rPx);
                vec3 Q = PosAt(ip + ivec2(round(off)));
                vec3 v = Q - P;
                float l2 = dot(v, v);
                if (l2 < 1e-6)
                {
                    continue;
                }
                float fall = clamp(1.0 - l2 / r2, 0.0, 1.0);
                float sinE = dot(v, N) * inversesqrt(l2);
                best = max(best, (sinE - u._AOTune.y) * fall);
            }
            occ += best;
        }
        occ = clamp(occ / float(Dirs) * u._AOTune.x, 0.0, 1.0);
    }
    if (ground)
    {
        occ *= u._AOProjB.w;
    }

    // The scene's alpha is 1 everywhere at this point; the blur pass reads the occlusion from it and puts 1 back.
    outColor = vec4(col.rgb, 1.0 - occ);
}
