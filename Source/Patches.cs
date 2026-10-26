using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Every hook into the game lives here. Each one is applied on its own, so if a game update
    // renames something, that one feature logs a warning and switches itself off while the rest
    // keep working. With the 3D view off, every hook passes straight through to the original.
    public static class Patches
    {
        private static Harmony harmony;
        private static AccessTools.FieldRef<PawnRenderer, Pawn> pawnOfRenderer;
        private static readonly List<FieldInfo> dragFields = new List<FieldInfo>();   // the game's own camera movement, cleared while the mod drags
        private static bool dragging;
        private static Vector3 dragAnchor;     // the point on the ground that was under the pointer when the drag began
        private static Vector3 dragStartPos;   // where the camera was at the start of this frame

        // Set while a standing pawn is being drawn: where its feet are and how big it is
        // compared with a person.
        private static bool pawnUpright;
        private static Pawn uprightPawn;     // the pawn pawnUpright is about
        private static Vector3 pawnFeet;
        private static float pawnLift;       // an upright card sitting on a seat (Poses): how far it is raised
        private static float pawnSize = 1f;
        private const float HandHeight = 0.55f;   // up the sprite from the feet, in cells
        private const float HandReach = 0.2f;     // how far in front of the pawn the weapon is held
        private static readonly Dictionary<Type, int> layerKinds = new Dictionary<Type, int>();

        // Set once ApplyAll has run (a floor layout registered later hooks its own drawing then, Floors).
        public static bool Ready;
        public const string HarmonyId = "isorim.core";

        public static void ApplyAll()
        {
            harmony = new Harmony(HarmonyId);
            Probe.BiomeHook(harmony);   // harness only (-isorimbiome=)

            Patch("camera", AccessTools.Method(typeof(CameraDriver), "Update"),
                postfix: nameof(CameraUpdate_Postfix));

            // Each game starts with the 3D view off unless an addon locks it on: the last game's switch is not kept.
            Patch("3D view state per game", AccessTools.Constructor(typeof(Game)), postfix: nameof(GameCtor_Postfix));
            Patch("temporary camera state on leaving play", AccessTools.Method(typeof(Root), "Update"),
                postfix: nameof(RootUpdate_Postfix));
            // A game that starts in 3D (locked on) gets its map built for 3D inside the loading screen, not after it.
            Patch("3D view built while loading", AccessTools.Method(typeof(MapDrawer), "RegenerateEverythingNow"),
                prefix: nameof(RegenerateEverythingNow_Prefix));

            // Moving the camera: the game moves it along the map's own axes, which only match
            // the screen while north is up.
            Patch("camera movement in the isometric view", AccessTools.Method(typeof(CameraDriver), "CalculateCurInputDollyVect"),
                postfix: nameof(DollyVect_Postfix));
            // Dragging the map with the middle mouse button: done by the mod while the 3D view is on,
            // so the ground stays under the pointer whichever way the camera is turned.
            foreach (string name in new[] { "desiredDollyRaw", "desiredDolly", "mouseDragVect" })
            {
                FieldInfo f = AccessTools.Field(typeof(CameraDriver), name);
                if (f != null && f.FieldType == typeof(Vector2))
                {
                    dragFields.Add(f);
                }
            }
            Patch("map dragging", AccessTools.Method(typeof(CameraDriver), "Update"), prefix: nameof(CameraUpdate_Prefix));

            // Dragging a selection box, and the name labels under colonists.
            Type selection = AccessTools.TypeByName("RimWorld.ThingSelectionUtility");
            Patch("box selection in the isometric view", selection == null ? null : AccessTools.Method(selection, "MultiSelectableThingsInScreenRectDistinct"),
                postfix: nameof(DragSelect_Postfix));
            Patch("box selection in the isometric view", selection == null ? null : AccessTools.Method(selection, "MultiSelectableZonesInScreenRectDistinct"),
                postfix: nameof(DragSelectZones_Postfix));
            Patch("box selection in the isometric view", selection == null ? null : AccessTools.Method(selection, "MultiSelectablePlansInScreenRectDistinct"),
                postfix: nameof(DragSelectPlans_Postfix));
            Patch("box selection in the isometric view", AccessTools.Method(typeof(DragBox), nameof(DragBox.Contains), new[] { typeof(Thing) }),
                prefix: nameof(DragBoxContains_Prefix));
            Patch("box selection in the isometric view", AccessTools.PropertyGetter(typeof(DragBox), nameof(DragBox.IsValid)),
                prefix: nameof(DragBoxIsValid_Prefix));
            Patch("name labels in the isometric view", AccessTools.Method(typeof(GenMapUI), "LabelDrawPosFor", new[] { typeof(Thing), typeof(float) }),
                prefix: nameof(LabelDrawPosFor_Prefix));

            // Far labels fade out with the perspective camera (stack counts, quality, names).
            Patch("far labels", AccessTools.Method(typeof(GenMapUI), "DrawThingLabel", new[] { typeof(Thing), typeof(string), typeof(Color) }),
                prefix: nameof(DrawThingLabel_Prefix));
            Patch("far labels", AccessTools.Method(typeof(GenMapUI), "DrawPawnLabel", new[] { typeof(Pawn), typeof(Vector2), typeof(float), typeof(float),
                typeof(Dictionary<string, string>), typeof(GameFont), typeof(bool), typeof(bool) }),
                prefix: nameof(DrawPawnLabel_Prefix));

            // Rain, snow and fog: stood up in front of the camera instead of lying on the ground (Weather).
            Patch("weather", AccessTools.Method(typeof(SkyOverlay), "DrawWorldOverlay", new[] { typeof(Map), typeof(Material), typeof(float), typeof(int) }),
                prefix: nameof(DrawWorldOverlay_Prefix));
            Patch("weather", AccessTools.Method(typeof(SkyOverlay), "DrawScreenOverlay", new[] { typeof(Material), typeof(float), typeof(int), typeof(Camera) }),
                prefix: nameof(DrawScreenOverlay_Prefix));
            // The short overloads too: the JIT can inline the long ones into them, past the hooks above.
            Patch("weather", AccessTools.Method(typeof(SkyOverlay), "DrawWorldOverlay", new[] { typeof(Map), typeof(Material), typeof(int) }),
                prefix: nameof(DrawWorldOverlayShort_Prefix));
            Patch("weather", AccessTools.Method(typeof(SkyOverlay), "DrawScreenOverlay", new[] { typeof(Material), typeof(int), typeof(Camera) }),
                prefix: nameof(DrawScreenOverlayShort_Prefix));
            // Rain and snow fall in 3D (Falling): each weather overlay's own DrawOverlay is replaced. Only the classes that
            // declare one: the many that inherit WeatherOverlayDualPanner's are hooked once there.
            HashSet<MethodInfo> overlayDraws = new HashSet<MethodInfo>();
            foreach (Type t in GenTypes.AllSubclassesNonAbstract(typeof(SkyOverlay)))
            {
                MethodInfo m = AccessTools.DeclaredMethod(t, "DrawOverlay", new[] { typeof(Map) });
                if (m != null && !m.IsAbstract && overlayDraws.Add(m))
                {
                    Patch("weather", m, prefix: nameof(SkyOverlayDraw_Prefix));
                }
            }

            Patch("mouse position", AccessTools.Method(typeof(Verse.UI), "UIToMapPosition", new[] { typeof(Vector2) }),
                prefix: nameof(UIToMapPosition_Prefix));

            Patch("visible area", AccessTools.PropertyGetter(typeof(CameraDriver), "CurrentViewRect"),
                postfix: nameof(CurrentViewRect_Postfix));

            Type thingsLayer = AccessTools.TypeByName("Verse.SectionLayer_ThingsGeneral");
            Patch("3D shapes", thingsLayer == null ? null : AccessTools.Method(thingsLayer, "TakePrintFrom"),
                prefix: nameof(TakePrintFrom_Prefix), finalizer: nameof(TakePrintFrom_Finalizer));

            bool anyPrintPlane = false;
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(Printer_Plane)))
            {
                // 1.7: PrintPlane takes the base class MapDrawLayer (SectionLayer derives from it).
                if (m.Name == "PrintPlane" && StartsWith(m, typeof(MapDrawLayer), typeof(Vector3), typeof(Vector2), typeof(Material), typeof(float)))
                {
                    anyPrintPlane |= Patch("3D shapes and upright plants", m, postfix: nameof(PrintPlane_Postfix));
                }
            }
            if (!anyPrintPlane)
            {
                ModLog.Warn("[IsoRim] Printer_Plane.PrintPlane was not found in the expected form. Nothing can be raised and plants stay flat.");
            }

            // 1.7: DrawLayer is declared on MapDrawLayer and SectionLayer does not override it, so the
            // declared method is patched; the prefix passes non-section layers through.
            Patch("lighting and fog", AccessTools.DeclaredMethod(typeof(MapDrawLayer), "DrawLayer"),
                prefix: nameof(DrawLayer_Prefix));

            try
            {
                pawnOfRenderer = AccessTools.FieldRefAccess<PawnRenderer, Pawn>("pawn");
            }
            catch (Exception)
            {
                pawnOfRenderer = null;
            }
            if (pawnOfRenderer == null)
            {
                missed.Add("upright pawns (PawnRenderer.pawn)");
            }
            Patch("upright pawns", AccessTools.Method(typeof(PawnRenderer), "RenderPawnAt"),
                prefix: nameof(RenderPawnAt_Prefix), finalizer: nameof(RenderPawnAt_Finalizer));

            // Wounds on 3D figures and animals (WoundMarks) are worked out again when the game clears its own wound cache.
            Patch("wounds on 3D figures", AccessTools.Method(typeof(PawnOverlayDrawer), "ClearCache"), postfix: nameof(WoundCacheCleared_Postfix));

            // The game works out each pawn's sprite layers ahead of drawing, on worker threads.
            // For a pawn the mod draws as a 3D model that work is wasted, so it is skipped.
            MethodInfo preRender = FindMethod(typeof(PawnRenderer), "ParallelPreRenderPawnAt");
            if (preRender != null)
            {
                Patch("skipping sprite work for 3D pawns", preRender, prefix: nameof(PreRender_Prefix));
            }

            // In 3D the game's turn of a drafted pawn south is skipped: it keeps the way it walked, aimed or its job faces (DraftedSouth).
            Patch("drafted pawns keep their facing", AccessTools.Method(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.UpdateRotation)),
                transpiler: nameof(DraftedSouth_Transpiler));

            // The facing a standing pawn's sprite is drawn with, turned with the camera (View3D.SeenFacing): every
            // layer of the sprite (body, head, apparel, hair, carried weapon, overlays) takes it from here.
            MethodInfo preRenderResults = FindMethod(typeof(PawnRenderer), "ParallelGetPreRenderResults");
            if (preRenderResults != null && preRenderResults.GetParameters().Length > 1 && preRenderResults.GetParameters()[1].ParameterType == typeof(Rot4?))
            {
                Patch("upright pawn sprites facing the camera", preRenderResults, prefix: nameof(PreRenderResultsCaller_Prefix));
                Patch("upright pawn sprites facing the camera", preRenderResults, prefix: nameof(PreRenderResults_Prefix));
            }
            else
            {
                missed.Add("upright pawn sprites facing the camera (PawnRenderer.ParallelGetPreRenderResults)");
            }
            // People drawn in full rather than from the game's cached pawn image (option, with Facial Animation only):
            // the cached image skips the render tree, so animated faces stood still.
            if (preRenderResults != null && ModsConfig.IsActive("Nals.FacialAnimation") && Array.Exists(preRenderResults.GetParameters(), p => p.Name == "disableCache" && p.ParameterType == typeof(bool)))
            {
                Patch("sprites drawn in full", preRenderResults, prefix: nameof(PreRenderCache_Prefix));
            }
            Patch("upright pawn sprites facing the camera", AccessTools.Method(typeof(PawnRenderUtility), nameof(PawnRenderUtility.CalculateCarriedDrawPos)),
                prefix: nameof(CarriedDrawPos_Prefix), postfix: nameof(CarriedDrawPos_Postfix));

            // Walls, rock and trees live in the mod's own layers. Whenever one appears or goes
            // (built, deconstructed, mined, cut down) those layers are redrawn round it.
            Patch("redrawing walls and trees as they change", FindMethod(typeof(Thing), "SpawnSetup"), postfix: nameof(SpawnSetup_Postfix));
            Patch("redrawing walls and trees as they go", FindMethod(typeof(Thing), "DeSpawn"), prefix: nameof(DeSpawn_Prefix));
            // A tree block is redrawn only for a change in a cell with a tree (SectionLayer_IsoRimTrees); a fog change marks
            // its cell for the cell texture (IsoCellMap.FogDirty_Postfix).
            Patch("redrawing trees as they change", AccessTools.Method(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty), new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) }),
                postfix: nameof(TreesDirty_Postfix));
            // Rock marked for mining is tinted (MineMarks): marking or unmarking a cell redraws its block.
            Patch("tinting rock marked for mining", AccessTools.Method(typeof(DesignationManager), nameof(DesignationManager.AddDesignation)),
                postfix: nameof(MineDesignation_Postfix));
            Patch("tinting rock marked for mining", AccessTools.Method(typeof(DesignationManager), nameof(DesignationManager.RemoveDesignation)),
                postfix: nameof(MineUndesignation_Postfix));

            // Sunk holes (pits, SinkHoles.cs): the layers the game paints on the ground leave out the cells a hole cuts.
            Patch("ground cut under sunk holes", FindMethod(typeof(SectionLayer_Terrain), "Regenerate"), postfix: nameof(SinkCut_Postfix));
            Patch("ground cut under sunk holes", FindMethod(typeof(SectionLayer_TerrainEdges), "Regenerate"), postfix: nameof(SinkCut_Postfix));
            Patch("ground cut under sunk holes", FindMethod(typeof(SectionLayer_TerrainScatter), "Regenerate"), postfix: nameof(SinkCut_Postfix));
            Patch("ground cut under sunk holes", FindMethod(typeof(SectionLayer_Snow), "Regenerate"), postfix: nameof(SinkCutColours_Postfix));
            Patch("ground cut under sunk holes", FindMethod(typeof(SectionLayer_Sand), "Regenerate"), postfix: nameof(SinkCutColours_Postfix));

            // Bridge decks (Bridges): the ground layers' pieces on a deck are lifted onto it, the game's flat props sprite
            // is left out (the 3D supports stand in), and things drawn every frame are lifted (Floors.ThingPhase_Prefix:
            // also a pawn's rise on a stair step and things on hidden floors, with a floor layout).
            Patch("bridge decks", FindMethod(typeof(SectionLayer_Terrain), "Regenerate"), postfix: nameof(BridgeGround_Postfix));
            Patch("bridge decks", FindMethod(typeof(SectionLayer_Snow), "Regenerate"), postfix: nameof(BridgeGround_Postfix));
            Patch("bridge decks", FindMethod(typeof(SectionLayer_Sand), "Regenerate"), postfix: nameof(BridgeGround_Postfix));
            Patch("bridge decks", FindMethod(typeof(SectionLayer_Plans), "Regenerate"), postfix: nameof(BridgeGround_Postfix));
            Type zonesLayer = AccessTools.TypeByName("Verse.SectionLayer_Zones");
            Patch("bridge decks", zonesLayer != null ? FindMethod(zonesLayer, "Regenerate") : null, postfix: nameof(BridgeGround_Postfix));
            Patch("bridge decks", FindMethod(typeof(SectionLayer_BridgeProps), "Regenerate"), postfix: nameof(BridgeProps_Postfix));
            Patch("things on bridge decks", AccessTools.Method(typeof(Thing), nameof(Thing.DynamicDrawPhase)),
                prefix: nameof(ThingLift_Prefix), finalizer: nameof(ThingLift_Finalizer));

            // The preview of what is being placed, under the pointer.
            Type ghostDrawer = AccessTools.TypeByName("Verse.GhostDrawer");
            MethodInfo ghostThing = ghostDrawer != null ? FindMethod(ghostDrawer, "DrawGhostThing") : null;
            if (ghostThing != null && (!StartsWith(ghostThing, typeof(IntVec3), typeof(Rot4), typeof(ThingDef), typeof(Graphic), typeof(Color))
                || ghostThing.GetParameters().Length < 9 || ghostThing.GetParameters()[8].ParameterType != typeof(ThingDef)))
            {
                ghostThing = null;
            }
            Patch("3D placement preview", ghostThing, postfix: nameof(DrawGhostThing_Postfix));
            // Where the 3D preview stands in, the game's own flat ghost sprite is left out (only that call; the comps' and
            // place workers' ghosts stay): Ghosts.DrawGameGhostSprite.
            Patch("3D placement preview without the flat sprite", ghostThing, transpiler: nameof(GhostSprite_Transpiler));
            // Blueprints and frames drawn every frame whose flat sprite would lie in a wall: their 3D ghost (Ghosts.DrawPlanned).
            Patch("3D ghosts of things to be built in walls", AccessTools.Method(typeof(Blueprint), "DrawAt"), prefix: nameof(BlueprintDraw_Prefix));
            Patch("3D ghosts of things to be built in walls", AccessTools.Method(typeof(Frame), "DrawAt"), postfix: nameof(FrameDraw_Postfix));

            // The pointer in 3D: selection brackets on the ground, and clicking anywhere on a tall
            // thing picks it. (The selection drag box is the game's own: a plain rectangle on screen.)
            MethodInfo bracket = FindMethod(typeof(SelectionDrawer), "DrawSelectionBracketFor");
            if (bracket != null && !StartsWith(bracket, typeof(object)))
            {
                bracket = null;
            }
            Patch("selection on the ground", bracket, prefix: nameof(DrawSelectionBracketFor_Prefix));
            MethodInfo under = FindMethod(typeof(GenUI), "ThingsUnderMouse");
            if (under != null && !StartsWith(under, typeof(Vector3), typeof(float), typeof(TargetingParameters)))
            {
                under = null;
            }
            Patch("clicking on 3D shapes", under, postfix: nameof(ThingsUnderMouse_Postfix));

            // Designation icons float over what they mark (their drawing is redirected above).
            Patch("floating designation icons", FindMethod(typeof(Designation), "DesignationDraw"),
                prefix: nameof(DesignationDraw_Prefix), finalizer: nameof(Scope_Finalizer));
            // Cell designations (mine, smooth ...) are drawn in one instanced batch on the ground,
            // where they would be inside the rock; in 3D they are drawn one by one like the rest.
            Patch("floating designation icons", AccessTools.Method(typeof(DesignationManager), "DrawDesignations"),
                prefix: nameof(DrawDesignations_Prefix), finalizer: nameof(DrawDesignations_Finalizer));
            Type drawBatch = AccessTools.TypeByName("Verse.DrawBatch");
            if (drawBatch != null)
            {
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(drawBatch))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (m.Name == "DrawMesh" && ps.Length > 1 && ps[1].ParameterType == typeof(Matrix4x4) && !m.IsStatic)
                    {
                        Patch("floating batched icons", m, prefix: nameof(DrawBatch_Prefix));
                    }
                }
            }

            // The faint screen-aligned box the game draws round a designation drag is replaced by
            // one on the ground (Interaction.DrawDesignationDrag); its measurement labels are kept.
            Patch("drag area on the ground", FindMethod(typeof(DesignationDragger), "DraggerOnGUI"),
                prefix: nameof(DraggerOnGUI_Prefix), finalizer: nameof(DraggerOnGUI_Finalizer));
            MethodInfo drawBox = AccessTools.Method(typeof(Widgets), "DrawBox", new[] { typeof(Rect), typeof(int), typeof(Texture2D) });
            Patch("drag area on the ground", drawBox, prefix: nameof(WidgetsDrawBox_Prefix));

            // Mining, deconstructing and smoothing walls aim at the block the pointer shows, not the
            // ground cell behind its top (Interaction.DesignatorMouseCell).
            Patch("designators aim at blocks", AccessTools.Method(typeof(DesignatorManager), "ProcessInputEvents"), transpiler: nameof(DesignatorCell_Transpiler));
            Patch("designators aim at blocks", AccessTools.Method(typeof(DesignationDragger), "StartDrag"), transpiler: nameof(DesignatorCell_Transpiler));
            Patch("designators aim at blocks", AccessTools.Method(typeof(DesignationDragger), "GetCurrentBoundary"), transpiler: nameof(DesignatorCell_Transpiler));
            Patch("designators aim at blocks", AccessTools.Method(typeof(GenUI), "RenderMouseoverBracket"), prefix: nameof(MouseoverBracket_Prefix));
            Patch("designators aim at blocks", AccessTools.Method(typeof(DesignatorUtility), "RenderHighlightOverSelectableCells"), prefix: nameof(HighlightCells_Prefix));
            Patch("designators aim at blocks", AccessTools.Method(typeof(DesignatorUtility), "RenderHighlightOverSelectableThings"), prefix: nameof(HighlightThings_Prefix));

            // The game lays dark sheets round the outside of the map to hide whatever lies past
            // its edge. In 3D they would cover the cut through the earth below the edge.
            Type clipper = AccessTools.TypeByName("Verse.MapEdgeClipDrawer");
            Patch("earth below the map edge", clipper != null ? FindMethod(clipper, "DrawClippers") : null, prefix: nameof(DrawClippers_Prefix));

            // 1.7 draws nothing past the map edge on normal maps (only maps without the edge
            // sheets, such as orbit, do; the earth edge is not shown there, see SectionLayer_IsoRimEdge).

            Diagnostics();

            // Crash trail (log only): a modal window opened while the 3D view is on gets one line in the
            // player's log, so a log that ends in a graphics crash shows whether a dialog had just opened.
            Patch("crash trail", AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)), postfix: nameof(WindowStackAdd_Postfix));

            // The game's flat outlines of things seen through walls are made from the sprite work
            // skipped above, and do not fit 3D figures anyway, so they are left out in 3D.
            MethodInfo silhouettes = FindMethod(typeof(DynamicDrawManager), "DrawSilhouettes");
            if (silhouettes != null)
            {
                Patch("no flat silhouettes in 3D", silhouettes, prefix: nameof(DrawSilhouettes_Prefix));
            }
            // The same for turrets, the shuttle and other things with a silhouette; Silhouettes3D draws all of them upright.
            Patch("no flat thing silhouettes in 3D", AccessTools.Method(typeof(SilhouetteUtility), nameof(SilhouetteUtility.DrawGraphicSilhouette)), prefix: nameof(DrawSilhouettes_Prefix));

            // Parts the game draws every frame on top of a building (fire, charge bars, turret
            // heads) and the status icons over things have to move up with the raised sprite.
            Patch("moving parts of raised buildings", FindMethod(typeof(Thing), "DynamicDrawPhaseAt"),
                prefix: nameof(ThingDraw_Prefix), finalizer: nameof(Scope_Finalizer));

            Type overlays = AccessTools.TypeByName("RimWorld.OverlayDrawer");
            int overlayHooks = 0;
            if (overlays != null)
            {
                // One scope per thing round everything the overlay pass draws for it (StatusIcons): the game's own
                // icons and any a mod adds into that pass (its own overlay types drawn into the same batch).
                MethodInfo drawAll = FindMethod(overlays, "DrawAllOverlays");
                if (drawAll != null && Patch("status icons", drawAll, prefix: nameof(DrawAllOverlays_Prefix),
                    finalizer: nameof(DrawAllOverlays_Finalizer), transpiler: nameof(DrawAllOverlays_Transpiler)) && statusPerThing)
                {
                    overlayHooks++;
                }
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(overlays))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (m.Name.StartsWith("Render") && ps.Length > 0 && ps[0].ParameterType == typeof(Thing)
                        && !m.IsAbstract && !m.ContainsGenericParameters)
                    {
                        bool forbidden = m.Name.Contains("Forbidden") && !m.Name.Contains("Big") && !m.Name.Contains("Refuel");
                        if (!forbidden && statusPerThing)
                        {
                            continue;   // inside the per-thing scope above
                        }
                        bool hooked = forbidden
                            ? Patch("forbidden icons", m, prefix: nameof(ForbiddenOverlay_Prefix))
                            : Patch("status icons", m, prefix: nameof(OverlayRender_Prefix), finalizer: nameof(Scope_Finalizer));
                        if (hooked)
                        {
                            overlayHooks++;
                        }
                    }
                }
            }
            if (overlayHooks == 0)
            {
                ModLog.Warn("[IsoRim] The game's status icon drawing was not found. Icons such as \"no power\" may be hidden inside raised buildings.");
            }

            Type renderUtility = AccessTools.TypeByName("Verse.PawnRenderUtility");
            MethodInfo aiming = renderUtility != null ? FindMethod(renderUtility, "DrawEquipmentAiming") : null;
            if (aiming != null && !StartsWith(aiming, typeof(Thing), typeof(Vector3), typeof(float)))
            {
                aiming = null;
            }
            Patch("3D weapons in hand", aiming, prefix: nameof(DrawEquipmentAiming_Prefix), finalizer: nameof(DrawEquipmentAiming_Finalizer));
            // A 3D figure aiming a gun shows its aim pie at the muzzle (AimPies).
            Patch("aim pies at the muzzle", AccessTools.Method(typeof(GenDraw), nameof(GenDraw.DrawAimPie)), prefix: nameof(DrawAimPie_Prefix));
            // A 3D figure's weapon is posed by the game (FigureWeapons): which pose it chose, and the shield belt's bubble caught.
            Patch("3D weapons carried", renderUtility != null ? FindMethod(renderUtility, "DrawCarriedWeapon") : null,
                prefix: nameof(DrawCarriedWeapon_Prefix), finalizer: nameof(DrawCarriedWeapon_Finalizer));
            Patch("shield bubbles on 3D figures", AccessTools.DeclaredMethod(typeof(CompShield), "Draw"), transpiler: nameof(CatchDraws_Transpiler));

            // Wind turbines: the blades are a turning 3D model.
            Type wind = AccessTools.TypeByName("RimWorld.CompPowerPlantWind");
            Patch("wind turbine blades", wind == null ? null : AccessTools.DeclaredMethod(wind, "PostDraw"),
                prefix: nameof(WindPostDraw_Prefix), finalizer: nameof(WindPostDraw_Finalizer));

            // Walls are cut down in front of roofed rooms, so they have to be redrawn when a roof changes.
            Patch("cut-down walls keeping up with roofs", AccessTools.Method(typeof(RoofGrid), "SetRoof"),
                postfix: nameof(SetRoof_Postfix));


            // The water's colour bands reach a few cells from the shore, into the next blocks of the
            // map, which the game does not redraw for a change of terrain.
            Patch("water keeping up with the shore", AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
                postfix: nameof(TerrainChanged_Postfix));

            MethodInfo movers = FindMethod(typeof(Building_Door), "DrawMovers");
            if (movers != null && !StartsWith(movers, typeof(Vector3), typeof(float)))
            {
                movers = null;
            }
            Patch("3D doors", movers, prefix: nameof(DrawMovers_Prefix));
            // Doors wider than a cell with their own models (ornate door): frame, arch and two sliding leaves (Doors.DrawWide).
            Patch("3D wide doors", AccessTools.DeclaredMethod(typeof(Building_MultiTileDoor), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(MultiTileDoorDrawAt_Prefix));
            // One-cell supported doors with their own models (vac barrier, ancient blast door): frame, lintel,
            // leaves and hacking terminal (Doors.DrawSupported); the game's flat terminal is then left out.
            Patch("3D supported doors", AccessTools.DeclaredMethod(typeof(Building_SupportedDoor), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(SupportedDoorDrawAt_Prefix));
            // (Api 30) a vac barrier's field as its door's glass part
            Patch("3D vac barrier fields", AccessTools.DeclaredMethod(typeof(Building_VacBarrier), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(VacBarrierDrawAt_Prefix), finalizer: nameof(VacBarrierDrawAt_Finalizer));
            Patch("3D vac barrier fields", AccessTools.Method(typeof(Graphic), nameof(Graphic.Draw), new[] { typeof(Vector3), typeof(Rot4), typeof(Thing), typeof(float) }),
                prefix: nameof(GraphicDraw_Prefix));
            Patch("3D supported doors", AccessTools.DeclaredMethod(typeof(CompHackable), "PostDraw"),
                prefix: nameof(HackablePostDraw_Prefix));

            // Buildings the game draws every frame (campfire, torches, caskets): their model is baked
            // into the map mesh (SectionLayer_IsoRimBodies); only the body sprite is held back, and
            // the flat sprites that would double what the model shows (an open casket's lid, the
            // fire over a torch whose model has its own flame).
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(Thing), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(ThingDrawAt_Prefix));
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(CompEmptyStateGraphic), "PostDraw"),
                prefix: nameof(EmptyStatePostDraw_Prefix));
            // The game's flat fire and darklight sprites are left out where the model has its own flame (FlameStandsIn).
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(CompFireOverlay), "PostDraw"),
                prefix: nameof(FireOverlayPostDraw_Prefix), finalizer: nameof(FireOverlayPostDraw_Finalizer));
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(CompDarklightOverlay), "PostDraw"),
                prefix: nameof(FireOverlayPostDraw_Prefix), finalizer: nameof(FireOverlayPostDraw_Finalizer));
            // Burning fires (grass, buildings, items, corpses, pawns on fire): the game draws every one with its flat
            // flickering sprite; in 3D that draw is the campfire's flame (Effects.BurningFire).
            Patch("3D burning fires", AccessTools.DeclaredMethod(typeof(Graphic_Flicker), "DrawWorker"),
                prefix: nameof(FlickerDraw_Prefix));
            // Ritual fires (pyre, effigy, sacrificial flag, burnbong): their live flame is Effects'.
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(CompRitualFireOverlay), "PostDraw"),
                prefix: nameof(FireOverlayPostDraw_Prefix), finalizer: nameof(FireOverlayPostDraw_Finalizer));
            // A bookcase draws its books and bookends itself after its body; with the model, the
            // books are baked onto its shelves (Bookcases), rebuilt when one goes in or out.
            Patch("3D bookcases", AccessTools.DeclaredMethod(typeof(Building_Bookcase), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(BookcaseDrawAt_Prefix));
            Patch("3D bookcases", AccessTools.DeclaredMethod(typeof(Building_Bookcase), "Notify_ItemAdded"),
                postfix: nameof(BookcaseContents_Postfix));
            Patch("3D bookcases", AccessTools.DeclaredMethod(typeof(Building_Bookcase), "Notify_ItemRemoved"),
                postfix: nameof(BookcaseContents_Postfix));
            // Flat sprites drawn every frame over contents that the 3D model replaces (gibbet cage top, dryad sphere front).
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(Building_GibbetCage), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(GibbetDrawAt_Prefix));
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(CaveExit), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(CaveExitDrawAt_Prefix));
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(CompDryadHolder), "PostDraw"),
                prefix: nameof(DryadHolderPostDraw_Prefix));
            // Glass over contents (Glass.cs): the flat glass, top and window sprites are left out, the contents placed in the chamber.
            Patch("3D glass", AccessTools.DeclaredMethod(typeof(Building_MechGestator), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(MechGestatorDrawAt_Prefix));
            Patch("3D glass", AccessTools.DeclaredMethod(typeof(Building_GrowthVat), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(GrowthVatDrawAt_Prefix));
            Patch("3D glass", AccessTools.DeclaredMethod(typeof(Building_WastepackAtomizer), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(AtomizerDrawAt_Prefix));
            Patch("3D glass", AccessTools.DeclaredMethod(typeof(CompBiosculpterPod), "PostDraw"),
                prefix: nameof(BiosculpterPostDraw_Prefix));
            // Holding platform with its model: the restraint chains in 3D from its eye rings (HoldingChains.cs).
            Patch("3D holding platform chains", AccessTools.DeclaredMethod(typeof(Building_HoldingPlatform), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(HoldingPlatformDrawAt_Prefix));
            // Outfit stands with their model: head model, the apparel and weapon stood upright over it (Stands.cs).
            Patch("3D outfit stands", AccessTools.DeclaredMethod(typeof(Building_OutfitStand), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(OutfitStandDrawAt_Prefix));
            // Extra per-frame sprites the model already has as parts (the ancient stockpile exit's ladder).
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(CompDrawAdditionalGraphics), "PostDraw"),
                prefix: nameof(DrawAdditionalGraphicsPostDraw_Prefix));
            // Harvester cables run up to the model's connectors (WorkParts.cs).
            Patch("3D cables", AccessTools.DeclaredMethod(typeof(CompCableConnection), "PostDraw"),
                prefix: nameof(CableConnectionPostDraw_Prefix));
            // A new paint colour: the game redraws only things in the map mesh.
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(Thing), "Notify_ColorChanged"),
                postfix: nameof(BodyColorChanged_Postfix));

            // Parts that turn in 3D (the lightball's ball, Spinners): the game's flat mote for them is left out.
            Patch("3D turning parts", AccessTools.DeclaredMethod(typeof(Mote), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(MoteDrawAt_Prefix));

            // Work progress bars: hung over the thing worked on without the game's southward offset, shown farther out (Bars).
            MethodInfo barDraw = AccessTools.DeclaredMethod(typeof(MoteProgressBar), "DrawAt", new[] { typeof(Vector3), typeof(bool) });
            if (barDraw == null)
            {
                missed.Add("progress bars (not found)");
            }
            else
            {
                try
                {
                    harmony.Patch(barDraw, prefix: new HarmonyMethod(typeof(Bars), nameof(Bars.DrawAt_Prefix)),
                        finalizer: new HarmonyMethod(typeof(Bars), nameof(Bars.DrawAt_Finalizer)));
                    hooked.Add("MoteProgressBar.DrawAt");
                }
                catch (Exception e)
                {
                    missed.Add("MoteProgressBar.DrawAt (" + e.Message + ")");
                    ModLog.Warn("[IsoRim] Could not hook MoteProgressBar.DrawAt for progress bars: " + e.Message);
                }
            }

            // Parts that bob in 3D (the grav engine's orb, Bobbers): the game's flat orb is left out while the model stands in.
            Patch("3D bobbing parts", AccessTools.DeclaredMethod(typeof(Building_GravEngine), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(GravEngineDrawAt_Prefix));
            Patch("3D bobbing parts", AccessTools.DeclaredMethod(typeof(CompCerebrexCore), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(CerebrexDrawAt_Prefix));
            Patch("3D bobbing parts", AccessTools.DeclaredMethod(typeof(Building_MechRelay), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(MechRelayDrawAt_Prefix));
            // Parts that beat in 3D (the fleshmass heart, Pulses): the game's three flat parts are left out while the model stands in.
            Patch("3D beating parts", AccessTools.DeclaredMethod(typeof(Building_FleshmassHeart), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(FleshmassHeartDrawAt_Prefix));

            // Statues (Statues.cs): the 3D base stands in for the flat base sprite, the subject stands on it as a 3D figure.
            Patch("3D statues", AccessTools.DeclaredMethod(typeof(CompStatue), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(StatueDrawAt_Prefix));

            // Shield bubbles: a dome over the circle instead of a flat ring on the ground (ShieldDomes).
            Patch("shield domes", AccessTools.DeclaredMethod(typeof(CompProjectileInterceptor), "PostDraw"), transpiler: nameof(ShieldDraw_Transpiler), postfix: nameof(ShieldDraw_Postfix));
            // Shots and shells a shield stops meet its dome: drawn on to it, the game's intercept effect shown there (ShieldHits).
            Patch("shots stopped on the dome", AccessTools.DeclaredMethod(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.CheckIntercept)),
                prefix: nameof(ShieldIntercept_Prefix), postfix: nameof(ShieldIntercept_Postfix), finalizer: nameof(ShieldIntercept_Finalizer));
            Patch("shots stopped on the dome", AccessTools.DeclaredMethod(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.CheckBombardmentIntercept)),
                prefix: nameof(ShieldBombardment_Prefix), finalizer: nameof(ShieldBombardment_Finalizer));
            Patch("shots stopped on the dome", AccessTools.DeclaredMethod(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.CompTick)), postfix: nameof(ShieldTick_Postfix));
            Patch("shots stopped on the dome", AccessTools.Method(typeof(FleckManager), nameof(FleckManager.CreateFleck), new[] { typeof(FleckCreationData) }), prefix: nameof(CreateFleck_Prefix));

            // Smoke and dust the game throws from a building: from the model's nozzles, standing up at their height (Emitters).
            Patch("smoke from 3D nozzles", AccessTools.Method(typeof(CompThrownFleckEmitter), "Emit"), prefix: nameof(FleckEmit_Prefix));
            Patch("steam from 3D nozzles", AccessTools.Method(typeof(IntermittentSteamSprayer), nameof(IntermittentSteamSprayer.SteamSprayerTick)),
                prefix: nameof(SprayerTick_Prefix), finalizer: nameof(SprayerTick_Finalizer));
            Patch("steam from 3D nozzles", AccessTools.Method(typeof(FleckMaker), nameof(FleckMaker.ThrowAirPuffUp)), prefix: nameof(AirPuff_Prefix));
            Patch("smoke from 3D nozzles", AccessTools.Method(typeof(FleckStatic), "Draw", new[] { typeof(float), typeof(DrawBatch) }), prefix: nameof(FleckDraw_Prefix), finalizer: nameof(FleckDraw_Finalizer));
            // Icons thrown from a pawn's cell (downed "!", sleep Zs, hearts ...) over the 3D figure's head (Overhead).
            Patch("icons over 3D figures' heads", AccessTools.Method(typeof(FleckManager), nameof(FleckManager.CreateFleck), new[] { typeof(FleckCreationData) }), prefix: nameof(OverheadFleck_Prefix));
            // A turret's flash a mod throws by hand at the turret's cell: at its 3D gun's barrel end (Projectiles.TurretFlash).
            Patch("muzzle flashes at the muzzle", AccessTools.Method(typeof(FleckManager), nameof(FleckManager.CreateFleck), new[] { typeof(FleckCreationData) }), prefix: nameof(TurretFlash_Prefix));

            // Turret guns: a 3D gun turning on its 3D base.
            Type turretTop = AccessTools.TypeByName("RimWorld.TurretTop");
            if (turretTop != null)
            {
                turretParent = AccessTools.Field(turretTop, "parentTurret");
                turretRotation = AccessTools.Property(turretTop, "CurRotation");
            }
            Patch("3D turret guns", turretTop == null || turretParent == null || turretRotation == null ? null : FindMethod(turretTop, "DrawTurret"),
                prefix: nameof(DrawTurret_Prefix));

            // Bullets and shells fly from the muzzle to the height of their target, not along the ground (Projectiles).
            Patch("projectiles at muzzle height", AccessTools.DeclaredMethod(typeof(Projectile), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(ProjectileDrawAt_Prefix));
            // Projectile classes of other mods that draw themselves (VFE Medieval 2's warbow arrow): IsoRim draws them only
            // when they are indirect fire, on the shell arc (Projectiles.OverrideDrawAt); everything else stays the mod's.
            foreach (Type sub in GenTypes.AllSubclasses(typeof(Projectile)))
            {
                MethodInfo own = AccessTools.DeclaredMethod(sub, "DrawAt", new[] { typeof(Vector3), typeof(bool) });
                if (own != null && !own.IsAbstract && own.DeclaringType == sub)
                {
                    Patch("indirect fire of other mods on the shell arc", own, prefix: nameof(ProjectileOverrideDrawAt_Prefix), finalizer: nameof(ProjectileOverrideDrawAt_Finalizer));
                    // (its own Graphics.DrawMesh calls through DrawHook, so they are dropped while IsoRim draws it on the arc)
                    TryRedirect(own, Hook(nameof(Redirect_Transpiler)));
                }
            }
            // Flecks other mods' projectiles spawn in their own DrawAt (trails) follow the projectile as drawn in 3D.
            Patch("projectile trails in 3D", AccessTools.Method(typeof(FleckManager), nameof(FleckManager.CreateFleck), new[] { typeof(FleckCreationData) }),
                prefix: nameof(ProjectileFleck_Prefix));
            // Drop pods, shuttles, meteorites and every other skyfaller come down from (or go up into) the sky with a real
            // height, not slid over the ground (Skyfallers).
            Patch("skyfallers from the sky", AccessTools.DeclaredMethod(typeof(Skyfaller), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(SkyfallerDrawAt_Prefix));
            Patch("skyfallers from the sky", AccessTools.DeclaredMethod(typeof(Skyfaller_FlyingPawn), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(SkyfallerDrawAt_Prefix));
            // Power beams, bombardments (and the orbital strike permits) and lightning stand up from where they hit into
            // the sky, not lie on the ground running north (SkyStrikes).
            Patch("strikes from the sky", AccessTools.DeclaredMethod(typeof(CompOrbitalBeam), "PostDraw"), prefix: nameof(OrbitalBeamPostDraw_Prefix));
            Patch("strikes from the sky", AccessTools.DeclaredMethod(typeof(Bombardment), "DrawAt", new[] { typeof(Vector3), typeof(bool) }), prefix: nameof(BombardmentDrawAt_Prefix));
            Patch("strikes from the sky", AccessTools.DeclaredMethod(typeof(WeatherEvent_LightningStrike), "WeatherEventDraw"), prefix: nameof(LightningDraw_Prefix));
            Patch("strikes from the sky", AccessTools.DeclaredMethod(typeof(WeatherEvent_LightningStrikeDelayed), "WeatherEventDraw"), prefix: nameof(LightningDraw_Prefix));
            // A shot's flash at the muzzle, standing up, not flat in the middle of the shooter (Projectiles.ShotFlash).
            Patch("muzzle flashes at the muzzle", AccessTools.DeclaredMethod(typeof(Verb), "TryCastNextBurstShot"), transpiler: nameof(ShotFlash_Transpiler));
            // A roof the game drops breaks into pieces that fall (CollapseDebris; after other mods' prefixes).
            CollapseDebris.Patch(harmony);

            // The buttons in the bottom right corner of the map view.
            Patch("buttons for the 3D view and roofs", AccessTools.Method(typeof(PlaySettings), "DoPlaySettingsGlobalControls"),
                postfix: nameof(PlaySettings_Postfix));

            // In 3D the sun stands in the south at noon (IsoRim's sun lights the faces turned toward
            // the camera), so the game's shadows have to fall north. This one value feeds the
            // shadow vector (_CastVect / SunDir: building and tree shadows, animal shadows, Dynamic
            // Pawn Shadows) and the area the game draws sun shadows for.
            Patch("shadows falling away from the 3D sun", AccessTools.Method(typeof(GenCelestial), "GetLightSourceInfo", new[] { typeof(Map), typeof(GenCelestial.LightType) }),
                postfix: nameof(GetLightSourceInfo_Postfix));
            // The game builds the moving edge of a building's shadow only on its west, east and
            // south sides (its shadows never point north). In 3D the north side gets one too.
            Type sunShadows = AccessTools.TypeByName("Verse.SectionLayer_SunShadows");
            Patch("shadows falling away from the 3D sun", sunShadows == null ? null : AccessTools.DeclaredMethod(sunShadows, "Regenerate"),
                postfix: nameof(SunShadowsRegenerate_Postfix));

            // Night, lamp and indoor light per face (IsoGlow): copy the corner colours of every block
            // of the game's lighting overlay it rebuilds, and hand them to the shaders before the map
            // is drawn (after this frame's rebuilds).
            Type lightingOverlay = AccessTools.TypeByName("Verse.SectionLayer_LightingOverlay");
            Patch("night and indoor light on raised things", lightingOverlay == null ? null : AccessTools.Method(lightingOverlay, "GenerateLightingOverlay"),
                postfix: nameof(GenerateLightingOverlay_Postfix));
            Patch("night and indoor light on raised things", AccessTools.Method(typeof(MapDrawer), "DrawMapMesh"),
                prefix: nameof(DrawMapMesh_Prefix));

            // Culling (Cull): map blocks and moving model parts wholly off screen are not drawn.
            Patch("culling", AccessTools.Method(typeof(MapDrawer), "DrawMapMesh"), transpiler: nameof(CullDrawMapMesh_Transpiler));
            Patch("culling", AccessTools.Method(typeof(LayerSubMesh), "FinalizeMesh"), postfix: nameof(CullFinalizeMesh_Postfix));
            // Roof clipping (RoofClip): models baked into the game's own layers carry texture coordinate 1, the rest of
            // the piece is filled up to one per vertex.
            Patch("things under roofs", AccessTools.Method(typeof(LayerSubMesh), "FinalizeMesh"), prefix: nameof(RoofFinalizeMesh_Prefix));
            Patch("culling", AccessTools.Method(typeof(DynamicDrawManager), "DrawDynamicThings"),
                prefix: nameof(CullThings_Prefix), finalizer: nameof(CullThings_Finalizer));
            Patch("culling", AccessTools.Method(typeof(Thing), "DynamicDrawPhase"), prefix: nameof(CullThing_Prefix), postfix: nameof(CullThing_Postfix));
            if (ModLog.Diag)
            {
                IsoGlow.Benchmark();
            }

            RedirectDrawCalls();

            // Held shields bound to a model (ApparelModelDef, Api 9): their own worn picture gives way to the model on 3D
            // figures (ApparelModels). Nothing is hooked when no shield is bound.
            try
            {
                ApparelModels.Patch(harmony);
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] Could not hook bound shields: " + e);
            }

            // Beams and streams drawn in 3D from one end to the other, on one floor and between stacked floors (FloorSprays).
            try
            {
                FloorSprays.Patch(harmony);
                hooked.Add("FloorSprays (beams and streams in 3D)");
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] Could not hook beams and streams: " + e);
            }

            // Stacked floors (Floors): only when a gameplay addon has registered a floor layout.
            Ready = true;
            Floors.ApplyPatches();

            // 1.7 port: one dev-mode line listing which hooks went in and which did not.
            ModLog.Note("[IsoRim] Hooks applied (" + hooked.Count + "): " + string.Join("; ", hooked));
            if (missed.Count > 0)
            {
                ModLog.Warn("[IsoRim] Hooks missing (" + missed.Count + "): " + string.Join("; ", missed));
            }
        }

        // __0 map, __1 subMesh, __2 rect, __3 ref firstCenterInd, __4 centered, __5 filter.
        public static void GenerateLightingOverlay_Postfix(Map __0, LayerSubMesh __1, CellRect __2, bool __4, Predicate<int> __5)
        {
            try
            {
                IsoGlow.Generated(__0, __1, __2, __4, __5);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Copying the map's light failed: " + e, 93611201);
            }
        }

        public static IEnumerable<CodeInstruction> CullDrawMapMesh_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Cull.DrawMapMeshTranspiler(instructions);
        }

        public static void RoofFinalizeMesh_Prefix(LayerSubMesh __instance)
        {
            RoofClip.Pad(__instance);
        }

        public static void CullFinalizeMesh_Postfix(LayerSubMesh __instance, MeshParts __0)
        {
            if ((__0 & (MeshParts.Verts | MeshParts.Tris)) != MeshParts.None)
            {
                MeshGuard.CheckLayer(__instance);
            }
            Cull.MeshFinalized(__0);
        }

        public static bool CullThing_Prefix(Thing __instance, DrawPhase phase)
        {
            return Cull.ThingPhase(__instance, phase);
        }

        public static void CullThing_Postfix()
        {
            Cull.ThingPhaseDone();
        }

        public static void CullThings_Prefix()
        {
            UprightScopes.FrameCheck();
            Cull.ThingsBegin();
            PartBatches.Begin();
        }

        public static Exception CullThings_Finalizer(Exception __exception)
        {
            Cull.ThingsEnd();
            PartBatches.Flush();
            return __exception;
        }

        public static void DrawMapMesh_Prefix()
        {
            try
            {
                IsoGlow.BeginFrame(Find.CurrentMap);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Handing the map's light to the shaders failed: " + e, 93611202);
            }
            try
            {
                IsoCellMap.BeginFrame(Find.CurrentMap);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Handing the map's cell data to the shaders failed: " + e, 93611203);
            }
        }

        // The game's sun is in the north (its shadows always point south); IsoRim's is in the south.
        // East and west already agree, so in 3D only the north-south part is mirrored.
        public static void GetLightSourceInfo_Postfix(GenCelestial.LightType type, ref GenCelestial.LightInfo __result)
        {
            if (type == GenCelestial.LightType.Shadow && View3D.Active)
            {
                __result.vector.y = -__result.vector.y;
            }
        }

        private static readonly AccessTools.FieldRef<SectionLayer, Section> sectionOf = AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        // Adds the missing north edges to a block of the game's building shadows (3D only; the
        // map is rebuilt when the 3D view is switched, so top-down keeps the game's own mesh).
        // Same layout as the game's other edges: the corners on the building stay (alpha 0), the
        // outer pair slides along the cast by the building's shadow height (alpha). Wound so it
        // faces up once slid north; slid south (the game's own sun) it would face away and be culled.
        public static void SunShadowsRegenerate_Postfix(SectionLayer __instance)
        {
            if (!View3D.Active)
            {
                return;
            }
            try
            {
                Section section = sectionOf(__instance);
                Map map = section != null ? section.map : null;
                Material mat = MatBases.SunShadow;
                if (map == null || section == null || mat == null)
                {
                    return;
                }
                LayerSubMesh sm = __instance.GetSubMesh(mat);
                if (sm == null || sm.verts.Count == 0)
                {
                    return;
                }
                Building[] grid = map.edificeGrid.InnerArray;
                CellIndices idx = map.cellIndices;
                float y = AltitudeLayer.Shadows.AltitudeFor();
                Color32 low = new Color32(0, 0, 0, 0);
                int added = 0;
                int maxX = Mathf.Min(section.botLeft.x + 17, map.Size.x);
                int maxZ = Mathf.Min(section.botLeft.z + 17, map.Size.z);
                for (int i = section.botLeft.x; i < maxX; i++)
                {
                    for (int j = section.botLeft.z; j < maxZ; j++)
                    {
                        Building b = grid[idx.CellToIndex(i, j)];
                        if (b == null || !(b.def.staticSunShadowHeight > 0f) || j >= map.Size.z - 1)
                        {
                            continue;
                        }
                        float h = b.def.staticSunShadowHeight;
                        Building north = grid[idx.CellToIndex(i, j + 1)];
                        if (north != null && north.def.staticSunShadowHeight >= h)
                        {
                            continue;
                        }
                        Color32 high = new Color32(0, 0, 0, (byte)(255f * h));
                        int n = sm.verts.Count;
                        sm.verts.Add(new Vector3(i, y, j + 1));
                        sm.verts.Add(new Vector3(i + 1, y, j + 1));
                        sm.verts.Add(new Vector3(i, y, j + 1));
                        sm.verts.Add(new Vector3(i + 1, y, j + 1));
                        sm.colors.Add(low);
                        sm.colors.Add(low);
                        sm.colors.Add(high);
                        sm.colors.Add(high);
                        sm.tris.Add(n);
                        sm.tris.Add(n + 2);
                        sm.tris.Add(n + 3);
                        sm.tris.Add(n);
                        sm.tris.Add(n + 3);
                        sm.tris.Add(n + 1);
                        added++;
                    }
                }
                if (added > 0)
                {
                    sm.finalized = false;
                    sm.FinalizeMesh(MeshParts.Verts | MeshParts.Tris | MeshParts.Colors);
                    sm.mesh.bounds = new Bounds(Vector3.zero, new Vector3(1000f, 1000f, 1000f));
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Adding the north edges of building shadows failed: " + e, 93611171);
            }
        }

        private static readonly List<string> hooked = new List<string>();
        private static readonly List<string> missed = new List<string>();

        private static FieldInfo turretParent;
        private static PropertyInfo turretRotation;

        // A turret drawing its gun. If the turret has a gun model it is drawn turned to the way the
        // gun is aiming, on top of the 3D base, and the game's flat gun is not drawn. As the game's
        // own: a spraying gun (foam turret) sweeps along its spray (AimAngleOverride), the gun kicks
        // back by the recoil offset, and a gun whose sprite shows it loaded (rocketswarm launcher,
        // full) shows its loaded model. Its shots, flashes and beams start at the barrel end of this pose
        // (Projectiles.TurretMuzzle, TurretGunEnd: keep the two the same).
        public static bool DrawTurret_Prefix(object __instance, Vector3 __1)
        {
            if (!View3D.Ready || !IsoRimMod.Settings.models3D || __instance == null)
            {
                return true;
            }
            try
            {
                Building_Turret turret = turretParent.GetValue(__instance) as Building_Turret;
                ModelEntry entry;
                if (turret == null || !ModelLibrary.TryGet(turret.def, out entry) || entry.top == null)
                {
                    return true;
                }
                Verb verb = turret.CurrentEffectiveVerb;
                float? over = verb != null ? verb.AimAngleOverride : null;
                float aim = over ?? (float)turretRotation.GetValue(__instance, null);
                Model gun = entry.top;
                if (entry.topLoaded != null && turret.def.building != null && turret.TurretTopMaterial != turret.def.building.turretTopMat)
                {
                    gun = entry.topLoaded;
                }
                Vector3 at = turret.DrawPos;
                Vector3 fit = entry.Fit(turret.def) * entry.def.scale;
                DynamicModels.ThroughRoof = entry.def.topThroughRoof;   // (Api 31) the gun rises through a roof over it
                try
                {
                    DynamicModels.Draw(ObjLoader.SheetTinted(gun, R3DTextures.SheetTint(turret)), new Vector3(at.x + __1.x, entry.def.topHeight * fit.y, at.z + __1.z), aim - 90f, fit, R3DTextures.ModelTint(turret), R3DTextures.ModelSwatch(turret));
                }
                finally
                {
                    DynamicModels.ThroughRoof = false;
                }
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a turret's gun failed: " + e, 93611023);
                return true;
            }
        }

        // Adds the 3D view switch, and the roof switch while the 3D view is on, to the row of
        // buttons in the bottom right corner.
        public static void PlaySettings_Postfix(WidgetRow row, bool worldView)
        {
            if (worldView || row == null || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                if (View3D.LockedOn)
                {
                    row.Icon(R3DIcons.View3D, "R3D_Msg_ViewButtonLocked".Translate(View3D.LockedBy));
                }
                else
                {
                    bool view = View3D.Active;
                    row.ToggleableIcon(ref view, R3DIcons.View3D, "R3D_Msg_ViewButton".Translate(KeyLabel(R3DKeyDefOf.R3D_ToggleView)), SoundDefOf.Mouseover_ButtonToggle);
                    if (view != View3D.Active)
                    {
                        View3D.Active = view;
                    }
                }
                if (View3D.Active)
                {
                    bool roofs = IsoRimMod.Settings.showRoofs;
                    row.ToggleableIcon(ref roofs, R3DIcons.Roofs, "R3D_Msg_RoofsButton".Translate(KeyLabel(R3DKeyDefOf.R3D_ToggleRoofs)), SoundDefOf.Mouseover_ButtonToggle);
                    if (roofs != IsoRimMod.Settings.showRoofs)
                    {
                        Roofs.Set(roofs);
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the corner buttons failed: " + e, 93611024);
            }
        }

        private static string KeyLabel(KeyBindingDef key)
        {
            if (key == null || key.MainKey == KeyCode.None)
            {
                return "R3D_Msg_NoKeySet".Translate();
            }
            return key.MainKeyLabel;
        }

        private static bool Patch(string feature, MethodBase target, string prefix = null, string postfix = null, string finalizer = null, string transpiler = null)
        {
            if (target == null)
            {
                ModLog.Warn("[IsoRim] The game method behind \"" + feature + "\" was not found. That part of the 3D view is off.");
                missed.Add(feature + " (not found)");
                return false;
            }
            try
            {
                harmony.Patch(target, Hook(prefix), Hook(postfix), Hook(transpiler), Hook(finalizer));
                hooked.Add(target.DeclaringType?.Name + "." + target.Name);
                return true;
            }
            catch (Exception e)
            {
                missed.Add(target.DeclaringType?.Name + "." + target.Name + " (" + e.Message + ")");
                ModLog.Warn("[IsoRim] Could not hook " + target.DeclaringType + "." + target.Name + " for \"" + feature + "\": " + e.Message);
                return false;
            }
        }

        // Looks a method up by name without throwing if the game has several with that name.
        private static MethodInfo FindMethod(Type type, string name)
        {
            try
            {
                return AccessTools.Method(type, name);
            }
            catch (Exception)
            {
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                {
                    if (m.Name == name)
                    {
                        return m;
                    }
                }
                return null;
            }
        }

        private static HarmonyMethod Hook(string name)
        {
            if (name == null)
            {
                return null;
            }
            HarmonyMethod hook = new HarmonyMethod(AccessTools.Method(typeof(Patches), name));
            if (ModelTakeovers.Contains(name))
            {
                hook.priority = Priority.High;
            }
            return hook;
        }

        // The "draw a model instead" prefixes: each returns false only when IsoRim has a model (or 3D part) standing in for
        // the thing and has drawn it, and true otherwise. They run at high priority, before other mods' prefixes on the same
        // draw: Harmony 3 skips every later prefix that could skip the original once one returns false, so at the default
        // priority a mod that draws its own version and registers earlier would hide IsoRim's model for good. With a model,
        // IsoRim's takes over; without one, the other mod draws as it intends. Not here: pawns and held weapons (animation
        // mods draw those on purpose) and projectiles, skyfallers and strikes (IsoRim only places those in 3D).
        private static readonly HashSet<string> ModelTakeovers = new HashSet<string>
        {
            nameof(DrawTurret_Prefix), nameof(DrawMovers_Prefix), nameof(MultiTileDoorDrawAt_Prefix), nameof(SupportedDoorDrawAt_Prefix),
            nameof(HackablePostDraw_Prefix), nameof(ThingDrawAt_Prefix), nameof(BookcaseDrawAt_Prefix), nameof(GibbetDrawAt_Prefix),
            nameof(CaveExitDrawAt_Prefix), nameof(DryadHolderPostDraw_Prefix), nameof(MechGestatorDrawAt_Prefix), nameof(GrowthVatDrawAt_Prefix),
            nameof(HoldingPlatformDrawAt_Prefix), nameof(OutfitStandDrawAt_Prefix), nameof(DrawAdditionalGraphicsPostDraw_Prefix),
            nameof(AtomizerDrawAt_Prefix), nameof(CableConnectionPostDraw_Prefix), nameof(BiosculpterPostDraw_Prefix), nameof(GravEngineDrawAt_Prefix),
            nameof(CerebrexDrawAt_Prefix), nameof(MechRelayDrawAt_Prefix), nameof(FleshmassHeartDrawAt_Prefix), nameof(StatueDrawAt_Prefix),
            nameof(EmptyStatePostDraw_Prefix), nameof(MoteDrawAt_Prefix),
        };

        private static bool StartsWith(MethodInfo m, params Type[] types)
        {
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length < types.Length)
            {
                return false;
            }
            for (int i = 0; i < types.Length; i++)
            {
                if (ps[i].ParameterType != types[i])
                {
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- camera

        public static void CameraUpdate_Postfix(CameraDriver __instance)
        {
            try
            {
                ApplyDrag(__instance);
                View3D.Tick();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Camera update failed: " + e, 93611010);
            }
        }

        public static void GameCtor_Postfix()
        {
            View3D.NewGame();
        }

        public static void RootUpdate_Postfix()
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null)
            {
                FullWalls.Reset();
                FirstPerson.Reset();
            }
        }

        public static void RegenerateEverythingNow_Prefix(MapDrawer __instance)
        {
            try
            {
                View3D.PrepareForFirstBuild(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Preparing the 3D view while loading failed: " + e, 93611190);
            }
        }

        // The direction asked for by the movement keys and by the pointer at a screen edge:
        // x is "right", y is "up the screen".
        public static void DollyVect_Postfix(ref Vector2 __result)
        {
            if (FirstPerson.On)
            {
                __result = Vector2.zero;   // the camera rides with the colonist
                return;
            }
            if (View3D.Ready && __result != Vector2.zero)
            {
                __result = View3D.ScreenMoveToGround(__result, false);
            }
        }

        // Dragging the map with the middle mouse button. The game's own drag moves the camera
        // along the map's axes and keeps some speed after the button is let go, which goes wrong
        // with a tilted, turned camera (it used to run away into the grey beyond the map). While
        // the 3D view is on the mod does it instead: the point of ground grabbed stays under the
        // pointer, and the camera stops as soon as the button is let go.
        public static void CameraUpdate_Prefix(CameraDriver __instance)
        {
            try
            {
                ZoomLimit(__instance);
                if (!View3D.Ready || View3D.ControlsLocked)
                {
                    dragging = false;
                    return;
                }
                if (View3D.LookDragging || View3D.LookHeld())
                {
                    // The middle button is turning the view (free look), not dragging the map.
                    dragging = false;
                    StopGameDrag(__instance);
                    return;
                }
                bool held = Input.GetMouseButton(2);
                if (!held)
                {
                    if (dragging)
                    {
                        dragging = false;
                        StopGameDrag(__instance);
                    }
                    return;
                }
                Camera cam = Find.Camera;
                if (cam == null)
                {
                    return;
                }
                if (!dragging)
                {
                    dragging = true;
                    dragAnchor = View3D.ScreenToGround(Input.mousePosition);
                }
                dragStartPos = cam.transform.position;
                StopGameDrag(__instance);
            }
            catch (Exception e)
            {
                dragging = false;
                Log.ErrorOnce("[IsoRim] Dragging the map failed: " + e, 93611018);
            }
        }

        // The game will not zoom in past 11; in the 3D view the mod lets the camera come closer (minZoom).
        // The game's own limit comes back when the 3D view is off. Each config (map type) keeps the limit found under
        // ours, read again whenever someone else sets it (another mod, the harness), so theirs is kept; a closer limit
        // than ours is left as it is.
        private static readonly Dictionary<CameraMapConfig, Vector2> zoomLimits = new Dictionary<CameraMapConfig, Vector2>();   // x: theirs, y: ours

        private static void ZoomLimit(CameraDriver driver)
        {
            CameraMapConfig config = driver.config;
            if (config == null)
            {
                return;
            }
            Vector2 lim;
            if (!zoomLimits.TryGetValue(config, out lim) || config.sizeRange.min != lim.y)
            {
                lim.x = config.sizeRange.min;
            }
            lim.y = View3D.Ready ? Mathf.Min(lim.x, Mathf.Max(2f, IsoRimMod.Settings.minZoom)) : lim.x;
            zoomLimits[config] = lim;
            config.sizeRange.min = lim.y;
        }

        private static void StopGameDrag(CameraDriver driver)
        {
            for (int i = 0; i < dragFields.Count; i++)
            {
                dragFields[i].SetValue(driver, Vector2.zero);
            }
        }

        // Called after the game's camera update: moves the camera so the grabbed ground point
        // is back under the pointer.
        private static void ApplyDrag(CameraDriver driver)
        {
            if (!dragging || !View3D.Ready || driver == null || Find.CurrentMap == null)
            {
                return;
            }
            Vector3 now = View3D.ScreenToGround(Input.mousePosition);
            Vector3 target = dragStartPos + (dragAnchor - now);
            if (float.IsNaN(target.x) || float.IsNaN(target.z) || float.IsInfinity(target.x) || float.IsInfinity(target.z))
            {
                return;
            }
            IntVec3 size = Find.CurrentMap.Size;
            target.x = Mathf.Clamp(target.x, 0f, size.x);
            target.z = Mathf.Clamp(target.z, 0f, size.z);
            driver.JumpToCurrentMapLoc(new Vector3(target.x, 0f, target.z));
        }

        // A selection box dragged on screen. The game looks for things in the block of cells between two of the box's
        // corners on the map, which only covers the box while north is up and the camera looks straight down. In 3D
        // what is found is what shows inside the box on screen, as drawn: a thing counts when the box catches any part
        // of it standing up from its cell (a colonist's head is enough), as the flat game counts a thing when the box
        // catches any part of its cell. On stacked floors (Floors) that is every floor from the viewed one down, each at
        // its own height, a lower floor only where it is seen through the open air above it; hidden floors never.
        // Things, zones and plans (ThingSelectionUtility's three box lookups) and DragBox.Contains all go through
        // BoxCells and ShowsInBox; the box itself stays the game's (its start a map point, drawn where it shows).
        public static IEnumerable<Thing> DragSelect_Postfix(IEnumerable<Thing> __result, Rect __0)
        {
            if (__result == null || !View3D.Ready || Find.CurrentMap == null)
            {
                return __result;
            }
            return ThingsShowingInBox(Find.CurrentMap, __0);
        }

        private static readonly HashSet<Thing> boxYielded = new HashSet<Thing>();

        private static IEnumerable<Thing> ThingsShowingInBox(Map map, Rect box)
        {
            Rect grown = GrownBox(box);
            // Tall things a few cells nearer the camera than the box can still reach up into it.
            int margin = Mathf.CeilToInt(2.5f / Mathf.Tan(View3D.Pitch * Mathf.Deg2Rad)) + 1;
            List<Thing> found = new List<Thing>();
            boxYielded.Clear();
            foreach (IntVec3 c in BoxCells(box, margin))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = map.thingGrid.ThingsListAt(c);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (!boxYielded.Contains(t) && ThingSelectionUtility.SelectableByMapClick(t) && !t.def.neverMultiSelect && ShowsInBox(t, grown))
                    {
                        boxYielded.Add(t);
                        found.Add(t);
                    }
                }
            }
            boxYielded.Clear();
            return found;
        }

        // The cells that can show inside a screen box (interface coordinates): on the ground and on every floor from the
        // viewed one down (Floors), the block under the box at that floor's height in that floor's rows, widened by margin.
        private static IEnumerable<IntVec3> BoxCells(Rect box, int margin)
        {
            for (int k = 0; k <= Floors.PointerTop; k++)
            {
                if (!Floors.Shown(k))
                {
                    continue;
                }
                float height;
                int zShift;
                Floors.FloorPlane(k, out height, out zShift);
                CellRect r = BoxRect(box, height, margin);
                for (int z = r.minZ; z <= r.maxZ; z++)
                {
                    for (int x = r.minX; x <= r.maxX; x++)
                    {
                        yield return new IntVec3(x, 0, z + zShift);
                    }
                }
            }
        }

        // The block of ground cells under a screen box (interface coordinates) seen at a height, widened by margin cells.
        private static CellRect BoxRect(Rect box, float height, int margin)
        {
            float scale = Prefs.UIScale;
            float h = Verse.UI.screenHeight;
            Vector3 a = View3D.ScreenToHeight(new Vector2(box.xMin, h - box.yMin) * scale, height);
            Vector3 b = View3D.ScreenToHeight(new Vector2(box.xMax, h - box.yMin) * scale, height);
            Vector3 c = View3D.ScreenToHeight(new Vector2(box.xMin, h - box.yMax) * scale, height);
            Vector3 d = View3D.ScreenToHeight(new Vector2(box.xMax, h - box.yMax) * scale, height);
            CellRect r = new CellRect();
            r.minX = Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x))) - margin;
            r.maxX = Mathf.FloorToInt(Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x))) + margin;
            r.minZ = Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z))) - margin;
            r.maxZ = Mathf.FloorToInt(Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z))) + margin;
            return r;
        }

        // The box (interface coordinates) grown by half a cell, so a thing counts when the box
        // only catches part of it.
        private static Rect GrownBox(Rect box)
        {
            float slack = HalfCellUi();
            return Rect.MinMaxRect(
                Mathf.Min(box.xMin, box.xMax) - slack, Mathf.Min(box.yMin, box.yMax) - slack,
                Mathf.Max(box.xMin, box.xMax) + slack, Mathf.Max(box.yMin, box.yMax) + slack);
        }

        // Half a cell on screen, in interface units.
        private static float HalfCellUi()
        {
            return Screen.height / (2f * Mathf.Max(1f, Find.Camera.orthographicSize)) * 0.5f / Prefs.UIScale;
        }

        // Whether the upright line from a drawn point (true space: y its height in cells, on its floor) up by top shows
        // inside the (grown) box. Upright lines stay upright on screen, so it is a span in y at one x. On a floor below
        // the viewed one it must also be seen: the pointer's ray at one of its ends falls through to that floor.
        private static bool ShowsInBox(Vector3 at, float top, int floor, Rect grown)
        {
            float scale = Prefs.UIScale;
            float h = Verse.UI.screenHeight;
            Vector2 g = View3D.WorldToScreen(at);
            float x = g.x / scale;
            if (x < grown.xMin || x > grown.xMax)
            {
                return false;
            }
            Vector2 t = top > 0f ? View3D.WorldToScreen(new Vector3(at.x, at.y + top, at.z)) : g;
            float yLow = h - g.y / scale;
            float yHigh = h - t.y / scale;
            if (Mathf.Max(yLow, yHigh) < grown.yMin || Mathf.Min(yLow, yHigh) > grown.yMax)
            {
                return false;
            }
            return floor >= Floors.PointerTop || Floors.PointerFloorAt(g) <= floor || Floors.PointerFloorAt(t) <= floor;
        }

        // A thing: a pawn where it is drawn, anything else by the cells it stands on, on its floor as drawn.
        private static bool ShowsInBox(Thing t, Rect grown)
        {
            int floor;
            Vector3 lift;
            if (!DrawnFloor(t.Position, out floor, out lift))
            {
                return false;
            }
            if (Floors.On)
            {
                lift = Floors.ThingOffset(t);   // its floor's, and the layout's own for this thing (a pawn on a stair)
            }
            else
            {
                lift.y += Bridges.LiftOf(t);   // on a bridge deck
            }
            float top = Interaction.PickHeight(t);
            if (t is Pawn)
            {
                float headRise;
                Vector3 d = Poses.DrawnSpot((Pawn)t, out headRise);   // sitting: on its seat (top already counts it)
                return ShowsInBox(new Vector3(d.x, 0f, d.z) + lift, top, floor, grown);
            }
            foreach (IntVec3 c in t.OccupiedRect())
            {
                Vector3 m = c.ToVector3Shifted();
                if (ShowsInBox(new Vector3(m.x, 0f, m.z) + lift, top, floor, grown))
                {
                    return true;
                }
            }
            return false;
        }

        // A cell lying flat (zones, plans): its middle.
        private static bool CellShowsInBox(IntVec3 c, Rect grown)
        {
            int floor;
            Vector3 lift;
            Vector3 m = c.ToVector3Shifted();
            return DrawnFloor(c, out floor, out lift) && ShowsInBox(new Vector3(m.x, 0f, m.z) + lift, 0f, floor, grown);
        }

        // The floor a cell is on and how it is moved to where it is drawn (true space); false on a hidden floor, above the
        // viewed one or between floors.
        private static bool DrawnFloor(IntVec3 c, out int floor, out Vector3 lift)
        {
            floor = 0;
            lift = Vector3.zero;
            if (!Floors.On)
            {
                return true;
            }
            floor = Floors.FloorOfRow(c.z);
            if (floor < 0 || floor > Floors.PointerTop || !Floors.Shown(floor))
            {
                return false;
            }
            lift = Floors.RowOffset(c.z);
            return true;
        }

        // Zones and plans lie flat: they count when the middle of one of their cells shows inside the box.
        public static IEnumerable<Zone> DragSelectZones_Postfix(IEnumerable<Zone> __result, Rect __0, Zone __1)
        {
            Map map = Find.CurrentMap;
            if (__result == null || !View3D.Ready || map == null)
            {
                return __result;
            }
            Rect grown = GrownBox(__0);
            List<Zone> found = new List<Zone>();
            foreach (IntVec3 c in BoxCells(__0, 0))
            {
                Zone z = c.InBounds(map) ? map.zoneManager.ZoneAt(c) : null;
                if (z != null && z.IsMultiselectable && (__1 == null || z.GetType() == __1.GetType()) && !found.Contains(z) && CellShowsInBox(c, grown))
                {
                    found.Add(z);
                }
            }
            return found;
        }

        public static IEnumerable<Plan> DragSelectPlans_Postfix(IEnumerable<Plan> __result, Rect __0, ColorDef __1)
        {
            Map map = Find.CurrentMap;
            if (__result == null || !View3D.Ready || map == null)
            {
                return __result;
            }
            Rect grown = GrownBox(__0);
            List<Plan> found = new List<Plan>();
            foreach (IntVec3 c in BoxCells(__0, 0))
            {
                Plan p = c.InBounds(map) ? c.GetPlan(map) : null;
                if (p != null && (__1 == null || p.Color == __1) && !found.Contains(p) && CellShowsInBox(c, grown))
                {
                    found.Add(p);
                }
            }
            return found;
        }

        // The game's DragBox (mods ask it): a thing is in the box when it shows inside it on screen, as above.
        public static bool DragBoxContains_Prefix(DragBox __instance, Thing t, ref bool __result)
        {
            if (!View3D.Ready || t == null || !t.Spawned)
            {
                return true;
            }
            __result = ShowsInBox(t, GrownBox(__instance.ScreenRect));
            return false;
        }

        // A drag counts as a box once it is half a cell long on screen, wherever its ends land (the game measures it on
        // the map, where its start and end may lie on different floors).
        public static bool DragBoxIsValid_Prefix(DragBox __instance, ref bool __result)
        {
            if (!View3D.Ready)
            {
                return true;
            }
            __result = (__instance.start.MapToUIPosition() - Verse.UI.MousePositionOnUIInverted).magnitude > HalfCellUi();
            return false;
        }

        // Where a thing's name label goes. The game puts it a little to the south of the thing,
        // which is "below" only while north is up. __1 is that distance (negative).
        public static bool LabelDrawPosFor_Prefix(Thing __0, float __1, ref Vector2 __result)
        {
            Vector3 lift = Floors.DrawnOffset(__0);   // on an upper floor (Floors)
            Attachments.Placed attached;
            if (Attachments.Any && __0 is Pawn && Attachments.Place(__0, false, null, out attached))
            {
                // Attached to a host (Api 13): hung from its anchor (another mod's own label offset left out).
                lift.y += attached.at.y / View3D.Squash;
                __1 = 0f;
            }
            if (!View3D.Ready || __0 == null || View3D.Yaw == 0f && lift == Vector3.zero)
            {
                return true;
            }
            try
            {
                Vector3 onScreen = Find.Camera.WorldToScreenPoint(__0.DrawPos + lift + View3D.GroundForward * __1);
                float scale = Prefs.UIScale;
                __result = new Vector2(onScreen.x / scale, Verse.UI.screenHeight - onScreen.y / scale);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Placing a name label failed: " + e, 93611193);
                return true;
            }
        }

        // A thing's label: skipped when it is too far away, faded on the way. The grey background
        // is drawn with GUI.color as the game leaves it, so that is faded too.
        public static bool DrawThingLabel_Prefix(Thing __0, ref Color __2)
        {
            if (!IsoRimMod.Settings.thingLabels)
            {
                return false;
            }
            if (!View3D.Ready || __0 == null)
            {
                return true;
            }
            if (!View3D.InFront(__0.DrawPos))
            {
                return false;   // behind the camera: it would show mirrored up in the sky
            }
            float a = View3D.LabelAlpha(__0.DrawPos);
            if (a <= 0.01f)
            {
                return false;
            }
            if (a < 1f)
            {
                __2.a *= a;
                GUI.color = new Color(1f, 1f, 1f, a);
            }
            return true;
        }

        // The same for names over people and animals. The player's own colonists always keep theirs.
        public static bool DrawPawnLabel_Prefix(Pawn __0, ref float __2)
        {
            if (__0 != null && __0 == FirstPerson.Pawn)
            {
                return false;   // first person: no name floating in front of the eyes
            }
            if (!View3D.Ready || __0 == null)
            {
                return true;
            }
            if (!View3D.InFront(__0.DrawPos))
            {
                return false;   // behind the camera: it would show mirrored up in the sky
            }
            if (__0.IsColonist)
            {
                return true;
            }
            float a = View3D.LabelAlpha(__0.DrawPos);
            if (a <= 0.01f)
            {
                return false;
            }
            __2 *= a;
            return true;
        }

        public static bool DrawWorldOverlay_Prefix(Map __0, Material __1, int __3)
        {
            return Weather.DrawWorldOverlay_Prefix(__0, __1, __3);
        }

        public static bool DrawScreenOverlay_Prefix(Material __0, int __2)
        {
            return Weather.DrawScreenOverlay_Prefix(__0, __2);
        }

        public static bool DrawWorldOverlayShort_Prefix(Map __0, Material __1, int __2)
        {
            return Weather.DrawWorldOverlay_Prefix(__0, __1, __2);
        }

        public static bool DrawScreenOverlayShort_Prefix(Material __0, int __1)
        {
            return Weather.DrawScreenOverlay_Prefix(__0, __1);
        }

        public static bool SkyOverlayDraw_Prefix(SkyOverlay __instance, Map __0)
        {
            return Falling.DrawOverlay_Prefix(__instance, __0);
        }

        // The game assumes the camera looks straight down when it turns a mouse position into a map cell.
        public static bool UIToMapPosition_Prefix(Vector2 __0, ref Vector3 __result)
        {
            if (!View3D.Ready)
            {
                return true;
            }
            __result = Floors.ScreenToMap(__0 * Prefs.UIScale);
            return false;
        }

        // Same for the area it thinks is on screen: tilted or turned, the camera sees a different part of the map.
        public static void CurrentViewRect_Postfix(ref CellRect __result)
        {
            if (View3D.Ready)
            {
                __result = Floors.WidenView(View3D.GroundRect());
            }
        }

        // ---------------------------------------------------------------- static map geometry

        // Called by the game once for each non-moving thing when it rebuilds a block of the map.
        // Decides what shape the thing takes; the sprite quads it then prints are reshaped in
        // PrintPlane_Postfix.
        public static bool TakePrintFrom_Prefix(SectionLayer __instance, Thing __0, out bool __state)
        {
            PrintContext.Clear();
            __state = false;
            try
            {
                __state = Bridges.BeginPrint(__instance, __0);   // on a bridge deck: what it prints is lifted (finalizer)
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Lifting a thing onto a bridge failed: " + e, 93611471);
            }
            if (!View3D.Active || __0 == null || __0.def == null)
            {
                return true;
            }

            try
            {
                // Blueprints and frames: a see-through ghost of the finished thing. A frame keeps
                // the game's sprite of the work under way beneath it.
                if (Ghosts.Target(__0) != null)
                {
                    if (Ghosts.Print(__instance, __0))
                    {
                        return __0 is Frame;
                    }
                    return true;
                }

                if (__0 is Plant)
                {
                    float rim = Forms.BaseUnder(__0, false);
                    ThingForm plantForm = Forms.For(__0.def);
                    if (plantForm.form == Form3D.Model)
                    {
                        if (SectionLayer_IsoRimTrees.Handles(__0))
                        {
                            return false;   // drawn in the mod's own tree layer
                        }
                        if (ModelBaker.Bake(__instance, __0, plantForm.model, rim))
                        {
                            return false;   // a 3D plant replaces the sprite
                        }
                    }
                    if (IsoRimMod.Settings.uprightPlants)
                    {
                        PrintContext.mode = PrintMode.Upright;
                        PrintContext.thing = __0;   // the card's size by kind of plant (UprightCards.SizeOf)
                        PrintContext.baseHeight = rim;
                    }
                    else if (rim > 0f)
                    {
                        PrintContext.mode = PrintMode.Lift;
                        PrintContext.height = rim;
                    }
                    return true;
                }

                ThingForm form = Forms.For(__0.def);
                bool item = __0.def.category == ThingCategory.Item;
                float resting = item ? Forms.BaseUnder(__0, true) : 0f;

                // A wall fixture without a model: its sprite stands on the wall face (WallMount).
                if (form.form != Form3D.Model && !item && WallMount.Is(__0.def) && WallMount.PrintCard(__instance, __0))
                {
                    return false;
                }

                switch (form.form)
                {
                    case Form3D.Model:
                        if (ModelBaker.Bake(__instance, __0, form.model, resting))
                        {
                            return false;   // the model replaces the sprite
                        }
                        break;

                    case Form3D.Block:
                    {
                        if (SectionLayer_IsoRimWalls.Handles(__0))
                        {
                            return false;   // drawn in the mod's own wall layer
                        }
                        // A wall hiding a room from the camera is drawn part height.
                        float blockHeight = WallBoxes.HeightFor(__0, form.height);
                        IsoCellMap.Dirty(__0.Map, __0.OccupiedRect());   // the fog lid lies on its top
                        GroundShadows.AroundBlock(__instance, __0);
                        if (IsoRimMod.Settings.texturedWalls && WallBoxes.PrintBlock(__instance, __0, blockHeight))
                        {
                            return false;   // drawn from the mod's own textures; the game's sprite is not used
                        }
                        PrintContext.mode = PrintMode.Block;
                        PrintContext.height = blockHeight;
                        break;
                    }

                    case Form3D.Ground:
                        if (SectionLayer_IsoRimWalls.Handles(__0))
                        {
                            return false;   // (Api 11) drawn in the mod's own wall layer (WallBoxes.PrintGround)
                        }
                        break;   // without the mod's wall textures: the game's flat sprite

                    case Form3D.Extruded:
                        PrintContext.mode = PrintMode.Extrude;
                        PrintContext.baseHeight = resting;
                        PrintContext.height = resting + form.height;
                        break;

                    case Form3D.Worn:
                        if (GroundGear.Bake(__instance, __0, resting))
                        {
                            return false;   // the clothes a colonist wears, laid down, replace the sprite
                        }
                        PrintContext.mode = PrintMode.Extrude;   // none to show: the sprite made solid, as with the game's art
                        PrintContext.baseHeight = resting;
                        PrintContext.height = resting + Forms.ItemThickness(__0.def);
                        break;

                    default:
                        if (resting > 0f)
                        {
                            PrintContext.mode = PrintMode.Lift;
                            PrintContext.height = resting;
                        }
                        break;
                }
                PrintContext.thing = __0;
                PrintContext.layer = __instance;
            }
            catch (Exception e)
            {
                PrintContext.Clear();
                Log.ErrorOnce("[IsoRim] Building 3D geometry failed: " + e, 93611011);
            }
            return true;
        }

        public static void TakePrintFrom_Finalizer(bool __state)
        {
            if (__state)
            {
                Bridges.EndPrint();
            }
            // A wall that drew no top sprite still gets plain sides.
            if (PrintContext.mode == PrintMode.Block && !PrintContext.done && PrintContext.thing != null)
            {
                try
                {
                    WallBoxes.PrintPlainSides(PrintContext.layer, PrintContext.thing, PrintContext.height);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Building wall sides failed: " + e, 93611013);
                }
            }
            PrintContext.Clear();
        }

        // Runs after the game adds one flat sprite quad to a block's mesh.
        // __1 is the quad's centre, __2 its size, __3 its material and __4 its rotation.
        public static void PrintPlane_Postfix(MapDrawLayer __0, Vector3 __1, Vector2 __2, Material __3, float __4)
        {
            if (PrintContext.mode == PrintMode.None || !(__0 is SectionLayer layer))
            {
                return;
            }
            try
            {
                PlaneHandler.Handle(layer, __1, __2, __3, __4);
            }
            catch (Exception e)
            {
                PrintContext.Clear();
                Log.ErrorOnce("[IsoRim] Reshaping a sprite failed: " + e, 93611014);
            }
        }

        // The lighting overlay and the fog of war are flat sheets. The lighting is pulled in front
        // of everything so it also darkens upright things; the fog is drawn as a lid on the tops of
        // what is under it (lifted in its shader, IsoCellMap) so unexplored areas read as solid.
        public static bool DrawLayer_Prefix(MapDrawLayer __instance)
        {
            return DrawLayer_Section(__instance as SectionLayer);
        }

        private static bool DrawLayer_Section(SectionLayer __instance)
        {
            if (__instance == null)
            {
                return true;
            }
            Type type = __instance.GetType();
            int kind;
            if (!layerKinds.TryGetValue(type, out kind))
            {
                string name = type.Name;
                kind = name.Contains("LightingOverlay") ? 1 : (name.Contains("FogOfWar") ? 2 : (name == "SectionLayer_BuildingsDamage" ? 3 : 0));
                layerKinds[type] = kind;
            }
            IsoRimSettings st = IsoRimMod.Settings;
            if (kind == 0 && __instance is SectionLayer_IsoRimWalls)
            {
                WallBoxes.UpdateCovered(__instance);   // rock caps under a shown floor are not drawn (CliffLook.cs)
            }
            if (Floors.sectionLifted)
            {
                return Floors.DrawLayer(__instance, kind);   // a block of an upper floor
            }

            // The fog lies on the tops of the rock and walls under it: the game's mesh drawn with
            // the lid material, whose shader lifts it. Flat (the game's own drawing) with the 3D
            // view or the lid off (View3D.FogLid: the setting, or an addon's lock).
            if (kind == 2 && (!View3D.Ready || !View3D.FogLid))
            {
                return true;
            }
            if (!View3D.Ready)
            {
                return true;
            }
            if (kind == 0)
            {
                // IsoRim's faces that carry their own night and indoor light get a stencil mark, so
                // the pulled overlay below skips them.
                if (__instance.Visible)
                {
                    IsoGlow.DrawLayerMarks(__instance);
                }
                return true;
            }
            if (kind == 3)
            {
                return !st.damage3D;   // the mod's own marks are drawn on the 3D shapes instead
            }
            if (kind == 2)
            {
                if (!__instance.Visible)
                {
                    return false;
                }
                List<LayerSubMesh> fogSubs = __instance.subMeshes;
                for (int i = 0; i < fogSubs.Count; i++)
                {
                    LayerSubMesh sm = fogSubs[i];
                    if (sm.finalized && !sm.disabled)
                    {
                        // (cover: storey 1 shows; fogged ground cells under its floors get no lid, the per-storey lid)
                        Material lid = sm.material == MatBases.FogOfWar ? IsoCellMap.FogFor(sm.material, Floors.ShownSolid(1)) : null;
                        Graphics.DrawMesh(sm.mesh, Matrix4x4.identity, lid ?? sm.material, sm.renderLayer);
                    }
                }
                return false;
            }
            if (!st.lightingFix)
            {
                return true;
            }
            if (!__instance.Visible)
            {
                return false;
            }

            // The overlay is drawn with a copy of the game's material that skips pixels of IsoRim
            // faces lit by their own cell (stencil bit 32, see IsoGlow).
            Matrix4x4 m = View3D.LightingMatrix;
            List<LayerSubMesh> subs = __instance.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sm = subs[i];
                if (sm.finalized && !sm.disabled)
                {
                    Material mat = sm.material == MatBases.LightOverlay ? IsoGlow.OverlayFor(sm.material) : sm.material;
                    Graphics.DrawMesh(sm.mesh, m, mat, sm.renderLayer);   // 1.7 vanilla passes renderLayer
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- pawns

        // While a pawn is being drawn, DrawHook stands every piece of it up around its feet.
        // An animal that has a 3D model is drawn as that instead.
        // __0 is the position the pawn is drawn at.
        // Returns false (the game's own drawing of the pawn is skipped altogether) when a 3D
        // model has been drawn in its place: the game's sprite pipeline for a person is costly,
        // and skipping it rather than throwing its output away is most of what keeps a big
        // colony fast. Portraits and other off-map drawing are left to the game.
        public static bool RenderPawnAt_Prefix(PawnRenderer __instance, Vector3 __0, bool __2)
        {
            if (renderDepth++ > 0)
            {
                // A pawn drawn inside another's drawing (another mod's render node draws its rider): the outer pawn's
                // drawing state is put back when this one is done (RenderPawnAt_Finalizer).
                nestedDraws.Push(new PawnDrawState
                {
                    active = DrawHook.active, billboard = DrawHook.billboard, skip = DrawHook.skip, xform = DrawHook.xform,
                    upright = pawnUpright, pawn = uprightPawn, feet = pawnFeet, lift = pawnLift, size = pawnSize,
                    aiming = cardAiming, aimGame = cardAimGame, aimSeen = cardAimSeen,
                });
            }
            DrawHook.active = false;
            DrawHook.billboard = false;
            DrawHook.skip = false;
            pawnUpright = false;
            pawnLift = 0f;
            cardAiming = false;
            if (!View3D.Ready)
            {
                return true;
            }
            Camera current = Camera.current;
            if (current != null && current != Find.Camera)
            {
                return true;   // a portrait or some other camera: draw as usual
            }

            try
            {
                IsoRimSettings st = IsoRimMod.Settings;
                Pawn pawn = pawnOfRenderer != null ? pawnOfRenderer(__instance) : null;
                float spriteHeight = 1.5f;
                Cards.Drawn(pawn);   // not a card this frame unless stood up below (Api.IsCard)

                if (pawn != null && pawn == FirstPerson.Pawn)
                {
                    return false;   // first person: the camera is in this colonist's head
                }

                if (pawn != null && st.mechs3D && st.models3D && pawn.RaceProps != null && pawn.RaceProps.IsMechanoid
                    && Mechs.Draw(pawn, __0))
                {
                    AimPies.ModelPawnExtras(pawn);   // its aim pie and ropes (AimPies)
                    return false;
                }

                // Anomaly entities bound to models go the mechs' way (ModelDef.entity).
                if (pawn != null && st.animals3D && st.models3D && pawn.RaceProps != null && !pawn.RaceProps.Humanlike && !pawn.RaceProps.IsMechanoid
                    && Mechs.Draw(pawn, __0))
                {
                    AimPies.ModelPawnExtras(pawn);   // its aim pie and ropes (AimPies)
                    return false;
                }

                if (pawn != null && st.animals3D && st.models3D && pawn.RaceProps != null && !pawn.RaceProps.Humanlike
                    && Animals.Draw(pawn, __0))
                {
                    AimPies.ModelPawnExtras(pawn);
                    return false;
                }

                if (pawn != null && st.pawns3D && st.models3D && pawn.RaceProps != null && pawn.RaceProps.Humanlike)
                {
                    if (People.Draw(pawn, __0, __2))
                    {
                        AimPies.ModelPawnExtras(pawn);
                        return false;   // with its weapon (FigureWeapons)
                    }
                }

                if (pawn != null)
                {
                    if (pawn.Dead || pawn.GetPosture() != PawnPosture.Standing)
                    {
                        // Lying down stays flat, but is raised onto the bed (or the couch of a building
                        // that shows who is inside, such as the gene extractor) if it has height.
                        Building_Bed bed = pawn.Dead ? null : pawn.CurrentBed();
                        Building holder = pawn.Dead ? null : Glass.HolderOf(pawn);
                        // A flat sprite lies on the top of the bedding, not under the pillow and blanket (Poses).
                        float bedTop = Poses.FlatLyingHeight((Building)bed ?? holder, pawn);   // on their slot (Api 28)
                        Building_GibbetCage gibbet = pawn.Dead ? People.GibbetOf(pawn) : null;
                        if (gibbet != null)
                        {
                            bedTop = Forms.HeightOf(gibbet);   // on the cage floor
                        }
                        if (bedTop <= 0f)
                        {
                            // In a 3D carrier's hands, or a corpse on a shelf.
                            Vector3 held = __0;
                            bedTop = Carried.LiftOf(pawn, ref held) + Corpses.RestHeight(pawn);
                        }
                        if (bedTop > 0f)
                        {
                            DrawHook.xform = View3D.LiftMatrix(bedTop);
                            DrawHook.active = true;
                        }
                        return true;
                    }

                    if (!pawn.RaceProps.Humanlike && pawn.ageTracker != null)
                    {
                        PawnKindLifeStage stage = pawn.ageTracker.CurKindLifeStage;
                        if (stage != null && stage.bodyGraphicData != null)
                        {
                            spriteHeight = stage.bodyGraphicData.drawSize.y;
                        }
                    }
                }

                if (!st.uprightPawns)
                {
                    return true;
                }

                // The feet are where the bottom of the sprite's figure is on the flat sprite. The
                // sprite is stood up about that point, then slid so the feet come down on the
                // side of the pawn's spot nearest the camera, whichever way the camera faces.
                Vector3 pivot = __0;
                pivot.z -= spriteHeight * st.feetOffset;
                Vector3 feet = __0 - View3D.GroundForward * (spriteHeight * st.feetOffset);
                pawnLift = 0f;
                Vector3 onSeat = __0;
                Poses.Seat seat;
                if (pawn != null && Poses.Seated(pawn, ref onSeat, out seat))
                {
                    // Sitting (Poses): stood on the seat in the middle of its cell (not slid toward the camera), with the
                    // game's shift up the screen taken back out.
                    Vector3 standingFeet = onSeat - View3D.GroundForward * (spriteHeight * st.feetOffset);
                    feet = Vector3.Lerp(standingFeet, onSeat, seat.amount);
                    pawnLift = seat.Lift;
                }
                Attachments.Placed attached;
                if (pawn != null && Attachments.Any && Attachments.Place(pawn, true, null, out attached))
                {
                    // Attached to a host (Api 13): the card stands on the anchor (not slid), sitting ones sunk to the hips.
                    feet = new Vector3(attached.at.x, __0.y, attached.at.z);
                    bool sits = attached.a.pose == AttachPose.Sit || attached.a.pose == AttachPose.SitNoLegs;
                    pawnLift = attached.at.y - (sits ? CardHips * spriteHeight : 0f);
                }
                float skyLift;
                if (pawn != null && Skyfallers.PawnLift(pawn, out skyLift))
                {
                    pawnLift += skyLift;   // a flying animal arriving or leaving, at its height on the skyfaller's path
                }
                DrawHook.xform =Matrix4x4.Translate(new Vector3(feet.x - pivot.x, 0f, feet.z - pivot.z)) * View3D.UprightMatrix(pivot);
                if (pawnLift > 0f)
                {
                    DrawHook.xform = View3D.LiftMatrix(pawnLift) * DrawHook.xform;
                }
                if (Statues.ground > 0f)
                {
                    DrawHook.xform = View3D.LiftMatrix(Statues.ground) * DrawHook.xform;   // a statue's subject, on its 3D base
                }
                DrawHook.active = true;
                pawnUpright = true;
                uprightPawn = pawn;
                pawnFeet = feet;
                pawnSize = spriteHeight / 1.5f;
                cardAiming = pawn != null && CardAim(pawn, __2, out cardAimGame, out cardAimSeen);
                Cards.Upright(pawn);   // where its card stands this frame (Api.DrawOnCard)
            }
            catch (Exception e)
            {
                DrawHook.active = false;
            DrawHook.billboard = false;
                Log.ErrorOnce("[IsoRim] Standing a pawn upright failed: " + e, 93611012);
            }
            return true;
        }

        public static bool DrawAimPie_Prefix(Thing shooter, LocalTargetInfo target, int degreesWide)
        {
            return AimPies.DrawAimPie_Prefix(shooter, target, degreesWide);
        }

        // True for a pawn that will certainly be drawn as a 3D model while the view is on.
        public static bool DrawnAsModel(Pawn pawn)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (pawn == null || !View3D.Ready || !st.models3D || pawn.RaceProps == null || pawn.def == null || !Corpses.ShownAsModel(pawn))
            {
                return false;
            }
            if (pawn.RaceProps.Humanlike)
            {
                return st.pawns3D && People.FigureRace(pawn) && pawn.story != null && !People.Hidden(pawn);
            }
            if (Mechs.HasModel(pawn))
            {
                return pawn.RaceProps.IsMechanoid ? st.mechs3D : st.animals3D;
            }
            ModelEntry entry;
            return st.animals3D && ModelLibrary.TryGet(pawn.def, out entry) && entry.def.animal && entry.HasModel;
        }

        public static void SpawnSetup_Postfix(Thing __instance)
        {
            NoteShapeChange(__instance, __instance != null ? __instance.Map : null);
        }

        public static void DeSpawn_Prefix(Thing __instance)
        {
            NoteShapeChange(__instance, __instance != null ? __instance.MapHeld : null);
        }

        public static void MineDesignation_Postfix(DesignationManager __instance, Designation newDes)
        {
            MineMarks.Changed(__instance.map, newDes);
        }

        public static void MineUndesignation_Postfix(DesignationManager __instance, Designation des)
        {
            MineMarks.Changed(__instance.map, des);
        }

        public static void TreesDirty_Postfix(MapDrawer __instance, IntVec3 loc, ulong dirtyFlags)
        {
            SectionLayer_IsoRimTrees.MapMeshDirty_Postfix(__instance, loc, dirtyFlags);
            IsoCellMap.FogDirty_Postfix(__instance, loc, dirtyFlags);   // a cell's fog changed: the fog clip follows (RoofClip)
        }

        public static void BridgeGround_Postfix(SectionLayer __instance)
        {
            try
            {
                Bridges.LiftGround(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Lifting the ground onto a bridge deck failed: " + e, 93611473);
            }
        }

        public static void BridgeProps_Postfix(SectionLayer __instance)
        {
            Bridges.HideProps(__instance);
        }

        public static bool ThingLift_Prefix(Thing __instance, DrawPhase phase, out bool __state)
        {
            return Floors.ThingPhase_Prefix(__instance, phase, out __state);
        }

        public static void ThingLift_Finalizer(bool __state)
        {
            Floors.ThingPhase_Finalizer(__state);
        }

        public static void SinkCut_Postfix(SectionLayer __instance)
        {
            try
            {
                SinkHoles.CutGround(__instance, false);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Cutting the ground under a sunk hole failed: " + e, 93611391);
            }
        }

        public static void SinkCutColours_Postfix(SectionLayer __instance)
        {
            try
            {
                SinkHoles.CutGround(__instance, true);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Cutting the ground under a sunk hole failed: " + e, 93611391);
            }
        }

        private static void NoteShapeChange(Thing t, Map map)
        {
            if (t == null || map == null || t.def == null || !View3D.Active || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                if (t.def.category == ThingCategory.Building)
                {
                    IsoCellMap.Dirty(map, t.OccupiedRect());   // the fog lid lies on what stands there
                    SinkHoles.NoteChange(t, map);               // a pit: the ground under and round it
                    if (t is Building_Door && IsoRimMod.Settings.cutRooms && ShaderCut.On)
                    {
                        IsoCellMap.Dirty(map, CellRect.CenteredOn(t.Position, WallBoxes.RockReach + 1));   // (shader cut-away)
                    }
                    else if (t is Building_Door && IsoRimMod.Settings.cutRooms)
                    {
                        // A door makes (or unmakes) a room: the walls and rock in front of it may change.
                        MapSections.QueueAround(map, t.Position, WallBoxes.RockReach + 1, typeof(SectionLayer_IsoRimWalls), true);
                    }
                    if (SectionLayer_IsoRimBodies.Handles(t))
                    {
                        // The game does not redraw the map mesh for things it draws every frame.
                        MapSections.QueueAround(map, t.Position, 0, typeof(SectionLayer_IsoRimBodies), true);
                    }
                    if (WallBoxes.IsWallForm(Forms.For(t.def)))
                    {
                        // Its own block and the ones beside it: a neighbour's side shows or hides. (A wall drawn from a
                        // model is a block to its neighbours too, WallBoxes.ModelWall.)
                        MapSections.QueueAround(map, t.Position, WallBoxes.RockReach + 1, typeof(SectionLayer_IsoRimWalls), true);
                        if (IsoRimMod.Settings.showRoofs)
                        {
                            MapSections.QueueAround(map, t.Position, SectionLayer_IsoRimRoofs.Reach, typeof(SectionLayer_IsoRimRoofs), true);
                        }
                    }
                }
                else if (t is Plant && SectionLayer_IsoRimTrees.Handles(t))
                {
                    // Marked for the game to redraw (at once on screen, when it comes into view off screen) rather
                    // than queued: a forest off screen is not rebuilt block after block while the game runs.
                    SectionLayer_IsoRimTrees.Mark(map, t.Position);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Noting a change to the map failed: " + e, 93611121);
            }
        }

        public static void DrawGhostThing_Postfix(IntVec3 __0, Rot4 __1, ThingDef __2, Color __4, ThingDef __8)
        {
            if (!View3D.Ready)
            {
                return;
            }
            try
            {
                // The style the designator places (a Morbid altar): its own model, as the game's ghost sprite.
                Designator_Place place = Find.DesignatorManager != null ? Find.DesignatorManager.SelectedDesignator as Designator_Place : null;
                Ghosts.DrawPreview(__2, __0, __1, __4, __8, place != null && place.PlacingDef == __2 ? place.ThingStyleDefForPreview : null);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the placement preview failed: " + e, 93611131);
            }
        }

        private static bool inDraggerGUI;

        public static void DraggerOnGUI_Prefix()
        {
            inDraggerGUI = true;
        }

        public static void DraggerOnGUI_Finalizer()
        {
            inDraggerGUI = false;
        }

        public static bool WidgetsDrawBox_Prefix()
        {
            return !(inDraggerGUI && View3D.Ready);
        }

        public static bool DrawSelectionBracketFor_Prefix(object __0)
        {
            Thing t = __0 as Thing;
            if (!View3D.Ready || t == null || !t.Spawned || t.Map != Find.CurrentMap)
            {
                return true;
            }
            try
            {
                Interaction.Bracket(t);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a selection bracket failed: " + e, 93611161);
                return true;
            }
        }

        // Puts whatever 3D shape is under the pointer first in the list of things clicked on.
        public static void ThingsUnderMouse_Postfix(TargetingParameters __2, ref List<Thing> __result)
        {
            if (!View3D.Ready || __result == null)
            {
                return;
            }
            try
            {
                List<Thing> hits = Interaction.PickUnderPointer(Find.CurrentMap);
                for (int i = hits.Count - 1; i >= 0; i--)
                {
                    Thing t = hits[i];
                    if (__2 != null && !__2.CanTarget(new TargetInfo(t)))
                    {
                        continue;
                    }
                    __result.Remove(t);
                    __result.Insert(0, t);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Picking a 3D shape under the pointer failed: " + e, 93611162);
            }
        }

        private static int windowsLogged;

        public static void WindowStackAdd_Postfix(Window window)
        {
            if (!View3D.Active || window == null || !(window.forcePause || window.absorbInputAroundWindow))
            {
                return;
            }
            windowsLogged++;
            CrashTrail.Note("window opened in 3D: " + window.GetType().Name, windowsLogged <= 40);   // the crash report keeps the latest
        }

        // With the -isorimdiag switch (see ModLog.Diag), writes what the 3D view depends on to the
        // log, so a report from a new game version shows straight away what has changed.
        private static void Diagnostics()
        {
            if (!ModLog.Diag)
            {
                return;
            }
            try
            {
                Material solid = R3DMaterials.Solid;
                Shader cutout = ShaderDatabase.Cutout;
                ModLog.Note("[IsoRim] Diagnostics: game " + VersionControl.CurrentVersionStringWithRev
                    + " | solid shader " + (solid != null && solid.shader != null ? solid.shader.name : "MISSING")
                    + " (has _Color " + (solid != null && solid.HasProperty("_Color")) + ")"
                    + " | cutout shader " + (cutout != null ? cutout.name : "MISSING")
                    + " | game shadow material " + (R3DMaterials.GameShadow != null ? R3DMaterials.GameShadow.shader.name : "MISSING"));
                List<string> layers = new List<string>();
                foreach (Type t in typeof(SectionLayer).AllSubclassesNonAbstract())
                {
                    layers.Add(t.Name);
                }
                ModLog.Note("[IsoRim] Diagnostics: map layers: " + string.Join(", ", layers));
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Diagnostics failed: " + e.Message);
            }
        }

        public static bool DrawClippers_Prefix()
        {
            return !(View3D.Ready && IsoRimMod.Settings.mapEdge3D);
        }

        public static bool DrawSilhouettes_Prefix()
        {
            return !View3D.Ready;
        }

        // Above a zoom the game draws a person from a cached image of it (GlobalTextureAtlasManager) instead of its
        // render tree, which then neither runs nor asks for new materials: Facial Animation's faces stop moving, and
        // in the 3D view the zoom says little about how close a person is seen. Drawn in full while the 3D view is
        // on, with Facial Animation active and the option set.
        private static bool? facialAnimation;

        // The game's wound drawer forgot its draw calls (a hediff changed): the 3D marks are worked out again (WoundMarks).
        public static void WoundCacheCleared_Postfix(PawnOverlayDrawer __instance)
        {
            WoundMarks.Cleared(__instance);
        }

        public static void PreRenderCache_Prefix(ref bool disableCache)
        {
            if (disableCache || !View3D.Ready)
            {
                return;
            }
            if (!facialAnimation.HasValue)
            {
                facialAnimation = ModsConfig.IsActive("Nals.FacialAnimation");
            }
            if (IsoRimMod.Settings.fullPawnDraw && facialAnimation.Value)
            {
                disableCache = true;
            }
        }

        // The game chooses a standing pawn's sprites by its facing on the map (pawn.Rotation), assuming north is up
        // the screen. Stood upright square to a turned camera (RenderPawnAt_Prefix) the sprite must instead show the
        // facing as the camera sees it (View3D.SeenFacing), or a pawn walking away from the camera shows its side.
        // __1 is rotOverride. Given by the caller only for set poses (statues, entities on a holding platform, the order
        // ghost), which keep theirs. Other mods may set it in their own prefixes to change the facing drawn (Yayo's
        // Animation does, for every animated pawn, walking included): that is a map facing too, so the camera turn is
        // applied last, after every other prefix, to whatever facing the game and those mods decided, and exactly once.
        // Lying and dead pawns stay flat on the ground and keep the map facing. Runs on the game's worker threads,
        // ahead of drawing.
        [ThreadStatic] private static bool rotFromCaller;

        [HarmonyPriority(Priority.First)]
        public static void PreRenderResultsCaller_Prefix(Rot4? __1)
        {
            rotFromCaller = __1.HasValue;
        }

        [HarmonyPriority(Priority.Last)]
        public static void PreRenderResults_Prefix(PawnRenderer __instance, ref Rot4? __1, bool __2)
        {
            if (rotFromCaller || !View3D.Ready || !IsoRimMod.Settings.uprightPawns)
            {
                return;
            }
            try
            {
                Pawn pawn = pawnOfRenderer != null ? pawnOfRenderer(__instance) : null;
                if (pawn == null || pawn.Dead || pawn.GetPosture() != PawnPosture.Standing)
                {
                    return;
                }
                Rot4 facing = __1 ?? pawn.Rotation;
                // Aiming, the sprite faces the way its gun points on screen (CardAim), so body and gun agree as in the game
                // (unless another mod turned the body away from the game's facing).
                float aimGame, aimSeen;
                Rot4 seen = facing == pawn.Rotation && CardAim(pawn, __2, out aimGame, out aimSeen) ? Rot4.FromAngleFlat(aimSeen) : View3D.SeenFacing(facing);
                if (seen != facing || __1.HasValue)
                {
                    __1 = seen;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Turning a pawn sprite's facing with the camera failed: " + e, 93611191);
            }
        }

        // What an upright pawn sprite carries sits beside it by the pawn's map facing (CalculateCarriedDrawPos reads
        // pawn.Rotation, not the drawn facing): moved to where the game puts it for the facing seen. Only the game's
        // plain placement is changed (a job's own placement, or a pushed cart, is left alone).
        public static void CarriedDrawPos_Prefix(Vector3 __2, out Vector3 __state)
        {
            __state = __2;
        }

        public static void CarriedDrawPos_Postfix(Pawn __0, ref Vector3 __2, Vector3 __state)
        {
            if (!pawnUpright || __0 == null || __0 != uprightPawn || !View3D.Ready)
            {
                return;
            }
            try
            {
                Rot4 seen = View3D.SeenFacing(__0.Rotation);
                if (seen != __0.Rotation && (__2 - (__state + CarryOffset(__0, __0.Rotation))).sqrMagnitude < 1e-8f)
                {
                    __2 = __state + CarryOffset(__0, seen);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Placing what an upright pawn carries failed: " + e, 93611192);
            }
        }

        // The game's plain offset of a carried thing for a facing (PawnRenderUtility.CalculateCarriedDrawPos).
        private static Vector3 CarryOffset(Pawn pawn, Rot4 facing)
        {
            Vector3 o = new Vector3(0f, 0f, -0.1f);
            switch (facing.AsInt)
            {
                case 0:
                    o.z = 0f;
                    break;
                case 1:
                    o.x = 0.18f;
                    break;
                case 3:
                    o.x = -0.18f;
                    break;
            }
            if (pawn.DevelopmentalStage == DevelopmentalStage.Child)
            {
                o.z -= 0.1f;
            }
            o.y += facing == Rot4.North ? -0.03658537f : 0.03658537f;
            return o;
        }

        public static bool PreRender_Prefix(PawnRenderer __instance)
        {
            try
            {
                // Only people: the game still uses an animal's prepared sprite in a few places
                // the mod does not take over (the sprite then came out garbled when the animal
                // turned), and animals are cheap to prepare anyway.
                Pawn pawn = pawnOfRenderer != null ? pawnOfRenderer(__instance) : null;
                if (pawn == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
                {
                    return true;
                }
                // Kept while another mod animates the body (BodyAnims): such mods work out their animation here.
                return !DrawnAsModel(pawn) || BodyAnims.Active;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Skipping sprite work for a 3D pawn failed: " + e, 93611190);
                return true;
            }
        }

        public static void RenderPawnAt_Finalizer()
        {
            renderDepth = Math.Max(0, renderDepth - 1);
            if (renderDepth > 0 && nestedDraws.Count > 0)
            {
                PawnDrawState s = nestedDraws.Pop();   // back to the outer pawn's drawing (RenderPawnAt_Prefix)
                DrawHook.active = s.active;
                DrawHook.billboard = s.billboard;
                DrawHook.skip = s.skip;
                DrawHook.xform = s.xform;
                pawnUpright = s.upright;
                uprightPawn = s.pawn;
                pawnFeet = s.feet;
                pawnLift = s.lift;
                pawnSize = s.size;
                cardAiming = s.aiming;
                cardAimGame = s.aimGame;
                cardAimSeen = s.aimSeen;
                return;
            }
            nestedDraws.Clear();
            DrawHook.active = false;
            DrawHook.billboard = false;
            DrawHook.skip = false;
            pawnUpright = false;
            cardAiming = false;
        }

        // Pawns drawn inside another pawn's drawing (RenderPawnAt_Prefix): how deep, and the outer pawns' drawing state.
        private static int renderDepth;

        // A sitting card's hips, of its sprite height up from its feet.
        private const float CardHips = 0.2f;

        private struct PawnDrawState
        {
            public bool active, billboard, skip, upright, aiming;
            public Matrix4x4 xform;
            public Pawn pawn;
            public Vector3 feet;
            public float lift, size, aimGame, aimSeen;
        }

        private static readonly Stack<PawnDrawState> nestedDraws = new Stack<PawnDrawState>();

        // How tall a pawn's upright card is (its sprite's draw height; people 1.5), and where its feet stand: the bottom of
        // the sprite's figure, slid to the side of the pawn's spot nearest the camera whichever way the camera faces.
        internal static float CardHeight(Pawn pawn)
        {
            float spriteHeight = 1.5f;
            if (pawn != null && pawn.RaceProps != null && !pawn.RaceProps.Humanlike && pawn.ageTracker != null)
            {
                PawnKindLifeStage stage = pawn.ageTracker.CurKindLifeStage;
                if (stage != null && stage.bodyGraphicData != null)
                {
                    spriteHeight = stage.bodyGraphicData.drawSize.y;
                }
            }
            return spriteHeight;
        }

        internal static Vector3 CardFeet(Pawn pawn, Vector3 drawPos, float spriteHeight)
        {
            return drawPos - View3D.GroundForward * (spriteHeight * IsoRimMod.Settings.feetOffset);
        }

        // Aiming on a 2D card (the game's own weapon sprite, stood up with the card by DrawHook).
        private static bool cardAiming;     // the upright pawn being drawn is aiming (CardAim)
        private static float cardAimGame;   // the aim angle the game uses (map space)
        private static float cardAimSeen;   // where that aim points on screen (View3D.ScreenAim)

        // Whether the game draws this pawn aiming its weapon (PawnRenderUtility.DrawEquipmentAndApparelExtras: same
        // tests), with the game's aim angle (map space, or the verb's override) and the angle that aim shows on a
        // camera-facing card, from the drawn positions (an aim between floors tilts up or down the screen).
        // Thread-safe reads only: also called from PreRenderResults_Prefix on the game's worker threads.
        internal static bool CardAim(Pawn pawn, bool neverAim, out float game, out float seen)
        {
            game = 0f;
            seen = 0f;
            if (neverAim || pawn.equipment == null || pawn.equipment.Primary == null)
            {
                return false;
            }
            Verse.AI.Job job = pawn.CurJob;
            if (job == null || job.def == null || job.def.neverShowWeapon)
            {
                return false;
            }
            Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
            if (busy == null || busy.neverAimWeapon || !busy.focusTarg.IsValid)
            {
                return false;
            }
            Vector3 from = pawn.DrawPos;
            Vector3 to = busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted();
            bool apart = (to - from).MagnitudeHorizontalSquared() > 0.001f;
            if (apart)
            {
                game = (to - from).AngleFlat();
            }
            Verb verb = pawn.CurrentEffectiveVerb;
            if (verb != null && verb.AimAngleOverride.HasValue)
            {
                game = verb.AimAngleOverride.Value;
                apart = false;
            }
            if (!apart)
            {
                seen = View3D.ScreenAim(Quaternion.AngleAxis(game, Vector3.up) * Vector3.forward, 0f);
                return true;
            }
            Vector3 a = from, b = to;
            Floors.Drawn(ref a);
            Floors.Drawn(ref b);
            float rise = ((b.y - to.y) - (a.y - from.y)) * View3D.Squash;   // the floors' height between them, in cells
            Vector3 d = b - a;
            d.y = 0f;
            seen = View3D.ScreenAim(d, rise);
            return true;
        }

        // The weapon in a standing pawn's hands. __0 is the weapon, __1 where it is drawn and __2 the direction it
        // points, in degrees clockwise from north.
        // On a 2D card the game draws its own sprite (DrawHook stands it up with the card, with the game's offsets,
        // flip, recoil and layer); only an aim is turned, about the pawn, from its map angle to the angle it shows on
        // screen, before any other mod's prefix sees it (they then work on the turned pose).
        // A 3D figure's weapon is drawn by the game too, caught and put on the figure (FigureWeapons).
        [HarmonyPriority(Priority.First)]
        public static bool DrawEquipmentAiming_Prefix(Thing __0, ref Vector3 __1, ref float __2)
        {
            FigureWeapons.Weapon(__0);
            if (!pawnUpright || __0 == null || !View3D.Ready)
            {
                return true;
            }
            try
            {
                if (cardAiming && uprightPawn != null)
                {
                    float turn = Mathf.DeltaAngle(cardAimGame, cardAimSeen);
                    float factor = uprightPawn.ageTracker != null ? uprightPawn.ageTracker.CurLifeStage.equipmentDrawDistanceFactor : 1f;
                    Vector3 reach = new Vector3(0f, 0f, 0.4f + __0.def.equippedDistanceOffset).RotatedBy(cardAimGame) * factor;
                    Vector3 centre = __1 - reach;
                    Vector3 off = (__1 - centre).RotatedBy(turn);
                    __1 = new Vector3(centre.x + off.x, __1.y, centre.z + off.z);
                    __2 = Mathf.Repeat(__2 + turn, 360f);
                    // Where the barrel ends as drawn: shots start there (Projectiles.CardTip).
                    Projectiles.BeginTip(uprightPawn, __0, pawnFeet.y);
                }
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a held weapon failed: " + e, 93611016);
                return true;
            }
        }

        // The weapon has been drawn (or a mod drew it its own way): no longer looking for its barrel tip.
        public static void DrawEquipmentAiming_Finalizer()
        {
            Projectiles.EndTip();
            FigureWeapons.Weapon(null);
        }

        public static void DrawCarriedWeapon_Prefix()
        {
            FigureWeapons.DrawCarriedWeapon_Prefix();
        }

        public static void DrawCarriedWeapon_Finalizer()
        {
            FigureWeapons.DrawCarriedWeapon_Finalizer();
        }

        public static IEnumerable<CodeInstruction> CatchDraws_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return FigureWeapons.CatchDraws_Transpiler(instructions);
        }

        // A projectile class's own DrawAt: IsoRim draws it when it is indirect fire (Projectiles.OverrideDrawAt); either way
        // the mod's DrawAt runs in a scope whose flecks follow the drawn projectile (closed by the finalizer).
        public static bool ProjectileOverrideDrawAt_Prefix(Projectile __instance, Vector3 __0)
        {
            try
            {
                return Projectiles.OverrideDrawAt(__instance, __0);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a projectile in 3D failed: " + e, 93611502);
                return true;
            }
        }

        public static void ProjectileOverrideDrawAt_Finalizer(Projectile __instance)
        {
            Projectiles.EndOwnDraw(__instance);
        }

        // A fleck spawned inside a projectile's own DrawAt (Projectiles.OwnFleck).
        public static void ProjectileFleck_Prefix(ref FleckCreationData fleckData)
        {
            try
            {
                Projectiles.OwnFleck(ref fleckData);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Moving a projectile's fleck onto its 3D flight failed: " + e, 93611530);
            }
        }

        // A projectile drawn at its height in 3D (Projectiles). __0 is where the game would draw it.
        public static bool ProjectileDrawAt_Prefix(Projectile __instance, Vector3 __0)
        {
            try
            {
                if (Projectiles.OwnDrawSkipsBase(__instance))
                {
                    return false;   // a lobbed projectile's own DrawAt calling the game's: IsoRim drew it on the arc already
                }
                return Projectiles.DrawAt(__instance, __0);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a projectile in 3D failed: " + e, 93611502);
                return true;
            }
        }

        // A held weapon as a real object (from its art, or its model), lying level at position and pointing aim
        // degrees clockwise from north. False if it has neither: the caller draws the game's sprite. Its art made solid as a 3D
        // colonist holds it (GroundGear.HeldSprite), unless another mod binds a model to it (with the game's art off).
        public static bool HeldWeapon3D(Thing weapon, Vector3 position, float aim)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (GroundGear.HeldSprite(weapon.def) && SpriteSolids.DrawHeld(weapon, position, aim))
            {
                return true;
            }
            ModelEntry entry;
            if (!st.models3D || !ModelLibrary.TryGet(weapon.def, out entry) || !entry.HasModel)
            {
                return false;
            }
            DynamicModels.Draw(entry.Pick(weapon), position, aim - 90f, Vector3.one * entry.def.scale, weapon.DrawColor, R3DTextures.StuffSwatch(weapon));
            return true;
        }

        // A wind turbine drawing its blades. If the turbine has a 3D model, a turning 3D rotor is
        // drawn and the game's flat blades are thrown away.
        public static void WindPostDraw_Prefix(ThingComp __instance, out bool __state)
        {
            __state = false;
            if (!View3D.Ready)
            {
                return;
            }
            try
            {
                if (Turbines.Draw(__instance))
                {
                    __state = true;
                    DrawHook.skip = true;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a wind turbine's blades failed: " + e, 93611021);
            }
        }

        public static void WindPostDraw_Finalizer(bool __state)
        {
            if (__state)
            {
                DrawHook.skip = false;
            }
        }

        private static readonly AccessTools.FieldRef<TerrainGrid, Map> terrainMap = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");


        // The terrain at __0 changed from __1 to __2.
        public static void TerrainChanged_Postfix(TerrainGrid __instance, IntVec3 __0, TerrainDef __1, TerrainDef __2)
        {
            try
            {
                Bridges.TerrainChanged(terrainMap(__instance), __0, __1, __2);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Noting a bridge change failed: " + e, 93611472);
            }
            bool bridge = (__1 != null && __1.bridge) || (__2 != null && __2.bridge);   // the water under a bridge is drawn (Bridges)
            if (!View3D.Ready || !IsoRimMod.Settings.waterStyle || !(Ground.IsWater(__1) || Ground.IsWater(__2) || bridge))
            {
                return;
            }
            MapSections.QueueAround(terrainMap(__instance), __0, SectionLayer_IsoRimWater.Reach, typeof(SectionLayer_IsoRimWater));
        }

        // A roof was added or removed at __0.
        public static void SetRoof_Postfix(RoofGrid __instance, IntVec3 __0)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (!View3D.Active || (!st.cutRooms && !st.showRoofs) || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                List<Map> maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                {
                    if (maps[i].roofGrid == __instance)
                    {
                        if (st.cutRooms && ShaderCut.On)
                        {
                            IsoCellMap.Dirty(maps[i], CellRect.CenteredOn(__0, WallBoxes.RockReach + 1));   // (shader cut-away)
                        }
                        else if (st.cutRooms)
                        {
                            Cutaway.RoofChanged(maps[i], __0);
                            if (st.cutRock)
                            {
                                MapSections.QueueAround(maps[i], __0, WallBoxes.RockReach + 1, typeof(SectionLayer_IsoRimWalls), true);
                            }
                        }
                        if (st.showRoofs)
                        {
                            // A roof's slopes are worked out from the cells around it, so the
                            // blocks next door may need their part of the roof redrawn too.
                            MapSections.QueueAround(maps[i], __0, SectionLayer_IsoRimRoofs.Reach, typeof(SectionLayer_IsoRimRoofs));
                        }
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Noting a roof change failed: " + e, 93611022);
            }
        }

        // The sliding leaves of a door. __1 is how far they have slid open.
        public static bool DrawMovers_Prefix(Building_Door __instance, float __1)
        {
            if (!View3D.Ready || __instance == null || !IsoRimMod.Settings.doors3D)
            {
                return true;
            }
            try
            {
                if (__instance.def.size.x != 1 || __instance.def.size.z != 1)
                {
                    return true;   // wide doors without their own models (MultiTileDoorDrawAt_Prefix) keep the game's drawing
                }
                return !Doors.Draw(__instance, __1);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a door failed: " + e, 93611017);
                return true;
            }
        }

        private static Func<Building_Door, float> doorOpenPct;
        private static Func<Building_Door, bool> doorStuckOpen;
        private static Action<ThingWithComps> compsPostDraw;
        private static Func<Building_Door, float> openPctOnly;

        // The game's OpenPct of a door (0 shut .. 1 open), for one-cell doors whose leaves gather (Doors.DrawOne, Api 18).
        internal static float DoorOpenPct(Building_Door door)
        {
            if (openPctOnly == null)
            {
                openPctOnly = AccessTools.MethodDelegate<Func<Building_Door, float>>(AccessTools.PropertyGetter(typeof(Building_Door), "OpenPct"));
            }
            return openPctOnly(door);
        }

        // A wide door with its own models: drawn in 3D in place of the game's movers, frame and top
        // sprites; its comps still draw (the rest of what Building_Door.DrawAt does).
        public static bool MultiTileDoorDrawAt_Prefix(Building_MultiTileDoor __instance)
        {
            if (!View3D.Ready || __instance == null || !IsoRimMod.Settings.doors3D || !IsoRimMod.Settings.models3D)
            {
                return true;
            }
            try
            {
                ModelEntry entry;
                if (!ModelLibrary.TryGet(__instance.def, out entry) || entry.doorLeaf == null || !entry.HasModel)
                {
                    return true;
                }
                if (doorOpenPct == null)
                {
                    doorOpenPct = AccessTools.MethodDelegate<Func<Building_Door, float>>(AccessTools.PropertyGetter(typeof(Building_Door), "OpenPct"));
                    doorStuckOpen = AccessTools.MethodDelegate<Func<Building_Door, bool>>(AccessTools.PropertyGetter(typeof(Building_Door), "StuckOpen"));
                    compsPostDraw = AccessTools.MethodDelegate<Action<ThingWithComps>>(AccessTools.Method(typeof(ThingWithComps), "Comps_PostDraw"));
                }
                if (!Doors.DrawWide(__instance, entry, doorOpenPct(__instance), doorStuckOpen(__instance)))
                {
                    return true;
                }
                compsPostDraw(__instance);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a wide door failed: " + e, 93611231);
                return true;
            }
        }

        private static Action<Building_Door> doorPreDraw;

        // The game's rotation of a one-cell door from the walls beside it (Building_Door.DoorPreDraw).
        internal static void PreDrawDoor(Building_Door door)
        {
            if (doorPreDraw == null)
            {
                doorPreDraw = AccessTools.MethodDelegate<Action<Building_Door>>(AccessTools.Method(typeof(Building_Door), "DoorPreDraw"));
            }
            doorPreDraw(door);
        }

        // (Api 30) A vac barrier drawn with a glass part: its field. Building_VacBarrier.DrawAt draws the field sprite (its own
        // Graphic) after the door, only while the barrier is on; while such a door is drawn, that one draw becomes the glass
        // part across the doorway, tinted by the sprite's own colour unless a colour source or the def says otherwise.
        [ThreadStatic] private static Building_Door fieldDoor;
        [ThreadStatic] private static ModelEntry fieldEntry;

        public static void VacBarrierDrawAt_Prefix(Building_VacBarrier __instance)
        {
            ModelEntry entry = SupportedDoorModel(__instance);
            fieldDoor = entry != null && entry.glass != null ? __instance : null;
            fieldEntry = fieldDoor != null ? entry : null;
        }

        public static void VacBarrierDrawAt_Finalizer()
        {
            fieldDoor = null;
            fieldEntry = null;
        }

        public static bool GraphicDraw_Prefix(Graphic __instance, Thing thing)
        {
            if (fieldDoor == null || thing != fieldDoor)
            {
                return true;
            }
            try
            {
                Doors.DrawSupportedGlass(fieldDoor, fieldEntry, __instance.Color);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a vac barrier's field failed: " + e, 93611301);
                return true;
            }
        }

        // The supported door's model entry while it is drawn by Doors.DrawSupported; else null.
        private static ModelEntry SupportedDoorModel(Thing t)
        {
            // (Api 29: multi-cell supported doors too; the game's Building_MultiTileDoor has its own path, DrawWide)
            if (!View3D.Ready || t == null || !IsoRimMod.Settings.doors3D || !IsoRimMod.Settings.models3D || !(t is Building_SupportedDoor)
                || (t.def.size.x != 1 || t.def.size.z != 1) && t is Building_MultiTileDoor)
            {
                return null;
            }
            ModelEntry entry;
            return ModelLibrary.TryGet(t.def, out entry) && entry.HasModel ? entry : null;
        }

        // Building_SupportedDoor.DrawAt with the door's own models: the game's support and top sprites and
        // movers are left out; its comps still draw (a vac barrier then draws its field as the game does).
        public static bool SupportedDoorDrawAt_Prefix(Building_SupportedDoor __instance)
        {
            try
            {
                ModelEntry entry = SupportedDoorModel(__instance);
                if (entry == null)
                {
                    return true;
                }
                if (doorOpenPct == null)
                {
                    doorOpenPct = AccessTools.MethodDelegate<Func<Building_Door, float>>(AccessTools.PropertyGetter(typeof(Building_Door), "OpenPct"));
                    doorStuckOpen = AccessTools.MethodDelegate<Func<Building_Door, bool>>(AccessTools.PropertyGetter(typeof(Building_Door), "StuckOpen"));
                    compsPostDraw = AccessTools.MethodDelegate<Action<ThingWithComps>>(AccessTools.Method(typeof(ThingWithComps), "Comps_PostDraw"));
                }
                if (doorPreDraw == null)
                {
                    doorPreDraw = AccessTools.MethodDelegate<Action<Building_Door>>(AccessTools.Method(typeof(Building_Door), "DoorPreDraw"));
                }
                doorPreDraw(__instance);   // the game's rotation for a one-cell door, from the walls beside it
                if (!Doors.DrawSupported(__instance, entry, doorOpenPct(__instance)))
                {
                    return true;
                }
                compsPostDraw(__instance);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a supported door failed: " + e, 93611281);
                return true;
            }
        }

        // The hacking terminal of a door drawn by Doors.DrawSupported is its 3D model (hackTerminalModel).
        public static bool HackablePostDraw_Prefix(CompHackable __instance)
        {
            try
            {
                ModelEntry entry = SupportedDoorModel(__instance.parent);
                return entry == null || entry.hackTerminal == null;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Checking a door's hacking terminal failed: " + e, 93611282);
                return true;
            }
        }

        public static bool ThingDrawAt_Prefix(Thing __instance)
        {
            try
            {
                if (__instance.def.drawerType == DrawerType.RealtimeOnly)
                {
                    if (LiveItems.Draw(__instance))
                    {
                        return false;   // an item drawn every frame (a book on the ground): solid, as items in the map mesh
                    }
                    bool held = SectionLayer_IsoRimBodies.StandsIn(__instance);
                    if (Probe.On)
                    {
                        Probe.CountBody(__instance, held);
                    }
                    if (held)
                    {
                        SectionLayer_IsoRimBodies.Track(__instance);
                        return false;   // the baked model stands in for the body sprite
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Checking a building's 3D model failed: " + e, 93611022);
            }
            return true;
        }

        // ThingWithComps.DrawAt called directly (not virtually): what Building_Bookcase.DrawAt
        // runs as base.DrawAt before its books and bookends (the body, held back by
        // ThingDrawAt_Prefix, then the comps).
        private static Action<Thing, Vector3, bool> bookcaseBaseDraw;
        private static bool bookcaseBaseTried;

        private static Action<Thing, Vector3, bool> BookcaseBaseDraw()
        {
            if (!bookcaseBaseTried)
            {
                bookcaseBaseTried = true;
                MethodInfo target = AccessTools.DeclaredMethod(typeof(ThingWithComps), "DrawAt", new[] { typeof(Vector3), typeof(bool) });
                if (target != null)
                {
                    DynamicMethod dm = new DynamicMethod("IsoRim_BookcaseBaseDraw", null, new[] { typeof(Thing), typeof(Vector3), typeof(bool) },
                        typeof(Patches).Module, true);
                    ILGenerator il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, typeof(ThingWithComps));
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Call, target);
                    il.Emit(OpCodes.Ret);
                    bookcaseBaseDraw = (Action<Thing, Vector3, bool>)dm.CreateDelegate(typeof(Action<Thing, Vector3, bool>));
                }
            }
            return bookcaseBaseDraw;
        }

        public static bool BookcaseDrawAt_Prefix(Building_Bookcase __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                if (!SectionLayer_IsoRimBodies.StandsIn(__instance))
                {
                    return true;
                }
                Action<Thing, Vector3, bool> baseDraw = BookcaseBaseDraw();
                if (baseDraw == null)
                {
                    return true;
                }
                // As the game's own: base.DrawAt a little lower, then (left out) the books and bookends.
                baseDraw(__instance, drawLoc - Altitudes.AltIncVect * 2f, flip);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a bookcase failed: " + e, 93611121);
                return true;
            }
        }

        // True while the thing is drawn as its 3D model.
        private static bool ModelStandsIn(Thing t)
        {
            return t != null && View3D.Ready && Forms.For(t.def).form == Form3D.Model;
        }

        // The gibbet cage's model has its own cage top: the game's flat top sprite (drawn after
        // base.DrawAt) is left out, the comps still drawn. The corpse is drawn by DynamicDrawPhaseAt.
        public static bool GibbetDrawAt_Prefix(Building_GibbetCage __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                Action<Thing, Vector3, bool> baseDraw = ModelStandsIn(__instance) ? BookcaseBaseDraw() : null;
                if (baseDraw == null)
                {
                    return true;
                }
                baseDraw(__instance, drawLoc, flip);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a gibbet cage failed: " + e, 93611251);
                return true;
            }
        }

        // The cave exit's DrawAt draws only its flat rope (no base.DrawAt): left out while the model, which has the
        // rope as a part, stands in.
        public static bool CaveExitDrawAt_Prefix(CaveExit __instance)
        {
            try
            {
                return !ModelStandsIn(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a cave exit failed: " + e, 93617021);
                return true;
            }
        }

        // A dryad cocoon or healing pod with its 3D shell: the dryad inside is drawn as the game
        // does (raised to the shell's floor by the draw scope), the flat front of the sphere left out.
        public static bool DryadHolderPostDraw_Prefix(CompDryadHolder __instance)
        {
            try
            {
                if (__instance.parent == null || !ModelStandsIn(__instance.parent))
                {
                    return true;
                }
                if (__instance.Props.drawContents)
                {
                    ThingOwner held = __instance.GetDirectlyHeldThings();
                    Vector3 at = __instance.parent.Position.ToVector3ShiftedWithAltitude(AltitudeLayer.BuildingOnTop);
                    for (int i = 0; i < held.Count; i++)
                    {
                        held[i].DrawNowAt(at);
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a dryad cocoon failed: " + e, 93611252);
                return true;
            }
        }

        public static bool MechGestatorDrawAt_Prefix(Building_MechGestator __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                ModelEntry entry = Glass.EntryFor(__instance);
                Action<Thing, Vector3, bool> baseDraw = entry != null ? BookcaseBaseDraw() : null;
                if (baseDraw == null)
                {
                    return true;
                }
                Glass.DrawMechGestator(__instance, entry, drawLoc, flip, baseDraw);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a mech gestator failed: " + e, 93611262);
                return true;
            }
        }

        public static bool GrowthVatDrawAt_Prefix(Building_GrowthVat __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                ModelEntry entry = Glass.EntryFor(__instance);
                Action<Thing, Vector3, bool> baseDraw = entry != null ? BookcaseBaseDraw() : null;
                if (baseDraw == null)
                {
                    return true;
                }
                Glass.DrawGrowthVat(__instance, entry, drawLoc, flip, baseDraw);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a growth vat failed: " + e, 93611263);
                return true;
            }
        }

        // An occupied holding platform drawn as its model: the body (held back) and comps as the game's
        // base.DrawAt, then the chains in 3D. An empty one keeps the game's drawing (loose chains on the deck).
        public static bool HoldingPlatformDrawAt_Prefix(Building_HoldingPlatform __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                if (__instance.HeldPawn == null || !SectionLayer_IsoRimBodies.StandsIn(__instance))
                {
                    return true;
                }
                ModelEntry entry = Forms.For(__instance.def).model;
                Action<Thing, Vector3, bool> baseDraw = entry != null && entry.def.chainAnchors != null ? BookcaseBaseDraw() : null;
                if (baseDraw == null)
                {
                    return true;
                }
                baseDraw(__instance, drawLoc, flip);
                HoldingChains.Draw(__instance, entry);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a holding platform's chains failed: " + e, 93611291);
                return true;
            }
        }

        // An outfit stand drawn as its model (stand and body baked, SectionLayer_IsoRimBodies): its head model
        // and what it shows (Stands.Draw) in place of the game's flat base, body, head and apparel.
        public static bool OutfitStandDrawAt_Prefix(Building_OutfitStand __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                if (!SectionLayer_IsoRimBodies.StandsIn(__instance))
                {
                    return true;
                }
                ModelEntry entry = Forms.For(__instance.def).model;
                return entry == null || !Stands.Draw(__instance, entry, drawLoc);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing an outfit stand failed: " + e, 93611292);
                return true;
            }
        }

        // CompDrawAdditionalGraphics.PostDraw: left out while the thing is drawn as a model with hideExtraGraphics.
        public static bool DrawAdditionalGraphicsPostDraw_Prefix(ThingComp __instance)
        {
            try
            {
                Thing t = __instance.parent;
                if (!ModelStandsIn(t))
                {
                    return true;
                }
                ModelEntry entry = Forms.For(t.def).model;
                return entry == null || !entry.def.hideExtraGraphics;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Checking a building's extra sprites failed: " + e, 93611293);
                return true;
            }
        }

        public static bool AtomizerDrawAt_Prefix(Building_WastepackAtomizer __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                ModelEntry entry = Glass.EntryFor(__instance);
                Action<Thing, Vector3, bool> baseDraw = entry != null ? BookcaseBaseDraw() : null;
                if (baseDraw == null)
                {
                    return true;
                }
                Glass.DrawAtomizer(__instance, entry, drawLoc, flip, baseDraw);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a wastepack atomizer failed: " + e, 93611264);
                return true;
            }
        }

        public static bool CableConnectionPostDraw_Prefix(CompCableConnection __instance)
        {
            try
            {
                Thing t = __instance.parent;
                if (t == null || !View3D.Ready || !IsoRimMod.Settings.models3D)
                {
                    return true;
                }
                ThingForm form = Forms.For(t.def);
                if (form.form != Form3D.Model || form.model == null || form.model.def.cableAnchors.NullOrEmpty())
                {
                    return true;
                }
                return !Cables.Draw(__instance, form.model);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing harvester cables failed: " + e, 93611272);
                return true;
            }
        }

        public static bool BiosculpterPostDraw_Prefix(CompBiosculpterPod __instance)
        {
            try
            {
                ModelEntry entry = __instance.parent != null ? Glass.EntryFor(__instance.parent) : null;
                if (entry == null)
                {
                    return true;
                }
                Glass.DrawBiosculpter(__instance, entry);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a biosculpter pod failed: " + e, 93611265);
                return true;
            }
        }

        public static void BookcaseContents_Postfix(Building_Bookcase __instance)
        {
            try
            {
                if (SectionLayer_IsoRimBodies.Handles(__instance))
                {
                    MapSections.QueueAround(__instance.Map, __instance.Position, 0, typeof(SectionLayer_IsoRimBodies), true);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Updating a bookcase's books failed: " + e, 93611122);
            }
        }

        public static void BodyColorChanged_Postfix(Thing __instance)
        {
            try
            {
                if (SectionLayer_IsoRimBodies.Handles(__instance))
                {
                    MapSections.QueueAround(__instance.Map, __instance.Position, 0, typeof(SectionLayer_IsoRimBodies), true);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Repainting a building's 3D model failed: " + e, 93611123);
            }
        }

        public static bool GravEngineDrawAt_Prefix(Building_GravEngine __instance, Vector3 drawLoc, bool flip)
        {
            return Bobbers.GravEngineDrawAt_Prefix(__instance, drawLoc, flip);
        }

        public static bool CerebrexDrawAt_Prefix(CompCerebrexCore __instance)
        {
            return Bobbers.CerebrexDrawAt_Prefix(__instance);
        }

        public static bool MechRelayDrawAt_Prefix(Building_MechRelay __instance, Vector3 drawLoc)
        {
            return Bobbers.MechRelayDrawAt_Prefix(__instance, drawLoc);
        }

        public static bool FleshmassHeartDrawAt_Prefix(Building_FleshmassHeart __instance, Vector3 drawLoc, bool flip)
        {
            return Pulses.HeartDrawAt_Prefix(__instance, drawLoc, flip);
        }

        public static bool SkyfallerDrawAt_Prefix(Skyfaller __instance, Vector3 __0)
        {
            try
            {
                return Skyfallers.DrawAt(__instance, __0);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a skyfaller in 3D failed: " + e, 93611511);
                return true;
            }
        }

        public static bool OrbitalBeamPostDraw_Prefix(CompOrbitalBeam __instance)
        {
            try
            {
                return SkyStrikes.BeamPostDraw(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing an orbital beam in 3D failed: " + e, 93611521);
                return true;
            }
        }

        public static bool BombardmentDrawAt_Prefix(Bombardment __instance)
        {
            try
            {
                return SkyStrikes.BombardmentDrawAt(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a bombardment in 3D failed: " + e, 93611522);
                return true;
            }
        }

        public static bool LightningDraw_Prefix(WeatherEvent_LightningFlash __instance)
        {
            try
            {
                return SkyStrikes.LightningDraw(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a lightning bolt in 3D failed: " + e, 93611523);
                return true;
            }
        }

        public static bool StatueDrawAt_Prefix(CompStatue __instance, Vector3 __0)
        {
            return Statues.DrawAt_Prefix(__instance, __0);
        }

        public static bool MoteDrawAt_Prefix(Mote __instance)
        {
            return Spinners.MoteDrawAt_Prefix(__instance);
        }

        public static bool EmptyStatePostDraw_Prefix(ThingComp __instance)
        {
            return __instance.parent == null || !(SectionLayer_IsoRimBodies.StandsIn(__instance.parent) || StateModels.StandsIn(__instance.parent));
        }

        public static void FireOverlayPostDraw_Prefix(ThingComp __instance, out bool __state)
        {
            __state = false;
            if (__instance.parent != null && !DrawHook.skip && SectionLayer_IsoRimBodies.FlameStandsIn(__instance.parent))
            {
                __state = true;
                DrawHook.skip = true;   // the fire sprite goes through DrawHook (Graphic draws are redirected)
            }
        }

        public static void FireOverlayPostDraw_Finalizer(bool __state)
        {
            if (__state)
            {
                DrawHook.skip = false;
            }
        }

        // A burning fire (the Fire thing) is the campfire's 3D flame in place of its flat sprite (Effects.BurningFire).
        public static bool FlickerDraw_Prefix(Thing thing)
        {
            Fire fire = thing as Fire;
            if (fire == null || !View3D.Ready || DrawHook.skip)
            {
                return true;
            }
            return !Effects.BurningFire(fire);
        }

        // ---------------------------------------------------------------- drafted pawns keep their facing

        // The game's UpdateRotation ends by turning a drafted pawn south (if (pawn.Drafted) pawn.Rotation = Rot4.South),
        // toward the player of the top-down view. With the camera free to turn that south means nothing, so in 3D that one
        // step is skipped: a drafted pawn keeps the way it walked, aimed or its job faces. Everything else in the method
        // (moving, aiming, job facing, fixed rotation) is the game's. A mod that skips UpdateRotation (turning its pawns
        // itself) never reaches the step. 3D off: the game's south turn as before.
        public static IEnumerable<CodeInstruction> DraftedSouth_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            MethodInfo drafted = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Drafted));
            FieldInfo south = AccessTools.Field(typeof(Rot4), nameof(Rot4.South));   // (a static readonly field: ldsfld)
            MethodInfo setRot = AccessTools.PropertySetter(typeof(Thing), nameof(Thing.Rotation));
            List<int> found = new List<int>();
            for (int i = 4; i + 1 < code.Count; i++)
            {
                if (code[i].opcode == OpCodes.Ldsfld && code[i].operand as FieldInfo == south && code[i + 1].operand as MethodInfo == setRot
                    && code.Skip(i - 4).Take(4).Any(c => c.operand as MethodInfo == drafted))
                {
                    found.Add(i + 1);
                }
            }
            if (found.Count != 1 || drafted == null || south == null || setRot == null)
            {
                ModLog.Warn("[IsoRim] Drafted pawns keep their facing: the game's drafted-south step was found " + found.Count + " times (expected 1); left as it is.");
                return code;
            }
            code[found[0]].opcode = OpCodes.Call;
            code[found[0]].operand = AccessTools.Method(typeof(Patches), nameof(DraftedSouth));
            return code;
        }

        // The game's drafted-south step (pawn.Rotation = Rot4.South), skipped in the 3D view.
        public static void DraftedSouth(Thing pawn, Rot4 rot)
        {
            if (View3D.Active)
            {
                if (Probe.On)
                {
                    Probe.NoteDraftFacing(pawn as Pawn);
                }
                return;
            }
            pawn.Rotation = rot;
        }

        // ---------------------------------------------------------------- raised things

        // What DrawHook was doing before a scope started, so it can be put back afterwards.
        public struct DrawScope
        {
            public bool changed;
            public bool wasActive;
            public Matrix4x4 wasXform;
            public bool wasBillboard;
            public float wasHeight;
            public bool wasAnchored;
            public Vector3 wasAnchor;
            public bool wasPivoted;
            public Vector3 wasPivot;
            public int uprightMark;   // a thing's drawing: UprightScopes.Depth + 1 when it started (0: not marked)
            public bool floorSet;     // the scope put the thing's upper-floor matrix on (Overhead: a pawn's status icon)
        }

        // Wraps the game's per-frame drawing of one thing. __0 is the drawing phase; only the
        // final phase runs on the main thread and actually draws.
        public static void ThingDraw_Prefix(Thing __instance, DrawPhase __0, out DrawScope __state)
        {
            __state = default(DrawScope);
            if (__0 == DrawPhase.Draw)
            {
                __state.uprightMark = UprightScopes.Depth + 1;   // an upright scope its drawing leaves open is closed after it
            }
            if (__0 != DrawPhase.Draw || !View3D.Ready || __instance == null || __instance is Pawn)
            {
                return;
            }
            try
            {
                StateModels.Track(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Checking whether a building was emptied failed: " + e, 93611211);
            }
            Mote mote = __instance as Mote;
            if (mote != null && mote.def == ThingDefOf.Mote_TempRoof)
            {
                // The game's "roof just built here" tile (MoteMaker.PlaceTempRoof) is a roof, not an icon: it lies flat
                // over its cell at the top of the walls, as the game draws it over the cell. Stood up like the icons
                // below, it showed as striped boards round anyone building a roof.
                BeginFlat(ref __state, View3D.WallHeight + RoofTileLift);
                return;
            }
            // The power beam's and the bombardment's huge ground glow lies flat on what the strike hits (SkyStrikes);
            // stood up as an overhead icon it was a 90-cell card across the screen.
            float glowLift;
            if (mote != null && SkyStrikes.FlatGlow(mote, out glowLift))
            {
                BeginFlat(ref __state, glowLift);
                return;
            }
            // A work progress bar: one card over the thing worked on, raised over its top (Bars).
            MoteProgressBar bar = mote as MoteProgressBar;
            if (bar != null && !Bars.Off)
            {
                Vector3 barAt;
                float barHeight;
                Bars.Anchor(bar, out barAt, out barHeight);
                BeginBillboard(ref __state, barHeight, false, Vector3.zero);
                if (__state.changed)
                {
                    DrawHook.hasPivot = true;
                    DrawHook.billboardPivot = barAt;
                }
                return;
            }
            // Speech bubbles and icons over people's heads stand up and float above them.
            if (mote != null && (mote.def.altitudeLayer == AltitudeLayer.MetaOverlays || mote.def.altitudeLayer == AltitudeLayer.MoteOverhead))
            {
                Pawn speaker = BubbleOwner(mote);
                if (speaker != null)
                {
                    // A speech, thought or mood bubble (and the fleeing or alert sign thrown off someone): one card over
                    // the person, laid out round them as the game shows it from above.
                    BeginBillboard(ref __state, BubbleCardHeight(speaker), false, Vector3.zero);
                    if (__state.changed)
                    {
                        DrawHook.hasPivot = true;
                        DrawHook.billboardPivot = mote.link1.LastDrawPos;
                        Pawn spoken = mote is MoteBubble mb ? mb.arrowTarget : null;
                        DrawHook.hasPointerAt = spoken != null && spoken.Spawned;
                        if (DrawHook.hasPointerAt)
                        {
                            Vector3 at = spoken.DrawPos;
                            DrawHook.pointerAt = new Vector3(at.x, Interaction.PickHeight(spoken) * 0.5f, at.z);
                        }
                    }
                    return;
                }
                // An icon in a pawn's cell (an effecter's drifting emote: a berserk pawn's bits ...): over that pawn's head
                // (Overhead), lying or standing; otherwise at a standing colonist's head height.
                Pawn under = mote.Spawned && !Overhead.Off ? mote.Position.GetFirstPawn(mote.Map) : null;
                BeginBillboard(ref __state, under != null ? Overhead.Height(under) : OverheadHeight, false, Vector3.zero);
                return;
            }
            BeginLift(__instance, ref __state);
        }

        private const float RoofTileLift = 0.03f;     // the temporary roof tile just over the wall tops

        // Lays every mesh drawn until the scope ends flat at this height (cells), whatever drawing layer the game used.
        private static void BeginFlat(ref DrawScope state, float height)
        {
            if (DrawHook.active)
            {
                return;
            }
            state.changed = true;
            state.wasActive = DrawHook.active;
            state.wasXform = DrawHook.xform;
            DrawHook.xform = View3D.LiftMatrix(height) * Matrix4x4.Scale(new Vector3(1f, 0f, 1f));
            DrawHook.active = true;
        }

        private const float OverheadHeight = 1.45f;   // above a standing colonist's head, in cells
        private const float IconGap = 0.30f;          // how far above a thing its icons float

        // Stands every mesh drawn until the scope ends upright, facing the camera, raised by height cells.
        // With anchored, the icons gather over anchor (see DrawHook.hasAnchor) and bob gently,
        // like the diamond over a Sim's head.
        private static void BeginBillboard(ref DrawScope state, float height, bool anchored, Vector3 anchor)
        {
            if (DrawHook.active)
            {
                return;
            }
            state.changed = true;
            state.wasActive = DrawHook.active;
            state.wasXform = DrawHook.xform;
            state.wasBillboard = DrawHook.billboard;
            state.wasHeight = DrawHook.billboardHeight;
            state.wasAnchored = DrawHook.hasAnchor;
            state.wasAnchor = DrawHook.billboardAnchor;
            state.wasPivoted = DrawHook.hasPivot;
            state.wasPivot = DrawHook.billboardPivot;
            if (anchored)
            {
                height += 0.05f * Mathf.Sin(Time.realtimeSinceStartup * 2.4f + anchor.x * 1.3f + anchor.z * 0.7f);
            }
            DrawHook.billboard = true;
            DrawHook.billboardHeight = height;
            DrawHook.hasAnchor = anchored;
            DrawHook.billboardAnchor = anchor;
            DrawHook.hasPivot = false;
            DrawHook.hasPointerAt = false;
            DrawHook.active = true;
        }

        // The person a bubble mote belongs to: MoteBubble (speech, thought, mood, forced job) and the signs thrown
        // off someone (MoteThrownAttached: fleeing, attacking, alerts), attached to a spawned pawn. Null otherwise.
        private static Pawn BubbleOwner(Mote mote)
        {
            if (!(mote is MoteBubble) && !(mote is MoteThrown))   // MoteThrownAttached is internal; a thrown mote counts only while attached (below)
            {
                return null;
            }
            if (!mote.link1.Linked || !mote.link1.Target.HasThing)
            {
                return null;
            }
            Pawn p = mote.link1.Target.Thing as Pawn;
            return p != null && p.Spawned ? p : null;
        }

        // How high the bubble card's hinge (the person's middle in the game's layout) stands: the game puts a
        // bubble 0.75 cells up and right of the person, so it sits beside and just over the head; lying down, low.
        private const float BubbleBelowTop = 0.6f;

        private static float BubbleCardHeight(Pawn p)
        {
            float top = p.GetPosture() == PawnPosture.Standing ? Interaction.PickHeight(p) : 0.45f;
            return Mathf.Max(0f, top - BubbleBelowTop);
        }


        // The forbidden cross: the game may draw it in a batch the mod cannot reach, so in 3D
        // it is drawn here instead, upright over the thing.
        public static bool ForbiddenOverlay_Prefix(Thing __0)
        {
            if (!View3D.Ready || __0 == null || __0 is Pawn)
            {
                return true;
            }
            try
            {
                Material forbiddenMat = ForbiddenIcon.Mat;
                if (forbiddenMat == null)
                {
                    return true;
                }
                DrawScope scope = default(DrawScope);
                Vector3 at = __0.TrueCenter();
                BeginBillboard(ref scope, Interaction.PickHeight(__0) + IconGap, true, at);
                DrawHook.DrawMesh(MeshPool.plane05, Matrix4x4.TRS(new Vector3(at.x, AltitudeLayer.MetaOverlays.AltitudeFor(), at.z), Quaternion.identity, Vector3.one), forbiddenMat, 0);
                Scope_Finalizer(scope);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a forbidden icon failed: " + e, 93611171);
                return true;
            }
        }

        private static readonly List<DesignationDef> unbatched = new List<DesignationDef>();

        public static void DrawDesignations_Prefix()
        {
            unbatched.Clear();
            if (!View3D.Ready)
            {
                return;
            }
            List<DesignationDef> defs = DefDatabase<DesignationDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                if (defs[i].shouldBatchDraw)
                {
                    defs[i].shouldBatchDraw = false;
                    unbatched.Add(defs[i]);
                }
            }
        }

        public static void DrawDesignations_Finalizer()
        {
            for (int i = 0; i < unbatched.Count; i++)
            {
                unbatched[i].shouldBatchDraw = true;
            }
            unbatched.Clear();
        }

        // A designation (mine, haul, cut, deconstruct ...): its icon floats over what it marks.
        public static void DesignationDraw_Prefix(Designation __instance, out DrawScope __state)
        {
            __state = default(DrawScope);
            if (!View3D.Ready || __instance == null)
            {
                return;
            }
            try
            {
                float h = 0f;
                LocalTargetInfo target = __instance.target;
                Map map = __instance.designationManager != null ? __instance.designationManager.map : Find.CurrentMap;
                if (map != null && MineMarks.OnTop(__instance) && target.Cell.InBounds(map))
                {
                    // the game's mine mark lies flat on the top of the block it marks (over the fog lid where fogged), as
                    // the game draws it on the ground (MineMarks); its own layer height is kept
                    if (!DrawHook.active)
                    {
                        BeginBillboard(ref __state, 0f, false, Vector3.zero);
                        DrawHook.billboard = false;
                        DrawHook.xform = MineMarks.MarkMatrix(map, target.Cell);
                    }
                    return;
                }
                if (target.HasThing)
                {
                    h = Interaction.PickHeight(target.Thing);
                }
                else if (target.Cell.IsValid && map != null)
                {
                    h = WallBoxes.BlockHeightAt(map, target.Cell);
                    Building b = target.Cell.InBounds(map) ? target.Cell.GetEdifice(map) : null;
                    if (b != null)
                    {
                        h = Mathf.Max(h, Interaction.PickHeight(b));
                    }
                }
                Vector3 anchor = target.HasThing ? target.Thing.TrueCenter() : target.Cell.ToVector3Shifted();
                BeginBillboard(ref __state, h + IconGap, true, anchor);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Raising a designation icon failed: " + e, 93611170);
            }
        }

        // The game also draws some icons in batches; the same transform is applied there.
        public static bool DrawBatch_Prefix(ref Matrix4x4 __1)
        {
            if (Emitters.On)
            {
                __1 = Emitters.Stand(__1);   // a puff from a model's nozzle (any thread)
                return true;
            }
            if (!DrawHook.active && !DrawHook.floor)
            {
                return true;
            }
            if (DrawHook.skip)
            {
                return false;
            }
            __1 = DrawHook.Final(__1);
            return true;
        }

        // Wraps the drawing of one status icon. __0 is the thing the icon belongs to.
        public static void OverlayRender_Prefix(Thing __0, out DrawScope __state)
        {
            __state = default(DrawScope);
            if (!View3D.Ready || __0 == null)
            {
                return;
            }
            if (__0 is Pawn pawn)
            {
                if (Overhead.Off)
                {
                    return;
                }
                // A status icon over a pawn (the question mark over creepjoiners, visitors and traders; a mech's
                // self-shutdown): one card over the 3D figure's head (Overhead), laid out round it as the game shows it
                // from above (the question mark sits up and right of the pawn: its middle at the head's height).
                try
                {
                    float iconBase = Overhead.Height(pawn) - OverlayAbove(pawn);
                    BeginBillboard(ref __state, Mathf.Max(0f, iconBase), false, Vector3.zero);
                    if (__state.changed)
                    {
                        DrawHook.hasPivot = true;
                        DrawHook.billboardPivot = pawn.DrawPos;
                        IconFloor(pawn, ref __state);
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Placing a pawn's status icon failed: " + e, 93611581);
                }
                return;
            }
            // Status icons (no power, forbidden, out of fuel ...) stand up and float above the thing.
            float h = 0f;
            try
            {
                h = Interaction.PickHeight(__0);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Finding the height for a status icon failed: " + e, 93611191);
            }
            BeginBillboard(ref __state, h + IconGap, true, __0.TrueCenter());
            if (__state.changed)
            {
                try
                {
                    IconFloor(__0, ref __state);   // a building on an upper floor or deck: its icons over it there
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Placing a status icon on an upper floor failed: " + e, 93611582);
                }
            }
        }

        // Puts the upper floor's matrix of the thing an icon belongs to on for the scope (nothing on the ground).
        private static void IconFloor(Thing t, ref DrawScope state)
        {
            Matrix4x4 floorM = Overhead.Floor(t);
            if (floorM != Matrix4x4.identity && !DrawHook.floor)
            {
                state.floorSet = true;
                DrawHook.floor = true;
                DrawHook.floorMatrix = floorM;
            }
        }

        // ---- the game's overlay pass (OverlayDrawer.DrawAllOverlays), one scope per thing
        // The pass walks the things with icons and draws everything for one thing before the next: the game's own
        // icons (no power, broken down, out of fuel, the question mark ...) and whatever a mod draws into the same
        // pass for that thing (its own overlay types, drawn into the pass's batch from its own code). The transpiler
        // marks each thing as the walk reaches it; everything drawn until the next one stands up over it as one card
        // (OverlayRender_Prefix). If the walk is not found the game's icon methods are wrapped one by one instead.
        private static bool statusPerThing;   // the transpiler found the walk
        private static bool statusPass;       // inside DrawAllOverlays
        private static DrawScope statusScope; // the scope of the thing being drawn

        public static IEnumerable<CodeInstruction> DrawAllOverlays_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo next = AccessTools.Method(typeof(Patches), nameof(StatusIconsFor));
            MethodInfo key = AccessTools.PropertyGetter(typeof(KeyValuePair<Thing, OverlayTypes>), "Key");
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                yield return ci;
                if (key != null && ci.Calls(key))
                {
                    // the thing just taken from the walk stays on the stack for the game; a copy goes to StatusIconsFor
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Call, next);
                    found++;
                }
            }
            statusPerThing = found > 0;
        }

        public static void DrawAllOverlays_Prefix()
        {
            statusScope = default(DrawScope);
            statusPass = true;
        }

        public static void DrawAllOverlays_Finalizer()
        {
            EndStatusIcons();
            statusPass = false;
        }

        public static void StatusIconsFor(Thing t)
        {
            if (!statusPass)
            {
                return;
            }
            EndStatusIcons();
            try
            {
                OverlayRender_Prefix(t, out statusScope);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Standing up a thing's status icons failed: " + e, 93611583);
            }
        }

        private static void EndStatusIcons()
        {
            DrawScope s = statusScope;
            statusScope = default(DrawScope);
            Scope_Finalizer(s);
        }

        // How far up the picture the game draws a pawn's status icon from the pawn (OverlayDrawer: the question mark at
        // size.z - 0.45 cells up and to the right; the self-shutdown sign on the pawn itself).
        private static float OverlayAbove(Pawn p)
        {
            return p.ShouldShowQuestionMark() ? p.def.size.z - 0.45f : 0f;
        }

        public static void Scope_Finalizer(DrawScope __state)
        {
            if (__state.floorSet)
            {
                DrawHook.floor = false;
                DrawHook.floorMatrix = Matrix4x4.identity;
            }
            if (__state.uprightMark > 0)
            {
                UprightScopes.CloseFrom(__state.uprightMark - 1);
            }
            if (__state.changed)
            {
                DrawHook.active = __state.wasActive;
                DrawHook.xform = __state.wasXform;
                DrawHook.billboard = __state.wasBillboard;
                DrawHook.billboardHeight = __state.wasHeight;
                DrawHook.hasAnchor = __state.wasAnchored;
                DrawHook.billboardAnchor = __state.wasAnchor;
                DrawHook.hasPivot = __state.wasPivoted;
                DrawHook.billboardPivot = __state.wasPivot;
            }
        }

        private static void BeginLift(Thing t, ref DrawScope state)
        {
            // Already inside another scope, such as an item in a pawn's hands: leave that in charge.
            if (DrawHook.active)
            {
                return;
            }
            try
            {
                float h = Forms.HeightOf(t);
                Fire fire = t as Fire;
                if (h <= 0f && fire != null && fire.parent == null)
                {
                    h = FireBaseHeight(fire);
                }
                if (h <= 0f)
                {
                    return;
                }
                state.changed = true;
                state.wasActive = DrawHook.active;
                state.wasXform = DrawHook.xform;
                DrawHook.xform = View3D.LiftMatrix(h);
                // What the game draws inside it (a reliquary's relic) moved to where the model holds it.
                ThingForm form = Forms.For(t.def);
                if (form.form == Form3D.Model && form.model != null && form.model.def.contentOffset != Vector3.zero)
                {
                    Vector3 shift = Quaternion.AngleAxis(form.model.Yaw(t.Rotation), Vector3.up) * form.model.def.contentOffset;
                    DrawHook.xform = Matrix4x4.Translate(new Vector3(shift.x, 0f, shift.z)) * DrawHook.xform;
                }
                DrawHook.active = true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Raising a thing's moving parts failed: " + e, 93611015);
            }
        }

        // A fire burning on a wall, a bed or anything else raised burns on its top, not inside it.
        private static float FireBaseHeight(Fire fire)
        {
            Map map = fire.Map;
            if (map == null || !fire.Position.InBounds(map))
            {
                return 0f;
            }
            float h = 0f;
            List<Thing> things = fire.Position.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing o = things[i];
                if (o != fire && !(o is Pawn) && !(o is Fire))
                {
                    h = Mathf.Max(h, Forms.HeightOf(o));
                }
            }
            return h;
        }

        // Finds every method in the pawn drawing code that hands a mesh to Unity and points it at
        // DrawHook instead. Shadows are left out so they stay on the ground.
        private static void RedirectDrawCalls()
        {
            List<Type> types = new List<Type>();
            AddTypeAndSubclasses(types, "Verse.PawnRenderer");
            AddTypeAndSubclasses(types, "Verse.PawnRenderTree");
            AddTypeAndSubclasses(types, "Verse.PawnRenderUtility");
            AddTypeAndSubclasses(types, "Verse.PawnRenderNodeWorker");
            AddTypeAndSubclasses(types, "Verse.Graphic");
            AddTypeAndSubclasses(types, "RimWorld.OverlayDrawer");
            AddTypeAndSubclasses(types, "RimWorld.TurretTop");
            AddTypeAndSubclasses(types, "RimWorld.CompPowerPlantWind");
            AddTypeAndSubclasses(types, "Verse.Designation");

            HarmonyMethod transpiler = Hook(nameof(Redirect_Transpiler));
            int count = 0;

            foreach (Type type in types)
            {
                if (type.ContainsGenericParameters || type.Name.Contains("Shadow"))
                {
                    continue;
                }
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                {
                    if (TryRedirect(m, transpiler))
                    {
                        count++;
                    }
                }
            }

            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(GenDraw)))
            {
                if ((m.Name == "DrawMeshNowOrLater" || m.Name == "DrawFillableBar") && TryRedirect(m, transpiler))
                {
                    count++;
                }
            }
            // The band node's tuning bar is drawn straight to Unity: through DrawHook it rises onto
            // the 3D node with the rest of what is drawn over it each frame, as other bars do.
            MethodInfo bandBar = AccessTools.DeclaredMethod(typeof(CompBandNode), "PostDraw");
            if (bandBar != null && TryRedirect(bandBar, transpiler))
            {
                count++;
            }
            // A bubble's icon and arrow are drawn straight to Unity: through DrawHook they stand up with the bubble.
            MethodInfo bubble = AccessTools.DeclaredMethod(typeof(MoteBubble), "DrawAt", new[] { typeof(Vector3), typeof(bool) });
            if (bubble != null && TryRedirect(bubble, Hook(nameof(MoteBubble_Transpiler))))
            {
                count++;
            }

            if (count == 0)
            {
                ModLog.Warn("[IsoRim] No drawing code could be redirected, so pawns will stay flat.");
            }
            else
            {
                ModLog.Note("[IsoRim] Drawing redirected in " + count + " methods.");
            }
        }

        private static void AddTypeAndSubclasses(List<Type> types, string typeName)
        {
            Type type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                return;
            }
            types.Add(type);
            foreach (Type sub in type.AllSubclasses())
            {
                types.Add(sub);
            }
        }

        private static bool TryRedirect(MethodInfo m, HarmonyMethod transpiler)
        {
            try
            {
                if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null)
                {
                    return false;
                }
                if (m.Name.Contains("Shadow") || m.Name.Contains("Debug"))
                {
                    return false;
                }

                bool draws = false;
                foreach (KeyValuePair<OpCode, object> instruction in PatchProcessor.ReadMethodBody(m))
                {
                    if (IsRedirectable(instruction.Value as MethodInfo))
                    {
                        draws = true;
                        break;
                    }
                }
                if (!draws)
                {
                    return false;
                }

                harmony.Patch(m, null, null, transpiler, null);
                return true;
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Could not redirect drawing in " + m.DeclaringType + "." + m.Name + ": " + e.Message);
                return false;
            }
        }

        private static bool IsRedirectable(MethodInfo callee)
        {
            return callee != null
                && callee.DeclaringType == typeof(Graphics)
                && callee.Name == "DrawMesh"
                && DrawHook.ReplacementFor(callee) != null;
        }

        private static readonly AccessTools.FieldRef<IntermittentSteamSprayer, Thing> sprayerParent = AccessTools.FieldRefAccess<IntermittentSteamSprayer, Thing>("parent");

        // Notes whose steam sprayer is ticking, so its air puffs can come out of the model's nozzle (Emitters.Spray).
        public static void SprayerTick_Prefix(IntermittentSteamSprayer __instance)
        {
            Emitters.sprayer = sprayerParent(__instance);
        }

        public static Exception SprayerTick_Finalizer(Exception __exception)
        {
            Emitters.sprayer = null;
            return __exception;
        }

        public static bool AirPuff_Prefix(Vector3 loc, Map map)
        {
            if (Emitters.sprayer == null)
            {
                return true;
            }
            try
            {
                return Emitters.Spray(loc, map);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Throwing steam from a model failed: " + e, 93611222);
                return true;
            }
        }

        public static bool FleckEmit_Prefix(CompThrownFleckEmitter __instance)
        {
            try
            {
                return Emitters.Emit(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Throwing smoke from a model failed: " + e, 93611221);
                return true;
            }
        }

        // (FleckStatic.Draw(altitude, batch): every puff, thrown or not, ends up here; the one-line
        // FleckThrown.Draw is inlined by the runtime and cannot be hooked.)
        public static void FleckDraw_Prefix(ref FleckStatic __instance, out bool __state)
        {
            __state = Emitters.Begin(ref __instance);
        }

        public static void FleckDraw_Finalizer(bool __state)
        {
            if (__state)
            {
                Emitters.End();
            }
        }

        // A blueprint the game draws every frame: its 3D ghost in place of the flat sprite where that sprite would lie in a wall.
        public static bool BlueprintDraw_Prefix(Blueprint __instance)
        {
            try
            {
                return !Ghosts.DrawPlanned(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a blueprint's 3D ghost failed: " + e, 93611561);
                return true;
            }
        }

        // A frame the same, over its work sprite.
        public static void FrameDraw_Postfix(Frame __instance)
        {
            try
            {
                Ghosts.DrawPlanned(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a frame's 3D ghost failed: " + e, 93611562);
            }
        }

        // GhostDrawer.DrawGhostThing: its graphic.DrawFromDef(loc, rot, def, extraRotation) call goes through
        // Ghosts.DrawGameGhostSprite, which leaves the flat sprite out where IsoRim's 3D preview stands in.
        public static IEnumerable<CodeInstruction> GhostSprite_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo from = AccessTools.Method(typeof(Graphic), nameof(Graphic.DrawFromDef), new[] { typeof(Vector3), typeof(Rot4), typeof(ThingDef), typeof(float) });
            MethodInfo to = AccessTools.Method(typeof(Ghosts), nameof(Ghosts.DrawGameGhostSprite));
            int swapped = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && from != null && to != null && ci.operand as MethodInfo == from)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = to;
                    swapped++;
                }
                yield return ci;
            }
            if (swapped != 1)
            {
                ModLog.Warn("[IsoRim] 3D placement preview: the game's ghost sprite call was found " + swapped + " times (expected 1)");
            }
        }

        public static IEnumerable<CodeInstruction> DesignatorCell_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo from = AccessTools.Method(typeof(Verse.UI), "MouseCell");
            MethodInfo to = AccessTools.Method(typeof(Interaction), nameof(Interaction.DesignatorMouseCell));
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Call && from != null && ci.operand as MethodInfo == from)
                {
                    ci.operand = to;
                }
                yield return ci;
            }
        }

        public static bool HighlightCells_Prefix(Designator designator, List<IntVec3> dragCells)
        {
            try
            {
                return !Interaction.DrawDragHighlight(designator, dragCells, false);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the drag highlight failed: " + e, 93611167);
                return true;
            }
        }

        public static bool HighlightThings_Prefix(Designator designator, List<IntVec3> dragCells)
        {
            try
            {
                return !Interaction.DrawDragHighlight(designator, dragCells, true);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the drag highlight failed: " + e, 93611168);
                return true;
            }
        }

        public static bool MouseoverBracket_Prefix()
        {
            try
            {
                return !Interaction.DrawBlockBracket();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the designator bracket failed: " + e, 93611164);
                return true;
            }
        }

        // The game's FleckMaker.Static(caster.Position, map, ShotFlash, muzzleFlashScale) becomes
        // Projectiles.ShotFlash(..., this): the verb is passed on for its caster and target.
        public static IEnumerable<CodeInstruction> ShotFlash_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo from = AccessTools.Method(typeof(FleckMaker), nameof(FleckMaker.Static), new[] { typeof(IntVec3), typeof(Map), typeof(FleckDef), typeof(float) });
            MethodInfo to = AccessTools.Method(typeof(Projectiles), nameof(Projectiles.ShotFlash));
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Call && from != null && to != null && ci.operand as MethodInfo == from)
                {
                    CodeInstruction verb = new CodeInstruction(OpCodes.Ldarg_0);
                    verb.labels.AddRange(ci.labels);
                    ci.labels.Clear();
                    yield return verb;
                    ci.operand = to;
                    found++;
                }
                yield return ci;
            }
            if (found == 0)
            {
                ModLog.Warn("[IsoRim] The shot flash in Verb.TryCastNextBurstShot was not found; muzzle flashes are the game's.");
            }
        }

        public static void ShieldDraw_Postfix(CompProjectileInterceptor __instance)
        {
            try
            {
                ShieldHits.Draw(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a shot on its way to a shield dome failed: " + e, 93611561);
            }
        }

        public static void ShieldIntercept_Prefix(CompProjectileInterceptor __instance)
        {
            ShieldHits.InterceptBegin(__instance);
        }

        public static void ShieldIntercept_Postfix(CompProjectileInterceptor __instance, Projectile projectile, Vector3 newExactPos, bool __result)
        {
            if (!__result)
            {
                return;
            }
            try
            {
                ShieldHits.Intercepted(__instance, projectile, newExactPos);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a shot on to a shield dome failed: " + e, 93611562);
            }
        }

        public static Exception ShieldIntercept_Finalizer(Exception __exception)
        {
            ShieldHits.InterceptEnd();
            return __exception;
        }

        public static void ShieldBombardment_Prefix(CompProjectileInterceptor __instance, Bombardment.BombardmentProjectile projectile)
        {
            try
            {
                ShieldHits.BombardmentBegin(__instance, projectile);
            }
            catch (Exception e)
            {
                ShieldHits.BombardmentEnd();
                Log.ErrorOnce("[IsoRim] Putting a shield's flash on its dome failed: " + e, 93611563);
            }
        }

        public static Exception ShieldBombardment_Finalizer(Exception __exception)
        {
            ShieldHits.BombardmentEnd();
            return __exception;
        }

        public static void ShieldTick_Postfix(CompProjectileInterceptor __instance)
        {
            try
            {
                ShieldHits.Tick(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Showing a shield's flash on its dome failed: " + e, 93611564);
            }
        }

        public static bool CreateFleck_Prefix(FleckCreationData fleckData)
        {
            return ShieldHits.CreateFleck(fleckData);
        }

        public static void TurretFlash_Prefix(FleckManager __instance, ref FleckCreationData fleckData)
        {
            try
            {
                Projectiles.TurretFlash(__instance.parent, ref fleckData);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Putting a turret's flash at its muzzle failed: " + e, 93611595);
            }
        }

        public static void OverheadFleck_Prefix(FleckManager __instance, FleckCreationData fleckData)
        {
            try
            {
                Overhead.OnFleck(__instance.parent, fleckData);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Raising an icon over a figure failed: " + e, 93611580);
            }
        }

        public static IEnumerable<CodeInstruction> ShieldDraw_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo from = AccessTools.Method(typeof(Graphics), "DrawMesh", new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(Camera), typeof(int), typeof(MaterialPropertyBlock) });
            MethodInfo to = AccessTools.Method(typeof(ShieldDomes), nameof(ShieldDomes.DrawMesh));
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Call && from != null && ci.operand as MethodInfo == from)
                {
                    ci.operand = to;
                }
                yield return ci;
            }
        }

        public static IEnumerable<CodeInstruction> Redirect_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Call)
                {
                    MethodInfo callee = ci.operand as MethodInfo;
                    if (IsRedirectable(callee))
                    {
                        ci.operand = DrawHook.ReplacementFor(callee);
                    }
                }
                yield return ci;
            }
        }

        // MoteBubble.DrawAt: the icon goes through DrawHook like the bubble itself; the arrow toward the
        // person spoken to through DrawHook.DrawPointer, which turns it to point at them on screen.
        public static IEnumerable<CodeInstruction> MoteBubble_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo pointer = AccessTools.Method(typeof(DrawHook), nameof(DrawHook.DrawPointer));
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Call)
                {
                    MethodInfo callee = ci.operand as MethodInfo;
                    if (IsRedirectable(callee))
                    {
                        ParameterInfo[] ps = callee.GetParameters();
                        bool arrow = ps.Length == 5 && ps[1].ParameterType == typeof(Vector3) && ps[2].ParameterType == typeof(Quaternion);
                        ci.operand = arrow ? pointer : DrawHook.ReplacementFor(callee);
                    }
                }
                yield return ci;
            }
        }
    }
}

namespace IsoRim
{
    // The game's forbidden cross, looked up once on the main thread at startup.
    [StaticConstructorOnStartup]
    public static class ForbiddenIcon
    {
        public static readonly Material Mat = Find();

        private static Material Find()
        {
            Type overlays = AccessTools.TypeByName("RimWorld.OverlayDrawer");
            FieldInfo f = overlays != null ? AccessTools.Field(overlays, "ForbiddenMat") : null;
            return f != null ? f.GetValue(null) as Material : null;
        }
    }
}
