#version 450

// IsoRim 3D view: one fragment program for every IsoRim ShaderDef (Solid, SolidTex, Outline, Ghost,
// Blend, Overlay). Render state (blend, depth, cull, bias, queue) comes from the defs in
// Defs\ShaderDefs\IsoRim_Shaders.xml; the vertex stage is vanilla SceneDefault.vert.glsl, or
// IsoRim.vert.glsl (the same plus wall end lines and moving outlines) for Solid, LitMark and Outline.
// USE_TEXTURE is defined by the engine for defs with useTexture = true (IsoRimSolidTex).
//
// colour = vertex colour * _Color [* texture, cut out below alpha 0.5] * shade
// shade (_LightMode): 0 unlit; 1 model curve; 2 wall curve; 3 roof slates (model curve * 1.05, at most 1.1).
// The sun is IsoRim's own (C# R3DMaterials.SunDirAt), pushed every frame as the global _IsoSun.
// The face normal comes from screen derivatives of the world position in true (unsquashed) space,
// turned toward the camera, and snapped to the same 26 direction groups as the C# BinOf.
//
// Night, lamp and indoor light (_GlowMode): 0 none (the game's pulled lighting overlay darkens it);
// 1 the light of the face's OWN cell, from IsoRim's glow texture (the corner colours of the game's
// lighting overlay, one texel per cell corner) and the sky colour, by the game's LightOverlay
// formula, applied by a second "light" pass (_MarkPass 1, defs IsoRimLitMark*, queue 3099) that
// multiplies the colour already there like the game's overlay and sets stencil bit 32 so the
// pulled overlay skips the pixel; 2 sky colour only (roofs), in this pass; 3 own cell, in this
// pass (ghosts, drawn after the overlay).

#define QUANTIZE 1   // 1: snap normals to IsoRim's 26 direction groups (original look); 0: exact face normals

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec3 vNormal;     // roof clipping: x = 4 when clipped, y z = the owner cell (IsoRim.vert.glsl, IsoRimTex.vert.glsl); x + 10: may go see-through while building (BuildSee)
layout(location = 4) in vec3 vPosLocal;   // roof clipping: the position in roof space; both mean nothing else here

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _LightMode; // @default = 0.0
    vec4 _IsoSun;     // global, no default: xyz = direction toward IsoRim's sun (or moon)
    vec4 _IsoView;    // global, no default: xyz = true-space direction toward the camera, w = Squash
    vec4 _IsoSky;     // global, no default: rgb = the game's sky light colour (the LightOverlay _Color)
    vec4 _IsoGlowMap; // global, no default: (origin x, origin z, 1/(W+1), 1/(H+1)) of the glow texture
    float _GlowMode;  // @default = 0.0
    float _MarkPass;  // @default = 0.0     1 on the light-pass materials (IsoRimLitMark*)
    float _OutlinePush; // @default = 0.0   vertex stage only (IsoRim.vert.glsl): moving outlines
    vec4 _IsoCellMap; // global, no default: (origin x, origin z, W, H) of the cell texture (IsoCellMap)
    vec4 _IsoLevel;   // global, no default: x = the map's ground height in cells, y = what is left of a cut wall
    vec4 _IsoFloorGlow; // global, no default: stacked floors (Floors.GlowFloors): x = floors above the ground, y = storey height (true cells), z = rows from one floor to the next, w = 1 + the highest floor drawn solid (the slab clip); 0 without floors
    vec4 _IsoRoof;    // global, no default: roof and fog clipping (RoofClip): x = bits 1 roofs, 2 the fog, 4 slabs, y = wall height, z = Rise, w = Pitch
    vec4 _IsoEye;     // global, no default: xyz = the eye in drawn space, w = 1 with a perspective camera (View3D)
    vec4 _IsoSee;     // global, no default: see-through while building (BuildSee): x, y = the pointer (drawn x, z), z = the viewed floor's height (true cells), w = coverage at the pointer (0 = off)
    vec4 _IsoSee2;    // global, no default: x = radius of full coverage, y = radius where it ends (cells), z = dither cell (pixels)
    float _IsoOwnerFloor; // @default = 0.0   per draw (RoofClip.FloorBlock): 1 + the floor a moving model stands on; 0 = not given
    float _IsoFade;   // @default = 0.0   faded floors (FadeFloors): share of pixels kept, 0 = drawn solid
    float _IsoFadeFrom; // @default = 0.0   faded floors: where in the dither pattern (0-1) the kept share starts
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _IsoGlowTex;   // global: texel (x, z) = cell corner (x, z)
layout(set = 2, binding = 3) uniform sampler2D _IsoCellTex;   // @vertex @default = black   global: the cell texture, read by the vertex stages (shader cut-away) and the roof clipping
layout(set = 2, binding = 5) uniform sampler2D _IsoRoofTex;   // @default = black   global: the roof over each cell (RoofClip)

// Read by the vertex stages of instanced draws (IsoRim.vert.glsl, IsoRimTex.vert.glsl); declared here
// because the engine takes the material layout (and so this binding) from the fragment stage.
#ifdef INSTANCED
layout(std430, set = 2, binding = 4) readonly buffer _InstanceTransforms
{
    mat4 instanceTransforms[];
};
#endif

const float DEG = 0.017453292519943295;

// ---- Roof clipping (Source\RoofClip.cs). The same in IsoRimSolid.frag.glsl, IsoRimCardPlant.frag.glsl,
// IsoRimCardSnow.frag.glsl and IsoRimCardMark.frag.glsl (its light pass).
// _IsoRoofTex: one texel per cell, r = the lid drawn over it (1 built roof, 2 rock, 3 ship
// deck, 4 a floor's slab, 0 none), g = corner levels SW | SE << 4, b = NW | NE << 4 (built roofs). _IsoRoof: x & 1 while roofs are drawn,
// y = wall height, z = Rise, w = Pitch (SectionLayer_IsoRimRoofs). Roof space: x, z in map cells of the owner's floor,
// y in true cells above that floor. Kind 4 (RoofClip.Slab): a stacked floor stands over the cell; its slab clips (at
// its underside) while that floor is drawn solid (SlabShown), with the roofs shown or hidden (_IsoRoof.x & 4); kinds
// 1-3 clip only while the roofs are drawn (_IsoRoof.x & 1).
const float RoofOverhang = 0.07;   // SectionLayer_IsoRimRoofs.Overhang: the eave strip outside a built roof
const float RoofUnder = 0.012;     // the plain copy under a built roof's slates (SectionLayer_IsoRimRoofs.Facet)
const float RoofMargin = 0.004;
const float SlabThickness = 0.12;  // Floors.SlabThickness

ivec2 RoofTexel(ivec2 c, out bool inside)
{
    ivec2 size = ivec2(uMaterial._IsoCellMap.zw);
    c -= ivec2(floor(uMaterial._IsoCellMap.xy + 0.5));
    inside = c.x >= 0 && c.y >= 0 && c.x < size.x && c.y < size.y;
    return ivec2(c.x, size.y - 1 - c.y);   // rows stored north first
}

// Whether the slab over cell c clips: the floor one above c's own (by its row) is drawn solid, i.e. no higher than the
// highest solid floor (_IsoFloorGlow: z = rows from one floor to the next, w = 1 + that floor; 0 without floors).
bool SlabShown(ivec2 c)
{
    vec4 fg = uMaterial._IsoFloorGlow;
    if ((int(round(uMaterial._IsoRoof.x)) & 4) == 0 || fg.z < 0.5 || fg.w < 1.5)
        return false;
    return floor(float(c.y) / fg.z) + 1.0 < fg.w - 0.5;
}

// The lid kind over cell c (0 none or open, 1 built, 2 rock, 3 ship, 4 slab), with its texel.
int RoofLid(ivec2 c, bool openToo, out vec4 r)
{
    bool inside;
    ivec2 t = RoofTexel(c, inside);
    r = vec4(0.0);
    if (!inside)
        return 0;
    r = texelFetch(_IsoRoofTex, t, 0);
    int kind = int(round(r.r * 255.0));
    if (kind == 4)
        return SlabShown(c) ? 4 : 0;   // a floor's slab: never opened by the cut-away
    if (kind < 1 || kind > 3 || (int(round(uMaterial._IsoRoof.x)) & 1) == 0)
        return 0;
    if (!openToo && (int(round(texelFetch(_IsoCellTex, t, 0).a * 255.0)) & 2) != 0)
        return 0;   // opened by the cut-away: not drawn
    return kind;
}

// The underside of the lid of kind over a cell at f (0..1 across it), less the margin, in true cells.
float RoofUnderside(int kind, vec4 r, vec2 f)
{
    vec4 R = uMaterial._IsoRoof;
    if (kind == 2)
        return R.y - 0.005 - RoofMargin;   // rock: a flat lid just under the wall tops
    if (kind == 3)
        return R.y - RoofMargin;           // ship deck (ShipRoofs: wall tops + 0.005)
    if (kind == 4)
        return uMaterial._IsoFloorGlow.y - SlabThickness - RoofMargin;   // a floor's slab: storey height less its thickness
    int g = int(round(r.g * 255.0));
    int b = int(round(r.b * 255.0));
    float base = R.y + R.z - RoofUnder - RoofMargin;
    float sw = base + R.w * float(g & 15);
    float se = base + R.w * float(g >> 4);
    float nw = base + R.w * float(b & 15);
    float ne = base + R.w * float(b >> 4);
    float u = f.x, v = f.y;
    // Split along the same diagonal as the drawn facets.
    if (abs(sw - ne) >= abs(nw - se))
        return v > u ? sw + (ne - nw) * u + (nw - sw) * v : sw + (se - sw) * u + (ne - se) * v;
    return u + v < 1.0 ? sw + (se - sw) * u + (nw - sw) * v : ne + (ne - nw) * (u - 1.0) + (ne - se) * (v - 1.0);
}

float RoofEave(ivec2 n, vec2 f)
{
    vec4 r;
    if (RoofLid(n, false, r) != 1)
        return 1e9;
    return RoofUnderside(1, r, f) - RoofOverhang * uMaterial._IsoRoof.w;   // the eave slopes down by this
}

// ---- Fog clipping (RoofClip.cs, the fog; _IsoRoof.x & 2). The same in the three files. _IsoCellTex: a & 4 = the cell
// is fogged in the game's fog grid, r, g = its top (16 bits, 1/4096 cells), b = how far its block is cut down.
const float FogAbove = 0.012;   // IsoRimFog.vert.glsl Above: the lid's lift over the tops

// The fog lid over cell c (the cell's top as the cut-away leaves it), or very high where the cell is not fogged.
float FogLid(ivec2 c)
{
    bool inside;
    ivec2 t = RoofTexel(c, inside);
    if (!inside)
        return 1e9;
    vec4 o = texelFetch(_IsoCellTex, t, 0);
    if ((int(round(o.a * 255.0)) & 4) == 0)
        return 1e9;
    float full = (round(o.r * 255.0) * 256.0 + round(o.g * 255.0)) / 4096.0;
    return full * (1.0 - (1.0 - uMaterial._IsoLevel.y) * o.b) + FogAbove;
}

// True: a fragment at p (roof space) of a thing standing in cell owner is above the fog lid over its own cell, or the
// thing stands in a fogged cell and the fragment is above that cell's lid (the game draws no fogged things).
bool FogHides(vec3 p, vec2 owner)
{
    return p.y > min(FogLid(ivec2(floor(p.xz))), FogLid(ivec2(floor(owner))));
}

// True: a fragment of a thing standing in cell owner, at p (roof space), is above the underside of a roof drawn over it,
// or above the fog lid (FogHides). roofs false (RoofClip.ThroughRoof, Api 31): built, rock and ship roofs (kinds 1-3)
// leave it alone; the fog and a floor's slab still clip it.
bool RoofHides(vec3 p, vec2 owner, bool roofs)
{
    int mode = int(round(uMaterial._IsoRoof.x));
    if ((mode & 2) != 0 && FogHides(p, owner))
        return true;
    if ((mode & 5) == 0)
        return false;   // neither roofs nor slabs clip
    if (!roofs)
    {
        vec4 rs;
        if (RoofLid(ivec2(floor(owner)), true, rs) != 4)
            return false;   // not under a shown slab
        ivec2 cs = ivec2(floor(p.xz));
        return RoofLid(cs, false, rs) == 4 && p.y > RoofUnderside(4, rs, p.xz - vec2(cs));
    }
    vec4 r;
    if (RoofLid(ivec2(floor(owner)), true, r) == 0)
        return false;   // only things that stand under a roof (open or not) are clipped
    ivec2 c = ivec2(floor(p.xz));
    vec2 f = p.xz - vec2(c);
    int kind = RoofLid(c, false, r);
    if (kind != 0)
        return p.y > RoofUnderside(kind, r, f);
    // Outside the roof: only the eave strip of a built roof beside it.
    float h = 1e9;
    if (f.x < RoofOverhang)
        h = min(h, RoofEave(c + ivec2(-1, 0), vec2(1.0, f.y)));
    if (f.x > 1.0 - RoofOverhang)
        h = min(h, RoofEave(c + ivec2(1, 0), vec2(0.0, f.y)));
    if (f.y < RoofOverhang)
        h = min(h, RoofEave(c + ivec2(0, -1), vec2(f.x, 1.0)));
    if (f.y > 1.0 - RoofOverhang)
        h = min(h, RoofEave(c + ivec2(0, 1), vec2(f.x, 0.0)));
    return p.y > h;
}

// ---- See-through while building (Source\BuildSee.cs): a fragment of something tagged (vNormal.x + 10) at or above
// the viewed floor is dropped where the 4 x 4 ordered dither pattern, fixed on the screen, is below the coverage at its
// spot (full within _IsoSee2.x cells of the pointer on the ground plane, nothing from _IsoSee2.y). Every layer drops the
// same pixels, so what is behind shows through all of them.
const float Bayer4[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);

bool SeeThrough(float trueY)
{
    vec4 s = uMaterial._IsoSee;
    if (s.w <= 0.0 || trueY - uMaterial._IsoLevel.x < s.z + 0.03)
        return false;
    vec4 r = uMaterial._IsoSee2;
    float cover = s.w * (1.0 - smoothstep(r.x, r.y, distance(vWorldPos.xz, s.xy)));
    if (cover <= 0.0)
        return false;
    ivec2 q = ivec2(floor(gl_FragCoord.xy / max(1.0, r.z))) & 3;
    return (Bayer4[q.y * 4 + q.x] + 0.5) / 16.0 < cover;
}

vec3 Dir(float azimuthDeg, float elevationDeg)
{
    float az = azimuthDeg * DEG;
    float el = elevationDeg * DEG;
    return vec3(sin(az) * cos(el), sin(el), cos(az) * cos(el));
}

// R3DMaterials.BinOf + MakeBinNormals: straight up, straight down, or one of 8 compass sectors
// at 0, 35 or 62 degrees of elevation.
// The normal comes from screen derivatives and wobbles a little from pixel to pixel, so a face lying
// exactly on a group edge flips between two groups in stripes and speckles. Models are full of such
// faces (rings of 16 or 32 sides put sides at 22.5 degrees, bevels at y 0.65, 30-degree undersides
// at -0.5, 22.5-degree slopes at 0.924), so the edges sit a little off those round values: compass
// edges at 21 + 45k degrees, steepness edges at y 0.235 / 0.635 / 0.90, down at -0.485. Keep
// R3DMaterials.BinOf in step.
vec3 BinNormal(vec3 n)
{
    if (n.y >= 0.90)
        return vec3(0.0, 1.0, 0.0);
    if (n.y <= -0.485)
        return vec3(0.0, -1.0, 0.0);
    float elevation = n.y < 0.235 ? 0.0 : (n.y < 0.635 ? 35.0 : 62.0);
    float az = atan(n.x, n.z) / DEG;
    az = az - 360.0 * floor(az / 360.0);
    float sector = mod(floor((az + 1.5) / 45.0 + 0.5), 8.0);
    return Dir(sector * 45.0, elevation);
}

// R3DMaterials.ModelShade: 1 on top by day, 0.5 facing away from the sun.
float ModelShade(vec3 n, vec3 L)
{
    float d = max(0.0, dot(n, L));
    return 0.5 + 0.5 * clamp(d / max(0.2, L.y), 0.0, 1.0);
}

// R3DMaterials.WallShadeFor (curve 1): 1 on top and underneath, 0.52 to 0.80 round the sides.
float WallShade(vec3 n, vec3 L)
{
    if (n.y >= 0.92)
        return 1.0;
    vec2 o = n.xz;
    if (dot(o, o) < 1e-6)
        return 1.0;
    vec2 sunFlat = L.xz;
    sunFlat = dot(sunFlat, sunFlat) < 1e-6 ? vec2(0.0, -1.0) : normalize(sunFlat);
    return mix(0.52, 0.80, (dot(normalize(o), sunFlat) + 1.0) * 0.5);
}

// The game's LightOverlay.frag.glsl as a multiplier: L = sky * (1 - cover) + glow, with its clamps.
vec3 GlowLight(vec4 g)
{
    vec3 s = uMaterial._IsoSky.rgb * (1.0 - g.a);
    vec3 L = s + g.rgb;
    float clampBase = 1.5 - 0.5 * min(s.r, s.b);
    if (L.r > clampBase) L *= clampBase / L.r;
    if (L.g > clampBase) L *= clampBase / L.g;
    if (L.b > clampBase) L *= clampBase / L.b;
    return L;
}

// ---- Faded floors (Source/FadeFloors.cs, prototype): a fragment of a floor above the viewed one is dropped where the
// 4 x 4 ordered dither pattern, fixed on the screen, is outside the floor's kept range: _IsoFade wide (0 = drawn solid), from
// _IsoFadeFrom, wrapping round (floors with overlapping ranges cover each other only there).
const float FadeBayer[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);

bool FadeDrop()
{
    float keep = uMaterial._IsoFade;
    if (keep <= 0.0)
        return false;
    ivec2 q = ivec2(floor(gl_FragCoord.xy)) & 3;
    return fract((FadeBayer[q.y * 4 + q.x] + 0.5) / 16.0 - uMaterial._IsoFadeFrom) >= keep;
}

void main()
{
    // Derivatives first, in uniform control flow (before any discard).
    float squash = uMaterial._IsoView.w;
    vec3 p = vec3(vWorldPos.x, vWorldPos.y * squash, vWorldPos.z);
    vec3 n = cross(dFdx(p), dFdy(p));
    float len = length(n);
    n = len > 1e-20 ? n / len : vec3(0.0, 1.0, 0.0);
    if (dot(n, uMaterial._IsoView.xyz) < 0.0)
        n = -n;
    vec3 faceNormal = n;   // the exact face normal, turned toward the camera
    if (FadeDrop())
        discard;

    float tagged = vNormal.x;
    if (tagged > 9.0)
    {
        tagged -= 10.0;
        if (SeeThrough(p.y))
            discard;
    }
    // Roof clipping: whatever of a thing under a drawn roof rises above its underside is hidden by the roof anyway.
    if (tagged > 3.0 && RoofHides(vPosLocal, vNormal.yz, tagged < 4.5))
        discard;

    vec4 color = vColor * uMaterial._Color;
#ifdef USE_TEXTURE
    color *= texture(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy);
    if (color.a < 0.5)
        discard;
#endif
    // Light pass: where an upright face meets the ground, face and ground have the same depth on
    // the foot line and the ground can win there, so the bottom row of pixels is left to the
    // overlay (else that ground pixel would take the face's light instead). Ground at height 0.
    bool lightPass = uMaterial._MarkPass > 0.5;
    if (lightPass && squash > 0.0 && abs(faceNormal.y) < 0.92 && p.y < 0.002)
        discard;

    float glowMode = uMaterial._GlowMode;
    vec3 glowLight = vec3(1.0);
    if (lightPass || glowMode > 1.5)
    {
        vec4 g = vec4(0.0);                      // mode 2 (roofs): sky only
        if (lightPass || glowMode > 2.5)
        {
            // Upright faces read a little outward along their own normal, so a wall face takes the
            // corners on its own side of the wall (the game leaves walls out of those averages).
            vec2 xz = vWorldPos.xz;
            if (squash > 0.0 && abs(faceNormal.y) < 0.92)
                xz += faceNormal.xz * 0.1;
            // Stacked floors (Floors.GlowFloors): an upper floor is drawn over the ground rows, but its cells and
            // their light are rows further north. What is at or above floor k's height takes the light of floor k's
            // cell over that spot (the ground's walls end 0.02 below the first floor; nothing reaches through a floor).
            vec4 fg = uMaterial._IsoFloorGlow;
            if (fg.x > 0.5 && fg.y > 0.0)
            {
                xz.y += clamp(floor((p.y + 0.01) / fg.y), 0.0, fg.x) * fg.z;
            }
            vec4 gm = uMaterial._IsoGlowMap;
            vec2 uv = vec2((xz.x - gm.x + 0.5) * gm.z, 1.0 - (xz.y - gm.y + 0.5) * gm.w);
            g = textureLod(_IsoGlowTex, uv, 0.0);
        }
        glowLight = GlowLight(g);
        if (lightPass)
        {
            // Blend DstColor/SrcColor: the colour already there times 2 * this, as the game's overlay.
            outColor = vec4(glowLight * 0.5, 1.0);
            return;
        }
    }

    float mode = uMaterial._LightMode;
    float shade = 1.0;
    if (mode > 0.5 && squash > 0.0)
    {
#if QUANTIZE
        n = BinNormal(n);
#endif
        vec3 L = uMaterial._IsoSun.xyz;
        if (mode < 1.5)
            shade = ModelShade(n, L);
        else if (mode < 2.5)
            shade = WallShade(n, L);
        else
            shade = min(1.1, ModelShade(n, L) * 1.05);
    }
    color.rgb *= shade;

    if (glowMode > 1.5)
    {
        color.rgb = min(color.rgb, vec3(1.0));   // the 8-bit colour the overlay would multiply
        color.rgb *= glowLight;
    }
    outColor = color;
}
