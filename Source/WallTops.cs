using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 11) Wall tops: a one-cell linked wall drawn on IsoRim's own wall path (WallBoxes.PrintBlock, the block every
    // vanilla wall is), with a top shaped by a rule, for walls whose look is more than a box (castle walls with merlons, a
    // low wall with a ridge, a damaged wall with broken stubs) and for rubble lying on the ground. A model pack declares it
    // on a ModelDef (<wallTop>, WallTopSpec); no model file is needed.
    //
    // Why on the block path: vanilla walls stay seamless because each cell draws only the faces that look onto something
    // lower, with the texture laid by map position and the outline rules of WallBoxes (rim on exposed cap edges, a ground
    // line, end lines up each outside corner). A wall top keeps all of that for its body (the block, as tall as
    // WallTopSpec.height) and adds a height field on a 1/16 cell grid over the cap: its height depends only on which sides of
    // the cell join a neighbour (a neighbour as tall or taller, or a door), which corners are inner corners, the map position
    // and the rule's numbers. Faces, rims and end lines of the raised parts follow the same rules as the blocks (faces only
    // where a column stands over a lower one, across cell borders too, the neighbour's own field deciding), so a run, an L,
    // a T, a cross and a run's end all come out of one rule, with no seams and no lines inside a run.
    //
    // What stands above the body is the crown: it is left out where a floor is built right over the wall (stacked floors:
    // nothing rises above the wall height there), it is hidden while the cut-away lowers the wall (shader cut-away: flag
    // ShaderCut.Crown; the old cut-away leaves it out of a cut wall), and it is clipped under a roof and in the fog
    // (RoofClip.Own) as the crowns of model walls are. Everything about the body (cut-away, Floors.FullBlock, joins, the
    // fog lid, stacked-floor wall columns) follows from its height and fill as for any block.
    //
    // Ground (rule Rubble on a thing the game does not count as a wall: fill 0, standable): Form3D.Ground, drawn by the
    // walls layer from the field alone (no body). It is not a wall anywhere (no wall height, never cut, rooms see through
    // it); only the walls layer and the game's print hook know it.
    public enum WallTopRule
    {
        None,       // a plain block with the spec's height and side texture
        Merlons,    // castle wall: a row of merlons along every open side, every `period` cells on the map grid, a square one at
                    // every outside and inner corner; `band` deep, `length` long, `rise` high
        Ridge,      // low wall: a continuous ridge `rise` high, keeping `band` from every open side (through L, T and cross)
        Ragged,     // damaged wall: broken stubs along the open sides, `band` deep, one per `segment` of the map grid, each of
                    // one of the `steps` heights (by map position); corners one stub each
        Rubble      // rubble: a `bed` thick slab whose open edges are set in by `inset` or twice that per `segment`, with stones
                    // on a `segment` map grid (`chance` of a stone, heights between steps[0] and steps[1])
    }

    // One horizontal strip of a game texture, pixels counted from the top left of the image file.
    public class WallTopStrip
    {
        public string texture;      // a texture path (as graphicData texPath, with the file's extension left off); null: the spec's texture
        public int x;
        public int y;
        public int width;
        public int height;
        public int shift;           // moved this many pixels to the right, wrapping round (a course laid half a brick on)
    }

    public class WallTopSpec
    {
        public float wallHeight = 1.65f;    // the wall height (cells) the heights below are for; they follow the walls drawn now
        public float height = -1f;          // the body (the block): its top, cells; -1: the game's fill x the wall height
        public WallTopRule rule = WallTopRule.None;
        public float rise;                  // Merlons, Ridge: how far the raised part stands over the body (cells)
        public float band = 0.25f;          // Merlons, Ragged: how deep the edge parts reach in from an open side; Ridge: how far
                                            // the ridge keeps from an open side (cells, not scaled)
        public float period = 0.5f;         // Merlons: one merlon every period cells along a side, on the map grid
        public float length = 0.25f;        // Merlons: the length of one merlon (cells)
        public float segment = 0.25f;       // Ragged: the length of one stub; Rubble: the stone and edge grid (cells, map grid)
        public List<float> steps;           // Ragged: the stub heights picked from; Rubble: the lowest and highest stone (cells)
        public float chance = 0.35f;        // Rubble: share of grid squares with a stone
        public float bed = 0.035f;          // Rubble: the slab's thickness (cells)
        public float inset = 0.0625f;       // Rubble: how far an open edge is set in (once or twice, by map position)
        public string texture;              // the side texture's game texture (cut by strips)
        public List<WallTopStrip> strips;   // the side texture, top to bottom: strips cut from game textures at run time and
                                            // laid one under the other (all as wide as the first); none: plain sides
        public Vector2 tile = new Vector2(0.5f, 0.55f);   // the side texture covers tile.x cells across and tile.y cells up
        public float brightness = 1f;       // the side texture's colour (the thing's colour times this)
        public float topShade = 0.92f;      // the cap and the tops of raised parts: the thing's colour times this
        public float partShade = -1f;       // Rubble: the tops of stones (-1: topShade)
        public float sideShade = 0.8f;      // sides without a texture (rubble): the thing's colour times this

        // A height given for wallHeight, at the walls drawn now.
        public float Scaled(float cells)
        {
            return cells * View3D.WallHeight / Mathf.Max(0.01f, wallHeight);
        }
    }

    public static class WallTops
    {
        private static readonly Dictionary<WallTopSpec, Texture2D> sides = new Dictionary<WallTopSpec, Texture2D>();
        private static readonly Dictionary<WallTopSpec, int> ids = new Dictionary<WallTopSpec, int>();

        // A thing that can lie on the ground as rubble: one cell, a building, a linked graphic.
        internal static bool GroundShaped(ThingDef def)
        {
            return def.category == ThingCategory.Building && def.graphicData != null && def.size.x == 1 && def.size.z == 1
                && def.graphicData.linkType != LinkDrawerType.None && !def.IsFrame && !def.IsBlueprint;
        }

        // The lit side material of a spec in a thing's colour, or null for plain sides (no strips, or unreadable).
        public static Material SideMaterial(WallTopSpec spec, Color tint)
        {
            Texture2D tex = SideTexture(spec);
            if (tex == null)
            {
                return null;
            }
            int id;
            if (!ids.TryGetValue(spec, out id))
            {
                id = ids.Count + 1;
                ids[spec] = id;
            }
            Color c = new Color(tint.r * spec.brightness, tint.g * spec.brightness, tint.b * spec.brightness, 1f);
            return R3DMaterials.LitTexture(tex, "IsoRim_WallTop_" + id, c, 1);
        }

        internal static Texture2D SideTexture(WallTopSpec spec)
        {
            Texture2D tex;
            if (sides.TryGetValue(spec, out tex))
            {
                return tex;
            }
            tex = null;
            try
            {
                tex = Build(spec);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Making a wall top's side texture failed: " + e, 93611290);
            }
            sides[spec] = tex;
            return tex;
        }

        // The strips cut out of their textures and laid top to bottom into one small tiling texture (rows bottom first).
        private static Texture2D Build(WallTopSpec spec)
        {
            if (spec.strips == null || spec.strips.Count == 0)
            {
                return null;
            }
            int w = spec.strips[0].width;
            int total = 0;
            for (int s = 0; s < spec.strips.Count; s++)
            {
                total += Math.Max(0, spec.strips[s].height);
            }
            if (w <= 0 || total <= 0)
            {
                return null;
            }
            Color32[] px = new Color32[w * total];
            int row = 0;
            for (int s = spec.strips.Count - 1; s >= 0; s--)
            {
                WallTopStrip st = spec.strips[s];
                string path = st.texture ?? spec.texture;
                Texture2D src = path != null ? ContentFinder<Texture2D>.Get(path, false) : null;
                int sw = 0, sh = 0;
                Color32[] from = src != null ? SpritePixels.Full(src, out sw, out sh) : null;
                if (from == null)
                {
                    ModLog.Warn("[IsoRim] Wall top: cannot read the texture " + (path ?? "(none)") + "; plain sides are drawn.");
                    return null;
                }
                for (int r = st.height - 1; r >= 0; r--)
                {
                    int srcRow = sh - 1 - (st.y + r);   // the file counts rows from the top, the pixels from the bottom
                    for (int x = 0; x < w; x++)
                    {
                        int sx = st.x + Mod(x - st.shift, Math.Max(1, Math.Min(w, st.width)));
                        Color32 p = srcRow >= 0 && srcRow < sh && sx >= 0 && sx < sw ? from[srcRow * sw + sx] : new Color32(0, 0, 0, 255);
                        p.a = 255;
                        px[row * w + x] = p;
                    }
                    row++;
                }
            }
            Texture2D tex = new Texture2D(w, total, TextureFormat.RGBA32, true, false);
            tex.name = "IsoRim_WallTopSide";
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            Aniso.Own(tex);   // level 4, or the anisotropic filtering option's if higher
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        private static int Mod(int a, int n)
        {
            int m = a % n;
            return m < 0 ? m + n : m;
        }

        // Harness: forget the made textures (a new game reads them again).
        public static void Clear()
        {
            sides.Clear();
        }
    }

    public static partial class WallBoxes
    {
        private const int TopGrid = 16;                // height field: 1/16 cell
        private const int Win = TopGrid + 2;           // the cell's field plus one column of each neighbour's
        private const float CrownLine = 0.03f;         // rims and end lines of the raised parts standing on a top
        private const float TopEps = 0.0005f;

        // One cell of the 3 x 3 round the cell drawn: shaped (a wall top) or of one height (BuiltHeightAt).
        private sealed class TopCell
        {
            public bool shaped;
            public bool ground;
            public float uniform;
            public float body;
            public float cap;                          // nothing rises above this (a floor right over it); MaxValue: free
            public WallTopSpec spec;
            public IntVec3 cell;
            public readonly bool[] open = new bool[4]; // W, E, S, N
            public readonly int[] icu = new int[4];
            public readonly int[] icv = new int[4];
            public int inner;
        }

        private static readonly TopCell[] topCells = { new TopCell(), new TopCell(), new TopCell(), new TopCell(), new TopCell(),
            new TopCell(), new TopCell(), new TopCell(), new TopCell() };
        private static readonly float[] window = new float[Win * Win];
        private static readonly IntVec3[] SideDirs = { IntVec3.West, IntVec3.East, IntVec3.South, IntVec3.North };

        // Whether cell n counts as joined to the shaped thing own in c: for a block, a neighbour as tall (as built) or a door;
        // for rubble, any wall, door, rubble or thing it links to.
        private static bool TopJoined(Map map, Building own, bool ground, float body, IntVec3 n)
        {
            if (!n.InBounds(map))
            {
                return false;
            }
            Building nb = n.GetEdifice(map);
            if (nb == null)
            {
                return false;
            }
            if (nb is Building_Door)
            {
                return true;
            }
            if (!ground)
            {
                return BuiltHeightAt(map, n) >= body - 0.01f;
            }
            ThingForm nf = Forms.For(nb.def);
            if (IsWallForm(nf) || nf.form == Form3D.Ground)
            {
                return true;
            }
            LinkFlags a = own.def.graphicData != null ? own.def.graphicData.linkFlags : LinkFlags.None;
            LinkFlags b = nb.def.graphicData != null ? nb.def.graphicData.linkFlags : LinkFlags.None;
            return (a & b) != LinkFlags.None;
        }

        private static void Shape(Map map, IntVec3 c, TopCell tc)
        {
            tc.cell = c;
            tc.shaped = false;
            tc.uniform = 0f;
            tc.spec = null;
            if (!c.InBounds(map))
            {
                return;
            }
            Building b = c.GetEdifice(map);
            if (b == null)
            {
                return;
            }
            ThingForm f = Forms.For(b.def);
            if (f.wallTops == null || (f.form != Form3D.Block && f.form != Form3D.Ground) || !SectionLayer_IsoRimWalls.Handles(b))
            {
                tc.uniform = BuiltHeightAt(map, c);
                return;
            }
            tc.shaped = true;
            tc.spec = f.wallTops;
            tc.ground = f.form == Form3D.Ground;
            tc.body = tc.ground ? 0f : f.height;
            tc.cap = !tc.ground && Floors.SlabOver(map, c) ? Mathf.Max(tc.body, View3D.WallHeight) : float.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                tc.open[k] = !TopJoined(map, b, tc.ground, tc.body, c + SideDirs[k]);
            }
            tc.inner = 0;
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    if (!tc.open[sx < 0 ? 0 : 1] && !tc.open[sz < 0 ? 2 : 3]
                        && !TopJoined(map, b, tc.ground, tc.body, c + new IntVec3(sx, 0, sz)))
                    {
                        tc.icu[tc.inner] = sx > 0 ? 1 : 0;
                        tc.icv[tc.inner] = sz > 0 ? 1 : 0;
                        tc.inner++;
                    }
                }
            }
        }

        // The height (cells) of cell tc at sample (i, j) of its 16 x 16 grid.
        private static float Sample(TopCell tc, int i, int j)
        {
            if (!tc.shaped)
            {
                return tc.uniform;
            }
            float h = TopAt(tc, (i + 0.5f) / TopGrid, (j + 0.5f) / TopGrid);
            return Mathf.Min(h, tc.cap);
        }

        private static float TopAt(TopCell tc, float u, float v)
        {
            WallTopSpec s = tc.spec;
            float body = tc.body;
            IntVec3 c = tc.cell;
            float wx = c.x + u;
            float wz = c.z + v;
            float D = s.band;
            float dmin = 9f;
            int near = 0;
            int nearSide = -1;
            bool nearW = false;
            bool nearS = false;
            for (int k = 0; k < 4; k++)
            {
                if (!tc.open[k])
                {
                    continue;
                }
                float d = k == 0 ? u : k == 1 ? 1f - u : k == 2 ? v : 1f - v;
                dmin = Mathf.Min(dmin, d);
                if (d < D)
                {
                    if (near == 0)
                    {
                        nearSide = k;
                    }
                    near++;
                    nearW |= k == 0;
                    nearS |= k == 2;
                }
            }
            int cornerHit = -1;
            for (int q = 0; q < tc.inner; q++)
            {
                float dq = Mathf.Max(Mathf.Abs(u - tc.icu[q]), Mathf.Abs(v - tc.icv[q]));
                dmin = Mathf.Min(dmin, dq);
                if (dq < D && cornerHit < 0)
                {
                    cornerHit = q;
                }
            }

            switch (s.rule)
            {
                case WallTopRule.Ridge:
                    return body + (dmin >= D ? s.Scaled(s.rise) : 0f);

                case WallTopRule.Merlons:
                {
                    if (dmin >= D)
                    {
                        return body;
                    }
                    if (cornerHit >= 0 || near >= 2)
                    {
                        return body + s.Scaled(s.rise);   // a square merlon in an inner or outside corner
                    }
                    if (near == 0)
                    {
                        return body;
                    }
                    double a = nearSide < 2 ? wz : wx;
                    double off = Math.Abs(a - Math.Round(a / s.period) * s.period);
                    return off <= s.length * 0.5 + 1e-6 ? body + s.Scaled(s.rise) : body;
                }

                case WallTopRule.Ragged:
                {
                    if (dmin >= D || s.steps == null || s.steps.Count == 0)
                    {
                        return body;
                    }
                    if (cornerHit >= 0)
                    {
                        return body + Stub(s, Hash01(c.x + tc.icu[cornerHit], c.z + tc.icv[cornerHit], 7));
                    }
                    if (near >= 2)
                    {
                        return body + Stub(s, Hash01(c.x + (nearW ? 0 : 1), c.z + (nearS ? 0 : 1), 7));
                    }
                    if (near == 0)
                    {
                        return body;
                    }
                    int line = SideLine(c, nearSide);
                    float a = nearSide < 2 ? wz : wx;
                    int seg = Mathf.FloorToInt(a / s.segment + 1e-6f);
                    return body + Stub(s, Hash01(seg, line, nearSide >= 2 ? 3 : 5));
                }

                case WallTopRule.Rubble:
                {
                    for (int k = 0; k < 4; k++)
                    {
                        if (!tc.open[k])
                        {
                            continue;
                        }
                        float d = k == 0 ? u : k == 1 ? 1f - u : k == 2 ? v : 1f - v;
                        float a = k < 2 ? wz : wx;
                        int q = Mathf.FloorToInt(a / s.segment + 1e-6f);
                        if (d < s.inset * (1 + (int)(Hash01(q, SideLine(c, k), 17) * 2f)))
                        {
                            return 0f;   // a ragged edge, set in once or twice by map position
                        }
                    }
                    int gx = Mathf.FloorToInt(wx / s.segment);
                    int gz = Mathf.FloorToInt(wz / s.segment);
                    if (s.steps != null && s.steps.Count >= 2 && Hash01(gx, gz, 11) < s.chance)
                    {
                        float lo = s.steps[0];
                        float hi = s.steps[1];
                        return body + s.Scaled((float)Math.Round(lo + (hi - lo) * Hash01(gx, gz, 13), 3));
                    }
                    return body + s.Scaled(s.bed);
                }

                default:
                    return body;
            }
        }

        // The map line of open side k of cell c (W, E: an x line; S, N: a z line).
        private static int SideLine(IntVec3 c, int k)
        {
            return k >= 2 ? c.z + (k == 2 ? 0 : 1) : c.x + (k == 0 ? 0 : 1);
        }

        private static float Stub(WallTopSpec s, float h01)
        {
            return s.Scaled(s.steps[Mathf.Clamp((int)(h01 * s.steps.Count), 0, s.steps.Count - 1)]);
        }

        private static uint HashMix(uint h, int v)
        {
            unchecked
            {
                h = (h ^ (uint)v) * 16777619u;
                h ^= h >> 13;
                h *= 0x5bd1e995u;
                h ^= h >> 15;
            }
            return h;
        }

        // A number in [0, 1) from three integers (the same as the design previews' hash, so the look matches them).
        private static float Hash01(int a, int b, int c)
        {
            uint h = HashMix(HashMix(HashMix(2166136261u, a), b), c);
            return (h & 0xffffffu) / (float)0x1000000;
        }

        // The heights of the cell's own 16 x 16 field and one column of each neighbour's round it (window, 18 x 18).
        private static void FillWindow(Map map, IntVec3 c)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    Shape(map, c + new IntVec3(dx, 0, dz), topCells[(dx + 1) * 3 + dz + 1]);
                }
            }
            for (int wi = 0; wi < Win; wi++)
            {
                int gi = wi - 1;
                int dx = gi < 0 ? -1 : gi >= TopGrid ? 1 : 0;
                int i = gi - dx * TopGrid;
                for (int wj = 0; wj < Win; wj++)
                {
                    int gj = wj - 1;
                    int dz = gj < 0 ? -1 : gj >= TopGrid ? 1 : 0;
                    int j = gj - dz * TopGrid;
                    window[wi * Win + wj] = Sample(topCells[(dx + 1) * 3 + dz + 1], i, j);
                }
            }
        }

        private static float W(int i, int j)
        {
            return window[(i + 1) * Win + j + 1];
        }

        // The crown of a block with a wall top: everything of its field above the cap (PrintBlock draws the body).
        private static void PrintCrown(SectionLayer layer, Thing t, WallTopSpec spec, float height, Color tint, Color capTint, Color32 lineColor,
            int own, float floorH, int zShift)
        {
            Map map = t.Map;
            IntVec3 c = t.Position;
            if (spec.rule == WallTopRule.None || (!FullHeights && IsCut(map, c, t)))
            {
                return;   // (old cut-away) a cut wall has no crown
            }
            FillWindow(map, c);
            int mark = CutTags.Mark(layer);
            int roofMark = RoofClip.Mark(layer);
            EmitField(layer, c, spec, height, false, tint, capTint, lineColor, floorH, zShift);
            CutTags.Apply(layer, mark, c, own != 0 ? own | ShaderCut.Crown : 0);   // hidden while the wall is cut down
            RoofClip.Tag(layer, roofMark, c);   // clipped under a roof and in the fog
        }

        // (Api 11) Rubble on the ground (Form3D.Ground): the field alone, no body; not a wall.
        public static void PrintGround(SectionLayer layer, Thing t)
        {
            ThingForm form = Forms.For(t.def);
            WallTopSpec spec = form.wallTops;
            if (spec == null)
            {
                return;
            }
            Map map = t.Map;
            IntVec3 c = t.Position;
            Color tint = MineMarks.Tint(t, t.DrawColor);
            Color32 lineColor = R3DMaterials.Shaded(tint, LineShade);
            FillWindow(map, c);
            int whole = CutTags.Mark(layer);
            EmitField(layer, c, spec, 0f, true, tint, tint, lineColor, 0f, 0);
            CutTags.Apply(layer, whole, c, 0);
        }

        // Tops, faces, rims, ground lines and end lines of every column of the window's own cell standing above baseH, by
        // WallBoxes' own rules: faces only where a column stands over a lower one (in the cell or across its border).
        private static void EmitField(SectionLayer layer, IntVec3 c, WallTopSpec spec, float baseH, bool ground, Color tint, Color capTint,
            Color32 lineColor, float floorH, int zShift)
        {
            Material solidMat = R3DMaterials.Solid;
            LayerSubMesh solid = solidMat != null ? MeshWriter.Sub(layer, solidMat) : null;
            if (solid == null || solid.verts.Count + 6000 > MeshWriter.MaxVerts)
            {
                return;
            }
            Material sideMat = ground ? null : WallTops.SideMaterial(spec, tint);
            LayerSubMesh texSide = sideMat != null ? MeshWriter.Sub(layer, sideMat) : null;
            if (texSide != null && texSide.verts.Count + 3000 > MeshWriter.MaxVerts)
            {
                texSide = null;
            }
            float squash = View3D.Squash;
            float g = 1f / TopGrid;
            float x0 = c.x;
            float z0 = c.z;
            Color32 topCol = R3DMaterials.Shaded(capTint, spec.topShade);
            Color32 partCol = R3DMaterials.Shaded(capTint, spec.partShade >= 0f ? spec.partShade : spec.topShade);
            Color32 plainSide = R3DMaterials.Shaded(tint, spec.sideShade);
            float bedTop = baseH + spec.Scaled(spec.bed) + TopEps;
            float lineTop = OutlineWidth * 0.7f / squash;

            // Tops, merged along x.
            for (int j = 0; j < TopGrid; j++)
            {
                int i = 0;
                while (i < TopGrid)
                {
                    float h = W(i, j);
                    if (h <= baseH + TopEps)
                    {
                        i++;
                        continue;
                    }
                    int e = i;
                    while (e + 1 < TopGrid && Mathf.Abs(W(e + 1, j) - h) < TopEps)
                    {
                        e++;
                    }
                    float y = h / squash;
                    float xa = x0 + i * g, xb = x0 + (e + 1) * g, za = z0 + j * g, zb = z0 + (j + 1) * g;
                    MeshWriter.Quad(solid, new Vector3(xa, y, za), new Vector3(xa, y, zb), new Vector3(xb, y, zb), new Vector3(xb, y, za),
                        ground && h > bedTop ? partCol : topCol);
                    i = e + 1;
                }
            }

            // Faces (with their rims and ground lines), across x (axis 0) and across z (axis 1), merged along the boundary.
            for (int axis = 0; axis < 2; axis++)
            {
                for (int a = 0; a <= TopGrid; a++)
                {
                    int b = 0;
                    while (b < TopGrid)
                    {
                        int dir;
                        float lo, hi;
                        bool thin;
                        if (!FaceAt(axis, a, b, baseH, ground, out dir, out lo, out hi, out thin))
                        {
                            b++;
                            continue;
                        }
                        int e = b;
                        int dir2;
                        float lo2, hi2;
                        bool thin2;
                        while (e + 1 < TopGrid && FaceAt(axis, a, e + 1, baseH, ground, out dir2, out lo2, out hi2, out thin2)
                            && dir2 == dir && Mathf.Abs(lo2 - lo) < TopEps && Mathf.Abs(hi2 - hi) < TopEps && thin2 == thin)
                        {
                            e++;
                        }
                        // dir: the face (0 S, 1 N, 2 W, 3 E) looking toward the lower column
                        Vector3 pa = axis == 0 ? new Vector3(x0 + a * g, 0f, z0 + b * g) : new Vector3(x0 + b * g, 0f, z0 + a * g);
                        Vector3 pb = axis == 0 ? new Vector3(x0 + a * g, 0f, z0 + (e + 1) * g) : new Vector3(x0 + (e + 1) * g, 0f, z0 + a * g);
                        Vector3 outward = FaceOut[dir].ToVector3();
                        Vector3 right = FaceRight[dir].ToVector3();
                        Vector3 p0 = Vector3.Dot(pb - pa, right) > 0f ? pa : pb;
                        Vector3 p1 = Vector3.Dot(pb - pa, right) > 0f ? pb : pa;
                        float yLo = lo / squash;
                        float yHi = hi / squash;
                        Vector3 l0 = Raised(p0, yLo), l1 = Raised(p1, yLo), h0 = Raised(p0, yHi), h1 = Raised(p1, yHi);
                        if (texSide != null)
                        {
                            float u0 = Vector3.Dot(p0 - new Vector3(0f, 0f, zShift), right) / spec.tile.x;
                            float u1 = Vector3.Dot(p1 - new Vector3(0f, 0f, zShift), right) / spec.tile.x;
                            float v0 = (floorH + lo) / spec.tile.y;
                            float v1 = (floorH + hi) / spec.tile.y;
                            MeshWriter.TexturedQuad(texSide, l0, h0, h1, l1,
                                new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1), new Vector2(u1, v0), White);
                        }
                        else
                        {
                            MeshWriter.Quad(LitSide(layer, dir, solid), l0, h0, h1, l1, plainSide);
                        }
                        float w = thin ? CrownLine : OutlineWidth;
                        Vector3 inner = outward * -w;
                        float rimY = (hi + 0.002f) / squash;
                        MeshWriter.Quad(solid, Raised(p0, rimY), Raised(p0 + inner, rimY), Raised(p1 + inner, rimY), Raised(p1, rimY), lineColor);
                        if (lo <= TopEps)
                        {
                            Vector3 q0 = Raised(p0 + outward * Gap, 0f);
                            Vector3 q1 = Raised(p1 + outward * Gap, 0f);
                            MeshWriter.Quad(solid, q0, Raised(q0, lineTop), Raised(q1, lineTop), q1, lineColor);
                        }
                        b = e + 1;
                    }
                }
            }

            // End lines up every outside corner of a column, and rim squares in the inner corners of a top.
            for (int i = 0; i < TopGrid; i++)
            {
                for (int j = 0; j < TopGrid; j++)
                {
                    float h = W(i, j);
                    if (h <= baseH + TopEps)
                    {
                        continue;
                    }
                    float yHi = h / squash;
                    for (int sx = -1; sx <= 1; sx += 2)
                    {
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            float o1 = W(i + sx, j), o2 = W(i, j + sz), o3 = W(i + sx, j + sz);
                            float cx = x0 + (i + (sx > 0 ? 1 : 0)) * g;
                            float cz = z0 + (j + (sz > 0 ? 1 : 0)) * g;
                            float zlo = Mathf.Max(o1, Mathf.Max(o2, o3));
                            float low = Mathf.Max(zlo, baseH);
                            if (h > low + TopEps)
                            {
                                float w = ground || zlo >= baseH - TopEps ? CrownLine : OutlineWidth;
                                Vector3 p = new Vector3(cx, low / squash, cz);
                                CornerLine(solid, sx < 0 ? 2 : 3, p, new Vector3(0f, 0f, -sz), yHi, lineColor, w);
                                CornerLine(solid, sz < 0 ? 0 : 1, p, new Vector3(-sx, 0f, 0f), yHi, lineColor, w);
                            }
                            else if (Mathf.Abs(o1 - h) < TopEps && Mathf.Abs(o2 - h) < TopEps && o3 < h - TopEps)
                            {
                                float w = ground || o3 >= baseH - TopEps ? CrownLine : OutlineWidth;
                                float rimY = (h + 0.002f) / squash;
                                float sxa = sx > 0 ? cx - w : cx;
                                float sza = sz > 0 ? cz - w : cz;
                                MeshWriter.Quad(solid, new Vector3(sxa, rimY, sza), new Vector3(sxa, rimY, sza + w),
                                    new Vector3(sxa + w, rimY, sza + w), new Vector3(sxa + w, rimY, sza), lineColor);
                            }
                        }
                    }
                }
            }
        }

        // The face on grid boundary a (axis 0: x = a / 16, between columns a - 1 and a; axis 1: z) at position b along it, if
        // one of the cell's own columns there stands above the other and above baseH: which way it looks, from lo to hi, and
        // whether its rim is the thin crown line (the lower side reaches the cap) or the block's own.
        private static bool FaceAt(int axis, int a, int b, float baseH, bool ground, out int dir, out float lo, out float hi, out bool thin)
        {
            dir = -1;
            lo = hi = 0f;
            thin = false;
            float hA = axis == 0 ? W(a - 1, b) : W(b, a - 1);   // the column before the boundary (west or south)
            float hB = axis == 0 ? W(a, b) : W(b, a);           // after it (east or north)
            bool ownA = a - 1 >= 0;
            bool ownB = a < TopGrid;
            float other;
            if (ownA && hA > Mathf.Max(hB, baseH) + TopEps)
            {
                dir = axis == 0 ? 3 : 1;   // looks east / north
                hi = hA;
                other = hB;
            }
            else if (ownB && hB > Mathf.Max(hA, baseH) + TopEps)
            {
                dir = axis == 0 ? 2 : 0;   // looks west / south
                hi = hB;
                other = hA;
            }
            else
            {
                return false;
            }
            lo = Mathf.Max(other, baseH);
            thin = ground || other >= baseH - TopEps;
            return true;
        }

        // An end line on face f at corner p (its foot), the face running from p in direction into; up to yTop.
        private static void CornerLine(LayerSubMesh solid, int f, Vector3 p, Vector3 into, float yTop, Color32 color, float width)
        {
            Vector3 outward = FaceOut[f].ToVector3();
            Vector3 right = FaceRight[f].ToVector3();
            Vector3 q = p + outward * Gap;
            if (Vector3.Dot(into, right) > 0f)
            {
                EndLine(solid, f, q - right * Gap, q, right, yTop, color, false, width);   // the face's left end
            }
            else
            {
                EndLine(solid, f, q + right * Gap, q, -right, yTop, color, true, width);  // its right end
            }
        }
    }
}
