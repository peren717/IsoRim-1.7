using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 52) Shapes without a model file, for things no one has made a model for (ModelDef.shape; the dev-mode binder's
    // shape row). Raised needs no shape: it is the automatic look of a def with no model, whose height, offset, outline and
    // groundShadow the bound def sets (Forms, Extruder).
    public enum ModelShape
    {
        None,
        Card,   // the sprite stood up facing the camera (ShapeCards)
        Box     // a box of the thing's footprint with sprites on its faces (ShapeBoxes)
    }

    public enum BoxSide
    {
        Top,
        North,
        East,
        South,
        West
    }

    public enum BoxLook
    {
        Sprite,        // the sprite the thing shows now, whole (its empty margin left out)
        SpriteNorth,   // the sprite of that facing (Graphic_Multi), whole
        SpriteEast,
        SpriteSouth,
        SpriteWest,
        Edge,          // sides only: a strip of the sprite along that side, stretched up the face, as Raised's sides
        MeanColour,    // the sprite's mean colour, no sprite
        None           // left open
    }

    // One entry of ModelDef.boxFaces: <li><side>North</side><look>None</look></li>. A side not listed shows its Sprite.
    public class BoxFace
    {
        public BoxSide side;
        public BoxLook look;
        public bool mirror = false;   // the sprite flipped left to right
    }

    // A Box is an ordinary generated model (ModelLibrary.MakeEntry): outline, ground shading, ghost, selection, rotation and
    // multi-cell fit are the model path's. Its faces are plain, in the sprite's mean colour (spriteTint), which shows through
    // the sprite's see-through pixels; the sprites are textured quads over them, written after the model (ModelBaker) so
    // they are lit and clipped under roofs as a model's textured faces are. Faces belong to the box and turn with it.
    public static class ShapeBoxes
    {
        public const float DefaultHeight = 0.6f;
        private const float Wall = 0.04f;        // an open box's wall thickness, cells
        private const float FaceLift = 0.004f;   // a sprite's distance in front of its face, cells
        private const float EdgeStrip = 0.05f;   // the share of the sprite an Edge face shows
        private const float EdgeInside = 0.1f;   // how far in from the paint's edge it is taken (the sprite's dark outline stays out)
        private const float InnerShade = 0.6f;
        private static readonly Color32 White = new Color32(255, 255, 255, 255);

        public static float HeightOf(ModelDef md)
        {
            return md.height > 0f ? md.height : DefaultHeight;
        }

        public static BoxLook LookOf(ModelDef md, BoxSide side, out bool mirror)
        {
            mirror = false;
            if (md.boxFaces != null)
            {
                for (int i = 0; i < md.boxFaces.Count; i++)
                {
                    BoxFace f = md.boxFaces[i];
                    if (f != null && f.side == side)
                    {
                        mirror = f.mirror;
                        return f.look;
                    }
                }
            }
            return BoxLook.Sprite;
        }

        // The box's half size. builtFor is the footprint (the binder sets it); the model is then not stretched (ModelEntry.Fit).
        private static void Half(ModelDef md, out float hx, out float hz)
        {
            hx = md.builtFor.x > 0f ? md.builtFor.x / 2f : 0.5f;
            hz = md.builtFor.y > 0f ? md.builtFor.y / 2f : 0.5f;
        }

        // The top's inset, kept short of closing the top. scale divides out: Height and Top inset are cells on the map.
        private static float InsetOf(ModelDef md, float hx, float hz)
        {
            return Mathf.Clamp(md.topInset, 0f, Mathf.Max(0f, Mathf.Min(hx, hz) - 0.02f));
        }

        private static float ModelHeight(ModelDef md)
        {
            return HeightOf(md) / Mathf.Max(0.01f, md.scale);
        }

        // The corners of a side seen from outside: bottom left, top left, top right, bottom right (clockwise, the game's
        // front faces). Model space: +x east, +z north. inset: the top's; d: moved in from the box, for an open box's inside.
        private static void Corners(BoxSide side, float hx, float hz, float y0, float y1, float inset, Vector3[] q)
        {
            float tx = hx - inset;
            float tz = hz - inset;
            switch (side)
            {
                case BoxSide.North:
                    q[0] = new Vector3(hx, y0, hz);
                    q[1] = new Vector3(tx, y1, tz);
                    q[2] = new Vector3(-tx, y1, tz);
                    q[3] = new Vector3(-hx, y0, hz);
                    break;
                case BoxSide.East:
                    q[0] = new Vector3(hx, y0, -hz);
                    q[1] = new Vector3(tx, y1, -tz);
                    q[2] = new Vector3(tx, y1, tz);
                    q[3] = new Vector3(hx, y0, hz);
                    break;
                case BoxSide.South:
                    q[0] = new Vector3(-hx, y0, -hz);
                    q[1] = new Vector3(-tx, y1, -tz);
                    q[2] = new Vector3(tx, y1, -tz);
                    q[3] = new Vector3(hx, y0, -hz);
                    break;
                case BoxSide.West:
                    q[0] = new Vector3(-hx, y0, hz);
                    q[1] = new Vector3(-tx, y1, tz);
                    q[2] = new Vector3(-tx, y1, -tz);
                    q[3] = new Vector3(-hx, y0, -hz);
                    break;
                default:
                    q[0] = new Vector3(-tx, y1, -tz);
                    q[1] = new Vector3(-tx, y1, tz);
                    q[2] = new Vector3(tx, y1, tz);
                    q[3] = new Vector3(tx, y1, -tz);
                    break;
            }
        }

        // The box of a def: closed, or with walls where a side is left open, so it stays one closed shell for its outline.
        public static Model Build(ModelDef md)
        {
            float hx, hz;
            Half(md, out hx, out hz);
            float h = ModelHeight(md);
            float inset = InsetOf(md, hx, hz);
            bool[] open = new bool[5];
            bool anyOpen = false;
            for (int s = 0; s < 5; s++)
            {
                bool mirror;
                open[s] = LookOf(md, (BoxSide)s, out mirror) == BoxLook.None;
                anyOpen |= open[s];
            }
            List<Vector3[]> quads = new List<Vector3[]>();
            List<Color> colors = new List<Color>();
            float ix = Mathf.Max(0.01f, hx - Wall);
            float iz = Mathf.Max(0.01f, hz - Wall);
            float iy0 = Mathf.Min(Wall, h * 0.25f);
            float iy1 = Mathf.Max(iy0 + 0.01f, h - Wall);
            for (int s = 0; s < 5; s++)
            {
                Vector3[] outer = new Vector3[4];
                Corners((BoxSide)s, hx, hz, 0f, h, inset, outer);
                if (!anyOpen)
                {
                    quads.Add(outer);
                    colors.Add(Color.white);
                    continue;
                }
                Vector3[] inner = new Vector3[4];
                Corners((BoxSide)s, ix, iz, iy0, iy1, inset, inner);
                if (open[s])
                {
                    for (int k = 0; k < 4; k++)   // the rim round the opening
                    {
                        int n = (k + 1) % 4;
                        quads.Add(new[] { outer[k], outer[n], inner[n], inner[k] });
                        colors.Add(Color.white);
                    }
                }
                else
                {
                    quads.Add(outer);
                    colors.Add(Color.white);
                    quads.Add(new[] { inner[3], inner[2], inner[1], inner[0] });   // its inside
                    colors.Add(Color.white * InnerShade);
                }
            }
            // the bottom: on the ground, out of sight (and the floor of an open box)
            quads.Add(new[] { new Vector3(-hx, 0f, hz), new Vector3(-hx, 0f, -hz), new Vector3(hx, 0f, -hz), new Vector3(hx, 0f, hz) });
            colors.Add(Color.white);
            if (anyOpen)
            {
                quads.Add(new[] { new Vector3(-ix, iy0, -iz), new Vector3(-ix, iy0, iz), new Vector3(ix, iy0, iz), new Vector3(ix, iy0, -iz) });
                colors.Add(Color.white * InnerShade);
            }
            return ObjLoader.FromQuads(quads, colors);
        }

        // The part of a texture with paint on it: u0, v0, u1, v1; the whole texture if it cannot be read.
        private static Vector4 Painted(Texture tex)
        {
            Extruder.Mask mask = Extruder.MaskOf(tex, Vector2.one, Vector2.zero);
            if (mask == null || mask.lids.Count == 0)
            {
                return new Vector4(0f, 0f, 1f, 1f);
            }
            Vector4 b = new Vector4(1f, 1f, 0f, 0f);
            for (int i = 0; i < mask.lids.Count; i++)
            {
                Vector4 l = mask.lids[i];
                b.x = Mathf.Min(b.x, l.x);
                b.y = Mathf.Min(b.y, l.y);
                b.z = Mathf.Max(b.z, l.z);
                b.w = Mathf.Max(b.w, l.w);
            }
            return b.z > b.x && b.w > b.y ? b : new Vector4(0f, 0f, 1f, 1f);
        }

        private static Rot4 ArtOf(BoxLook look, Rot4 now)
        {
            switch (look)
            {
                case BoxLook.SpriteNorth:
                    return Rot4.North;
                case BoxLook.SpriteEast:
                    return Rot4.East;
                case BoxLook.SpriteSouth:
                    return Rot4.South;
                case BoxLook.SpriteWest:
                    return Rot4.West;
                default:
                    return now;
            }
        }

        private static readonly Dictionary<string, Material> faceMaterials = new Dictionary<string, Material>();
        private static readonly int maskId = Shader.PropertyToID("_MaskTex");
        private static readonly int colorTwoId = Shader.PropertyToID("_ColorTwo");

        // The lit textured material of a sprite: its texture coloured as the game's shader colours it. Null if it cannot be read.
        private static Material FaceMaterial(Material sprite)
        {
            Texture tex = sprite != null ? sprite.mainTexture : null;
            if (tex == null)
            {
                return null;
            }
            Texture mask = sprite.shader == ShaderDatabase.CutoutComplex && sprite.HasProperty(maskId) ? sprite.GetTexture(maskId) : null;
            if (mask == null)
            {
                return R3DMaterials.LitTexture(tex as Texture2D, "boxface" + tex.GetInstanceID(), sprite.color);
            }
            Color two = sprite.HasProperty(colorTwoId) ? sprite.GetColor(colorTwoId) : Color.white;
            Color32 c1 = sprite.color, c2 = two;
            string key = tex.GetInstanceID() + "|" + mask.GetInstanceID() + "|" + c1.r + "," + c1.g + "," + c1.b + "|" + c2.r + "," + c2.g + "," + c2.b;
            Material m;
            if (!faceMaterials.TryGetValue(key, out m))
            {
                Texture2D made = null;
                try
                {
                    made = Masked(tex, mask, sprite.color, two);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Preparing a box face's texture failed (" + tex.name + "): " + e, 93619521);
                }
                m = made != null ? R3DMaterials.LitTexture(made, "boxface" + key, Color.white) : null;
                faceMaterials[key] = m;
            }
            return m;
        }

        // CutoutComplex on the CPU: the first colour where the mask is red, the second where it is green, each by its amount.
        private static Texture2D Masked(Texture tex, Texture mask, Color one, Color two)
        {
            int w, h, mw, mh;
            Color32[] src = SpritePixels.Full(tex, out w, out h);
            Color32[] mpx = SpritePixels.Full(mask, out mw, out mh);
            if (src == null || mpx == null || w <= 0 || h <= 0)
            {
                return null;
            }
            Color32[] px = new Color32[src.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    Color32 p = src[i];
                    Color32 k = mpx[(y * mh / h) * mw + x * mw / w];
                    float gate = k.a > 0 ? 1f : 0f;   // MaskGate.glsl
                    float r = k.r / 255f * gate;
                    float g = k.g / 255f * gate;
                    Color tint = (one * r + Color.white * (1f - r)) * (two * g + Color.white * (1f - g));
                    px[i] = new Color32((byte)(p.r * tint.r), (byte)(p.g * tint.g), (byte)(p.b * tint.b), p.a);
                }
            }
            Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, true, false);
            t.name = "IsoRim_BoxFace_" + tex.name;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.SetPixels32(px);
            t.Apply(true);
            return t;
        }

        // Writes the sprites of a box, placed as its model was (ModelBaker.BakeModel).
        public static void PrintFaces(SectionLayer layer, Thing thing, ModelDef md, Vector3 center, Quaternion turn, Vector3 fit, Vector3 offset, float baseHeight)
        {
            Graphic g = thing.Graphic;
            if (g == null)
            {
                return;
            }
            float hx, hz;
            Half(md, out hx, out hz);
            float h = ModelHeight(md);
            float inset = InsetOf(md, hx, hz);
            float squash = View3D.Squash;
            float baseY = baseHeight / squash;
            Rot4 now = thing.Rotation;
            bool perFacing = g is Graphic_Multi && !g.ShouldDrawRotated;   // its sprite for a facing is drawn the way it looks already
            Vector3[] q = new Vector3[4];
            Vector2[] uv = new Vector2[4];
            for (int s = 0; s < 5; s++)
            {
                BoxSide side = (BoxSide)s;
                bool mirror;
                BoxLook look = LookOf(md, side, out mirror);
                if (look == BoxLook.None || look == BoxLook.MeanColour || (look == BoxLook.Edge && side == BoxSide.Top))
                {
                    continue;
                }
                Rot4 art = ArtOf(look, now);
                Material sprite = g.MatAt(art, thing);
                Material mat = FaceMaterial(sprite);
                LayerSubMesh sm = mat != null ? MeshWriter.Sub(layer, mat) : null;
                if (sm == null || sm.verts.Count + 4 > MeshWriter.MaxVerts)
                {
                    continue;   // the face stays its plain mean colour
                }
                Vector4 p = Painted(sprite.mainTexture);
                float u0 = p.x, v0 = p.y, u1 = p.z, v1 = p.w;
                bool flipped = (art == Rot4.West && g.WestFlipped) || (art == Rot4.East && g.EastFlipped);
                if (flipped != mirror)
                {
                    float t = u0;
                    u0 = u1;
                    u1 = t;
                }
                // Turns of the art against the box: a sprite drawn per facing already looks the way the thing faces.
                int turns = perFacing && look == BoxLook.Sprite || perFacing && look == BoxLook.Edge ? now.AsInt : 0;
                if (look == BoxLook.Edge)
                {
                    EdgeUVs((BoxSide)(1 + (s - 1 + turns) % 4), u0, v0, u1, v1, uv);
                }
                else
                {
                    uv[0] = new Vector2(u0, v0);
                    uv[1] = new Vector2(u0, v1);
                    uv[2] = new Vector2(u1, v1);
                    uv[3] = new Vector2(u1, v0);
                    if (side == BoxSide.Top && turns != 0)
                    {
                        Vector2 a = uv[0], b = uv[1], c = uv[2], d = uv[3];
                        Vector2[] was = { a, b, c, d };
                        for (int k = 0; k < 4; k++)
                        {
                            uv[k] = was[(k + turns) % 4];
                        }
                    }
                }
                Corners(side, hx, hz, 0f, h, inset, q);
                Vector3 normal = Vector3.Cross(q[1] - q[0], q[2] - q[0]).normalized * FaceLift;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 w = turn * (Vector3.Scale(q[k] + normal, fit) + offset);
                    q[k] = new Vector3(center.x + w.x, baseY + w.y / squash, center.z + w.z);
                }
                MeshWriter.TexturedQuad(sm, q[0], q[1], q[2], q[3], uv[0], uv[1], uv[2], uv[3], White);
            }
        }

        // The sprite's strip along the side of it that lies toward `edge`, as face corners seen from outside.
        private static void EdgeUVs(BoxSide edge, float u0, float v0, float u1, float v1, Vector2[] uv)
        {
            float du = (u1 - u0) * EdgeStrip;
            float dv = (v1 - v0) * EdgeStrip;
            float iu = (u1 - u0) * EdgeInside;
            float iv = (v1 - v0) * EdgeInside;
            float a, b;
            switch (edge)
            {
                case BoxSide.North:   // its top row; seen from the north, east is on the left
                    a = v1 - iv - dv;
                    b = v1 - iv;
                    uv[0] = new Vector2(u1, b);
                    uv[1] = new Vector2(u1, a);
                    uv[2] = new Vector2(u0, a);
                    uv[3] = new Vector2(u0, b);
                    break;
                case BoxSide.South:
                    a = v0 + iv + dv;
                    b = v0 + iv;
                    uv[0] = new Vector2(u0, b);
                    uv[1] = new Vector2(u0, a);
                    uv[2] = new Vector2(u1, a);
                    uv[3] = new Vector2(u1, b);
                    break;
                case BoxSide.East:
                    a = u1 - iu - du;
                    b = u1 - iu;
                    uv[0] = new Vector2(b, v0);
                    uv[1] = new Vector2(a, v0);
                    uv[2] = new Vector2(a, v1);
                    uv[3] = new Vector2(b, v1);
                    break;
                default:
                    a = u0 + iu + du;
                    b = u0 + iu;
                    uv[0] = new Vector2(b, v1);
                    uv[1] = new Vector2(a, v1);
                    uv[2] = new Vector2(a, v0);
                    uv[3] = new Vector2(b, v0);
                    break;
            }
        }
    }

    // A Card: the thing's sprite stood up in the middle of its footprint, facing the camera, baked once as plant cards are
    // (UprightCards). IsoRimCardSprite is the plant card's vertex stage with a fragment stage that keeps what a building
    // sprite needs and CutoutPlant does not have: the CutoutComplex mask and second colour. No sway: corner alpha 0.
    public static class ShapeCards
    {
        public const string SpriteDef = "IsoRimCardSprite";
        private static readonly Dictionary<Material, Material> cards = new Dictionary<Material, Material>();
        private static readonly int maskedId = Shader.PropertyToID("_IsoMasked");
        private static readonly Color32 NoSway = new Color32(255, 255, 255, 0);

        public static bool Is(ThingDef def)
        {
            ThingForm f = Forms.For(def);
            return f.shape == ModelShape.Card;
        }

        // The sprite's size on the map before the def's scale, as the game prints it for this facing.
        private static Vector2 SizeOf(Graphic g, Rot4 rot)
        {
            return !g.ShouldDrawRotated && rot.IsHorizontal ? g.drawSize.Rotated() : g.drawSize;
        }

        // How high a card of this def stands, cells: for selection, what rests on it and the like.
        public static float HeightOf(ThingDef def, ModelDef md)
        {
            float tall = def.graphicData != null ? def.graphicData.drawSize.y : 1f;
            return Mathf.Max(0.1f, tall * md.scale);
        }

        public static Material For(Material mat)
        {
            Material card;
            if (mat == null)
            {
                return null;
            }
            if (cards.TryGetValue(mat, out card))
            {
                return card;
            }
            card = null;
            Shader s = R3DMaterials.ShaderOf(SpriteDef);
            if (s != null)
            {
                card = new Material(s);
                card.CopyPropertiesFromMaterial(mat);
                card.SetFloat(maskedId, mat.shader == ShaderDatabase.CutoutComplex ? 1f : 0f);
                card.name = mat.name + "_ShapeCard";
                IsoGlow.UseCard(card);   // lit from its own cell, as plant cards
            }
            cards[mat] = card;
            return card;
        }

        public static bool Print(SectionLayer layer, Thing t, ModelEntry entry, float resting)
        {
            Graphic g = t.Graphic;
            ModelDef md = entry.def;
            if (g == null)
            {
                return false;
            }
            Rot4 rot = t.Rotation;
            Material sprite = g.MatAt(rot, t);
            Material card = For(sprite);
            LayerSubMesh sm = card != null ? MeshWriter.Sub(layer, card) : null;
            if (sm == null || sm.verts.Count + 4 > MeshWriter.MaxVerts)
            {
                return false;
            }
            Vector2 size = SizeOf(g, rot) * md.scale;
            if (t.MultipleItemsPerCellDrawn())
            {
                size *= 0.8f;
            }
            Extruder.Mask mask = Extruder.MaskOf(sprite.mainTexture, Vector2.one, Vector2.zero);
            float foot = 0f;   // the sprite's empty rows under its paint go below the ground
            if (mask != null)
            {
                foot = 1f;
                for (int i = 0; i < mask.lids.Count; i++)
                {
                    foot = Mathf.Min(foot, mask.lids[i].y);
                }
            }
            bool flipped = (rot == Rot4.West && g.WestFlipped) || (rot == Rot4.East && g.EastFlipped);
            float u0 = flipped ? 1f : 0f;
            float u1 = flipped ? 0f : 1f;
            Vector3 center = t.DrawPos;
            float lift = resting + md.offset.y;
            Vector3 hinge = new Vector3(center.x + md.offset.x, center.y + lift / View3D.Squash, center.z + md.offset.z);
            float w = size.x * 0.5f;
            float bottom = -foot * size.y;
            float top = bottom + size.y;
            int start = sm.verts.Count;
            AddCorner(sm, hinge, -w, bottom, u0, 0f);
            AddCorner(sm, hinge, -w, top, u0, 1f);
            AddCorner(sm, hinge, w, top, u1, 1f);
            AddCorner(sm, hinge, w, bottom, u1, 0f);
            MeshWriter.AddQuadTris(sm, start);
            if (md.groundShadow && lift <= 0.001f && GroundShadows.On)
            {
                float sx = t.def.size.x * 0.8f;
                float sz = t.def.size.z * 0.8f;
                GroundShadows.UnderSprite(layer, new Vector3(hinge.x - sx / 2f, 0f, hinge.z - sz / 2f), new Vector2(sx, 0f), new Vector2(0f, sz), null, Mathf.Min(size.y, 1f));
            }
            return true;
        }

        private static void AddCorner(LayerSubMesh sm, Vector3 hinge, float x, float z, float u, float v)
        {
            sm.verts.Add(hinge);
            sm.uvsChannelTwo.Add(new Vector3(x, 0f, z));
            sm.uvs.Add(new Vector3(u, v, 0f));
            sm.colors.Add(NoSway);
        }
    }
}
