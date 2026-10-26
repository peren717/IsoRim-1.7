#version 450

// IsoRim sky: zenith to horizon gradient, the horizon colour below it (the distance fog,
// IsoRimDistFog, fades the far map into the same colour, so the cut at the view distance never
// shows), a sun disc with a glow and a moon disc with phases.
// At night (C3): stars, the Milky Way, a few planets and a ringed gas giant, all on a celestial
// sphere that turns with the hour round a pole set by the map's latitude. Everything is made
// here from hashes and noise, no textures.
// In space (_SkySpace.x): black with the night sky all round, below the horizon too; or, with
// _SkySpace.y, nothing below the horizon, so the game's planet backdrop behind the map shows there,
// under a thin blue rim of air (_SkySpace.z). The same program as IsoRimSkyDim (_SkyDim.w = 1, the
// game's light-overlay blend, 2 x src x dst) then darkens that backdrop below the horizon by the map's
// sky light (_SkyDim.rgb), as the game's light overlay darkens it on the map's own space cells.

layout(location = 0) in vec3 vRay;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _SkyFwd;      // @default = vec4(0.0, 0.0, 1.0, 0.0)
    vec4 _SkyRight;    // @default = vec4(1.0, 0.0, 0.0, 0.0)
    vec4 _SkyUp;       // @default = vec4(0.0, 1.0, 0.0, 0.0)
    vec4 _SkyZenith;   // @default = vec4(0.3, 0.5, 0.85, 1.0)
    vec4 _SkyHorizon;  // @default = vec4(0.75, 0.82, 0.9, 1.0)
    vec4 _SkySun;      // @default = vec4(0.0, 1.0, 0.0, 0.0)
    vec4 _SkySunColor; // @default = vec4(1.0, 0.95, 0.8, 1.0)
    vec4 _SkyMoon;     // @default = vec4(0.0, 1.0, 0.0, 0.0)
    vec4 _SkyCelA;     // @default = vec4(1.0, 0.0, 0.0, 0.0)
    vec4 _SkyCelB;     // @default = vec4(0.0, 0.0, 1.0, 0.0)
    vec4 _SkyCelP;     // @default = vec4(0.0, 1.0, 0.0, 0.0)
    vec4 _SkyNight;    // @default = vec4(0.0, 0.0, 0.0, 0.0)
    vec4 _SkyMisc;     // @default = vec4(0.0, 0.5, 0.0, 1.0)
    vec4 _SkyTrueSun;  // @default = vec4(0.0, 1.0, 0.0, 1.0)
    vec4 _SkySpace;    // @default = vec4(0.0, 0.0, 0.0, 0.0)
    vec4 _SkyDim;      // @default = vec4(1.0, 1.0, 1.0, 0.0)
    vec4 _SkyMid;      // @default = vec4(0.5, 0.66, 0.88, 1.0)   painted style only (IsoRimSkyPaint.frag.glsl)
    vec4 _SkyCloudLit; // @default = vec4(1.0, 0.98, 0.92, 1.0)   painted style only
    vec4 _SkyCloudShade; // @default = vec4(0.68, 0.76, 0.92, 1.0) painted style only
    vec4 _SkyCloud;    // @default = vec4(0.0, 0.0, 0.0, 0.0)   painted style only
} u;

// ------------------------------------------------------------------ noise

float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

vec3 hash33(vec3 p)
{
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.xxy + p.yxx) * p.zyx);
}

float vnoise(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = mix(mix(hash13(i), hash13(i + vec3(1, 0, 0)), f.x),
                  mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), f.x), f.y);
    float b = mix(mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), f.x),
                  mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), f.x), f.y);
    return mix(a, b, f.z);
}

float fbm(vec3 p)
{
    float s = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; i++)
    {
        s += a * vnoise(p);
        p = p * 2.03 + vec3(1.7, 9.2, 3.1);
        a *= 0.5;
    }
    return s;
}

// ------------------------------------------------------------------ stars

// One layer of stars: a grid on the faces of a cube round the sky, at most one star per cell.
// Each star is a soft dot about a pixel or two wide whatever the zoom (pix = pixel size in radians).
vec3 starLayer(vec3 d, float n, float density, float pix, float t, float seed)
{
    vec3 a = abs(d);
    vec2 uv;
    float face;
    if (a.x >= a.y && a.x >= a.z)
    {
        uv = d.yz / a.x;
        face = d.x > 0.0 ? 0.0 : 1.0;
    }
    else if (a.y >= a.z)
    {
        uv = d.xz / a.y;
        face = d.y > 0.0 ? 2.0 : 3.0;
    }
    else
    {
        uv = d.xy / a.z;
        face = d.z > 0.0 ? 4.0 : 5.0;
    }
    vec2 g = uv * n;
    vec2 cell = floor(g);
    vec3 h = hash33(vec3(cell, face * 17.0 + seed));
    if (h.x > density)
    {
        return vec3(0.0);
    }
    vec2 sp = 0.2 + 0.6 * h.yz;
    float dist = length(fract(g) - sp);
    float sz = max(pix * n * 0.75, 0.02);
    float core = exp(-dist * dist / (sz * sz));
    float h2 = hash13(vec3(cell.yx, face * 7.1 + seed * 3.0));
    float mag = 0.04 + pow(h2, 8.0) * 2.0;
    float tw = 0.7 + 0.3 * sin(t * (2.0 + 4.0 * h.y) + h.z * 6.283);
    vec3 col = mix(vec3(0.7, 0.8, 1.0), vec3(1.0, 0.82, 0.62), h.y * h.z * 1.6);
    return col * core * mag * tw;
}

// ------------------------------------------------------------------ main

void main()
{
    vec3 r = normalize(vRay);
    float e = r.y;
    if (u._SkyDim.w > 0.5)
    {
        if (e >= 0.0)
        {
            discard;
        }
        outColor = vec4(u._SkyDim.rgb * 0.5, 1.0);
        return;
    }
    vec3 c = u._SkyHorizon.rgb;
    if (e > 0.0)
    {
        c = mix(u._SkyHorizon.rgb, u._SkyZenith.rgb, pow(clamp(e, 0.0, 1.0), 0.55));
    }

    float pix = max(length(dFdx(r)), length(dFdy(r)));
    float above = smoothstep(-0.01, 0.12, e);
    if (u._SkySpace.x > 0.5)
    {
        if (u._SkySpace.y > 0.5)
        {
            if (e < 0.0)
            {
                discard;
            }
            above = 1.0;
            // The planet's air seen edge on: a thin blue band along the horizon.
            c += vec3(0.25, 0.5, 1.0) * (exp(-e * 90.0) * 0.55 + exp(-e * 18.0) * 0.12) * u._SkySpace.z;
        }
        else
        {
            above = 1.0;
        }
    }
    float days = u._SkyMisc.x;
    float time = u._SkyMisc.z;
    float day = u._SkyMisc.w;

    // The celestial sphere: the ray in sky coordinates (pole along z).
    vec3 cs = vec3(dot(r, u._SkyCelA.xyz), dot(r, u._SkyCelB.xyz), dot(r, u._SkyCelP.xyz));

    // Milky Way: a band round a great circle, brightest toward its core, with dark dust lanes.
    float band = 0.0;
    if (u._SkyNight.y > 0.001 && above > 0.0)
    {
        vec3 gn = normalize(vec3(0.35, 0.55, 0.76));
        vec3 g1 = normalize(cross(gn, vec3(0.0, 0.0, 1.0)));
        vec3 g2 = cross(gn, g1);
        float b = dot(cs, gn);
        float l = atan(dot(cs, g2), dot(cs, g1));
        float core = exp(-l * l * 1.2);
        float w = 0.11 + 0.09 * core;
        band = exp(-b * b / (w * w));
        if (band > 0.002)
        {
            float clouds = band * (0.35 + 0.9 * fbm(cs * 4.0)) * (0.45 + 0.9 * core);
            float lane = smoothstep(0.42, 0.68, fbm(cs * 7.0 + 3.0)) * exp(-b * b / (w * w * 0.12));
            clouds *= 1.0 - 0.8 * lane;
            vec3 mw = mix(vec3(0.55, 0.62, 0.88), vec3(1.0, 0.86, 0.66), core * 0.85);
            float neb = smoothstep(0.62, 0.8, fbm(cs * 11.0 + 7.0)) * band;
            mw += vec3(0.9, 0.45, 0.6) * neb * 0.6;
            c += mw * clouds * 0.16 * u._SkyNight.y * above;
        }
    }

    // Stars: a dense faint layer, a sparse bright one, thicker along the Milky Way.
    if (u._SkyNight.x > 0.001 && above > 0.0)
    {
        float twinkle = time * (1.0 + 2.0 * (1.0 - clamp(e * 3.0, 0.0, 1.0)));
        vec3 s = starLayer(cs, 300.0, 0.03 + 0.1 * band, pix, twinkle, 1.0);
        s += starLayer(cs, 90.0, 0.07, pix, twinkle, 5.0) * 1.4;
        c += s * u._SkyNight.x * above;
    }

    // Planets: three wanderers along the ecliptic, steady (no twinkle), moving from day to day.
    if (u._SkyNight.z > 0.001 && above > 0.0)
    {
        vec3 en = normalize(vec3(0.0, -0.4, 0.92));
        vec3 e1 = vec3(1.0, 0.0, 0.0);
        vec3 e2 = cross(en, e1);
        float sz = max(pix * 1.4, 0.0006);
        for (int i = 0; i < 3; i++)
        {
            float fi = float(i);
            float lon = 1.3 + fi * 2.2 + days * (i == 0 ? 0.031 : (i == 1 ? 0.0105 : 0.0019));
            vec3 pd = normalize(cos(lon) * e1 + sin(lon) * e2 + en * 0.04 * sin(lon * 2.0 + fi));
            vec3 v = cs - pd;
            float d2 = dot(v, v);
            float mag = i == 0 ? 2.4 : (i == 1 ? 1.0 : 1.7);
            vec3 col = i == 0 ? vec3(1.0, 0.97, 0.86) : (i == 1 ? vec3(1.0, 0.55, 0.35) : vec3(1.0, 0.9, 0.74));
            float p = exp(-d2 / (sz * sz)) + exp(-d2 / (sz * sz * 18.0)) * 0.06;
            c += col * p * mag * u._SkyNight.z * above;
        }
    }

    // Gas giant with rings, lit by the real sun, so it shows phases; faint by day, solid at night, hiding
    // the stars behind it. It stands at a fixed place in the sky (_SkyCelA.w azimuth, _SkyCelB.w height,
    // radians, set by Sky.GiantDir) on the far side from the sun's whole path, so the two never meet; it is
    // laid on after the sun all the same (it is nearer and would hide it), before the moon.
    vec3 gCol = vec3(0.0);
    float gCover = 0.0;
    if (u._SkyNight.w > 0.001)
    {
        vec3 g = vec3(sin(u._SkyCelA.w) * cos(u._SkyCelB.w), sin(u._SkyCelB.w), cos(u._SkyCelA.w) * cos(u._SkyCelB.w));
        vec3 pole = normalize(u._SkyCelP.xyz * 0.85 + u._SkyCelA.xyz * 0.35 + u._SkyCelB.xyz * 0.2);
        vec3 L = normalize(u._SkyTrueSun.xyz);
        float R = 0.06 * u._SkyMisc.y;

        float b = dot(r, g);
        float ang = sqrt(max(2.0 * (1.0 - b), 0.0));
        vec3 col = vec3(0.0);
        float cover = 0.0;

        if (ang < R * 2.6)
        {
            // The planet (a sphere of radius R at distance 1).
            float disc = b * b - (1.0 - R * R);
            float tP = 1e9;
            float pCover = 1.0 - smoothstep(R - pix, R + pix, ang);
            vec3 pCol = vec3(0.0);
            if (pCover > 0.0)
            {
                tP = b - sqrt(max(disc, 0.0));
                vec3 n = normalize(r * tP - g);
                float lat = dot(n, pole);
                vec3 q = vec3(lat * 9.0, dot(n, cross(pole, g)) * 2.0, 0.0);
                float bandsN = sin(lat * 23.0 + 2.2 * fbm(q + vec3(0.0, 0.0, days * 0.02)));
                vec3 cream = vec3(0.93, 0.84, 0.68);
                vec3 rust = vec3(0.76, 0.52, 0.34);
                vec3 base = mix(cream, rust, 0.5 + 0.5 * bandsN);
                base = mix(base, vec3(0.62, 0.72, 0.82), smoothstep(0.75, 0.95, abs(lat)) * 0.6);
                vec3 spotC = normalize(-g + pole * -0.35 + cross(pole, g) * 0.3);
                float spot = smoothstep(0.22, 0.12, length(n - spotC));
                base = mix(base, vec3(0.85, 0.45, 0.3), spot * 0.8);
                float lit = max(dot(n, L), 0.0);
                float limb = 0.55 + 0.45 * max(dot(n, -r), 0.0);
                float rim = pow(1.0 - max(dot(n, -r), 0.0), 3.0);
                pCol = base * (lit * 1.1 + 0.06) * limb + vec3(0.25, 0.35, 0.5) * rim * 0.25;
            }

            // The rings: a disc in the planet's equator, with a gap, shadowed by the planet.
            float rCover = 0.0;
            vec3 rCol = vec3(0.0);
            float denom = dot(r, pole);
            if (abs(denom) > 1e-4)
            {
                float tR = dot(g, pole) / denom;
                vec3 P = r * tR;
                float rho = length(P - g) / R;
                if (rho > 1.3 && rho < 2.4 && tR > 0.0)
                {
                    float dens = 0.55 + 0.35 * sin(rho * 47.0) + 0.2 * sin(rho * 113.0);
                    dens *= smoothstep(1.3, 1.4, rho) * (1.0 - smoothstep(2.25, 2.4, rho));
                    dens *= 1.0 - 0.9 * (smoothstep(1.86, 1.9, rho) - smoothstep(1.97, 2.01, rho));
                    dens = clamp(dens, 0.0, 1.0);
                    vec3 qv = P - g;
                    float bb = dot(qv, L);
                    float cc = dot(qv, qv) - R * R;
                    float shade = (bb < 0.0 && bb * bb - cc > 0.0) ? 0.08 : 1.0;
                    float rl = (0.3 + 0.7 * abs(dot(pole, L))) * shade * smoothstep(-0.25, 0.1, L.y + 0.3);
                    rCol = vec3(0.86, 0.8, 0.7) * (rl + 0.06);
                    rCover = dens * (0.3 + 0.55 * clamp(rl * 2.0, 0.0, 1.0));
                    if (tR > tP)
                    {
                        // Behind the planet.
                        rCover *= 1.0 - pCover;
                    }
                }
            }

            // Ring in front of the planet over the planet, otherwise the planet over the ring.
            cover = pCover + rCover * (1.0 - pCover);
            col = cover > 0.0 ? (pCol * pCover * (1.0 - rCover) + rCol * rCover) / max(pCover * (1.0 - rCover) + rCover, 1e-4) : vec3(0.0);
        }

        gCol = col;
        gCover = cover * u._SkyNight.w * above;
    }

    // Sun: a soft glow round it, warm at the horizon, and the disc.
    vec3 cNoSun = c;
    float sd = dot(r, normalize(u._SkySun.xyz));
    float glow = pow(max(sd, 0.0), 24.0) * 0.35 + pow(max(sd, 0.0), 300.0) * 0.6;
    float sdisc = smoothstep(0.99965, 0.99985, sd);
    c += u._SkySunColor.rgb * (glow + sdisc * 1.5) * u._SkySun.w;

    if (gCover > 0.0)
    {
        c = mix(c, cNoSun, gCover);
        vec3 night = mix(c, gCol, gCover);
        vec3 daylit = c + gCol * gCover * 0.55;
        c = mix(night, daylit, day);
    }

    // Moon: a pale disc with its phase (_SkyTrueSun.w), grey seas and a faint halo.
    vec3 m = normalize(u._SkyMoon.xyz);
    float md = dot(r, m);
    float phase = u._SkyTrueSun.w;              // 0 new, 0.5 full
    float lightFrac = 0.5 - 0.5 * cos(phase * 6.2832);
    float mhalo = pow(max(md, 0.0), 120.0) * 0.12 * (0.25 + 0.75 * lightFrac);
    float MR = 0.0195;
    float mang = sqrt(max(2.0 * (1.0 - md), 0.0));
    vec3 moonC = vec3(0.0);
    float mCover = 1.0 - smoothstep(MR - pix, MR + pix, mang);
    if (mCover > 0.0)
    {
        vec3 t1 = normalize(cross(m, vec3(0.0, 1.0, 0.0)));
        vec3 t2 = cross(t1, m);
        vec2 p = vec2(dot(r, t1), dot(r, t2)) / MR;
        float pz = sqrt(max(1.0 - dot(p, p), 0.0));
        float ph = phase * 6.2832;
        float lit = smoothstep(-0.06, 0.06, p.x * sin(ph) - pz * cos(ph));
        float seas = smoothstep(0.45, 0.7, fbm(vec3(p * 2.2, 4.0)));
        vec3 surf = vec3(0.88, 0.9, 1.0) * (1.0 - 0.35 * seas);
        moonC = surf * (lit * 0.95 + 0.05);
    }
    float mShow = u._SkyMoon.w;
    c = mix(c, mix(c, moonC, mCover), mShow);
    c += vec3(0.85, 0.9, 1.0) * mhalo * mShow;

    outColor = vec4(c, 1.0);
}
