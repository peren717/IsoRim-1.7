using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The placement grid: while a building or floor is being placed in the 3D view (Interaction.PlacingAt: the building tool
    // in hand, the pointer where the game draws its ghost or a floor's bracket), bright white lines along the cell edges round the pointer's cell, fading
    // with the distance from that cell's middle to nothing at the range (settings placeGrid, placeGridRange). THE one place
    // that draws it.
    //
    // Where: the cells of the pointer's floor (Floors: the pointer's cell is in the rows of the floor it works on; the mesh is
    // placed by that cell, so stacked floors move it onto that floor as every draw, Floors.Single_Prefix) that are in the map
    // and in that floor's rows; every such cell, rock and walls included (they hide the lines by depth). Height: the walking
    // surface (a bridge deck or its ramp, Bridges.HeightAt; else the ground or the floor) plus GroundLift, as the selection
    // rings lie.
    // Look: a bright white core LinePixels wide on screen (solid in its middle) over a dark edge HaloPixels wide (reads on
    // light floors) over a soft white glow GlowPixels wide (reads on dark soil), feathered to nothing at their sides; drawn
    // after the game's lighting overlay (readable at night), depth tested and not written (walls, buildings, plants, pawns
    // and the ghost in front hide it), as the selection rings.
    // Cost: nothing unless something is being placed; then one draw a frame. The layout (which cells, their surface heights)
    // is worked out only when the pointer's cell, the range, the floor or the bridges setting changes; the mesh (24 vertices
    // per cell edge: ~11k at range 8, ~54k at 16) is rebuilt from it then and when the line width changes (zoom, a turn),
    // into reused lists.
    [StaticConstructorOnStartup]
    internal static class PlaceGrid
    {
        // ---- tuning (one place)
        internal const int DefaultRange = 8;     // cells (the setting's default)
        internal const int MinRange = 3;
        internal const int MaxRange = 16;
        private const float LinePixels = 2.5f;   // the white core's width on screen
        private const float LineSolid = 0.5f;    // share of the core's width at full opacity (the rest feathers out)
        private const float LineAlpha = 1f;      // the core's opacity at the pointer
        private const float GlowPixels = 9f;     // the soft white glow round the core (reads on dark soil and grass)
        private const float GlowAlpha = 0.2f; 
        private const float HaloPixels = 5f;     // the dark edge just round the core (reads on light floors)
        private const float HaloAlpha = 0.3f;
        private const float FadePower = 3f;      // opacity 1 - (d / range)^FadePower: near full strength well out, 0 at the range
        private const float GroundLift = 0.02f;  // above the walking surface (true cells), clear of it in depth (as SelectionRings)
        private const float WidthStep = 0.08f;   // a line width change below this share keeps the mesh (zooming)
        private const float MaxWidth = 0.3f;     // cells: the widest a line gets (the ground seen almost edge on)
        private const int Queue = 3110;          // after the game's lighting overlay (3100), before the fog of war (3175)

        // ---- harness counters
        internal static int Drawn;               // frames drawn
        internal static int Builds;              // mesh rebuilds
        internal static int Layouts;             // layout passes (cells and surface heights)
        internal static IntVec3 LastCentre = IntVec3.Invalid;
        internal static int LastRange;
        internal static int LastSegments;        // cell edges in the mesh
        internal static float LastReach;         // the farthest vertex with any opacity, from the centre (cells)
        internal static float LastCentreHeight;  // the walking surface at the centre's corners (true cells, highest)

        private static Material mat;
        private static Mesh mesh;
        private static bool failed;

        // The opacity share of a line point d cells from the pointer cell's middle: 1 there, staying high near it and falling
        // to 0 at the range (a soft circle).
        internal static float Fade(float d, int range)
        {
            float t = Mathf.Clamp01(d / Mathf.Max(1, range));
            return 1f - Mathf.Pow(t, FadePower);
        }

        // From View3D every frame while the 3D view is ready.
        internal static void Draw()
        {
            if (failed)
            {
                return;
            }
            try
            {
                DrawGrid();
            }
            catch (Exception e)
            {
                failed = true;
                Log.ErrorOnce("[IsoRim] Drawing the placement grid failed; it is off from now on: " + e, 93627141);
            }
        }

        // What the mesh was built for (the map by its id: no reference kept to a map left behind).
        private static int keyMapId = -1;
        private static IntVec3 keyCell = IntVec3.Invalid;
        private static int keyRange, keyFloor, keyRows;
        private static float keyWx, keyWz, keySquash;
        private static bool keyBridges;

        private static void DrawGrid()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            IntVec3 c;
            if (st == null || !st.placeGrid || !View3D.Ready || Interaction.PlacingAt(out c) == null)
            {
                return;
            }
            Map map = Find.CurrentMap;
            if (mat == null)
            {
                mat = R3DMaterials.Make(R3DMaterials.BlendDef, "IsoRim_PlaceGrid", Queue);
                if (mat == null)
                {
                    failed = true;
                    return;
                }
            }
            int range = Mathf.Clamp(st.placeGridRange, MinRange, MaxRange);
            int floor = Floors.On ? Floors.FloorOfRow(c.z) : 0;
            int rows = Floors.On ? Floors.GroundRowsOf(map) : 0;
            float wx, wz;
            LineWidths(c, out wx, out wz);
            bool bridges = Bridges.Active;
            float squash = View3D.Squash;
            // the layout (which cells, their surface heights) only when the cells change; the mesh also when the line width
            // changes (zoom, a turn, free look)
            bool layout = map.uniqueID != keyMapId || c != keyCell || range != keyRange || floor != keyFloor || rows != keyRows || bridges != keyBridges;
            if (layout)
            {
                keyMapId = map.uniqueID;
                keyCell = c;
                keyRange = range;
                keyFloor = floor;
                keyRows = rows;
                keyBridges = bridges;
                Layout(map, c, range, floor, rows, bridges);
            }
            if (mesh == null || layout || squash != keySquash || Changed(wx, keyWx) || Changed(wz, keyWz))
            {
                keySquash = squash;
                keyWx = wx;
                keyWz = wz;
                Build(c, range, wx, wz, squash);
            }
            // placed by the pointer cell's middle: the row of the matrix puts it on that cell's floor (Floors.Single_Prefix)
            Graphics.DrawMesh(mesh, Matrix4x4.Translate(new Vector3(c.x + 0.5f, 0f, c.z + 0.5f)), mat, 0);
            Drawn++;
            LastCentre = c;
            LastRange = range;
        }

        private static bool Changed(float w, float was)
        {
            return Mathf.Abs(w - was) > WidthStep * was;
        }

        // The width (cells) of a line along x (wx, across it in z) and along z (wz) that shows LinePixels wide on screen at the
        // pointer, for any camera (turned, tilted, perspective): from the screen steps of one cell along x and along z there.
        private static void LineWidths(IntVec3 c, out float wx, out float wz)
        {
            Vector3 p = new Vector3(c.x + 0.5f, 0f, c.z + 0.5f);
            if (Floors.On)
            {
                p += Floors.RowOffset(c.z);
            }
            Vector2 o = View3D.WorldToScreen(p);
            Vector2 su = View3D.WorldToScreen(p + Vector3.right) - o;
            Vector2 sv = View3D.WorldToScreen(p + Vector3.forward) - o;
            float area = Mathf.Abs(su.x * sv.y - su.y * sv.x);   // pixels per cell, squared, on the ground
            wx = area > 1e-4f ? Mathf.Min(MaxWidth, LinePixels * su.magnitude / area) : MaxWidth;
            wz = area > 1e-4f ? Mathf.Min(MaxWidth, LinePixels * sv.magnitude / area) : MaxWidth;
        }

        // ---- the mesh (local to the pointer cell's middle; drawn units: height over the squash)

        private static readonly List<Vector3> verts = new List<Vector3>();
        private static readonly List<Color32> colours = new List<Color32>();
        private static readonly List<int> tris = new List<int>();
        // ---- the layout (Layout, kept while the cells stay): the grid's square of corners x0..x0+side-1 (z the same)
        private static float[] corners = new float[0];   // the walking surface at each corner
        private static bool[] covered = new bool[0];     // each cell x0-1..x0+side-1 (z the same) the grid covers
        private static int cornerSide;

        // A cell the grid covers: in the map and in the rows of the pointer's floor.
        private static bool Covers(Map map, int x, int z, int floor, int rows)
        {
            if (x < 0 || z < 0 || x >= map.Size.x || z >= map.Size.z)
            {
                return false;
            }
            return rows <= 0 || Floors.FloorOfRow(z) == floor;
        }

        private static void Layout(Map map, IntVec3 c, int range, int floor, int rows, bool bridges)
        {
            Layouts++;
            int x0 = c.x - range, z0 = c.z - range, side = 2 * range + 2;
            if (corners.Length < side * side)
            {
                corners = new float[side * side];
                covered = new bool[(side + 1) * (side + 1)];
            }
            cornerSide = side;
            for (int j = 0; j < side; j++)
            {
                for (int i = 0; i < side; i++)
                {
                    corners[j * side + i] = bridges ? CornerHeight(map, x0 + i, z0 + j) : 0f;
                }
            }
            for (int j = 0; j <= side; j++)
            {
                for (int i = 0; i <= side; i++)
                {
                    covered[j * (side + 1) + i] = Covers(map, x0 - 1 + i, z0 - 1 + j, floor, rows);
                }
            }
        }

        private static bool Covered(int x, int z, int x0, int z0)
        {
            return covered[(z - z0 + 1) * (cornerSide + 1) + (x - x0 + 1)];
        }

        private static void Build(IntVec3 c, int range, float wx, float wz, float squash)
        {
            Builds++;
            verts.Clear();
            colours.Clear();
            tris.Clear();
            int x0 = c.x - range, z0 = c.z - range, side = cornerSide;   // corners x0..x0+side-1
            LastCentreHeight = Mathf.Max(Mathf.Max(Corner(c.x, c.z, x0, z0), Corner(c.x + 1, c.z, x0, z0)),
                Mathf.Max(Corner(c.x, c.z + 1, x0, z0), Corner(c.x + 1, c.z + 1, x0, z0)));
            float cx = c.x + 0.5f, cz = c.z + 0.5f;
            int segments = 0;
            float reach = 0f;
            for (int j = z0; j <= z0 + side - 1; j++)
            {
                for (int i = x0; i < x0 + side - 1; i++)
                {
                    // the edge along x from corner (i, j) to (i + 1, j), between the cells (i, j - 1) and (i, j)
                    if (Covered(i, j - 1, x0, z0) || Covered(i, j, x0, z0))
                    {
                        if (Segment(new Vector2(i, j), new Vector2(i + 1, j), Corner(i, j, x0, z0), Corner(i + 1, j, x0, z0), wx, cx, cz, range, squash, ref reach))
                        {
                            segments++;
                        }
                    }
                }
            }
            for (int i = x0; i <= x0 + side - 1; i++)
            {
                for (int j = z0; j < z0 + side - 1; j++)
                {
                    // the edge along z from corner (i, j) to (i, j + 1), between the cells (i - 1, j) and (i, j)
                    if (Covered(i - 1, j, x0, z0) || Covered(i, j, x0, z0))
                    {
                        if (Segment(new Vector2(i, j), new Vector2(i, j + 1), Corner(i, j, x0, z0), Corner(i, j + 1, x0, z0), wz, cx, cz, range, squash, ref reach))
                        {
                            segments++;
                        }
                    }
                }
            }
            if (mesh == null)
            {
                mesh = new Mesh { name = "IsoRim_PlaceGrid" };
                mesh.MarkDynamic();
            }
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetColors(colours);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            LastSegments = segments;
            LastReach = reach;
        }

        private static float Corner(int x, int z, int x0, int z0)
        {
            return corners[(z - z0) * cornerSide + (x - x0)];
        }

        // The walking surface at a cell corner: the highest of the four cells meeting there (a deck's edge lies on the deck).
        private static float CornerHeight(Map map, int x, int z)
        {
            const float In = 0.01f;   // a point just inside each of the four cells
            float h = Bridges.HeightAt(map, x - In, z - In);
            h = Mathf.Max(h, Bridges.HeightAt(map, x + In, z - In));
            h = Mathf.Max(h, Bridges.HeightAt(map, x - In, z + In));
            return Mathf.Max(h, Bridges.HeightAt(map, x + In, z + In));
        }

        // One cell edge a..b (world x, z) at the surface heights ha, hb, as three feathered strips (the glow, the dark edge,
        // then the core over them), the core width wide across; false when it is past the range (no opacity at either end).
        private static bool Segment(Vector2 a, Vector2 b, float ha, float hb, float width, float cx, float cz, int range, float squash, ref float reach)
        {
            float da = Vector2.Distance(a, new Vector2(cx, cz)), db = Vector2.Distance(b, new Vector2(cx, cz));
            float fa = Fade(da, range), fb = Fade(db, range);
            if (fa <= 0f && fb <= 0f)
            {
                return false;
            }
            if (fa > 0f)
            {
                reach = Mathf.Max(reach, da);
            }
            if (fb > 0f)
            {
                reach = Mathf.Max(reach, db);
            }
            Vector2 across = b.x != a.x ? new Vector2(0f, 1f) : new Vector2(1f, 0f);
            Vector3 pa = new Vector3(a.x - cx, (ha + GroundLift) / squash, a.y - cz);
            Vector3 pb = new Vector3(b.x - cx, (hb + GroundLift) / squash, b.y - cz);
            float px = width / LinePixels;   // cells per screen pixel across this line
            Strip(pa, pb, across, px * GlowPixels * 0.5f, 0f, 255, GlowAlpha * fa, GlowAlpha * fb);
            Strip(pa, pb, across, px * HaloPixels * 0.5f, 0f, 0, HaloAlpha * fa, HaloAlpha * fb);
            Strip(pa, pb, across, width * 0.5f, LineSolid, 255, LineAlpha * fa, LineAlpha * fb);
            return true;
        }

        // A strip along pa..pb, half wide to each side across, in grey g: the opacity (alphas at both ends) across the middle
        // solid share of its width, falling to nothing at its sides.
        private static void Strip(Vector3 pa, Vector3 pb, Vector2 across, float half, float solid, byte g, float alphaA, float alphaB)
        {
            Vector3 off = new Vector3(across.x * half, 0f, across.y * half);
            Vector3 inner = off * solid;
            int n = verts.Count;
            verts.Add(pa - off);
            verts.Add(pa - inner);
            verts.Add(pa + inner);
            verts.Add(pa + off);
            verts.Add(pb - off);
            verts.Add(pb - inner);
            verts.Add(pb + inner);
            verts.Add(pb + off);
            byte ea = (byte)Mathf.RoundToInt(Mathf.Clamp01(alphaA) * 255f), eb = (byte)Mathf.RoundToInt(Mathf.Clamp01(alphaB) * 255f);
            colours.Add(new Color32(g, g, g, 0));
            colours.Add(new Color32(g, g, g, ea));
            colours.Add(new Color32(g, g, g, ea));
            colours.Add(new Color32(g, g, g, 0));
            colours.Add(new Color32(g, g, g, 0));
            colours.Add(new Color32(g, g, g, eb));
            colours.Add(new Color32(g, g, g, eb));
            colours.Add(new Color32(g, g, g, 0));
            // three quads across (a_k, a_k+1, b_k+1, b_k); both sides are drawn (the material culls nothing)
            for (int k = 0; k < 3; k++)
            {
                tris.Add(n + k);
                tris.Add(n + k + 1);
                tris.Add(n + 4 + k + 1);
                tris.Add(n + k);
                tris.Add(n + 4 + k + 1);
                tris.Add(n + 4 + k);
            }
        }
    }
}
