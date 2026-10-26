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
        // Whether a building drawn from a linked model (a wall from a model pack, a fence: a post in the cell and an arm toward
        // each neighbour) can stand in wall shapes.
        public static bool Takes(Building b)
        {
            if (b == null || b.def == null || b.def.drawerType == DrawerType.RealtimeOnly || b.def.size.x != 1 || b.def.size.z != 1)
            {
                return false;
            }
            ThingForm f = Forms.For(b.def);
            return f.form == Form3D.Model && f.model != null && f.model.def.linked && f.model.variants != null && f.model.variants.Length >= 1;
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
        public static Model For(Thing thing, Model post, Model arm, float yaw, int links, List<Piece> pieces, bool outlineArms)
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
            int diagDirs = 0;   // the diagonals it joins (bit i: Diagonals[i])
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
                    IntVec3 nd = c + d;
                    Building partner = nd.InBounds(map) ? nd.GetEdifice(map) : null;
                    if (partner == null || (WallBoxes.BlockHeightAt(map, nd) <= 0f && !Takes(partner))
                        || WallBoxes.BuiltHeightAt(map, a) > 0f || WallBoxes.BuiltHeightAt(map, b) > 0f)
                    {
                        continue;
                    }
                    Vector2 corner = mid + new Vector2(d.x, d.z) * 0.5f;
                    if (HeldAt(map, corner + new Vector2(d.x * 0.08f, -d.z * 0.08f)) || HeldAt(map, corner + new Vector2(-d.x * 0.08f, d.z * 0.08f)))
                    {
                        diag |= 1 << (d.x * d.z > 0 ? 0 : 1);   // axis 0: south-west to north-east; 1: north-west to south-east
                        diagDirs |= 1 << i;
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
                .Append('/').Append(arms).Append(outlineArms ? 'L' : 'l').Append(diagDirs);
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
            made = Build(post, arm, yaw, diag, diagDirs, phase, arms, pieces, mid, outlineArms);
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
            public float postHalf;   // furthest the post alone reaches from its middle
            public int capMat;   // the material whose colour closes the cuts (the largest tinted surface)
            public Color capColor;
            public bool capTint;
            public string capFrom;
        }

        private static Seg SegFor(Model post, Model arm, float yaw, bool outlineArms)
        {
            long k = ((long)post.id << 32) ^ ((long)(arm != null ? arm.id : 0) << 12) ^ ((long)Mathf.RoundToInt(yaw) << 1) ^ (outlineArms ? 1L : 0L);
            Seg s;
            if (segs.TryGetValue(k, out s))
            {
                return s;
            }
            s = new Seg();
            AddPart(s, post, Quaternion.AngleAxis(yaw, Vector3.up), false, true);
            if (arm != null)
            {
                AddPart(s, arm, Quaternion.AngleAxis(90f, Vector3.up), !outlineArms, true);
                AddPart(s, arm, Quaternion.AngleAxis(270f, Vector3.up), !outlineArms, true);
            }
            Quaternion py = Quaternion.AngleAxis(yaw, Vector3.up);
            for (int i = 0; i < post.positions.Length; i++)
            {
                Vector3 p = py * post.positions[i];
                s.postHalf = Mathf.Max(s.postHalf, Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.z)));
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
            public bool post;   // a corner post: cut at the unmoved sides, leaves no caps
            public int group;   // which run it belongs to: slant axis 0/1, the straight run (post and arms) 10; -1 none
            public float minX, maxX, minZ, maxZ;
        }

        private struct Plane
        {
            public float nx, nz, d;   // inside: nx * x + nz * z + d >= 0
            public float d0;          // d before an open side is moved out
            public bool open, cap;
            public float Dist(Vector3 p) => nx * p.x + nz * p.z + d;
        }

        // Output being built.
        private static readonly List<Vector3> oPos = new List<Vector3>(), oNor = new List<Vector3>();
        private static readonly List<Color> oCol = new List<Color>();
        private static readonly List<bool> oTint = new List<bool>(), oNone = new List<bool>();
        private static readonly List<Vector2> oUv = new List<Vector2>();
        private static readonly List<int> oMat = new List<int>();

        // A cut side's plane (as Plane) with the stretch of it the source was cut along (t across it: across = (-nz, nx)).
        private sealed class Cut
        {
            public float nx, nz, d0;   // the unmoved side
            public float t0, t1;
            public bool posted;
        }

        private static Model Build(Model post, Model arm, float yaw, int diag, int diagDirs, float[] phase, int arms, List<Piece> pieces,
            Vector2 mid, bool outlineArms)
        {
            Seg seg = SegFor(post, arm, yaw, outlineArms);
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
                    AddSource(src, seg, turn, shift, ax);
                }
            }
            if (arms != 0)
            {
                // the straight run: the post (as the cell of a straight run draws it) and its arms, one group
                AddModel(src, post, Quaternion.AngleAxis(yaw, Vector3.up), Vector3.zero, 0, false, false, 10);
            }
            if (arm != null && arms != 0)
            {
                for (int d = 0; d < 4; d++)
                {
                    if ((arms & (1 << d)) != 0)
                    {
                        AddModel(src, arm, Quaternion.AngleAxis(90f * d, Vector3.up), Vector3.zero, post.materials != null ? post.materials.Length : 0,
                            !outlineArms, false, 10);
                    }
                }
            }

            // Where runs meet (a slant and an arm, two slants): each run only on its side of the bisectors round the cell's middle
            // (a mitre), so they do not overlap.
            src = Sectors(src, diagDirs, arms);

            // Pass 1: where the source is cut along open sides (ends, bevels, chamfers at bends).
            List<Cut> cuts = new List<Cut>();
            ClipAll(src, pieces, mid, margin, seg, null, cuts);
            // A post of the wall's own (its linked model's first part) at each end of every such cut, its face on the side and
            // its corner at the end: the corners are framed as the wall frames its own; the side itself is then cut exactly
            // (no cap: the posts cover it).
            int before = src.Count;
            foreach (Cut ct in cuts)
            {
                if (ct.t1 - ct.t0 < 0.05f || post.positions.Length == 0)   // (no post: a wall top's crown, ShapeTops; capped)
                {
                    continue;
                }
                ct.posted = true;
                Vector3 outward = new Vector3(-ct.nx, 0f, -ct.nz);
                Vector3 across = new Vector3(-ct.nz, 0f, ct.nx);
                Vector3 basePt = new Vector3(-ct.nx * ct.d0, 0f, -ct.nz * ct.d0);
                Quaternion turn = Quaternion.LookRotation(outward, Vector3.up) * Quaternion.AngleAxis(yaw, Vector3.up);
                float half = Mathf.Max(0.5f, seg.postHalf);
                const float reach = 0.5f;   // (a short cut: the two posts overlap, each corner framed)
                AddModel(src, post, turn, basePt + across * (ct.t0 + reach) - outward * half, 0, true, true);   // (no outline of its own: its edges inside the wall would line the top)
                AddModel(src, post, turn, basePt + across * (ct.t1 - reach) - outward * half, 0, true, true);   // (no outline of its own: its edges inside the wall would line the top)
            }

            // Pass 2: the model.
            oPos.Clear(); oNor.Clear(); oCol.Clear(); oTint.Clear(); oNone.Clear(); oUv.Clear(); oMat.Clear();
            ClipAll(src, pieces, mid, margin, seg, cuts, null);
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

        private static bool SamePlane(Cut c, float nx, float nz, float d0)
        {
            return Mathf.Abs(c.nx - nx) < 1e-3f && Mathf.Abs(c.nz - nz) < 1e-3f && Mathf.Abs(c.d0 - d0) < 1e-3f;
        }

        private static Cut Posted(List<Cut> cuts, float nx, float nz, float d0)
        {
            if (cuts == null)
            {
                return null;
            }
            foreach (Cut c in cuts)
            {
                if (c.posted && SamePlane(c, nx, nz, d0))
                {
                    return c;
                }
            }
            return null;
        }

        // Clips the source to every piece. Pass 1 (record != null): only notes the cuts along open sides. Pass 2 (posted:
        // pass 1's cuts): writes the faces and the caps; a posted side is cut exactly with no cap; posts are cut at the
        // unmoved sides and leave no caps.
        private static void ClipAll(List<Tri> src, List<Piece> pieces, Vector2 mid, float margin, Seg seg, List<Cut> posted, List<Cut> record)
        {
            List<Plane> planes = new List<Plane>();
            List<Plane> exact = new List<Plane>();
            List<V> poly = new List<V>(16), next = new List<V>(16);
            Dictionary<int, List<KeyValuePair<Vector3, Vector3>>> segsOn = new Dictionary<int, List<KeyValuePair<Vector3, Vector3>>>();
            for (int pi = 0; pi < pieces.Count; pi++)
            {
                Piece pc = pieces[pi];
                planes.Clear();
                exact.Clear();
                int n = pc.poly.Length;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = pc.poly[i] - mid;
                    minX = Mathf.Min(minX, a.x); maxX = Mathf.Max(maxX, a.x); minZ = Mathf.Min(minZ, a.y); maxZ = Mathf.Max(maxZ, a.y);
                    Plane pl;
                    if (!EdgePlane(pc, i, mid, out pl))
                    {
                        continue;
                    }
                    Plane ex = pl;
                    ex.open = false;
                    ex.cap = false;
                    if (pc.open[i] && Posted(posted, pl.nx, pl.nz, pl.d) == null)
                    {
                        pl.d += margin;
                    }
                    else if (pc.open[i])
                    {
                        pl.cap = false;   // posted: the posts close it
                    }
                    planes.Add(pl);
                    exact.Add(ex);
                }
                // Where another piece's open side meets this piece in a hollow corner, its moved-out side cuts this piece too
                // (else the moved-out strip of this piece shows past that side).
                for (int pj = 0; pj < pieces.Count; pj++)
                {
                    if (pj == pi)
                    {
                        continue;
                    }
                    Piece other = pieces[pj];
                    for (int i = 0; i < other.poly.Length; i++)
                    {
                        Plane pl;
                        if (!other.open[i] || !EdgePlane(other, i, mid, out pl) || !Touches(pc, other, i) || !AllInside(pc, mid, pl))
                        {
                            continue;
                        }
                        Plane ex = pl;
                        ex.open = false;
                        ex.cap = false;
                        if (Posted(posted, pl.nx, pl.nz, pl.d) == null)
                        {
                            pl.d += margin;
                        }
                        pl.open = false;
                        pl.cap = false;
                        planes.Add(pl);
                        exact.Add(ex);
                    }
                }
                minX -= margin; maxX += margin; minZ -= margin; maxZ += margin;
                segsOn.Clear();
                foreach (Tri t in src)
                {
                    if (t.maxX < minX || t.minX > maxX || t.maxZ < minZ || t.minZ > maxZ)
                    {
                        continue;
                    }
                    if (record != null && t.post)
                    {
                        continue;
                    }
                    List<Plane> use = t.post ? exact : planes;
                    poly.Clear();
                    poly.Add(t.a); poly.Add(t.b); poly.Add(t.c);
                    for (int j = 0; j < use.Count && poly.Count >= 3; j++)
                    {
                        ClipBy(poly, next, use[j]);
                        List<V> sw = poly; poly = next; next = sw;
                    }
                    if (poly.Count < 3)
                    {
                        continue;
                    }
                    if (record == null)
                    {
                        // An upright face deep inside the shape (where two parts of the source overlap: the slant and an arm)
                        // never shows; its outline would rise over the top as a stray line.
                        bool none = t.none;
                        for (int k = 1; k + 1 < poly.Count; k++)
                        {
                            Emit(poly[0], poly[k], poly[k + 1], t.mat, t.tint, none);
                        }
                    }
                    if (t.post)
                    {
                        continue;
                    }
                    // edges of the clipped face lying on a cut side: where a cap will close the cut
                    Vector3 fn = Vector3.Cross(t.b.p - t.a.p, t.c.p - t.a.p);
                    float fl = fn.magnitude;
                    for (int j = 0; j < planes.Count; j++)
                    {
                        Plane pl = planes[j];
                        if (record != null ? !pl.open : !pl.cap)
                        {
                            continue;
                        }
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
                    Plane pl = planes[kv.Key];
                    if (record != null)
                    {
                        Note(record, pl.nx, pl.nz, pl.d - margin, kv.Value);
                    }
                    else
                    {
                        Cap(pl, kv.Value, seg);
                    }
                }
            }
        }

        // Each run's triangles cut to the part of the cell nearer (by angle round the middle) its own directions than any
        // other run's: a slant axis runs toward the joined diagonals on it, the straight run toward its linked sides. As both
        // are a cell thick, the bisectors are mitres: the faces meet exactly.
        private static List<Tri> Sectors(List<Tri> src, int diagDirs, int arms)
        {
            List<Vector2> dirs = new List<Vector2>();
            List<int> groups = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                if ((diagDirs & (1 << i)) != 0)
                {
                    IntVec3 d = Diagonals[i];
                    dirs.Add(new Vector2(d.x, d.z).normalized);
                    groups.Add(d.x * d.z > 0 ? 0 : 1);
                }
            }
            for (int d = 0; d < 4; d++)
            {
                if ((arms & (1 << d)) != 0)
                {
                    IntVec3 dir = GenAdj.CardinalDirections[d];
                    dirs.Add(new Vector2(dir.x, dir.z));
                    groups.Add(10);
                }
            }
            List<Tri> outList = new List<Tri>(src.Count);
            List<V> poly = new List<V>(16), next = new List<V>(16);
            List<Plane> planes = new List<Plane>();
            foreach (Tri t in src)
            {
                bool alone = true;
                for (int j = 0; j < groups.Count; j++)
                {
                    alone &= groups[j] == t.group;
                }
                if (t.group < 0 || alone)
                {
                    outList.Add(t);
                    continue;
                }
                for (int i = 0; i < dirs.Count; i++)
                {
                    if (groups[i] != t.group)
                    {
                        continue;
                    }
                    planes.Clear();
                    for (int j = 0; j < dirs.Count; j++)
                    {
                        if (groups[j] == t.group)
                        {
                            continue;
                        }
                        Vector2 n = (dirs[i] - dirs[j]).normalized;   // the bisector: nearer dirs[i] on its positive side
                        planes.Add(new Plane { nx = n.x, nz = n.y, d = 0f });
                    }
                    poly.Clear();
                    poly.Add(t.a); poly.Add(t.b); poly.Add(t.c);
                    for (int j = 0; j < planes.Count && poly.Count >= 3; j++)
                    {
                        ClipBy(poly, next, planes[j]);
                        List<V> sw = poly; poly = next; next = sw;
                    }
                    for (int k = 1; k + 1 < poly.Count; k++)
                    {
                        Tri nt = t;
                        nt.a = poly[0];
                        nt.b = poly[k];
                        nt.c = poly[k + 1];
                        Bounds(ref nt);
                        outList.Add(nt);
                    }
                }
            }
            return outList;
        }

        // The plane of side i of a piece (relative to the cell's middle, inside: >= 0), unmoved.
        private static bool EdgePlane(Piece pc, int i, Vector2 mid, out Plane pl)
        {
            int n = pc.poly.Length;
            Vector2 a = pc.poly[i] - mid, b = pc.poly[(i + 1) % n] - mid;
            Vector2 d = b - a;
            float len = d.magnitude;
            pl = default(Plane);
            if (len < 1e-4f)
            {
                return false;
            }
            Vector2 inn = new Vector2(-d.y, d.x) / len;   // left of a -> b: inside (counter-clockwise)
            pl = new Plane { nx = inn.x, nz = inn.y, d = -(inn.x * a.x + inn.y * a.y), open = pc.open[i], cap = !pc.mine[i] };
            pl.d0 = pl.d;
            return true;
        }

        // Whether side i of other ends on a corner of pc.
        private static bool Touches(Piece pc, Piece other, int i)
        {
            Vector2 a = other.poly[i], b = other.poly[(i + 1) % other.poly.Length];
            foreach (Vector2 p in pc.poly)
            {
                if ((p - a).sqrMagnitude < 1e-6f || (p - b).sqrMagnitude < 1e-6f)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool AllInside(Piece pc, Vector2 mid, Plane pl)
        {
            foreach (Vector2 p in pc.poly)
            {
                Vector2 q = p - mid;
                if (pl.nx * q.x + pl.nz * q.y + pl.d < -1e-4f)
                {
                    return false;
                }
            }
            return true;
        }

        // Pass 1: adds the stretch the edges cover to the cut of that (unmoved) plane.
        private static void Note(List<Cut> cuts, float nx, float nz, float d0, List<KeyValuePair<Vector3, Vector3>> edges)
        {
            Vector2 across = new Vector2(-nz, nx);
            float t0 = float.MaxValue, t1 = float.MinValue;
            foreach (KeyValuePair<Vector3, Vector3> e in edges)
            {
                float ta = T(e.Key, across), tb = T(e.Value, across);
                t0 = Mathf.Min(t0, Mathf.Min(ta, tb));
                t1 = Mathf.Max(t1, Mathf.Max(ta, tb));
            }
            foreach (Cut c in cuts)
            {
                if (SamePlane(c, nx, nz, d0))
                {
                    c.t0 = Mathf.Min(c.t0, t0);
                    c.t1 = Mathf.Max(c.t1, t1);
                    return;
                }
            }
            cuts.Add(new Cut { nx = nx, nz = nz, d0 = d0, t0 = t0, t1 = t1 });
        }

        private static void AddSource(List<Tri> src, Seg seg, Quaternion turn, Vector3 shift, int group)
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
                    none = seg.none[t],
                    group = group
                };
                Bounds(ref tri);
                src.Add(tri);
            }
        }

        // A part of the wall's model (an arm, a corner post) turned and moved into the cell; its materials sit matOffset into
        // the segment's.
        private static void AddModel(List<Tri> src, Model m, Quaternion turn, Vector3 shift, int matOffset, bool none, bool post, int group = -1)
        {
            int n = m.positions.Length;
            for (int i = 0; i + 2 < n; i += 3)
            {
                Tri tri = new Tri
                {
                    a = Raw(m, i, turn, shift),
                    b = Raw(m, i + 1, turn, shift),
                    c = Raw(m, i + 2, turn, shift),
                    mat = (m.material != null ? m.material[i] : 0) + matOffset,
                    tint = m.tinted != null && m.tinted[i],
                    none = none || (m.noOutline != null && m.noOutline[i]),
                    post = post,
                    group = group
                };
                Bounds(ref tri);
                src.Add(tri);
            }
        }

        private static V Raw(Model m, int k, Quaternion turn, Vector3 shift)
        {
            return new V
            {
                p = turn * m.positions[k] + shift,
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
