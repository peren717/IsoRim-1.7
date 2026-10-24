#version 450

// IsoRim 3D view: vertex stage of the IsoRimSolidTex and IsoRimLitMarkTex defs. The game's
// SceneDefault.vert.glsl unchanged, plus an instanced variant (INSTANCED, compiled by the engine for
// DrawMeshInstanced: the same textured part of many doors, animals or people at once, PartBatches),
// which takes each copy's matrix from _InstanceTransforms (binding 4, also declared in
// IsoRimSolid.frag.glsl, where the engine reads the material layout from).

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 1.0, 0.0, 0.0)
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)
layout(location = 4) in vec4 inUv1; // @vertex TexCoord1 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

#ifdef INSTANCED
layout(std430, set = 2, binding = 4) readonly buffer _InstanceTransforms
{
    mat4 instanceTransforms[];
};
#endif

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) out vec3 vNormal;
layout(location = 4) out vec3 vPosLocal;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

// Must match IsoRimSolid.frag.glsl exactly (the layout is read from the fragment stage).
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _LightMode; // @default = 0.0
    vec4 _IsoSun;     // global, no default
    vec4 _IsoView;    // global, no default: xyz = true-space direction toward the camera, w = Squash
    vec4 _IsoSky;     // global, no default
    vec4 _IsoGlowMap; // global, no default
    float _GlowMode;  // @default = 0.0
    float _MarkPass;  // @default = 0.0
    float _OutlinePush; // @default = 0.0   vertex stage only (IsoRim.vert.glsl): moving outlines
    vec4 _IsoCellMap; // global, no default: (origin x, origin z, W, H) of the cell texture (IsoCellMap)
    vec4 _IsoLevel;   // global, no default: x = the map's ground height in cells, y = what is left of a cut wall
    vec4 _IsoFloorGlow; // global, no default: stacked floors (Floors.GlowFloors): x = floors above the ground, y = storey height (true cells), z = rows from one floor to the next, w = 1 + the highest floor drawn solid (the slab clip); 0 without floors
    vec4 _IsoRoof;    // global, no default: roof and fog clipping (RoofClip): x = bits 1 roofs, 2 the fog, 4 slabs, y = wall height, z = Rise, w = Pitch
    vec4 _IsoEye;     // global, no default: xyz = the eye in drawn space, w = 1 with a perspective camera (View3D)
    vec4 _IsoSee;     // global, no default: see-through while building (BuildSee): x, y = the pointer (drawn x, z), z = the viewed floor's height (true cells), w = coverage at the pointer (0 = off)
    vec4 _IsoSee2;    // global, no default: x = radius of full coverage, y = radius where it ends (cells), z = dither cell (pixels)
    float _IsoOwnerFloor; // @default = 0.0   per draw (RoofClip.FloorBlock): 1 + the floor a moving model stands on; 0 = not given
} uMaterial;

// ---- Step 7: the cut-away in the vertex stage (C# ShaderCut, Source\ShaderCut.cs). IsoRim's walls, damage,
// trees and roofs layers carry in texture coordinate 1 the cell a vertex belongs to (x + 0.5, z + 0.5) and what may
// happen to it (z, flags); the map's cell texture (IsoCellMap) says, per cell, the full top height (r, g), how far the
// cell's block is cut down (b) and whether its tree is thinned (a & 1) or its roof open (a & 2). _IsoLevel.y = the part
// of a cut wall that is left (cutHeight).
layout(set = 2, binding = 3) uniform sampler2D _IsoCellTex;

const ivec2 CutDirs[9] = ivec2[9](ivec2(0, 0), ivec2(0, -1), ivec2(0, 1), ivec2(-1, 0), ivec2(1, 0),
                                  ivec2(-1, -1), ivec2(1, -1), ivec2(-1, 1), ivec2(1, 1));
const float CutFoot = 0.06;   // cells: below this nothing moves (the dark line along the ground)

vec4 CutCell(ivec2 c)
{
    ivec2 size = ivec2(uMaterial._IsoCellMap.zw);
    c -= ivec2(floor(uMaterial._IsoCellMap.xy + 0.5));
    if (c.x < 0 || c.y < 0 || c.x >= size.x || c.y >= size.y)
    {
        return vec4(0.0);
    }
    return texelFetch(_IsoCellTex, ivec2(c.x, size.y - 1 - c.y), 0);   // rows stored north first
}

float CutFull(vec4 t)
{
    return (round(t.r * 255.0) * 256.0 + round(t.g * 255.0)) / 4096.0;
}

float CutTop(vec4 t, float cut)
{
    return CutFull(t) * (1.0 - (1.0 - uMaterial._IsoLevel.y) * cut);
}

// False: the vertex is hidden (a leaf of a thinned tree, an open roof, a side against a block that is not lower).
// Otherwise a vertex of a cut block above CutFoot is pressed down so the top lands at the cut height (and a textured
// side's v follows, so the pattern is cut off rather than squeezed, as when the wall was baked lower).
bool CutAway(inout vec3 pos, inout vec2 uv)
{
    int flags = int(inUv1.z + 0.5);
    if (flags == 0 || uMaterial._IsoCellMap.z < 1.0)
    {
        return true;
    }
    ivec2 own = ivec2(floor(inUv1.xy));
    vec4 o = CutCell(own);
    int bits = int(round(o.a * 255.0));
    if ((flags & 64) != 0 && (bits & 1) != 0)
        return false;
    if ((flags & 128) != 0 && (bits & 1) == 0)
        return false;
    if ((flags & 256) != 0 && (bits & 2) != 0)
        return false;
    float cut = o.b;
    int second = (flags >> 9) & 15;
    if (second != 0)
    {
        cut = max(cut, CutCell(own + CutDirs[second]).b);
    }
    float full = CutFull(o);
    if ((flags & 65536) != 0 && cut > 0.0)
        return false;   // (Api 11) a wall top's crown (WallTops.cs, ShaderCut.Crown): hidden while its wall is cut down
    int cond = (flags >> 1) & 15;
    if (cond != 0)
    {
        vec4 n = CutCell(own + CutDirs[cond]);
        if (CutTop(n, n.b) >= CutTop(o, cut) - 0.01)
            return false;
    }
    float squash = uMaterial._IsoView.w;
    if ((flags & 1) != 0 && cut > 0.0 && full > CutFoot + 0.01 && squash > 0.0)
    {
        float yc = pos.y * squash - uMaterial._IsoLevel.x;
        if (yc > CutFoot)
        {
            float s = max(0.0, (full * uMaterial._IsoLevel.y - CutFoot) / (full - CutFoot));
            float yn = mix(yc, CutFoot + (yc - CutFoot) * s, cut);
            if ((flags & 32) != 0)
                uv.y *= yn / yc;
            pos.y = (yn + uMaterial._IsoLevel.x) / squash;
        }
    }
    return true;
}


// ---- Roof clipping (Source\RoofClip.cs): which cell's roof clips this vertex (texture coordinate 1 flags: 8192 =
// baked, the cell in x, y and the mesh position already in that cell's map space; 16384 = a moving model, the cell
// where the matrix puts the origin) and where the vertex is in that roof's space (x, true height, z, on the owner's
// floor). Handed to IsoRimSolid.frag.glsl in vNormal (x = 4, y z = the cell) and vPosLocal, which it reads only then.
bool RoofSpace(vec3 local, vec3 world, vec3 origin, out vec2 owner, out vec3 roofPos)
{
    owner = vec2(0.0);
    roofPos = local;
    float squash = uMaterial._IsoView.w;
    if (uMaterial._IsoRoof.x < 0.5 || squash <= 0.0)
        return false;
    int flags = int(inUv1.z + 0.5);
    if ((flags & 8192) != 0)
    {
        owner = inUv1.xy;
        roofPos = vec3(local.x, local.y * squash - uMaterial._IsoLevel.x, local.z);
        return true;
    }
    if ((flags & 16384) != 0)
    {
        // Stacked floors (Floors.GlowFloors): a thing on floor k is clipped in that floor's rows (further north, as the
        // light pass reads them). k is the floor of the cell the thing stands in, given per draw (_IsoOwnerFloor,
        // DynamicModels.Submit); only without it is it guessed from the height of the model's origin, which a pose
        // can put under the floor (a work stroke's dip, a lying animal) or above the next one (a tall animal's parts).
        vec4 fg = uMaterial._IsoFloorGlow;
        float k = 0.0;
        if (fg.x > 0.5 && fg.y > 0.0)
            k = uMaterial._IsoOwnerFloor > 0.5 ? clamp(uMaterial._IsoOwnerFloor - 1.0, 0.0, fg.x)
                : clamp(floor((origin.y * squash - uMaterial._IsoLevel.x + 0.01) / fg.y), 0.0, fg.x);
        owner = vec2(origin.x, origin.z + k * fg.z);
        roofPos = vec3(world.x, world.y * squash - uMaterial._IsoLevel.x - k * fg.y, world.z + k * fg.z);
        return true;
    }
    return false;
}

void main()
{
    vec3 pos = inPosition;
    vec2 uv = inUv0.xy;
    bool keep = CutAway(pos, uv);
#ifdef INSTANCED
    mat4 model = instanceTransforms[gl_InstanceIndex];
#else
    mat4 model = push.modelMatrix;
#endif
    vec4 world = model * vec4(pos, 1.0);
    vec2 roofOwner;
    vec3 roofPos;
    bool roofClip = RoofSpace(pos, world.xyz, model[3].xyz, roofOwner, roofPos);
    gl_Position = keep ? uVertex.ViewProj * world : vec4(2.0, 2.0, 0.5, 1.0);   // hidden: outside the view, all corners alike

    vPosLocal = roofClip ? roofPos : pos;
    vColor = inColor;
    vUv = uv;
    vWorldPos = world.xyz;
    // See-through while building (BuildSee, ShaderCut.See = bit 15): vNormal.x + 10 tells IsoRimSolid.frag.glsl.
    // Through roofs (RoofClip.ThroughRoof = bit 16, Api 31): x = 5 instead of 4 (only the fog and floor slabs clip).
    bool see = (int(inUv1.z + 0.5) & 32768) != 0;
    float clipCode = (int(inUv1.z + 0.5) & 65536) != 0 ? 5.0 : 4.0;
    vNormal = roofClip ? vec3(see ? clipCode + 10.0 : clipCode, roofOwner) : (see ? vec3(10.0, 0.0, 0.0) : inNormal);
}
