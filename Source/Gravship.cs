using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using CornerType = RimWorld.SectionLayer_GravshipHull.CornerType;

namespace IsoRim
{
    // Gravships in the 3D view (design: isorim-gravship-design.md, options A + R2).
    //
    // Hull cells are ordinary IsoRim blocks. Where Odyssey draws one of its diagonal hull corner pieces,
    // a 3D wedge stands instead: half of the empty corner cell, the half against its two hull neighbours,
    // with its slanted face looking out. Which cells get one, of which kind, and its colour all come from
    // calling the game's own rule (SectionLayer_GravshipHull.ShouldDrawCornerPiece), so the 3D hull follows
    // the vanilla rules exactly, including future changes to them. Corner (2-thick stair) and Diagonal
    // (1-thick) pieces only differ in how much of the neighbouring wall art the 2D sprite repaints; in 3D
    // both are the same wedge. With the wedges drawn, the game's 8 flat corner colour quads are left out
    // (its mask copies and substructure edge quads stay); with the 3D view off nothing changes.
    //
    // The wedges are printed into the walls layer (SectionLayer_IsoRimWalls), so they follow the wall
    // settings, the cut-away and every redraw of the walls exactly like the blocks; that layer also takes
    // the change flags of the game's hull layer (terrain: a corner cell stops being one when substructure
    // is laid on it).
    [StaticConstructorOnStartup]
    public static class HullCorners
    {
        private static readonly ThingDef hullDef = DefDatabase<ThingDef>.GetNamedSilentFail("GravshipHull");
        private static HashSet<Material> vanillaQuads;
        private static bool quadsLooked;
        private static ulong vanillaFlags;
        private static bool flagsRead;
        private static readonly HashSet<int> unknownLogged = new HashSet<int>();

        static HullCorners()
        {
            if (!ModsConfig.OdysseyActive)
            {
                return;
            }
            try
            {
                var target = AccessTools.Method(typeof(SectionLayer_GravshipHull), nameof(SectionLayer_GravshipHull.Regenerate));
                if (target == null)
                {
                    ModLog.Warn("[IsoRim] SectionLayer_GravshipHull.Regenerate not found: the game's flat hull corners stay under the 3D ones.");
                    return;
                }
                new Harmony("isorim.gravship").Patch(target, postfix: new HarmonyMethod(typeof(HullCorners), nameof(Regenerate_Postfix)));
                ModLog.Note("[IsoRim] Hooked SectionLayer_GravshipHull.Regenerate (3D hull corners).");
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Could not hook the game's gravship hull corners: " + e.Message);
            }
        }

        // Whether hull corners are drawn in 3D: the 3D view with textured 3D walls, and the vanilla hull (Odyssey) or a
        // wall registered with corner wedges (Api 26, WallPieces) drawn as blocks.
        public static bool Active
        {
            get
            {
                IsoRimSettings st = IsoRimMod.Settings;
                if (!View3D.Active || !st.walls3D || !st.texturedWalls)
                {
                    return false;
                }
                return VanillaOn || WallPieces.corners.Count > 0;
            }
        }

        private static bool VanillaOn => hullDef != null && ModsConfig.OdysseyActive && Forms.For(hullDef).form == Form3D.Block;

        // The corner wedge type for two legs (Api 26 rules give legs; the code below works on the vanilla types).
        private static CornerType TypeOf(IntVec3 a, IntVec3 b)
        {
            bool w = a == IntVec3.West || b == IntVec3.West, n = a == IntVec3.North || b == IntVec3.North;
            bool e = a == IntVec3.East || b == IntVec3.East, s = a == IntVec3.South || b == IntVec3.South;
            return w && n ? CornerType.Corner_NW : n && e ? CornerType.Corner_NE : e && s ? CornerType.Corner_SE : s && w ? CornerType.Corner_SW : CornerType.None;
        }

        // A corner piece in the cell: the game's hull rule (ShouldDrawCornerPiece), then the walls registered with corner
        // wedges (Api 26), each only while its wall is drawn as a block; safe at the map edge.
        public static bool At(Map map, IntVec3 c, out CornerType type, out Color color)
        {
            type = CornerType.None;
            color = Color.white;
            if (map == null || !c.InBounds(map))
            {
                return false;
            }
            if (VanillaOn && VanillaAt(map, c, out type, out color))
            {
                return true;
            }
            IntVec3 a, b;
            ThingDef wall;
            if (WallPieces.corners.Count > 0 && WallPieces.Corner(map, c, out a, out b, out color, out wall) && Forms.For(wall).form == Form3D.Block)
            {
                type = TypeOf(a, b);
                return type != CornerType.None;
            }
            type = CornerType.None;
            color = Color.white;
            return false;
        }

        private static bool VanillaAt(Map map, IntVec3 c, out CornerType type, out Color color)
        {
            if (!SectionLayer_GravshipHull.ShouldDrawCornerPiece(c, map, map.terrainGrid, out type, out color))
            {
                return false;
            }
            IntVec3 a, b;
            if (!Legs(type, out a, out b))
            {
                if (unknownLogged.Add((int)type))
                {
                    ModLog.Warn("[IsoRim] Unknown gravship hull corner type " + type + ": drawn flat as in the game.");
                }
                return false;
            }
            return true;
        }

        // The two hull neighbours (directions) a corner piece stands against.
        public static bool Legs(CornerType type, out IntVec3 a, out IntVec3 b)
        {
            switch (type)
            {
                case CornerType.Corner_NW:
                case CornerType.Diagonal_NW:
                    a = IntVec3.West;
                    b = IntVec3.North;
                    return true;
                case CornerType.Corner_NE:
                case CornerType.Diagonal_NE:
                    a = IntVec3.North;
                    b = IntVec3.East;
                    return true;
                case CornerType.Corner_SE:
                case CornerType.Diagonal_SE:
                    a = IntVec3.East;
                    b = IntVec3.South;
                    return true;
                case CornerType.Corner_SW:
                case CornerType.Diagonal_SW:
                    a = IntVec3.South;
                    b = IntVec3.West;
                    return true;
            }
            a = b = IntVec3.Invalid;
            return false;
        }

        // A corner piece is as tall as the lower of its two hull neighbours (so it follows the cut-away).
        public static float HeightAt(Map map, IntVec3 c, CornerType type)
        {
            IntVec3 a, b;
            if (!Legs(type, out a, out b))
            {
                return 0f;
            }
            return Mathf.Min(WallBoxes.BlockHeightAt(map, c + a), WallBoxes.BlockHeightAt(map, c + b));
        }

        // True if a 3D corner piece in cell covers the side of the hull block in cell from that faces it,
        // up to at least the given height (0: just the ground there).
        public static bool Covers(Map map, IntVec3 cell, IntVec3 from, float height)
        {
            if (map == null || !from.InBounds(map) || !cell.InBounds(map) || !Active)
            {
                return false;
            }
            Building fb = from.GetEdifice(map);
            if (fb == null || (fb.def != hullDef && !WallPieces.cornerWalls.Contains(fb.def)) || !SectionLayer_IsoRimWalls.Handles(fb))
            {
                return false;   // only hull cells are legs (the game's rule needs the other two sides not hull)
            }
            if (cell.GetEdifice(map) != null)
            {
                return false;
            }
            CornerType type;
            Color color;
            IntVec3 a, b;
            if (!At(map, cell, out type, out color) || !Legs(type, out a, out b) || (cell + a != from && cell + b != from))
            {
                return false;
            }
            return height <= 0f || HeightAt(map, cell, type) >= height - 0.01f;
        }

        // The walls layer redraws on the game's hull-layer changes too (buildings, terrain, things, roofs).
        public static void AddChangeFlags(SectionLayer layer, Section section)
        {
            layer.relevantChangeTypes |= WallPieces.extraFlags | WallShapes.extraFlags;   // (Api 26/34: what registered corner and shape rules need)
            if (!ModsConfig.OdysseyActive)
            {
                return;
            }
            try
            {
                if (!flagsRead)
                {
                    flagsRead = true;
                    vanillaFlags = new SectionLayer_GravshipHull(section).relevantChangeTypes;
                }
                layer.relevantChangeTypes |= vanillaFlags;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading the game's hull layer change flags failed: " + e.Message, 93611401);
            }
        }

        // Prints the corner pieces of a block of the map into the walls layer.
        public static void Print(SectionLayer layer, Map map, CellRect rect)
        {
            if (!Active || map == null)
            {
                return;
            }
            foreach (IntVec3 c in rect)
            {
                CornerType type;
                Color color;
                if (c.GetEdifice(map) != null || !At(map, c, out type, out color))
                {
                    continue;
                }
                PrintOne(layer, map, c, type, color);
            }
        }

        private static readonly Vector2[] tri = new Vector2[3];
        private static readonly WallBoxes.PrismEdge[] triEdges = new WallBoxes.PrismEdge[3];

        private static void PrintOne(SectionLayer layer, Map map, IntVec3 c, CornerType type, Color color)
        {
            IntVec3 l1, l2;
            Legs(type, out l1, out l2);
            Building leg1 = (c + l1).GetEdifice(map);
            Building leg2 = (c + l2).GetEdifice(map);
            if (leg1 == null || leg2 == null || !SectionLayer_IsoRimWalls.Handles(leg1) || !SectionLayer_IsoRimWalls.Handles(leg2))
            {
                return;
            }
            float h1 = WallBoxes.BlockHeightAt(map, c + l1);
            float h2 = WallBoxes.BlockHeightAt(map, c + l2);
            float height = Mathf.Min(h1, h2);
            if (height <= 0.01f)
            {
                return;
            }

            // Corners in cells: B is the right angle between the two legs, V1 the end against leg 1
            // (where leg 1's outer face carries on), V2 the end against leg 2.
            Vector2 cell = new Vector2(c.x, c.z);
            Vector2 B = cell + new Vector2((l1.x > 0 || l2.x > 0) ? 1f : 0f, (l1.z > 0 || l2.z > 0) ? 1f : 0f);
            Vector2 V1 = B - new Vector2(l2.x, l2.z);
            Vector2 V2 = B - new Vector2(l1.x, l1.z);
            Vector2 diagOut = -new Vector2(l1.x + l2.x, l1.z + l2.z).normalized;

            // Where leg i's outer face carries on past Vi (that face looks out the way the other leg points away).
            Vector2 join1;
            bool line1;
            Continuation(map, c + l1, -l2, h1, diagOut, out join1, out line1);
            Vector2 join2;
            bool line2;
            Continuation(map, c + l2, -l1, h2, diagOut, out join2, out line2);

            // Counter-clockwise seen from above (x east, z north).
            bool ccw = Cross(V2 - V1, B - V1) > 0f;
            tri[0] = ccw ? V1 : V2;
            tri[1] = ccw ? V2 : V1;
            tri[2] = B;
            WallBoxes.PrismEdge hyp = new WallBoxes.PrismEdge();
            hyp.open = true;
            hyp.joinStart = ccw ? join1 : join2;
            hyp.lineStart = ccw ? line1 : line2;
            hyp.foreignStart = hyp.lineStart;
            hyp.joinEnd = ccw ? join2 : join1;
            hyp.lineEnd = ccw ? line2 : line1;
            hyp.foreignEnd = hyp.lineEnd;
            triEdges[0] = hyp;
            triEdges[1] = new WallBoxes.PrismEdge();
            triEdges[2] = new WallBoxes.PrismEdge();

            WallKind kind = WallBoxes.KindOf(leg1);
            // (shader cut-away) the piece goes down with leg 1, or with leg 2 if that is cut further (the lower leg).
            int whole = CutTags.Mark(layer);
            int own = CutTags.Enabled && WallBoxes.CanBeCut(leg1.def) ? ShaderCut.Lower | ShaderCut.Second(l2 - l1) : 0;
            WallBoxes.prismOwner = c + l1;
            WallBoxes.prismFlags = own;
            try
            {
                if (!WallBoxes.PrintPrism(layer, tri, triEdges, 0f, height, kind, color))
                {
                    return;
                }

                // The cap's rim turns round the inner corner B where the cell diagonally inside is open.
                IntVec3 inner = c + l1 + l2;
                if (WallBoxes.BlockHeightAt(map, inner) < height - 0.01f)
                {
                    WallBoxes.RimSquare(layer, B, new Vector2(-(l1.x + l2.x), -(l1.z + l2.z)), 0f, height, color);
                }
                GroundBand(layer, tri, triEdges);
            }
            finally
            {
                CutTags.Apply(layer, whole, c + l1, own);
                WallBoxes.prismFlags = 0;
            }
        }

        // The face of the hull block in cell leg looking out along dir: carries on past the corner piece's end
        // (join = its outward normal, with a corner line), runs on in line with it (another corner piece:
        // join = the piece's own normal, no line) or is not there (join zero: a square end).
        private static void Continuation(Map map, IntVec3 leg, IntVec3 dir, float legHeight, Vector2 diagOut, out Vector2 join, out bool line)
        {
            IntVec3 next = leg + dir;
            join = Vector2.zero;
            line = false;
            if (Covers(map, next, leg, 0f))
            {
                join = diagOut;
                return;
            }
            if (next.InBounds(map) && WallBoxes.BlockHeightAt(map, next) < legHeight - 0.01f)
            {
                join = new Vector2(dir.x, dir.z);
                line = true;
            }
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        // Soft shading on the ground along the open faces, as GroundShadows.AroundBlock does for blocks.
        internal static void GroundBand(SectionLayer layer, Vector2[] poly, WallBoxes.PrismEdge[] edges)
        {
            Material mat = R3DMaterials.GroundShadow;
            if (!GroundShadows.On || mat == null)
            {
                return;
            }
            LayerSubMesh sm = MeshWriter.Sub(layer, mat);
            if (sm == null || sm.verts.Count + 16 > MeshWriter.MaxVerts)
            {
                return;
            }
            const float Reach = 0.30f;
            float y = AltitudeLayer.Shadows.AltitudeFor();
            Color32 inner = new Color32(0, 0, 0, (byte)(0.32f * 255f));
            Color32 clear = new Color32(0, 0, 0, 0);
            int n = poly.Length;
            for (int i = 0; i < n; i++)
            {
                if (!edges[i].open)
                {
                    continue;
                }
                Vector2 a = poly[i];
                Vector2 b = poly[(i + 1) % n];
                Vector2 d = (b - a).normalized;
                Vector2 o = new Vector2(d.y, -d.x);
                MeshWriter.Quad(sm, V3(a, y), V3(a + o * Reach, y), V3(b + o * Reach, y), V3(b, y), inner, clear, clear, inner);
                // Fill the gap to the band of a face that carries on round a corner.
                if (edges[i].lineStart && edges[i].joinStart != Vector2.zero)
                {
                    Vector2 j = edges[i].joinStart;
                    MeshWriter.Quad(sm, V3(a, y), V3(a + j * Reach, y), V3(a + (j + o).normalized * Reach, y), V3(a + o * Reach, y), inner, clear, clear, clear);
                }
                if (edges[i].lineEnd && edges[i].joinEnd != Vector2.zero)
                {
                    Vector2 j = edges[i].joinEnd;
                    MeshWriter.Quad(sm, V3(b, y), V3(b + o * Reach, y), V3(b + (j + o).normalized * Reach, y), V3(b + j * Reach, y), inner, clear, clear, clear);
                }
            }
        }

        private static Vector3 V3(Vector2 p, float y)
        {
            return new Vector3(p.x, y, p.y);
        }

        // ---- the game's own flat corner quads

        // After the game rebuilds its hull layer: while the 3D corner pieces are drawn, its 8 corner
        // colour pieces (AngledGravshipHull*) are emptied. Their GravshipMask/IndoorMask copies (used by
        // the launch capture and the indoor shading) and the substructure edge quads are kept.
        public static void Regenerate_Postfix(SectionLayer_GravshipHull __instance)
        {
            if (!Active)
            {
                return;
            }
            try
            {
                HashSet<Material> quads = VanillaQuads();
                if (quads == null)
                {
                    return;
                }
                List<LayerSubMesh> subs = __instance.subMeshes;
                for (int i = 0; i < subs.Count; i++)
                {
                    if (subs[i].material != null && quads.Contains(subs[i].material) && subs[i].verts.Count > 0)
                    {
                        subs[i].Clear(MeshParts.All);   // not finalized: not drawn, and left out of the culling boxes
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Hiding the game's flat hull corners failed: " + e, 93611402);
            }
        }

        private static HashSet<Material> VanillaQuads()
        {
            if (quadsLooked)
            {
                return vanillaQuads;
            }
            quadsLooked = true;
            var getMaterial = AccessTools.Method(typeof(SectionLayer_GravshipHull), "GetMaterial");
            if (getMaterial == null)
            {
                ModLog.Warn("[IsoRim] SectionLayer_GravshipHull.GetMaterial not found: the game's flat hull corners stay under the 3D ones.");
                return null;
            }
            HashSet<Material> set = new HashSet<Material>();
            foreach (CornerType t in Enum.GetValues(typeof(CornerType)))
            {
                if (t == CornerType.None)
                {
                    continue;
                }
                CachedMaterial cm = getMaterial.Invoke(null, new object[] { t }) as CachedMaterial;
                Material m = cm != null ? cm.Material : null;
                if (m != null)
                {
                    set.Add(m);
                }
            }
            vanillaQuads = set;
            return set;
        }
    }

    public static partial class WallBoxes
    {
        // One side of a prism printed by PrintPrism, from corner i to corner i + 1 of its footprint.
        // open: the side shows (closed sides lie against another block and are left out). At each end,
        // join is the outward direction of the face that carries on past that corner (zero: none, a square
        // end); line: a dark corner line goes up there, shared with that face as wall end lines are; foreign:
        // that face belongs to another block, which leaves its half of the line out, so it is drawn here too.
        public struct PrismEdge
        {
            public bool open;
            public Vector2 joinStart, joinEnd;
            public bool lineStart, lineEnd;
            public bool foreignStart, foreignEnd;
            // (shader cut-away, Api 34) a closed side against a block that may be cut lower: baked with this ShaderCut.Cond
            // tag, shown only while that block is lower (as a block's side against a neighbour that may drop). 0: left out.
            public int cond;
        }

        // A wall-like prism standing on any convex footprint (corners counter-clockwise seen from above, in
        // cells, x east and y north), drawn exactly like the blocks: the textured cap over a plain lid,
        // textured sides over plain lit faces on the open sides, the dark rim on the cap and the line along
        // the ground under each open side, and corner lines. baseHeight lifts the whole prism (cells; 0 on
        // the ground). Used for gravship hull corner pieces (a triangle); meant to serve any other angled
        // wall footprint too (e.g. Vanilla Diagonal Walls' joins). False, with nothing drawn, if the wall
        // textures are missing.
        // (shader cut-away) the tag PrintPrism gives its textured sides (the caller tags the rest); 0 = none.
        public static IntVec3 prismOwner;
        public static int prismFlags;

        // (Api 34) capTint: the cap's colour when it differs from the sides' (a stuffless block's atlas tones, BlockTones);
        // drop, vBase, zShift: a wall upstairs reaching down to the wall below (Floors.WallColumn), as PrintBlock does: the
        // sides go down by drop with no line along their foot, the side texture starts at vBase and follows the ground rows.
        public static bool PrintPrism(SectionLayer layer, IList<Vector2> poly, PrismEdge[] edges, float baseHeight, float height, WallKind kind, Color tint,
            Color? capTint = null, float drop = 0f, float vBase = 0f, int zShift = 0)
        {
            int n = poly.Count;
            Material topMat = MaterialFor(kind, true, 0, false, capTint ?? tint);
            LayerSubMesh top = topMat != null ? MeshWriter.Sub(layer, topMat) : null;
            if (n < 3 || top == null || top.verts.Count + n > MeshWriter.MaxVerts)
            {
                return false;
            }
            Material solidMat = R3DMaterials.Solid;
            LayerSubMesh solid = solidMat != null ? MeshWriter.Sub(layer, solidMat) : null;
            bool room = solid != null && solid.verts.Count + 40 * n <= MeshWriter.MaxVerts;
            float squash = View3D.Squash;
            float y0 = (baseHeight - drop) / squash;
            float topY = (baseHeight + height) / squash;
            float rimY = topY + 0.002f / squash;
            Color32 lineColor = R3DMaterials.Shaded(tint, LineShade);

            // The cap (texture laid in map space like the blocks', so it runs on into theirs) and the plain lid under it.
            int start = top.verts.Count;
            for (int i = 0; i < n; i++)
            {
                top.verts.Add(new Vector3(poly[i].x, topY, poly[i].y));
                top.uvs.Add(new Vector3(poly[i].x * 0.5f, poly[i].y * 0.5f, 0f));
                top.colors.Add(White);
            }
            Fan(top, start, n);
            if (room)
            {
                float lidY = topY - 0.01f / squash;
                Color32 lid = R3DMaterials.Shaded(tint, 0.9f);
                start = solid.verts.Count;
                for (int i = 0; i < n; i++)
                {
                    solid.verts.Add(new Vector3(poly[i].x, lidY, poly[i].y));
                    solid.uvs.Add(Vector3.zero);
                    solid.colors.Add(lid);
                }
                Fan(solid, start, n);
            }

            for (int i = 0; i < n; i++)
            {
                PrismEdge e = edges[i];
                bool condSide = !e.open && e.cond != 0 && CutTags.Enabled;
                if (!e.open && !condSide)
                {
                    continue;
                }
                int condMark = condSide ? CutTags.Mark(layer) : -1;   // (Api 34) a side shown only while the block against it is lower
                PrismSide(layer, poly, edges, i, kind, tint, y0, topY, rimY, height, drop, vBase, zShift, squash, lineColor, room ? solid : null,
                    condSide ? e.cond : 0);
                if (condSide)
                {
                    CutTags.Apply(layer, condMark, prismOwner, prismFlags | e.cond);
                }
            }
            return true;
        }

        // One side of PrintPrism (cond != 0: a closed side baked for the shader cut-away, Api 34: no corner lines).
        private static void PrismSide(SectionLayer layer, IList<Vector2> poly, PrismEdge[] edges, int i, WallKind kind, Color tint, float y0, float topY,
            float rimY, float height, float drop, float vBase, int zShift, float squash, Color32 lineColor, LayerSubMesh solid, int cond)
        {
            int n = poly.Count;
            bool room = solid != null;
            PrismEdge e = edges[i];
            {
                Vector2 a = poly[i];
                Vector2 b = poly[(i + 1) % n];
                float len = (b - a).magnitude;
                if (len < 1e-4f)
                {
                    return;
                }
                Vector2 dir = (b - a) / len;          // along the side, left to right seen from outside
                Vector2 o = new Vector2(dir.y, -dir.x); // outward
                Vector3 p0 = new Vector3(a.x, y0, a.y);
                Vector3 p1 = new Vector3(b.x, y0, b.y);
                Vector3 out3 = new Vector3(o.x, 0f, o.y);

                // Joins with this prism's own open neighbours are worked out here.
                PrismEdge prev = edges[(i + n - 1) % n];
                PrismEdge next = edges[(i + 1) % n];
                Vector2 joinA = prev.open ? OutwardOf(poly[(i + n - 1) % n], a) : e.joinStart;
                Vector2 joinB = next.open ? OutwardOf(b, poly[(i + 2) % n]) : e.joinEnd;
                bool lineA = prev.open ? Cross2(OutwardOf(poly[(i + n - 1) % n], a), o) > 1e-3f : e.lineStart;
                bool lineB = next.open ? Cross2(o, OutwardOf(b, poly[(i + 2) % n])) > 1e-3f : e.lineEnd;

                Material sideMat = MaterialFor(kind, false, 0, false, tint);
                LayerSubMesh side = sideMat != null ? MeshWriter.Sub(layer, sideMat) : null;
                bool textured = side != null && side.verts.Count + 4 <= MeshWriter.MaxVerts;
                if (textured)
                {
                    float along = a.x * dir.x + (a.y - zShift) * dir.y;   // (upstairs: as the ground row, so the texture runs on)
                    float u0 = along * 0.5f;
                    float u1 = (along + len) * 0.5f;
                    float v0 = vBase;
                    float v1 = vBase + (height + drop) / SideCellsHigh;
                    int sideMark = (prismFlags | cond) != 0 ? CutTags.Mark(layer) : -1;
                    MeshWriter.TexturedQuad(side, p0, Raised(p0, topY), Raised(p1, topY), p1,
                        new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1), new Vector2(u1, v0), White);
                    CutTags.Apply(layer, sideMark, prismOwner, prismFlags | cond | ShaderCut.TexSide);
                }
                if (!room)
                {
                    return;
                }
                Vector3 b0 = textured ? p0 - out3 * Gap : p0;
                Vector3 b1 = textured ? p1 - out3 * Gap : p1;
                MeshWriter.Quad(LitSide(layer, 0, solid), b0, Raised(b0, topY), Raised(b1, topY), b1, R3DMaterials.Shaded(tint, 0.9f));

                // The rim on the cap, mitred against the face that carries on at each end.
                Vector2 ia = InnerCorner(a, o, joinA);
                Vector2 ib = InnerCorner(b, o, joinB);
                MeshWriter.Quad(solid, new Vector3(a.x, rimY, a.y), new Vector3(ia.x, rimY, ia.y), new Vector3(ib.x, rimY, ib.y), new Vector3(b.x, rimY, b.y), lineColor);

                // The line along the ground (not for a wall reaching down to the one below).
                Vector3 q0 = p0 + out3 * Gap;
                Vector3 q1 = p1 + out3 * Gap;
                float lineTop = y0 + OutlineWidth * 0.7f / squash;
                if (drop <= 0f)
                {
                    MeshWriter.Quad(solid, q0, Raised(q0, lineTop), Raised(q1, lineTop), q1, lineColor);
                }
                if (cond != 0)
                {
                    return;
                }

                // Corner lines.
                if (lineA && joinA != Vector2.zero)
                {
                    CornerLine(solid, a, o, joinA, dir, true, y0, topY, lineColor);
                    if (!prev.open && e.foreignStart)
                    {
                        // The other face arrives at a; its half of the line, into that face.
                        Vector2 dIn = new Vector2(-joinA.y, joinA.x);
                        CornerLine(solid, a, joinA, o, -dIn, false, y0, topY, lineColor);
                    }
                }
                if (lineB && joinB != Vector2.zero)
                {
                    CornerLine(solid, b, o, joinB, -dir, false, y0, topY, lineColor);
                    if (!next.open && e.foreignEnd)
                    {
                        Vector2 dOut = new Vector2(-joinB.y, joinB.x);
                        CornerLine(solid, b, joinB, o, dOut, true, y0, topY, lineColor);
                    }
                }
            }
        }

        // A small square of rim on a cap at an inside corner p (inward = toward the cap from p, diagonal).
        public static void RimSquare(SectionLayer layer, Vector2 p, Vector2 inward, float baseHeight, float height, Color tint)
        {
            Material solidMat = R3DMaterials.Solid;
            LayerSubMesh solid = solidMat != null ? MeshWriter.Sub(layer, solidMat) : null;
            if (solid == null || solid.verts.Count + 4 > MeshWriter.MaxVerts)
            {
                return;
            }
            float rimY = (baseHeight + height) / View3D.Squash + 0.002f / View3D.Squash;
            float sx = Mathf.Sign(inward.x) * OutlineWidth;
            float sz = Mathf.Sign(inward.y) * OutlineWidth;
            Color32 lineColor = R3DMaterials.Shaded(tint, LineShade);
            MeshWriter.Quad(solid, new Vector3(p.x, rimY, p.y), new Vector3(p.x, rimY, p.y + sz), new Vector3(p.x + sx, rimY, p.y + sz),
                new Vector3(p.x + sx, rimY, p.y), lineColor);
        }

        // One half of a corner line on the face with outward normal n at corner p: from just past the corner
        // to the corner, its inner edge moved into the face (along) by the shader's share of the width, given
        // the face round the corner (other). startEnd: the corner is the face's left end seen from outside.
        private static void CornerLine(LayerSubMesh sm, Vector2 p, Vector2 n, Vector2 other, Vector2 along, bool startEnd, float y0, float topY, Color32 color)
        {
            float code = 10f + 8f * Compass(n) + Compass(other);
            Vector2 face = p + n * Gap;
            Vector2 outer = face - along * Gap;
            Vector3 inner = new Vector3(along.x * OutlineWidth, along.y * OutlineWidth, code);
            Vector3 c3 = new Vector3(face.x, y0, face.y);
            Vector3 o3 = new Vector3(outer.x, y0, outer.y);
            Vector3 a = startEnd ? o3 : c3;
            Vector3 d = startEnd ? c3 : o3;
            Vector3 ua = startEnd ? Vector3.zero : inner;
            Vector3 ud = startEnd ? inner : Vector3.zero;
            int start = sm.verts.Count;
            sm.verts.Add(a);
            sm.verts.Add(Raised(a, topY));
            sm.verts.Add(Raised(d, topY));
            sm.verts.Add(d);
            sm.uvs.Add(ua);
            sm.uvs.Add(ua);
            sm.uvs.Add(ud);
            sm.uvs.Add(ud);
            for (int i = 0; i < 4; i++)
            {
                sm.colors.Add(color);
            }
            MeshWriter.AddQuadTris(sm, start);
        }

        // 0..7 = east, north-east, north, ... counter-clockwise (IsoRim.vert.glsl, codes 10..73).
        private static int Compass(Vector2 v)
        {
            float a = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
            return Mathf.RoundToInt(Mathf.Repeat(a, 360f) / 45f) % 8;
        }

        private static Vector2 OutwardOf(Vector2 a, Vector2 b)
        {
            Vector2 d = (b - a).normalized;
            return new Vector2(d.y, -d.x);
        }

        private static float Cross2(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        // Where the inner edge of the rim along a side (outward o) meets the rim of the face carrying on at
        // corner p (outward join): a mitre; a square end when there is none or it runs on in line.
        private static Vector2 InnerCorner(Vector2 p, Vector2 o, Vector2 join)
        {
            float w = OutlineWidth;
            float det = o.x * join.y - o.y * join.x;
            if (join == Vector2.zero || Mathf.Abs(det) < 1e-4f)
            {
                return p - o * w;
            }
            // (x - p).o = -w and (x - p).join = -w
            float dx = (-w * join.y + w * o.y) / det;
            float dz = (-o.x * w + join.x * w) / det;
            return p + new Vector2(dx, dz);
        }

        private static void Fan(LayerSubMesh sm, int start, int n)
        {
            for (int i = 1; i + 1 < n; i++)
            {
                sm.tris.Add(start);
                sm.tris.Add(start + i);
                sm.tris.Add(start + i + 1);
            }
        }
    }

    // A flat metal deck over gravships when roofs are shown (option R2), from GPT's ship kit roof pieces
    // (Models/roof_*.obj, delivery 20261014-0613_ship_kit): a plate per cell, a triangle plate on each 3D
    // hull corner piece, rims along the outline, corner rims at right-angled outside corners and small
    // filler blocks at the others. Built from SectionLayer_IsoRimRoofs, so it follows "show roofs", the roof
    // lifted under the pointer or over colonists, and the sky colour at night, like the house roofs.
    //
    // A ship roof cell (lid kind 3 in SectionLayer_IsoRimRoofs) is a built roof whose cell has a gravship deck as its
    // foundation (IsDeck: Substructure and its kin, TerrainDef.IsSubstructure, the test the game's hull layer uses, or any
    // foundation that carries the game's Substructure affordance, as modded ship decks do: Vanilla Gravship Expanded's
    // enemy substructure and subarmor have the affordance but not the tag), or a wall, door or vac barrier standing on
    // one beside one (the edge of the roof). Every other
    // built roof keeps the pitched house roof (an upper-floor opening in a ship's deck, IFloorLayout.ShipCell, counts
    // as Substructure here). A corner piece cell gets a triangle when either of its two
    // hull neighbours carries the ship roof.
    [StaticConstructorOnStartup]
    public static class ShipRoofs
    {
        public const int Kind = 3;
        private const float Lift = 0.005f;       // above the wall tops, in cells (GPT: cap + .005)
        private const float RimHalf = 0.07f;     // half the width of a rim piece
        private const float RimLow = 0.15f;      // where the rim's top plate starts
        private const float RimTop = 0.19f;

        private static bool loaded;
        private static Model square, triangle, rim, rim90, rimDiagonal;
        private static Color ribColor = new Color(0.43f, 0.47f, 0.48f), plateColor = new Color(0.65f, 0.68f, 0.67f);
        private static Material material;
        private static readonly List<Vector2> edgeA = new List<Vector2>();
        private static readonly List<Vector2> edgeB = new List<Vector2>();
        private static readonly List<Vector2> starts = new List<Vector2>();
        private static readonly List<Vector2> dirs = new List<Vector2>();

        public static bool IsShip(Map map, IntVec3 c)
        {
            if (!ModsConfig.OdysseyActive || map == null || !c.InBounds(map))
            {
                return false;
            }
            TerrainDef f = map.terrainGrid.FoundationAt(c);
            if (IsDeck(f))
            {
                return true;
            }
            // (an upper-floor opening in a ship's deck, the floor layout's say: IFloorLayout.ShipCell)
            if (f != null || Api.Floors == null)
            {
                return false;
            }
            int ground = Api.Floors.GroundRows(map);
            return ground > 0 && c.z >= ground && Api.Floors.ShipCell(map, c);
        }

        private static TerrainAffordanceDef deckAffordance;
        private static bool deckAffordanceLooked;

        // A gravship deck foundation: Substructure's tag (TerrainDef.IsSubstructure), or the game's Substructure affordance
        // (what only a gravship deck carries in the game: where things built for a ship may stand).
        public static bool IsDeck(TerrainDef f)
        {
            if (f == null)
            {
                return false;
            }
            if (f.IsSubstructure)
            {
                return true;
            }
            if (!deckAffordanceLooked)
            {
                deckAffordanceLooked = true;
                deckAffordance = DefDatabase<TerrainAffordanceDef>.GetNamedSilentFail("Substructure");
            }
            return deckAffordance != null && f.affordances != null && f.affordances.Contains(deckAffordance);
        }

        // The deck's material: plain colours, shaded by the sun like the house roof's slates, sky light only.
        private static Material Mat
        {
            get
            {
                if (material == null && R3DMaterials.Solid != null)
                {
                    material = R3DMaterials.Make(R3DMaterials.SolidDef, "IsoRim_ShipRoof", 3600, R3DMaterials.LightRoof);
                    IsoGlow.Use(material, IsoGlow.SkyOnly);
                }
                return material;
            }
        }

        private static void Load()
        {
            if (loaded)
            {
                return;
            }
            loaded = true;
            string dir = IsoRimMod.RootDir != null ? Path.Combine(IsoRimMod.RootDir, "Models") : null;
            if (dir == null)
            {
                return;
            }
            square = ModelLibrary.LoadFile(Path.Combine(dir, "roof_square.obj"));
            triangle = ModelLibrary.LoadFile(Path.Combine(dir, "roof_triangle.obj"));
            rim = ModelLibrary.LoadFile(Path.Combine(dir, "roof_rim.obj"));
            rim90 = ModelLibrary.LoadFile(Path.Combine(dir, "roof_rim90.obj"));
            rimDiagonal = ModelLibrary.LoadFile(Path.Combine(dir, "roof_rim_diagonal.obj"));
            if (rim != null && rim.materials != null)
            {
                foreach (ModelMaterial m in rim.materials)
                {
                    if (m.name == "ship_rib")
                    {
                        ribColor = m.color;
                    }
                    else if (m.name == "ship_plate")
                    {
                        plateColor = m.color;
                    }
                }
            }
            if (square == null || triangle == null || rim == null || rim90 == null || rimDiagonal == null)
            {
                ModLog.Warn("[IsoRim] Ship roof pieces missing in Models/ (roof_square, roof_triangle, roof_rim, roof_rim90, roof_rim_diagonal).");
            }
        }

        // What covers a cell: 0 nothing, 1 a full plate, 2 a triangle on a hull corner piece (type, legs).
        private static int Piece(Map map, Func<int, int, int> kindAt, int x, int z, out CornerType type)
        {
            type = CornerType.None;
            int k = kindAt(x, z);
            if (k == Kind)
            {
                return 1;
            }
            if (k != 0 || !HullCorners.Active)
            {
                return 0;
            }
            IntVec3 c = new IntVec3(x, 0, z);
            Color color;
            IntVec3 a, b;
            if (!c.InBounds(map) || c.GetEdifice(map) != null || !HullCorners.At(map, c, out type, out color) || !HullCorners.Legs(type, out a, out b))
            {
                return 0;
            }
            return kindAt(x + a.x, z + a.z) == Kind || kindAt(x + b.x, z + b.z) == Kind ? 2 : 0;
        }

        private static readonly IntVec3[] Sides = { IntVec3.South, IntVec3.East, IntVec3.North, IntVec3.West };
        private static readonly Vector2[] squareCorners = new Vector2[4];
        private static readonly Vector2[] triCorners = new Vector2[3];

        // The outline sides of the piece in cell (x, z), counter-clockwise (roof on the left), from-to in cells.
        private static void Edges(Map map, Func<int, int, int> kindAt, int x, int z, List<Vector2> from, List<Vector2> to)
        {
            if (ShapeRoofs.CoverOf(map, new IntVec3(x, 0, z)) != null)
            {
                return;   // (Api 34) a cell with wall shapes runs its own outline (ShapedCell)
            }
            CornerType type;
            int p = Piece(map, kindAt, x, z, out type);
            if (p == 0)
            {
                return;
            }
            if (p == 1)
            {
                // south, east, north, west
                IntVec3[] sides = Sides;
                Vector2[] a = squareCorners;
                a[0] = new Vector2(x, z);
                a[1] = new Vector2(x + 1, z);
                a[2] = new Vector2(x + 1, z + 1);
                a[3] = new Vector2(x, z + 1);
                for (int f = 0; f < 4; f++)
                {
                    int nx = x + sides[f].x, nz = z + sides[f].z;
                    CornerType nt;
                    int np = Piece(map, kindAt, nx, nz, out nt);
                    bool covered = np == 1;
                    if (np == 2)
                    {
                        IntVec3 la, lb;
                        HullCorners.Legs(nt, out la, out lb);
                        covered = (nx + la.x == x && nz + la.z == z) || (nx + lb.x == x && nz + lb.z == z);
                    }
                    if (!covered)
                    {
                        from.Add(a[f]);
                        to.Add(a[(f + 1) % 4]);
                    }
                }
                return;
            }
            IntVec3 l1, l2;
            HullCorners.Legs(type, out l1, out l2);
            Vector2 B = new Vector2(x + ((l1.x > 0 || l2.x > 0) ? 1 : 0), z + ((l1.z > 0 || l2.z > 0) ? 1 : 0));
            Vector2 V1 = B - new Vector2(l2.x, l2.z);
            Vector2 V2 = B - new Vector2(l1.x, l1.z);
            Vector2[] t = triCorners;
            bool ccw = (V2 - V1).x * (B - V1).y - (V2 - V1).y * (B - V1).x > 0f;
            t[0] = ccw ? V1 : V2;
            t[1] = ccw ? V2 : V1;
            t[2] = B;
            from.Add(t[0]);
            to.Add(t[1]);   // the slanted side always shows
            // A side along a leg shows when that leg carries no ship roof.
            for (int k = 1; k < 3; k++)
            {
                Vector2 s = t[k], e = t[(k + 1) % 3];
                Vector2 mid = (s + e) * 0.5f;
                IntVec3 leg = (mid.x == x) ? IntVec3.West : (mid.x == x + 1) ? IntVec3.East : (mid.y == z) ? IntVec3.South : IntVec3.North;
                if (kindAt(x + leg.x, z + leg.z) != Kind)
                {
                    from.Add(s);
                    to.Add(e);
                }
            }
        }

        // Builds the deck over the ship roof cells of one block of the map.
        public static void Build(SectionLayer layer, Map map, CellRect rect, Func<int, int, int> kindAt)
        {
            if (!ModsConfig.OdysseyActive)
            {
                return;
            }
            Load();
            Material mat = Mat;
            LayerSubMesh sm = mat != null ? MeshWriter.Sub(layer, mat) : null;
            if (sm == null || square == null || triangle == null || rim == null || rim90 == null || rimDiagonal == null)
            {
                return;
            }
            float baseY = View3D.WallHeight + Lift;   // cells; a per-map base height would add here (design section 11)
            foreach (IntVec3 c in rect)
            {
                if (sm.verts.Count + 2000 > MeshWriter.MaxVerts)
                {
                    break;
                }
                CornerType type;
                List<ShapeRoofs.Piece> cover = ShapeRoofs.CoverOf(map, c);   // (Api 34) null: no wall shapes here
                if (cover != null)
                {
                    if (ShapeRoofs.KindOf(cover, kindAt(c.x, c.z)) == Kind)
                    {
                        ShapedCell(layer, sm, map, c, cover, baseY, kindAt(c.x, c.z) == 0 && cover.Count > 0 ? cover[0].tag : c);
                    }
                    continue;
                }
                int p = Piece(map, kindAt, c.x, c.z, out type);
                if (p == 0)
                {
                    continue;
                }
                Vector2 centre = new Vector2(c.x + 0.5f, c.z + 0.5f);
                int cellMark = CutTags.Mark(layer);   // (shader cut-away) hidden while this cell's roof is open
                if (p == 1)
                {
                    Put(sm, square, centre, 1f, 0f, 0f, 1f, baseY);
                }
                else
                {
                    // roof_triangle covers the north-east half (corners NW, NE, SE); turned for the others.
                    switch (type)
                    {
                        case CornerType.Corner_NE:
                        case CornerType.Diagonal_NE:
                            Put(sm, triangle, centre, 1f, 0f, 0f, 1f, baseY);
                            break;
                        case CornerType.Corner_NW:
                        case CornerType.Diagonal_NW:
                            Put(sm, triangle, centre, 0f, -1f, 1f, 0f, baseY);
                            break;
                        case CornerType.Corner_SW:
                        case CornerType.Diagonal_SW:
                            Put(sm, triangle, centre, -1f, 0f, 0f, -1f, baseY);
                            break;
                        default:
                            Put(sm, triangle, centre, 0f, 1f, -1f, 0f, baseY);
                            break;
                    }
                }

                // Rims along the outline sides, and the corner where each side ends.
                edgeA.Clear();
                edgeB.Clear();
                Edges(map, kindAt, c.x, c.z, edgeA, edgeB);
                for (int i = 0; i < edgeA.Count; i++)
                {
                    Vector2 a = edgeA[i], b = edgeB[i];
                    Vector2 d = (b - a).normalized;
                    Vector2 mid = (a + b) * 0.5f;
                    if (Mathf.Abs(d.x) > 0.9f || Mathf.Abs(d.y) > 0.9f)
                    {
                        Put(sm, rim, mid, d.x, -d.y, d.y, d.x, baseY);   // rim runs along x: x -> d
                    }
                    else
                    {
                        // roof_rim_diagonal runs south-west to north-east (direction (1,1)): turn it onto d.
                        float s2 = 0.70710678f;
                        float cs = (d.x + d.y) * s2, sn = (d.y - d.x) * s2;   // rotation from (1,1)/sqrt2 to d
                        Put(sm, rimDiagonal, mid, cs, -sn, sn, cs, baseY);
                    }
                    CornerAt(sm, map, kindAt, b, d, baseY);
                }
                // A corner piece's deck goes with the roof of its first leg (the corner cell itself is in no room).
                IntVec3 owner = c, legA, legB;
                if (p != 1 && HullCorners.Legs(type, out legA, out legB))
                {
                    owner = c + legA;
                }
                CutTags.Apply(layer, cellMark, owner, ShaderCut.Roof);
            }
        }

        private static readonly List<ShapeRoofs.Edge> shapeEdges = new List<ShapeRoofs.Edge>();

        // (Api 34) A ship roof cell with wall shapes: the deck plate clipped to the roof's pieces there (ShapeRoofs), rims
        // along the outline of those pieces (stretched to each side's length) and a filler block at each outside corner.
        private static void ShapedCell(SectionLayer layer, LayerSubMesh sm, Map map, IntVec3 c, List<ShapeRoofs.Piece> cover, float baseY, IntVec3 tag)
        {
            int cellMark = CutTags.Mark(layer);
            int[] mark = ShapeRoofs.MarkTris(layer);
            Put(sm, square, new Vector2(c.x + 0.5f, c.z + 0.5f), 1f, 0f, 0f, 1f, baseY);
            ShapeRoofs.ClipSince(layer, mark, cover, Kind);
            ShapeRoofs.Outline(map, c, Kind, shapeEdges);
            foreach (ShapeRoofs.Edge e in shapeEdges)
            {
                Vector2 d = e.b - e.a;
                float len = d.magnitude;
                if (len < 0.01f)
                {
                    continue;
                }
                d /= len;
                Vector2 mid = (e.a + e.b) * 0.5f;
                Put(sm, rim, mid, d.x * len, -d.y, d.y * len, d.x, baseY);   // rim runs along x: x -> d, stretched to the side
                // an outside corner at its end: the gap between the two rims' square ends filled
                Vector2 n1 = e.o, n2 = e.joinB;
                if (n1.x * n2.y - n1.y * n2.x > 1e-3f)
                {
                    Vector2 m = e.b + (n1 + n2) * (RimHalf / (1f + Vector2.Dot(n1, n2)));
                    Vector2[] poly = { e.b, e.b + n1 * RimHalf, m, e.b + n2 * RimHalf };
                    Block(sm, poly, baseY, baseY + RimLow, ribColor);
                    Block(sm, poly, baseY + RimLow, baseY + RimTop, plateColor);
                }
            }
            CutTags.Apply(layer, cellMark, tag, ShaderCut.Roof);
        }

        // The corner where a side ending at p (direction d1) meets the next side of the outline.
        private static void CornerAt(LayerSubMesh sm, Map map, Func<int, int, int> kindAt, Vector2 p, Vector2 d1, float baseY)
        {
            starts.Clear();
            dirs.Clear();
            int px = Mathf.RoundToInt(p.x), pz = Mathf.RoundToInt(p.y);
            for (int dx = -1; dx <= 0; dx++)
            {
                for (int dz = -1; dz <= 0; dz++)
                {
                    edgeTmpA.Clear();
                    edgeTmpB.Clear();
                    Edges(map, kindAt, px + dx, pz + dz, edgeTmpA, edgeTmpB);
                    for (int i = 0; i < edgeTmpA.Count; i++)
                    {
                        if ((edgeTmpA[i] - p).sqrMagnitude < 1e-6f)
                        {
                            dirs.Add((edgeTmpB[i] - edgeTmpA[i]).normalized);
                        }
                    }
                }
            }
            if (dirs.Count == 0)
            {
                return;
            }
            Vector2 d2 = dirs[0];
            float turn = d1.x * d2.y - d1.y * d2.x;
            if (turn <= 1e-3f)
            {
                return;   // straight on, or an inside corner: the rims already meet
            }
            bool square90 = Mathf.Abs(d1.x * d2.x + d1.y * d2.y) < 1e-3f && (Mathf.Abs(d1.x) > 0.9f || Mathf.Abs(d1.y) > 0.9f);
            if (square90)
            {
                // roof_rim90's arms run west and north from the corner: west -> back along d1, north -> along d2.
                Put(sm, rim90, p, d1.x, d2.x, d1.y, d2.y, baseY);
                return;
            }
            // Any other outside corner: a small block filling the gap between the two rims' square ends.
            Vector2 n1 = new Vector2(d1.y, -d1.x);
            Vector2 n2 = new Vector2(d2.y, -d2.x);
            Vector2 m = p + (n1 + n2) * (RimHalf / (1f + Vector2.Dot(n1, n2)));
            Vector2[] poly = { p, p + n1 * RimHalf, m, p + n2 * RimHalf };
            Block(sm, poly, baseY, baseY + RimLow, ribColor);
            Block(sm, poly, baseY + RimLow, baseY + RimTop, plateColor);
        }

        private static readonly List<Vector2> edgeTmpA = new List<Vector2>();
        private static readonly List<Vector2> edgeTmpB = new List<Vector2>();

        // A roof piece placed with its footprint centre at c, turned by the 2x2 matrix (m00 m01; m10 m11) on (x, z),
        // its bottom at baseY (cells).
        private static void Put(LayerSubMesh sm, Model m, Vector2 c, float m00, float m01, float m10, float m11, float baseY)
        {
            float squash = View3D.Squash;
            Vector3[] pos = m.positions;
            int start = sm.verts.Count;
            for (int i = 0; i < pos.Length; i++)
            {
                Vector3 v = pos[i];
                sm.verts.Add(new Vector3(c.x + m00 * v.x + m01 * v.z, (baseY + v.y) / squash, c.y + m10 * v.x + m11 * v.z));
                sm.colors.Add(R3DMaterials.Shaded(m.colors[i], 1f));
                sm.uvs.Add(Vector3.zero);
            }
            for (int i = 0; i < pos.Length; i++)
            {
                sm.tris.Add(start + i);
            }
        }

        // A plain solid block on a convex footprint (corners counter-clockwise), from y0 to y1 (cells).
        private static void Block(LayerSubMesh sm, Vector2[] poly, float y0, float y1, Color color)
        {
            float squash = View3D.Squash;
            Color32 c = R3DMaterials.Shaded(color, 1f);
            int n = poly.Length;
            int start = sm.verts.Count;
            for (int i = 0; i < n; i++)
            {
                sm.verts.Add(new Vector3(poly[i].x, y1 / squash, poly[i].y));
                sm.colors.Add(c);
                sm.uvs.Add(Vector3.zero);
            }
            for (int i = 1; i + 1 < n; i++)
            {
                sm.tris.Add(start);
                sm.tris.Add(start + i);
                sm.tris.Add(start + i + 1);
            }
            for (int i = 0; i < n; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % n];
                MeshWriter.Quad(sm, new Vector3(a.x, y0 / squash, a.y), new Vector3(a.x, y1 / squash, a.y), new Vector3(b.x, y1 / squash, b.y),
                    new Vector3(b.x, y0 / squash, b.y), c);
            }
        }
    }
}
