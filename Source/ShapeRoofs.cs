using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 34) Roofs over wall shapes (WallShapes): in a cell with registered shapes the roof does not cover the whole cell
    // but only what lies inside the walls' outer faces. The cover of such a cell is a set of convex pieces, each under one
    // lid kind (1 house roof, 3 ship roof):
    //   - every shape piece whose owner's cell carries that roof (a wall under the edge of a roof carries it, as blocks do);
    //   - the rest of the cell (the free parts, cut along the shapes' sides): in a cell whose block the shapes replace (a
    //     trimmed wall) each free part takes the roof of the open cell beside it across the cell's edge (the room inside, or
    //     nothing outside); in any other cell the cell's own roof.
    // The roof builders (pitched, flat, ship deck) draw such a cell's surface as usual, clip it to the pieces (Clip), and run
    // their edges (eaves, parapets, rims) along the cover's outline (Outline) instead of the cell's sides. Cells without
    // shapes are drawn as before. Generic: no mod names here.
    internal static class ShapeRoofs
    {
        internal sealed class Piece
        {
            public Vector2[] poly;   // counter-clockwise, map cells
            public int kind;
            public IntVec3 tag;      // the cell whose roof it goes with (shader cut-away)
        }

        internal struct Edge
        {
            public Vector2 a, b;        // along the outline, roof on the left
            public Vector2 o;           // outward
            public Vector2 offA, offB;  // where the edge's outward offset lines meet the next edges' at its ends (unit overhang)
            public Vector2 joinA, joinB; // the outward directions of the outline sides before and after it (o where none)
            public IntVec3 tag;
        }

        // The tri counts of every piece of mesh of a layer, before a cell's roof is written (ClipSince).
        public static int[] MarkTris(SectionLayer layer)
        {
            List<LayerSubMesh> subs = layer.subMeshes;
            int[] m = new int[subs.Count];
            for (int i = 0; i < subs.Count; i++)
            {
                m[i] = subs[i].tris.Count;
            }
            return m;
        }

        // Clips everything written to the layer since the mark to the cover's pieces of this kind.
        public static void ClipSince(SectionLayer layer, int[] mark, List<Piece> cover, int kind)
        {
            List<LayerSubMesh> subs = layer.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                Clip(subs[i], i < mark.Length ? mark[i] : 0, cover, kind);
            }
        }

        private static int depth;
        private static Map cacheMap;
        private static Func<int, int, int> kinds;
        private static readonly Dictionary<int, List<Piece>> cache = new Dictionary<int, List<Piece>>();
        private static readonly List<Piece> noPieces = new List<Piece>();

        // While a block's roofs are built (kindAt: the lid kinds round it; SectionLayer_IsoRimRoofs).
        public static void Begin(Map map, Func<int, int, int> kindAt)
        {
            WallShapes.Begin(map);
            depth++;
            cache.Clear();
            cacheMap = map;
            kinds = kindAt;
        }

        public static void End()
        {
            WallShapes.End();
            if (--depth <= 0)
            {
                depth = 0;
                cache.Clear();
                cacheMap = null;
                kinds = null;
            }
        }

        public static bool Active => depth > 0 && WallShapes.Any && WallShapes.Drawn;

        private static int KindAt(IntVec3 c) => kinds != null ? kinds(c.x, c.z) : 0;

        // The roof pieces over a cell with wall shapes; null for a cell without (its grid lid as usual).
        public static List<Piece> CoverOf(Map map, IntVec3 c)
        {
            if (!Active || map != cacheMap || !c.InBounds(map))
            {
                return null;
            }
            int key = map.cellIndices.CellToIndex(c);
            List<Piece> cover;
            if (cache.TryGetValue(key, out cover))
            {
                return cover;
            }
            WallShapes.Cell s = WallShapes.Cell.Walled(WallShapes.At(map, c));   // (fences alone: roofs as before)
            cover = s != null ? Compute(map, c, s) : null;
            cache[key] = cover;
            return cover;
        }

        // The lid kind a cell with shapes is drawn as: its own, or (a cell outside any roof) its pieces'.
        public static int KindOf(List<Piece> cover, int own)
        {
            if (own != 0 || cover == null)
            {
                return own;
            }
            for (int i = 0; i < cover.Count; i++)
            {
                if (cover[i].kind != 0)
                {
                    return cover[i].kind;
                }
            }
            return 0;
        }

        private static bool Roofish(int kind) => kind == 1 || kind == ShipRoofs.Kind;

        private static List<Piece> Compute(Map map, IntVec3 c, WallShapes.Cell s)
        {
            List<Piece> cover = new List<Piece>();
            for (int k = 0; k < s.polys.Count; k++)
            {
                IntVec3 oc = s.OwnerCell(k);
                int kind = KindAt(oc);
                if (Roofish(kind))
                {
                    cover.Add(new Piece { poly = s.polys[k], kind = kind, tag = oc });
                }
            }
            List<List<Vector2>> free = FreeParts(c, s);
            if (free.Count == 0)
            {
                return cover;
            }
            int[] decided = new int[free.Count];   // -1 undecided, else the lid kind (0: none)
            IntVec3[] tags = new IntVec3[free.Count];
            int own = KindAt(c);
            for (int i = 0; i < free.Count; i++)
            {
                if (!s.replaces)
                {
                    decided[i] = own;
                    tags[i] = c;
                    continue;
                }
                decided[i] = -1;
                List<Vector2> part = free[i];
                for (int j = 0; j < part.Count; j++)
                {
                    Vector2 a = part[j], b = part[(j + 1) % part.Count];
                    IntVec3 across;
                    if (!OnCellSide(c, a, b, out across))
                    {
                        continue;
                    }
                    int k = OpenRoof(map, across);
                    if (decided[i] <= 0)
                    {
                        decided[i] = k;
                        tags[i] = across;
                    }
                }
            }
            // parts inside the cell take the decision of a free part beside them
            for (int pass = 0; pass < free.Count; pass++)
            {
                bool changed = false;
                for (int i = 0; i < free.Count; i++)
                {
                    if (decided[i] >= 0)
                    {
                        continue;
                    }
                    for (int j = 0; j < free.Count; j++)
                    {
                        if (decided[j] >= 0 && Adjacent(free[i], free[j]))
                        {
                            decided[i] = decided[j];
                            tags[i] = tags[j];
                            changed = true;
                            break;
                        }
                    }
                }
                if (!changed)
                {
                    break;
                }
            }
            for (int i = 0; i < free.Count; i++)
            {
                if (decided[i] > 0 && Roofish(decided[i]))
                {
                    cover.Add(new Piece { poly = free[i].ToArray(), kind = decided[i], tag = tags[i] });
                }
            }
            return cover;
        }

        // The parts of a cell outside its shapes, cut along the shapes' sides (convex, counter-clockwise, map cells).
        internal static List<List<Vector2>> FreeParts(IntVec3 c, WallShapes.Cell s)
        {
            List<List<Vector2>> parts = new List<List<Vector2>>
            {
                new List<Vector2> { new Vector2(c.x, c.z), new Vector2(c.x + 1, c.z), new Vector2(c.x + 1, c.z + 1), new Vector2(c.x, c.z + 1) }
            };
            for (int k = 0; k < s.polys.Count; k++)
            {
                Vector2[] p = s.polys[k];
                for (int j = 0; j < p.Length; j++)
                {
                    Vector2 a = p[j], b = p[(j + 1) % p.Length];
                    if ((b - a).sqrMagnitude < 1e-8f)
                    {
                        continue;
                    }
                    List<List<Vector2>> next = new List<List<Vector2>>(parts.Count + 2);
                    foreach (List<Vector2> part in parts)
                    {
                        List<Vector2> left, right;
                        SplitLine(part, a, b, out left, out right);
                        if (left.Count >= 3 && Area(left) > 1e-6f && right.Count >= 3 && Area(right) > 1e-6f)
                        {
                            next.Add(left);
                            next.Add(right);
                        }
                        else
                        {
                            next.Add(part);
                        }
                    }
                    parts = next;
                }
            }
            List<List<Vector2>> free = new List<List<Vector2>>();
            foreach (List<Vector2> part in parts)
            {
                if (s.Holding(Centroid(part), 0f) < 0 && Area(part) > 1e-5f)
                {
                    free.Add(part);
                }
            }
            return free;
        }

        // Which free parts of a cell whose block its shapes replace belong to the open cell beside them: decided by the cell
        // across the part's side on the cell's edge (side(n): that cell counts), then passed on to the parts inside.
        internal static bool[] FreeSide(IntVec3 c, List<List<Vector2>> free, Func<IntVec3, bool> side)
        {
            int[] decided = new int[free.Count];
            for (int i = 0; i < free.Count; i++)
            {
                decided[i] = -1;
                List<Vector2> part = free[i];
                for (int j = 0; j < part.Count; j++)
                {
                    IntVec3 across;
                    if (OnCellSide(c, part[j], part[(j + 1) % part.Count], out across) && decided[i] <= 0)
                    {
                        decided[i] = side(across) ? 1 : 0;
                    }
                }
            }
            for (int pass = 0; pass < free.Count; pass++)
            {
                bool changed = false;
                for (int i = 0; i < free.Count; i++)
                {
                    if (decided[i] >= 0)
                    {
                        continue;
                    }
                    for (int j = 0; j < free.Count; j++)
                    {
                        if (decided[j] >= 0 && Adjacent(free[i], free[j]))
                        {
                            decided[i] = decided[j];
                            changed = true;
                            break;
                        }
                    }
                }
                if (!changed)
                {
                    break;
                }
            }
            bool[] r = new bool[free.Count];
            for (int i = 0; i < free.Count; i++)
            {
                r[i] = decided[i] > 0;
            }
            return r;
        }

        // The roof over an open cell beside a trimmed wall: its lid kind if it is no wall (a room's floor, a door, a cell
        // with shapes of its own that are not a wall); 0 otherwise.
        private static int OpenRoof(Map map, IntVec3 n)
        {
            if (!n.InBounds(map))
            {
                return 0;
            }
            int k = KindAt(n);
            if (!Roofish(k))
            {
                return 0;
            }
            WallShapes.Cell s = WallShapes.Cell.Walled(WallShapes.At(map, n));
            if (s != null)
            {
                return s.replaces ? 0 : k;
            }
            Building b = n.GetEdifice(map);
            return b != null && WallBoxes.IsWallForm(Forms.For(b.def)) ? 0 : k;
        }

        private static bool OnCellSide(IntVec3 c, Vector2 a, Vector2 b, out IntVec3 across)
        {
            across = IntVec3.Invalid;
            const float E = 1e-4f;
            if (Mathf.Abs(a.x - c.x) < E && Mathf.Abs(b.x - c.x) < E)
            {
                across = c + IntVec3.West;
            }
            else if (Mathf.Abs(a.x - c.x - 1) < E && Mathf.Abs(b.x - c.x - 1) < E)
            {
                across = c + IntVec3.East;
            }
            else if (Mathf.Abs(a.y - c.z) < E && Mathf.Abs(b.y - c.z) < E)
            {
                across = c + IntVec3.South;
            }
            else if (Mathf.Abs(a.y - c.z - 1) < E && Mathf.Abs(b.y - c.z - 1) < E)
            {
                across = c + IntVec3.North;
            }
            return across.IsValid && (b - a).sqrMagnitude > 1e-8f;
        }

        // Two parts of one cut share a side (two corners in common).
        private static bool Adjacent(List<Vector2> p, List<Vector2> q)
        {
            int same = 0;
            for (int i = 0; i < p.Count; i++)
            {
                for (int j = 0; j < q.Count; j++)
                {
                    if ((p[i] - q[j]).sqrMagnitude < 1e-8f)
                    {
                        same++;
                        break;
                    }
                }
            }
            return same >= 2;
        }

        // ---- where the roof is

        // Point p (map cells) lies under a roof of this kind as drawn (cells with shapes: their pieces).
        public static bool Under(Map map, Vector2 p, int kind)
        {
            IntVec3 n = new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.y));
            if (!n.InBounds(map))
            {
                return false;
            }
            List<Piece> cover = CoverOf(map, n);
            if (cover == null)
            {
                return KindAt(n) == kind;
            }
            for (int i = 0; i < cover.Count; i++)
            {
                if (cover[i].kind == kind && WallShapes.Inside(cover[i].poly, p, 0f))
                {
                    return true;
                }
            }
            return false;
        }

        // ---- the outline

        private static readonly List<float> cuts = new List<float>();

        // The sides of the cover of kind in cell c that the roof ends at (nothing of that roof just outside), roof on the
        // left, with where their overhang lines meet the next side's at each end.
        public static void Outline(Map map, IntVec3 c, int kind, List<Edge> into)
        {
            into.Clear();
            List<Piece> cover = CoverOf(map, c);
            if (cover == null)
            {
                return;
            }
            foreach (Piece pc in cover)
            {
                if (pc.kind != kind)
                {
                    continue;
                }
                Vector2[] p = pc.poly;
                for (int j = 0; j < p.Length; j++)
                {
                    Vector2 a = p[j], b = p[(j + 1) % p.Length];
                    Vector2 d = b - a;
                    float len = d.magnitude;
                    if (len < 1e-4f)
                    {
                        continue;
                    }
                    Vector2 dir = d / len;
                    Vector2 o = new Vector2(dir.y, -dir.x);
                    Cuts(map, c, a, b, len, dir);
                    int start = -1;
                    for (int i = 0; i + 1 < cuts.Count; i++)
                    {
                        Vector2 m = a + d * ((cuts[i] + cuts[i + 1]) * 0.5f);
                        bool open = !Under(map, m + o * 0.004f, kind);
                        if (open && start < 0)
                        {
                            start = i;
                        }
                        if ((!open || i + 2 == cuts.Count) && start >= 0)
                        {
                            int end = open ? i + 1 : i;
                            Edge e = new Edge { a = a + d * cuts[start], b = a + d * cuts[end], o = o, tag = pc.tag };
                            e.offA = Offset(map, e.a, dir, o, kind, true, out e.joinA);
                            e.offB = Offset(map, e.b, dir, o, kind, false, out e.joinB);
                            into.Add(e);
                            start = -1;
                        }
                    }
                }
            }
        }

        private static void Cuts(Map map, IntVec3 c, Vector2 a, Vector2 b, float len, Vector2 dir)
        {
            cuts.Clear();
            cuts.Add(0f);
            cuts.Add(1f);
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    List<Piece> cover = CoverOf(map, new IntVec3(c.x + dx, 0, c.z + dz));
                    if (cover == null)
                    {
                        continue;
                    }
                    foreach (Piece pc in cover)
                    {
                        foreach (Vector2 q in pc.poly)
                        {
                            Vector2 v = q - a;
                            float t = (v.x * dir.x + v.y * dir.y) / len;
                            if (t > 1e-3f && t < 1f - 1e-3f && Mathf.Abs(v.x * dir.y - v.y * dir.x) < 2e-3f)
                            {
                                cuts.Add(t);
                            }
                        }
                    }
                }
            }
            for (int g = Mathf.CeilToInt(Mathf.Min(a.x, b.x) + 1e-3f); g < Mathf.Max(a.x, b.x) - 1e-3f; g++)
            {
                cuts.Add((g - a.x) / (b.x - a.x));
            }
            for (int g = Mathf.CeilToInt(Mathf.Min(a.y, b.y) + 1e-3f); g < Mathf.Max(a.y, b.y) - 1e-3f; g++)
            {
                cuts.Add((g - a.y) / (b.y - a.y));
            }
            cuts.Sort();
            for (int i = cuts.Count - 1; i > 0; i--)
            {
                if (cuts[i] - cuts[i - 1] < 1e-3f)
                {
                    cuts.RemoveAt(i);
                }
            }
            cuts[0] = 0f;
            cuts[cuts.Count - 1] = 1f;
        }

        // At an end p of an outline side (going along dir, outward o): the outward direction of the outline side that
        // carries on there (found by turning round p through the open air to the roof), and from it the corner of the two
        // overhang lines for a unit overhang (o itself where the outline runs on straight or nothing is found).
        private static Vector2 Offset(Map map, Vector2 p, Vector2 dir, Vector2 o, int kind, bool atStart, out Vector2 join)
        {
            join = o;
            const float R = 0.03f;
            const int Steps = 72;
            Vector2 from = atStart ? dir : -dir;
            float sign = atStart ? -1f : 1f;
            float prev = 0f;
            for (int s = 1; s <= Steps; s++)
            {
                float ang = sign * (s * 360f / Steps) * Mathf.Deg2Rad;
                if (!Under(map, p + Rotate(from, ang) * R, kind))
                {
                    prev = ang;
                    continue;
                }
                float lo = prev, hi = ang;
                for (int it = 0; it < 8; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Under(map, p + Rotate(from, mid) * R, kind))
                    {
                        hi = mid;
                    }
                    else
                    {
                        lo = mid;
                    }
                }
                Vector2 u = Rotate(from, (lo + hi) * 0.5f);
                float deg = Mathf.Atan2(u.y, u.x) * Mathf.Rad2Deg;
                float snapped = Mathf.Round(deg / 45f) * 45f;
                if (Mathf.Abs(Mathf.DeltaAngle(deg, snapped)) < 6f)
                {
                    u = new Vector2(Mathf.Cos(snapped * Mathf.Deg2Rad), Mathf.Sin(snapped * Mathf.Deg2Rad));
                }
                Vector2 walk = atStart ? -u : u;
                Vector2 j = new Vector2(walk.y, -walk.x);
                join = j;
                float dot = Vector2.Dot(o, j);
                if (dot < -0.5f)
                {
                    return o;   // a sharp turn back: a square end
                }
                return (o + j) / (1f + dot);
            }
            return o;
        }

        private static Vector2 Rotate(Vector2 v, float rad)
        {
            float cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
            return new Vector2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
        }

        // ---- clipping what a roof builder wrote for a cell

        private struct V
        {
            public Vector3 p, uv, uv2;
            public Color32 col;
        }

        private static readonly List<V> polyA = new List<V>(), polyB = new List<V>();

        // Cuts the triangles written to sm since fromTri down to the pieces of the cover of this kind (seen from above: x and
        // z), keeping their texture coordinates, colours and cut-away tags.
        public static void Clip(LayerSubMesh sm, int fromTri, List<Piece> cover, int kind)
        {
            if (sm == null || fromTri >= sm.tris.Count)
            {
                return;
            }
            int triCount = sm.tris.Count - fromTri;
            int[] tris = new int[triCount];
            sm.tris.CopyTo(fromTri, tris, 0, triCount);
            sm.tris.RemoveRange(fromTri, triCount);
            bool hasUv2 = sm.uvsChannelTwo != null && sm.uvsChannelTwo.Count > 0;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                foreach (Piece pc in cover)
                {
                    if (pc.kind != kind)
                    {
                        continue;
                    }
                    polyA.Clear();
                    for (int k = 0; k < 3; k++)
                    {
                        int i = tris[t + k];
                        polyA.Add(new V
                        {
                            p = sm.verts[i],
                            uv = i < sm.uvs.Count ? sm.uvs[i] : Vector3.zero,
                            uv2 = hasUv2 && i < sm.uvsChannelTwo.Count ? sm.uvsChannelTwo[i] : Vector3.zero,
                            col = i < sm.colors.Count ? sm.colors[i] : new Color32(255, 255, 255, 255)
                        });
                    }
                    if (!ClipTo(pc.poly))
                    {
                        continue;
                    }
                    int start = sm.verts.Count;
                    foreach (V v in polyA)
                    {
                        sm.verts.Add(v.p);
                        sm.uvs.Add(v.uv);
                        sm.colors.Add(v.col);
                        if (hasUv2)
                        {
                            while (sm.uvsChannelTwo.Count < sm.verts.Count - 1)
                            {
                                sm.uvsChannelTwo.Add(Vector3.zero);
                            }
                            sm.uvsChannelTwo.Add(v.uv2);
                        }
                    }
                    for (int k = 1; k + 1 < polyA.Count; k++)
                    {
                        sm.tris.Add(start);
                        sm.tris.Add(start + k);
                        sm.tris.Add(start + k + 1);
                    }
                }
            }
        }

        // polyA cut to the inside of a counter-clockwise convex polygon (x, z); false if nothing is left.
        private static bool ClipTo(Vector2[] poly)
        {
            for (int e = 0; e < poly.Length && polyA.Count >= 3; e++)
            {
                Vector2 a = poly[e], b = poly[(e + 1) % poly.Length];
                Vector2 d = b - a;
                polyB.Clear();
                for (int i = 0; i < polyA.Count; i++)
                {
                    V p = polyA[i], q = polyA[(i + 1) % polyA.Count];
                    float fp = d.x * (p.p.z - a.y) - d.y * (p.p.x - a.x);
                    float fq = d.x * (q.p.z - a.y) - d.y * (q.p.x - a.x);
                    const float E = 1e-6f;
                    if (fp >= -E)
                    {
                        polyB.Add(p);
                    }
                    if ((fp > E && fq < -E) || (fp < -E && fq > E))
                    {
                        float t = fp / (fp - fq);
                        polyB.Add(new V
                        {
                            p = Vector3.Lerp(p.p, q.p, t),
                            uv = Vector3.Lerp(p.uv, q.uv, t),
                            uv2 = t < 0.5f ? p.uv2 : q.uv2,
                            col = Color32.Lerp(p.col, q.col, t)
                        });
                    }
                }
                polyA.Clear();
                polyA.AddRange(polyB);
            }
            return polyA.Count >= 3;
        }

        // ---- small polygon helpers

        // A convex polygon cut to the inside of a counter-clockwise convex polygon (x, z); null if nothing is left.
        public static List<Vector2> ClipPolygon(Vector2[] poly, Vector2[] clip)
        {
            List<Vector2> cur = new List<Vector2>(poly);
            for (int e = 0; e < clip.Length && cur.Count >= 3; e++)
            {
                Vector2 a = clip[e], b = clip[(e + 1) % clip.Length];
                List<Vector2> left, right;
                SplitLine(cur, a, b, out left, out right);
                cur = left;
            }
            if (cur.Count < 3 || Area(cur) < 1e-6f)
            {
                return null;
            }
            return cur;
        }

        // part split by the line through a and b: left (counter-clockwise side) and right.
        private static void SplitLine(List<Vector2> part, Vector2 a, Vector2 b, out List<Vector2> left, out List<Vector2> right)
        {
            left = new List<Vector2>();
            right = new List<Vector2>();
            Vector2 d = b - a;
            int n = part.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = part[i], q = part[(i + 1) % n];
                float fp = d.x * (p.y - a.y) - d.y * (p.x - a.x);
                float fq = d.x * (q.y - a.y) - d.y * (q.x - a.x);
                const float E = 1e-6f;
                if (fp >= -E)
                {
                    left.Add(p);
                }
                if (fp <= E)
                {
                    right.Add(p);
                }
                if ((fp > E && fq < -E) || (fp < -E && fq > E))
                {
                    Vector2 r = p + (q - p) * (fp / (fp - fq));
                    left.Add(r);
                    right.Add(r);
                }
            }
        }

        private static float Area(List<Vector2> p)
        {
            float a = 0f;
            for (int i = 0; i < p.Count; i++)
            {
                Vector2 u = p[i], v = p[(i + 1) % p.Count];
                a += u.x * v.y - v.x * u.y;
            }
            return Mathf.Abs(a) * 0.5f;
        }

        private static Vector2 Centroid(List<Vector2> p)
        {
            Vector2 s = Vector2.zero;
            foreach (Vector2 v in p)
            {
                s += v;
            }
            return s / p.Count;
        }
    }
}
