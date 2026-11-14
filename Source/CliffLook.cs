using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // PROTOTYPE (branch cliff-proto): a smoother look for natural rock.
    //   1. Outlines are chamfered: a corner of the rock with only one rock cell round it (a convex corner) moves into
    //      that cell, so a diagonal run of cells becomes one slanted face and a lone corner is cut off, as vanilla's
    //      rounded 2D rock edges. Concave corners stay put, so nothing reaches into an open cell.
    //   2. Rock under rock or under a floor (stacked floors) goes straight up to the floor above with no lean, no rim
    //      and no line along the bottom of the one above: a tall cliff is one face, its texture running on. Only the top
    //      gets the dark rim. The floor's terrain on a ledge over rock is cut to the same chamfered outline.
    //   3. (Mode 2) A small, position-hashed bend part of the way up each face, so tall faces are not dead flat.
    // Every vertex still belongs to its own cell (cut tags as before) and lies inside it. Built and smoothed walls
    // stay square (PrintBlock). Mode 0 is the old look, for before/after shots: -isorimcliff=N or the probe key cliff=N.
    public static partial class WallBoxes
    {
        public static int CliffMode = ReadCliffMode();

        private static int ReadCliffMode()
        {
            string s;
            int m;
            if (GenCommandLine.TryGetCommandLineArg("isorimcliff", out s) && int.TryParse(s, out m))
            {
                return Mathf.Clamp(m, 0, 2);
            }
            return 1;
        }

        private static readonly Vector3[] smGround = new Vector3[4];
        private static readonly Vector3[] smMid = new Vector3[4];
        private static readonly Vector3[] smTop = new Vector3[4];
        private static readonly bool[] smCovered = new bool[4];
        private static readonly bool[] smMoved = new bool[4];

        // A cell the rock's outline treats as rock: a block at least as tall, or a door (ShapesRock).
        private static bool Solid(Map map, int x, int z, float height)
        {
            return ShapesRock(map, new IntVec3(x, 0, z), height);
        }

        private static bool NaturalRock(Map map, IntVec3 c)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return false;
            }
            Building b = c.GetEdifice(map);
            float h;
            return b != null && b.def.building != null && b.def.building.isNaturalRock && Floors.FullBlock(map, c, out h, true);
        }

        // How many of the four cells round map corner (cx, cz) are solid, and which way is into them.
        private static int Occupancy(Map map, int cx, int cz, float height, out int dx, out int dz)
        {
            int sw = Solid(map, cx - 1, cz - 1, height) ? 1 : 0;
            int se = Solid(map, cx, cz - 1, height) ? 1 : 0;
            int nw = Solid(map, cx - 1, cz, height) ? 1 : 0;
            int ne = Solid(map, cx, cz, height) ? 1 : 0;
            dx = Math.Sign(se + ne - sw - nw);
            dz = Math.Sign(nw + ne - sw - se);
            return sw + se + nw + ne;
        }

        private static bool Convex(Map map, int cx, int cz, float height)
        {
            return Occupancy(map, cx, cz, height) == 1;
        }

        private static int Occupancy(Map map, int cx, int cz, float height)
        {
            int dx, dz;
            return Occupancy(map, cx, cz, height, out dx, out dz);
        }

        // Where the rock's outline passes through map corner (cx, cz) at the ground of its storey (x, z in cells; y 0).
        // A convex corner moves into its one rock cell: half a cell along each side, or 0.2 along a side whose other end
        // is convex too (the tip of a one-cell spur, a lone cell), so no side shrinks to nothing.
        public static Vector3 ChamferedCorner(Map map, int cx, int cz, float height, out bool moved, out int dx, out int dz)
        {
            int kind;
            Vector3 p = CornerAt(map, cx, cz, height, out kind, out dx, out dz);
            moved = kind != 0;
            return p;
        }

        // The same, and how the corner moved: 0 not, 1 a convex corner into its rock cell, 2 a hole's corner into its open
        // cell (OpeningCorner).
        private static Vector3 CornerAt(Map map, int cx, int cz, float height, out int kind, out int dx, out int dz)
        {
            int n = Occupancy(map, cx, cz, height, out dx, out dz);
            kind = 0;
            if (n == 1)
            {
                float mx = Convex(map, cx + dx, cz, height) ? 0.2f : 0.5f;
                float mz = Convex(map, cx, cz + dz, height) ? 0.2f : 0.5f;
                kind = 1;
                return new Vector3(cx + dx * mx, 0f, cz + dz * mz);
            }
            if (n == 3 && HoleChamfer && OpeningCorner(map, cx, cz, -dx, -dz))
            {
                float mx = HolePull(map, cx - dx, cz, height);
                float mz = HolePull(map, cx, cz - dz, height);
                if (mx >= 0f && mz >= 0f)
                {
                    kind = 2;
                    return new Vector3(cx - dx * mx, 0f, cz - dz * mz);
                }
            }
            return new Vector3(cx, 0f, cz);
        }

        // ---- holes in natural rock floors (stacked floors): their corners are chamfered as the rock's outline is
        //
        // A hole is open air on a storey (no floor, no block) that the open air round the storey's edge does not reach:
        // a pit or shaft in a mountain top. Its corners are cut as a block of rock would be, mirrored: where three natural
        // rock cells with a floor above them meet one open cell under the hole (a concave corner of the rock), the corner
        // moves into that open cell, half a cell along each side, 0.2 along a side whose other end moves so too (a one-cell
        // side), and not at all where the other end of either side is a convex rock corner (a diagonal runs straight on
        // through it, or a rock tip is cut there). The convex corners of a hole are the rock's own convex corners (chamfered already). The rock below,
        // the floor's terrain and slab top over it (FloorCorner), the rim and the contact shadow all take the moved corner
        // from here, so they keep meeting. Only these corners leave their cells: the rock's faces and caps and the floors
        // over the three rock cells reach a little into the open cell (which draws nothing of its own there: no floor
        // above it, nothing standing in it), keeping their own cells' cut tags. The ground of a map without floors, the
        // outside of a mountain and the open air round a storey's edge have no such corners: their outlines are as before.

        public static bool HoleChamfer = true;   // harness A/B (probe key holechamfer=0|1)

        // (harness, probe key cornerdump) map corner (cx, cz) of a rock storey: how it moved, and the corner the floors
        // above put there.
        public static string DebugCorner(Map map, int cx, int cz)
        {
            IntVec3 c = new IntVec3(cx, 0, cz);
            float height = BlockHeightAt(map, c.InBounds(map) && NaturalRock(map, c) ? c : new IntVec3(cx - 1, 0, cz - 1));
            int kind, dx, dz;
            bool fullWas = FullHeights;
            FullHeights = ShaderCut.On;
            try
            {
                Vector3 p = CornerAt(map, cx, cz, height, out kind, out dx, out dz);
                string s = "corner " + cx + "," + cz + " occupancy " + Occupancy(map, cx, cz, height) + " kind " + kind + " -> " + p.x.ToString("0.00") + "," + p.z.ToString("0.00");
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 b = new IntVec3(cx - 1 + (i & 1), 0, cz - 1 + (i >> 1));
                    IntVec3 up = Floors.Stacked(map, b, 1);
                    Building e = b.InBounds(map) ? b.GetEdifice(map) : null;
                    s += " | " + b.x + "," + b.z + " " + (e != null ? e.def.defName : "-") + (up.IsValid && up.InBounds(map) ? (Floors.HasFloor(map, up) ? " floor" : " air") : " none");
                    Vector3[] q;
                    if (up.IsValid && up.InBounds(map) && SlabOverRock(map, up, out q))
                    {
                        int k = (i & 1) == 0 ? ((i >> 1) == 0 ? 2 : 3) : ((i >> 1) == 0 ? 1 : 0);
                        s += " q" + k + " " + (b.x + q[k].x).ToString("0.00") + "," + (b.z + q[k].z).ToString("0.00");
                    }
                }
                return s;
            }
            finally
            {
                FullHeights = fullWas;
            }
        }

        // Map corner (cx, cz), whose one open cell is the one toward (ox, oz) (each -1 or 1), is a hole's corner: nothing
        // stands in that cell, the cell above it is a hole, and the three other cells are natural rock with a floor above.
        private static bool OpeningCorner(Map map, int cx, int cz, int ox, int oz)
        {
            int x = ox < 0 ? cx - 1 : cx;
            int z = oz < 0 ? cz - 1 : cz;
            int ax = ox < 0 ? cx : cx - 1;
            int az = oz < 0 ? cz : cz - 1;
            IntVec3 open = new IntVec3(x, 0, z);
            if (!open.InBounds(map) || open.GetEdifice(map) != null)
            {
                return false;
            }
            if (!RockTop(map, new IntVec3(ax, 0, z)) || !RockTop(map, new IntVec3(x, 0, az)) || !RockTop(map, new IntVec3(ax, 0, az)))
            {
                return false;
            }
            IntVec3 up = Floors.Stacked(map, open, 1);
            float h;
            return up.IsValid && up.InBounds(map) && !Floors.HasFloor(map, up) && !Floors.FullBlock(map, up, out h, true) && InHole(map, up);
        }

        // Natural rock drawn by the walls layer with a floor right above it (a mountain top).
        private static bool RockTop(Map map, IntVec3 c)
        {
            if (!NaturalRock(map, c) || !SectionLayer_IsoRimWalls.Handles(c.GetEdifice(map)))
            {
                return false;
            }
            IntVec3 up = Floors.Stacked(map, c, 1);
            return up.IsValid && up.InBounds(map) && Floors.HasFloor(map, up);
        }

        // How far a hole's corner moves along a side whose other end is map corner (qx, qz); -1: it stays (a convex rock
        // corner there: a diagonal runs straight through, or the cut tip of a rock; moved, the rock cell between them would
        // be bent in at that tip).
        private static float HolePull(Map map, int qx, int qz, float height)
        {
            int ex, ez;
            int n = Occupancy(map, qx, qz, height, out ex, out ez);
            if (n == 1)
            {
                return -1f;
            }
            return n == 3 && OpeningCorner(map, qx, qz, -ex, -ez) ? 0.2f : 0.5f;
        }

        // Which open-air cells of the upper storeys the open air round the storey's edge reaches (4-way, through cells
        // without a floor or a block), per map; the rest are holes. Worked out again on first use after a floor or a
        // block on an upper storey changed (Floors.MapMeshDirty_Postfix -> OpenAirChanged); where that turned a cell into
        // a hole or back, the blocks round it are rebuilt.
        private sealed class OpenAir
        {
            public bool[] outside;
            public bool dirty = true;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Map, OpenAir> openAir = new System.Runtime.CompilerServices.ConditionalWeakTable<Map, OpenAir>();
        private static bool openAirDirtying;
        public static int OpenAirBuilds;   // (harness: how often it was worked out)
        public static double OpenAirMs;

        public static void OpenAirChanged(Map map)
        {
            OpenAir o;
            if (!openAirDirtying && map != null && openAir.TryGetValue(map, out o))
            {
                o.dirty = true;
            }
        }

        private static bool InHole(Map map, IntVec3 c)
        {
            OpenAir o = openAir.GetValue(map, m => new OpenAir());
            if (o.dirty)
            {
                BuildOpenAir(map, o);
            }
            int i = c.z * map.Size.x + c.x;
            return o.outside != null && i >= 0 && i < o.outside.Length && !o.outside[i];
        }

        private static void BuildOpenAir(Map map, OpenAir o)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            o.dirty = false;
            int w = map.Size.x;
            bool[] was = o.outside;
            bool[] outside = new bool[w * map.Size.z];
            int rows;
            float[] heights;
            int[] shifts;
            int n = Floors.Bands(map, out rows, out heights, out shifts);
            Queue<int> q = new Queue<int>();
            for (int k = 1; k <= n; k++)
            {
                int z0 = shifts[k];
                if (z0 < 0 || z0 + rows > map.Size.z)
                {
                    continue;
                }
                TerrainGrid terrain = map.terrainGrid;
                Func<int, int, bool> open = (x, z) =>
                {
                    // (Floors.HasFloor on an upper storey: the terrain is drawn there)
                    IntVec3 c = new IntVec3(x, 0, z);
                    float h;
                    return terrain.TerrainAt(c).dontRender && !Floors.FullBlock(map, c, out h, true);
                };
                for (int x = 0; x < w; x++)
                {
                    for (int r = 0; r < rows; r++)
                    {
                        if ((x == 0 || x == w - 1 || r == 0 || r == rows - 1) && open(x, z0 + r))
                        {
                            int i = (z0 + r) * w + x;
                            if (!outside[i])
                            {
                                outside[i] = true;
                                q.Enqueue(i);
                            }
                        }
                    }
                }
                while (q.Count > 0)
                {
                    int i = q.Dequeue();
                    int x = i % w, z = i / w;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                        int nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (nx < 0 || nx >= w || nz < z0 || nz >= z0 + rows)
                        {
                            continue;
                        }
                        int j = nz * w + nx;
                        if (!outside[j] && open(nx, nz))
                        {
                            outside[j] = true;
                            q.Enqueue(j);
                        }
                    }
                }
            }
            o.outside = outside;
            OpenAirBuilds++;
            OpenAirMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (was == null || was.Length != outside.Length)
            {
                return;
            }
            // a cell that became a hole or stopped being one: the rock under it and the floors round it are made again
            openAirDirtying = true;
            try
            {
                for (int i = 0; i < outside.Length; i++)
                {
                    if (was[i] != outside[i])
                    {
                        IntVec3 c = new IntVec3(i % w, 0, i / w);
                        map.mapDrawer.MapMeshDirty(c, (ulong)RimWorld.MapMeshFlagDefOf.Terrain | (ulong)RimWorld.MapMeshFlagDefOf.Buildings);
                        IntVec3 below = Floors.Stacked(map, c, -1);
                        if (below.IsValid && below.InBounds(map))
                        {
                            map.mapDrawer.MapMeshDirty(below, (ulong)RimWorld.MapMeshFlagDefOf.Buildings);
                        }
                    }
                }
            }
            finally
            {
                openAirDirtying = false;
            }
        }

        // The cell above a cell in its column covers it: a floor (a ledge over the rock) or a block standing on it.
        private static bool CoveredAbove(Map map, IntVec3 above)
        {
            float h;
            return above.IsValid && above.InBounds(map) && (Floors.HasFloor(map, above) || Floors.FullBlock(map, above, out h, true));
        }

        // One corner of a rock block: the outline at the ground, part of the way up, at the cap; covered says the face
        // goes straight up to the floor above (no lean) there.
        private static void SmoothCorner(Map map, int cx, int cz, float height, bool hasUp, IntVec3 upOff, out Vector3 ground, out Vector3 mid, out Vector3 top,
            out bool covered, out bool moved)
        {
            int dx, dz;
            ground = ChamferedCorner(map, cx, cz, height, out moved, out dx, out dz);
            covered = false;
            if (hasUp)
            {
                for (int i = 0; i < 4 && !covered; i++)
                {
                    int x = cx - 1 + (i & 1);
                    int z = cz - 1 + (i >> 1);
                    if (Solid(map, x, z, height))
                    {
                        covered = CoveredAbove(map, new IntVec3(x + upOff.x, 0, z + upOff.z));
                    }
                }
            }
            if (dx == 0 && dz == 0)
            {
                mid = new Vector3(ground.x, height * 0.5f, ground.z);
                top = new Vector3(ground.x, height, ground.z);
                return;
            }
            float h1 = Hashing.Unit(cx, cz, 1);
            float h2 = Hashing.Unit(cx, cz, 2);
            float h3 = Hashing.Unit(cx, cz, 3);
            float diag = moved ? 0.7f : 1f;   // a chamfered corner leans along the diagonal
            if (covered)
            {
                // Straight up to the floor above; with face noise a small bend part of the way up.
                float bend = CliffMode >= 2 ? (0.03f + 0.10f * h1) * diag : 0f;
                mid = new Vector3(ground.x + dx * bend, height * (0.30f + 0.40f * h2), ground.z + dz * bend);
                top = new Vector3(ground.x, height, ground.z);
                return;
            }
            float inMid = (CliffMode >= 2 ? 0.05f + 0.18f * h1 : 0.05f + 0.10f * h1) * diag;
            float inTop = (0.18f + 0.14f * h3) * diag;
            mid = new Vector3(ground.x + dx * inMid, height * (0.34f + 0.22f * h2), ground.z + dz * inMid);
            top = new Vector3(ground.x + dx * inTop, height - (0.03f + 0.10f * h1), ground.z + dz * inTop);
        }

        private static bool PrintRockSmooth(SectionLayer layer, Thing t, float height, WallKind kind)
        {
            Color tint = MineMarks.Tint(t, t.DrawColor);   // marked for mining: toward the mark's colour
            Color? two = OreSecond(t, kind);               // ore: its nuggets' colour
            Material capMat = kind == WallKind.Rock ? RockCapMaterial(t) : null;
            Material topMat = capMat ?? MaterialFor(kind, true, 0, false, tint, two);
            if (topMat == null)
            {
                return false;
            }
            LayerSubMesh top = MeshWriter.Sub(layer, topMat);
            if (top == null || top.verts.Count + 4 > MeshWriter.MaxVerts)
            {
                return false;
            }
            Material solidMat = R3DMaterials.Solid;
            LayerSubMesh solid = solidMat != null ? MeshWriter.Sub(layer, solidMat) : null;
            bool room = solid != null && solid.verts.Count + 200 <= MeshWriter.MaxVerts;

            Map map = t.Map;
            IntVec3 c = t.Position;
            float squash = View3D.Squash;
            Color32 lineColor = R3DMaterials.Shaded(tint, LineShade);
            bool tags = CutTags.Enabled;
            int own = tags && CanBeCut(t.def) ? ShaderCut.Lower : 0;
            int whole = CutTags.Mark(layer);

            // The column: this storey's floor height and row shift, the storey above (how far up its floor is) and below.
            float floorH;
            int zShift;
            int storey = Floors.ColumnFloor(map, c, out floorH, out zShift);
            IntVec3 up = storey >= 0 ? Floors.Stacked(map, c, 1) : IntVec3.Invalid;
            bool hasUp = up.IsValid && up.InBounds(map);
            float upH = height;
            if (hasUp)
            {
                float hu;
                int zu;
                Floors.ColumnFloor(map, up, out hu, out zu);
                upH = Mathf.Max(height, hu - floorH);
            }
            IntVec3 upOff = hasUp ? up - c : IntVec3.Zero;
            bool onRock = storey >= 1 && NaturalRock(map, Floors.Stacked(map, c, -1));
            bool rockAbove = hasUp && NaturalRock(map, up);

            for (int k = 0; k < 4; k++)
            {
                SmoothCorner(map, c.x + CornerX[k], c.z + CornerZ[k], height, hasUp, upOff, out smGround[k], out smMid[k], out smTop[k], out smCovered[k], out smMoved[k]);
            }

            // The cap (under the floor above where there is one: then drawn only while that floor is hidden, CoveredByFloor).
            bool coveredCap = CoveredByFloor(map, c);
            if (coveredCap)
            {
                LayerSubMesh ct = MeshWriter.Sub(layer, CoveredTwin(topMat));
                if (ct != null && ct.verts.Count + 4 <= MeshWriter.MaxVerts)
                {
                    top = ct;
                }
            }
            MeshWriter.TexturedQuad(top,
                ToWorld(smTop[0], squash), ToWorld(smTop[1], squash), ToWorld(smTop[2], squash), ToWorld(smTop[3], squash),
                CapUV(smTop[0], capMat, upOff.z, c), CapUV(smTop[1], capMat, upOff.z, c), CapUV(smTop[2], capMat, upOff.z, c), CapUV(smTop[3], capMat, upOff.z, c), White);
            if (room)
            {
                Vector3 down = new Vector3(0f, -0.01f, 0f);
                MeshWriter.Quad(solid, ToWorld(smTop[0] + down, squash), ToWorld(smTop[1] + down, squash),
                    ToWorld(smTop[2] + down, squash), ToWorld(smTop[3] + down, squash), R3DMaterials.Shaded(tint, 0.9f));
            }

            for (int f = 0; f < 4; f++)
            {
                markRim[f] = false;
            }
            for (int f = 0; f < 4; f++)
            {
                int cond = 0;
                if (!Exposed(map, c + FaceOut[f], height))
                {
                    if (!tags || !MayDrop(map, c + FaceOut[f]))
                    {
                        continue;
                    }
                    cond = ShaderCut.Cond(FaceOut[f]);
                }
                int faceMark = CutTags.Mark(layer);
                int l = FaceLeft[f];
                int r = FaceRightCorner[f];
                Vector3 right = FaceRight[f].ToVector3();
                if (smMoved[l] || smMoved[r])
                {
                    Vector3 run = smGround[r] - smGround[l];
                    run.y = 0f;
                    if (run.sqrMagnitude > 1e-4f)
                    {
                        right = run.normalized;
                    }
                }
                Vector3 outward = new Vector3(right.z, 0f, -right.x);
                Vector3 back = outward * -Gap;
                bool coveredFace = smCovered[l] && smCovered[r];

                // Up the face: ground to mid, mid to cap, and where covered on to the floor above.
                for (int part = 0; part < 3; part++)
                {
                    Vector3 a, b, d, e;
                    if (part == 0)
                    {
                        a = smGround[l]; b = smMid[l]; d = smGround[r]; e = smMid[r];
                    }
                    else if (part == 1)
                    {
                        a = smMid[l]; b = smTop[l]; d = smMid[r]; e = smTop[r];
                    }
                    else
                    {
                        if (!smCovered[l] && !smCovered[r])
                        {
                            break;
                        }
                        a = smTop[l]; d = smTop[r];
                        b = smCovered[l] ? new Vector3(a.x, upH, a.z) : a;
                        e = smCovered[r] ? new Vector3(d.x, upH, d.z) : d;
                    }
                    // the upper part catches more light, as the old leaning face did: also on a straight face at the top of
                    // its column (under a ledge), not part way up a tall cliff
                    bool sloped = part == 1 && (!coveredFace || !rockAbove);
                    Material sideMat = MaterialFor(kind, false, f, sloped, tint, two);
                    LayerSubMesh side = sideMat != null ? MeshWriter.Sub(layer, sideMat) : null;
                    bool textured = side != null && side.verts.Count + 4 <= MeshWriter.MaxVerts;
                    if (textured)
                    {
                        int sideMark = CutTags.Mark(layer);
                        MeshWriter.TexturedQuad(side, ToWorld(a, squash), ToWorld(b, squash), ToWorld(e, squash), ToWorld(d, squash),
                            ColumnUV(a, right, floorH, zShift), ColumnUV(b, right, floorH, zShift), ColumnUV(e, right, floorH, zShift), ColumnUV(d, right, floorH, zShift), White);
                        CutTags.Apply(layer, sideMark, c, own | cond | ShaderCut.TexSide);
                    }
                    if (room)
                    {
                        Vector3 shift = textured ? back : Vector3.zero;
                        float shade = (sloped ? 1f + SlopeLight * 1.4f : 1f) * 0.9f;
                        MeshWriter.Quad(LitSide(layer, f, solid), ToWorld(a + shift, squash), ToWorld(b + shift, squash), ToWorld(e + shift, squash), ToWorld(d + shift, squash),
                            R3DMaterials.Shaded(tint, shade));
                    }
                }
                if (!room)
                {
                    CutTags.Apply(layer, faceMark, c, own | cond);
                    continue;
                }

                // The dark rim along the top: on the cap, or on the floor's edge above a covered face (none under rock,
                // whose own face carries on up, and none where the surface goes on at that height: SurfaceGoesOn).
                if (!(rockAbove && coveredFace) && !SurfaceGoesOn(map, c, f))
                {
                    Vector3 lift = new Vector3(0f, 0.002f, 0f);
                    Vector3 rl = smCovered[l] ? new Vector3(smTop[l].x, upH + 0.004f, smTop[l].z) : smTop[l] + lift;
                    Vector3 rr = smCovered[r] ? new Vector3(smTop[r].x, upH + 0.004f, smTop[r].z) : smTop[r] + lift;
                    Vector3 inner = outward * -OutlineWidth;
                    MeshWriter.Quad(solid, ToWorld(rl, squash), ToWorld(rl + inner, squash), ToWorld(rr + inner, squash), ToWorld(rr, squash), lineColor);
                    markRim[f] = !smCovered[l] && !smCovered[r];   // the rim lies on the cap
                }

                // The line along the ground, not where this storey stands on rock (the face below carries on).
                if (!onRock)
                {
                    Vector3 q0 = smGround[l] - back;
                    Vector3 q1 = smGround[r] - back;
                    Vector3 lineTop = new Vector3(0f, OutlineWidth * 0.7f, 0f);
                    MeshWriter.Quad(solid, ToWorld(q0, squash), ToWorld(q0 + lineTop, squash), ToWorld(q1 + lineTop, squash), ToWorld(q1, squash), lineColor);
                }
                CutTags.Apply(layer, faceMark, c, own | cond);
            }
            if (room)
            {
                // marked for mining: the faint top-edge line on the cap (under a floor, shown only while the floor is hidden)
                MineMarks.PrintOutline(coveredCap ? MeshWriter.Sub(layer, CoveredTwin(solidMat)) : solid, t, smTop, markRim, OutlineWidth,
                    capMat != null ? capMat.color : tint * TopShade, squash);
            }
            CutTags.Apply(layer, whole, c, own);
            return true;
        }

        // Side texture coordinates that run on up a column of storeys (v from the ground) and along a run (u along the
        // face, with the storey's row shift taken out).
        private static Vector2 ColumnUV(Vector3 cells, Vector3 right, float floorH, int zShift)
        {
            Vector3 p = new Vector3(cells.x, 0f, cells.z - zShift);
            return new Vector2(Vector3.Dot(p, right) * 0.5f, (floorH + cells.y) / SideCellsHigh);
        }

        public static int FaceLeftCorner(int f)
        {
            return FaceLeft[f];
        }

        public static int FaceRightCornerOf(int f)
        {
            return FaceRightCorner[f];
        }

        // The chamfered ground outline of a natural rock block (corners SW, NW, NE, SE in map cells, y 0) when the smooth
        // look draws it, for its contact shadow (GroundShadows.AroundBlock, called while the wall layer is built).
        public static bool RockGroundCorners(Thing t, out Vector3[] corners, out bool[] moved)
        {
            corners = null;
            moved = null;
            if (CliffMode <= 0 || !IsoRimMod.Settings.ruggedRock || t.def.building == null || !t.def.building.isNaturalRock)
            {
                return false;
            }
            Map map = t.Map;
            IntVec3 c = t.Position;
            float height = BlockHeightAt(map, c);
            corners = new Vector3[4];
            moved = new bool[4];
            bool any = false;
            for (int k = 0; k < 4; k++)
            {
                int dx, dz;
                corners[k] = ChamferedCorner(map, c.x + CornerX[k], c.z + CornerZ[k], height, out moved[k], out dx, out dz);
                any |= moved[k];
            }
            return any;
        }

        // ---- the floor's terrain over rock (stacked floors): cut to the rock's chamfered outline

        private static readonly Dictionary<int, Vector3[]> clipCorners = new Dictionary<int, Vector3[]>();

        // Postfix work for SectionLayer_Terrain.Regenerate on an upper floor: every terrain triangle of a cell standing
        // over natural rock is moved into the rock's chamfered outline (bilinear over the cell, so the edge blends and
        // their centres follow).
        public static void ClipTerrain(SectionLayer layer, Section section)
        {
            if (CliffMode <= 0 || section == null || !View3D.Active || !IsoRimMod.Settings.ruggedRock)
            {
                return;
            }
            Map map = section.map;
            float h;
            int z;
            if (Floors.ColumnFloor(map, section.botLeft, out h, out z) < 1)
            {
                return;
            }
            bool fullWas = FullHeights;
            FullHeights = ShaderCut.On;   // the rock's outline as the wall layer works it out
            try
            {
                clipCorners.Clear();
                foreach (IntVec3 c in section.CellRect)
                {
                    IntVec3 below = Floors.Stacked(map, c, -1);
                    if (!NaturalRock(map, below) || !SectionLayer_IsoRimWalls.Handles(below.GetEdifice(map)))
                    {
                        continue;
                    }
                    float rh = BlockHeightAt(map, below);
                    Vector3[] q = new Vector3[4];
                    bool any = false;
                    for (int k = 0; k < 4; k++)
                    {
                        bool moved;
                        q[k] = FloorCorner(map, c, below, k, rh, out moved);
                        any |= moved;
                    }
                    if (any)
                    {
                        clipCorners[c.x * 4096 + c.z] = q;
                    }
                }
                if (clipCorners.Count == 0)
                {
                    return;
                }
                foreach (LayerSubMesh sm in layer.subMeshes)
                {
                    if (sm.verts.Count == 0 || sm.mesh == null)
                    {
                        continue;
                    }
                    bool changed = false;
                    bool[] done = new bool[sm.verts.Count];
                    for (int i = 0; i + 2 < sm.tris.Count; i += 3)
                    {
                        Vector3 v0 = sm.verts[sm.tris[i]], v1 = sm.verts[sm.tris[i + 1]], v2 = sm.verts[sm.tris[i + 2]];
                        int cx = Mathf.FloorToInt((v0.x + v1.x + v2.x) / 3f);
                        int cz = Mathf.FloorToInt((v0.z + v1.z + v2.z) / 3f);
                        Vector3[] q;
                        if (!clipCorners.TryGetValue(cx * 4096 + cz, out q))
                        {
                            continue;
                        }
                        for (int j = 0; j < 3; j++)
                        {
                            int vi = sm.tris[i + j];
                            if (done[vi])
                            {
                                continue;
                            }
                            done[vi] = true;
                            Vector3 v = sm.verts[vi];
                            float u = Mathf.Clamp01(v.x - cx);
                            float w = Mathf.Clamp01(v.z - cz);
                            // corners: 0 SW, 1 NW, 2 NE, 3 SE
                            Vector3 s = Vector3.Lerp(q[0], q[3], u);
                            Vector3 n = Vector3.Lerp(q[1], q[2], u);
                            Vector3 p = Vector3.Lerp(s, n, w);
                            sm.verts[vi] = new Vector3(cx + p.x, v.y, cz + p.z);
                            changed = true;
                        }
                    }
                    if (changed)
                    {
                        sm.mesh.SetVertices(sm.verts);
                        sm.mesh.RecalculateBounds();
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Cutting the floor's edge over rock failed: " + e, 93611471);
            }
            finally
            {
                FullHeights = fullWas;
            }
        }

        // The floor's slab over natural rock: only its top, cut to the rock's chamfered outline (corners SW, NW, NE, SE
        // relative to the cell); the rock is the body under it.
        public static bool SlabOverRock(Map map, IntVec3 c, out Vector3[] corners)
        {
            corners = null;
            IntVec3 below = Floors.Stacked(map, c, -1);
            if (CliffMode <= 0 || !IsoRimMod.Settings.ruggedRock || !View3D.Active || !NaturalRock(map, below) || !SectionLayer_IsoRimWalls.Handles(below.GetEdifice(map)))
            {
                return false;
            }
            bool fullWas = FullHeights;
            FullHeights = ShaderCut.On;
            try
            {
                float rh = BlockHeightAt(map, below);
                corners = new Vector3[4];
                for (int k = 0; k < 4; k++)
                {
                    bool moved;
                    corners[k] = FloorCorner(map, c, below, k, rh, out moved);
                }
            }
            finally
            {
                FullHeights = fullWas;
            }
            return true;
        }

        // Corner k (SW, NW, NE, SE) of the floor's cell c over the rock in below, relative to the cell, for both its slab
        // top and its terrain: the rock's chamfered corner, unless the floor goes on past that corner (a cell beside c
        // there, sharing an edge with it, has a floor or a block on this storey, as over a dug cell): then the square
        // corner, or the cut-off corner would leave a hole in the floor. Only a cell touching the corner diagonally
        // keeps the cut (the two floors just meet at a point there). A hole's corner (CornerAt kind 2) always moves: the
        // cells round it are the hole and three floors over rock, all cut to it.
        private static Vector3 FloorCorner(Map map, IntVec3 c, IntVec3 below, int k, float rh, out bool moved)
        {
            int dx, dz, kind;
            Vector3 g = CornerAt(map, below.x + CornerX[k], below.z + CornerZ[k], rh, out kind, out dx, out dz);
            moved = kind != 0;
            if (kind == 1 && (FloorGoesOn(map, c + new IntVec3(CornerX[k] == 0 ? -1 : 1, 0, 0)) || FloorGoesOn(map, c + new IntVec3(0, 0, CornerZ[k] == 0 ? -1 : 1))))
            {
                moved = false;
                return new Vector3(CornerX[k], 0f, CornerZ[k]);
            }
            return new Vector3(g.x - below.x, 0f, g.z - below.z);
        }

        private static bool FloorGoesOn(Map map, IntVec3 c)
        {
            float h;
            return c.InBounds(map) && (Floors.HasFloor(map, c) || Floors.FullBlock(map, c, out h, true));
        }

        // ---- rock under a floor (rock-top floors, isorim-pz-rocktop-floors-plan.md §4.2-4.3)

        // The seam rule: a top-edge line (the dark rim) is drawn only where the walkable surface really ends. On a rock with
        // a floor right above it (its top is walkable: a mountain top), where the cell beyond face f has a floor (or a full
        // block) on that storey too, the surface goes on at the rim's height (a stairs landing or a slab over the open
        // ground beside a mountain top): no rim. A rock with no floor above keeps its rims (its top is not walkable; plan
        // §4.4); without floors (and on the top storey) Stacked is Invalid and the rim stays.
        private static bool SurfaceGoesOn(Map map, IntVec3 c, int f)
        {
            IntVec3 up = Floors.Stacked(map, c, 1);
            if (!up.IsValid || !up.InBounds(map) || !Floors.HasFloor(map, up))
            {
                return false;
            }
            IntVec3 n = c + FaceOut[f];
            return n.InBounds(map) && FloorGoesOn(map, Floors.Stacked(map, n, 1));
        }

        // A rock whose cell has a floor right above it: its cap goes into a "covered" sub-mesh (its own copy of the cap
        // material), drawn only while that floor's storey is hidden (a cut view: the cap stands in for the floor, same
        // texture and UV). The plain solid quad just under the cap stays: the rock's body, as the slab body under a floor
        // (it closes the hair-thin steps where a floor over rock meets a rock column's lower cap). While the floor shows, it is drawn in its own cell (its
        // light, fog and hiding) and the cap under it is not drawn at all. Not under a block standing on that floor (a
        // rock column: the floor there has no slab, Floors.SlabGeometry, and the cap closes the column where the block
        // above is chamfered). Without floors Stacked is Invalid: never covered.
        private static bool CoveredByFloor(Map map, IntVec3 c)
        {
            float h;
            int z;
            if (Floors.ColumnFloor(map, c, out h, out z) < 0)
            {
                return false;
            }
            IntVec3 up = Floors.Stacked(map, c, 1);
            return up.IsValid && up.InBounds(map) && Floors.HasFloor(map, up) && !Floors.FullBlock(map, up, out h, true);
        }

        public static bool HideCoveredCaps = true;   // harness A/B (probe key covercaps=0|1): off, covered caps always drawn

        private static readonly Dictionary<Material, Material> coveredTwins = new Dictionary<Material, Material>();
        private static readonly HashSet<Material> coveredSet = new HashSet<Material>();

        private static Material CoveredTwin(Material m)
        {
            if (m == null)
            {
                return null;
            }
            Material t;
            if (coveredTwins.TryGetValue(m, out t))
            {
                return t;
            }
            t = new Material(m);
            t.name = m.name + "_Covered";
            if (IsoGlow.Marked(m))
            {
                IsoGlow.Use(t, IsoGlow.OwnCell);   // its own stencil mark, as the original's
            }
            coveredTwins[m] = t;
            coveredSet.Add(t);
            return t;
        }

        // Per draw of a walls layer (Patches.DrawLayer_Section, ground and upper floors): its covered sub-meshes are off
        // while the storey above its section is shown. One flag per section; meshes are not rebuilt on a view change.
        public static void UpdateCovered(SectionLayer layer)
        {
            if (coveredSet.Count == 0)
            {
                return;
            }
            Section s = Floors.SectionOf(layer);
            int k = s != null ? Floors.FloorOfRow(s.botLeft.z) : -1;
            bool hide = HideCoveredCaps && Floors.On && k >= 0 && Floors.ShownSolid(k + 1);
            List<LayerSubMesh> subs = layer.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                if (subs[i].material != null && coveredSet.Contains(subs[i].material))
                {
                    subs[i].disabled = hide;
                }
            }
        }
    }
}
