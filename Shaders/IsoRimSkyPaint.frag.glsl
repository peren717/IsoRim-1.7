#version 450

// IsoRim sky, painted style (IsoRimSettings.skyStyle 0, the default; IsoRimSky.frag.glsl is the classic one).
// The same inputs and the same celestial sphere as the classic sky, drawn like a painted backdrop:
// a few flat bands from the horizon to the zenith with soft brushy edges, chunky two- and three-tone
// cartoon clouds drifting slowly (_SkyCloud, none in space), the sun and moon as clean discs with a
// soft halo, a few sparkling stars (the brightest with a small cross glint), a soft painted Milky Way,
// and a gas giant in flat colour bands with a dark outline and a flat ring. All procedural.

layout(location = 0) in vec3 vRay;

layout(location = 0) out vec4 outColor;

// Must match IsoRimSky.vert.glsl exactly.
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
    vec4 _SkyMid;      // @default = vec4(0.5, 0.66, 0.88, 1.0)   painted: the band colour half way up
    vec4 _SkyCloudLit; // @default = vec4(1.0, 0.98, 0.92, 1.0)   painted: the clouds' lit tone
    vec4 _SkyCloudShade; // @default = vec4(0.68, 0.76, 0.92, 1.0) painted: the clouds' shaded tone
    vec4 _SkyCloud;    // @default = vec4(0.0, 0.0, 0.0, 0.0)   painted: cover (0 none .. 1 overcast), drift x, drift z, -
} u;

// ------------------------------------------------------------------ noise

float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
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

float vnoise2(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i), hash12(i + vec2(1, 0)), f.x),
               mix(hash12(i + vec2(0, 1)), hash12(i + vec2(1, 1)), f.x), f.y);
}

float fbm3(vec3 p)
{
    float s = 0.0;
    float a = 0.5;
    for (int i = 0; i < 3; i++)
    {
        s += a * vnoise(p);
        p = p * 2.03 + vec3(1.7, 9.2, 3.1);
        a *= 0.5;
    }
    return s / 0.875;
}

// One cumulus cloud as a signed distance (negative inside), in the cloud's own frame (x along the
// horizon, y up the screen, about 1 unit wide): three round puffs along the base and two on top,
// sized and placed by the cloud's hashes a and b. lift moves every puff up and shrink scales them,
// which gives the lit (scalloped) part of the cloud for the shading.
float cumulus(vec2 q, vec3 a, vec3 b, float lift, float shrink)
{
    vec2 c0 = vec2(-0.25 + 0.06 * a.x, 0.0);
    vec2 c1 = vec2(0.0, 0.04 + 0.04 * a.z);
    vec2 c2 = vec2(0.24 - 0.06 * b.y, -0.01);
    vec2 c3 = vec2(-0.10 + 0.07 * a.y, 0.15 + 0.05 * b.x);
    vec2 c4 = vec2(0.10 + 0.06 * b.z, 0.11 + 0.05 * a.x);
    q.y -= lift;
    float d = length(q - c0) - (0.15 + 0.04 * a.y) * shrink;
    d = min(d, length(q - c1) - (0.20 + 0.04 * b.x) * shrink);
    d = min(d, length(q - c2) - (0.14 + 0.04 * b.z) * shrink);
    d = min(d, length(q - c3) - (0.12 + 0.05 * a.z) * shrink);
    d = min(d, length(q - c4) - (0.10 + 0.04 * b.y) * shrink);
    return d;
}

// ------------------------------------------------------------------ stars

// Sparse painted stars on a cube-face grid round the sky, at most one per cell: round dots a pixel
// or two wide, the brightest with a small four-point glint (arms along the cell axes, in pixels).
vec3 starLayer(vec3 d, float n, float density, float pix, float t, float seed, float glints)
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
    vec2 sp = 0.3 + 0.4 * h.yz;
    // The offset from the star in screen pixels (pix: one pixel in radians).
    vec2 px = (fract(g) - sp) / max(pix * n, 1e-5);
    float h2 = hash13(vec3(cell.yx, face * 7.1 + seed * 3.0));
    float big = step(1.0 - glints, h2);
    float rad = 0.9 + 0.8 * h2 + big * 0.6;
    float dist = length(px);
    float core = 1.0 - smoothstep(rad - 0.6, rad + 0.6, dist);
    float arm = 3.5 + 4.0 * h.z;
    float glint = (exp(-abs(px.x) / arm * 2.2) * exp(-px.y * px.y * 1.6)
                 + exp(-abs(px.y) / arm * 2.2) * exp(-px.x * px.x * 1.6)) * big;
    float tw = 0.8 + 0.2 * sin(t * (1.5 + 3.0 * h.y) + h.z * 6.283);
    vec3 col = mix(vec3(0.95, 0.96, 1.0), vec3(1.0, 0.9, 0.72), step(0.7, h.y));
    return col * clamp(core + glint * 0.85, 0.0, 1.0) * (0.55 + 0.45 * h2) * tw;
}

// ------------------------------------------------------------------ main

void main()
{
    vec3 r = normalize(vRay);
    float e = r.y;
    if (u._SkyDim.w > 0.5)
    {
        // Not used in the painted pass (IsoRimSkyDim runs the classic program), kept for parity.
        if (e >= 0.0)
        {
            discard;
        }
        outColor = vec4(u._SkyDim.rgb * 0.5, 1.0);
        return;
    }

    float pix = max(length(dFdx(r)), length(dFdy(r)));

    // Bands: horizon, mid and zenith colours, stepped into a few flat tones whose edges wander
    // like brush strokes (noise stretched along the horizon).
    vec3 c = u._SkyHorizon.rgb;
    if (e > 0.0)
    {
        float t = pow(clamp(e, 0.0, 1.0), 0.6);
        float brush = vnoise(vec3(r.x * 5.0, r.y * 55.0, r.z * 5.0)) * 0.7 + vnoise(vec3(r.x * 19.0, r.y * 160.0, r.z * 19.0)) * 0.3;
        const float N = 6.0;
        float tt = t * N + (brush - 0.5) * 0.7;
        float q = (floor(tt) + smoothstep(0.42, 0.58, fract(tt))) / N;
        q = clamp(q, 0.0, 1.0);
        c = q < 0.5 ? mix(u._SkyHorizon.rgb, u._SkyMid.rgb, q * 2.0) : mix(u._SkyMid.rgb, u._SkyZenith.rgb, q * 2.0 - 1.0);
        // Soften into the horizon colour right at the horizon (the distance fog ends in it).
        c = mix(u._SkyHorizon.rgb, c, smoothstep(0.0, 0.035, e));
    }

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
            c = u._SkyZenith.rgb;
            // The planet's air seen edge on: a thin blue band along the horizon, in two flat steps.
            float rim = smoothstep(0.03, 0.022, e) * 0.5 + smoothstep(0.11, 0.09, e) * 0.14;
            c += vec3(0.25, 0.5, 1.0) * rim * u._SkySpace.z;
        }
        else
        {
            above = 1.0;
            c = u._SkyZenith.rgb;
        }
    }
    float days = u._SkyMisc.x;
    float time = u._SkyMisc.z;
    float day = u._SkyMisc.w;

    vec3 cs = vec3(dot(r, u._SkyCelA.xyz), dot(r, u._SkyCelB.xyz), dot(r, u._SkyCelP.xyz));

    // Milky Way: a soft lavender band in a few flat dabs.
    float band = 0.0;
    if (u._SkyNight.y > 0.001 && above > 0.0)
    {
        vec3 gn = normalize(vec3(0.35, 0.55, 0.76));
        vec3 g1 = normalize(cross(gn, vec3(0.0, 0.0, 1.0)));
        vec3 g2 = cross(gn, g1);
        float b = dot(cs, gn);
        float l = atan(dot(cs, g2), dot(cs, g1));
        float core = exp(-l * l * 1.2);
        float w = 0.10 + 0.06 * core;
        band = exp(-b * b / (w * w));
        if (band > 0.01)
        {
            float f = fbm3(vec3(l * 2.2, b * 16.0, 2.7)) * band * (0.6 + 0.6 * core);   // streaked along the band
            float dabs = smoothstep(0.30, 0.36, f) * 0.45 + smoothstep(0.48, 0.54, f) * 0.35 + smoothstep(0.66, 0.72, f) * 0.2;
            vec3 mw = mix(vec3(0.42, 0.42, 0.72), vec3(0.72, 0.62, 0.88), smoothstep(0.48, 0.54, f));
            c += vec3(0.30, 0.30, 0.55) * smoothstep(0.25, 0.7, band) * 0.14 * u._SkyNight.y * above;
            c = mix(c, c + mw * 0.34, dabs * u._SkyNight.y * above);
        }
    }

    // Stars: a few bright sparkles (some with glints) and a sprinkle of small dots.
    if (u._SkyNight.x > 0.001 && above > 0.0)
    {
        float twinkle = time;
        vec3 s = starLayer(cs, 26.0, 0.13, pix, twinkle, 3.0, 0.3);
        s += starLayer(cs, 70.0, 0.025 + 0.05 * band, pix, twinkle, 9.0, 0.0) * 0.6;
        c += s * u._SkyNight.x * above;
    }

    // Planets: three steady round dots along the ecliptic.
    if (u._SkyNight.z > 0.001 && above > 0.0)
    {
        vec3 en = normalize(vec3(0.0, -0.4, 0.92));
        vec3 e1 = vec3(1.0, 0.0, 0.0);
        vec3 e2 = cross(en, e1);
        for (int i = 0; i < 3; i++)
        {
            float fi = float(i);
            float lon = 1.3 + fi * 2.2 + days * (i == 0 ? 0.031 : (i == 1 ? 0.0105 : 0.0019));
            vec3 pd = normalize(cos(lon) * e1 + sin(lon) * e2 + en * 0.04 * sin(lon * 2.0 + fi));
            float d = length(cs - pd) / max(pix, 1e-5);
            float rad = i == 0 ? 2.2 : (i == 1 ? 1.5 : 1.8);
            vec3 col = i == 0 ? vec3(1.0, 0.97, 0.86) : (i == 1 ? vec3(1.0, 0.6, 0.42) : vec3(1.0, 0.9, 0.74));
            float p = 1.0 - smoothstep(rad - 0.7, rad + 0.7, d);
            c = mix(c, col, p * u._SkyNight.z * above);
        }
    }

    // Gas giant: flat bands, two-tone light, a dark outline, a flat ring.
    if (u._SkyNight.w > 0.001)
    {
        float ra = 1.2 + days * 0.004;
        vec3 gc = vec3(cos(0.96) * cos(ra), cos(0.96) * sin(ra), sin(0.96));
        vec3 g = normalize(gc.x * u._SkyCelA.xyz + gc.y * u._SkyCelB.xyz + gc.z * u._SkyCelP.xyz);
        vec2 gf = length(g.xz) > 1e-4 ? normalize(g.xz) : vec2(0.0, 1.0);
        float gel = asin(clamp(g.y, -1.0, 1.0)) * u._SkyCelP.w;
        g = vec3(gf.x * cos(gel), sin(gel), gf.y * cos(gel));
        // The ring seen nearly edge on, as a thin slanted ellipse: its axis mostly "up" across the line
        // of sight (gu), leaned sideways (gs) and a little toward us.
        vec3 gu = normalize(vec3(0.0, 1.0, 0.0) - g * g.y);
        vec3 gs = cross(g, gu);
        vec3 pole = normalize(gu * 0.94 + gs * 0.34 + g * 0.26);
        vec3 L = normalize(u._SkyTrueSun.xyz);
        float R = 0.06 * u._SkyMisc.y;

        float b = dot(r, g);
        float ang = sqrt(max(2.0 * (1.0 - b), 0.0));
        vec3 col = vec3(0.0);
        float cover = 0.0;

        if (ang < R * 2.6)
        {
            float line = max(pix * 1.6, R * 0.03);
            float disc = b * b - (1.0 - R * R);
            float tP = 1e9;
            float pCover = 1.0 - smoothstep(R - pix, R + pix, ang);
            vec3 pCol = vec3(0.0);
            if (pCover > 0.0)
            {
                tP = b - sqrt(max(disc, 0.0));
                vec3 n = normalize(r * tP - g);
                float lat = dot(n, pole);
                float lon = dot(n, cross(pole, g));
                float wob = vnoise(vec3(lat * 14.0, lon * 3.0, 1.3 + days * 0.02)) - 0.5;
                float bandV = sin(lat * 19.0 + wob * 2.2) * 0.5 + 0.5;
                vec3 cream = vec3(0.95, 0.84, 0.66);
                vec3 tanC = vec3(0.86, 0.66, 0.46);
                vec3 rust = vec3(0.78, 0.45, 0.28);
                vec3 base = bandV < 0.38 ? cream : (bandV < 0.72 ? tanC : rust);
                base = mix(base, vec3(0.9, 0.8, 0.66), step(0.86, abs(lat)));
                float lit = smoothstep(-0.04, 0.04, dot(n, L));
                vec3 shade = base * vec3(0.42, 0.36, 0.42);
                pCol = mix(shade, base, 0.08 + 0.92 * lit);
                // The outline: a dark ring just inside the edge.
                float edge = smoothstep(R - line - pix, R - line, ang);
                pCol = mix(pCol, vec3(0.08, 0.06, 0.08), edge);
            }

            float rCover = 0.0;
            vec3 rCol = vec3(0.0);
            float denom = dot(r, pole);
            if (abs(denom) > 1e-4)
            {
                float tR = dot(g, pole) / denom;
                vec3 P = r * tR;
                float rho = length(P - g) / R;
                if (rho > 1.35 && rho < 2.35 && tR > 0.0)
                {
                    float aa = pix / R * 1.5;
                    float inside = smoothstep(1.4, 1.4 + aa, rho) * (1.0 - smoothstep(2.3 - aa, 2.3, rho));
                    float gap = smoothstep(1.86, 1.86 + aa, rho) - smoothstep(1.95, 1.95 + aa, rho);
                    vec3 rc = rho < 1.7 ? vec3(0.70, 0.58, 0.46) : (rho < 2.08 ? vec3(0.90, 0.80, 0.64) : vec3(0.80, 0.70, 0.56));
                    vec3 qv = P - g;
                    float bb = dot(qv, L);
                    float cc = dot(qv, qv) - R * R;
                    float shade = (bb < 0.0 && bb * bb - cc > 0.0) ? 0.35 : 1.0;
                    float rl = shade * (0.55 + 0.45 * smoothstep(-0.25, 0.1, L.y + 0.3));
                    rCol = rc * rl;
                    rCover = inside * (1.0 - 0.85 * gap) * 0.95;
                    if (tR > tP)
                    {
                        rCover *= 1.0 - pCover;
                    }
                }
            }

            cover = pCover + rCover * (1.0 - pCover);
            col = cover > 0.0 ? (pCol * pCover * (1.0 - rCover) + rCol * rCover) / max(pCover * (1.0 - rCover) + rCover, 1e-4) : vec3(0.0);
        }

        cover *= u._SkyNight.w * above;
        vec3 night = mix(c, col, cover);
        vec3 daylit = mix(c, mix(c, col, 0.55), cover);
        c = mix(night, daylit, day);
    }

    // Sun: a clean disc and a soft halo in two steps, plus a wide warm wash low in the sky.
    vec3 sdir = normalize(u._SkySun.xyz);
    float sd = dot(r, sdir);
    float sang = sqrt(max(2.0 * (1.0 - sd), 0.0));
    float SR = 0.03;
    float sdisc = 1.0 - smoothstep(SR - pix, SR + pix, sang);
    float halo = (1.0 - smoothstep(SR * 1.5, SR * 2.6, sang)) * 0.3 + (1.0 - smoothstep(SR * 2.6, SR * 4.5, sang)) * 0.12;
    float wash = pow(max(sd, 0.0), 10.0) * 0.22 * (1.0 - smoothstep(0.1, 0.5, sdir.y));
    vec3 sunDisc = mix(u._SkySunColor.rgb, vec3(1.0, 0.95, 0.62), 0.75);
    vec3 haloC = mix(u._SkySunColor.rgb, vec3(1.0, 0.97, 0.85), 0.6);
    float sShow = u._SkySun.w;
    c = mix(c, haloC, clamp(halo * 1.3, 0.0, 1.0) * sShow);
    c += u._SkySunColor.rgb * wash * sShow;
    c = mix(c, sunDisc, sdisc * sShow);

    // Moon: a clean cream disc with its phase and a soft halo.
    vec3 m = normalize(u._SkyMoon.xyz);
    float md = dot(r, m);
    float phase = u._SkyTrueSun.w;
    float lightFrac = 0.5 - 0.5 * cos(phase * 6.2832);
    float MR = 0.022;
    float mang = sqrt(max(2.0 * (1.0 - md), 0.0));
    float mhalo = (1.0 - smoothstep(MR * 1.3, MR * 3.2, mang)) * 0.16 * (0.3 + 0.7 * lightFrac);
    float mCover = 1.0 - smoothstep(MR - pix, MR + pix, mang);
    float mLit = 0.0;
    if (mCover > 0.0)
    {
        vec3 t1 = normalize(cross(m, vec3(0.0, 1.0, 0.0)));
        vec3 t2 = cross(t1, m);
        vec2 p = vec2(dot(r, t1), dot(r, t2)) / MR;
        float pz = sqrt(max(1.0 - dot(p, p), 0.0));
        float ph = phase * 6.2832;
        float w = max(pix / MR, 0.02);
        mLit = smoothstep(-w, w, p.x * sin(ph) - pz * cos(ph));
    }
    float mShow = u._SkyMoon.w;
    c += vec3(0.85, 0.9, 1.0) * mhalo * mShow;
    vec3 moonC = mix(c + vec3(0.02, 0.025, 0.04), vec3(1.0, 0.95, 0.82), mLit);
    c = mix(c, moonC, mCover * mShow);

    // Clouds: separate cumulus clouds (cumulus) scattered one per cell of a grid on the sky. The grid
    // is the sky seen from below in stereographic projection, which keeps shapes undistorted, gently
    // stretched toward the horizon so the clouds there come out smaller and flatter (never smeared).
    // Each cloud stands upright on the screen (its up = toward the zenith), has a flat bottom and
    // three tones: a shaded underside, the body, and a scalloped lit top. Heavy cover (rain, snow,
    // fog) adds an overcast layer behind them.
    float cov = u._SkyCloud.x;
    if (cov > 0.001 && e > 0.003 && u._SkySpace.x < 0.5)
    {
        vec3 litC = u._SkyCloudLit.rgb;
        vec3 shd = u._SkyCloudShade.rgb;
        vec3 mid = mix(shd, litC, 0.85);
        float fade = smoothstep(0.003, 0.05, e);

        float oc = smoothstep(0.72, 0.95, cov);
        if (oc > 0.0)
        {
            c = mix(c, mix(shd, mid, 0.35), oc * fade);
        }

        vec2 p0 = r.xz / (1.0 + e);
        vec2 drift = u._SkyCloud.yz;
        vec2 ps = p0 * (1.0 + 0.6 * dot(p0, p0)) * 1.7 + drift;
        vec2 cell0 = floor(ps);
        float prob = mix(0.3, 1.0, cov);
        float best = 1e3;
        vec2 bq = vec2(0.0);
        vec3 ba = vec3(0.0);
        vec3 bb = vec3(0.0);
        for (int j = -1; j <= 1; j++)
        {
            for (int i = -1; i <= 1; i++)
            {
                vec2 cell = cell0 + vec2(float(i), float(j));
                vec3 h = hash33(vec3(cell, 41.0));
                if (h.x > prob)
                {
                    continue;
                }
                vec3 a = hash33(vec3(cell, 13.0));
                vec3 b = hash33(vec3(cell, 77.0));
                vec2 cen = cell + 0.3 + 0.4 * b.xy;
                vec2 world = cen - drift;
                vec2 upv = length(world) > 1e-3 ? -normalize(world) : vec2(0.0, 1.0);
                vec2 side = vec2(upv.y, -upv.x);
                float k = (0.5 + 0.5 * h.y) * mix(0.9, 1.3, cov);
                vec2 rel = ps - cen;
                vec2 q = vec2(dot(rel, side), dot(rel, upv)) / k;
                float d = max(cumulus(q, a, b, 0.0, 1.0), -(q.y + 0.08)) * k;
                if (d < best)
                {
                    best = d;
                    bq = q;
                    ba = a;
                    bb = b;
                }
            }
        }
        // A slightly brushy edge.
        best += (vnoise2(ps * 9.0) - 0.5) * 0.02;
        float dl = cumulus(bq, ba, bb, 0.1, 0.84);
        float dh = cumulus(bq, ba, bb, 0.15, 0.6);
        float w = fwidth(best) * 0.8 + 1e-4;
        float wl = fwidth(dl) * 0.8 + 1e-4;
        float a = 1.0 - smoothstep(-w, w, best);
        if (a > 0.0)
        {
            float lit = 1.0 - smoothstep(-wl, wl, dl);
            float hi = 1.0 - smoothstep(-wl, wl, dh);
            vec3 cc = mix(shd, mid, lit);
            vec3 crown = min(litC * vec3(1.02, 1.0, 0.92) + vec3(0.03, 0.02, 0.0) * (1.0 - oc), vec3(1.0));
            cc = mix(cc, mix(crown, mid, oc * 0.6), hi);   // a warm crown (dull under overcast)
            c = mix(c, cc, a * fade);
        }
    }

    outColor = vec4(c, 1.0);
}
