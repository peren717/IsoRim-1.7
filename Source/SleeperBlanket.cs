using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The blanket over a sleeper whose body the bed hides (a head-only bed: !bed_showSleeperBody, People.Draw headOnly): the
    // bed's own top surface pushed up by the hidden body, so the blanket shows shoulders, chest, hips and legs.
    //
    // Not a fixed model: a grid over the body (s along, from just under the jaw to past the toes; u across) gives a height d
    // over the bed (Grid); every corner is dropped onto the bed's model (Under: the highest face under it, not above the
    // sleeper's slot + Reach, so a bunk's upper level is skipped; a bed without a model, raised from its sprite: its raised
    // top) and each face takes the colour of the bed face under its middle, by the bake's own rule (ModelBaker: corner colour,
    // x the stuff tint on "stuff" faces, x the sheet colour, Thing.DrawColorTwo, on "sheet" faces; textured faces x
    // TextureLift x their texture's mean). The profile meets the bed with slope 0 (no crease, the bed's normal), then dips
    // under it and ends in a wall and a bottom: the outline shell of that rim stays under the blanket. Inside, the base rides
    // over steps (the turned-down band); only the tangent line follows the bed exactly. A grid row is moved onto colour edges
    // along the middle (the band's edge stays sharp). Width is kept on the bed's top at the hips, length on the bed's foot.
    // Spec and reference: tools\blanket_review.py in the notes folder (shape A3 at 1.7, 2026-11-08, approved by the user).
    //
    // Built once per sleeper and kept (Entry) while the bed, its place, turn, model, colours, the sleeper's spot and size stay
    // the same; drawn each frame as ready-made parts through the figure's own path (DynamicModels.DrawParts: outline, sun
    // shadow, floors and their fade, Api.DrawFigure, culling). Not drawn by itself for a figure another mod draws (Api 62
    // placing path: People.Draw returns before anything is drawn); that mod's drawer asks for it with
    // FigureDrawContext.DrawSleeperBlanket (Api 67: the same TryDraw, with the placing People.Draw worked out). No setting.
    internal static class SleeperBlanket
    {
        // ---- the shape (figure units of a size-1 adult; z from the head's middle toward the feet)
        private const float End = 1.24f;        // the cloth meets the bed again past the toes
        private const float Dip = 0.07f;        // the rim this far under the bed surface
        private const float Wall = 0.07f;       // the wall below the rim
        private const float DipW = 0.14f;       // the dip ring's width at the foot end (and the length clamp's margin)
        private const float DipWX = 0.12f;      // at the sides
        private const float DipWHead = 0.06f;   // at the head end (draping toward the pillow, under the head)
        private const float Reach = 0.25f;      // the bed's top looked for at most this far over the sleeper's slot
        private const float RefStart = 0.14f;   // the lump starts here (under the jaw)
        private const float Height = 1.7f;      // the approved height (x the keys below)
        private const int NS = 19, NU = 11;     // rows along and columns across, inner

        private static readonly float[] HZ = { RefStart, 0.19f, 0.26f, 0.40f, 0.54f, 0.66f, 0.80f, 0.92f, End };
        private static readonly float[] HH = { 0f, 0.07f, 0.12f, 0.145f, 0.15f, 0.145f, 0.115f, 0.085f, 0f };
        private static readonly float[] WZ = { RefStart, 0.22f, 0.30f, 0.46f, 0.60f, 0.70f, 0.85f, 1.0f, End };
        private static readonly float[] WW = { 0.24f, 0.31f, 0.34f, 0.34f, 0.32f, 0.32f, 0.30f, 0.27f, 0.22f };

        // Catmull-Rom through the keys, slope 0 at both ends, never below 0.
        private static float Spline(float[] zs, float[] ys, float z, float scale)
        {
            int n = zs.Length;
            if (z <= zs[0])
            {
                return ys[0] * scale;
            }
            if (z >= zs[n - 1])
            {
                return ys[n - 1] * scale;
            }
            int i = 0;
            while (i < n - 2 && zs[i + 1] <= z)
            {
                i++;
            }
            float h = zs[i + 1] - zs[i];
            float t = (z - zs[i]) / h;
            float m0 = (i == 0 ? 0f : (ys[i + 1] - ys[i - 1]) / (zs[i + 1] - zs[i - 1])) * h;
            float m1 = (i + 1 == n - 1 ? 0f : (ys[i + 2] - ys[i]) / (zs[i + 2] - zs[i])) * h;
            float t2 = t * t, t3 = t2 * t;
            float y = (2f * t3 - 3f * t2 + 1f) * ys[i] + (t3 - 2f * t2 + t) * m0 + (-2f * t3 + 3f * t2) * ys[i + 1] + (t3 - t2) * m1;
            return Mathf.Max(0f, y * scale);
        }

        private static float Ridge(float v)
        {
            v = Mathf.Min(1f, Mathf.Abs(v));
            float a = 1f - v * v;
            return a * a;
        }

        // A rounded ramp across: 0 with slope 0 at |u| = 1, straight up the middle part, a rounded top.
        private static float Ramp(float u)
        {
            const float k = 0.25f;
            float t = 1f - Mathf.Min(1f, Mathf.Abs(u));
            float g = t <= k ? t * t / (2f * k) : t >= 1f - k ? 1f - k - (1f - t) * (1f - t) / (2f * k) : t - k / 2f;
            return g / (1f - k);
        }

        private static float Across(float u, float z)
        {
            float torso = Ramp(u);
            float legs = Ramp(u) - 0.12f * Ridge(u / 0.35f);   // the legs: one mound, a shallow groove down the middle
            float t = Smooth((z - 0.64f) / 0.16f);
            return torso + (legs - torso) * t;
        }

        private static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        private struct GridPoint
        {
            public float s, u, x, z, d, xa, za;
            public bool rim;   // the dip ring (and its outer edge)
        }

        private static readonly float[] innerS = new float[NS];
        private static readonly List<float> snaps = new List<float>();

        // The grid at size 1 for a lump starting at RefStart: rows along (two dip rows each end), columns across (two dip
        // columns each side). snaps: inner rows moved onto colour edges (in s).
        private static GridPoint[,] Grid()
        {
            for (int i = 0; i < NS; i++)
            {
                innerS[i] = i / (float)(NS - 1);
            }
            for (int q = 0; q < snaps.Count; q++)
            {
                float sn = snaps[q];
                int best = 1;
                for (int i = 2; i < NS - 1; i++)
                {
                    if (Mathf.Abs(innerS[i] - sn) < Mathf.Abs(innerS[best] - sn))
                    {
                        best = i;
                    }
                }
                if (Mathf.Abs(innerS[best] - sn) < 0.5f / (NS - 1))
                {
                    innerS[best] = sn;
                }
            }
            int rows = NS + 4, cols = NU + 4;
            GridPoint[,] g = new GridPoint[rows, cols];
            float span = End - RefStart;
            for (int i = 0; i < rows; i++)
            {
                float sv = i == 0 ? -1f : i == 1 ? -0.6f : i == rows - 2 ? 1.6f : i == rows - 1 ? 2f : innerS[i - 2];
                for (int j = 0; j < cols; j++)
                {
                    float uv = j == 0 ? -2f : j == 1 ? -1.6f : j == cols - 2 ? 1.6f : j == cols - 1 ? 2f : -1f + 2f * (j - 2) / (NU - 1);
                    float sIn = Mathf.Clamp01(sv);
                    float uIn = Mathf.Clamp(uv, -1f, 1f);
                    float zIn = RefStart + sIn * span;
                    float w = Spline(WZ, WW, zIn, 1f);
                    float x = w * Mathf.Sign(uIn) * Mathf.Pow(Mathf.Abs(uIn), 0.9f);
                    float z = zIn;
                    float eu = Mathf.Abs(uv) > 1f ? Mathf.Abs(uv) - 1f : 0f;
                    float es = sv < 0f ? -sv : sv > 1f ? sv - 1f : 0f;
                    if (Mathf.Abs(uv) > 1f)
                    {
                        x += Mathf.Sign(uv) * DipWX * (Mathf.Abs(uv) - 1f);
                    }
                    if (sv < 0f)
                    {
                        z -= DipWHead * -sv;
                    }
                    else if (sv > 1f)
                    {
                        z += DipW * (sv - 1f);
                    }
                    g[i, j] = new GridPoint
                    {
                        s = sv,
                        u = uv,
                        x = x,
                        z = z,
                        d = Spline(HZ, HH, zIn, Height) * Across(uIn, zIn) - Dip * Smooth(Mathf.Max(eu, es)),
                        xa = Mathf.Abs(uv) <= 1f ? x : Mathf.Sign(uv) * w,
                        za = zIn,
                        rim = eu > 0f || es > 0f
                    };
                }
            }
            return g;
        }

        // ---- the bed under a point

        private struct Hit
        {
            public bool ok;
            public float y;      // over the sleeper's floor, cells
            public int key;      // which colour (the bed face's material; -1 raised sprite)
            public Color colour;
        }

        // What a lump is dropped onto: a bed's model placed as the bake places it (ModelBaker.PointOnMap), or its raised sprite.
        private class Bed
        {
            public Building_Bed thing;
            public Model model;            // null: raised from its sprite (Extruded): a flat top at height over its footprint
            public float height;
            public CellRect rect;
            public Color spriteColour;
            public Vector3 p0, ax, az;     // model (0,0,0), (1,0,0), (0,0,1) on the map (x, z); ax.y / az.y unused
            public float fitY;
            public float det;
            public Color tint, two;
            public string swatch;
            public bool textured;
            public Cells cells;            // the model's triangles by where they lie (model x, z)
        }

        // A model's triangles sorted into a coarse grid over its footprint (model x, z), so a ray looks at a few only.
        private class Cells
        {
            public const int N = 12;
            public float x0, z0, sx, sz;
            public int[][] tris;
        }

        private static readonly Dictionary<Model, Cells> cellsOf = new Dictionary<Model, Cells>();

        private static Cells CellsOf(Model m)
        {
            Cells g;
            if (cellsOf.TryGetValue(m, out g))
            {
                return g;
            }
            Vector3[] p = m.positions;
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            for (int i = 0; i < p.Length; i++)
            {
                x0 = Mathf.Min(x0, p[i].x);
                x1 = Mathf.Max(x1, p[i].x);
                z0 = Mathf.Min(z0, p[i].z);
                z1 = Mathf.Max(z1, p[i].z);
            }
            g = new Cells { x0 = x0 - 1e-3f, z0 = z0 - 1e-3f, sx = (x1 - x0 + 2e-3f) / Cells.N, sz = (z1 - z0 + 2e-3f) / Cells.N };
            List<int>[] lists = new List<int>[Cells.N * Cells.N];
            for (int t = 0; t + 2 < p.Length; t += 3)
            {
                float a0 = Mathf.Min(p[t].x, Mathf.Min(p[t + 1].x, p[t + 2].x)), a1 = Mathf.Max(p[t].x, Mathf.Max(p[t + 1].x, p[t + 2].x));
                float b0 = Mathf.Min(p[t].z, Mathf.Min(p[t + 1].z, p[t + 2].z)), b1 = Mathf.Max(p[t].z, Mathf.Max(p[t + 1].z, p[t + 2].z));
                int i0 = Mathf.Clamp((int)((a0 - g.x0) / g.sx), 0, Cells.N - 1), i1 = Mathf.Clamp((int)((a1 - g.x0) / g.sx), 0, Cells.N - 1);
                int j0 = Mathf.Clamp((int)((b0 - g.z0) / g.sz), 0, Cells.N - 1), j1 = Mathf.Clamp((int)((b1 - g.z0) / g.sz), 0, Cells.N - 1);
                for (int i = i0; i <= i1; i++)
                {
                    for (int j = j0; j <= j1; j++)
                    {
                        List<int> l = lists[i * Cells.N + j] ?? (lists[i * Cells.N + j] = new List<int>());
                        l.Add(t / 3);
                    }
                }
            }
            g.tris = new int[lists.Length][];
            for (int k = 0; k < lists.Length; k++)
            {
                g.tris[k] = lists[k] != null ? lists[k].ToArray() : new int[0];
            }
            if (cellsOf.Count > 256)
            {
                cellsOf.Clear();
            }
            cellsOf[m] = g;
            return g;
        }

        private static Bed BedOf(Building_Bed b)
        {
            ThingForm f = Forms.For(b);
            Bed bed = new Bed { thing = b };
            if (f.form == Form3D.Model && f.model != null && f.model.HasModel)
            {
                ModelEntry entry = ModelLibrary.StyledEntry(b) ?? f.model;
                Model m = entry.HasModel ? entry.Pick(b) : null;
                if (m == null || m.positions == null || m.colors == null)
                {
                    return null;
                }
                bed.model = m;
                Vector3 fit;
                float yaw;
                bed.p0 = ModelBaker.PointOnMap(b, entry, m, Vector3.zero, out fit, out yaw);
                bed.ax = ModelBaker.PointOnMap(b, entry, m, Vector3.right, out fit, out yaw) - bed.p0;
                bed.az = ModelBaker.PointOnMap(b, entry, m, Vector3.forward, out fit, out yaw) - bed.p0;
                bed.fitY = ModelBaker.PointOnMap(b, entry, m, Vector3.up, out fit, out yaw).y - bed.p0.y;
                bed.det = bed.ax.x * bed.az.z - bed.az.x * bed.ax.z;
                if (Mathf.Abs(bed.det) < 1e-6f || bed.fitY <= 1e-6f)
                {
                    return null;
                }
                bed.tint = entry.def.spriteTint ? SpriteColors.MeanFor(b) : R3DTextures.ModelTint(b);
                bed.two = R3DTextures.HasSheet(m) ? R3DTextures.SheetTint(b) : Color.white;
                bed.textured = IsoRimMod.Settings.texturedModels && m.materials != null && m.uvs != null && m.material != null;
                bed.swatch = bed.textured ? R3DTextures.ModelSwatch(b) : null;
                bed.cells = CellsOf(m);
                return bed;
            }
            if (f.form == Form3D.Extruded)
            {
                bed.height = Forms.HeightOf(b);
                bed.rect = b.OccupiedRect();
                bed.spriteColour = SpriteUnder(b);
                return bed.height > 0.05f ? bed : null;
            }
            return null;
        }

        // The bed's top at map point (x, z), at most limit over the floor.
        private static Hit Under(Bed bed, float x, float z, float limit)
        {
            Hit h = default(Hit);
            if (bed.model == null)
            {
                IntVec3 c = new IntVec3(Mathf.FloorToInt(x), 0, Mathf.FloorToInt(z));
                if (bed.rect.Contains(c) && bed.height <= limit)
                {
                    h.ok = true;
                    h.y = bed.height;
                    h.key = -1;
                    h.colour = bed.spriteColour;
                }
                return h;
            }
            // map -> model (x, z): solve p0 + lx ax + lz az = (x, z)
            float dx = x - bed.p0.x, dz = z - bed.p0.z;
            float lx = (dx * bed.az.z - bed.az.x * dz) / bed.det;
            float lz = (bed.ax.x * dz - dx * bed.ax.z) / bed.det;
            float lim = (limit - bed.p0.y) / bed.fitY;
            Model m = bed.model;
            Vector3[] p = m.positions;
            float best = float.NegativeInfinity;
            int bestT = -1;
            float b1 = 0f, b2 = 0f;
            Cells g = bed.cells;
            int ci = (int)((lx - g.x0) / g.sx), cj = (int)((lz - g.z0) / g.sz);
            if (ci < 0 || cj < 0 || ci >= Cells.N || cj >= Cells.N)
            {
                return h;
            }
            int[] list = g.tris[ci * Cells.N + cj];
            for (int q = 0; q < list.Length; q++)
            {
                int t = list[q];
                Vector3 A = p[3 * t], B = p[3 * t + 1], C = p[3 * t + 2];
                float d = (B.x - A.x) * (C.z - A.z) - (C.x - A.x) * (B.z - A.z);
                if (Mathf.Abs(d) < 1e-12f)
                {
                    continue;
                }
                float l1 = ((lx - A.x) * (C.z - A.z) - (C.x - A.x) * (lz - A.z)) / d;
                float l2 = ((B.x - A.x) * (lz - A.z) - (lx - A.x) * (B.z - A.z)) / d;
                if (l1 < -1e-5f || l2 < -1e-5f || l1 + l2 > 1f + 1e-5f)
                {
                    continue;
                }
                float y = A.y + l1 * (B.y - A.y) + l2 * (C.y - A.y);
                if (y <= lim && y > best)
                {
                    int mi = m.material != null ? m.material[3 * t] : 0;
                    ModelMaterial mm = m.materials != null && mi < m.materials.Length ? m.materials[mi] : null;
                    if (mm != null && (mm.glow || mm.leaf))
                    {
                        continue;
                    }
                    best = y;
                    bestT = t;
                    b1 = l1;
                    b2 = l2;
                }
            }
            if (bestT < 0)
            {
                return h;
            }
            h.ok = true;
            h.y = bed.p0.y + best * bed.fitY;
            int i0 = 3 * bestT;
            h.key = m.material != null ? m.material[i0] : 0;
            h.colour = FaceColour(bed, i0, b1, b2);
            return h;
        }

        // The colour the bake gives the bed face (corner i0's triangle) at barycentric (b1, b2) (ModelBaker.BakeModel).
        private static Color FaceColour(Bed bed, int i0, float b1, float b2)
        {
            Model m = bed.model;
            int mi = m.material != null ? m.material[i0] : 0;
            ModelMaterial mm = m.materials != null && mi < m.materials.Length ? m.materials[mi] : null;
            bool own = bed.textured && m.OwnColour(i0);
            Color c;
            if (bed.textured && mm != null && mm.texture != null && !own)
            {
                c = mm.color * ModelBaker.TextureLift;
                if (mm.tinted)
                {
                    c *= bed.tint;
                }
                if (mm.sheet)
                {
                    c *= bed.two;
                }
                c *= TextureMean(mm.texture.Replace("{stuff}", bed.swatch ?? "Wood"));
            }
            else
            {
                c = m.colors[i0] * (1f - b1 - b2) + m.colors[i0 + 1] * b1 + m.colors[i0 + 2] * b2;
                if (m.tinted[i0])
                {
                    c *= bed.tint;
                }
                if (mm != null && mm.sheet)
                {
                    c *= bed.two;
                }
                if (own && mm != null && mm.texture != null)
                {
                    c *= ModelBaker.TextureLift * TextureMean(mm.texture.Replace("{stuff}", bed.swatch ?? "Wood"));
                }
            }
            c.a = 1f;
            return c;
        }

        private static readonly Dictionary<string, Color> texMeans = new Dictionary<string, Color>();

        // The mean colour of a model texture (a swatch), read once.
        private static Color TextureMean(string path)
        {
            Color c;
            if (texMeans.TryGetValue(path, out c))
            {
                return c;
            }
            c = Color.white;
            Texture2D tex = R3DTextures.Get(path);
            Color32[] px = tex != null ? SpritePixels.Sample(tex, Vector2.one, Vector2.zero, 8) : null;
            if (px != null && px.Length > 0)
            {
                float r = 0f, g = 0f, b = 0f;
                for (int i = 0; i < px.Length; i++)
                {
                    r += px[i].r;
                    g += px[i].g;
                    b += px[i].b;
                }
                float k = 1f / (255f * px.Length);
                c = new Color(r * k, g * k, b * k, 1f);
            }
            texMeans[path] = c;
            return c;
        }

        // A bed raised from its sprite: the sprite's colour round the middle of its footprint, as the game's shader colours it
        // (CutoutComplex: the first colour where the mask is red, the second where green, Shapes.Masked).
        private static Color SpriteUnder(Building_Bed b)
        {
            try
            {
                Graphic g = b.Graphic;
                Material mat = g != null ? g.MatAt(b.Rotation, b) : null;
                if (mat == null || mat.mainTexture == null)
                {
                    return SpriteColors.MeanFor(b);
                }
                Vector2 scale = new Vector2(0.4f, 0.3f), offset = new Vector2(0.3f, 0.25f);   // the middle, toward the foot
                Color32[] px = SpritePixels.Sample(mat.mainTexture, scale, offset, 4);
                if (px == null)
                {
                    return SpriteColors.MeanFor(b);
                }
                Texture maskTex = mat.shader == ShaderDatabase.CutoutComplex && mat.HasProperty("_MaskTex") ? mat.GetTexture("_MaskTex") : null;
                Color32[] mask = maskTex != null ? SpritePixels.Sample(maskTex, scale, offset, 4) : null;
                Color one = mat.color;
                Color two = mat.HasProperty("_ColorTwo") ? mat.GetColor("_ColorTwo") : Color.white;
                float r = 0f, gg = 0f, bb = 0f, n = 0f;
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].a < 128)
                    {
                        continue;
                    }
                    Color c = px[i];
                    if (mask != null && i < mask.Length && mask[i].a > 0)
                    {
                        float mr = mask[i].r / 255f, mg = mask[i].g / 255f;
                        c *= (one * mr + Color.white * (1f - mr)) * (two * mg + Color.white * (1f - mg));
                    }
                    else if (mask == null)
                    {
                        c *= one;
                    }
                    r += c.r;
                    gg += c.g;
                    bb += c.b;
                    n++;
                }
                return n > 0f ? new Color(r / n, gg / n, bb / n, 1f) : SpriteColors.MeanFor(b);
            }
            catch (System.Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading a bed's sprite colour failed: " + e.Message, 93611588);
                return SpriteColors.MeanFor(b);
            }
        }

        // ---- the cache

        private class Entry
        {
            public int bedId, model, rot, stamp, used;
            public IntVec3 pos;
            public Vector3 at;
            public float yaw, size, ground;
            public Color32 tint, two;
            public bool textured;
            public List<DynamicModels.Part> parts;   // null: nothing to draw (no top found)
        }

        private static readonly Dictionary<int, Entry> cache = new Dictionary<int, Entry>();
        private static readonly List<int> stale = new List<int>();
        private static int sweepFrame;
        private const int BuildsPerFrame = 1;
        private static int budgetFrame, budgetUsed;

        internal static bool On = true;   // harness (probe blanket=0|1): before/after shots; not a setting

        // Harness / perf: lumps built and the time spent (Stopwatch ticks).
        internal static int Built;
        internal static long BuildTicks;

        // Draws the lump of a sleeper in a head-only bed; at, yaw, size and ground as People.Draw places the figure.
        internal static void Draw(Pawn pawn, Building_Bed bed, Vector3 at, float yaw, float size, float ground)
        {
            TryDraw(pawn, bed, at, yaw, size, ground);
        }

        // Draw's work, shared by People.Draw and (Api 67) FigureDrawContext.DrawSleeperBlanket: true when the lump was drawn or
        // waits for this frame's build budget; false when there is nothing to draw (off, no bed, bed gone, no top found).
        internal static bool TryDraw(Pawn pawn, Building_Bed bed, Vector3 at, float yaw, float size, float ground)
        {
            if (!On || pawn == null || bed == null || !bed.Spawned)
            {
                return false;
            }
            int frame = Time.frameCount;
            Sweep(frame);
            ThingForm f = Forms.For(bed);
            int modelId = -1;
            Color32 tint = default(Color32), two = default(Color32);
            if (f.form == Form3D.Model && f.model != null && f.model.HasModel)
            {
                ModelEntry entry = ModelLibrary.StyledEntry(bed) ?? f.model;
                Model m = entry.HasModel ? entry.Pick(bed) : null;
                modelId = m != null ? m.id : -1;
                tint = entry.def.spriteTint ? Color.white : R3DTextures.ModelTint(bed);
                two = R3DTextures.SheetTint(bed);
            }
            bool textured = IsoRimMod.Settings.texturedModels;
            int stamp = SmoothFigures.Stamp;
            Entry e;
            if (!cache.TryGetValue(pawn.thingIDNumber, out e) || e.bedId != bed.thingIDNumber || e.model != modelId || e.rot != bed.Rotation.AsInt
                || e.pos != bed.Position || e.stamp != stamp || e.textured != textured || !Same(e.tint, tint) || !Same(e.two, two)
                || Mathf.Abs(e.size - size) > 0.001f || Mathf.Abs(e.ground - ground) > 0.001f || Mathf.Abs(e.yaw - yaw) > 0.01f
                || Mathf.Abs(e.at.x - at.x) > 0.001f || Mathf.Abs(e.at.z - at.z) > 0.001f)
            {
                // one new lump a frame (a few ms each): when a whole colony goes to bed at once the rest follow frame by frame
                if (budgetFrame != frame)
                {
                    budgetFrame = frame;
                    budgetUsed = 0;
                }
                if (budgetUsed >= BuildsPerFrame)
                {
                    return true;   // (waits for a later frame's budget)
                }
                budgetUsed++;
                if (e != null && e.parts != null)
                {
                    DynamicModels.Destroy(e.parts);
                }
                e = new Entry
                {
                    bedId = bed.thingIDNumber, model = modelId, rot = bed.Rotation.AsInt, pos = bed.Position, stamp = stamp, textured = textured,
                    tint = tint, two = two, size = size, ground = ground, yaw = yaw, at = at
                };
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    e.parts = Build(bed, at, yaw, size, ground);
                }
                catch (System.Exception ex)
                {
                    Log.ErrorOnce("[IsoRim] Making a sleeper's blanket failed: " + ex, 93611589);
                    e.parts = null;
                }
                BuildTicks += sw.ElapsedTicks;
                Built++;
                cache[pawn.thingIDNumber] = e;
            }
            e.used = frame;
            if (e.parts == null)
            {
                return false;
            }
            DynamicModels.DrawParts(e.parts, at, yaw, Matrix4x4.identity);
            return true;
        }

        private static bool Same(Color32 a, Color32 b)
        {
            return a.r == b.r && a.g == b.g && a.b == b.b;
        }

        // Lumps not drawn for a while (the sleeper got up, left the map) are thrown away.
        private static void Sweep(int frame)
        {
            if (frame - sweepFrame < 600)
            {
                return;
            }
            sweepFrame = frame;
            stale.Clear();
            foreach (KeyValuePair<int, Entry> kv in cache)
            {
                if (frame - kv.Value.used > 600)
                {
                    stale.Add(kv.Key);
                }
            }
            for (int i = 0; i < stale.Count; i++)
            {
                Entry e = cache[stale[i]];
                if (e.parts != null)
                {
                    DynamicModels.Destroy(e.parts);
                }
                cache.Remove(stale[i]);
            }
        }

        // Harness: whether this sleeper has a lump drawn now (built and not empty).
        internal static bool Has(Pawn pawn)
        {
            Entry e;
            return pawn != null && cache.TryGetValue(pawn.thingIDNumber, out e) && e.parts != null && Time.frameCount - e.used < 5;
        }

        // ---- building

        private static Vector3 Map(Vector3 at, Quaternion turn, float fx, float fz)
        {
            Vector3 v = turn * new Vector3(fx, 0f, fz);
            return new Vector3(at.x + v.x, 0f, at.z + v.z);
        }

        private static Hit UnderLocal(Bed bed, Vector3 at, Quaternion turn, float fx, float fz, float limit)
        {
            Vector3 p = Map(at, turn, fx, fz);
            return Under(bed, p.x, p.z, limit);
        }

        private static List<DynamicModels.Part> Build(Building_Bed b, Vector3 at, float yaw, float size, float ground)
        {
            Bed bed = BedOf(b);
            if (bed == null)
            {
                return null;
            }
            Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up);
            float limit = ground + Reach;
            Hit hips = UnderLocal(bed, at, turn, 0f, 0.62f * size, limit);
            if (!hips.ok)
            {
                return null;
            }
            float span = End - RefStart;
            float start = RefStart * size;
            // length: the toes' end and its dip stay on the bed's top (a big race in a short bed is shortened)
            float last = 0.62f * size;
            for (float z = last; z < 1.6f * size; z += 0.01f)
            {
                Hit h = UnderLocal(bed, at, turn, 0f, z, limit);
                if (!h.ok || h.y < hips.y - 0.03f)
                {
                    break;
                }
                last = z;
            }
            float end = Mathf.Min(End * size, last - DipW * size);
            float zscale = Mathf.Max(0.4f * span * size, end - start) / span;
            // width: the side dip stays on the bed's top and its bevel at the hips; a double bed: each sleeper's half
            float reach = float.MaxValue;
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                float x = 0f;
                while (x < 0.8f * size)
                {
                    Hit h = UnderLocal(bed, at, turn, sgn * (x + 0.01f), 0.62f * size, limit);
                    if (!h.ok || h.y < hips.y - 0.06f)
                    {
                        break;
                    }
                    x += 0.01f;
                }
                reach = Mathf.Min(reach, x);
            }
            reach = Mathf.Min(reach, 0.48f);
            float full = 0f;
            for (int i = 0; i < WW.Length; i++)
            {
                full = Mathf.Max(full, WW[i]);
            }
            full += DipWX;
            float xscale = Mathf.Min(size, (reach - 0.015f) / full);
            if (xscale <= 0.05f)
            {
                return null;
            }
            // rows onto the colour edges along the middle
            snaps.Clear();
            int prev = int.MinValue;
            for (float z = start; z < end; z += 0.01f)
            {
                Hit h = UnderLocal(bed, at, turn, 0f, z, limit);
                int k = h.ok ? h.key : int.MaxValue;
                if (prev != int.MinValue && k != prev)
                {
                    snaps.Add((z - 0.005f - start) / (zscale * span));
                }
                prev = k;
            }
            GridPoint[,] g = Grid();
            int R = g.GetLength(0), C = g.GetLength(1);
            Hit[,] ownHit = new Hit[R, C];
            float[,] own = new float[R, C];
            bool[,] ownOk = new bool[R, C];
            for (int i = 0; i < R; i++)
            {
                for (int j = 0; j < C; j++)
                {
                    Hit h = UnderLocal(bed, at, turn, g[i, j].x * xscale, start + (g[i, j].z - RefStart) * zscale, limit);
                    ownHit[i, j] = h;
                    own[i, j] = h.y;
                    ownOk[i, j] = h.ok;
                }
            }
            // corners (figure frame: x across, y over the sleeper's floor - ground, z along)
            Vector3[] corner = new Vector3[R * C];
            Color[] cornerColour = new Color[R * C];
            for (int i = 0; i < R; i++)
            {
                for (int j = 0; j < C; j++)
                {
                    GridPoint q = g[i, j];
                    float fx = q.x * xscale, fz = start + (q.z - RefStart) * zscale;
                    Hit anc = q.rim ? UnderLocal(bed, at, turn, q.xa * xscale, start + (q.za - RefStart) * zscale, limit) : ownHit[i, j];   // (inside: its own point)
                    float baseY = ownOk[i, j] ? own[i, j] : hips.y;
                    if (q.rim && anc.ok)
                    {
                        baseY = Mathf.Min(baseY, anc.y);
                    }
                    if (!q.rim && q.d > 0f && q.s > 0f && q.s < 1f && Mathf.Abs(q.u) < 0.999f)
                    {
                        // inside: over steps (the band), never into one; the tangent line follows the bed exactly
                        float top = float.NegativeInfinity;
                        for (int a = -2; a <= 2; a++)
                        {
                            for (int c = -1; c <= 1; c++)
                            {
                                int ii = i + a, jj = j + c;
                                if (ii >= 0 && ii < R && jj >= 0 && jj < C && ownOk[ii, jj])
                                {
                                    top = Mathf.Max(top, own[ii, jj]);
                                }
                            }
                        }
                        if (top > float.NegativeInfinity)
                        {
                            baseY = top;
                        }
                    }
                    corner[i * C + j] = new Vector3(fx, baseY + q.d * size - ground, fz);
                    cornerColour[i * C + j] = (anc.ok ? anc : hips).colour;
                }
            }
            // triangles: the top, the wall round the rim, the bottom
            List<Vector3> pos = new List<Vector3>(R * C * 8);
            List<Color> col = new List<Color>(R * C * 8);
            for (int i = 0; i < R - 1; i++)
            {
                for (int j = 0; j < C - 1; j++)
                {
                    Vector3 a = corner[i * C + j], b2 = corner[i * C + j + 1], c = corner[(i + 1) * C + j + 1], d = corner[(i + 1) * C + j];
                    Vector3 mid = (a + b2 + c + d) * 0.25f;
                    Hit h = UnderLocal(bed, at, turn, mid.x, mid.z, limit);
                    Color fc = (h.ok ? h : hips).colour;
                    AddTri(pos, col, a, b2, c, fc);
                    AddTri(pos, col, a, c, d, fc);
                }
            }
            List<int> ring = new List<int>();
            for (int j = 0; j < C; j++)
            {
                ring.Add(j);
            }
            for (int i = 1; i < R; i++)
            {
                ring.Add(i * C + C - 1);
            }
            for (int j = C - 2; j >= 0; j--)
            {
                ring.Add((R - 1) * C + j);
            }
            for (int i = R - 2; i > 0; i--)
            {
                ring.Add(i * C);
            }
            Vector3 bottom = Vector3.zero;
            for (int k = 0; k < ring.Count; k++)
            {
                bottom += corner[ring[k]];
            }
            bottom = bottom / ring.Count - new Vector3(0f, Wall * size, 0f);
            for (int k = 0; k < ring.Count; k++)
            {
                Vector3 a = corner[ring[k]], b2 = corner[ring[(k + 1) % ring.Count]];
                Vector3 al = a - new Vector3(0f, Wall * size, 0f), bl = b2 - new Vector3(0f, Wall * size, 0f);
                Color wc = cornerColour[ring[k]];
                AddTri(pos, col, a, b2, bl, wc);
                AddTri(pos, col, a, bl, al, wc);
                AddTri(pos, col, al, bl, bottom, wc);
            }
            Model lump = MakeModel(pos, col);
            if (lump == null)
            {
                return null;
            }
            List<Model> models = new List<Model> { lump };
            List<Matrix4x4> locals = new List<Matrix4x4> { Matrix4x4.identity };
            List<Color> tints = new List<Color> { Color.white };
            return DynamicModels.BuildMerged(models, locals, tints, 0, null, float.NegativeInfinity, SmoothFigures.On);
        }

        private static void AddTri(List<Vector3> pos, List<Color> col, Vector3 a, Vector3 b, Vector3 c, Color colour)
        {
            pos.Add(a);
            pos.Add(b);
            pos.Add(c);
            col.Add(colour);
            col.Add(colour);
            col.Add(colour);
        }

        // A closed triangle soup as a model: wound outward, face normals, the outline shell as IsoRim builds it (no ground rule:
        // the lump's own heights are over the sleeper's floor, its rim may reach below it).
        private static Model MakeModel(List<Vector3> pos, List<Color> col)
        {
            int n = pos.Count;
            if (n < 3)
            {
                return null;
            }
            double vol = 0;
            for (int i = 0; i + 2 < n; i += 3)
            {
                vol += Vector3.Dot(pos[i], Vector3.Cross(pos[i + 1], pos[i + 2]));
            }
            Model m = new Model();
            m.positions = new Vector3[n];
            m.normals = new Vector3[n];
            m.colors = new Color[n];
            m.tinted = new bool[n];
            for (int i = 0; i + 2 < n; i += 3)
            {
                Vector3 a = pos[i], b = vol < 0 ? pos[i + 2] : pos[i + 1], c = vol < 0 ? pos[i + 1] : pos[i + 2];
                m.positions[i] = a;
                m.positions[i + 1] = b;
                m.positions[i + 2] = c;
                Vector3 nf = Vector3.Cross(b - a, c - a);
                nf = nf.sqrMagnitude > 1e-14f ? nf.normalized : Vector3.up;
                m.normals[i] = m.normals[i + 1] = m.normals[i + 2] = nf;
                m.colors[i] = col[i];
                m.colors[i + 1] = col[i + 1];
                m.colors[i + 2] = col[i + 2];
            }
            Vector3[] lifted = new Vector3[n];
            Vector3 lo = m.positions[0], hi = m.positions[0];
            for (int i = 0; i < n; i++)
            {
                lifted[i] = m.positions[i] + new Vector3(0f, 10f, 0f);   // (out of reach of BuildHull's ground rule)
                lo = Vector3.Min(lo, m.positions[i]);
                hi = Vector3.Max(hi, m.positions[i]);
            }
            m.hull = ObjLoader.HullOf(lifted, m.normals);
            m.maxY = hi.y;
            m.minX = lo.x;
            m.maxX = hi.x;
            m.minZ = lo.z;
            m.maxZ = hi.z;
            m.halfDepth = Mathf.Max(Mathf.Abs(lo.z), Mathf.Abs(hi.z));
            m.id = ObjLoader.NewId();
            return m;
        }

        // For the harness (probe blanketcheck): a sleeper's lump, the build count and time.
        internal static string Report(Pawn pawn)
        {
            Entry e;
            bool has = pawn != null && cache.TryGetValue(pawn.thingIDNumber, out e) && e.parts != null;
            return "blanket " + (has ? "built" : "none") + ", drawn now " + Has(pawn) + ", built " + Built + ", build ms "
                + (BuildTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0");
        }
    }
}
