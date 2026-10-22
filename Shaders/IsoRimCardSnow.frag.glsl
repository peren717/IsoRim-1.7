#version 450

// IsoRim: the game's Data\Core\Shaders\Map\TransparentPlant.frag.glsl (1.7.4976) unchanged apart from two global
// members after _SwayHead that IsoRimCard.vert.glsl reads (the material layout is read from this
// stage). Compare with the game's file after a game update.

layout(location = 1) in vec2 vUv;
layout(location = 3) flat in vec2 vRoofOwner;   // roof clipping (IsoRimCard.vert.glsl)
layout(location = 4) in vec3 vRoofPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0) (unused, kept for layout)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _SwayHead;  // @default = 0.0 (consumed by PlantWindSway.vert, kept for layout)
    vec4 _IsoView;    // global, no default: w = Squash (read by IsoRimCard.vert.glsl)
    vec4 _IsoCard;    // global, no default: sin a, cos a (a = pitch - 90), sin yaw, cos yaw
    vec4 _IsoCellMap; // global, no default: (origin x, origin z, W, H) of the cell texture (IsoCellMap)
    vec4 _IsoRoof;    // global, no default: roof and fog clipping (RoofClip): x = bits 1 roofs, 2 the fog, 4 slabs, y = wall height, z = Rise, w = Pitch
    vec4 _IsoFloorGlow; // global, no default: stacked floors (Floors.GlowFloors): z = rows from one floor to the next, y = storey height, w = 1 + the highest floor drawn solid (the slab clip); 0 without floors
    float _IsoFade;   // @default = 0.0   faded floors (FadeFloors): share of pixels kept, 0 = drawn solid
    float _IsoFadeFrom; // @default = 0.0   faded floors: where in the dither pattern (0-1) the kept share starts
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 3) uniform sampler2D _IsoCellTex;   // @default = black   global: the cell texture (IsoCellMap)
layout(set = 2, binding = 4) uniform sampler2D _IsoRoofTex;   // @default = black   global: the roof over each cell (RoofClip)

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

// The fog lid over cell c (the cell's full top (no cut-away level here: a cut block's cell holds no plant)), or very high where the cell is not fogged.
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
    return full + FogAbove;
}

// True: a fragment at p (roof space) of a thing standing in cell owner is above the fog lid over its own cell, or the
// thing stands in a fogged cell and the fragment is above that cell's lid (the game draws no fogged things).
bool FogHides(vec3 p, vec2 owner)
{
    return p.y > min(FogLid(ivec2(floor(p.xz))), FogLid(ivec2(floor(owner))));
}

// True: a fragment of a thing standing in cell owner, at p (roof space), is above the underside of a roof drawn over it,
// or above the fog lid (FogHides).
bool RoofHides(vec3 p, vec2 owner)
{
    int mode = int(round(uMaterial._IsoRoof.x));
    if ((mode & 2) != 0 && FogHides(p, owner))
        return true;
    if ((mode & 5) == 0)
        return false;   // neither roofs nor slabs clip
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
    if (FadeDrop())
        discard;
    if (RoofHides(vRoofPos, vRoofOwner))
        discard;
    outColor = texture(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy);
}
