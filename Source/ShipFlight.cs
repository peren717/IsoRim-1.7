using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The gravship takeoff (and later landing) cutscene in the 3D view (plan: isorim-gravship-launch-plan.md).
    //
    // Vanilla photographs the ship from above (GravshipCapturer), takes it off the map and flies that one flat picture
    // along its curves (GravshipRenderer). In 3D the picture is wrong (the capture camera looks with IsoRim's matrices)
    // and a flat sheet. Instead, just before the game takes the ship off the map (GravshipUtility.GenerateGravship),
    // a snapshot of its 3D geometry is made (ShipSnapshot): the triangles of the map's section meshes that belong to the
    // ship's cells, combined per material. Every frame of the cutscene the snapshot is flown along vanilla's own curves,
    // with vanilla's fake height (a slide up the screen) turned into a real height, and the camera follows it.
    //
    // One switch: the mode is decided when the cutscene starts (the 3D view ready); otherwise every hook passes through
    // to vanilla. While any cutscene runs, IsoRim's own camera keys are locked (View3D.ControlsLocked), as vanilla locks
    // its own camera.
    [StaticConstructorOnStartup]
    public static class ShipFlight
    {
        // Harness only (probe shipflight=0): the vanilla cutscene in 3D (before/after shots).
        public static bool Enabled = true;

        private static bool mode3D;      // this cutscene is drawn in 3D
        private static bool takeoff;
        private static ShipSnapshot snap;
        private static ShipCells cells;

        // The pose drawn this frame (true heights).
        private static bool posed;
        private static Vector3 ground;
        private static float height;
        private static Quaternion turn = Quaternion.identity;

        // The camera: the player's view before the cutscene, and the followed one.
        private static bool camSaved;
        private static Vector3 savedPos;
        private static float savedSize;
        private static bool camFollowing;
        private static Vector3 camFocus;
        private static float camSize;

        private static readonly AccessTools.FieldRef<GravshipRenderer, Gravship> rGravship = AccessTools.FieldRefAccess<GravshipRenderer, Gravship>("gravship");
        private static readonly AccessTools.FieldRef<GravshipRenderer, Vector3> rOffset = AccessTools.FieldRefAccess<GravshipRenderer, Vector3>("gravshipOffset");
        private static readonly AccessTools.FieldRef<GravshipRenderer, Vector3> rCenter = AccessTools.FieldRefAccess<GravshipRenderer, Vector3>("takeoffOrLandingPosition");
        private static readonly AccessTools.FieldRef<GravshipRenderer, Vector3> rEnginePos = AccessTools.FieldRefAccess<GravshipRenderer, Vector3>("takeoffOrLandingEnginePos");
        private static readonly AccessTools.FieldRef<GravshipRenderer, MaterialPropertyBlock> rFlameBlock = AccessTools.FieldRefAccess<GravshipRenderer, MaterialPropertyBlock>("thrusterFlameBlock");
        private static readonly AccessTools.FieldRef<GravshipRenderer, Dictionary<Thing, EventQueue>> rExhaustTimers = AccessTools.FieldRefAccess<GravshipRenderer, Dictionary<Thing, EventQueue>>("exhaustTimers");
        private static readonly AccessTools.FieldRef<CameraDriver, Vector3> camRootPos = AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");
        private static readonly AccessTools.FieldRef<WorldComponent_GravshipController, Gravship> cGravship = AccessTools.FieldRefAccess<WorldComponent_GravshipController, Gravship>("gravship");
        private static readonly AccessTools.FieldRef<GravshipRenderer, FleckSystem> rExhaust = AccessTools.FieldRefAccess<GravshipRenderer, FleckSystem>("exhaustFleckSystem");
        private static readonly Material matExtenderGlow = AccessTools.Field(typeof(GravshipRenderer), "MatGravFieldExtenderGlow").GetValue(null) as Material;
        private static readonly Material matEngineGlow = AccessTools.Field(typeof(GravshipRenderer), "MatGravEngineGlow").GetValue(null) as Material;
        private static readonly int Color2 = Shader.PropertyToID("_Color2");
        private static readonly int GravshipHeight = Shader.PropertyToID("_GravshipHeight");
        private static readonly int IsTakeoff = Shader.PropertyToID("_IsTakeoff");
        private static readonly Material matDownwash = AccessTools.Field(typeof(GravshipRenderer), "MatGravshipDownwash").GetValue(null) as Material;
        private static readonly Material matDistortion = AccessTools.Field(typeof(GravshipRenderer), "MatGravshipDistortion").GetValue(null) as Material;
        private static readonly Material matLensFlare = AccessTools.Field(typeof(GravshipRenderer), "MatGravshipLensFlare").GetValue(null) as Material;
        private static readonly AccessTools.FieldRef<GravshipRenderer, MaterialPropertyBlock> rDistortionBlock = AccessTools.FieldRefAccess<GravshipRenderer, MaterialPropertyBlock>("distortionBlock");
        private static readonly AccessTools.FieldRef<GravshipRenderer, MaterialPropertyBlock> rFlareBlock = AccessTools.FieldRefAccess<GravshipRenderer, MaterialPropertyBlock>("flareBlock");
        private static readonly AccessTools.FieldRef<GravshipRenderer, Map> rMap = AccessTools.FieldRefAccess<GravshipRenderer, Map>("map");
        private static readonly AccessTools.FieldRef<GravshipRenderer, Rot4> rLandingRot = AccessTools.FieldRefAccess<GravshipRenderer, Rot4>("landingRotation");

        // How high over the deck the thruster nozzles and the engine's orb are, in cells (thruster models are about a wall
        // high, the nozzle in their middle; the orb floats over the engine).
        private const float NozzleShare = 0.5f;
        private const float OrbHeight = 1.2f;
        private const float ExtenderHeight = 0.6f;

        static ShipFlight()
        {
            if (!ModsConfig.OdysseyActive)
            {
                return;
            }
            try
            {
                Harmony h = new Harmony("isorim.gravship");
                Type ctl = typeof(WorldComponent_GravshipController);
                h.Patch(AccessTools.Method(ctl, nameof(WorldComponent_GravshipController.InitiateTakeoff)), postfix: new HarmonyMethod(typeof(ShipFlight), nameof(InitiateTakeoff_Postfix)));
                h.Patch(AccessTools.Method(ctl, nameof(WorldComponent_GravshipController.InitiateLanding)), postfix: new HarmonyMethod(typeof(ShipFlight), nameof(InitiateLanding_Postfix)));
                h.Patch(AccessTools.Method(typeof(GravshipUtility), nameof(GravshipUtility.GenerateGravship)), prefix: new HarmonyMethod(typeof(ShipFlight), nameof(GenerateGravship_Prefix)));
                h.Patch(AccessTools.Method(typeof(GravshipRenderer), nameof(GravshipRenderer.UpdateTakeoff)), prefix: new HarmonyMethod(typeof(ShipFlight), nameof(UpdateTakeoff_Prefix)));
                h.Patch(AccessTools.Method(ctl, "DrawTerrainCurtain"), prefix: new HarmonyMethod(typeof(ShipFlight), nameof(DrawTerrainCurtain_Prefix)));
                h.Patch(AccessTools.Method(ctl, "DrawGravship"), postfix: new HarmonyMethod(typeof(ShipFlight), nameof(DrawGravship_Postfix)));
                h.Patch(AccessTools.Method(ctl, "ResetCutscene"), postfix: new HarmonyMethod(typeof(ShipFlight), nameof(End)));
                h.Patch(AccessTools.Method(ctl, nameof(WorldComponent_GravshipController.FinalizeInit)), postfix: new HarmonyMethod(typeof(ShipFlight), nameof(End)));
                // Landing: the old ground frozen, the placed ship hidden and recorded, the snapshot flown in, the swap.
                h.Patch(AccessTools.Method(typeof(GravshipCapturer), nameof(GravshipCapturer.BeginTerrainRender)), prefix: new HarmonyMethod(typeof(ShipFlight), nameof(BeginTerrainRender_Prefix)));
                h.Patch(AccessTools.Method(ctl, "PlaceGravship"), postfix: new HarmonyMethod(typeof(ShipFlight), nameof(PlaceGravship_Postfix)));
                h.Patch(AccessTools.Method(typeof(GravshipRenderer), nameof(GravshipRenderer.BeginCutscene)), prefix: new HarmonyMethod(typeof(ShipFlight), nameof(BeginCutscene_Prefix)));
                h.Patch(AccessTools.Method(typeof(GravshipRenderer), nameof(GravshipRenderer.UpdateLanding)), prefix: new HarmonyMethod(typeof(ShipFlight), nameof(UpdateLanding_Prefix)));
                h.Patch(AccessTools.Method(typeof(MapDrawLayer), nameof(MapDrawLayer.DrawLayer)),
                    prefix: new HarmonyMethod(typeof(ShipFreeze), nameof(ShipFreeze.DrawLayer_Prefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(ShipFreeze), nameof(ShipFreeze.DrawLayer_Finalizer)));
                ModLog.Note("[IsoRim] Hooked the gravship cutscene (3D ship flight).");
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Could not hook the gravship cutscene: " + e.Message);
            }
        }

        // ---- start and end

        public static void InitiateTakeoff_Postfix(Building_GravEngine engine)
        {
            Begin(true);
            if (mode3D && engine != null && engine.Spawned)
            {
                // The ship's cells, and its moving parts recorded from now on (ShipCapture), while it still stands here.
                try
                {
                    cells = ShipCells.Of(engine);
                    if (cells.top > 0)
                    {
                        ShowAllFloors(engine.Map);   // before the recording: hidden floors draw nothing to record
                    }
                    ShipCapture.Start(cells, false);
                }
                catch (Exception e)
                {
                    cells = null;
                    Log.ErrorOnce("[IsoRim] Finding the gravship's cells failed: " + e, 93611504);
                }
            }
        }

        public static void InitiateLanding_Postfix(Gravship gravship, Map map)
        {
            Begin(false);
            if (!mode3D)
            {
                return;
            }
            try
            {
                landingTop = TopOf(gravship);
                if (landingTop > 0)
                {
                    ShowAllFloors(map);
                }
            }
            catch (Exception e)
            {
                landingTop = 0;
                Log.ErrorOnce("[IsoRim] Reading the landing gravship's floors failed: " + e, 93611509);
            }
        }

        // The highest floor of a flying ship: its cells carry their floor in y (the floor layout's ship encoding; 0 on
        // maps without floors).
        private static int TopOf(Gravship g)
        {
            int top = 0;
            if (g != null && Api.Floors != null)
            {
                foreach (IntVec3 c in g.Foundations.Keys)
                {
                    top = Mathf.Max(top, c.y);
                }
            }
            return top;
        }

        // ---- the floor view (decision: a ship with upper decks is seen whole): every floor is shown while the cutscene
        // runs (the map's top floor viewed, through the floor layout), the player's own view comes back after it.
        private static int savedView = -1;
        private static int landingTop;

        private static void ShowAllFloors(Map map)
        {
            IFloorLayout layout = Api.Floors;
            if (layout == null || map == null || Floors.GroundRowsOf(map) <= 0)
            {
                return;
            }
            int was = layout.ViewFloor(map, layout.FloorCount(map));
            if (was >= 0 && savedView < 0)
            {
                savedView = was;
            }
        }

        private static void RestoreView()
        {
            int k = savedView;
            savedView = -1;
            if (k >= 0 && Api.Floors != null)
            {
                try
                {
                    Api.Floors.ViewFloor(Find.CurrentMap, k);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Putting the floor view back after the gravship cutscene failed: " + e, 93611510);
                }
            }
        }

        private static void Begin(bool isTakeoff)
        {
            Clear();
            takeoff = isTakeoff;
            mode3D = Enabled && View3D.Ready;
            if (!mode3D)
            {
                return;
            }
            if (FirstPerson.On)
            {
                FirstPerson.Leave(null);
            }
            CameraDriver cd = Find.CameraDriver;
            if (cd != null)
            {
                savedPos = camRootPos(cd);
                savedSize = cd.RootSize;
                camSaved = true;
            }
        }

        // The cutscene ended (or a game was loaded): after a takeoff the map camera goes back to the player's view (the
        // game shows the world now; the landing hands the camera back where the ship landed); everything is dropped.
        public static void End()
        {
            if (mode3D && camSaved && Find.CameraDriver != null)
            {
                if (takeoff)
                {
                    Find.CameraDriver.SetRootPosAndSize(savedPos, savedSize);
                }
                else
                {
                    Find.CameraDriver.SetRootSize(savedSize);
                }
            }
            Clear();
            mode3D = false;
        }

        private static void Clear()
        {
            if (snap != null)
            {
                snap.Dispose();
                snap = null;
            }
            ShipCapture.Stop();
            ShipFreeze.Unfreeze();
            cells = null;
            landed = false;
            lightThawed = false;
            cutOut = false;
            posed = false;
            camSaved = false;
            camFollowing = false;
            landingTop = 0;
            RestoreView();
        }

        // ---- the snapshot: the ship is still on the map here, the game takes it off right after

        public static void GenerateGravship_Prefix(Building_GravEngine engine)
        {
            if (!mode3D || !takeoff || !WorldComponent_GravshipController.CutsceneInProgress || engine == null || !engine.Spawned)
            {
                return;
            }
            try
            {
                if (snap != null)
                {
                    snap.Dispose();
                }
                List<ShipCapture.Rec> recorded = ShipCapture.Take();
                ShipCapture.Stop();
                snap = ShipSnapshot.Build(engine, cells ?? ShipCells.Of(engine), recorded);
            }
            catch (Exception e)
            {
                ShipCapture.Stop();
                snap = null;
                Log.ErrorOnce("[IsoRim] Making the 3D snapshot of the gravship failed (the game's flat cutscene is shown): " + e, 93611501);
            }
        }

        // ---- drawing

        public static bool UpdateTakeoff_Prefix(GravshipRenderer __instance, float progress)
        {
            if (!mode3D || snap == null)
            {
                return true;
            }
            try
            {
                Takeoff(__instance, progress);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Flying the 3D gravship failed (the game's flat cutscene is shown): " + e, 93611502);
                snap.Dispose();
                snap = null;
                return true;
            }
        }

        // ---- landing (vanilla's order: WorldComponent_GravshipController.InitiateLanding)

        // The curtain picture is about to be taken: the sections over the landing area are frozen instead (ShipFreeze).
        public static void BeginTerrainRender_Prefix(CellRect landingBounds)
        {
            if (!mode3D || takeoff || Find.CurrentMap == null)
            {
                return;
            }
            try
            {
                ShipFreeze.Freeze(Find.CurrentMap, landingBounds, landingTop);
            }
            catch (Exception e)
            {
                ShipFreeze.Unfreeze();
                Log.ErrorOnce("[IsoRim] Freezing the ground for the gravship landing failed: " + e, 93611505);
            }
        }

        // The ship stands on the map now, under the frozen ground: its cells, and its moving parts hidden and recorded.
        public static void PlaceGravship_Postfix(Building_GravEngine __result)
        {
            if (!mode3D || takeoff || __result == null || !__result.Spawned)
            {
                return;
            }
            try
            {
                cells = ShipCells.Of(__result);
                ShipCapture.Start(cells, true);
            }
            catch (Exception e)
            {
                cells = null;
                Log.ErrorOnce("[IsoRim] Finding the landed gravship's cells failed: " + e, 93611506);
            }
        }

        // The landing cutscene starts: the snapshot is made from the placed ship (its sections and the frame recorded).
        public static void BeginCutscene_Prefix(Gravship gravship)
        {
            if (!mode3D || takeoff || cells == null || gravship == null || gravship.Engine == null || !gravship.Engine.Spawned)
            {
                return;
            }
            try
            {
                if (snap != null)
                {
                    snap.Dispose();
                }
                snap = ShipSnapshot.Build(gravship.Engine, cells, ShipCapture.Take());
                ShipCapture.Hold();   // still hidden until touchdown
            }
            catch (Exception e)
            {
                snap = null;
                Touchdown();
                Log.ErrorOnce("[IsoRim] Making the 3D snapshot of the landing gravship failed: " + e, 93611507);
            }
        }

        public static bool UpdateLanding_Prefix(GravshipRenderer __instance, float progress)
        {
            if (!mode3D)
            {
                return true;
            }
            if (landed || snap == null)
            {
                return landed ? false : !ShipFreeze.Active;   // after the swap nothing more is flown
            }
            try
            {
                Landing(__instance, progress);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Flying the 3D gravship in failed: " + e, 93611508);
                Touchdown();
                return false;
            }
        }

        private static bool landed;
        private static bool lightThawed;
        private static bool cutOut;
        private const float CutOutBelow = 5f;
        private const float LightFrom = 0.88f;

        private static void Landing(GravshipRenderer r, float progress)
        {
            Gravship g = rGravship(r);
            if (progress >= SwapAt)
            {
                // The snapshot's pose is the real one now: the real ship shows again, the old ground goes, in one frame.
                Touchdown();
                return;
            }
            if (progress >= LightFrom && !lightThawed)
            {
                // The light overlay goes live while the ship is a few cells over its place and covers it: the landed
                // ship's indoor dark is in it, which the snapshot (drawn over the same cells) then takes as the real ship
                // would. Earlier it would print the ship's rooms as dark patches on the old ground.
                lightThawed = true;
                ShipFreeze.Thaw(l => l.GetType().Name.Contains("LightingOverlay"));
            }
            // ---- vanilla 1.7 GravshipRenderer.UpdateLanding (the curves; only the slide up the screen becomes a height)
            float p = progress.RemapClamped(0f, 0.95f, 0f, 1f);
            float num = Mathf.Pow(1f - p, 5f);
            Rot4 rot = rLandingRot(r);
            float slide;
            Vector3 approach;
            if (rot == Rot4.North || rot == Rot4.South)
            {
                slide = 100f * num;
                approach = rot.AsQuat * -g.launchDirection.ToVector3().normalized * 200f * num;
            }
            else
            {
                slide = 200f * num;
                approach = rot.AsQuat * -g.launchDirection.ToVector3().normalized * 100f * Mathf.Pow(1f - p, 9f);
            }
            // ---- end vanilla
            // Flown to the snapshot's own centre (the placed ship's), which vanilla's landing centre should equal.
            ground = cells.pivot + approach;
            height = HeightFor(slide);
            turn = Quaternion.identity;
            posed = true;
            if (!cutOut && height < CutOutBelow)
            {
                cutOut = true;
                ShipFreeze.CutOut(cells);   // what stood on the landing place leaves the frozen ground as the ship comes down
            }
            DrawFlight(r, g, rEnginePos(r) + approach, p, num, false);
        }

        // Raw cutscene progress of the swap: vanilla's ship is at rest from 0.95 (UpdateLanding remaps 0-0.95).
        private const float SwapAt = 0.95f;

        private static void Touchdown()
        {
            landed = true;
            if (snap != null)
            {
                snap.Dispose();
                snap = null;
            }
            ShipCapture.Stop();
            ShipFreeze.Unfreeze();
            posed = false;
        }

        public static bool DrawTerrainCurtain_Prefix()
        {
            // The curtain is a flat picture of the landing area taken with IsoRim's camera: laid flat it is wrong in 3D.
            return !mode3D;
        }

        private static void Takeoff(GravshipRenderer r, float progress)
        {
            Gravship g = rGravship(r);
            // ---- vanilla 1.7 GravshipRenderer.UpdateTakeoff (the curves; only the slide up the screen becomes a height)
            float num = Mathf.Pow(progress, 3.5f);
            float slide = Find.Camera.orthographicSize * 2.5f * num;
            Vector3 normalized = g.launchDirection.ToVector3().normalized;
            float num2 = (Mathf.Approximately(normalized.x, 1f) || Mathf.Approximately(normalized.z, -1f)) ? (-1f) : 1f;
            float y = Mathf.SmoothStep(0f, num2 * 20f, Mathf.InverseLerp(0.2f, 1f, progress + 0.02f * Mathf.Sin(23.561945f * progress)));
            Quaternion quaternion = Quaternion.Euler(0f, y, 0f);
            rOffset(r) += quaternion * normalized * (25f * Time.deltaTime * Mathf.InverseLerp(0.2f, 1f, progress));
            // ---- end vanilla
            ground = rCenter(r) + rOffset(r);
            height = HeightFor(slide);
            turn = quaternion;
            posed = true;
            DrawFlight(r, g, rEnginePos(r) + rOffset(r), progress, num, true);
        }

        // The ship and everything round it this frame. All of it is worked out in drawn space (upper floors already moved
        // over the ground and raised): the floors' mapping of single draws is off meanwhile, so a ship flown over the
        // north edge of the ground is not read as standing on a floor there. The recorder is paused meanwhile: the flying
        // copy's own deck edge (drawn lit, through IsoGlow.DrawLit) passes over the landing ship's cells near touchdown and
        // was taken for one of its parts to hide.
        private static void DrawFlight(GravshipRenderer r, Gravship g, Vector3 enginePos, float progress, float heightShare, bool isTakeoff)
        {
            bool was = Floors.InDrawnSpace;
            bool recording = ShipCapture.Recording;
            Floors.InDrawnSpace = true;
            ShipCapture.Recording = false;
            try
            {
                snap.Draw(ground, height, turn);
                Effects(r, g, enginePos, progress, heightShare, isTakeoff);
            }
            finally
            {
                Floors.InDrawnSpace = was;
                ShipCapture.Recording = recording;
            }
        }

        // Vanilla's fake height is a slide up the screen by that many cells; a point raised h cells moves h cos(pitch)
        // up the screen, so h = slide / cos(pitch). Kept well below the eye (and the near plane).
        private static float HeightFor(float slide)
        {
            float c = Mathf.Max(0.05f, Mathf.Cos(View3D.Pitch * Mathf.Deg2Rad));
            float cap = Mathf.Max(2f, 0.4f * View3D.EyePosition.y);
            return Mathf.Min(slide / c, cap);
        }

        // Everything vanilla draws round the flying ship (GravshipRenderer.DrawGravship / DrawGravshipGroundEffects,
        // vanilla 1.7), in 3D: the shadow on the ground (ShipSnapshot.DrawShadow), the thruster flames stood up round
        // their thrust (axial cards: long along the thrust, turned about it to face the camera) at the nozzles, the engine
        // and field extender glows as cards facing the camera, the exhaust, and the screen effects (downwash, distortion,
        // lens flares: the game's screen-space shaders on a card filling the screen, _DrawPos from the 3D points).
        // progress: the takeoff's, or the landing's remapped one (0-1 over raw 0-0.95), as vanilla passes them on.
        private static void Effects(GravshipRenderer r, Gravship g, Vector3 enginePos, float progress, float heightShare, bool isTakeoff)
        {
            Map map = rMap(r);
            snap.DrawShadow(ground, height, turn, isTakeoff ? progress : 0f, map);

            // Downwash: round the ground under the ship; the card lies at that depth, so the ship in front covers it.
            if (progress > 0f && map != null && !map.Biome.inVacuum && matDownwash != null)
            {
                Vector3 under = new Vector3(ground.x, 0f, ground.z);
                matDownwash.SetFloat(ShaderPropertyIDs.Progress, progress);
                matDownwash.SetFloat(GravshipHeight, heightShare);
                matDownwash.SetVector(ShaderPropertyIDs.DrawPos, View3D.WorldToViewport(under));
                matDownwash.SetFloat(IsTakeoff, isTakeoff ? 1f : 0f);
                Graphics.DrawMesh(MeshPool.plane10, View3D.ScreenCard(View3D.DepthOf(under) - 0.5f), matDownwash, 0);
            }

            float nozzle = View3D.WallHeight * NozzleShare;
            Vector3 engineAt = Turned(enginePos + g.Engine.def.Size.ToVector3() * 0.25f);
            Vector3 orb = new Vector3(engineAt.x, height + snap.FloorHeight(snap.EngineFloor) + OrbHeight, engineAt.z);
            MaterialPropertyBlock distortion = rDistortionBlock(r);
            if (matDistortion != null && distortion != null)
            {
                distortion.SetFloat(ShaderPropertyIDs.Progress, progress);
                distortion.SetFloat(GravshipHeight, heightShare);
                distortion.SetVector(ShaderPropertyIDs.DrawPos, View3D.WorldToViewport(orb));
                distortion.SetFloat(IsTakeoff, isTakeoff ? 1f : 0f);
                Graphics.DrawMesh(MeshPool.plane10, View3D.ScreenCard(View3D.NearCard), matDistortion, 0, null, 0, distortion);
            }
            if (isTakeoff ? progress <= 0f : progress >= 1f)
            {
                return;
            }
            Color value = new Color(1f, 1f, 1f, 1f);
            value *= Mathf.Lerp(0.75f, 1f, Mathf.PerlinNoise1D(progress * 100f));
            value.a = Mathf.InverseLerp(0f, 0.1f, isTakeoff ? progress : 1f - progress);
            if (matLensFlare != null)
            {
                matLensFlare.SetColor(Color2, value);
            }
            MaterialPropertyBlock block = rFlameBlock(r);
            MaterialPropertyBlock flare = rFlareBlock(r);
            Dictionary<Thing, EventQueue> timers = rExhaustTimers(r);
            Vector3 toCamera = -View3D.Forward;
            float s = View3D.Squash;
            foreach (KeyValuePair<Thing, PositionData.Data> kv in g.ThrusterPlacements)
            {
                Thing thing = kv.Key;
                PositionData.Data data = kv.Value;
                if (data.rotation.AsIntVec3 == -g.launchDirection)
                {
                    continue;
                }
                CompProperties_GravshipThruster props = thing.TryGetComp<CompGravshipThruster>().Props;
                float size = thing.def.size.x * props.flameSize;
                Vector3 offset = thing.Rotation.AsQuat * props.flameOffsetsPerDirection[thing.Rotation.AsInt];
                Vector3 local = GenThing.TrueCenter(data.local, thing.Rotation, thing.def.size, 0f) - thing.Rotation.AsIntVec3.ToVector3() * (thing.def.size.z * 0.5f + size * 0.5f) + offset;
                Vector3 pos = Turned(enginePos + local).SetToAltitude(AltitudeLayer.Skyfaller).WithYOffset(0.07317074f);
                float up = snap.FloorHeight(data.local.y);   // an upper deck's thruster: its floor (ship cells carry it in y)
                Vector3 at = new Vector3(pos.x, height + up + nozzle, pos.z);
                MaterialRequest req = new MaterialRequest(props.FlameShaderType.Shader);
                req.renderQueue = 3201;
                Material mat = MaterialPool.MatFrom(req);
                block.Clear();
                block.SetColor(Color2, value);
                foreach (ShaderParameter p in props.flameShaderParameters)
                {
                    p.Apply(block);
                }
                // The quad's texture up stays along the thruster's facing as in vanilla (local z); its face turns about
                // that axis toward the camera.
                Vector3 along = turn * data.rotation.AsQuat * Vector3.forward;
                Vector3 face = toCamera - along * Vector3.Dot(along, toCamera);
                if (face.sqrMagnitude < 1e-4f)
                {
                    face = Vector3.up;
                }
                Matrix4x4 m = Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f)) * Matrix4x4.TRS(at, Quaternion.LookRotation(along, face.normalized), new Vector3(size, 1f, size));
                Graphics.DrawMesh(MeshPool.plane10, m, mat, 0, null, 0, block);
                if (matLensFlare != null && flare != null)
                {
                    flare.SetVector(ShaderPropertyIDs.DrawPos, View3D.WorldToViewport(at));
                    Graphics.DrawMesh(MeshPool.plane10, View3D.ScreenCard(View3D.NearCard), matLensFlare, 0, null, 0, flare);
                }
                EventQueue q;
                if (props.exhaustSettings != null && props.exhaustSettings.enabled && timers != null && timers.TryGetValue(thing, out q))
                {
                    q.Push(Time.deltaTime);
                    while (q.Pop())
                    {
                        Exhaust(r, props.exhaustSettings, pos, data.rotation.AsQuat, height + up + nozzle);
                    }
                }
            }
            if (matExtenderGlow != null)
            {
                matExtenderGlow.SetColor(Color2, value);
                foreach (IntVec3 c in g.GravFieldExtenderPositions)
                {
                    Vector3 p = Turned(enginePos + c.ToVector3() + ThingDefOf.GravFieldExtender.graphicData.drawSize.ToVector3() * 0.5f);
                    Glow(new Vector3(p.x, height + snap.FloorHeight(c.y) + ExtenderHeight, p.z), 8f, matExtenderGlow, snap.GlowPull);
                }
            }
            if (matEngineGlow != null)
            {
                matEngineGlow.SetColor(Color2, value);
                Glow(orb, 12.5f, matEngineGlow, snap.GlowPull);
            }
        }

        // Vanilla 1.7 GravshipRenderer.EmitSmoke, the puff then stood up at the nozzle's height as IsoRim's own smoke
        // (Emitters: drawn upright facing the camera, at that height; it stays there as the ship moves on).
        private static void Exhaust(GravshipRenderer r, CompProperties_GravshipThruster.ExhaustSettings settings, Vector3 position, Quaternion thrusterRotation, float at)
        {
            FleckSystem system = rExhaust(r);
            if (system == null)
            {
                return;
            }
            Quaternion quaternion = Quaternion.identity;
            if (settings.inheritThrusterRotation)
            {
                quaternion = thrusterRotation * quaternion;
            }
            if (settings.inheritGravshipRotation)
            {
                quaternion = turn * quaternion;
            }
            Vector3 spawn = position + quaternion * settings.spawnOffset + Rand.InsideUnitSphere.WithY(0f).normalized * settings.spawnRadiusRange.RandomInRange;
            Emitters.Raise(spawn, at, 0f, settings.ExhaustFleckDef);
            system.CreateFleck(new FleckCreationData
            {
                def = settings.ExhaustFleckDef,
                spawnPosition = spawn,
                scale = settings.scaleRange.RandomInRange,
                velocity = quaternion * Quaternion.Euler(0f, settings.velocityRotationRange.RandomInRange, 0f) * (settings.velocity * settings.velocityMultiplierRange.RandomInRange),
                rotationRate = settings.rotationOverTimeRange.RandomInRange,
                ageTicksOverride = -1
            });
        }

        // A glow sprite stood up facing the camera, centred on a point (true height in y). It is moved toward the camera
        // by more than half its size (no change on screen with the orthographic camera), so the deck it hangs over does not
        // cut it off with a hard edge; by pull more for a ship with upper decks, whose walls would otherwise stand through
        // the card and show where they cross it (a V at the corner toward the camera).
        private static void Glow(Vector3 at, float size, Material mat, float pull = 0f)
        {
            at -= View3D.Forward * (size * 0.6f + pull);
            Vector3 pivot = new Vector3(at.x, at.y / View3D.Squash, at.z);
            Graphics.DrawMesh(MeshPool.plane10, View3D.UprightMatrix(pivot) * Matrix4x4.TRS(pivot, Quaternion.identity, new Vector3(size, 1f, size)), mat, 0);
        }

        private static Vector3 Turned(Vector3 p)
        {
            return turn * (p - ground) + ground;
        }

        // ---- the camera: follows the ship (eased), keeps the player's turn and tilt, and zooms out so the whole ship
        // fits, at least as far as vanilla zooms (DrawGravship sets its size first; this runs after it and wins)

        public static void DrawGravship_Postfix(WorldComponent_GravshipController __instance, float progress)
        {
            if (!mode3D || !posed || snap == null)
            {
                return;
            }
            CameraDriver cd = Find.CameraDriver;
            Map map = Find.CurrentMap;
            if (cd == null || map == null || cd.config.gravshipFreeCam)
            {
                return;
            }
            try
            {
                Follow(__instance, cd, map, progress);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Following the gravship with the camera failed: " + e, 93611503);
            }
        }

        private static void Follow(WorldComponent_GravshipController ctl, CameraDriver cd, Map map, float progress)
        {
            if (!camFollowing)
            {
                camFollowing = true;
                camFocus = camRootPos(cd);
                camFocus.y = 0f;
                camSize = cd.RootSize;
            }
            float dt = Time.deltaTime;
            Vector3 fwd = View3D.Forward;
            // The ground point seen behind the ship's middle (along the line of sight), kept on the map.
            Vector3 mid = snap.Middle(ground, height, turn);
            Vector3 target = fwd.y < -0.01f ? mid + fwd * (mid.y / -fwd.y) : mid;
            if (!takeoff)
            {
                // Landing: the camera waits over the landing site while the ship is far out, picks it up as it comes in
                // and rides down with it to touchdown (where the two meet).
                Vector3 site = cells.pivot;
                float far = Vector3.Distance(new Vector3(target.x, 0f, target.z), site);
                target = Vector3.Lerp(site, target, Mathf.InverseLerp(PickUpFar, PickUpNear, far));
            }
            target.x = Mathf.Clamp(target.x, 2f, map.Size.x - 2f);
            target.z = Mathf.Clamp(target.z, 2f, map.Size.z - 2f);
            target.y = 0f;
            camFocus = Vector3.Lerp(camFocus, target, 1f - Mathf.Exp(-4f * dt));

            // Vanilla's zoom for this progress (WorldComponent_GravshipController.DrawGravship / GetCutsceneZoomRange, 1.7).
            CameraMapConfig config = cd.config;
            Gravship g = cGravship(ctl);
            FloatRange range = config.gravshipEnableOverrideSizeRange || g == null || g.capture == null
                ? config.gravshipOverrideSizeRange : new FloatRange(g.capture.minCameraSize, g.capture.maxCameraSize);
            float x = progress.RemapClamped(0f, takeoff ? 0.7f : 0.8f, 0f, 1f);
            float eased = x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) * 0.5f;
            float vanillaSize = takeoff ? Mathf.Lerp(range.min, range.max, eased) : Mathf.Lerp(range.max, range.min, eased);
            float fit = snap.FitSize(ground, height, turn, camFocus, Find.Camera.aspect);
            float want = Mathf.Min(Mathf.Max(vanillaSize, fit), config.sizeRange.max * 1.5f);
            camSize = Mathf.Lerp(camSize, want, Mathf.Clamp01(5f * dt));

            Vector3 root = camRootPos(cd);
            cd.SetRootPosAndSize(new Vector3(camFocus.x, root.y, camFocus.z), camSize);
        }

        // A landing ship is picked up by the camera between these distances from the landing site (cells).
        private const float PickUpFar = 90f;
        private const float PickUpNear = 40f;

        // For the harness log.
        public static string Describe()
        {
            if (!mode3D)
            {
                return "flight: vanilla";
            }
            if (snap == null)
            {
                return "flight: 3D, no snapshot";
            }
            return "flight: 3D, " + snap.Items + " moving parts, ground " + ground.ToString("F1") + " height " + height.ToString("0.0") + " turn " + turn.eulerAngles.y.ToString("0.0")
                + ", camera focus " + camFocus.ToString("F1") + " size " + camSize.ToString("0.0");
        }
    }

    // The ship's 3D geometry at takeoff, combined per material, in drawn space relative to the cutscene's centre (the
    // centre of the substructure's bounds widened by one cell, as vanilla's capture).
    //
    // The ship's cells S: the valid substructure, the hull corner pieces standing beside it (HullCorners) and the cells
    // of buildings on it. From the section meshes over S (an allow-list of layers, regenerated first so they are
    // current), a triangle belongs to the ship when:
    //   - it carries a cut-away / roof-clip tag (texture coordinate 1): its owner cell is in S. Of the cut-away flags,
    //     a twig (shown only for a thinned tree) is dropped, and a side shown only while a neighbour is lower (Cond) is
    //     dropped when that neighbour is in S too (an inner join) and kept when it is outside (exposed in flight);
    //   - otherwise, the cell under its middle is in S;
    //   - and a flat triangle (all of it on the ground) only when the cell under its middle is substructure (so the
    //     ground bands and lips a hull cell prints around itself stay on the ground). The game's substructure props and
    //     hull layers draw their lips and corner ground pieces just outside the deck: for those, one ring more.
    // Tag data is dropped (texture coordinate 1 = 0): the copy is never cut away or roof-clipped, and nothing in it
    // reads the map's cell textures at the old cells.
    public sealed class ShipSnapshot
    {
        private const int MaxVerts = 65000;   // 16-bit indices (UInt32 index support in Waylith unknown)

        private readonly List<Mesh> meshes = new List<Mesh>();
        private readonly List<Material> mats = new List<Material>();      // drawn with
        private readonly List<Material> sources = new List<Material>();   // the section's own (for the light-pass mark)
        private readonly List<int> layers = new List<int>();
        private readonly List<bool> depths = new List<bool>();
        private readonly List<ShipCapture.Rec> items = new List<ShipCapture.Rec>();   // moving parts (matrix relative to the pivot)
        private Vector3 pivot;
        private float[] heights;    // the ship's floors' heights (true cells), [0] the ground
        private int upperSlab;      // vertices of the upper decks' slab (log)

        // The floor the engine stands on, and floor k's height over the ground (true cells; 0 the ground and without floors).
        public int EngineFloor { get; private set; }

        // How much further a glow card is pulled toward the camera so the whole ship stands behind it: none for a one-storey
        // ship (as before), else the box's reach round its middle (cells).
        public float GlowPull
        {
            get
            {
                if (heights == null || heights.Length < 2)
                {
                    return 0f;
                }
                return new Vector3(max.x - min.x, max.y - min.y, max.z - min.z).magnitude * 0.5f + max.y;
            }
        }

        public float FloorHeight(int k)
        {
            return heights != null && k > 0 && k < heights.Length ? heights[k] : 0f;
        }
        private Vector3 min, max;   // drawn x, z relative to the pivot; y in true cells
        private Texture2D shadowTex;
        private Mesh slab;
        private Material shadowMat;
        private Vector2 shadowSize;

        public int Items
        {
            get { return items.Count; }
        }

        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> sectionLayers = AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");
        private static readonly int[] DX = { 0, 0, 0, -1, 1, -1, 1, -1, 1 };   // ShaderCut direction codes 1-8
        private static readonly int[] DZ = { 0, -1, 1, 0, 0, -1, -1, 1, 1 };
        private static readonly HashSet<string> skippedLogged = new HashSet<string>();

        // Cell classes.
        private const byte Ring = 1, Ship = 2, Deck = 3;

        private sealed class Chunk
        {
            public readonly List<Vector3> verts = new List<Vector3>();
            public readonly List<int> tris = new List<int>();
            public readonly List<Color32> colors = new List<Color32>();
            public readonly List<Vector3> uvs = new List<Vector3>();
            public readonly List<Vector3> normals = new List<Vector3>();
            public bool hasNormals;
        }

        private sealed class Group
        {
            public Material mat;
            public int layer;
            public bool depth;   // the deck's terrain: its depth is written first (DepthMat)
            public bool upper;   // an upper deck's terrain: kept apart, no depth pass (its slab under it writes the depth, as on the map)
            public readonly List<Chunk> chunks = new List<Chunk>();
        }

        public int Vertices { get; private set; }
        public int Triangles { get; private set; }

        public static ShipSnapshot Build(Building_GravEngine engine, ShipCells cells, List<ShipCapture.Rec> recorded)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Map map = engine.Map;
            HashSet<IntVec3> deck = engine.ValidSubstructure;
            CellRect bounds = cells.bounds;
            ShipSnapshot s = new ShipSnapshot { pivot = cells.pivot, EngineFloor = cells.engineFloor };
            s.heights = new float[cells.top + 1];
            for (int k = 1; k <= cells.top; k++)
            {
                s.heights[k] = cells.Height(k);
            }
            Func<float, float, byte> cellAt = cells.At;
            s.MakeShadow(cells);
            s.MakeSlab(cells);

            // ---- the moving parts, as drawn in the frame before (ShipCapture), moved to the pivot
            if (recorded != null)
            {
                Matrix4x4 back = Matrix4x4.Translate(-s.pivot);
                foreach (ShipCapture.Rec r in recorded)
                {
                    s.items.Add(new ShipCapture.Rec { mesh = r.mesh, matrix = back * r.matrix, mat = r.mat, props = r.props });
                }
            }

            // ---- the sections over them, current
            Array sections = MapSections.SectionsOf(map);
            if (sections == null)
            {
                return s;
            }
            // On each of the ship's floors: the same ground area in that floor's rows (its blocks line up with the ground's).
            List<Section> list = new List<Section>();
            List<int> floorOf = new List<int>();
            for (int k = 0; k <= cells.top; k++)
            {
                CellRect over = bounds.ExpandedBy(1).ClipInsideMap(map);
                if (k > 0)
                {
                    over = bounds.ExpandedBy(1);
                    over.minZ = Mathf.Max(over.minZ, 0) + cells.shifts[k];
                    over.maxZ = Mathf.Min(over.maxZ, cells.groundRows - 1) + cells.shifts[k];
                    over = over.ClipInsideMap(map);
                    if (over.maxZ < over.minZ)
                    {
                        continue;
                    }
                }
                for (int sx = over.minX / Section.Size; sx <= over.maxX / Section.Size && sx < sections.GetLength(0); sx++)
                {
                    for (int sz = over.minZ / Section.Size; sz <= over.maxZ / Section.Size && sz < sections.GetLength(1); sz++)
                    {
                        if (sections.GetValue(sx, sz) is Section sec && !list.Contains(sec))
                        {
                            sec.RegenerateAllLayers();
                            list.Add(sec);
                            floorOf.Add(k);
                        }
                    }
                }
            }
            long regenMs = watch.ElapsedMilliseconds;

            // ---- gathering
            float squash = View3D.Squash;
            Dictionary<string, int[]> perLayer = new Dictionary<string, int[]>();
            List<Group> groups = new List<Group>();
            List<string> skipped = new List<string>();
            int[] remap = new int[0];
            for (int si = 0; si < list.Count; si++)
            {
                Section sec = list[si];
                // An upper floor's block is moved over the ground and raised by the floor's height (drawn space, as
                // Floors.RowMatrix draws it); its triangles are told apart by that floor's classes (cells.At by row).
                Vector3 from = floorOf[si] > 0 ? s.pivot - cells.Lift(floorOf[si]).MultiplyPoint3x4(Vector3.zero) : s.pivot;
                foreach (SectionLayer layer in sectionLayers(sec))
                {
                    int kind = Kind(layer);
                    if (kind == 0)
                    {
                        if (Prefs.DevMode && layer.Visible && Touches(layer, cellAt))
                        {
                            skipped.Add(layer.GetType().Name);
                        }
                        continue;
                    }
                    if (!layer.Visible)
                    {
                        continue;
                    }
                    string lname = layer.GetType().Name;
                    int[] stat;
                    if (!perLayer.TryGetValue(lname, out stat))
                    {
                        perLayer[lname] = stat = new int[2];
                    }
                    byte flatNeed = kind == 2 ? Ring : Deck;
                    foreach (LayerSubMesh sm in layer.subMeshes)
                    {
                        if (!sm.finalized || sm.disabled || sm.verts.Count == 0 || sm.material == null)
                        {
                            continue;
                        }
                        List<Vector3> v = sm.verts;
                        List<int> t = sm.tris;
                        List<Vector3> tag = sm.uvsChannelTwo.Count == v.Count ? sm.uvsChannelTwo : null;
                        bool upperTerrain = floorOf[si] > 0 && lname == "SectionLayer_Terrain";
                        Group grp = GroupFor(groups, sm.material, sm.renderLayer, upperTerrain);
                        grp.depth |= lname == "SectionLayer_Terrain" && !upperTerrain;
                        Chunk ch = grp.chunks[grp.chunks.Count - 1];
                        if (remap.Length < v.Count)
                        {
                            remap = new int[Math.Max(v.Count, remap.Length * 2)];
                        }
                        for (int i = 0; i < v.Count; i++)
                        {
                            remap[i] = -1;
                        }
                        for (int k = 0; k + 2 < t.Count; k += 3)
                        {
                            int a = t[k], b = t[k + 1], c = t[k + 2];
                            stat[0]++;
                            Vector3 va = v[a], vb = v[b], vc = v[c];
                            float mx = (va.x + vb.x + vc.x) / 3f, mz = (va.z + vb.z + vc.z) / 3f;
                            byte under = cellAt(mx, mz);
                            bool flat = va.y * squash < 0.02f && vb.y * squash < 0.02f && vc.y * squash < 0.02f;
                            int flags = tag != null ? (int)(tag[a].z + 0.5f) : 0;
                            bool keep;
                            if (flags != 0)
                            {
                                int ox = Mathf.FloorToInt(tag[a].x), oz = Mathf.FloorToInt(tag[a].y);
                                keep = cellAt(ox + 0.5f, oz + 0.5f) >= Ship && (flags & ShaderCut.Twig) == 0;
                                int cond = (flags >> ShaderCut.CondShift) & 15;
                                if (keep && cond > 0 && cond < 9 && cellAt(ox + DX[cond] + 0.5f, oz + DZ[cond] + 0.5f) >= Ship)
                                {
                                    keep = false;   // an inner join: hidden on the map, hidden in flight
                                }
                            }
                            else
                            {
                                keep = under >= Ship || (flat && under >= flatNeed);
                            }
                            if (!keep || (flat && under < flatNeed))
                            {
                                continue;
                            }
                            if (ch.verts.Count + 3 > MaxVerts)
                            {
                                ch = new Chunk();
                                grp.chunks.Add(ch);
                                for (int i = 0; i < v.Count; i++)
                                {
                                    remap[i] = -1;
                                }
                            }
                            ch.tris.Add(Vertex(sm, ch, a, remap, from));
                            ch.tris.Add(Vertex(sm, ch, b, remap, from));
                            ch.tris.Add(Vertex(sm, ch, c, remap, from));
                            stat[1]++;
                        }
                    }
                }
            }

            // ---- the slab under each upper deck (Floors draws one under every upper floor; the ship's part of it)
            for (int k = 1; k <= cells.top; k++)
            {
                s.AddUpperSlab(groups, map, cells, k);
            }

            // ---- meshes
            int verts = 0, tris = 0;
            s.min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            s.max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (Group grp in groups)
            {
                foreach (Chunk ch in grp.chunks)
                {
                    if (ch.tris.Count == 0)
                    {
                        continue;
                    }
                    Mesh m = new Mesh();
                    m.name = "IsoRimShipSnapshot";
                    m.SetVertices(ch.verts);
                    m.SetTriangles(ch.tris, 0);
                    m.SetColors(ch.colors);
                    m.SetUVs(0, ch.uvs);
                    if (ch.hasNormals)
                    {
                        m.SetNormals(ch.normals);
                    }
                    m.RecalculateBounds();
                    s.meshes.Add(m);
                    s.mats.Add(Flying(grp.mat));
                    s.sources.Add(grp.mat);
                    s.depths.Add(grp.depth);
                    s.layers.Add(grp.layer);
                    verts += ch.verts.Count;
                    tris += ch.tris.Count / 3;
                    foreach (Vector3 p in ch.verts)
                    {
                        Vector3 q = new Vector3(p.x, p.y * squash, p.z);
                        s.min = Vector3.Min(s.min, q);
                        s.max = Vector3.Max(s.max, q);
                    }
                }
            }
            if (s.meshes.Count == 0)
            {
                s.min = s.max = Vector3.zero;
            }
            s.Vertices = verts;
            s.Triangles = tris;

            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int[]> kv in perLayer)
            {
                parts.Add(kv.Key.Replace("SectionLayer_", "") + " " + kv.Value[1] + "/" + kv.Value[0]);
            }
            ModLog.Note("[IsoRim] Gravship snapshot: " + deck.Count + " deck cells" + (cells.top > 0 ? " on floors 0-" + cells.top + " (upper deck slab " + s.upperSlab + " vertices)" : "") + ", " + list.Count + " sections (regenerated in " + regenMs + " ms), "
                + s.meshes.Count + " meshes + " + s.items.Count + " moving parts, " + verts + " vertices, " + tris + " triangles, about " + ((verts * 40L + tris * 12L) / 1024) + " KB, built in "
                + watch.ElapsedMilliseconds + " ms; triangles kept per layer: " + string.Join(", ", parts)
                + "; box " + s.min.ToString("F1") + " to " + s.max.ToString("F1"));
            if (skipped.Count > 0)
            {
                skipped.RemoveAll(n => !skippedLogged.Add(n));
                if (skipped.Count > 0)
                {
                    ModLog.Warn("[IsoRim] Gravship snapshot: layers with geometry on the ship that are not flown: " + string.Join(", ", skipped));
                }
            }
            return s;
        }

        // 0 = not part of the snapshot, 1 = gathered, 2 = gathered with one ring more for flat pieces (lips).
        private static int Kind(SectionLayer layer)
        {
            switch (layer.GetType().Name)
            {
                case "SectionLayer_ThingsGeneral":
                case "SectionLayer_Terrain":
                case "SectionLayer_TerrainScatter":  // 1.7 ground scatter (on the deck too)
                case "SectionLayer_IsoRimWalls":
                case "SectionLayer_IsoRimBodies":
                case "SectionLayer_IsoRimRoofs":     // only while roofs are shown (its Visible)
                case "SectionLayer_IsoRimDamage":
                    return 1;
                case "SectionLayer_BuildingsDamage":
                    return IsoRimMod.Settings.damage3D ? 0 : 1;   // drawn only without the 3D marks (Patches.DrawLayer_Section)
                case "SectionLayer_SubstructureProps":
                case "SectionLayer_GravshipHull":
                    return 2;
            }
            int extra;
            return WallPieces.flown.TryGetValue(layer.GetType().Name, out extra) ? extra : 0;   // (Api 26) FlyLayer
        }

        // Layers that are not geometry of the ship (overlays, masks, shadows): not reported as skipped.
        private static readonly HashSet<string> notGeometry = new HashSet<string>
        {
            "SectionLayer_LightingOverlay", "SectionLayer_FogOfWar", "SectionLayer_IndoorMask", "SectionLayer_GravshipMask",
            "SectionLayer_SunShadows", "SectionLayer_EdgeShadows", "SectionLayer_BuildingsDamage", "SectionLayer_ThingsPowerGrid"
        };

        private static bool Touches(SectionLayer layer, Func<float, float, byte> cellAt)
        {
            if (notGeometry.Contains(layer.GetType().Name) || WallPieces.notGeometry.Contains(layer.GetType().Name))
            {
                return false;
            }
            foreach (LayerSubMesh sm in layer.subMeshes)
            {
                if (!sm.finalized || sm.disabled)
                {
                    continue;
                }
                for (int i = 0; i < sm.verts.Count; i += 4)
                {
                    if (cellAt(sm.verts[i].x, sm.verts[i].z) >= Ship)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static Group GroupFor(List<Group> groups, Material mat, int layer, bool upper = false)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].mat == mat && groups[i].layer == layer && groups[i].upper == upper)
                {
                    return groups[i];
                }
            }
            Group g = new Group { mat = mat, layer = layer, upper = upper };
            g.chunks.Add(new Chunk());
            groups.Add(g);
            return g;
        }

        // Floor k's deck cells' slab, as Floors builds it for the floor's blocks (same colours, thickness and sides), in
        // drawn space round the pivot, lit like IsoRim's models.
        private void AddUpperSlab(List<Group> groups, Map map, ShipCells cells, int k)
        {
            Material mat = R3DMaterials.LitModel;
            if (mat == null)
            {
                return;
            }
            int shift = cells.shifts[k];
            List<IntVec3> deckCells = new List<IntVec3>();
            foreach (IntVec3 g in cells.DeckCells(k))
            {
                deckCells.Add(new IntVec3(g.x, 0, g.z + shift));
            }
            if (deckCells.Count == 0)
            {
                return;
            }
            List<Vector3> v = new List<Vector3>();
            List<Color32> col = new List<Color32>();
            List<int> t = new List<int>();
            Predicate<IntVec3> floored = c => c.InBounds(map) && cells.FloorOfRow(c.z) == k && !map.terrainGrid.TerrainAt(c).dontRender;
            Floors.SlabGeometry(map, deckCells, cells.Height(k), cells.Height(k - 1), floored, v, col, t);
            if (v.Count == 0)
            {
                return;
            }
            Vector3 from = pivot - cells.Lift(k).MultiplyPoint3x4(Vector3.zero);
            upperSlab += v.Count;
            Group grp = GroupFor(groups, mat, 0);
            Chunk ch = grp.chunks[grp.chunks.Count - 1];
            if (ch.verts.Count + v.Count > MaxVerts)
            {
                ch = new Chunk();
                grp.chunks.Add(ch);
            }
            int n = ch.verts.Count;
            for (int i = 0; i < v.Count; i++)
            {
                ch.verts.Add(v[i] - from);
                ch.colors.Add(col[i]);
                ch.uvs.Add(Vector3.zero);
                if (ch.hasNormals)
                {
                    ch.normals.Add(Vector3.up);
                }
            }
            foreach (int i in t)
            {
                ch.tris.Add(n + i);
            }
        }

        private static int Vertex(LayerSubMesh sm, Chunk ch, int i, int[] remap, Vector3 pivot)
        {
            if (remap[i] >= 0)
            {
                return remap[i];
            }
            int n = ch.verts.Count;
            ch.verts.Add(sm.verts[i] - pivot);
            ch.colors.Add(sm.colors.Count > i ? sm.colors[i] : new Color32(255, 255, 255, 255));
            ch.uvs.Add(sm.uvs.Count > i ? sm.uvs[i] : Vector3.zero);
            if (sm.normals.Count > i)
            {
                if (!ch.hasNormals)
                {
                    ch.hasNormals = true;
                    while (ch.normals.Count < n)
                    {
                        ch.normals.Add(Vector3.up);
                    }
                }
                ch.normals.Add(sm.normals[i]);
            }
            else if (ch.hasNormals)
            {
                ch.normals.Add(Vector3.up);
            }
            remap[i] = n;
            return n;
        }

        // The game's ground materials (terrain, the deck's hull and prop pieces: queue 2000-2099) write no depth: drawn in the
        // same queue as the map's own ground, whichever comes later wins, and the ground showed through the flying deck.
        // The ship's copies draw a little later (+50, their order among themselves kept), so they always cover the ground
        // under them; depth tests still let anything standing in front of the ship hide it. IsoRim's own materials write
        // depth and are left as they are.
        private static readonly Dictionary<Material, Material> lifted = new Dictionary<Material, Material>();
        private static HashSet<Shader> ownShaders;

        // The game's terrain writes no depth either, so the flying deck hid nothing drawn after it (ground plants and
        // decals showed through): the deck's terrain first writes its depth alone, before anything of the map is drawn.
        private static Material depthMat;
        private static bool depthTried;

        private static Material DepthMat
        {
            get
            {
                if (!depthTried)
                {
                    depthTried = true;
                    Shader d = R3DMaterials.ShaderOf("IsoRimDepthPlane");
                    if (d != null)
                    {
                        depthMat = new Material(d);
                        depthMat.name = "IsoRim_ShipDeckDepth";
                        depthMat.renderQueue = 1990;
                    }
                }
                return depthMat;
            }
        }

        internal static Material Flying(Material m)
        {
            if (m == null || m.renderQueue >= 2100)
            {
                return m;
            }
            if (ownShaders == null)
            {
                ownShaders = new HashSet<Shader>();
                foreach (ShaderDef d in DefDatabase<ShaderDef>.AllDefsListForReading)
                {
                    if (d.defName.StartsWith("IsoRim") && d.Shader != null)
                    {
                        ownShaders.Add(d.Shader);
                    }
                }
            }
            if (ownShaders.Contains(m.shader))
            {
                return m;
            }
            Material copy;
            if (!lifted.TryGetValue(m, out copy) || copy == null)
            {
                copy = new Material(m.shader);
                copy.CopyPropertiesFromMaterial(m);
                copy.renderQueue = m.renderQueue + 50;
                copy.name = m.name + "_ShipFlight";
                lifted[m] = copy;
            }
            return copy;
        }

        // ---- flying: drawn at the ground point (the cutscene centre moved by vanilla's drift), raised by a true height and
        // turned about the vertical (vanilla's wobble). The turn does not mix with the squashed height.

        public void Draw(Vector3 ground, float height, Quaternion turn)
        {
            Matrix4x4 m = Matrix4x4.TRS(new Vector3(ground.x, height / View3D.Squash, ground.z), turn, Vector3.one);
            Material slabMat = R3DMaterials.Solid;
            if (slab != null && slabMat != null && height > SlabFrom)
            {
                IsoGlow.DrawLit(slab, m, slabMat, 0);
            }
            for (int i = 0; i < meshes.Count; i++)
            {
                if (depths[i] && DepthMat != null)
                {
                    Graphics.DrawMesh(meshes[i], m, DepthMat, layers[i]);
                }
                Graphics.DrawMesh(meshes[i], m, mats[i], layers[i]);
                Material mark = View3D.Ready ? IsoGlow.MarkFor(sources[i]) : null;   // as IsoGlow.DrawLit
                if (mark != null)
                {
                    Graphics.DrawMesh(meshes[i], m, mark, layers[i]);
                }
            }
            for (int i = 0; i < items.Count; i++)
            {
                ShipCapture.Rec r = items[i];
                if (r.mesh == null)
                {
                    continue;   // a mesh the game has let go of since
                }
                Matrix4x4 mi = m * r.matrix;
                Graphics.DrawMesh(r.mesh, mi, r.mat, 0, null, 0, r.props);
                Material mark = View3D.Ready ? IsoGlow.MarkFor(r.mat) : null;
                if (mark != null)
                {
                    Graphics.DrawMesh(r.mesh, mi, mark, 0);
                }
            }
        }

        // ---- the deck's edge: vanilla's deck has no thickness, so in flight its rim was paper thin. A plain dark slab
        // about a quarter cell thick hangs under the deck's outline (deck cells, and the wedge halves of the hull corner
        // cells): its underside and the sides along the outline. Only drawn once the ship is clear of the ground (at rest
        // it would stand below the ground, which writes no depth).
        private const float SlabThickness = 0.25f;
        private const float SlabFrom = 0.3f;
        private static readonly Color32 SlabColor = new Color32(62, 64, 70, 255);
        private static readonly IntVec3[] Sides = { IntVec3.North, IntVec3.East, IntVec3.South, IntVec3.West };

        private void MakeSlab(ShipCells cells)
        {
            List<Vector3> v = new List<Vector3>();
            List<int> t = new List<int>();
            float s = View3D.Squash;
            float bottom = -SlabThickness / s;
            Func<float, float, float, Vector3> at = (x, y, z) => new Vector3(x - pivot.x, y, z - pivot.z);
            Action<Vector3, Vector3, Vector3, Vector3> quad = (a, b, c, d) =>
            {
                int n = v.Count;
                v.Add(a);
                v.Add(b);
                v.Add(c);
                v.Add(d);
                t.Add(n);
                t.Add(n + 1);
                t.Add(n + 2);
                t.Add(n);
                t.Add(n + 2);
                t.Add(n + 3);
            };
            Action<Vector2, Vector2> side = (p, q) => quad(at(p.x, 0f, p.y), at(q.x, 0f, q.y), at(q.x, bottom, q.y), at(p.x, bottom, p.y));
            foreach (IntVec3 c in cells.DeckCells())
            {
                quad(at(c.x, bottom, c.z), at(c.x + 1, bottom, c.z), at(c.x + 1, bottom, c.z + 1), at(c.x, bottom, c.z + 1));
                foreach (IntVec3 d in Sides)
                {
                    IntVec3 n = c + d;
                    Vector2 legs;
                    if (cells.IsDeck(n.x, n.z) || (cells.Corner(n.x, n.z, out legs) && legs.x * -d.x + legs.y * -d.z > 0.5f))
                    {
                        continue;   // deck goes on, or a corner piece stands against this side
                    }
                    // The cell's edge on side d, from one corner to the other.
                    Vector2 m = new Vector2(c.x + 0.5f + d.x * 0.5f, c.z + 0.5f + d.z * 0.5f);
                    Vector2 along = new Vector2(Mathf.Abs(d.z) * 0.5f, Mathf.Abs(d.x) * 0.5f);
                    side(m - along, m + along);
                }
            }
            foreach (IntVec3 c in cells.CornerCells())
            {
                Vector2 legs;
                cells.Corner(c.x, c.z, out legs);
                // The wedge: the half of the cell toward its legs; its slanted side runs between the two corners on the
                // diagonal across it.
                List<Vector2> on = new List<Vector2>(), inside = new List<Vector2>();
                for (int k = 0; k < 4; k++)
                {
                    Vector2 corner = new Vector2(c.x + ((k == 1 || k == 2) ? 1 : 0), c.z + (k >= 2 ? 1 : 0));
                    float d = (corner.x - c.x - 0.5f) * legs.x + (corner.y - c.z - 0.5f) * legs.y;
                    if (Mathf.Abs(d) < 0.01f)
                    {
                        on.Add(corner);
                    }
                    else if (d > 0f)
                    {
                        inside.Add(corner);
                    }
                }
                if (on.Count != 2 || inside.Count != 1)
                {
                    continue;
                }
                int n0 = v.Count;
                v.Add(at(on[0].x, bottom, on[0].y));
                v.Add(at(on[1].x, bottom, on[1].y));
                v.Add(at(inside[0].x, bottom, inside[0].y));
                t.Add(n0);
                t.Add(n0 + 1);
                t.Add(n0 + 2);
                side(on[0], on[1]);
            }
            if (v.Count == 0 || v.Count > 65000)
            {
                return;
            }
            List<Color32> col = new List<Color32>(v.Count);
            for (int i = 0; i < v.Count; i++)
            {
                col.Add(SlabColor);
            }
            slab = new Mesh();
            slab.name = "IsoRimShipSlab";
            slab.SetVertices(v);
            slab.SetTriangles(t, 0);
            slab.SetColors(col);
            slab.RecalculateBounds();
        }

        // ---- the shadow: a soft silhouette of the ship's cells (4 texels a cell, corner cells as their wedge half),
        // made on the CPU (the game's capture is the broken picture), laid on the ground away from IsoRim's sun or moon

        private const int ShadowTexels = 4;
        private const int ShadowMargin = 2;   // cells round the bounds, for the soft edge

        private void MakeShadow(ShipCells cells)
        {
            CellRect r = cells.bounds.ExpandedBy(ShadowMargin);
            int w = r.Width * ShadowTexels, h = r.Height * ShadowTexels;
            float[] a = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int cx = r.minX + x / ShadowTexels, cz = r.minZ + y / ShadowTexels;
                    float u = (x % ShadowTexels + 0.5f) / ShadowTexels, v = (y % ShadowTexels + 0.5f) / ShadowTexels;
                    a[y * w + x] = cells.Covers(cx, cz, u, v) ? 1f : 0f;
                }
            }
            // A soft edge about half a cell wide (wider reads as a smudge, and the ship hides most of it while low).
            for (int pass = 0; pass < 2; pass++)
            {
                a = Box(a, w, h, 1, true);
                a = Box(a, w, h, 1, false);
            }
            Color32[] px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a[i]) * 255f));
            }
            shadowTex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            shadowTex.name = "IsoRimShipShadow";
            shadowTex.wrapMode = TextureWrapMode.Clamp;
            shadowTex.filterMode = FilterMode.Bilinear;
            shadowTex.SetPixels32(px);
            shadowTex.Apply(true);
            shadowMat = new Material(ShaderDatabase.Transparent);
            shadowMat.name = "IsoRimShipShadow";
            shadowMat.mainTexture = shadowTex;
            shadowMat.color = new Color(0f, 0f, 0f, 0f);
            shadowSize = new Vector2(r.Width, r.Height);
        }

        private static float[] Box(float[] src, int w, int h, int rad, bool across)
        {
            float[] dst = new float[src.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    int n = 0;
                    for (int k = -rad; k <= rad; k++)
                    {
                        int xx = across ? x + k : x, yy = across ? y : y + k;
                        if (xx >= 0 && yy >= 0 && xx < w && yy < h)
                        {
                            sum += src[yy * w + xx];
                        }
                        n++;
                    }
                    dst[y * w + x] = sum / n;
                }
            }
            return dst;
        }

        // Drawn on the ground under the ship (its centre at the ground point of the flight), moved away from the light by
        // the ship's height (light direction toward the sun or moon: IsoRim's, as every 3D shadow), turned with the ship.
        // Fades in over the first cell of height (none at rest: the deck itself lies there), fainter at night, and out at
        // the end of the takeoff as vanilla's (progress 0.9-1).
        public void DrawShadow(Vector3 ground, float height, Quaternion turn, float progress, Map map)
        {
            if (shadowMat == null || map == null || map.Biome == BiomeDefOf.Space || map.Biome == BiomeDefOf.Orbit)
            {
                return;   // no ground under a ship over space (vanilla's see-through biomes, WorldComponent_GravshipController.BiomeHasTransparency)
            }
            float glow = GenCelestial.CurCelestialSunGlow(map);
            float alpha = 0.65f * Mathf.Lerp(0.4f, 1f, glow) * Mathf.SmoothStep(0f, 1f, height) * progress.RemapClamped(0.9f, 1f, 1f, 0f);
            if (alpha <= 0.004f)
            {
                return;
            }
            Vector3 light = View3D.LightNow();
            float up = Mathf.Max(0.3f, light.y);
            Vector3 off = new Vector3(-light.x, 0f, -light.z) / up * height;
            float grow = 1f + 0.006f * height;
            Vector3 at = new Vector3(ground.x + off.x, AltitudeLayer.Gas.AltitudeFor(), ground.z + off.z);
            if (!at.ToIntVec3().InBounds(map))
            {
                return;   // off the map: no ground to fall on
            }
            shadowMat.color = new Color(0f, 0f, 0f, alpha);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(at, turn, new Vector3(shadowSize.x * grow, 1f, shadowSize.y * grow)), shadowMat, 0);
        }

        // The middle of the ship's box, in true space (map x, height in cells, map z).
        public Vector3 Middle(Vector3 ground, float height, Quaternion turn)
        {
            Vector3 c = turn * new Vector3((min.x + max.x) * 0.5f, 0f, (min.z + max.z) * 0.5f);
            return new Vector3(ground.x + c.x, height + (min.y + max.y) * 0.5f, ground.z + c.z);
        }

        // The camera size (half the screen height, in cells) that shows the whole box round the focus, with a margin.
        public float FitSize(Vector3 ground, float height, Quaternion turn, Vector3 focus, float aspect)
        {
            Vector3 right = View3D.Right, up = View3D.Up;
            float need = 0f;
            for (int k = 0; k < 8; k++)
            {
                Vector3 c = new Vector3((k & 1) == 0 ? min.x : max.x, 0f, (k & 2) == 0 ? min.z : max.z);
                Vector3 p = turn * c + new Vector3(ground.x, height + ((k & 4) == 0 ? min.y : max.y), ground.z);
                Vector3 d = p - focus;
                need = Mathf.Max(need, Mathf.Max(Mathf.Abs(Vector3.Dot(d, up)), Mathf.Abs(Vector3.Dot(d, right)) / Mathf.Max(0.3f, aspect)));
            }
            return need * 1.15f + 2f;
        }

        public void Dispose()
        {
            foreach (Mesh m in meshes)
            {
                if (m != null)
                {
                    UnityEngine.Object.Destroy(m);
                }
            }
            meshes.Clear();
            mats.Clear();
            sources.Clear();
            depths.Clear();
            layers.Clear();
            items.Clear();
            if (slab != null)
            {
                UnityEngine.Object.Destroy(slab);
                slab = null;
            }
            if (shadowMat != null)
            {
                UnityEngine.Object.Destroy(shadowMat);
                shadowMat = null;
            }
            if (shadowTex != null)
            {
                UnityEngine.Object.Destroy(shadowTex);
                shadowTex = null;
            }
        }
    }
}
