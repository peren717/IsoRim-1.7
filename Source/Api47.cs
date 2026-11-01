using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>
    /// (Api version 47) Marks a worn apparel as a head decal: in IsoRim's 3D view it is not drawn as a hat, its own worn head
    /// sprites (south, east, north; west mirrors east), coloured as the game colours them, are projected onto the figure's
    /// 3D head instead: the front faces from the south sprite, the sides from the east one, the back from the north one,
    /// the top from whichever side it leans to. Opaque texels cover the skin; see-through ones (an eye slit) show the head
    /// and face under it, IsoRim's eyes included. Hair and beard show or hide by the apparel's own renderSkipFlags (as the
    /// game draws them); headgear on top keeps drawing over it. For hoods, balaclavas, masks, face paint.
    /// The sprite is read as laid out like the game's head textures (any texture size; in a 128 px one the head is x 41..86
    /// across facing south and north, 50 texels tall), placed up and down by the game's eye line (texel row 70.6 from the
    /// top) on IsoRim's eyes, the east sprite front to back by its eye (column 79.5) on IsoRim's eye.
    /// From XML (load-gate it: e.g. a LoadFolders folder with IfModActive="Peren.IsoRim17"):
    ///   &lt;modExtensions&gt;&lt;li Class="IsoRim.HeadDecal" /&gt;&lt;/modExtensions&gt;
    /// or from code with <see cref="Api.SetHeadDecal"/>.
    /// </summary>
    public class HeadDecal : DefModExtension
    {
        /// <summary>True (default): the sprite's outer outline and the shading along it are left out and its fill carried
        /// out to the texture's edges, so the decal covers the whole head to its 3D silhouette (hoods, balaclavas, full face
        /// paint; the 3D head's own outline stands in). False: projected exactly as drawn, outline and all, and only where
        /// the sprite is opaque (a mask, a scar, a painted mark).</summary>
        public bool wholeHead = true;
    }

    public static partial class Api
    {
        /// <summary>(Api version 47) Makes apparel a head decal (<see cref="HeadDecal"/>) from code, or with null stops one
        /// set this way (an XML <c>IsoRim.HeadDecal</c> mod extension still counts). Call on the main thread; people already
        /// drawn pick it up within a couple of seconds.</summary>
        public static void SetHeadDecal(ThingDef apparel, HeadDecal decal)
        {
            HeadDecals.Set(apparel, decal);
        }

        /// <summary>(Api version 47) The head decal settings IsoRim uses for this apparel (code first, then the XML mod
        /// extension), or null when it is drawn as usual.</summary>
        public static HeadDecal HeadDecalOf(ThingDef apparel)
        {
            return HeadDecals.For(apparel);
        }
    }

    // Head decals (Api 47). People.Dress collects the worn apparel that is one (in worn order) instead of making it a stand-in
    // hat; People.Merged adds, to the head's merged meshes, one textured part per sprite facing used: the faces of IsoRim's
    // head model (person_head) with the head's widening, lifted a little out along the welded corner normals (each decal a
    // little further out than the one before), their texture coordinates projected from the sprite of the facing the face
    // looks toward. The parts are drawn with the head (IsoRim's lit textured material, cut out at alpha 0.5, lit by the face
    // as headgear is), so they follow the pose, light, night light, floors and fades with it.
    // The sprite textures are coloured here (apparel colour; with a mask, CutoutComplex's two colours) and, for wholeHead,
    // cleaned of the outer outline and bled outward; made once per texture and colour.
    internal static class HeadDecals
    {
        internal struct Worn
        {
            public HeadDecal decal;
            public Material south, east, north;

            public bool Same(Worn o)
            {
                return decal == o.decal && south == o.south && east == o.east && north == o.north;
            }
        }

        private static readonly Dictionary<ThingDef, HeadDecal> byCode = new Dictionary<ThingDef, HeadDecal>();
        private static readonly Dictionary<ThingDef, HeadDecal> resolved = new Dictionary<ThingDef, HeadDecal>();

        internal static void Set(ThingDef apparel, HeadDecal decal)
        {
            if (apparel == null)
            {
                return;
            }
            if (decal == null)
            {
                byCode.Remove(apparel);
            }
            else
            {
                byCode[apparel] = decal;
            }
            resolved.Clear();
            People.RefreshAll();
        }

        internal static HeadDecal For(ThingDef def)
        {
            if (def == null || def.apparel == null)
            {
                return null;
            }
            HeadDecal d;
            if (resolved.TryGetValue(def, out d))
            {
                return d;
            }
            if (!byCode.TryGetValue(def, out d))
            {
                d = def.GetModExtension<HeadDecal>();
            }
            resolved[def] = d;
            return d;
        }

        // People.Dress: a worn head decal (its sprites as the game would draw them on this wearer now).
        internal static void Add(People.Look look, Apparel a, HeadDecal decal)
        {
            try
            {
                Pawn wearer = a.Wearer;
                BodyTypeDef body = wearer != null && wearer.story != null && wearer.story.bodyType != null ? wearer.story.bodyType : BodyTypeDefOf.Male;
                ApparelGraphicRecord rec;
                if (!ApparelGraphicRecordGetter.TryGetGraphicApparel(a, body, false, out rec) || rec.graphic == null)
                {
                    return;
                }
                Worn w = new Worn { decal = decal, south = rec.graphic.MatSouth, east = rec.graphic.MatEast, north = rec.graphic.MatNorth };
                (look.headDecals ?? (look.headDecals = new List<Worn>())).Add(w);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading a head decal's sprites failed (" + a.def.defName + "): " + e, 93619470);
            }
        }

        internal static bool Same(List<Worn> a, List<Worn> b)
        {
            int n = a != null ? a.Count : 0;
            if (n != (b != null ? b.Count : 0))
            {
                return false;
            }
            for (int i = 0; i < n; i++)
            {
                if (!a[i].Same(b[i]))
                {
                    return false;
                }
            }
            return true;
        }

        // ---- the parts

        // Where the head sits in the game's head sprites, as fractions of the texture (128 px: south and north x 41..86,
        // rows 39..88 from the top; east x 39..88, rows 40..87): the head model's box is fitted to these.
        private const float FrontWidth = 46f / 128f;
        private const float SideWidth = 50f / 128f;
        private const float Height = 50f / 128f;
        private const float Middle = 0.5f;   // the box's middle across, in every facing
        // Up and down the sprite is placed by its eyes rather than its box, so a mask's eye holes sit on IsoRim's eyes: the
        // game's eye line (texel row 70.6 of 128 from the top) at the height of IsoRim's eyes on the head (FaceDecal).
        private const float EyeRow = 1f - 70.6f / 128f;
        private const float EyeHeight = 0.945f;
        // Front to back the east sprite is placed by its eye too (texel column 79.5 of 128, the male head's eye; the female's
        // sits a little further back) at the depth of IsoRim's eyes (person_face: z 0.153..0.220), so an eye hole drawn there
        // lands on the head's front corner, where the eye is, not on its flat side.
        private const float EyeColumn = 79.5f / 128f;
        private const float EyeDepth = 0.19f;
        // The flat sides never take the east sprite in front of this column (20 texels behind its eye): the eye and any eye
        // hole round it belong on the front corner (which takes the south sprite), so a hole drawn reaching back from the
        // eye leaves no stray notch on the side; the sides' front edge repeats the column there instead (plain fill).
        private const float SideFrontmost = EyeColumn - 20f / 128f;

        // How far out from the head's surface (cells, along the welded corner normals) the first decal stands, and each next one.
        private const float Lift = 0.006f;
        private const float LiftStep = 0.002f;

        private enum Side { Front, Right, Back }

        // Adds the look's decals to the head's merged parts (People.Merged), placed as the head is (headPlace: its widening).
        internal static void AddParts(List<DynamicModels.Part> parts, People.Look look, Model head, Matrix4x4 headPlace)
        {
            if (look.headDecals == null || head == null || head.positions == null || head.positions.Length < 3)
            {
                return;
            }
            try
            {
                Shape shape = ShapeOf(head);
                for (int k = 0; k < look.headDecals.Count; k++)
                {
                    Worn w = look.headDecals[k];
                    float lift = Lift + LiftStep * k;
                    for (int s = 0; s < 3; s++)
                    {
                        Side side = (Side)s;
                        Material from = side == Side.Front ? w.south : side == Side.Right ? w.east : w.north;
                        Material mat = MaterialFor(from, w.decal.wholeHead);
                        if (mat == null)
                        {
                            continue;
                        }
                        List<Vector3> verts = new List<Vector3>();
                        List<Vector2> uvs = new List<Vector2>();
                        Vector3[] p = head.positions;
                        for (int i = 0; i + 2 < p.Length; i += 3)
                        {
                            if (shape.side[i / 3] != side)
                            {
                                continue;
                            }
                            for (int c = i; c < i + 3; c++)
                            {
                                Vector3 at = p[c] + shape.lift[c] * lift;
                                verts.Add(headPlace.MultiplyPoint3x4(at));
                                uvs.Add(shape.Uv(p[c], side));
                            }
                        }
                        DynamicModels.Part part = DynamicModels.TexturedPart(verts, uvs, null, false, mat);   // lit by the face (textured faces have no smooth normals)
                        if (part != null)
                        {
                            parts.Add(part);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Making a head decal failed: " + e, 93619471);
            }
        }

        // The head model read once: which sprite each face takes, the welded outward normal at each corner, the box.
        private sealed class Shape
        {
            public Side[] side;
            public Vector3[] lift;
            public Vector3 centre, size;

            public Vector2 Uv(Vector3 v, Side s)
            {
                float y = EyeRow + (v.y - EyeHeight) / size.y * Height;
                switch (s)
                {
                    case Side.Front:
                        // seen from the front (+z) the viewer's right is -x
                        return new Vector2(Middle - (v.x - centre.x) / size.x * FrontWidth, y);
                    case Side.Back:
                        return new Vector2(Middle + (v.x - centre.x) / size.x * FrontWidth, y);
                    default:
                        // the east sprite faces right: forward (+z) to the right. On the left side (-x) the game mirrors
                        // the same sprite (west), which lands on the same texture point.
                        return new Vector2(Mathf.Min(SideFrontmost, EyeColumn + (v.z - EyeDepth) / size.z * SideWidth), y);
                }
            }
        }

        private static Model shapeOf;
        private static Shape shape;

        private static Shape ShapeOf(Model head)
        {
            if (shapeOf == head && shape != null)
            {
                return shape;
            }
            Vector3[] p = head.positions;
            Vector3 lo = p[0], hi = p[0];
            for (int i = 1; i < p.Length; i++)
            {
                lo = Vector3.Min(lo, p[i]);
                hi = Vector3.Max(hi, p[i]);
            }
            Shape s = new Shape { side = new Side[p.Length / 3], lift = new Vector3[p.Length], centre = (lo + hi) * 0.5f, size = Vector3.Max(hi - lo, Vector3.one * 1e-4f) };
            Dictionary<Vector3, Vector3> sum = new Dictionary<Vector3, Vector3>();
            Vector3[] faceN = new Vector3[p.Length / 3];
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                Vector3 a = p[i], b = p[i + 1], c = p[i + 2];
                Vector3 n = Vector3.Cross(b - a, c - a);
                Vector3 mid = (a + b + c) / 3f;
                if (Vector3.Dot(n, mid - s.centre) < 0f)
                {
                    n = -n;   // outward
                }
                faceN[i / 3] = n;   // (length: twice the area, the weight below)
                for (int k = i; k < i + 3; k++)
                {
                    Vector3 t;
                    sum.TryGetValue(p[k], out t);
                    sum[p[k]] = t + n;
                }
                // The sprite this face takes: the one it looks toward across the ground; a face looking up or down (the
                // crown, under the chin) by where it lies from the head's middle instead.
                Vector3 un = n.sqrMagnitude > 1e-14f ? n.normalized : Vector3.up;
                Vector2 dir = new Vector2(un.x, un.z);
                if (dir.magnitude < 0.35f)
                {
                    dir = new Vector2((mid.x - s.centre.x) / s.size.x, (mid.z - s.centre.z) / s.size.z);
                }
                s.side[i / 3] = Mathf.Abs(dir.x) > Mathf.Abs(dir.y) ? Side.Right : (dir.y >= 0f ? Side.Front : Side.Back);
            }
            for (int i = 0; i < p.Length; i++)
            {
                Vector3 n = sum[p[i]];
                Vector3 own = faceN[i / 3];
                // a corner on a knife edge (faces turned apart): its own face's normal
                s.lift[i] = n.sqrMagnitude > 1e-14f && Vector3.Dot(n.normalized, own.normalized) > 0.3f ? n.normalized : (own.sqrMagnitude > 1e-14f ? own.normalized : Vector3.up);
            }
            shapeOf = head;
            shape = s;
            return s;
        }

        // ---- the textures

        private static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();

        private static readonly int maskId = Shader.PropertyToID("_MaskTex");
        private static readonly int colorTwoId = Shader.PropertyToID("_ColorTwo");

        // The lit textured material for one sprite material: its texture coloured as the game colours it, cleaned for a whole
        // head; null if the sprite cannot be read.
        private static Material MaterialFor(Material sprite, bool wholeHead)
        {
            if (sprite == null || sprite.mainTexture == null)
            {
                return null;
            }
            Texture tex = sprite.mainTexture;
            Texture mask = sprite.HasProperty(maskId) ? sprite.GetTexture(maskId) : null;
            if (mask != null && sprite.shader != ShaderDatabase.CutoutComplex)
            {
                mask = null;
            }
            Color one = sprite.color;
            Color two = mask != null && sprite.HasProperty(colorTwoId) ? sprite.GetColor(colorTwoId) : Color.white;
            Color32 c1 = one, c2 = two;
            string key = tex.GetInstanceID() + "|" + (mask != null ? mask.GetInstanceID() : 0) + "|" + c1.r + "," + c1.g + "," + c1.b + "|" + c2.r + "," + c2.g + "," + c2.b
                + (wholeHead ? "|w" : "|-");
            Material m;
            if (materials.TryGetValue(key, out m))
            {
                return m;
            }
            Texture2D made = null;
            try
            {
                made = Make(tex, mask, one, two, wholeHead);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Preparing a head decal's texture failed (" + tex.name + "): " + e, 93619472);
            }
            m = made != null ? R3DMaterials.LitTexture(made, "headdecal" + key, Color.white) : null;
            if (made == null)
            {
                ModLog.Warn("[IsoRim] Head decal: cannot read the texture " + tex.name + "; that side is left plain.");
            }
            materials[key] = m;
            return m;
        }

        private static Texture2D Make(Texture tex, Texture mask, Color one, Color two, bool wholeHead)
        {
            int w, h;
            Color32[] src = SpritePixels.Full(tex, out w, out h);
            if (src == null || w <= 0 || h <= 0)
            {
                return null;
            }
            Color32[] px = (Color32[])src.Clone();
            Color32[] mpx = null;
            int mw = 0, mh = 0;
            if (mask != null)
            {
                Color32[] m = SpritePixels.Full(mask, out mw, out mh);
                mpx = m != null ? (Color32[])m.Clone() : null;
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    Color32 p = px[i];
                    Color tint = one;
                    if (mpx != null)
                    {
                        float r = mpx[(y * mh / h) * mw + x * mw / w].r / 255f;
                        tint = Color.Lerp(two, one, r);   // CutoutComplex: colour where the mask is red, colourTwo elsewhere
                    }
                    px[i] = new Color32((byte)(p.r * tint.r), (byte)(p.g * tint.g), (byte)(p.b * tint.b), p.a);
                }
            }
            if (wholeHead)
            {
                WholeHead(px, w, h);
            }
            Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, true, false);
            t.name = "IsoRim_HeadDecal_" + tex.name;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.SetPixels32(px);
            t.Apply(true);
            return t;
        }

        // Leaves out the sprite's outer edge (the outline and the shading along it: every opaque texel within a few texels of
        // the see-through area that reaches the texture's border) and fills that and the whole outside with the nearest kept
        // texel, opaque. See-through areas enclosed by the sprite (an eye slit) stay see-through.
        private static void WholeHead(Color32[] px, int w, int h)
        {
            int n = w * h;
            const int Unknown = int.MaxValue;
            int[] dist = new int[n];
            Queue<int> open = new Queue<int>();
            // the outside: see-through texels joined to the border
            for (int i = 0; i < n; i++)
            {
                dist[i] = Unknown;
            }
            for (int x = 0; x < w; x++)
            {
                Seed(px, dist, open, x, w);
                Seed(px, dist, open, (h - 1) * w + x, w);
            }
            for (int y = 0; y < h; y++)
            {
                Seed(px, dist, open, y * w, w);
                Seed(px, dist, open, y * w + w - 1, w);
            }
            bool[] outside = new bool[n];
            while (open.Count > 0)
            {
                int i = open.Dequeue();
                outside[i] = true;
                int x = i % w, y = i / w;
                TryOutside(px, dist, open, x - 1, y, w, h);
                TryOutside(px, dist, open, x + 1, y, w, h);
                TryOutside(px, dist, open, x, y - 1, w, h);
                TryOutside(px, dist, open, x, y + 1, w, h);
            }
            // distance (8-connected steps) of each opaque texel from the outside
            for (int i = 0; i < n; i++)
            {
                dist[i] = outside[i] ? 0 : Unknown;
                if (outside[i])
                {
                    open.Enqueue(i);
                }
            }
            while (open.Count > 0)
            {
                int i = open.Dequeue();
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        {
                            continue;
                        }
                        int j = ny * w + nx;
                        if (dist[j] == Unknown && px[j].a >= 128)
                        {
                            dist[j] = dist[i] + 1;
                            open.Enqueue(j);
                        }
                    }
                }
            }
            int edge = Mathf.Max(2, Mathf.RoundToInt(7f * w / 128f));
            // distance of each texel from the enclosed see-through areas (an eye slit): their own rim is kept as drawn but
            // never spread outward (an outline round a slit near the edge would otherwise smear over the head's side)
            int[] fromHole = new int[n];
            for (int i = 0; i < n; i++)
            {
                fromHole[i] = !outside[i] && px[i].a < 128 ? 0 : Unknown;
                if (fromHole[i] == 0)
                {
                    open.Enqueue(i);
                }
            }
            while (open.Count > 0)
            {
                int i = open.Dequeue();
                if (fromHole[i] > edge)
                {
                    continue;
                }
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        {
                            continue;
                        }
                        int j = ny * w + nx;
                        if (fromHole[j] == Unknown)
                        {
                            fromHole[j] = fromHole[i] + 1;
                            open.Enqueue(j);
                        }
                    }
                }
            }
            // kept: opaque texels further in than the edge (and off any slit's rim); everything outside or on the edge takes
            // the nearest kept one
            bool[] known = new bool[n];
            int kept = 0;
            for (int i = 0; i < n; i++)
            {
                if (!outside[i] && px[i].a >= 128 && dist[i] != Unknown && dist[i] > edge && fromHole[i] > edge)
                {
                    known[i] = true;
                    open.Enqueue(i);
                    kept++;
                }
            }
            if (kept == 0)
            {
                return;   // nothing inside the edge (a thin sprite): left as it is
            }
            while (open.Count > 0)
            {
                int i = open.Dequeue();
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        {
                            continue;
                        }
                        int j = ny * w + nx;
                        if (known[j] || !(outside[j] || (px[j].a >= 128 && dist[j] <= edge)))
                        {
                            continue;   // (enclosed see-through texels stay as they are)
                        }
                        known[j] = true;
                        Color32 c = px[i];
                        c.a = 255;
                        px[j] = c;
                        open.Enqueue(j);
                    }
                }
            }
        }

        private static void Seed(Color32[] px, int[] dist, Queue<int> open, int i, int w)
        {
            if (px[i].a < 128 && dist[i] != 0)
            {
                dist[i] = 0;
                open.Enqueue(i);
            }
        }

        private static void TryOutside(Color32[] px, int[] dist, Queue<int> open, int x, int y, int w, int h)
        {
            if (x < 0 || y < 0 || x >= w || y >= h)
            {
                return;
            }
            int i = y * w + x;
            if (px[i].a < 128 && dist[i] != 0)
            {
                dist[i] = 0;
                open.Enqueue(i);
            }
        }

        // Harness: forget the made textures (and the head read) so they are made again.
        internal static void Clear()
        {
            materials.Clear();
            resolved.Clear();
            shape = null;
            shapeOf = null;
        }
    }
}
