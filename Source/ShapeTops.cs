using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 34) The shaped top (Api 11 wall tops: merlons, a ridge, broken stubs) of a block wall standing in wall shapes (a
    // trimmed wall cell, e.g. a diagonal join). The block's body is the shapes' prism (WallShapes); its crown is built as a
    // wall drawn from a model is (ShapeModels): the crown of the straight run (the wall top's own height field, as a cell of a
    // straight run draws it) turned along the slant, repeated one cell apart and cut to the shapes, mitred where it meets the
    // straight run, cuts closed, outlined as models are. Its parts are kept per spec, body height and colours; the result per
    // shape configuration (ShapeModels). Straight cells keep their own field (EmitField). The crown follows the cut-away
    // (ShaderCut.Crown, as PrintCrown), roofs and the fog clip it, a floor right over the wall leaves it out.
    public static partial class WallBoxes
    {
        private static readonly Dictionary<string, Model[]> crownParts = new Dictionary<string, Model[]>();
        private static readonly List<ShapeModels.Piece> crownPieces = new List<ShapeModels.Piece>();

        internal static void PrintShapeCrowns(SectionLayer layer, Map map, CellRect rect)
        {
            if (!WallShapes.Any || map == null)
            {
                return;
            }
            foreach (IntVec3 c in rect)
            {
                WallShapes.Cell s = WallShapes.At(map, c);
                if (s == null || !s.replaces)
                {
                    continue;
                }
                Building b = c.GetEdifice(map);
                if (b == null || b.Position != c || !SectionLayer_IsoRimWalls.Handles(b))
                {
                    continue;
                }
                ThingForm f = Forms.For(b.def);
                WallTopSpec spec = f.wallTops;
                if (f.form != Form3D.Block || spec == null || spec.rule == WallTopRule.None || spec.rule == WallTopRule.Rubble)
                {
                    continue;
                }
                if ((!FullHeights && IsCut(map, c, b)) || Floors.SlabOver(map, c))
                {
                    continue;   // (old cut-away) a cut wall has no crown; nothing rises through a floor right over it
                }
                try
                {
                    PrintShapeCrown(layer, map, b, spec, f.height);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Drawing a wall top along a wall shape failed: " + e, 93611297);
                }
            }
        }

        private static void PrintShapeCrown(SectionLayer layer, Map map, Building b, WallTopSpec spec, float body)
        {
            IntVec3 c = b.Position;
            Color tint = b.DrawColor;
            Color capTint = tint;
            BlockTones(b, ref tint, ref capTint);
            tint = MineMarks.Tint(b, tint);
            capTint = MineMarks.Tint(b, capTint);
            Model[] parts = CrownParts(spec, body, tint, capTint);
            if (parts == null)
            {
                return;
            }
            // the straight links, as the wall top's own field joins (a neighbour as tall as built, or a door)
            int links = 0;
            for (int d = 0; d < 4; d++)
            {
                IntVec3 n = c + GenAdj.CardinalDirections[d];
                if (!n.InBounds(map))
                {
                    continue;
                }
                Building nb = n.GetEdifice(map);
                if (nb != null && (nb is Building_Door || BuiltHeightAt(map, n) >= body - 0.01f))
                {
                    links |= 1 << d;
                }
            }
            Model m = ShapeModels.For(b, parts[0], parts[1], 0f, links, crownPieces, true);
            if (m == null)
            {
                return;
            }
            int own = CutTags.Enabled && CanBeCut(b.def) ? ShaderCut.Lower : 0;
            int mark = CutTags.Mark(layer);
            int roofMark = RoofClip.Mark(layer);
            ModelBaker.WritePart(layer, b, m, new Vector3(c.x + 0.5f, 0f, c.z + 0.5f), Quaternion.identity, Vector3.zero, body, Color.white, 1f);
            CutTags.Apply(layer, mark, c, own != 0 ? own | ShaderCut.Crown : 0);   // hidden while the wall is cut down
            RoofClip.Tag(layer, roofMark, c);   // clipped under a roof and in the fog
        }

        // The crown as ShapeModels' parts: [0] an empty post, [1] the arm (the crown of a north-south straight cell's north half,
        // from the middle to the north edge), heights above the body. Null when the rule raises nothing.
        private static Model[] CrownParts(WallTopSpec spec, float body, Color tint, Color capTint)
        {
            Color32 t32 = tint, c32 = capTint;
            string key = spec.GetHashCode() + "/" + Mathf.RoundToInt(body * 1000f) + "/" + Mathf.RoundToInt(View3D.WallHeight * 1000f) + "/"
                + t32.r + "," + t32.g + "," + t32.b + "/" + c32.r + "," + c32.g + "," + c32.b;
            Model[] parts;
            if (crownParts.TryGetValue(key, out parts))
            {
                return parts;
            }
            if (crownParts.Count > 256)
            {
                crownParts.Clear();
            }
            Model arm = CrownArm(spec, body, tint, capTint);
            parts = arm != null ? new[] { EmptyModel(), arm } : null;
            crownParts[key] = parts;
            return parts;
        }

        private static Model EmptyModel()
        {
            Model m = new Model();
            m.positions = new Vector3[0];
            m.normals = new Vector3[0];
            m.colors = new Color[0];
            m.tinted = new bool[0];
            m.uvs = new Vector2[0];
            m.material = new int[0];
            m.materials = new ModelMaterial[0];
            m.hull = new Vector3[0];
            m.id = ObjLoader.NewId();
            return m;
        }

        // The north half of a north-south straight cell's crown (open to the west and east) as boxes over the body: the wall
        // top's field (TopAt) on its 1/16 grid, equal runs merged into rectangles. Cell-relative (the field's map-grid
        // rules taken at the map's origin: exact for periods dividing a cell, as the packs' are).
        private static Model CrownArm(WallTopSpec spec, float body, Color tint, Color capTint)
        {
            TopCell tc = new TopCell();
            tc.shaped = true;
            tc.ground = false;
            tc.spec = spec;
            tc.body = body;
            tc.cap = float.MaxValue;
            tc.cell = IntVec3.Zero;
            tc.open[0] = true;    // W
            tc.open[1] = true;    // E
            tc.open[2] = false;   // S
            tc.open[3] = false;   // N
            tc.inner = 0;
            const int Half = TopGrid / 2;
            float[,] h = new float[TopGrid, Half];
            for (int i = 0; i < TopGrid; i++)
            {
                for (int j = 0; j < Half; j++)
                {
                    h[i, j] = TopAt(tc, (i + 0.5f) / TopGrid, (Half + j + 0.5f) / TopGrid) - body;
                }
            }
            // rectangles of one height: runs along x per row, merged with identical runs of the rows above
            bool[,] used = new bool[TopGrid, Half];
            List<Vector3> pos = new List<Vector3>();
            List<Vector3> nor = new List<Vector3>();
            List<Color> col = new List<Color>();
            List<Vector2> uv = new List<Vector2>();
            List<int> mat = new List<int>();
            for (int j = 0; j < Half; j++)
            {
                for (int i = 0; i < TopGrid; i++)
                {
                    float hh = h[i, j];
                    if (used[i, j] || hh <= TopEps)
                    {
                        continue;
                    }
                    int e = i;
                    while (e + 1 < TopGrid && !used[e + 1, j] && Mathf.Abs(h[e + 1, j] - hh) < TopEps)
                    {
                        e++;
                    }
                    int top = j;
                    while (top + 1 < Half)
                    {
                        bool same = true;
                        for (int k = i; k <= e && same; k++)
                        {
                            same = !used[k, top + 1] && Mathf.Abs(h[k, top + 1] - hh) < TopEps;
                        }
                        if (!same)
                        {
                            break;
                        }
                        top++;
                    }
                    for (int a = i; a <= e; a++)
                    {
                        for (int bb = j; bb <= top; bb++)
                        {
                            used[a, bb] = true;
                        }
                    }
                    float g = 1f / TopGrid;
                    Vector3 min = new Vector3(i * g - 0.5f, 0f, j * g);
                    Vector3 max = new Vector3((e + 1) * g - 0.5f, hh, (top + 1) * g);
                    CrownBox(min, max, spec, body, pos, nor, col, uv, mat);
                }
            }
            if (pos.Count == 0)
            {
                return null;
            }
            Model m = new Model();
            m.positions = pos.ToArray();
            m.normals = nor.ToArray();
            m.colors = col.ToArray();
            m.tinted = new bool[pos.Count];
            m.uvs = uv.ToArray();
            m.material = mat.ToArray();
            Color top32 = capTint * spec.topShade;
            top32.a = 1f;
            Color side = tint * spec.sideShade;
            side.a = 1f;
            for (int k = 0; k < m.colors.Length; k++)
            {
                m.colors[k] = m.material[k] == 0 ? top32 : side;
            }
            Texture2D tex = WallTops.SideTexture(spec);
            ModelMaterial topMat = new ModelMaterial { name = "top", tinted = false, color = top32 };
            ModelMaterial sideMat = new ModelMaterial { name = "side", tinted = false, color = side };
            if (tex != null)
            {
                Color sc = tint * spec.brightness / ModelBaker.TextureLift;
                sc.a = 1f;
                sideMat.texture = "runtime";
                sideMat.runtimeTexture = tex;
                sideMat.runtimeName = "IsoRim_WallTopSide_" + spec.GetHashCode();
                sideMat.color = sc;
            }
            m.materials = new[] { topMat, sideMat };
            m.minX = m.minZ = float.MaxValue;
            m.maxX = m.maxZ = float.MinValue;
            foreach (Vector3 p in m.positions)
            {
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

        // A box over the body: its top (material 0) and four sides (material 1, the side texture laid as the wall top's: across
        // by tile.x, up by tile.y from the floor); no bottom (it stands on the cap).
        private static void CrownBox(Vector3 min, Vector3 max, WallTopSpec spec, float body, List<Vector3> pos, List<Vector3> nor,
            List<Color> col, List<Vector2> uv, List<int> mat)
        {
            Vector3[] n = { Vector3.up, Vector3.forward, Vector3.back, Vector3.right, Vector3.left };
            for (int f = 0; f < n.Length; f++)
            {
                Vector3 normal = n[f];
                Vector3 u = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.up;
                Vector3 v = Vector3.Cross(normal, u);
                Vector3[] q = new Vector3[4];
                for (int k = 0; k < 4; k++)
                {
                    float su = k == 1 || k == 2 ? 1f : -1f;
                    float sv = k >= 2 ? 1f : -1f;
                    Vector3 d = normal + u * su + v * sv;
                    q[k] = new Vector3(d.x > 0f ? max.x : min.x, d.y > 0f ? max.y : min.y, d.z > 0f ? max.z : min.z);
                }
                if (Vector3.Dot(Vector3.Cross(q[1] - q[0], q[2] - q[0]), normal) < 0f)
                {
                    Vector3 t = q[1];
                    q[1] = q[3];
                    q[3] = t;
                }
                int[] order = { 0, 1, 2, 0, 2, 3 };
                for (int k = 0; k < 6; k++)
                {
                    Vector3 p = q[order[k]];
                    pos.Add(p);
                    nor.Add(normal);
                    col.Add(Color.white);
                    mat.Add(f == 0 ? 0 : 1);
                    float across = Mathf.Abs(normal.z) > 0.5f ? p.x : p.z;
                    uv.Add(f == 0 ? Vector2.zero : new Vector2(across / Mathf.Max(0.01f, spec.tile.x), (body + p.y) / Mathf.Max(0.01f, spec.tile.y)));
                }
            }
        }
    }
}
