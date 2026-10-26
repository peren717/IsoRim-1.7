using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The flat roof (roof styles FlatSteel, FlatMarble, FlatGranite, SectionLayer_IsoRimRoofs.Flat): a thick slab in the
    // colour of that stuff (read from its def when built, so mods that recolour the stuff are followed), laid on the
    // wall tops over every built-roof cell (lid kind 1), with a low parapet round the edges where roofed meets unroofed.
    // Few big shapes, flat tones and dark lines along the parapet top and the slab's foot, as the rest of the 3D view.
    // The slab starts at the wall tops (its underside: things under it are clipped there, RoofClip, with no rise per
    // cell: SectionLayer_IsoRimRoofs.ClipPitch 0) and is Thickness of a wall height thick, so whatever pokes a little
    // above the walls (and is not clipped: sprites the game draws, held weapons) is hidden inside it. Walls, doors and
    // wall-set things below the wall tops are never covered.
    //
    // Built from SectionLayer_IsoRimRoofs (kinds and the cut-away as the pitched roof): "show roofs", the roof opened
    // under the pointer or over colonists (shader cut-away: every cell tagged ShaderCut.Roof; each side between two roof
    // cells gets a face, hidden inside the slab until the neighbour opens, so an opened room shows the slab's cut edge;
    // old cut-away: the opened cells are lid kind 0 and their sides get a plain face, no parapet), sky colour at night
    // (lamps under it do not light its top) and the sun on each face (_LightMode 3, as the ship deck). Rock roofs, ship
    // roofs and cells under a stacked floor are left to their own drawing, as with the pitched roof.
    [StaticConstructorOnStartup]
    public static class FlatRoofs
    {
        public const float Thickness = 0.35f;         // of a wall height, above the wall tops (0.5 tried: too blocky, and higher into a stacked floor above)
        public static float ThicknessOverride = -1f;  // harness only (probe flatroof=<fraction>)
        private const float Overhang = 0.07f;         // as the pitched roof's eaves (RoofClip's eave strip: IsoRimSolid.frag.glsl)
        private const float Below = 0.02f;            // the outer face reaches this far below the wall tops (no gap under it)
        private const float ParapetWidth = 0.16f;
        private const float ParapetHeight = 0.16f;
        private const float LineWidth = 0.035f;
        private const float Lift = 0.002f;            // lines over the faces they lie on
        private static readonly string[] StuffNames = { "Steel", "BlocksMarble", "BlocksGranite" };
        private static readonly Color Fallback = new Color(0.70f, 0.70f, 0.68f);

        // The slab's colour for the chosen flat style: the stuff's own colour (StuffProperties.color, as its walls).
        public static Color Colour
        {
            get
            {
                int i = Mathf.Clamp(SectionLayer_IsoRimRoofs.Style - SectionLayer_IsoRimRoofs.FirstFlatStyle, 0, StuffNames.Length - 1);
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(StuffNames[i]);
                return def != null && def.stuffProps != null ? def.stuffProps.color : Fallback;
            }
        }

        private static readonly IntVec3[] Side = { IntVec3.South, IntVec3.East, IntVec3.North, IntVec3.West };
        private static readonly int[] AX = { 0, 1, 1, 0 };   // side f runs from corner A to corner B, anticlockwise seen from above
        private static readonly int[] AZ = { 0, 0, 1, 1 };
        private static readonly int[] BX = { 1, 1, 0, 0 };
        private static readonly int[] BZ = { 0, 1, 1, 0 };
        private static readonly int[] edge = new int[4];      // 0 another roof cell, 1 the roof's edge (parapet), 2 an opened cell (old cut-away)

        private static Material material;

        private static Material Mat
        {
            get
            {
                if (material == null && R3DMaterials.Solid != null)
                {
                    material = R3DMaterials.Make(R3DMaterials.SolidDef, "IsoRim_RoofFlat", 3600, R3DMaterials.LightRoof);
                    IsoGlow.Use(material, IsoGlow.SkyOnly);   // sky colour only: lamps under a roof do not light its top
                }
                return material;
            }
        }

        // The slab's top over the wall tops, in true cells.
        public static float Depth
        {
            get { return View3D.WallHeight * (ThicknessOverride >= 0f ? ThicknessOverride : Thickness); }
        }

        // The slabs over the built-roof cells of one block. kindAt: the lid kinds (SectionLayer_IsoRimRoofs); opened: a
        // cell opened by the old cut-away (lid kind 0 although roofed). mark/apply: the per-cell cut-away tags.
        public static void Build(SectionLayer layer, Map map, CellRect rect, Func<int, int, int> kindAt, Func<int, int, bool> opened)
        {
            Material mat = Mat;
            LayerSubMesh sm = mat != null ? MeshWriter.Sub(layer, mat) : null;
            if (sm == null)
            {
                return;
            }
            float squash = View3D.Squash;
            float wall = View3D.WallHeight;
            float bottom = (wall - Below) / squash;
            float top = (wall + Depth) / squash;
            float rim = (wall + Depth + ParapetHeight) / squash;
            float lift = Lift / squash;
            Color colour = Colour;
            Color32 topCol = R3DMaterials.Shaded(colour, 1f);
            Color32 rimTop = R3DMaterials.Shaded(colour, 1.04f);
            Color32 rimInner = R3DMaterials.Shaded(colour, 0.8f);
            Color32 sideCol = R3DMaterials.Shaded(colour, 0.94f);
            Color32 cutCol = R3DMaterials.Shaded(colour, 0.82f);
            Color32 line = R3DMaterials.Shaded(colour, 0.3f);
            bool tagged = CutTags.Enabled;

            int cellMark = -1;
            IntVec3 markCell = IntVec3.Invalid;
            foreach (IntVec3 c in rect)
            {
                CutTags.Apply(layer, cellMark, markCell, ShaderCut.Roof);   // the cell before: hidden while its roof is open
                cellMark = -1;
                List<ShapeRoofs.Piece> cover = ShapeRoofs.CoverOf(map, c);   // (Api 34) null: no wall shapes here
                if (ShapeRoofs.KindOf(cover, kindAt(c.x, c.z)) != 1)
                {
                    continue;
                }
                if (sm.verts.Count + 256 > MeshWriter.MaxVerts)
                {
                    break;
                }
                cellMark = CutTags.Mark(layer);
                markCell = c;
                if (cover != null)
                {
                    if (kindAt(c.x, c.z) == 0 && cover.Count > 0)
                    {
                        markCell = cover[0].tag;   // a cell outside the roof: goes with the wall's
                    }
                    ShapedCell(layer, sm, map, c, cover, bottom, top, rim, lift, topCol, rimTop, rimInner, sideCol, line, squash);
                    continue;
                }
                for (int f = 0; f < 4; f++)
                {
                    int nx = c.x + Side[f].x, nz = c.z + Side[f].z;
                    edge[f] = kindAt(nx, nz) == 1 ? 0 : (opened(nx, nz) ? 2 : 1);
                }

                // The top, out over the eaves on the roof's edges (and round its outside corners).
                float x0 = c.x - (edge[3] == 1 ? Overhang : 0f);
                float x1 = c.x + 1 + (edge[1] == 1 ? Overhang : 0f);
                float z0 = c.z - (edge[0] == 1 ? Overhang : 0f);
                float z1 = c.z + 1 + (edge[2] == 1 ? Overhang : 0f);
                MeshWriter.Quad(sm, new Vector3(x0, top, z0), new Vector3(x0, top, z1), new Vector3(x1, top, z1), new Vector3(x1, top, z0), topCol);

                for (int f = 0; f < 4; f++)
                {
                    Vector3 outward = Side[f].ToVector3();
                    Vector3 a = new Vector3(c.x + AX[f], 0f, c.z + AZ[f]);
                    Vector3 b = new Vector3(c.x + BX[f], 0f, c.z + BZ[f]);
                    if (edge[f] == 0)
                    {
                        if (tagged)
                        {
                            // Inside the slab while the neighbour is shut; its cut edge once that room opens.
                            Wall(sm, a, b, bottom, top, cutCol);
                        }
                        continue;
                    }
                    if (edge[f] == 2)
                    {
                        Wall(sm, a, b, bottom, top, cutCol);
                        continue;
                    }
                    // The roof's edge: the outer face from under the wall tops to the parapet top, the parapet, and the
                    // dark lines along the parapet's outer edge and the slab's foot.
                    Vector3 ao = a + outward * Overhang + EndShift(c, f, (f + 3) % 4, kindAt);
                    Vector3 bo = b + outward * Overhang + EndShift(c, f, (f + 1) % 4, kindAt);
                    Wall(sm, ao, bo, bottom, rim, sideCol);
                    Vector3 inner = outward * -ParapetWidth;
                    Vector3 ai = ao + inner, bi = bo + inner;
                    MeshWriter.Quad(sm, new Vector3(ao.x, rim, ao.z), new Vector3(ai.x, rim, ai.z), new Vector3(bi.x, rim, bi.z), new Vector3(bo.x, rim, bo.z), rimTop);
                    Wall(sm, ai, bi, top, rim, rimInner);
                    Vector3 li = outward * -LineWidth;
                    MeshWriter.Quad(sm, new Vector3(ao.x, rim + lift, ao.z), new Vector3(ao.x + li.x, rim + lift, ao.z + li.z),
                        new Vector3(bo.x + li.x, rim + lift, bo.z + li.z), new Vector3(bo.x, rim + lift, bo.z), line);
                    Vector3 off = outward * Lift;
                    float foot = bottom + LineWidth / squash;
                    Wall(sm, ao + off, bo + off, bottom, foot, line);
                }

                // An inside corner of the roof's edge (the two cells beside this corner roofed, the one across it not): the two
                // parapets end where their outer faces meet, and a square block fills the corner between their inner faces.
                for (int f = 0; f < 4; f++)
                {
                    int g = (f + 1) % 4;
                    IntVec3 d = Side[f] + Side[g];
                    if (edge[f] != 0 || edge[g] != 0 || kindAt(c.x + d.x, c.z + d.z) == 1 || opened(c.x + d.x, c.z + d.z))
                    {
                        continue;
                    }
                    Vector3 corner = new Vector3(c.x + (d.x > 0 ? 1 : 0) + d.x * Overhang, 0f, c.z + (d.z > 0 ? 1 : 0) + d.z * Overhang);
                    Vector3 p0 = corner - new Vector3(d.x * ParapetWidth, 0f, d.z * ParapetWidth);
                    Box(sm, Mathf.Min(p0.x, corner.x), Mathf.Max(p0.x, corner.x), Mathf.Min(p0.z, corner.z), Mathf.Max(p0.z, corner.z), top, rim, rimTop, rimInner);
                }
            }
            CutTags.Apply(layer, cellMark, markCell, ShaderCut.Roof);
        }

        // Where the edge along side f ends at its corner toward side g: out round an outside corner (g is an edge too),
        // back to where the outer faces meet at an inside corner (g is another roof cell, and so is the cell across the
        // corner), else at the cell's corner.
        private static Vector3 EndShift(IntVec3 c, int f, int g, Func<int, int, int> kindAt)
        {
            if (edge[g] == 1)
            {
                return Side[g].ToVector3() * Overhang;
            }
            if (edge[g] == 0 && kindAt(c.x + Side[f].x + Side[g].x, c.z + Side[f].z + Side[g].z) == 1)
            {
                return Side[g].ToVector3() * -Overhang;
            }
            return Vector3.zero;
        }

        private static readonly List<ShapeRoofs.Edge> shapeEdges = new List<ShapeRoofs.Edge>();

        // (Api 34) A roof cell with wall shapes: the slab's top over the cell clipped to the roof's pieces there (ShapeRoofs),
        // and the outer face, parapet and lines along the outline of those pieces instead of the cell's sides.
        private static void ShapedCell(SectionLayer layer, LayerSubMesh sm, Map map, IntVec3 c, List<ShapeRoofs.Piece> cover, float bottom, float top,
            float rim, float lift, Color32 topCol, Color32 rimTop, Color32 rimInner, Color32 sideCol, Color32 line, float squash)
        {
            int[] mark = ShapeRoofs.MarkTris(layer);
            MeshWriter.Quad(sm, new Vector3(c.x, top, c.z), new Vector3(c.x, top, c.z + 1), new Vector3(c.x + 1, top, c.z + 1), new Vector3(c.x + 1, top, c.z), topCol);
            ShapeRoofs.ClipSince(layer, mark, cover, 1);
            ShapeRoofs.Outline(map, c, 1, shapeEdges);
            foreach (ShapeRoofs.Edge e in shapeEdges)
            {
                Vector2 ao2 = e.a + e.offA * Overhang, bo2 = e.b + e.offB * Overhang;
                Vector2 ai2 = e.a + e.offA * (Overhang - ParapetWidth), bi2 = e.b + e.offB * (Overhang - ParapetWidth);
                Vector2 al2 = e.a + e.offA * (Overhang - LineWidth), bl2 = e.b + e.offB * (Overhang - LineWidth);
                Vector3 ao = new Vector3(ao2.x, 0f, ao2.y), bo = new Vector3(bo2.x, 0f, bo2.y);
                Vector3 ai = new Vector3(ai2.x, 0f, ai2.y), bi = new Vector3(bi2.x, 0f, bi2.y);
                // the top out to the overhang, under the parapet
                MeshWriter.Quad(sm, new Vector3(e.a.x, top, e.a.y), ao + Vector3.up * top, bo + Vector3.up * top, new Vector3(e.b.x, top, e.b.y), topCol);
                Wall(sm, ao, bo, bottom, rim, sideCol);
                MeshWriter.Quad(sm, new Vector3(ao.x, rim, ao.z), new Vector3(ai.x, rim, ai.z), new Vector3(bi.x, rim, bi.z), new Vector3(bo.x, rim, bo.z), rimTop);
                Wall(sm, ai, bi, top, rim, rimInner);
                MeshWriter.Quad(sm, new Vector3(ao.x, rim + lift, ao.z), new Vector3(al2.x, rim + lift, al2.y),
                    new Vector3(bl2.x, rim + lift, bl2.y), new Vector3(bo.x, rim + lift, bo.z), line);
                Vector3 off = new Vector3(e.o.x, 0f, e.o.y) * Lift;
                float foot = bottom + LineWidth / squash;
                Wall(sm, ao + off, bo + off, bottom, foot, line);
            }
        }

        // An upright face from a to b (cells, y ignored), from y0 to y1 (drawn heights).
        private static void Wall(LayerSubMesh sm, Vector3 a, Vector3 b, float y0, float y1, Color32 col)
        {
            MeshWriter.Quad(sm, new Vector3(a.x, y0, a.z), new Vector3(a.x, y1, a.z), new Vector3(b.x, y1, b.z), new Vector3(b.x, y0, b.z), col);
        }

        // A box with its top and four sides (drawn heights).
        private static void Box(LayerSubMesh sm, float x0, float x1, float z0, float z1, float y0, float y1, Color32 topCol, Color32 sideCol)
        {
            MeshWriter.Quad(sm, new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), topCol);
            Wall(sm, new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), y0, y1, sideCol);
            Wall(sm, new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1), y0, y1, sideCol);
            Wall(sm, new Vector3(x1, 0f, z1), new Vector3(x0, 0f, z1), y0, y1, sideCol);
            Wall(sm, new Vector3(x0, 0f, z1), new Vector3(x0, 0f, z0), y0, y1, sideCol);
        }
    }
}
