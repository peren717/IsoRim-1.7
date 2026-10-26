using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Stacked floors on one map, for gameplay addons (IsoRim ProjectZ). IsoRim stays visual: the addon says where its
    // floors are, IsoRim draws them.
    //
    // A layout puts floor k's cells in rows of the same map north of the ground: cell (x, z + zShift_k) is the cell
    // above ground cell (x, z). IsoRim draws everything on floor k moved down by zShift_k rows and raised by the floor's
    // height, so it stands over the ground cell below it: map blocks (sections, which line up 1:1 with the ground's
    // when zShift is a multiple of 17) with one matrix each, things while they are drawn, and IsoRim's own marks
    // (selection brackets, drag outlines, ghosts) by the row they are in. Floors above the one the player works on
    // can be hidden; the pointer works on one floor at a time.
    public interface IFloorLayout
    {
        // Ground rows of the map, or 0 when the map has no floors (then IsoRim draws it as usual).
        int GroundRows(Map map);

        // Floors above the ground (1 = one upper floor).
        int FloorCount(Map map);

        // Floor k (1..FloorCount): its height above the ground in cells, how many rows north of the ground its cells
        // are, and whether it is drawn now.
        bool Floor(Map map, int k, out float height, out int zShift);

        // The floor the pointer works on (0 = the ground).
        int PointerFloor(Map map);

        // An extra true-space offset for one moving thing on top of its cell's (a pawn on a stair). Zero for most.
        Vector3 ThingOffset(Thing t);

        // Views floor k (0 = the ground; the floors above it are hidden, the pointer works on it) and returns the floor
        // viewed before; -1 when the layout can't (nothing changed). The gravship cutscene shows every floor of a ship
        // with upper decks while it runs and puts the player's view back after.
        int ViewFloor(Map map, int k)
        {
            return -1;
        }

        // A cell of a gravship's upper floor that has no Substructure but belongs to the ship (an opening in a deck,
        // such as a stairwell): its built roof is drawn as the ship's roof. False for most.
        bool ShipCell(Map map, IntVec3 c)
        {
            return false;
        }
    }

    // Hooks for gameplay addons. Everything is off (null) by default and then costs nothing.
    public static partial class Api
    {
        private static IFloorLayout floors;

        public static IFloorLayout Floors
        {
            get { return floors; }
            set
            {
                floors = value;
                IsoRim.Floors.Registered();
            }
        }

        // Roofs the addon keeps for the game's rules but that are not drawn (ProjectZ's stairwell: the room under it
        // stays indoors, the opening above stays open). Null: every roof is drawn.
        public static Predicate<RoofDef> HiddenRoof;

        public static bool RoofHidden(RoofDef roof)
        {
            return roof != null && HiddenRoof != null && HiddenRoof(roof);
        }

        // How tall a thing of this kind is drawn from its model, in cells (ModelEntry.DrawnHeight: the tallest model
        // times scale and footprint fit); 0 for things that follow the wall height as walls do: walls, doors, automatic
        // shapes, and models whose height follows the walls (ModelEntry.WallFit: columns, gravship thrusters, fleshmass),
        // which only ever reach the top of the walls.
        public static float ModelHeight(ThingDef def)
        {
            ModelEntry entry;
            if (def == null || !ModelLibrary.TryGet(def, out entry) || entry.def.flat || !entry.HasModel || entry.WallFit(def) > 0f)
            {
                return 0f;
            }
            if (def.size.x == 1 && def.size.z == 1 && typeof(RimWorld.Building_Door).IsAssignableFrom(def.thingClass)
                && !typeof(RimWorld.Building_SupportedDoor).IsAssignableFrom(def.thingClass))
            {
                return 0f;   // a one-cell door with its own models (Api 18, the animal flap) is drawn as tall as the walls
            }
            return entry.DrawnHeight(def);
        }
    }

    public static class Floors
    {
        public const int MaxFloors = 13;   // ProjectZ allows up to 13 storeys

        // True while the current map has floors and the 3D view draws (checked first by every hook).
        public static bool On;

        private static Map frameMap;
        private static int frameNo = -1;
        private static int groundRows;
        private static int count;
        private static int pointer;
        private static readonly float[] heights = new float[MaxFloors + 1];
        private static readonly int[] shifts = new int[MaxFloors + 1];
        private static readonly bool[] shown = new bool[MaxFloors + 1];
        private static readonly int[] fade = new int[MaxFloors + 1];   // FadeFloors slot of a shown floor (0: drawn solid)

        // FadeFloors: the slot of the faded floor being drawn now (0: none), set while one of its blocks or things is drawn.
        public static int FadeSlot;
        private static readonly Matrix4x4[] matrices = new Matrix4x4[MaxFloors + 1];

        private static bool patched;

        internal static void Registered()
        {
            if (Api.Floors != null && Patches.Ready)
            {
                ApplyPatches();
            }
        }

        // From Patches.ApplyAll, and later if a layout registers after that.
        internal static void ApplyPatches()
        {
            if (patched || Api.Floors == null)
            {
                return;
            }
            patched = true;
            Harmony h = new Harmony("isorim.floors");
            try
            {
                h.Patch(AccessTools.Method(typeof(Section), nameof(Section.DrawSection)),
                    prefix: new HarmonyMethod(typeof(Floors), nameof(DrawSection_Prefix)), finalizer: new HarmonyMethod(typeof(Floors), nameof(Section_Finalizer)));
                h.Patch(AccessTools.Method(typeof(Section), nameof(Section.DrawDynamicSections)),
                    prefix: new HarmonyMethod(typeof(Floors), nameof(DrawDynamicSections_Prefix)), finalizer: new HarmonyMethod(typeof(Floors), nameof(Section_Finalizer)));
                h.Patch(AccessTools.Method(typeof(SectionLayer_Terrain), nameof(SectionLayer_Terrain.Regenerate)),
                    postfix: new HarmonyMethod(typeof(Floors), nameof(TerrainRegenerate_Postfix)));
                if (SunShadowsType != null)
                {
                    h.Patch(AccessTools.DeclaredMethod(SunShadowsType, "ShouldDrawDynamic"),
                        prefix: new HarmonyMethod(typeof(Floors), nameof(SunShadowsShouldDraw_Prefix)));
                }
                PatchDrawing(h);
                ModLog.Note("[IsoRim] Floor layout registered: stacked floors are drawn.");
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] Could not hook the drawing of stacked floors: " + e);
            }
            try
            {
                FloorSounds.Patch(h);
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] Could not hook the sounds of stacked floors: " + e);
            }
        }

        // Works out this frame's floors for the current map (once a frame; cheap when the map has none).
        public static void BeginFrame()
        {
            IFloorLayout layout = Api.Floors;
            Map map = Find.CurrentMap;
            On = false;
            if (layout == null || map == null || !View3D.Ready)
            {
                return;
            }
            // Only the world view proper (Planet mode). WorldRendered is also true on maps whose generator renders the
            // planet behind them (Odyssey space maps: Background mode), and those maps have floors like any other.
            if (!RimWorld.Planet.WorldRendererUtility.DrawingMap)
            {
                return;   // the world map is drawn: no map rows to move
            }
            Refresh(layout, map);
            On = count > 0;
            int sig = On ? groundRows * 31 + pointer : -1;
            for (int k = 1; k <= count; k++)
            {
                sig = sig * 31 + (shown[k] ? 1 : 0) * 17 + fade[k] * 7 + Mathf.RoundToInt(heights[k] * 100f);
            }
            if (sig != viewSig)
            {
                viewSig = sig;
                ViewChanged();
            }
        }

        private static int viewSig = int.MinValue;

        private static void Refresh(IFloorLayout layout, Map map)
        {
            if (frameNo == Time.frameCount && ReferenceEquals(frameMap, map))
            {
                return;
            }
            frameNo = Time.frameCount;
            frameMap = map;
            count = 0;
            try
            {
                groundRows = layout.GroundRows(map);
                if (groundRows <= 0)
                {
                    return;
                }
                int n = Mathf.Min(layout.FloorCount(map), MaxFloors);
                float s = View3D.Squash;
                matrices[0] = Matrix4x4.identity;
                shown[0] = true;
                for (int k = 1; k <= n; k++)
                {
                    float h;
                    int z;
                    shown[k] = layout.Floor(map, k, out h, out z);
                    heights[k] = h;
                    shifts[k] = z;
                    matrices[k] = Matrix4x4.Translate(new Vector3(0f, h / s, -z));
                }
                pointer = Mathf.Clamp(layout.PointerFloor(map), 0, n);
                // Faded floors (FadeFloors): while an addon peeks (Api.PeekUpperFloors), the hidden floors right above the viewed one are drawn, faded.
                bool fadeOn = FadeFloors.Enabled;
                for (int k = 0; k <= n; k++)
                {
                    fade[k] = 0;
                    int up = k - pointer;
                    if (fadeOn && k > 0 && !shown[k] && up >= 1 && up <= FadeFloors.Keep.Length)
                    {
                        shown[k] = true;
                        fade[k] = up;
                    }
                }
                count = n;
            }
            catch (Exception e)
            {
                count = 0;
                Log.ErrorOnce("[IsoRim] The floor layout failed: " + e, 93611401);
            }
        }

        // The floor of a row: 0 ground, k, or -1 for rows between floors.
        public static int FloorOfRow(int z)
        {
            if (z < groundRows)
            {
                return 0;
            }
            for (int k = 1; k <= count; k++)
            {
                if (z >= shifts[k] && z < shifts[k] + groundRows)
                {
                    return k;
                }
            }
            return -1;
        }

        // Drawn-space matrix for what stands in a row; false when that floor is hidden now (or the row is between floors).
        public static bool RowMatrix(int z, out Matrix4x4 m)
        {
            int k = FloorOfRow(z);
            if (k <= 0)
            {
                m = Matrix4x4.identity;
                return k == 0;
            }
            m = matrices[k];
            return shown[k];
        }

        // True-space offset of a row (height in y), zero on the ground.
        public static Vector3 RowOffset(int z)
        {
            int k = FloorOfRow(z);
            return k > 0 ? new Vector3(0f, heights[k], -shifts[k]) : Vector3.zero;
        }

        public static float RowHeight(int z)
        {
            int k = FloorOfRow(z);
            return k > 0 ? heights[k] : 0f;
        }

        // The FadeFloors slot floor k is drawn with this frame (0: solid, hidden or no such floor).
        internal static int FadeOfFloor(int k)
        {
            return On && k > 0 && k <= count ? fade[k] : 0;
        }

        // The light lookup of the shaders (IsoRimSolid.frag.glsl, _IsoFloorGlow): (floors above the ground, storey height,
        // rows from one floor to the next, 1 + the highest floor drawn solid (the slab clip, RoofClip)); also read by the
        // roof clip (IsoRim.vert.glsl, IsoRimTex.vert.glsl, IsoRimCard*.frag.glsl), so it is handed over with the lighting
        // fix off too (IsoGlow.BeginFrame). A face drawn at or above floor k's height reads the glow of the cell k floors
        // north of where it is drawn, i.e. the floor's own cell: its own sky, roof and lamps, not the ground's under it. Zero
        // without floors (the ground's cells, as before). Floors must be evenly stacked (the layout's are).
        public static Vector4 GlowFloors()
        {
            if (!On || count <= 0 || heights[1] <= 0f || shifts[1] <= 0)
            {
                return Vector4.zero;
            }
            for (int k = 2; k <= count; k++)
            {
                if (Mathf.Abs(heights[k] - k * heights[1]) > 0.001f || shifts[k] != k * shifts[1])
                {
                    Log.ErrorOnce("[IsoRim] Floors are not evenly stacked: only floor 1 to " + (k - 1) + " take their own light.", 93611403);
                    return new Vector4(k - 1, heights[1], shifts[1], SolidTop(k - 1) + 1f);
                }
            }
            return new Vector4(count, heights[1], shifts[1], SolidTop(count) + 1f);
        }

        // (_IsoFloorGlow.w - 1) The highest floor up to top drawn solid (0: the ground only). A floor's slab clips what
        // stands under it only while that floor is drawn solid (RoofClip, lid kind 4).
        private static int SolidTop(int top)
        {
            for (int k = top; k >= 1; k--)
            {
                if (ShownSolid(k))
                {
                    return k;
                }
            }
            return 0;
        }

        // ---- THE drawing map (design "M1c-2 coordinates"): a point or matrix in the game's world space, whose z may be a
        // row of an upper floor, to where it is drawn: moved down by that floor's rows and up by its height. Drawn
        // space only ever holds ground rows (z < groundRows), so mapping twice changes nothing: every draw can go
        // through it, at the bottom (Graphics, see PatchDrawing), whatever drew it before. False: the row is between
        // floors or on a hidden floor (nothing is drawn there).
        public static bool Drawn(ref Matrix4x4 m)
        {
            if (!On || m.m23 < groundRows)
            {
                return true;
            }
            int k = FloorOfRow(Mathf.FloorToInt(m.m23));
            if (k <= 0 || !shown[k])
            {
                return false;
            }
            m = matrices[k] * m;
            return true;
        }

        public static bool Drawn(ref Vector3 p)
        {
            if (!On || p.z < groundRows)
            {
                return true;
            }
            int k = FloorOfRow(Mathf.FloorToInt(p.z));
            if (k <= 0 || !shown[k])
            {
                return false;
            }
            p = new Vector3(p.x, p.y + heights[k] / View3D.Squash, p.z - shifts[k]);
            return true;
        }

        // The same as a value, for IsoRim's own 3D drawing of a thing (figures, models, solid sprites; culling and lighting
        // lookups test it too): the matrix where it shows (unchanged when hidden), with the thing's extra offset from the
        // layout (a pawn on a stair step, ThingOffset) while that thing is drawn. The game's own drawing gets that offset
        // in DrawHook; these draws do not go through DrawHook.
        public static Matrix4x4 DrawnMatrix(Matrix4x4 m)
        {
            if (DrawHook.floor)
            {
                m = DrawHook.floorMatrix * m;
            }
            Drawn(ref m);
            return m;
        }

        // The same, and the floor the thing stands on: the floor of the row its matrix is in, before the stair offset and
        // the move onto its floor (0 on the ground, between floors and without floors). For RoofClip.FloorBlock.
        public static Matrix4x4 DrawnMatrix(Matrix4x4 m, out int floor)
        {
            floor = On && m.m23 >= groundRows ? Mathf.Max(0, FloorOfRow(Mathf.FloorToInt(m.m23))) : 0;
            return DrawnMatrix(m);
        }

        // A world point moved to where its floor stands (as Drawn), whether that floor is shown now or hidden: for
        // geometry between floors (which way, how high), not for drawing.
        public static void Placed(ref Vector3 p)
        {
            if (!On || p.z < groundRows)
            {
                return;
            }
            int k = FloorOfRow(Mathf.FloorToInt(p.z));
            if (k > 0)
            {
                p = new Vector3(p.x, p.y + heights[k] / View3D.Squash, p.z - shifts[k]);
            }
        }

        // Which way from one world point to another as drawn (flat): an aim between floors points at what is seen. A
        // point on a hidden floor counts where that floor stands (Placed): a shooter there still aims at its target.
        public static Vector3 FlatDelta(Vector3 from, Vector3 to)
        {
            Placed(ref from);
            Placed(ref to);
            Vector3 d = to - from;
            d.y = 0f;
            return d;
        }

        // Two world points on two different floors (neither between floors): a spray, beam or stream from one to the other.
        public static bool CrossFloor(Vector3 a, Vector3 b)
        {
            if (!On)
            {
                return false;
            }
            int ka = FloorOfRow(Mathf.FloorToInt(a.z));
            int kb = FloorOfRow(Mathf.FloorToInt(b.z));
            return ka != kb && ka >= 0 && kb >= 0;
        }

        // A point between two floors' points as drawn (Placed, weight f on a), put back in the rows of the floor of the
        // nearer end (things such as flecks are drawn by their row: they stand on that floor).
        public static Vector3 FlatLerp(Vector3 a, Vector3 b, float f)
        {
            Vector3 like = f >= 0.5f ? a : b;
            Placed(ref a);
            Placed(ref b);
            Vector3 p = a * f + b * (1f - f);
            int k = FloorOfRow(Mathf.FloorToInt(like.z));
            return new Vector3(p.x, like.y, p.z + (k > 0 ? shifts[k] : 0));
        }

        // The layout's extra offset of one thing (a pawn on a stair step) and its lift onto a bridge deck (Bridges), as a
        // drawn-space matrix; identity for most.
        public static Matrix4x4 ExtraMatrix(Thing t)
        {
            Vector3 extra = LayoutOffset(t);
            extra.y += Bridges.LiftOf(t);
            return extra == Vector3.zero ? Matrix4x4.identity : Matrix4x4.Translate(new Vector3(extra.x, extra.y / View3D.Squash, extra.z));
        }

        // Drawn-space offset of a thing (culling boxes, name labels); zero without floors and off bridges.
        public static Vector3 DrawnOffset(Thing t)
        {
            if (t == null || !t.Spawned)
            {
                return Vector3.zero;
            }
            Vector3 o = ThingOffset(t);
            return new Vector3(o.x, o.y / View3D.Squash, o.z);
        }

        // True-space offset of a thing (for culling boxes and labels): its floor's, the layout's own for it, and its lift
        // onto a bridge deck.
        public static Vector3 ThingOffset(Thing t)
        {
            Vector3 o = On ? RowOffset(t.Position.z) : Vector3.zero;
            o += LayoutOffset(t);
            o.y += Bridges.LiftOf(t);
            return o;
        }

        // The layout's extra offset of one thing (zero without a layout).
        private static Vector3 LayoutOffset(Thing t)
        {
            if (!On || Api.Floors == null)
            {
                return Vector3.zero;
            }
            try
            {
                // An attached pawn (Api 13) is drawn on its host: the pair takes one offset (a step, a ladder), the host's,
                // or the attached pawn's when it leads (Attachments.LayoutSource).
                return Api.Floors.ThingOffset(Attachments.Any ? Attachments.LayoutSource(t) : t);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] The floor layout's thing offset failed: " + e, 93611402);
                return Vector3.zero;
            }
        }

        // ---- the pointer

        // The number of floors above the ground this frame (0 without floors or with the 3D view off).
        public static int Count
        {
            get { return On ? count : 0; }
        }

        // The floors the pointer works on are the shown ones, from the one viewed (PointerTop) down to the ground. The ray
        // under a screen pixel lands on the first of them, from the top, whose floor is built where it meets that floor's
        // plane, or where something that can be selected stands in that cell (a floor's blueprint or frame on the open
        // air): the open air beside a building upstairs lets it through to the floor below, the ground always catches it.
        // Hidden floors above the viewed one are never hit. Everything that turns the pointer into a map position goes
        // through here: UI.UIToMapPosition (MouseCell, MouseMapPosition: orders, float menus, designators, drags, labels,
        // brackets), the 3D picking (Interaction) and the cut-away round the pointer (Dynamic).
        public static int PointerTop
        {
            get { return On ? pointer : 0; }
        }

        // The floor the ray under a screen pixel lands on (0 = the ground).
        public static int PointerFloorAt(Vector2 px)
        {
            if (!On || pointer <= 0 || frameMap == null)
            {
                return 0;
            }
            // With a designator in hand (build, floor, zone, order) the pointer stays on the viewed floor: it places on
            // that floor, open air included (a new upper floor is laid on the open air there). Orders and selection
            // without one fall through to the floor below.
            if (Find.DesignatorManager?.SelectedDesignator != null)
            {
                Vector3 v = View3D.ScreenToHeight(px, heights[pointer]);
                int vz = Mathf.FloorToInt(v.z);
                return vz >= 0 && vz < groundRows ? pointer : 0;
            }
            for (int k = pointer; k >= 1; k--)
            {
                Vector3 p = View3D.ScreenToHeight(px, heights[k]);
                int gz = Mathf.FloorToInt(p.z);
                if (gz < 0 || gz >= groundRows)
                {
                    continue;
                }
                IntVec3 c = new IntVec3(Mathf.FloorToInt(p.x), 0, gz + shifts[k]);
                if (HasFloor(frameMap, c) || SelectableOn(frameMap, c))
                {
                    return k;
                }
            }
            return 0;
        }

        // Something that can be selected stands in the cell (the pointer stops there on the open air upstairs).
        public static bool SelectableOn(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            List<Thing> things = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].def.selectable)
                {
                    return true;
                }
            }
            return false;
        }

        // Whether floor k is drawn now (the ground always; without floors only the ground exists).
        public static bool Shown(int k)
        {
            return k == 0 || On && k <= count && shown[k];
        }

        // Shown and drawn solid (not faded, FadeFloors): it covers what is under it.
        public static bool ShownSolid(int k)
        {
            return Shown(k) && (k == 0 || fade[k] == 0);
        }

        // The FadeFloors slot of floor k (0: solid or hidden).
        public static int FadeOf(int k)
        {
            return On && k > 0 && k <= count ? fade[k] : 0;
        }

        // Height (cells) of floor k and how far its rows are from the ground's (zero for the ground or without floors).
        public static void FloorPlane(int k, out float height, out int zShift)
        {
            if (!On || k <= 0 || k > count)
            {
                height = 0f;
                zShift = 0;
                return;
            }
            height = heights[k];
            zShift = shifts[k];
        }

        // The map position under a screen pixel: on the floor the ray lands on (PointerFloorAt), else on the ground.
        public static Vector3 ScreenToMap(Vector2 px)
        {
            int k = PointerFloorAt(px);
            if (k <= 0)
            {
                return View3D.ScreenToGround(px);
            }
            Vector3 p = View3D.ScreenToHeight(px, heights[k]);
            return new Vector3(p.x, 0f, p.z + shifts[k]);
        }

        // ---- the view rectangle: the ground's, and the same ground seen raised on each shown floor

        public static CellRect WidenView(CellRect ground)
        {
            if (!On)
            {
                return ground;
            }
            CellRect r = ground;
            float tan = Mathf.Max(0.2f, Mathf.Tan(View3D.Pitch * Mathf.Deg2Rad));
            for (int k = 1; k <= count; k++)
            {
                if (!shown[k])
                {
                    continue;
                }
                int pad = Mathf.CeilToInt(heights[k] / tan) + 2;
                CellRect f = ground.ExpandedBy(pad);
                f.minZ = Mathf.Max(f.minZ, 0) + shifts[k];
                f.maxZ = Mathf.Min(f.maxZ, groundRows - 1) + shifts[k];
                if (f.maxZ >= f.minZ)
                {
                    r = r.Encapsulate(f);
                }
            }
            return r;
        }

        // ---- sections

        // Set while a section on an upper floor is drawn: the matrix its layers are drawn with.
        public static bool sectionLifted;
        public static Matrix4x4 sectionMatrix = Matrix4x4.identity;

        public static bool DrawSection_Prefix(Section __instance)
        {
            sectionLifted = false;
            if (!On)
            {
                return true;
            }
            Matrix4x4 m;
            bool visible = RowMatrix(__instance.botLeft.z, out m);
            if (!visible)
            {
                // A hidden floor is not drawn, but its walls still cast their sun shadow on the ground (user 2026-10-16).
                DrawHiddenShadow(__instance);
                return false;
            }
            if (__instance.botLeft.z >= groundRows)
            {
                sectionLifted = true;
                sectionMatrix = m;
                FadeSlot = fade[FloorOfRow(__instance.botLeft.z)];
            }
            return true;
        }

        public static bool DrawDynamicSections_Prefix(Section __instance)
        {
            return DrawSection_Prefix(__instance);
        }

        public static void Section_Finalizer()
        {
            sectionLifted = false;
            FadeSlot = 0;
        }

        // Cull.SectionOverlaps: false for a section on a hidden floor (or between floors); otherwise true, with the
        // drawn-space shift of its box (zero on the ground and without floors).
        public static bool SectionDrawShift(Section section, out Vector3 shift)
        {
            shift = Vector3.zero;
            if (!On || section.botLeft.z < groundRows)
            {
                return true;
            }
            int k = FloorOfRow(section.botLeft.z);
            if (k <= 0 || !shown[k])
            {
                return false;
            }
            shift = new Vector3(0f, heights[k] / View3D.Squash, -shifts[k]);
            return true;
        }

        // A layer of a section on an upper floor (Patches.DrawLayer_Section, only while sectionLifted): drawn here with
        // the floor's matrix. kind as there: 0 normal, 1 lighting overlay, 2 fog, 3 the game's building damage.
        // Always returns false (the game's own drawing is skipped).
        public static bool DrawLayer(SectionLayer layer, int kind)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (kind == 1 || kind == 3 && st.damage3D || !layer.Visible)
            {
                // No lighting overlay on upper floors: the sheet covers the open air between floors too and would
                // darken the ground under it a second time at night. Damage: IsoRim's own marks are on the shapes.
                return false;
            }
            if (layer is SectionLayer_EdgeShadows)
            {
                // The game's soft band round walls lies flat on the floor beside them; up here that is the open air,
                // where it would hang as a dark sheet across the wall below.
                return false;
            }
            if (layer.GetType() == SunShadowsType)
            {
                // Sun shadows fall on the ground under the column, not in the open air beside the wall (DrawSunShadow).
                DrawSunShadow(layer, layerSection(layer));
                return false;
            }
            if (kind == 0)
            {
                IsoGlow.DrawLayerMarks(layer, sectionMatrix);
                if (layer is SectionLayer_Terrain)
                {
                    DrawSlab(layer);
                }
            }
            bool fogLid = kind == 2 && View3D.FogLid;
            // (the per-storey lid: no lid on fogged cells under a floor of the storey above while it shows)
            bool cover = false;
            if (fogLid)
            {
                Section s = layerSection(layer);
                cover = s != null && Shown(FloorOfRow(s.botLeft.z) + 1);
            }
            List<LayerSubMesh> subs = layer.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sm = subs[i];
                if (sm.finalized && !sm.disabled)
                {
                    Material mat = fogLid && sm.material == MatBases.FogOfWar ? IsoCellMap.FogFor(sm.material, cover) ?? sm.material : sm.material;
                    Graphics.DrawMesh(sm.mesh, sectionMatrix, mat, sm.renderLayer);
                }
            }
            return false;
        }

        // ---- sun shadows of upper floors (design isorim-pz-tall-shadows.md). The game builds each upper block's
        // SectionLayer_SunShadows from that floor's own walls: a footprint whose edges slide along the cast vector by up to
        // one wall (SunShadow.vert: + SunDir * vertex alpha). Drawn on the ground under the column (moved down by the
        // floor's rows) and slid along the cast by the floor's height, it is that storey's part of the column's shadow:
        // a wall over a wall joins into one shadow, a wall on a slab throws a band from the right place. The stencil of
        // the SunShadow shader darkens each pixel once, so the parts never darken twice where they overlap. Each part
        // covers its whole storey, from the top of the walls below to the top of its own (StoreyHeight is a little more
        // than a wall): no light line between the parts at dawn and dusk. Roofs and slabs cast nothing (as the game's
        // roofs); the receiver is the ground. Hidden floors still cast (user 2026-10-16).
        internal static readonly Type SunShadowsType = AccessTools.TypeByName("Verse.SectionLayer_SunShadows");

        // Harness A/B switch (ProjectZ test talltower): off, each part is only one wall long (a light line between them).
        public static bool ShadowGapFill = true;

        // A block of an upper floor (shown or hidden): how its sun shadow is moved to the ground (drawn space) and the
        // slides, in cast lengths, its part covers: drawn at lo and hi (and between, a wall apart, if a storey is more
        // than two walls), each draw covering one wall more. False for the ground, rows between floors and without floors.
        public static bool ShadowSlide(Section section, out Vector3 groundShift, out float lo, out float hi)
        {
            groundShift = Vector3.zero;
            lo = hi = 0f;
            if (!On || section == null || section.botLeft.z < groundRows)
            {
                return false;
            }
            int k = FloorOfRow(section.botLeft.z);
            if (k <= 0)
            {
                return false;
            }
            float wall = View3D.WallHeight;
            hi = heights[k] / wall;
            lo = ShadowGapFill ? Mathf.Min(hi, (k > 1 ? heights[k - 1] : 0f) / wall + 1f) : hi;
            groundShift = new Vector3(0f, 0f, -shifts[k]);
            return true;
        }

        private static void DrawSunShadow(SectionLayer layer, Section section)
        {
            Vector3 shift;
            float lo, hi;
            Vector4 cast;
            if (layer == null || !ShadowSlide(section, out shift, out lo, out hi) || !Shader.TryGetGlobalVector(ShaderPropertyIDs.MapSunLightDirection, out cast))
            {
                return;
            }
            // The same global the shader slides by (_CastVect: IsoRim's mirrored sun, Patches.GetLightSourceInfo_Postfix).
            Vector3 v = new Vector3(cast.x, 0f, cast.z);
            List<LayerSubMesh> subs = layer.subMeshes;
            float t = hi;
            while (true)
            {
                Matrix4x4 m = Matrix4x4.Translate(shift + v * t);
                for (int i = 0; i < subs.Count; i++)
                {
                    LayerSubMesh sm = subs[i];
                    if (sm.finalized && !sm.disabled && sm.mesh != null && sm.mesh.vertexCount > 0)
                    {
                        Graphics.DrawMesh(sm.mesh, m, sm.material, sm.renderLayer);
                    }
                }
                if (t <= lo + 0.0005f)
                {
                    break;
                }
                t = Mathf.Max(lo, t - 1f);
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Section, SectionLayer> sunLayers = new System.Runtime.CompilerServices.ConditionalWeakTable<Section, SectionLayer>();
        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> sectionLayers = AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");

        private static SectionLayer SunLayerOf(Section section)
        {
            SectionLayer found;
            if (sunLayers.TryGetValue(section, out found))
            {
                return found;
            }
            List<SectionLayer> layers = sectionLayers(section);
            for (int i = 0; layers != null && i < layers.Count; i++)
            {
                if (layers[i].GetType() == SunShadowsType)
                {
                    found = layers[i];
                    break;
                }
            }
            sunLayers.Add(section, found);
            return found;
        }

        // A block of a hidden floor: only its sun shadow, rebuilt here when its walls changed (the game rebuilds a block's
        // layers when it is drawn, and a hidden one is not).
        private static void DrawHiddenShadow(Section section)
        {
            Vector3 unusedShift;
            float unusedLo, unusedHi;
            if (SunShadowsType == null || !ShadowSlide(section, out unusedShift, out unusedLo, out unusedHi))
            {
                return;
            }
            SectionLayer layer = SunLayerOf(section);
            if (layer == null || !layer.Visible)
            {
                return;
            }
            if (layer.Dirty)
            {
                layer.Dirty = false;
                try
                {
                    layer.Regenerate();
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Rebuilding the sun shadow of a hidden floor failed: " + e, 93611404);
                }
            }
            if (Cull.ColumnShadowVisible(section))
            {
                DrawSunShadow(layer, section);
            }
        }

        // The game's test for a block's sun shadow outside the view (Section.DrawDynamicSections): for an upper floor,
        // where it falls on the ground.
        public static bool SunShadowsShouldDraw_Prefix(SectionLayer __instance, ref bool __result)
        {
            Section section = layerSection(__instance);
            Vector3 unusedShift;
            float unusedLo, unusedHi;
            if (!ShadowSlide(section, out unusedShift, out unusedLo, out unusedHi))
            {
                return true;
            }
            __result = Cull.ColumnShadowVisible(section);
            return false;
        }

        // ---- floor slabs: the game's terrain does not write depth, so an upper floor drawn as terrain alone would not
        // hide what is below it. Every drawn cell of an upper floor gets a solid slab (top at the floor, a little thick,
        // sides where the floor ends), coloured like its terrain, lit like IsoRim's models.
        private sealed class Slab
        {
            public Mesh mesh;
            public bool dirty = true;
            public int stamp;
            public bool held;   // HoldSlab
        }

        internal const float SlabThickness = 0.12f;
        // The slab's top lies this far (true cells) under the floor: the game draws the soft edge where two terrains meet
        // (SectionLayer_Terrain's fans) at height 0, level with the floor, and the slab writes depth there; level, the two
        // fought over every pixel and the edge flickered as the camera moved. The terrain's own cells (at the game's
        // Terrain altitude) were already above. Far under a pixel at any zoom.
        internal const float SlabTopDrop = 0.004f;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Section, Slab> slabs = new System.Runtime.CompilerServices.ConditionalWeakTable<Section, Slab>();
        private static readonly AccessTools.FieldRef<SectionLayer, Section> layerSection = AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        // The block a section layer belongs to.
        internal static Section SectionOf(SectionLayer layer) => layer != null ? layerSection(layer) : null;

        public static void TerrainRegenerate_Postfix(SectionLayer_Terrain __instance)
        {
            Section s = layerSection(__instance);
            Slab slab;
            if (s != null && slabs.TryGetValue(s, out slab))
            {
                slab.dirty = true;
            }
            WallBoxes.ClipTerrain(__instance, s);   // PROTOTYPE (CliffLook.cs): the floor's edge over rock follows its chamfer
            ShapeFloors.ClipTerrain(__instance, s); // (Api 34) no floor past a slanted wall shape upstairs
        }

        private static void DrawSlab(SectionLayer layer)
        {
            Section section = layerSection(layer);
            if (section == null || section.botLeft.z < groundRows)
            {
                return;
            }
            Slab slab = slabs.GetValue(section, s => new Slab());
            if (!slab.held && (slab.dirty || slab.stamp != View3D.Stamp))
            {
                slab.dirty = false;
                slab.stamp = View3D.Stamp;
                BuildSlab(section, slab);
            }
            Material mat = R3DMaterials.LitModel;
            if (slab.mesh != null && mat != null)
            {
                IsoGlow.DrawLit(slab.mesh, sectionMatrix, mat, 0);
            }
        }

        private static bool Floored(Map map, IntVec3 c, int floor)
        {
            return c.InBounds(map) && FloorOfRow(c.z) == floor && !map.terrainGrid.TerrainAt(c).dontRender;
        }

        private static void BuildSlab(Section section, Slab slab)
        {
            Map map = section.map;
            int floor = FloorOfRow(section.botLeft.z);
            List<Vector3> verts = new List<Vector3>();
            List<Color32> cols = new List<Color32>();
            List<int> tris = new List<int>();
            float floorH, belowH;
            int unusedShift;
            ColumnFloor(map, section.botLeft, out floorH, out unusedShift);
            ColumnFloor(map, Stacked(map, section.botLeft, -1), out belowH, out unusedShift);
            SlabGeometry(map, section.CellRect, floorH, belowH, c => Floored(map, c, floor), verts, cols, tris);
            if (verts.Count == 0)
            {
                slab.mesh = null;
                return;
            }
            if (slab.mesh == null)
            {
                slab.mesh = new Mesh();
            }
            else
            {
                slab.mesh.Clear();
            }
            slab.mesh.SetVertices(verts);
            slab.mesh.SetColors(cols);
            slab.mesh.SetTriangles(tris, 0);
            slab.mesh.RecalculateBounds();
            MeshGuard.Check(slab.mesh, "floor slab");
        }

        // The slab of some cells of one upper floor (in the map's own coordinates, as the floor's blocks are), for the
        // floor's blocks and for the gravship cutscene's copy of a ship's upper decks: floored says which cells have
        // the floor built (the cells given and their neighbours: a side stands where the floor ends).
        internal static void SlabGeometry(Map map, IEnumerable<IntVec3> cells, float floorH, float belowH, Predicate<IntVec3> floored,
            List<Vector3> verts, List<Color32> cols, List<int> tris)
        {
            float s = View3D.Squash;
            float top = -SlabTopDrop / s;
            float bottom = -SlabThickness / s;
            foreach (IntVec3 c in cells)
            {
                if (!floored(c))
                {
                    continue;
                }
                // The column (design section 5): a wall here standing on a wall below is one wall through both storeys and
                // fills the cell; over a wall below, the slab's edge starts at that wall's top (no face over its face).
                float wallHere, wallBelow;
                bool blockBelow = FullBlock(map, Stacked(map, c, -1), out wallBelow);
                // (only a block here reaches down through the slab to the wall below, WallColumn; a wall from a model
                // stands on the slab)
                if (blockBelow && FullBlock(map, c, out wallHere, true))
                {
                    continue;
                }
                Vector3[] chamfer;
                if (blockBelow && WallBoxes.SlabOverRock(map, c, out chamfer))
                {
                    // PROTOTYPE (CliffLook.cs): over natural rock only the top, cut to the rock's chamfered outline (the rock
                    // is the body under it)
                    Color32 rc = map.terrainGrid.TerrainAt(c).DrawColor;
                    rc.a = 255;
                    Quad(verts, cols, tris, rc, new Vector3(c.x + chamfer[0].x, top, c.z + chamfer[0].z), new Vector3(c.x + chamfer[1].x, top, c.z + chamfer[1].z),
                        new Vector3(c.x + chamfer[2].x, top, c.z + chamfer[2].z), new Vector3(c.x + chamfer[3].x, top, c.z + chamfer[3].z));
                    continue;
                }
                float sideBottom = blockBelow ? Mathf.Min(top, Mathf.Max(bottom, (belowH + wallBelow - floorH) / s)) : bottom;
                Color32 col = map.terrainGrid.TerrainAt(c).DrawColor;
                col.a = 255;
                List<Vector2[]> keep = ShapeFloors.Keep(map, c);   // (Api 34) a trimmed wall cell upstairs: the room's part only
                if (keep != null)
                {
                    ShapedSlab(map, c, keep, top, sideBottom, col, verts, cols, tris);
                    continue;
                }
                float x0 = c.x, x1 = c.x + 1, z0 = c.z, z1 = c.z + 1;
                Quad(verts, cols, tris, col, new Vector3(x0, top, z0), new Vector3(x0, top, z1), new Vector3(x1, top, z1), new Vector3(x1, top, z0));
                float bottomHere = bottom;
                bottom = sideBottom;
                if (!floored(c + IntVec3.South))
                {
                    Quad(verts, cols, tris, col, new Vector3(x0, bottom, z0), new Vector3(x0, top, z0), new Vector3(x1, top, z0), new Vector3(x1, bottom, z0));
                }
                if (!floored(c + IntVec3.North))
                {
                    Quad(verts, cols, tris, col, new Vector3(x0, bottom, z1), new Vector3(x0, top, z1), new Vector3(x1, top, z1), new Vector3(x1, bottom, z1));
                }
                if (!floored(c + IntVec3.West))
                {
                    Quad(verts, cols, tris, col, new Vector3(x0, bottom, z0), new Vector3(x0, top, z0), new Vector3(x0, top, z1), new Vector3(x0, bottom, z1));
                }
                if (!floored(c + IntVec3.East))
                {
                    Quad(verts, cols, tris, col, new Vector3(x1, bottom, z0), new Vector3(x1, top, z0), new Vector3(x1, top, z1), new Vector3(x1, bottom, z1));
                }
                bottom = bottomHere;
            }
        }

        // The gravship landing (ShipFreeze): a block's slab is kept as it is while held (built now if it is not yet), so
        // the upper decks placed under the frozen picture do not show early; let go, it is built again.
        internal static void HoldSlab(Section section, bool hold)
        {
            Slab slab = slabs.GetValue(section, s => new Slab());
            if (!hold)
            {
                slab.held = false;
                slab.dirty = true;
                return;
            }
            if (On && ReferenceEquals(frameMap, section.map) && (slab.dirty || slab.stamp != View3D.Stamp))
            {
                slab.dirty = false;
                slab.stamp = View3D.Stamp;
                BuildSlab(section, slab);
            }
            slab.held = true;
        }

        // A quad drawn from both sides (the shader turns each face toward the camera).
        // (Api 34) The slab of an upper-floor cell with replacing wall shapes: its top over the kept pieces (ShapeFloors) and a
        // side wherever a kept piece ends at no floor.
        private static void ShapedSlab(Map map, IntVec3 c, List<Vector2[]> keep, float top, float bottom, Color32 col,
            List<Vector3> verts, List<Color32> cols, List<int> tris)
        {
            float h;
            int z;
            int k = ColumnFloor(map, c, out h, out z);
            foreach (Vector2[] p in keep)
            {
                int start = verts.Count;
                for (int i = p.Length - 1; i >= 0; i--)   // clockwise seen from above, as the cells' quads
                {
                    verts.Add(new Vector3(p[i].x, top, p[i].y));
                    cols.Add(col);
                }
                for (int i = 1; i + 1 < p.Length; i++)
                {
                    tris.Add(start);
                    tris.Add(start + i);
                    tris.Add(start + i + 1);
                }
                for (int i = 0; i < p.Length; i++)
                {
                    Vector2 a = p[i], b = p[(i + 1) % p.Length];
                    Vector2 d = b - a;
                    if (d.sqrMagnitude < 1e-8f)
                    {
                        continue;
                    }
                    Vector2 o = new Vector2(d.y, -d.x).normalized;
                    if (ShapeFloors.FloorAt(map, (a + b) * 0.5f + o * 0.004f, k))
                    {
                        continue;
                    }
                    Quad(verts, cols, tris, col, new Vector3(a.x, bottom, a.y), new Vector3(a.x, top, a.y), new Vector3(b.x, top, b.y), new Vector3(b.x, bottom, b.y));
                }
            }
        }

        private static void Quad(List<Vector3> verts, List<Color32> cols, List<int> tris, Color32 col, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = verts.Count;
            verts.Add(a);
            verts.Add(b);
            verts.Add(c);
            verts.Add(d);
            cols.Add(col);
            cols.Add(col);
            cols.Add(col);
            cols.Add(col);
            tris.Add(i);
            tris.Add(i + 1);
            tris.Add(i + 2);
            tris.Add(i);
            tris.Add(i + 2);
            tris.Add(i + 3);
            tris.Add(i);
            tris.Add(i + 2);
            tris.Add(i + 1);
            tris.Add(i);
            tris.Add(i + 3);
            tris.Add(i + 2);
        }

        // ---- things drawn every frame

        // A thing on a hidden floor is not drawn at all; a pawn on a stair step gets its rise, a thing on a bridge deck its
        // lift (Bridges). The floor itself is added at the bottom (Drawn), like everything else. Hooked always (Patches),
        // not only with a floor layout.
        public static bool ThingPhase_Prefix(Thing __instance, DrawPhase phase, out bool __state)
        {
            __state = false;
            if (phase != DrawPhase.Draw || __instance == null || !View3D.Ready)
            {
                return true;
            }
            Matrix4x4 unused;
            if (On && !RowMatrix(__instance.Position.z, out unused))
            {
                return false;
            }
            if (On)
            {
                int fk = FloorOfRow(__instance.Position.z);
                if (fk > 0 && fade[fk] > 0)
                {
                    FadeSlot = fade[fk];
                    thingFade = true;
                }
            }
            Matrix4x4 m = ExtraMatrix(__instance);
            if (m == Matrix4x4.identity)
            {
                return true;
            }
            __state = true;
            DrawHook.floor = true;
            DrawHook.floorMatrix = m;
            return true;
        }

        private static bool thingFade;

        public static void ThingPhase_Finalizer(bool __state)
        {
            if (thingFade)
            {
                thingFade = false;
                FadeSlot = 0;
            }
            if (__state)
            {
                DrawHook.floor = false;
                DrawHook.floorMatrix = Matrix4x4.identity;
            }
        }

        // ---- the bottom of all drawing (design "M1c-2 coordinates", section 3). On Waylith, UnityEngine.Graphics is managed code:
        // every DrawMesh overload ends in DrawMeshCore or in an overload that queues the draw itself, every
        // DrawMeshInstanced in one of three overloads. Those get Drawn; lines get their two ends mapped; the game's
        // whole-map overlays (CellBoolDrawer) their cells; screen labels the point they are pinned to.
        private static void PatchDrawing(Harmony h)
        {
            HarmonyMethod single = new HarmonyMethod(typeof(Floors), nameof(Single_Prefix));
            HarmonyMethod array = new HarmonyMethod(typeof(Floors), nameof(InstancedArray_Prefix));
            HarmonyMethod list = new HarmonyMethod(typeof(Floors), nameof(InstancedList_Prefix));
            // FadeFloors: before the floor mapping (it reads the world row of the matrix).
            HarmonyMethod fadeSingle = new HarmonyMethod(typeof(Floors), nameof(Fade_Prefix)) { priority = Priority.First };
            HarmonyMethod fadeInstanced = new HarmonyMethod(typeof(Floors), nameof(FadeInstanced_Prefix)) { priority = Priority.First };
            int n = 0;
            foreach (System.Reflection.MethodInfo m in AccessTools.GetDeclaredMethods(typeof(Graphics)))
            {
                if (!m.IsStatic || m.ContainsGenericParameters)
                {
                    continue;
                }
                System.Reflection.ParameterInfo[] ps = m.GetParameters();
                bool hasArray = false, hasList = false;
                foreach (System.Reflection.ParameterInfo p in ps)
                {
                    hasArray |= p.Name == "matrices" && p.ParameterType == typeof(Matrix4x4[]);
                    hasList |= p.Name == "matrices" && p.ParameterType == typeof(List<Matrix4x4>);
                }
                string calls = Calls(m);
                bool hasMaterial = false;
                foreach (System.Reflection.ParameterInfo p in ps)
                {
                    hasMaterial |= p.Name == "material" && p.ParameterType == typeof(Material);
                }
                if (IsSingleEntry(m))
                {
                    h.Patch(m, prefix: single);
                    if (hasMaterial)
                    {
                        h.Patch(m, prefix: fadeSingle);
                    }
                    n++;
                }
                else if (m.Name == "DrawMeshInstanced" && calls.Contains("|EnqueueInstancedDirect|") && (hasArray || hasList))
                {
                    h.Patch(m, prefix: hasArray ? array : list);
                    if (hasMaterial)
                    {
                        h.Patch(m, prefix: fadeInstanced);
                    }
                    n++;
                }
            }
            h.Patch(AccessTools.Method(typeof(GenDraw), nameof(GenDraw.DrawLineBetween), new[] { typeof(Vector3), typeof(Vector3), typeof(Material), typeof(float) }),
                prefix: new HarmonyMethod(typeof(Floors), nameof(Line_Prefix)));
            h.Patch(AccessTools.Method(typeof(CellBoolDrawer), nameof(CellBoolDrawer.RegenerateMesh)), postfix: new HarmonyMethod(typeof(Floors), nameof(CellBool_Postfix)));
            h.Patch(AccessTools.Method(typeof(SectionLayer_LightingOverlay), nameof(SectionLayer_LightingOverlay.Regenerate)), postfix: new HarmonyMethod(typeof(Floors), nameof(LightingOverlay_Postfix)));
            h.Patch(AccessTools.Method(typeof(Camera), nameof(Camera.WorldToScreenPoint), new[] { typeof(Vector3) }), prefix: new HarmonyMethod(typeof(Floors), nameof(WorldToScreen_Prefix)));
            h.Patch(AccessTools.Method(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty), new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(Floors), nameof(MapMeshDirty_Postfix)));
            ModLog.Note("[IsoRim] Floors: " + n + " Graphics entry points mapped to the floors.");
        }

        // A Graphics method that queues one mesh draw with its world matrix itself (DrawMesh, DrawMeshCore): every single
        // mesh drawn passes exactly one of these. Also hooked for beams drawn in 3D (FloorSprays.Tilt), with or without floors.
        internal static bool IsSingleEntry(System.Reflection.MethodInfo m)
        {
            if (!m.IsStatic || m.ContainsGenericParameters || (m.Name != "DrawMesh" && m.Name != "DrawMeshCore"))
            {
                return false;
            }
            bool hasMatrix = false;
            foreach (System.Reflection.ParameterInfo p in m.GetParameters())
            {
                hasMatrix |= p.Name == "matrix" && p.ParameterType == typeof(Matrix4x4);
            }
            return hasMatrix && Calls(m).Contains("|Enqueue|");
        }

        // The names of the methods a method calls, as "|a|b|".
        private static string Calls(System.Reflection.MethodInfo m)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder("|");
            try
            {
                foreach (KeyValuePair<System.Reflection.Emit.OpCode, object> ins in PatchProcessor.ReadMethodBody(m))
                {
                    if (ins.Value is System.Reflection.MethodInfo callee)
                    {
                        sb.Append(callee.Name).Append('|');
                    }
                }
            }
            catch (Exception)
            {
            }
            return sb.ToString();
        }

        // While set, single meshes are drawn as given: the caller worked out drawn space itself (SkyStrikes: a column
        // from the sky whose matrix may reach far past its impact cell's rows, which must not be read as another floor).
        public static bool InDrawnSpace;

        public static bool Single_Prefix(ref Matrix4x4 matrix)
        {
            return !On || InDrawnSpace || Drawn(ref matrix);
        }

        // FadeFloors: a draw for a faded floor (its block or thing being drawn, or a world matrix in its rows) takes the
        // faded copy of its material. A draw found by its row alone (overlays, motes, fire: no block or thing being drawn)
        // whose shader has no faded form is left out.
        public static bool Fade_Prefix(ref Matrix4x4 matrix, ref Material material)
        {
            if (!On || material == null)
            {
                return true;
            }
            int slot = FadeSlot;
            bool byRow = false;
            if (slot == 0 && !InDrawnSpace && matrix.m23 >= groundRows)
            {
                int k = FloorOfRow(Mathf.FloorToInt(matrix.m23));
                slot = k > 0 ? fade[k] : 0;
                byRow = true;
            }
            if (slot > 0)
            {
                Material faded = FadeFloors.For(material, slot);
                if (byRow && ReferenceEquals(faded, material))
                {
                    return false;
                }
                material = faded;
            }
            return true;
        }

        public static void FadeInstanced_Prefix(ref Material material)
        {
            if (On && FadeSlot > 0 && material != null)
            {
                material = FadeFloors.For(material, FadeSlot);
            }
        }

        private static Matrix4x4[] instanceBuffer = new Matrix4x4[256];
        private static readonly List<Matrix4x4> instanceList = new List<Matrix4x4>();

        public static bool InstancedArray_Prefix(ref Matrix4x4[] matrices, ref int count)
        {
            if (!On || matrices == null)
            {
                return true;
            }
            int n = Mathf.Min(count, matrices.Length);
            int i = 0;
            while (i < n && matrices[i].m23 < groundRows)
            {
                i++;
            }
            if (i == n)
            {
                return true;   // all on the ground (the usual case): untouched
            }
            if (instanceBuffer.Length < n)
            {
                instanceBuffer = new Matrix4x4[Mathf.NextPowerOfTwo(n)];
            }
            int kept = 0;
            for (int j = 0; j < n; j++)
            {
                Matrix4x4 m = matrices[j];
                if (Drawn(ref m))
                {
                    instanceBuffer[kept++] = m;
                }
            }
            matrices = instanceBuffer;
            count = kept;
            return kept > 0;
        }

        public static bool InstancedList_Prefix(ref List<Matrix4x4> matrices)
        {
            if (!On || matrices == null || ReferenceEquals(matrices, instanceList))
            {
                return true;
            }
            int i = 0;
            while (i < matrices.Count && matrices[i].m23 < groundRows)
            {
                i++;
            }
            if (i == matrices.Count)
            {
                return true;
            }
            instanceList.Clear();
            for (int j = 0; j < matrices.Count; j++)
            {
                Matrix4x4 m = matrices[j];
                if (Drawn(ref m))
                {
                    instanceList.Add(m);
                }
            }
            matrices = instanceList;
            return instanceList.Count > 0;
        }

        // A line between two points (paths, move orders, links): each end where it shows. Between floors the line
        // climbs, so it is laid along the slope here instead of flat at one end's height.
        public static bool Line_Prefix(ref Vector3 A, ref Vector3 B, Material mat, float lineWidth)
        {
            if (!On)
            {
                return true;
            }
            if (!Drawn(ref A) || !Drawn(ref B))
            {
                return false;
            }
            if (Mathf.Abs(A.y - B.y) < 0.01f)
            {
                return true;
            }
            Vector3 d = A - B;
            if (d.magnitude < 0.01f)
            {
                return false;
            }
            Matrix4x4 matrix = Matrix4x4.TRS((A + B) * 0.5f, Quaternion.LookRotation(d), new Vector3(lineWidth, 1f, d.magnitude));
            Graphics.DrawMesh(MeshPool.plane10, matrix, mat, 0);
            return false;
        }

        // The screen point of a world point on the map camera (labels, damage numbers, arrows): where it shows.
        public static void WorldToScreen_Prefix(Camera __instance, ref Vector3 position)
        {
            if (On && position.z >= groundRows && ReferenceEquals(__instance, Find.Camera))
            {
                Drawn(ref position);
            }
        }

        // The game's lighting overlay of the block holding the ground's top rows also covers the rows between the
        // ground and the first floor (design section 6: the map ends at the ground's edge): there it would darken
        // the empty background past the north edge. Its cells there are left out.
        public static void LightingOverlay_Postfix(SectionLayer_LightingOverlay __instance)
        {
            Section s = layerSection(__instance);
            int rows = s != null ? GroundRowsOf(s.map) : 0;
            if (rows <= 0 || s.botLeft.z >= rows || s.botLeft.z + 17 <= rows)
            {
                return;
            }
            List<LayerSubMesh> subs = __instance.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sm = subs[i];
                List<int> t = sm.tris;
                List<Vector3> v = sm.verts;
                if (v.Count == 0)
                {
                    continue;
                }
                int kept = 0;
                for (int j = 0; j + 2 < t.Count; j += 3)
                {
                    if (v[t[j]].z > rows || v[t[j + 1]].z > rows || v[t[j + 2]].z > rows)
                    {
                        continue;
                    }
                    t[kept++] = t[j];
                    t[kept++] = t[j + 1];
                    t[kept++] = t[j + 2];
                }
                if (kept < t.Count)
                {
                    t.RemoveRange(kept, t.Count - kept);
                    if (sm.mesh != null)
                    {
                        sm.mesh.SetTriangles(t.ToArray(), 0);
                        MeshGuard.Check(sm.mesh, "upper floor overlay");
                    }
                }
            }
        }

        // ---- the game's whole-map overlays (home area, roofs, beauty, temperature ...): one mesh of world-space cell
        // quads. Each cell's quad is moved onto its floor; on hidden floors and between floors it is folded away.
        // The drawers are remembered so a change of the view (another floor shown) remakes them.
        private static readonly AccessTools.FieldRef<CellBoolDrawer, List<Mesh>> boolMeshes = AccessTools.FieldRefAccess<CellBoolDrawer, List<Mesh>>("meshes");
        private static readonly List<System.WeakReference<CellBoolDrawer>> boolDrawers = new List<System.WeakReference<CellBoolDrawer>>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CellBoolDrawer, object> boolKnown = new System.Runtime.CompilerServices.ConditionalWeakTable<CellBoolDrawer, object>();

        public static void CellBool_Postfix(CellBoolDrawer __instance)
        {
            object unused;
            if (!boolKnown.TryGetValue(__instance, out unused))
            {
                boolKnown.Add(__instance, null);
                boolDrawers.Add(new System.WeakReference<CellBoolDrawer>(__instance));
            }
            if (!On)
            {
                return;
            }
            List<Mesh> meshes = boolMeshes(__instance);
            for (int i = 0; meshes != null && i < meshes.Count; i++)
            {
                Mesh mesh = meshes[i];
                Vector3[] v = mesh.vertices;
                bool changed = false;
                for (int q = 0; q + 3 < v.Length; q += 4)
                {
                    float z = Mathf.Min(Mathf.Min(v[q].z, v[q + 1].z), Mathf.Min(v[q + 2].z, v[q + 3].z));
                    if (z < groundRows)
                    {
                        continue;
                    }
                    changed = true;
                    Vector3 probe = new Vector3(v[q].x, v[q].y, z + 0.5f);
                    if (!Drawn(ref probe))
                    {
                        v[q + 1] = v[q + 2] = v[q + 3] = v[q];
                        continue;
                    }
                    Vector3 shift = probe - new Vector3(v[q].x, v[q].y, z + 0.5f);
                    for (int c = 0; c < 4; c++)
                    {
                        v[q + c] += shift;
                    }
                }
                if (changed)
                {
                    mesh.vertices = v;
                    MeshGuard.Check(mesh, "upper floor overlay cells");
                    mesh.RecalculateBounds();
                }
            }
        }

        // The view changed (floors on, another floor shown, another height): the overlays are remade.
        private static void ViewChanged()
        {
            for (int i = boolDrawers.Count - 1; i >= 0; i--)
            {
                CellBoolDrawer d;
                if (boolDrawers[i].TryGetTarget(out d))
                {
                    d.SetDirty();
                }
                else
                {
                    boolDrawers.RemoveAt(i);
                }
            }
        }

        // ---- the floor column (design section 5): cell (x, z + k * zShift) stands over ground cell (x, z). These read the
        // layout itself (not this frame's state), for code that builds meshes.

        // The ground rows of a map with floors, else 0.
        public static int GroundRowsOf(Map map)
        {
            IFloorLayout layout = Api.Floors;
            if (layout == null || map == null)
            {
                return 0;
            }
            try
            {
                int rows = layout.GroundRows(map);
                return rows > 0 && layout.FloorCount(map) > 0 ? rows : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // The rows of the map that are real ground, where the world is drawn: the ground rows with floors, else every row.
        // The rows above them only hold the floors' cells (drawn moved over the ground), so past the ground is the void,
        // as past any other edge of the map.
        public static int DrawnRows(Map map)
        {
            if (map == null)
            {
                return 0;
            }
            int rows = GroundRowsOf(map);
            return rows > 0 ? Mathf.Min(rows, map.Size.z) : map.Size.z;
        }

        // All floors of a map as the layout gives them (not this frame's state; works with the 3D view off): the ground's
        // rows (0 without floors) and the number of floors above it; heights[k] (true cells) and shifts[k] (rows) of
        // floor k, index 0 the ground (0, 0). Without floors: 0 and the ground only.
        public static int Bands(Map map, out int groundRows, out float[] heights, out int[] shifts)
        {
            groundRows = GroundRowsOf(map);
            int n = 0;
            if (groundRows > 0)
            {
                try
                {
                    n = Mathf.Clamp(Api.Floors.FloorCount(map), 0, MaxFloors);
                }
                catch (Exception)
                {
                    n = 0;
                }
            }
            heights = new float[n + 1];
            shifts = new int[n + 1];
            for (int k = 1; k <= n; k++)
            {
                Api.Floors.Floor(map, k, out heights[k], out shifts[k]);
            }
            if (n == 0)
            {
                groundRows = 0;
            }
            return n;
        }

        // The floor of a cell (0 the ground, -1 between floors or without floors), its height and its row shift.
        public static int ColumnFloor(Map map, IntVec3 c, out float height, out int zShift)
        {
            height = 0f;
            zShift = 0;
            int rows = GroundRowsOf(map);
            if (rows <= 0)
            {
                return -1;
            }
            if (c.z < rows)
            {
                return 0;
            }
            int n = Mathf.Min(Api.Floors.FloorCount(map), MaxFloors);
            for (int k = 1; k <= n; k++)
            {
                float h;
                int z;
                Api.Floors.Floor(map, k, out h, out z);
                if (c.z >= z && c.z < z + rows)
                {
                    height = h;
                    zShift = z;
                    return k;
                }
            }
            return -1;
        }

        // The cell dk floors above (or below, dk < 0) a cell in its column; Invalid where there is none.
        public static IntVec3 Stacked(Map map, IntVec3 c, int dk)
        {
            float h;
            int z;
            int k = ColumnFloor(map, c, out h, out z);
            int to = k + dk;
            if (k < 0 || to < 0 || to > Mathf.Min(Api.Floors.FloorCount(map), MaxFloors))
            {
                return IntVec3.Invalid;
            }
            int zTo = 0;
            if (to > 0)
            {
                float h2;
                Api.Floors.Floor(map, to, out h2, out zTo);
            }
            return new IntVec3(c.x, 0, c.z - z + zTo);
        }

        // Whether a cell has a floor to lie on: always on the ground (and on maps without floors), on an upper floor only
        // where its floor is built (the open air up there has none), never between floors. Where the pointer's ray stops
        // (PointerFloorAt), and where what lies flat on the ground beside a thing can lie (GroundShadows; in the open air
        // it would hang across the wall below).
        public static bool HasFloor(Map map, IntVec3 c)
        {
            if (GroundRowsOf(map) <= 0)
            {
                return true;
            }
            float h;
            int z;
            int k = ColumnFloor(map, c, out h, out z);
            if (k <= 0)
            {
                return k == 0;
            }
            return c.InBounds(map) && !map.terrainGrid.TerrainAt(c).dontRender;
        }

        // The floor above a cell exists there (it is the cell's roof: the slab).
        public static bool SlabOver(Map map, IntVec3 c)
        {
            IntVec3 up = Stacked(map, c, 1);
            return up.IsValid && up.InBounds(map) && !map.terrainGrid.TerrainAt(up).dontRender;
        }

        // A full-height block (a wall) in a cell, and its height: a block of fill 0.9 or more (not a low wall), or a wall drawn from a model
        // (WallBoxes.ModelWall, its wall top; its crown is left out under a floor built over it).
        public static bool FullBlock(Map map, IntVec3 c, out float height)
        {
            return FullBlock(map, c, out height, false);
        }

        // The same; blocksOnly: blocks only, no wall from a model.
        internal static bool FullBlock(Map map, IntVec3 c, out float height, bool blocksOnly)
        {
            height = 0f;
            if (!c.IsValid || !c.InBounds(map))
            {
                return false;
            }
            Building b = c.GetEdifice(map);
            if (b == null)
            {
                return false;
            }
            ThingForm f = Forms.For(b.def);
            if (f.form != Form3D.Block)
            {
                if (blocksOnly || f.wallTop <= 0f)
                {
                    return false;
                }
                height = f.wallTop;
                return true;
            }
            if (WallBoxes.Fill(b.def) < 0.9f || WallBoxes.LowWall(b.def))
            {
                return false;   // (Api 20) a low wall drawn under its fill (a half wall) is not full height either
            }
            height = f.height;
            return true;
        }

        // A block on an upper floor standing on a block below (one wall through both storeys): how far its sides
        // go down to meet the one below (cells), the floor's height above the ground and its row shift (so the
        // texture runs on). False (all zero) for anything else.
        public static bool WallColumn(Map map, IntVec3 c, out float drop, out float floorHeight, out int zShift)
        {
            drop = 0f;
            int k = ColumnFloor(map, c, out floorHeight, out zShift);
            if (k <= 0)
            {
                return false;
            }
            float below;
            IntVec3 under = Stacked(map, c, -1);
            if (FullBlock(map, under, out below))
            {
                float hb;
                int zb;
                ColumnFloor(map, under, out hb, out zb);
                drop = Mathf.Max(0f, floorHeight - hb - below) + 0.004f;
            }
            return true;
        }

        // Something in a column changed: the cells above and below it are remade too (a wall's sides reach down,
        // a slab is a roof below it, a roof sits on top of a column).
        private static bool dirtying;

        public static void MapMeshDirty_Postfix(MapDrawer __instance, IntVec3 loc, ulong dirtyFlags)
        {
            if (dirtying || Api.Floors == null || Current.ProgramState != ProgramState.Playing || drawerSections(__instance) == null)
            {
                return;   // (as the game: nothing to remake while the map is made)
            }
            MineXray.Dirty(dirtyFlags);   // the x-ray of the floor below remakes its outline
            ulong want = (ulong)RimWorld.MapMeshFlagDefOf.Buildings | (ulong)RimWorld.MapMeshFlagDefOf.Terrain | (ulong)RimWorld.MapMeshFlagDefOf.Roofs;
            if ((dirtyFlags & want) == 0)
            {
                return;
            }
            Map map = mapOfDrawer(__instance);
            if (map == null || GroundRowsOf(map) <= 0)
            {
                return;
            }
            if (loc.z >= GroundRowsOf(map) && (dirtyFlags & ((ulong)RimWorld.MapMeshFlagDefOf.Buildings | (ulong)RimWorld.MapMeshFlagDefOf.Terrain)) != 0)
            {
                WallBoxes.OpenAirChanged(map);   // (CliffLook.cs) a floor or a block upstairs: which open air is a hole may change
            }
            if (loc.z >= GroundRowsOf(map) && (dirtyFlags & (ulong)RimWorld.MapMeshFlagDefOf.Terrain) != 0)
            {
                // a floor laid or taken up: the cell below has (or lost) its floor right above (cell bit 8, the per-storey fog lid)
                IntVec3 below = Stacked(map, loc, -1);
                if (below.IsValid && below.InBounds(map))
                {
                    IsoCellMap.Dirty(map, CellRect.SingleCell(below));
                }
            }
            dirtying = true;
            try
            {
                ulong flags = (ulong)RimWorld.MapMeshFlagDefOf.Buildings | (ulong)RimWorld.MapMeshFlagDefOf.Roofs;
                SlabDirty(__instance, loc);
                for (int dk = -1; dk <= 1; dk += 2)
                {
                    IntVec3 o = Stacked(map, loc, dk);
                    if (o.IsValid && o.InBounds(map))
                    {
                        // PROTOTYPE (CliffLook.cs): the floor's terrain above follows the rock's chamfer below
                        ulong extra = dk > 0 && WallBoxes.CliffMode > 0 && (dirtyFlags & (ulong)RimWorld.MapMeshFlagDefOf.Buildings) != 0
                            ? (ulong)RimWorld.MapMeshFlagDefOf.Terrain : 0UL;
                        // a wall drawn from a model below is printed with the things (the game's layer): it is remade too, its
                        // crown left out under the floor above or shown again (WallBoxes.ModelWall)
                        Building wb = dk < 0 ? o.GetEdifice(map) : null;
                        if (wb != null && Forms.For(wb.def).wallTop > 0f)
                        {
                            extra |= (ulong)RimWorld.MapMeshFlagDefOf.Things;
                        }
                        __instance.MapMeshDirty(o, flags | extra);
                        SlabDirty(__instance, o);
                    }
                }
            }
            finally
            {
                dirtying = false;
            }
        }

        private static readonly AccessTools.FieldRef<MapDrawer, Map> mapOfDrawer = AccessTools.FieldRefAccess<MapDrawer, Map>("map");
        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> drawerSections = AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        // The slab of the block holding a cell is built again (it leaves out cells with a wall through both storeys).
        private static void SlabDirty(MapDrawer drawer, IntVec3 c)
        {
            Section s = c.InBounds(mapOfDrawer(drawer)) ? drawer.SectionAt(c) : null;
            Slab slab;
            if (s != null && slabs.TryGetValue(s, out slab))
            {
                slab.dirty = true;
            }
        }
    }
}
