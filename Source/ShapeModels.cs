using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 34) A wall drawn from a model (a pack's linked wall: a post in the cell and an arm toward each neighbour it joins,
    // WallBoxes.ModelWall) standing in registered wall shapes (a trimmed wall cell: Api.AddWallShapes, e.g. a diagonal join).
    // Its model is made from the wall's own straight run, nothing stretched: the straight segment (post and both arms, as a
    // cell of a straight east-west run draws it) is turned along each slant the wall runs on and repeated along it one cell
    // apart (a diagonal cell holds about 1.41 of them; the repeats sit on one lattice per line on the map, so neighbours along
    // the line meet seamlessly), plus the plain arm toward each straight neighbour it links with. That is cut to the wall's
    // shape pieces (its own cell and the corners of the free cells beside it it owns): every face clipped to the vertical sides
    // of each convex piece (Sutherland-Hodgman per triangle); a side with open air outside is moved out a little, so the
    // model's own faces stay (the shapes are drawn a hair narrower than a model wall), a side against more wall is exact.
    // Each cut is closed by a cap in the wall's own colour (the cross-section of the cut faces), outlined where the side is
    // open. The result is an ordinary model (outline offsets as a loaded model's), kept per model, shape and lattice offset,
    // and baked by ModelBaker as the wall's model in place of post and arms: tint, paint, light, the cut-away tags, roof
    // clipping, damage marks and culling stay as they are. Generic: no mod names here.
    internal static class ShapeModels
    {
        // Whether a building is a wall drawn from a model that can stand in wall shapes.
        public static bool Takes(Building b)
        {
            if (b == null || b.def == null || b.def.drawerType == DrawerType.RealtimeOnly)
            {
                return false;
            }
            ThingForm f = Forms.For(b.def);
            return f.form == Form3D.Model && f.wallTop > 0f && f.model != null && f.model.def.linked && f.model.variants != null
                && f.model.variants.Length >= 1;
        }

        // One shape piece the wall owns: corners in map cells (counter-clockwise) and, per side, whether open air lies outside
        // all of it (open) or some of it (any).
        internal sealed class Piece
        {
            public Vector2[] poly;
            public bool[] open;
            public bool[] any;
            public bool[] mine;   // all of it against more of the same wall (no cap needed: never shows)
        }

        private static readonly Dictionary<string, Model> cache = new Dictionary<string, Model>();
        private static readonly Dictionary<long, Seg> segs = new Dictionary<long, Seg>();
        private static readonly StringBuilder key = new StringBuilder();

        private static readonly IntVec3[] Diagonals = { new IntVec3(1, 0, 1), new IntVec3(1, 0, -1), new IntVec3(-1, 0, -1), new IntVec3(-1, 0, 1) };
        private const int Phases = 64;   // lattice offsets along a slant are kept at steps of 1/64 cell

        public static void Clear()
        {
            cache.Clear();
            segs.Clear();
        }

        // The wall's model cut to its shapes, or null when the wall stands in no shape (drawn as before). post and arm are the
        // parts ModelBaker would draw (crowns already cut), yaw the post's turn, links the sides (bits 1 << d,
        // GenAdj.CardinalDirections order) its arms go to. pieces: the shape pieces it owns (map cells), for its ground band.
        public static Model For(Thing thing, Model post, Model arm, float yaw, int links, List<Piece> pieces)
        {
            pieces.Clear();
            Map map = thing.Map;
            if (map == null || post == null || !WallShapes.Any || !WallShapes.Drawn)
            {
                return null;
            }
            IntVec3 c = thing.Position;
            Vector2 mid = new Vector2(c.x + 0.5f, c.z + 0.5f);
            int diag = 0;
            int arms = 0;
            float[] phase = new float[2];
            WallShapes.Begin(map);
            try
            {
                WallShapes.Cell own = WallShapes.At(map, c);
                if (own == null || !own.replaces)
                {
                    return null;
                }
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        WallShapes.Cell s = WallShapes.At(map, new IntVec3(c.x + dx, 0, c.z + dz));
                        if (s == null)
                        {
                            continue;
                        }
                        for (int k = 0; k < s.polys.Count; k++)
                        {
                            if (s.owners[k] == thing)
                            {
                                pieces.Add(Sides(map, s.polys[k], s.heights[k], c));
                            }
                        }
                    }
                }
                if (pieces.Count == 0)
                {
                    return null;
                }
                // The slants it runs on: a wall in the diagonal neighbour, the two cells beside the corner free of walls, and
                // a shape piece standing in one of them at the corner (the join's band passes there).
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 d = Diagonals[i];
                    IntVec3 a = new IntVec3(c.x + d.x, 0, c.z);
                    IntVec3 b = new IntVec3(c.x, 0, c.z + d.z);
                    if (WallBoxes.BlockHeightAt(map, c + d) <= 0f || WallBoxes.BuiltHeightAt(map, a) > 0f || WallBoxes.BuiltHeightAt(map, b) > 0f)
                    {
                        continue;
                    }
                    Vector2 corner = mid + new Vector2(d.x, d.z) * 0.5f;
                    if (HeldAt(map, corner + new Vector2(d.x * 0.08f, -d.z * 0.08f)) || HeldAt(map, corner + new Vector2(-d.x * 0.08f, d.z * 0.08f)))
                    {
                        diag |= 1 << (d.x * d.z > 0 ? 0 : 1);   // axis 0: south-west to north-east; 1: north-west to south-east
                    }
                }
                // The straight arms: linked sides where a piece reaches the side's middle.
                for (int d = 0; d < 4; d++)
                {
                    if ((links & (1 << d)) == 0)
                    {
                        continue;
                    }
                    IntVec3 dir = GenAdj.CardinalDirections[d];
                    Vector2 p = mid + new Vector2(dir.x, dir.z) * 0.45f;
                    foreach (Piece pc in pieces)
                    {
                        if (WallShapes.Inside(pc.poly, p, -0.01f))
                        {
                            arms |= 1 << d;
                            break;
                        }
                    }
                }
            }
            finally
            {
                WallShapes.End();
            }
            if (diag == 0)
            {
                return null;   // not on a slant: drawn as before
            }
            for (int ax = 0; ax < 2; ax++)
            {
                if ((diag & (1 << ax)) != 0)
                {
                    float t = Vector2.Dot(mid, Axis(ax));
                    phase[ax] = Mathf.Round((t - Mathf.Floor(t)) * Phases) / Phases;
                }
            }

            // The key: models, turn, slants and their lattice offsets, arms, the pieces (relative to the cell) and their sides.
            key.Length = 0;
            key.Append(post.id).Append('/').Append(arm != null ? arm.id : 0).Append('/').Append(Mathf.RoundToInt(yaw)).Append('/')
                .Append(diag).Append('/').Append(Mathf.RoundToInt(phase[0] * Phases)).Append('/').Append(Mathf.RoundToInt(phase[1] * Phases))
                .Append('/').Append(arms);
            foreach (Piece pc in pieces)
            {
                key.Append('|');
                for (int i = 0; i < pc.poly.Length; i++)
                {
                    Vector2 v = pc.poly[i] - mid;
                    key.Append(Mathf.RoundToInt(v.x * 512f)).Append(',').Append(Mathf.RoundToInt(v.y * 512f))
                        .Append(pc.open[i] ? 'o' : pc.any[i] ? 'a' : 'c');
                }
            }
            string k2 = key.ToString();
            Model made;
            if (cache.TryGetValue(k2, out made))
            {
                return made;
            }
            if (cache.Count > 2048)
            {
                cache.Clear();
            }
            made = Build(post, arm, yaw, diag, phase, arms, pieces, mid);
            cache[k2] = made;
            return made;
        }

        // The unit direction of a slant axis (x > 0): 0 south-west to north-east, 1 north-west to south-east.
        private static Vector2 Axis(int ax)
        {
            return ax == 0 ? new Vector2(0.70710678f, 0.70710678f) : new Vector2(0.70710678f, -0.70710678f);
        }

        private static bool HeldAt(Map map, Vector2 p)
        {
            IntVec3 n = new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.y));
            WallShapes.Cell s = WallShapes.At(map, n);
            return s != null && s.Holding(p, 0f) >= 0;
        }

        // A piece and which of its sides have open air outside (sampled along the side, as WallShapes.PrintOne does).
        private static Piece Sides(Map map, Vector2[] poly, float height, IntVec3 own)
        {
            int n = poly.Length;
            Piece pc = new Piece { poly = poly, open = new bool[n], any = new bool[n], mine = new bool[n] };
            for (int i = 0; i < n; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % n];
                Vector2 d = b - a;
                float len = d.magnitude;
                if (len < 1e-4f)
                {
                    continue;
                }
                Vector2 o = new Vector2(d.y, -d.x) / len;
                int openCount = 0, mineCount = 0;
                const int Samples = 5;
                for (int s = 0; s < Samples; s++)
                {
                    Vector2 m = a + d * ((s + 0.5f) / Samples) + o * 0.004f;
                    IntVec3 by;
                    bool byShape;
                    if (!WallShapes.Covered(map, m, height, out by, out byShape))
                    {
                        openCount++;
                    }
                    else if (byShape && by == own)
                    {
                        mineCount++;
                    }
                }
                pc.open[i] = openCount == Samples;
                pc.any[i] = openCount > 0;
                pc.mine[i] = mineCount == Samples;
            }
            return pc;
        }

        // The band along the wall's foot under each open side of its pieces (as a block's, HullCorners.GroundBand).
        public static void GroundBands(SectionLayer layer, List<Piece> pieces)
        {
            foreach (Piece pc in pieces)
            {
                WallBoxes.PrismEdge[] edges = new WallBoxes.PrismEdge[pc.poly.Length];
                for (int i = 0; i < edges.Length; i++)
                {
                    edges[i].open = pc.open[i];
                }
                HullCorners.GroundBand(layer, pc.poly, edges);
            }
        }

        // ------------------------------------------------------------------ building the model

        private struct V
        {
            public Vector3 p, n;
            public Color col;
            public Vector2 uv;
        }

        // The straight segment: post and both arms of a cell in a straight east-west run, the faces on its joints
        // (x = +-0.5, where the next cell carries on) left out; the arms keep no outline of their own (as ModelBaker draws them).
        private sealed class Seg
        {
            public readonly List<V> v = new List<V>();
            public readonly List<int> mat = new List<int>();     // per triangle
            public readonly List<bool> tint = new List<bool>();
            public readonly List<bool> none = new List<bool>();
            public readonly List<ModelMaterial> mats = new List<ModelMaterial>();
            public float half;   // furthest it reaches either side of its line
            public int capMat;   // the material whose colour closes the cuts (the largest tinted surface)
            public Color capColor;
            public bool capTint;
            public string capFrom;
        }

        private static Seg SegFor(Model post, Model arm, float yaw)
        {
            long k = ((long)post.id << 32) ^ ((long)(arm != null ? arm.id : 0) << 12) ^ (long)Mathf.RoundToInt(yaw);
            Seg s;
            if (segs.TryGetValue(k, out s))
            {
                return s;
            }
            s = new Seg();
            AddPart(s, post, Quaternion.AngleAxis(yaw, Vector3.up), false, true);
            if (arm != null)
            {
                AddPart(s, arm, Quaternion.AngleAxis(90f, Vector3.up), true, true);
                AddPart(s, arm, Quaternion.AngleAxis(270f, Vector3.up), true, true);
            }
            // the cap colour: the material with the largest upright surface (what the wall shows on its sides)
            Dictionary<int, float> area = new Dictionary<int, float>();
            for (int t = 0; t < s.mat.Count; t++)
            {
                V a = s.v[t * 3], b = s.v[t * 3 + 1], cc = s.v[t * 3 + 2];
                Vector3 fn = Vector3.Cross(b.p - a.p, cc.p - a.p);
                float ar = fn.magnitude;
                if (ar < 1e-9f || Mathf.Abs(fn.y) > 0.3f * ar)
                {
                    continue;   // tops and bottoms
                }
                float cur;
                area.TryGetValue(s.mat[t], out cur);
                area[s.mat[t]] = cur + ar;
            }
            int best = 0;
            float bestA = -1f;
            foreach (KeyValuePair<int, float> kv in area)
            {
                if (kv.Value > bestA)
                {
                    bestA = kv.Value;
                    best = kv.Key;
                }
            }
            Color sum = Color.clear;
            int cnt = 0;
            bool tinted = false;
            for (int t = 0; t < s.mat.Count; t++)
            {
                if (s.mat[t] == best)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        sum += s.v[t * 3 + j].col;
                        cnt++;
                    }
                    tinted |= s.tint[t];
                }
            }
            ModelMaterial src = s.mats.Count > best ? s.mats[best] : null;
            ModelMaterial cap = new ModelMaterial
            {
                name = "cap", texture = null, tinted = src != null && src.tinted, color = src != null ? src.color : Color.white
            };
            s.mats.Add(cap);
            s.capMat = s.mats.Count - 1;
            s.capColor = cnt > 0 ? sum / cnt : Color.white;
            s.capColor.a = 1f;
            s.capTint = tinted;
            s.capFrom = src != null ? src.name : "?";
            segs[k] = s;
            return s;
        }

        private static void AddPart(Seg s, Model m, Quaternion turn, bool noOutline, bool dropJoints)
        {
            int offset = s.mats.Count;
            if (m.materials != null)
            {
                s.mats.AddRange(m.materials);
            }
            int n = m.positions.Length;
            for (int i = 0; i + 2 < n; i += 3)
            {
                Vector3 nn = turn * m.normals[i];
                if (dropJoints && Mathf.Abs(nn.x) > 0.9f)
                {
                    bool edge = true;
                    for (int k = i; k < i + 3 && edge; k++)
                    {
                        Vector3 p = turn * m.positions[k];
                        edge = Mathf.Abs(p.x) >= 0.5f - 0.02f && p.x * nn.x > 0f;
                    }
                    if (edge)
                    {
                        continue;   // a face on the joint with the next cell
                    }
                }
                for (int k = i; k < i + 3; k++)
                {
                    V v = new V
                    {
                        p = turn * m.positions[k],
                        n = turn * m.normals[k],
                        col = m.colors != null ? m.colors[k] : Color.white,
                        uv = m.uvs != null ? m.uvs[k] : Vector2.zero
                    };
                    s.v.Add(v);
                    s.half = Mathf.Max(s.half, Mathf.Abs(v.p.z));
                }
                s.mat.Add((m.material != null ? m.material[i] : 0) + offset);
                s.tint.Add(m.tinted != null && m.tinted[i]);
                s.none.Add(noOutline || (m.noOutline != null && m.noOutline[i]));
            }
        }

        // A source triangle placed in the cell (cell units, from the cell's middle).
        private struct Tri
        {
            public V a, b, c;
            public int mat;
            public bool tint, none;
            public float minX, maxX, minZ, maxZ;
        }

        private struct Plane
        {
            public float nx, nz, d;   // inside: nx * x + nz * z + d >= 0
            public bool open, cap;
            public float Dist(Vector3 p) => nx * p.x + nz * p.z + d;
        }

        // Output being built.
        private static readonly List<Vector3> oPos = new List<Vector3>(), oNor = new List<Vector3>();
        private static readonly List<Color> oCol = new List<Color>();
        private static readonly List<bool> oTint = new List<bool>(), oNone = new List<bool>();
        private static readonly List<Vector2> oUv = new List<Vector2>();
        private static readonly List<int> oMat = new List<int>();

        private static Model Build(Model post, Model arm, float yaw, int diag, float[] phase, int arms, List<Piece> pieces, Vector2 mid)
        {
            Seg seg = SegFor(post, arm, yaw);
            float margin = 0.04f + Mathf.Max(0f, seg.half - 0.5f);

            // The source: the segment repeated along each slant, and the arm toward each straight neighbour.
            List<Tri> src = new List<Tri>();
            for (int ax = 0; ax < 2; ax++)
            {
                if ((diag & (1 << ax)) == 0)
                {
                    continue;
                }
                Vector2 u = Axis(ax);
                Quaternion turn = Quaternion.FromToRotation(Vector3.right, new Vector3(u.x, 0f, u.y));
                // the line's lattice: segment middles where (point . u) is a whole number; this cell's middle sits phase past one
                for (int i = -3; i <= 3; i++)
                {
                    float s = i - phase[ax];
                    if (Mathf.Abs(s) > 2.3f)
                    {
                        continue;
                    }
                    Vector3 shift = new Vector3(u.x * s, 0f, u.y * s);
                    AddSource(src, seg, turn, shift);
                }
            }
            if (arm != null && arms != 0)
            {
                for (int d = 0; d < 4; d++)
                {
                    if ((arms & (1 << d)) != 0)
                    {
                        AddArm(src, arm, Quaternion.AngleAxis(90f * d, Vector3.up), post);
                    }
                }
            }

            oPos.Clear(); oNor.Clear(); oCol.Clear(); oTint.Clear(); oNone.Clear(); oUv.Clear(); oMat.Clear();
            List<Plane> planes = new List<Plane>();
            List<V> poly = new List<V>(16), next = new List<V>(16);
            Dictionary<int, List<KeyValuePair<Vector3, Vector3>>> segsOn = new Dictionary<int, List<KeyValuePair<Vector3, Vector3>>>();
            foreach (Piece pc in pieces)
            {
                planes.Clear();
                int n = pc.poly.Length;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = pc.poly[i] - mid, b = pc.poly[(i + 1) % n] - mid;
                    Vector2 d = b - a;
                    float len = d.magnitude;
                    minX = Mathf.Min(minX, a.x); maxX = Mathf.Max(maxX, a.x); minZ = Mathf.Min(minZ, a.y); maxZ = Mathf.Max(maxZ, a.y);
                    if (len < 1e-4f)
                    {
                        continue;
                    }
                    Vector2 inn = new Vector2(-d.y, d.x) / len;   // left of a -> b: inside (counter-clockwise)
                    Plane pl = new Plane { nx = inn.x, nz = inn.y, d = -(inn.x * a.x + inn.y * a.y), open = pc.open[i], cap = !pc.mine[i] };
                    if (pc.open[i])
                    {
                        pl.d += margin;
                    }
                    planes.Add(pl);
                }
                minX -= margin; maxX += margin; minZ -= margin; maxZ += margin;
                segsOn.Clear();
                foreach (Tri t in src)
                {
                    if (t.maxX < minX || t.minX > maxX || t.maxZ < minZ || t.minZ > maxZ)
                    {
                        continue;
                    }
                    poly.Clear();
                    poly.Add(t.a); poly.Add(t.b); poly.Add(t.c);
                    for (int j = 0; j < planes.Count && poly.Count >= 3; j++)
                    {
                        ClipBy(poly, next, planes[j]);
                        List<V> sw = poly; poly = next; next = sw;
                    }
                    if (poly.Count < 3)
                    {
                        continue;
                    }
                    Vector3 fn = Vector3.Cross(t.b.p - t.a.p, t.c.p - t.a.p);
                    for (int k = 1; k + 1 < poly.Count; k++)
                    {
                        Emit(poly[0], poly[k], poly[k + 1], t.mat, t.tint, t.none);
                    }
                    // edges of the clipped face lying on a cut side: where a cap will close the cut
                    for (int j = 0; j < planes.Count; j++)
                    {
                        Plane pl = planes[j];
                        float fl = fn.magnitude;
                        if (fl > 1e-9f && Mathf.Abs((fn.x * pl.nx + fn.z * pl.nz) / fl) > 0.995f)
                        {
                            continue;   // a face lying in the side itself
                        }
                        for (int k = 0; k < poly.Count; k++)
                        {
                            Vector3 p0 = poly[k].p, p1 = poly[(k + 1) % poly.Count].p;
                            if (Mathf.Abs(pl.Dist(p0)) < 1e-4f && Mathf.Abs(pl.Dist(p1)) < 1e-4f && (p1 - p0).sqrMagnitude > 1e-10f)
                            {
                                List<KeyValuePair<Vector3, Vector3>> l;
                                if (!segsOn.TryGetValue(j, out l))
                                {
                                    l = new List<KeyValuePair<Vector3, Vector3>>();
                                    segsOn[j] = l;
                                }
                                l.Add(new KeyValuePair<Vector3, Vector3>(p0, p1));
                            }
                        }
                    }
                }
                foreach (KeyValuePair<int, List<KeyValuePair<Vector3, Vector3>>> kv in segsOn)
                {
                    if (planes[kv.Key].cap)
                    {
                        Cap(planes[kv.Key], kv.Value, seg);
                    }
                }
            }
            if (oPos.Count == 0)
            {
                return null;
            }
            Model m = new Model();
            m.positions = oPos.ToArray();
            m.normals = oNor.ToArray();
            m.colors = oCol.ToArray();
            m.tinted = oTint.ToArray();
            m.uvs = oUv.ToArray();
            m.material = oMat.ToArray();
            m.materials = seg.mats.ToArray();
            m.noOutline = oNone.ToArray();
            m.minX = m.minZ = float.MaxValue;
            m.maxX = m.maxZ = float.MinValue;
            for (int i = 0; i < m.positions.Length; i++)
            {
                Vector3 p = m.positions[i];
                m.maxY = Mathf.Max(m.maxY, p.y);
                m.halfDepth = Mathf.Max(m.halfDepth, Mathf.Abs(p.z));
                m.minX = Mathf.Min(m.minX, p.x);
                m.maxX = Mathf.Max(m.maxX, p.x);
                m.minZ = Mathf.Min(m.minZ, p.z);
                m.maxZ = Mathf.Max(m.maxZ, p.z);
            }
            m.hull = ObjLoader.HullOf(m.positions, m.normals);
            m.id = ObjLoader.NewId();
            return m;
        }

        private static void AddSource(List<Tri> src, Seg seg, Quaternion turn, Vector3 shift)
        {
            for (int t = 0; t < seg.mat.Count; t++)
            {
                Tri tri = new Tri
                {
                    a = Moved(seg.v[t * 3], turn, shift),
                    b = Moved(seg.v[t * 3 + 1], turn, shift),
                    c = Moved(seg.v[t * 3 + 2], turn, shift),
                    mat = seg.mat[t],
                    tint = seg.tint[t],
                    none = seg.none[t]
                };
                Bounds(ref tri);
                src.Add(tri);
            }
        }

        // The arm toward a straight neighbour, as ModelBaker draws it (no outline of its own); its materials are the
        // segment's arm materials (they follow the post's in the segment).
        private static void AddArm(List<Tri> src, Model arm, Quaternion turn, Model post)
        {
            int offset = post.materials != null ? post.materials.Length : 0;
            int n = arm.positions.Length;
            for (int i = 0; i + 2 < n; i += 3)
            {
                Tri tri = new Tri
                {
                    a = Raw(arm, i, turn),
                    b = Raw(arm, i + 1, turn),
                    c = Raw(arm, i + 2, turn),
                    mat = (arm.material != null ? arm.material[i] : 0) + offset,
                    tint = arm.tinted != null && arm.tinted[i],
                    none = true
                };
                Bounds(ref tri);
                src.Add(tri);
            }
        }

        private static V Raw(Model m, int k, Quaternion turn)
        {
            return new V
            {
                p = turn * m.positions[k],
                n = turn * m.normals[k],
                col = m.colors != null ? m.colors[k] : Color.white,
                uv = m.uvs != null ? m.uvs[k] : Vector2.zero
            };
        }

        private static V Moved(V v, Quaternion turn, Vector3 shift)
        {
            v.p = turn * v.p + shift;
            v.n = turn * v.n;
            return v;
        }

        private static void Bounds(ref Tri t)
        {
            t.minX = Mathf.Min(t.a.p.x, Mathf.Min(t.b.p.x, t.c.p.x));
            t.maxX = Mathf.Max(t.a.p.x, Mathf.Max(t.b.p.x, t.c.p.x));
            t.minZ = Mathf.Min(t.a.p.z, Mathf.Min(t.b.p.z, t.c.p.z));
            t.maxZ = Mathf.Max(t.a.p.z, Mathf.Max(t.b.p.z, t.c.p.z));
        }

        // Sutherland-Hodgman: the part of poly inside plane into outPoly (corners on the plane snapped onto it).
        private static void ClipBy(List<V> poly, List<V> outPoly, Plane pl)
        {
            outPoly.Clear();
            int n = poly.Count;
            const float Eps = 1e-5f;
            for (int i = 0; i < n; i++)
            {
                V a = poly[i], b = poly[(i + 1) % n];
                float da = pl.Dist(a.p), db = pl.Dist(b.p);
                bool ina = da >= -Eps, inb = db >= -Eps;
                if (ina)
                {
                    outPoly.Add(a);
                }
                if (ina != inb && Mathf.Abs(da - db) > 1e-9f)
                {
                    float t = Mathf.Clamp01(da / (da - db));
                    V m = new V
                    {
                        p = Vector3.Lerp(a.p, b.p, t),
                        n = Vector3.Lerp(a.n, b.n, t),
                        col = Color.Lerp(a.col, b.col, t),
                        uv = Vector2.Lerp(a.uv, b.uv, t)
                    };
                    float dm = pl.Dist(m.p);
                    m.p.x -= pl.nx * dm;   // exactly on the plane, so the cap's corners match
                    m.p.z -= pl.nz * dm;
                    if (m.n.sqrMagnitude > 1e-8f)
                    {
                        m.n.Normalize();
                    }
                    outPoly.Add(m);
                }
            }
        }

        private static void Emit(V a, V b, V c, int mat, bool tint, bool none)
        {
            if (Vector3.Cross(b.p - a.p, c.p - a.p).sqrMagnitude < 1e-12f)
            {
                return;
            }
            Add(a, mat, tint, none);
            Add(b, mat, tint, none);
            Add(c, mat, tint, none);
        }

        private static void Add(V v, int mat, bool tint, bool none)
        {
            oPos.Add(v.p);
            oNor.Add(v.n);
            oCol.Add(v.col);
            oUv.Add(v.uv);
            oMat.Add(mat);
            oTint.Add(tint);
            oNone.Add(none);
        }

        // ------------------------------------------------------------------ caps

        private static readonly List<float> breaks = new List<float>();

        // Closes a cut: the faces the side cut through left edges on it; the cap fills, across the side, from the lowest to
        // the highest of them (their envelopes, worked out at every edge end), in the wall's own colour, facing out of the
        // side. A wall's cross-section stands on the ground up to its top profile, so this needs no closed mesh (model
        // packs' parts overlap and do not share corners). Outlined only on an open side.
        private static void Cap(Plane pl, List<KeyValuePair<Vector3, Vector3>> edges, Seg seg)
        {
            Vector2 across = new Vector2(-pl.nz, pl.nx);
            Vector3 outward = new Vector3(-pl.nx, 0f, -pl.nz);
            // a point on the side at t across: t * across + base, where base is the plane's nearest point to the middle
            Vector2 basePt = new Vector2(-pl.nx * pl.d, -pl.nz * pl.d);
            breaks.Clear();
            foreach (KeyValuePair<Vector3, Vector3> e in edges)
            {
                breaks.Add(T(e.Key, across));
                breaks.Add(T(e.Value, across));
            }
            breaks.Sort();
            for (int i = breaks.Count - 1; i > 0; i--)
            {
                if (breaks[i] - breaks[i - 1] < 1e-4f)
                {
                    breaks.RemoveAt(i);
                }
            }
            if (breaks.Count < 2)
            {
                return;
            }
            float prevT = 0f, prevLo = 0f, prevHi = 0f;
            bool prev = false;
            for (int i = 0; i < breaks.Count; i++)
            {
                float t = breaks[i];
                float lo = float.MaxValue, hi = float.MinValue;
                // between this break and the next: segments must cover the whole interval to count (no bridging of gaps)
                float tn = i + 1 < breaks.Count ? breaks[i + 1] : t;
                float tm = (t + tn) * 0.5f;
                bool covered = false;
                foreach (KeyValuePair<Vector3, Vector3> e in edges)
                {
                    float ta = T(e.Key, across), tb = T(e.Value, across);
                    float a0 = Mathf.Min(ta, tb), a1 = Mathf.Max(ta, tb);
                    if (t < a0 - 1e-4f || t > a1 + 1e-4f)
                    {
                        continue;
                    }
                    float y = Mathf.Abs(tb - ta) < 1e-6f ? Mathf.Max(e.Key.y, e.Value.y) : Mathf.Lerp(e.Key.y, e.Value.y, (t - ta) / (tb - ta));
                    float y2 = Mathf.Abs(tb - ta) < 1e-6f ? Mathf.Min(e.Key.y, e.Value.y) : y;
                    hi = Mathf.Max(hi, y);
                    lo = Mathf.Min(lo, y2);
                    if (tm >= a0 - 1e-4f && tm <= a1 + 1e-4f && tn > t)
                    {
                        covered = true;
                    }
                }
                if (hi < lo)
                {
                    prev = false;
                    continue;
                }
                if (prev && hi - lo + prevHi - prevLo > 1e-4f)
                {
                    Vector3 p0 = At(basePt, across, prevT, prevLo), p1 = At(basePt, across, t, lo);
                    Vector3 p2 = At(basePt, across, t, hi), p3 = At(basePt, across, prevT, prevHi);
                    CapTri(p0, p1, p2, outward, pl.open, seg);
                    CapTri(p0, p2, p3, outward, pl.open, seg);
                }
                prev = covered;
                prevT = t;
                prevLo = lo;
                prevHi = hi;
            }
        }

        private static float T(Vector3 p, Vector2 across) => p.x * across.x + p.z * across.y;

        private static Vector3 At(Vector2 basePt, Vector2 across, float t, float y)
        {
            return new Vector3(basePt.x + across.x * t, y, basePt.y + across.y * t);
        }

        private static void CapTri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, bool outlined, Seg seg)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f)
            {
                Vector3 t = b;
                b = c;
                c = t;
            }
            V va = new V { p = a, n = outward, col = seg.capColor };
            V vb = new V { p = b, n = outward, col = seg.capColor };
            V vc = new V { p = c, n = outward, col = seg.capColor };
            Emit(va, vb, vc, seg.capMat, seg.capTint, !outlined);
        }
    }
}
