using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 34: wall shapes. Another mod tells IsoRim, cell by cell, that walls stand there in a shape of their own (convex
    // polygons, each drawn as a wall building it names), optionally instead of the cell's own block (a trimmed wall cell).
    // A wall drawn from a model (a pack's linked wall model) stands in its shapes too: its straight run turned along the
    // slant, cut to the shapes (ShapeModels.cs). IsoRim stands them up as its 3D walls: the owner's height (wall height setting, upper floors reaching down to the wall
    // below), its stuff texture and colour (paint included), the cap, outlines and corner lines, sun lighting, the cut-away
    // (each piece goes down with its owner's cell), roofs over them clipped to the shapes (WallShapeRoofs), the gravship scene
    // and culling with the walls layer they are printed in. Which sides show is worked out here: a side shows where the space
    // just outside it holds no other shape or block at least as tall. Generic: no mod names here. First user: the user's own
    // Vanilla Diagonal Walls (its diagonal bands and trimmed wall cells).
    public static partial class Api
    {
        /// <summary>
        /// Registers wall shapes (Api version 34): for a cell, the rule fills <see cref="WallShape"/> with convex polygons
        /// (cell-local, 0..1, x east and z north, either winding), each with the wall building whose look, colour and height
        /// it takes (as IsoRim's block of that building). IsoRim extrudes them as its 3D walls. With ReplacesBlock set, the
        /// cell's own block is left out and only the polygons stand (a trimmed wall cell). Return false (or leave the shape
        /// empty) for cells the rule doesn't touch. Polygons should meet along whole edges or along the cell's edges; a side
        /// is drawn where nothing else stands just outside it. Asked while a block of the map is redrawn (walls, roofs),
        /// on the main thread, only while walls are IsoRim blocks (3D view, textured 3D walls); several times per cell, so
        /// keep it cheap and deterministic. changeFlags (the game's MapMeshFlag bits) are added to what redraws IsoRim's walls
        /// and roofs; buildings and things already do. <see cref="RefreshWallShapes"/> redraws the walls around a cell when
        /// nothing else reports a change. Leave the mod's own flat drawing out with <see cref="HideFlatLayer"/>. An exception is
        /// logged once and the rule is left out. Call once, at startup.
        /// </summary>
        public static void AddWallShapes(WallShapeRule rule, ulong changeFlags = 0) => WallShapes.Add(rule, changeFlags);
    }

    /// <summary>Api 34: fills the shape of a cell (see <see cref="Api.AddWallShapes"/>); false: nothing here.</summary>
    public delegate bool WallShapeRule(Map map, IntVec3 cell, WallShape into);

    /// <summary>Api 34: the 3D wall pieces of one cell (<see cref="Api.AddWallShapes"/>). Reused between calls.</summary>
    public sealed class WallShape
    {
        internal readonly List<Vector2[]> polygons = new List<Vector2[]>();
        internal readonly List<Building> owners = new List<Building>();

        /// <summary>Leave out the cell's own block: only the polygons stand.</summary>
        public bool ReplacesBlock;

        /// <summary>Adds a convex polygon (cell-local, 0..1, x east and z north, either winding) drawn as the wall owner.</summary>
        public void Add(IList<Vector2> polygon, Building owner)
        {
            if (polygon == null || polygon.Count < 3 || polygon.Count > 32 || owner == null)
            {
                return;
            }
            Vector2[] p = new Vector2[polygon.Count];
            for (int i = 0; i < p.Length; i++)
            {
                p[i] = polygon[i];
            }
            polygons.Add(p);
            owners.Add(owner);
        }

        public int Count => polygons.Count;

        internal void Clear()
        {
            polygons.Clear();
            owners.Clear();
            ReplacesBlock = false;
        }
    }

    // The registry behind Api 34 and the shapes of the cells, in map space.
    internal static class WallShapes
    {
        private static readonly List<WallShapeRule> rules = new List<WallShapeRule>();
        private static readonly HashSet<int> failed = new HashSet<int>();
        internal static ulong extraFlags;

        public static bool Any => rules.Count > 0;

        public static void Add(WallShapeRule rule, ulong flags)
        {
            if (rule != null && !rules.Contains(rule))
            {
                rules.Add(rule);
                extraFlags |= flags;
            }
        }

        // The shapes of one cell: counter-clockwise polygons in map cells, their owners, owner cells and heights.
        internal sealed class Cell
        {
            public readonly List<Vector2[]> polys = new List<Vector2[]>();
            public readonly List<Building> owners = new List<Building>();
            public readonly List<float> heights = new List<float>();
            public readonly List<bool> models = new List<bool>();   // the owner is drawn from a linked model (ShapeModels draws it)
            public bool replaces;
            public bool walls;   // some piece is a wall (not only fences): roofs and upper floors follow the shapes

            // The cell as roofs and upper floors see it: null when only fences stand in its shapes.
            public static Cell Walled(Cell c) => c != null && c.walls ? c : null;

            public IntVec3 OwnerCell(int k) => owners[k].Position;

            // Which polygon (if any) holds point p well inside (by margin), tallest first.
            public int Holding(Vector2 p, float margin)
            {
                int best = -1;
                for (int k = 0; k < polys.Count; k++)
                {
                    if (Inside(polys[k], p, margin) && (best < 0 || heights[k] > heights[best]))
                    {
                        best = k;
                    }
                }
                return best;
            }
        }

        private static readonly WallShape scratch = new WallShape();
        private static readonly Cell none = null;

        // Shapes are asked for many times while a block is drawn (each cell, then its neighbours' when its sides are worked
        // out): between Begin and End they are kept per cell. Outside, every call asks the rules again.
        private static int depth;
        private static Map cacheMap;
        private static readonly Dictionary<int, Cell> cache = new Dictionary<int, Cell>();

        public static void Begin(Map map)
        {
            if (depth++ == 0 || cacheMap != map)
            {
                cache.Clear();
                cacheMap = map;
            }
        }

        public static void End()
        {
            if (--depth <= 0)
            {
                depth = 0;
                cache.Clear();
                cacheMap = null;
            }
        }

        // Whether shapes are drawn now (the walls are IsoRim blocks).
        public static bool Drawn
        {
            get
            {
                IsoRimSettings st = IsoRimMod.Settings;
                return rules.Count > 0 && View3D.Active && st.walls3D && st.texturedWalls;
            }
        }

        // The shapes of a cell (null: none), owners drawn as IsoRim blocks only.
        public static Cell At(Map map, IntVec3 c)
        {
            if (rules.Count == 0 || map == null || !c.InBounds(map) || !Drawn)
            {
                return none;
            }
            int key = map.cellIndices.CellToIndex(c);
            Cell cell;
            if (depth > 0 && cacheMap == map && cache.TryGetValue(key, out cell))
            {
                return cell;
            }
            cell = Ask(map, c);
            if (depth > 0 && cacheMap == map)
            {
                cache[key] = cell;
            }
            return cell;
        }

        private static Cell Ask(Map map, IntVec3 c)
        {
            for (int i = 0; i < rules.Count; i++)
            {
                WallShapeRule r = rules[i];
                if (failed.Contains(r.GetHashCode()))
                {
                    continue;
                }
                scratch.Clear();
                try
                {
                    if (!r(map, c, scratch) || scratch.Count == 0)
                    {
                        continue;
                    }
                }
                catch (Exception e)
                {
                    if (failed.Add(r.GetHashCode()))
                    {
                        Log.Error("[IsoRim] A registered wall shape rule failed (left out from now on): " + e);
                    }
                    continue;
                }
                Cell cell = new Cell();
                cell.replaces = scratch.ReplacesBlock;
                for (int k = 0; k < scratch.Count; k++)
                {
                    Building owner = scratch.owners[k];
                    if (owner == null || !owner.Spawned || owner.Map != map)
                    {
                        continue;
                    }
                    ThingForm form = Forms.For(owner.def);
                    bool model = ShapeModels.Takes(owner);   // a wall drawn from a model: its model is cut to the shapes
                    if (!model && (!SectionLayer_IsoRimWalls.Handles(owner) || form.form != Form3D.Block))
                    {
                        continue;
                    }
                    Vector2[] p = scratch.polygons[k];
                    float area = 0f;
                    for (int j = 0; j < p.Length; j++)
                    {
                        Vector2 u = p[j], v = p[(j + 1) % p.Length];
                        area += u.x * v.y - v.x * u.y;
                    }
                    if (Mathf.Abs(area) < 1e-5f)
                    {
                        continue;
                    }
                    Vector2[] q = new Vector2[p.Length];
                    for (int j = 0; j < p.Length; j++)
                    {
                        Vector2 s = area > 0f ? p[j] : p[p.Length - 1 - j];
                        q[j] = new Vector2(c.x + Mathf.Clamp01(s.x), c.z + Mathf.Clamp01(s.y));
                    }
                    cell.polys.Add(q);
                    cell.owners.Add(owner);
                    float tall = WallBoxes.WallHeightOf(form);
                    cell.walls |= tall > 0f;
                    cell.heights.Add(tall > 0f ? WallBoxes.HeightFor(owner, tall) : form.height);   // (a fence: its model's height)
                    cell.models.Add(model);
                }
                if (cell.polys.Count == 0)
                {
                    cell.replaces = false;
                }
                return cell.polys.Count > 0 || cell.replaces ? cell : none;
            }
            return none;
        }

        // The cell's own block is left out (its shapes stand instead).
        public static bool Replaces(Map map, IntVec3 c)
        {
            Cell s = At(map, c);
            return s != null && s.replaces;
        }

        internal static bool Inside(Vector2[] poly, Vector2 p, float margin)
        {
            int n = poly.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % n];
                Vector2 d = b - a;
                float len = d.magnitude;
                if (len < 1e-6f)
                {
                    continue;
                }
                // left of a->b (inside of a counter-clockwise polygon) by at least margin
                if ((d.x * (p.y - a.y) - d.y * (p.x - a.x)) / len < margin)
                {
                    return false;
                }
            }
            return true;
        }

        // What stands at point p (map cells) at least need tall: a shape (cell of its owner) or a block (its cell); false:
        // nothing. Off the map counts as covered (no side there, as blocks at the map edge).
        public static bool Covered(Map map, Vector2 p, float need, out IntVec3 by, out bool shape)
        {
            IntVec3 n = new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.y));
            by = n;
            shape = false;
            if (!n.InBounds(map))
            {
                return true;
            }
            Cell s = At(map, n);
            if (s != null)
            {
                int k = s.Holding(p, 0f);
                if (k >= 0 && s.heights[k] >= need - 0.01f)
                {
                    by = s.OwnerCell(k);
                    shape = true;
                    return true;
                }
                if (s.replaces)
                {
                    return false;
                }
            }
            if (WallBoxes.BlockHeightAt(map, n) >= need - 0.01f)
            {
                return true;
            }
            return false;
        }

        // ---- drawing

        private static readonly List<Vector2> outPoly = new List<Vector2>();
        private static readonly List<WallBoxes.PrismEdge> outEdges = new List<WallBoxes.PrismEdge>();
        private static readonly List<float> cuts = new List<float>();
        private static readonly List<IntVec3> near = new List<IntVec3>();

        // Prints the shapes of a block of the map into the walls layer (between Begin and End).
        public static void Print(SectionLayer layer, Map map, CellRect rect)
        {
            if (rules.Count == 0 || map == null)
            {
                return;
            }
            foreach (IntVec3 c in rect)
            {
                Cell s = At(map, c);
                if (s == null)
                {
                    continue;
                }
                for (int k = 0; k < s.polys.Count; k++)
                {
                    if (!s.models[k])
                    {
                        PrintOne(layer, map, c, s, k);   // (a wall drawn from a model: its model cut to the shapes, ShapeModels)
                    }
                }
            }
        }

        private static void PrintOne(SectionLayer layer, Map map, IntVec3 c, Cell s, int k)
        {
            Vector2[] poly = s.polys[k];
            Building owner = s.owners[k];
            float height = s.heights[k];
            IntVec3 oc = owner.Position;
            if (height <= 0.01f)
            {
                return;
            }
            bool tags = CutTags.Enabled;
            int own = tags && WallBoxes.CanBeCut(owner.def) ? ShaderCut.Lower : 0;

            // Split each side where what stands outside it changes, and see which parts show.
            outPoly.Clear();
            outEdges.Clear();
            int n = poly.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % n];
                Vector2 d = b - a;
                float len = d.magnitude;
                if (len < 1e-4f)
                {
                    continue;
                }
                Vector2 dir = d / len;
                Vector2 o = new Vector2(dir.y, -dir.x);
                Cuts(map, c, poly, a, b, len, dir);
                for (int j = 0; j + 1 < cuts.Count; j++)
                {
                    float t0 = cuts[j], t1 = cuts[j + 1];
                    Vector2 m = a + d * ((t0 + t1) * 0.5f);
                    WallBoxes.PrismEdge e = new WallBoxes.PrismEdge();
                    IntVec3 by;
                    bool byShape;
                    if (!Covered(map, m + o * 0.004f, height, out by, out byShape))
                    {
                        e.open = true;
                    }
                    else if (tags && by != oc && Mathf.Abs(by.x - oc.x) <= 1 && Mathf.Abs(by.z - oc.z) <= 1 && WallBoxes.MayDrop(map, by))
                    {
                        e.cond = ShaderCut.Cond(by - oc);   // shown while the wall against it is cut lower
                    }
                    // merge with the part before when nothing changes
                    int last = outEdges.Count - 1;
                    if (j > 0 && last >= 0 && outEdges[last].open == e.open && outEdges[last].cond == e.cond)
                    {
                        continue;
                    }
                    outPoly.Add(a + d * t0);
                    outEdges.Add(e);
                }
            }
            int m2 = outPoly.Count;
            if (m2 < 3)
            {
                return;
            }
            // Where an open side meets one that isn't: which face carries on outside (its outward direction), whether a
            // corner line goes up there, and whether that face is a block's (which leaves its half of the line out).
            for (int i = 0; i < m2; i++)
            {
                WallBoxes.PrismEdge e = outEdges[i];
                if (!e.open)
                {
                    continue;
                }
                Vector2 a = outPoly[i], b = outPoly[(i + 1) % m2];
                Vector2 dir = (b - a).normalized;
                Vector2 o = new Vector2(dir.y, -dir.x);
                if (!outEdges[(i + m2 - 1) % m2].open)
                {
                    Vector2 join;
                    bool foreign;
                    if (Carry(map, a, dir, height, true, out join, out foreign))
                    {
                        e.joinStart = join;
                        e.lineStart = WallBoxes.Cross2Of(join, o) > 1e-3f;
                        e.foreignStart = foreign && e.lineStart;
                    }
                }
                if (!outEdges[(i + 1) % m2].open)
                {
                    Vector2 join;
                    bool foreign;
                    if (Carry(map, b, dir, height, false, out join, out foreign))
                    {
                        e.joinEnd = join;
                        e.lineEnd = WallBoxes.Cross2Of(o, join) > 1e-3f;
                        e.foreignEnd = foreign && e.lineEnd;
                    }
                }
                outEdges[i] = e;
            }

            Vector2[] pp = outPoly.ToArray();
            WallBoxes.PrismEdge[] pe = outEdges.ToArray();
            WallBoxes.PrintShapePrism(layer, map, c, owner, pp, pe, height, own);
        }

        // Where a side a..b of a polygon in cell c is to be split: at the corners of the shapes round it lying on it, and
        // where it crosses a cell edge (blocks change there). Parameters 0..1 along the side, sorted, into cuts.
        private static void Cuts(Map map, IntVec3 c, Vector2[] self, Vector2 a, Vector2 b, float len, Vector2 dir)
        {
            cuts.Clear();
            cuts.Add(0f);
            cuts.Add(1f);
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    Cell s = At(map, new IntVec3(c.x + dx, 0, c.z + dz));
                    if (s == null)
                    {
                        continue;
                    }
                    for (int k = 0; k < s.polys.Count; k++)
                    {
                        Vector2[] q = s.polys[k];
                        if (q == self)
                        {
                            continue;
                        }
                        for (int j = 0; j < q.Length; j++)
                        {
                            Vector2 v = q[j] - a;
                            float t = (v.x * dir.x + v.y * dir.y) / len;
                            float off = v.x * dir.y - v.y * dir.x;
                            if (t > 1e-3f && t < 1f - 1e-3f && Mathf.Abs(off) < 2e-3f)
                            {
                                cuts.Add(t);
                            }
                        }
                    }
                }
            }
            // whole cell lines crossed by the side
            AddGrid(a.x, b.x, a, b);
            AddGrid(a.y, b.y, a, b, true);
            cuts.Sort();
            for (int i = cuts.Count - 1; i > 0; i--)
            {
                if (cuts[i] - cuts[i - 1] < 1e-3f)
                {
                    cuts.RemoveAt(i);
                }
            }
            if (cuts.Count < 2 || cuts[cuts.Count - 1] < 1f - 1e-3f)
            {
                cuts.Add(1f);
            }
            cuts[cuts.Count - 1] = 1f;
            cuts[0] = 0f;
        }

        private static void AddGrid(float from, float to, Vector2 a, Vector2 b, bool z = false)
        {
            float lo = Mathf.Min(from, to), hi = Mathf.Max(from, to);
            if (hi - lo < 1e-4f)
            {
                return;
            }
            for (int g = Mathf.CeilToInt(lo + 1e-3f); g < hi - 1e-3f; g++)
            {
                cuts.Add((g - from) / (to - from));
            }
        }

        // The face carrying on outside at corner p of an open side going along dir (atStart: p is where the side starts):
        // found by turning round p from the side, through the open air, to the first solid ground; join is that face's
        // outward direction, foreign whether it is a block's face (not a shape's).
        private static bool Carry(Map map, Vector2 p, Vector2 dir, float height, bool atStart, out Vector2 join, out bool foreign)
        {
            join = Vector2.zero;
            foreign = false;
            const float R = 0.03f;
            const int Steps = 90;
            // atStart: turn clockwise from dir (the outside lies right of the side); at the end: anticlockwise from -dir.
            Vector2 from = atStart ? dir : -dir;
            float sign = atStart ? -1f : 1f;
            float prevAngle = 0f;
            for (int s = 1; s <= Steps; s++)
            {
                float ang = sign * (s * 360f / Steps) * Mathf.Deg2Rad;
                Vector2 u = Rotate(from, ang);
                IntVec3 by;
                bool byShape;
                if (!Covered(map, p + u * R, height, out by, out byShape))
                {
                    prevAngle = ang;
                    continue;
                }
                // refine between the last open angle and this one
                float lo = prevAngle, hi = ang;
                for (int it = 0; it < 8; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Covered(map, p + Rotate(from, mid) * R, height, out by, out byShape))
                    {
                        hi = mid;
                    }
                    else
                    {
                        lo = mid;
                    }
                }
                u = Rotate(from, (lo + hi) * 0.5f);
                // snap to the eight compass directions (the shapes' and blocks' faces run that way here)
                float a = Mathf.Atan2(u.y, u.x) * Mathf.Rad2Deg;
                float snapped = Mathf.Round(a / 45f) * 45f;
                if (Mathf.Abs(Mathf.DeltaAngle(a, snapped)) < 6f)
                {
                    u = new Vector2(Mathf.Cos(snapped * Mathf.Deg2Rad), Mathf.Sin(snapped * Mathf.Deg2Rad));
                }
                // atStart: the face arrives at p walking -u (outward: right of -u); at the end it leaves p along u.
                Vector2 walk = atStart ? -u : u;
                join = new Vector2(walk.y, -walk.x);
                Vector2 left = new Vector2(-walk.y, walk.x);
                Vector2 probe = p + u * (R * 1.5f) + left * 0.01f;
                if (Covered(map, probe, height, out by, out byShape))
                {
                    foreign = !byShape;
                }
                return true;
            }
            return false;
        }

        private static Vector2 Rotate(Vector2 v, float rad)
        {
            float cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
            return new Vector2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
        }
    }

    public static partial class WallBoxes
    {
        internal static float Cross2Of(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        // (Api 34, stacked floors) A shape piece upstairs standing on a shape piece of the cell below (a slanted wall through
        // both storeys, where no block stands below): how far it reaches down to that piece's top, as WallColumn does for
        // blocks. 0 when nothing of a shape is under its middle.
        private static float DropOntoShape(Map map, IntVec3 c, Vector2[] poly, float floorH)
        {
            IntVec3 under = Floors.Stacked(map, c, -1);
            if (!under.IsValid || !under.InBounds(map))
            {
                return 0f;
            }
            WallShapes.Cell below = WallShapes.At(map, under);
            if (below == null)
            {
                return 0f;
            }
            Vector2 mid = Vector2.zero;
            foreach (Vector2 p in poly)
            {
                mid += p;
            }
            mid = mid / poly.Length + new Vector2(under.x - c.x, under.z - c.z);
            int k = below.Holding(mid, 0f);
            if (k < 0)
            {
                return 0f;
            }
            float hb;
            int zb;
            Floors.ColumnFloor(map, under, out hb, out zb);
            return Mathf.Max(0f, floorH - hb - below.heights[k]) + 0.004f;
        }

        // (Api 34) One wall shape piece: a prism with the owner's look, colour and height (as PrintBlock: stuffless blocks'
        // atlas tones, mining tint, upper floors reaching down to the wall below), going down with its owner's cell.
        internal static void PrintShapePrism(SectionLayer layer, Map map, IntVec3 c, Building owner, Vector2[] poly, PrismEdge[] edges, float height, int own)
        {
            WallKind kind = KindOf(owner);
            Color tint = owner.DrawColor;
            Color capTint = tint;
            BlockTones(owner, ref tint, ref capTint);
            tint = MineMarks.Tint(owner, tint);
            capTint = MineMarks.Tint(owner, capTint);
            float drop, floorH;
            int zShift;
            if (Floors.WallColumn(map, c, out drop, out floorH, out zShift) && drop <= 0f)
            {
                drop = DropOntoShape(map, c, poly, floorH);   // upstairs over the same piece of wall below (no block there)
            }
            float vBase = (floorH - drop) / SideCellsHigh;
            int whole = CutTags.Mark(layer);
            prismOwner = owner.Position;
            prismFlags = own;
            bool ok;
            try
            {
                ok = PrintPrism(layer, poly, edges, 0f, height, kind, tint, capTint, drop, vBase, zShift);
            }
            finally
            {
                CutTags.Apply(layer, whole, owner.Position, own);
                prismFlags = 0;
            }
            if (ok && drop <= 0f)
            {
                HullCorners.GroundBand(layer, poly, edges);
            }
        }
    }
}
