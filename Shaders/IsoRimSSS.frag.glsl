#version 450

// IsoRim screen-space sun shadows (SunShadowsSS.cs), first pass over the map drawn so far (scene grab IsoRimSSS, queue
// 2452, right after the AO passes), reading the scene depth (_IsoSceneDepth, DepthGrab; reverse-Z, 0 = nothing drawn).
//
// Half resolution: only one pixel of each 2 x 2 block (both coordinates even, the block's "sample") works; the others
// pass the colour and alpha through. A sample above the ground plane is rebuilt in camera space (as IsoRimAO) and a ray is
// marched from it toward the sun (_SSLight: the direction that matches the game's own sun shadows on the ground: one
// cast vector per wall height). The march runs until the ray is higher than the tallest caster (_SSTune.x: walls, trees,
// the top shown ProjectZ floor), at most _SSParams.y cells, in _SSTune.z steps placed closer together near the start.
// A step whose scene point is in front of the ray point by less than the thickness plus the step's own depth span is a
// candidate; a short search between it and the step before finds where the ray went behind the surface, and that point
// must be within the thickness (coarse to fine: long rays at a low sun without skipping through thin things or
// darkening far behind them). A ray that leaves the screen finds nothing; near the screen edges shadows fade out
// (_SSTune.w = fade width, a fraction of the screen), so casters just off screen do not pop.
// The ground (terrain, floors, things flat on it) receives as well: while this pass draws, the game's own sun shadows
// and IsoRim's tree shadows are left out (SunShadowsSS.ReplacesGround), IsoRim's soft ground shadows stay.
//
// Stored in the alpha channel of the sample: 0.98 * (1 - shadow) for a receiver, 1 for a non-receiver (nothing drawn and
// no ground under it, or not a sample). IsoRimSSSBlur fills every pixel from the nearby samples at its own depth and darkens the colour.

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _SSProj;   // @default = vec4(0.1, 0.1, 0.01, 0.5)   projection m00, m11, m22, m23
    vec4 _SSProjB;  // @default = vec4(0.0, 0.0, 0.0, 0.0)    m03, m13, perspective (1), AO drawn this frame (1)
    vec4 _SSPlane;  // @default = vec4(0.0, 1.0, 0.0, 0.0)    ground plane in camera space: n.xyz, c
    vec4 _SSLight;  // @default = vec4(0.0, 1.0, 0.0, 0.0)    toward the sun in camera space, xyz; w unused
    vec4 _SSParams; // @default = vec4(1.0, 40.0, 1.0, 0.0)   strength, longest march (cells), thickness (cells), debug
    vec4 _SSTune;   // @default = vec4(3.0, 0.5, 24.0, 0.05)  tallest caster (cells), sun height (sine), steps, edge fade
    vec4 _SSColor;  // @default = vec4(0.5, 0.5, 0.6, 1.0)    the game's sun shadow colour (multiply)
    vec4 _SSWorld0; // @default = vec4(0.0, 0.0, 0.0, 0.0)    world origin in camera space, xyz; w = height map on (1)
    vec4 _SSWorldX; // @default = vec4(1.0, 0.0, 0.0, 0.0)    world x axis in camera space, xyz; w = height map width (cells)
    vec4 _SSWorldZ; // @default = vec4(0.0, 0.0, 1.0, 0.0)    world z axis in camera space, xyz; w = height map rows
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabIsoRimSSS;
layout(set = 2, binding = 2) uniform sampler2D _IsoSceneDepth;
layout(set = 2, binding = 3) uniform sampler2D _IsoShadowHeights;   // ShadowHeights.cs: per ground cell two solid spans, 1/8 cell

const float GroundEps = 0.03;
const float Lit = 0.98;   // alpha of a fully lit receiver (1 = no receiver)

ivec2 gSize;
bool gPersp;

// Camera-space position at pixel q (has = something drawn there above the ground; ground = the plane is nearer).
vec3 ScenePos(ivec2 q, out bool has, out bool ground)
{
    q = clamp(q, ivec2(0), gSize - 1);
    float d = texelFetch(_IsoSceneDepth, q, 0).r;
    vec2 ndc = (vec2(q) + 0.5) / vec2(gSize) * 2.0 - 1.0;
    vec3 o, dir, ps;
    has = d > 0.0;
    if (gPersp)
    {
        o = vec3(0.0);
        dir = vec3(ndc.x / u._SSProj.x, ndc.y / u._SSProj.y, -1.0);
        float zv = has ? -u._SSProj.w / (d + u._SSProj.z) : -1e6;
        ps = dir * -zv;
    }
    else
    {
        o = vec3((ndc.x - u._SSProjB.x) / u._SSProj.x, (ndc.y - u._SSProjB.y) / u._SSProj.y, 0.0);
        dir = vec3(0.0, 0.0, -1.0);
        float zv = has ? (d - u._SSProj.w) / u._SSProj.z : -1e6;
        ps = vec3(o.xy, zv);
    }
    ground = false;
    float dn = dot(u._SSPlane.xyz, dir);
    if (dn < -1e-5)
    {
        float t = -(dot(u._SSPlane.xyz, o) + u._SSPlane.w) / dn;
        if (t > 0.0)
        {
            vec3 pg = o + dir * t;
            if (ps.z <= pg.z + GroundEps)
            {
                ground = true;
                has = false;
                return pg;
            }
        }
    }
    return ps;
}

vec3 ScenePos(ivec2 q)
{
    bool h, g;
    return ScenePos(q, h, g);
}

// Pixel of a camera-space point (inverse of ScenePos).
vec2 PixelOf(vec3 p)
{
    vec2 ndc;
    if (gPersp)
    {
        float w = max(-p.z, 1e-4);
        ndc = vec2(u._SSProj.x * p.x, u._SSProj.y * p.y) / w;
    }
    else
    {
        ndc = vec2(u._SSProj.x * p.x + u._SSProjB.x, u._SSProj.y * p.y + u._SSProjB.y);
    }
    return (ndc * 0.5 + 0.5) * vec2(gSize) - 0.5;
}

bool OnScreen(vec2 px)
{
    return px.x >= 0.0 && px.y >= 0.0 && px.x <= float(gSize.x - 1) && px.y <= float(gSize.y - 1);
}

// How far the scene seen at the ray point's pixel is in front of the ray point (> 0 in front, cells). -1e6 when nothing
// is there (ground or empty), 1e6 when off screen.
float Gap(vec3 Q)
{
    vec2 px = PixelOf(Q);
    if (!OnScreen(px))
    {
        return 1e6;
    }
    bool sh, sg;
    vec3 S = ScenePos(ivec2(px + 0.5), sh, sg);
    if (!sh)
    {
        return -1e6;
    }
    if (gPersp)
    {
        return length(Q) - length(S);   // along the view ray: distances to the camera
    }
    return S.z - Q.z;
}

// Whether a camera-space point is inside a solid span of the world height map (walls, rock, ProjectZ slabs and the
// blocks on them, shown or hidden, on screen or not).
bool InHeightMap(vec3 Q)
{
    vec3 d = Q - u._SSWorld0.xyz;
    float x = dot(d, u._SSWorldX.xyz), z = dot(d, u._SSWorldZ.xyz), y = dot(d, u._SSPlane.xyz);
    if (x < 0.0 || z < 0.0 || x >= u._SSWorldX.w || z >= u._SSWorldZ.w || y < 0.0)
    {
        return false;
    }
    vec4 s = texelFetch(_IsoShadowHeights, ivec2(int(x), int(z)), 0) * (255.0 / 8.0);
    return (y >= s.x && y <= s.y) || (y >= s.z && y <= s.w);
}

void main()
{
    gSize = textureSize(_IsoSceneDepth, 0);
    gPersp = u._SSProjB.z > 0.5;
    ivec2 ip = ivec2(gl_FragCoord.xy);
    vec4 col = texelFetch(_GrabIsoRimSSS, ip, 0);
    int debug = int(u._SSParams.w + 0.5);
    if (((ip.x | ip.y) & 1) != 0)
    {
        outColor = col;   // not a sample: colour and alpha (AO's factor when AO ran) pass through
        return;
    }

    bool has, ground;
    vec3 P = ScenePos(ip, has, ground);
    if (!has && !ground)
    {
        outColor = vec4(col.rgb, 1.0);   // nothing there (sky, past the map): not a receiver
        return;
    }
    if (debug == 2)
    {
        outColor = ground ? vec4(0.15, 0.6, 0.15, 1.0) : vec4(0.6, 0.6, 0.6, 1.0);
        return;
    }

    // The ground receives too (its plane's normal); the game's own sun shadows are left out while this pass draws.
    vec3 N = u._SSPlane.xyz;
    if (!ground)
    {
        vec3 l = ScenePos(ip + ivec2(-1, 0)), r = ScenePos(ip + ivec2(1, 0));
        vec3 b = ScenePos(ip + ivec2(0, -1)), t = ScenePos(ip + ivec2(0, 1));
        vec3 dx = abs(r.z - P.z) < abs(P.z - l.z) ? r - P : P - l;
        vec3 dy = abs(t.z - P.z) < abs(P.z - b.z) ? t - P : P - b;
        N = normalize(cross(dx, dy));
        vec3 toCam = gPersp ? -P : vec3(0.0, 0.0, 1.0);
        if (dot(N, toCam) < 0.0)
        {
            N = -N;
        }
    }
    vec3 L = u._SSLight.xyz;
    float ndl = dot(N, L);
    if (debug == 5)
    {
        outColor = vec4(N * 0.5 + 0.5, 1.0);
        return;
    }
    if (debug == 3)
    {
        outColor = vec4(vec3(clamp(ndl, 0.0, 1.0)), 1.0);
        return;
    }

    // Faces turned away from the sun are already shaded by the light model: no shadow on top (fades in over a few degrees).
    float facing = smoothstep(0.0, 0.12, ndl);
    float shadow = 0.0;
    if (facing > 0.0)
    {
        float h = dot(u._SSPlane.xyz, P) + u._SSPlane.w;          // height above the ground, cells
        float sinSun = max(u._SSTune.y, 0.03);
        float tMax = clamp((u._SSTune.x - h) / sinSun, 0.0, u._SSParams.y);
        int steps = int(u._SSTune.z + 0.5);
        int k = ((ip.x >> 1) & 3) + (((ip.y >> 1) & 3) << 2);
        const int bayer[16] = int[16](0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
        float jit = (float(bayer[k]) + 0.5) / 16.0;
        float thick = u._SSParams.z;
        float dzPerT = gPersp ? 1.0 : abs(L.z);   // depth change per cell along the ray
        vec3 start = P + N * 0.02;
        float t0 = 0.04;
        float tPrev = t0;
        for (int i = 0; i < 64; i++)
        {
            if (i >= steps)
            {
                break;
            }
            float f = (float(i) + jit) / float(steps);
            float tt = t0 + (tMax - t0) * f * f;
            vec3 Q = start + L * tt;
            float g = Gap(Q);
            if (g >= 1e5)
            {
                break;   // off screen: nothing known, lit
            }
            float eps = 0.03 + 0.01 * tt;
            float span = (tt - tPrev) * dzPerT;
            if (g > eps && g < thick + span)
            {
                // Coarse to fine: where the ray went behind the surface between the last step and this one.
                float lo = tPrev, hi = tt;
                for (int s = 0; s < 4; s++)
                {
                    float mid = 0.5 * (lo + hi);
                    float gm = Gap(start + L * mid);
                    if (gm > eps && gm < 1e5)
                    {
                        hi = mid;
                    }
                    else
                    {
                        lo = mid;
                    }
                }
                float gh = Gap(start + L * hi);
                if (gh > eps && gh < thick)
                {
                    shadow = 1.0;
                    break;
                }
            }
            tPrev = tt;
        }
        // Fade near the screen edges (a caster just off screen is unknown to the depth).
        vec2 uv = (vec2(ip) + 0.5) / vec2(gSize);
        float e = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
        shadow *= smoothstep(0.0, max(u._SSTune.w, 1e-4), e);
        // The world height map: every 0.5 cells along the ray (at most 64 steps), off screen too; no edge fade.
        if (shadow < 1.0 && u._SSWorld0.w > 0.5)
        {
            float tHM = min(tMax, 64.0 * 0.5);
            float stepHM = max(0.5, tHM / 64.0);
            for (int i = 1; i <= 64; i++)
            {
                float tt = float(i) * stepHM - 0.2 * jit * stepHM;
                if (tt > tHM)
                {
                    break;
                }
                if (InHeightMap(start + L * tt))
                {
                    shadow = 1.0;
                    break;
                }
            }
        }
        shadow *= facing;
    }
    if (debug == 6)
    {
        outColor = vec4(vec3(1.0 - shadow), 1.0);
        return;
    }
    outColor = vec4(col.rgb, Lit * (1.0 - shadow));
}
