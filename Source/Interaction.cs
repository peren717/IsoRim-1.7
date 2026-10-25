using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Making the 3D view work with the pointer the way the flat one does:
    //   - a designation drag (zones, orders, building) is outlined on the ground, cell by cell
    //     (the selection drag box stays the game's own rectangle on screen; Patches selects what
    //     shows inside it);
    //   - selected things are marked by brackets on the ground round their footprint;
    //   - clicking anywhere on a tall thing (a wall face, a tree's crown, a colonist's head)
    //     picks that thing, not whatever is on the ground behind it;
    //   - dragging out a line of walls or furniture shows a 3D ghost in every cell;
    //   - the fog over unexplored ground lies on the rock tops and the ground, not in the air.
    [StaticConstructorOnStartup]
    public static class Interaction
    {
        private static Material overlay;
        private static Mesh quad;
        private static MaterialPropertyBlock block;
        private static int colorId;

        // Unlit, see-through, drawn over everything so marks on the ground show through walls.
        private static Material Overlay
        {
            get
            {
                if (overlay == null && R3DMaterials.Solid != null)
                {
                    overlay = R3DMaterials.Make(R3DMaterials.OverlayDef, "IsoRim_Overlay", 3650);
                }
                return overlay;
            }
        }

        // A flat square one unit across on the ground, centred on the origin.
        private static Mesh Quad
        {
            get
            {
                if (quad == null)
                {
                    quad = new Mesh();
                    quad.SetVertices(new List<Vector3>
                    {
                        new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f)
                    });
                    Color32 w = new Color32(255, 255, 255, 255);
                    quad.SetColors(new List<Color32> { w, w, w, w });
                    quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 }, 0);
                    quad.RecalculateBounds();
                    MeshGuard.Check(quad, "interaction quad");
                }
                return quad;
            }
        }

        // A flat rectangle on the ground (x0..x1 by z0..z1, in cells) in a colour.
        private static void Rect(float x0, float z0, float x1, float z1, Color col)
        {
            Material mat = Overlay;
            if (mat == null || x1 <= x0 || z1 <= z0)
            {
                return;
            }
            if (block == null)
            {
                block = new MaterialPropertyBlock();
                colorId = Shader.PropertyToID("_Color");
            }
            block.SetColor(colorId, col);
            float y = 0.015f / View3D.Squash;
            Matrix4x4 m = Matrix4x4.TRS(new Vector3((x0 + x1) * 0.5f, y, (z0 + z1) * 0.5f), Quaternion.identity, new Vector3(x1 - x0, 1f, z1 - z0));
            Graphics.DrawMesh(Quad, rectLift * m, mat, 0, null, 0, block);
        }

        private static Matrix4x4 rectLift = Matrix4x4.identity;   // Rect raised off the ground (a block designator's drag)

        // How thick lines on the ground are, so they stay about two pixels wide at any zoom.
        private static float LineWidth
        {
            get
            {
                Camera cam = Find.Camera;
                float size = cam != null ? cam.orthographicSize : 20f;
                return Mathf.Clamp(size * 2.4f / Mathf.Max(200f, Screen.height), 0.025f, 0.4f);
            }
        }

        // ---- the drag box

        // The area of a designation drag (zones, orders, building), outlined on the ground (a block designator's: on
        // its drag's plane, with the bracket and the highlights).
        public static void DrawDesignationDrag()
        {
            float top = DragTop;
            rectLift = top >= 0f ? View3D.LiftMatrix(top + 0.02f) : Matrix4x4.identity;
            try
            {
                DrawDragOutline();
            }
            finally
            {
                rectLift = Matrix4x4.identity;
            }
        }

        private static void DrawDragOutline()
        {
            DesignatorManager dm = Find.DesignatorManager;
            DesignationDragger dragger = dm != null ? dm.Dragger : null;
            if (dragger == null || !dragger.Dragging)
            {
                return;
            }
            // As vanilla: the drag's whole shape (valid cells or not), only for styles that draw an outline.
            DrawStyleDef style = dm.SelectedStyle;
            List<IntVec3> cells = dragger.CellBuffer;
            if (style == null || !style.drawOutline || cells == null || cells.Count == 0)
            {
                return;
            }
            int x0 = int.MaxValue;
            int z0 = int.MaxValue;
            int x1 = int.MinValue;
            int z1 = int.MinValue;
            for (int i = 0; i < cells.Count; i++)
            {
                x0 = Mathf.Min(x0, cells[i].x);
                z0 = Mathf.Min(z0, cells[i].z);
                x1 = Mathf.Max(x1, cells[i].x);
                z1 = Mathf.Max(z1, cells[i].z);
            }
            if (x1 == x0 && z1 == z0)
            {
                return;
            }
            // Vanilla's box green, lighter to read on grass, with a faint fill.
            float w = LineWidth * 1.5f;
            Color line = new Color(0.62f, 0.82f, 0.45f, 0.95f);
            Rect(x0, z0, x1 + 1f, z1 + 1f, new Color(0.62f, 0.82f, 0.45f, 0.12f));
            float ax = x0;
            float az = z0;
            float bx = x1 + 1f;
            float bz = z1 + 1f;
            Rect(ax - w, az - w, bx + w, az, line);
            Rect(ax - w, bz, bx + w, bz + w, line);
            Rect(ax - w, az, ax, bz, line);
            Rect(bx, az, bx + w, bz, line);
        }

        // ---- selection brackets

        // Corner brackets on the ground round what is selected.
        public static void Bracket(Thing t)
        {
            float x0;
            float z0;
            float x1;
            float z1;
            if (t is Pawn)
            {
                Vector3 p = t.DrawPos;
                x0 = p.x - 0.5f;
                x1 = p.x + 0.5f;
                z0 = p.z - 0.5f;
                z1 = p.z + 0.5f;
            }
            else
            {
                CellRect r = t.OccupiedRect();
                x0 = r.minX;
                z0 = r.minZ;
                x1 = r.maxX + 1f;
                z1 = r.maxZ + 1f;
            }
            float pad = 0.06f;
            x0 -= pad;
            z0 -= pad;
            x1 += pad;
            z1 += pad;
            float w = LineWidth * 1.3f;
            float len = Mathf.Min(0.32f, Mathf.Min(x1 - x0, z1 - z0) * 0.3f);
            Color col = new Color(1f, 1f, 1f, 0.95f);
            // each corner: one stroke along x, one along z
            Rect(x0, z0, x0 + len, z0 + w, col);
            Rect(x0, z0 + w, x0 + w, z0 + len, col);
            Rect(x1 - len, z0, x1, z0 + w, col);
            Rect(x1 - w, z0 + w, x1, z0 + len, col);
            Rect(x0, z1 - w, x0 + len, z1, col);
            Rect(x0, z1 - len, x0 + w, z1 - w, col);
            Rect(x1 - len, z1 - w, x1, z1, col);
            Rect(x1 - w, z1 - len, x1, z1 - w, col);
        }

        // ---- picking what is under the pointer

        // Things whose 3D shape the pointer is over, nearest the camera first.
        public static List<Thing> PickUnderPointer(Map map)
        {
            List<Thing> found = new List<Thing>();
            if (map == null)
            {
                return found;
            }
            Vector2 mouse = Input.mousePosition;
            const float Top = 3.2f;
            const float Step = 0.08f;
            // On stacked floors (Floors) every floor the pointer works on, from the viewed one down to the one its ray lands on.
            int lowest = Floors.PointerFloorAt(mouse);
            for (int k = Floors.PointerTop; k >= lowest; k--)
            {
                float floorH;
                int floorZ;
                Floors.FloorPlane(k, out floorH, out floorZ);
                for (float h = Top; h >= 0.1f; h -= Step)
                {
                    Vector3 p = View3D.ScreenToHeight(mouse, h + floorH);
                    IntVec3 c = new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.z) + floorZ);
                    if (!c.InBounds(map) || Floors.On && Floors.FloorOfRow(c.z) != k)
                    {
                        continue;
                    }
                    List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        if (found.Contains(t))
                        {
                            continue;
                        }
                        float top = PickHeight(t);
                        if (top >= h && h >= PickBottom(t))
                        {
                            found.Add(t);
                        }
                    }
                }
            }
            return found;
        }

        // The bottom of what the pointer can pick of a thing: 0, but a pawn attached to a host (Api 13: a rider) starts at
        // its place on the host, so the host's own body under it picks the host.
        internal static float PickBottom(Thing t)
        {
            Pawn pawn = t as Pawn;
            float bottom;
            return pawn != null && Attachments.Any && Poses.AttachBottom(pawn, out bottom) ? bottom : 0f;
        }

        internal static float PickHeight(Thing t)
        {
            Pawn pawn = t as Pawn;
            if (pawn != null)
            {
                if (pawn.RaceProps != null && pawn.RaceProps.Humanlike)
                {
                    if (pawn.Downed || pawn.Dead)
                    {
                        return 0.35f;
                    }
                    float headRise;
                    Poses.DrawnSpot(pawn, out headRise);   // sitting: on the seat, the head a little lower
                    return 1.25f + headRise;
                }
                float onHost;
                Poses.AttachRise(pawn, out onHost);   // attached to a host (Api 13): on its anchor
                return Mathf.Clamp(0.35f + 0.45f * pawn.BodySize, 0.3f, 2.4f) + onHost + Hover.Current(pawn);   // (Api 32) hovering
            }
            if (t.def == null)
            {
                return 0f;
            }
            if (t.Spawned && t.def.category == ThingCategory.Building && WallMount.Is(t.def))
            {
                return WallMount.TopOf(t);   // up on its wall
            }
            ThingForm f = Forms.For(t.def);
            if (t is Plant && f.form == Form3D.Model && f.model != null && f.model.HasModel)
            {
                float grown = ((Plant)t).Growth;
                return f.model.variants[0].maxY * f.model.def.scale * Mathf.Lerp(0.4f, 1f, grown);
            }
            if (f.form == Form3D.Model && f.model != null && f.model.HasModel)
            {
                // A turning part drawn over the body (the lightball's ball) may stand taller than it.
                float top = f.model.spin != null ? Mathf.Max(f.model.variants[0].maxY, f.model.spin.maxY) : f.model.variants[0].maxY;
                float s = f.model.def.scale * f.model.Fit(t.def).y;
                float h = top * s;
                if (f.model.bob != null)
                {
                    // a bobbing part (the grav engine's orb) at the top of its rise, as Bobbers draws it
                    h = Mathf.Max(h, f.model.bob.maxY * s + f.model.def.offset.y + f.model.def.bobHeight);
                }
                // the whole drawn model: a turret's gun on its pivot (topModel) and a part over the roof (roofModel), so icons
                // stand above the gun and the pointer picks a turret by its gun too
                h = Mathf.Max(h, f.model.DrawnHeight(t.def));
                return Mathf.Max(Forms.HeightOf(t), h);
            }
            return Forms.HeightOf(t);
        }

        // ---- designators that work on solid blocks

        // Mining, deconstructing and smoothing walls point at a block: in 3D their cell is the block
        // (wall, rock, building) the pointer shows, not the ground behind its top. Everything else
        // (zones, areas, building, orders on the ground, the cell readout) keeps the ground cell.
        public static bool TargetsBlocks(Designator d)
        {
            return d is Designator_Mine || d is Designator_Deconstruct || d is Designator_SmoothWalls;
        }

        private static int snapFrame = -1;
        private static Vector2 snapMouse;
        private static IntVec3 snapCell;
        private static float snapTop;

        // The height of the block DesignatorMouseCell picked under the pointer (0: the floor).
        internal static float MouseTop
        {
            get
            {
                DesignatorMouseCell();
                return snapTop;
            }
        }

        // Stands in for UI.MouseCell() in the designator code (DesignatorManager, DesignationDragger).
        public static IntVec3 DesignatorMouseCell()
        {
            IntVec3 ground = Verse.UI.MouseCell();
            DesignatorManager dm = Find.DesignatorManager;
            Map map = Find.CurrentMap;
            if (!View3D.Ready || dm == null || map == null || !TargetsBlocks(dm.SelectedDesignator))
            {
                return ground;
            }
            Vector2 mouse = Input.mousePosition;
            bool dragging = dm.Dragger != null && dm.Dragger.Dragging;
            if (!dragging)
            {
                dragLocked = false;
            }
            if (snapFrame == Time.frameCount && snapMouse == mouse && snapLocked == dragLocked)
            {
                LockDrag(map, dragging);
                return snapCell;
            }
            snapFrame = Time.frameCount;
            snapMouse = mouse;
            IntVec3 hit;
            if (dragLocked)
            {
                // a drag: the far corner on the plane of the corner it started from, so a drag straight across
                // the screen keeps to one row or one column whatever the blocks under the pointer
                // (moved by the start's own offset: a click on a wall or rock face hits the face below the top
                // plane, and the same screen point on that plane lies a cell away; the start cell stays the start)
                hit = OnDragPlane(mouse) + dragOffset;
                snapTop = dragTop;
            }
            else if (!PickBlockCell(map, mouse, out hit, out snapTop))
            {
                hit = ground;
                snapTop = 0f;
            }
            snapCell = hit;
            snapLocked = dragLocked;
            LockDrag(map, dragging);
            return hit;
        }

        // A drag starting (DesignationDragger.StartDrag): keep the start corner's plane until it ends.
        private static void LockDrag(Map map, bool dragging)
        {
            if (dragging && !dragLocked)
            {
                dragLocked = true;
                dragTop = snapTop;
                dragFloor = Floors.On && snapCell.InBounds(map) ? Floors.FloorOfRow(snapCell.z) : 0;
                dragOffset = snapCell - OnDragPlane(snapMouse);
            }
        }

        // The cell under a screen point on the drag's plane.
        private static IntVec3 OnDragPlane(Vector2 mouse)
        {
            Floors.FloorPlane(dragFloor, out float floorH, out int floorZ);
            Vector3 p = View3D.ScreenToHeight(mouse, dragTop + floorH);
            return new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.z) + floorZ);
        }

        private static IntVec3 dragOffset;  // start cell minus the start point's cell on the drag's plane

        private static bool dragLocked;     // a block designator's drag is on, its plane kept (dragTop, dragFloor)
        private static bool snapLocked;     // snapCell was picked on the drag's plane
        private static float dragTop;
        private static int dragFloor;

        // The height a block designator's drag is drawn at (-1: no such drag).
        internal static float DragTop
        {
            get
            {
                DesignatorManager dm = Find.DesignatorManager;
                if (!dragLocked || !View3D.Ready || dm == null || dm.Dragger == null || !dm.Dragger.Dragging
                    || !TargetsBlocks(dm.SelectedDesignator))
                {
                    return -1f;
                }
                return dragTop;
            }
        }

        // The game's drag highlights (DesignatorUtility) for a block designator, on the drag's plane like the bracket
        // instead of on the ground. False: the game draws its own.
        public static bool DrawDragHighlight(Designator designator, List<IntVec3> dragCells, bool things)
        {
            float top = DragTop;
            if (top < 0f || designator == null || designator.Map == null || dragCells == null)
            {
                return false;
            }
            Matrix4x4 lift = View3D.LiftMatrix(top + 0.02f);
            float y = AltitudeLayer.MetaOverlays.AltitudeFor();
            if (!things)
            {
                for (int i = 0; i < dragCells.Count; i++)
                {
                    Vector3 pos = dragCells[i].ToVector3Shifted();
                    pos.y = y;
                    Graphics.DrawMesh(MeshPool.plane10, lift * Matrix4x4.Translate(pos), DesignatorUtility.DragHighlightCellMat, 0);
                }
                return true;
            }
            highlighted.Clear();
            for (int i = 0; i < dragCells.Count; i++)
            {
                List<Thing> list = dragCells[i].GetThingList(designator.Map);
                for (int j = 0; j < list.Count; j++)
                {
                    Thing t = list[j];
                    if (!highlighted.Contains(t) && designator.CanDesignateThing(t).Accepted)
                    {
                        highlighted.Add(t);
                        Vector3 pos = t.DrawPos;
                        pos.y = y;
                        Graphics.DrawMesh(MeshPool.plane10, lift * Matrix4x4.Translate(pos), DesignatorUtility.DragHighlightThingMat, 0);
                    }
                }
            }
            return true;
        }

        private static readonly HashSet<Thing> highlighted = new HashSet<Thing>();

        // The cell of the first building the line of sight through a screen point meets, nearest the
        // camera first (walls cut down near the pointer count at their lowered height).
        public static bool PickBlockCell(Map map, Vector2 mouse, out IntVec3 cell, out float top)
        {
            const float Top = 3.2f;
            const float Step = 0.08f;
            int lowest = Floors.PointerFloorAt(mouse);   // (stacked floors) the viewed floor down to where the ray lands
            for (int k = Floors.PointerTop; k >= lowest; k--)
            {
                float floorH;
                int floorZ;
                Floors.FloorPlane(k, out floorH, out floorZ);
                for (float h = Top; h >= 0.1f; h -= Step)
                {
                    Vector3 p = View3D.ScreenToHeight(mouse, h + floorH);
                    IntVec3 c = new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.z) + floorZ);
                    if (!c.InBounds(map) || Floors.On && Floors.FloorOfRow(c.z) != k)
                    {
                        continue;
                    }
                    List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                    for (int i = 0; i < things.Count; i++)
                    {
                        if (things[i] is Building)
                        {
                            float t = PickHeight(things[i]);
                            if (t >= h)
                            {
                                cell = c;
                                top = t;
                                return true;
                            }
                        }
                    }
                }
            }
            cell = IntVec3.Invalid;
            top = 0f;
            return false;
        }

        private static Material bracketMat;

        // The mouse-over bracket of a block designator, on top of the block it points at
        // (GenUI.RenderMouseoverBracket; false = the game draws its own).
        public static bool DrawBlockBracket()
        {
            DesignatorManager dm = Find.DesignatorManager;
            if (!View3D.Ready || dm == null)
            {
                return false;
            }
            IntVec3 c;
            if (TargetsBlocks(dm.SelectedDesignator))
            {
                c = DesignatorMouseCell();
            }
            else if (Floors.On && Floors.FloorOfRow(Verse.UI.MouseCell().z) > 0)
            {
                // Any designator on an upper floor (Floors): the game's bracket on that floor.
                c = Verse.UI.MouseCell();
                snapTop = 0f;
            }
            else
            {
                return false;
            }
            if (bracketMat == null)
            {
                bracketMat = MaterialPool.MatFrom("UI/Overlays/MouseoverBracketTex", ShaderDatabase.MetaOverlay);
            }
            Vector3 pos = c.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
            Graphics.DrawMesh(MeshPool.plane10, View3D.LiftMatrix(snapTop + 0.02f) * Matrix4x4.Translate(pos), bracketMat, 0);
            return true;
        }

        // ---- ghosts along a drag

        private static PropertyInfo dragCells;
        private static FieldInfo placingRot;
        private static bool lookedUp;

        public static void DrawDragGhosts()
        {
            DesignatorManager dm = Find.DesignatorManager;
            Designator_Place place = dm != null ? dm.SelectedDesignator as Designator_Place : null;
            if (place == null || !IsoRimMod.Settings.ghosts3D)
            {
                return;
            }
            DesignationDragger dragger = dm.Dragger;
            if (dragger == null || !dragger.Dragging)
            {
                return;
            }
            if (!lookedUp)
            {
                lookedUp = true;
                dragCells = AccessTools.Property(typeof(DesignationDragger), "DragCells");
                placingRot = AccessTools.Field(typeof(Designator_Place), "placingRot");
            }
            List<IntVec3> cells = dragCells != null ? dragCells.GetValue(dragger, null) as List<IntVec3> : null;
            ThingDef def = place.PlacingDef as ThingDef;
            if (cells == null || def == null)
            {
                return;
            }
            Rot4 rot = placingRot != null ? (Rot4)placingRot.GetValue(place) : Rot4.North;
            IntVec3 mouse = UI.MouseCell();
            Color col = new Color(0.5f, 1f, 0.6f, 0.4f);
            int n = Mathf.Min(cells.Count, 400);
            for (int i = 0; i < n; i++)
            {
                if (cells[i] != mouse)
                {
                    Ghosts.DrawPreview(def, cells[i], rot, col, place.StuffDef, place.ThingStyleDefForPreview);
                }
            }
        }
    }
}
