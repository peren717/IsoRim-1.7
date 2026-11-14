using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only scenes for gas in 3D (GasSheets), real flow on the quick-test map (layout_gas3d*):
    //   gasscene=<label>,<kind>,x,z,<settle ticks>,<views>[,up1]
    //     kind pop:  the colonist in cell x,z (wearing a smokepop pack or a tox pack, from the layout) uses the pack through its
    //                own gizmo (Command_VerbTarget.ProcessInput: Targeter.BeginTargeting, the verb's own cast); up1: the
    //                colonist is first moved up its column to ProjectZ floor 1 (lay a slab there first: pzslab=)
    //     kind rot:  GsCorpses muffalo corpses round x,z made rotting (CompRottable.RotProgress at the rotting stage): the
    //                game's own CorpseGasEffectsTickRare makes the rot stink
    //     kind gas:  a cloud of BlindSmoke of radius GsCloudRadius by the game's GasUtility.AddGas (the performance scene)
    //     kind none: no gas (the scene without gas, for base vs head identity)
    //     then the game ticks until gas is in the cell (none: at once), and <settle ticks> more (the cloud spreads); paused.
    //     views, a colon list, each a screenshot renders\<tag>\gas_<label>_<view>.png with the camera on x,z (the floor's
    //     place for up1), held by a pushed view pose (Api 66) so Capture17's own shots do not move it:
    //       <d|n><p|c|w><0|1>[o]  d day (GsDay), n night (GsNight); p play zoom (GsPlay), c close-up (GsClose), w wide (GsWide);
    //                            0 the player's yaw, 1 a quarter turn on; o the pointer on the middle of the screen (a room
    //                            under it has its roof lifted, roofHover), else in the screen's corner
    //       f                    IsoRim's first person on the colonist in x,z (by the key's own path), day
    //       P                    performance: each gas block of the scene rebuilt GsRegenRuns times (its Regenerate, with
    //                            the postfixes), the time and vertex count logged; the frame times of GsPerfFrames frames
    //                            at play zoom (probe perf)
    //       2                    the 3D view off (the game's 2D view; turned back on at the end of the scene): views after it
    //                            are 2D shots (camera by the game's own driver)
    // Several gasscene keys queue and run one after the other. Log: "[IsoRim probe] gas <label> ...".
    public static partial class Probe
    {
        private const int GsStartWait = 320;       // frames before the first scene (Capture17 builds and settles first)
        private const int GsMaxTicks = 1200;       // ticks from the order to the first gas, at most
        private const int GsTicksPerFrame = 60;    // ticks run per frame while waiting and settling
        private const int GsSettle = 20;           // frames after a camera change before a screenshot
        private const int GsHourSettle = 60;       // frames after the hour changes
        private const float GsDay = 12f, GsNight = 23f;   // local hours of the day and night views
        private const float GsPlay = 11f, GsClose = 4f, GsWide = 18f;   // zoom: half the screen's height in cells
        private const int GsCorpses = 3;           // rot: muffalo corpses
        private const float GsCloudRadius = 12f;   // gas: the cloud's radius, cells
        private const int GsRegenRuns = 40;        // P: rebuilds of each gas block timed
        private const int GsPerfFrames = 240;      // P: frames timed

        private static readonly Queue<string[]> gsQueue = new Queue<string[]>();
        private static bool gsRunning;
        private static float gsYaw, gsPitch;
        private static bool gsPersp;
        private const int GsFpLeave = 150;         // frames after leaving first person (the camera swings back)

        private static bool GasKey(string key, string[] a, Map map)
        {
            if (key != "gasscene")
            {
                return false;
            }
            gsQueue.Enqueue(a);
            if (!gsRunning)
            {
                gsRunning = true;
                Api17Patch();
                FwStart(GsRunner(map));
            }
            return true;
        }

        private static IEnumerator<int> GsRunner(Map map)
        {
            yield return GsStartWait;
            // the player's camera angles and mode once, before any first person (leaving it swings the camera back over frames)
            gsYaw = View3D.Yaw;
            gsPitch = View3D.Pitch;
            gsPersp = View3D.Perspective;
            Log.Message("[IsoRim probe] gas view: yaw " + gsYaw.ToString("0.00") + ", pitch " + gsPitch.ToString("0.00") + ", perspective " + gsPersp);
            try
            {
                while (gsQueue.Count > 0)
                {
                    IEnumerator<int> scene = GsScene(gsQueue.Dequeue(), map);
                    while (scene.MoveNext())
                    {
                        yield return scene.Current;
                    }
                }
            }
            finally
            {
                gsRunning = false;
            }
            Log.Message("[IsoRim probe] gas all scenes done");
        }

        private static IEnumerator<int> GsScene(string[] a, Map map)
        {
            string label = a[0], kind = a[1];
            IntVec3 at = new IntVec3(I(a[2]), 0, I(a[3]));
            int settle = I(a[4]);
            string[] views = a[5].Split(':');
            bool up1 = a.Length > 6 && a[6] == "up1";
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            Directory.CreateDirectory(dir);
            string L = "[IsoRim probe] gas " + label + " ";
            foreach (Pawn dp in map.mapPawns.FreeColonistsSpawned)
            {
                if (dp.drafter != null)
                {
                    dp.drafter.Drafted = true;   // they stand where the layout put them (and the packs' gizmos show drafted)
                }
            }
            if (up1)
            {
                IntVec3 atUp = Floors.Stacked(map, at, 1);
                Pawn pm = at.GetFirstPawn(map);
                if (pm != null && atUp.IsValid)
                {
                    pm.DeSpawn();
                    GenSpawn.Spawn(pm, atUp, map);
                    pm.drafter.Drafted = true;
                }
                Log.Message(L + "up1: " + at + " -> " + atUp + " (floor of row " + Floors.FloorOfRow(atUp.z) + ")");
                at = atUp;
            }
            Pawn who = at.GetFirstPawn(map);
            int ticks = 0;
            if (kind == "pop")
            {
                Apparel pack = who?.apparel?.WornApparel.FirstOrDefault(ap => ap.GetWornGizmos().OfType<Command_VerbTarget>().Any());
                Command_VerbTarget cmd = pack?.GetWornGizmos().OfType<Command_VerbTarget>().FirstOrDefault();
                if (cmd == null)
                {
                    Check("gas " + label, false, "no colonist with a usable pack at " + at + (who != null ? " (" + who.LabelShort + ")" : ""));
                    yield break;
                }
                cmd.ProcessInput(null);
                Log.Message(L + "pop: " + who.LabelShort + " at " + at + " used " + pack.def.defName + " (" + cmd.verb.GetType().Name + "), job " + who.CurJobDef?.defName);
            }
            else if (kind == "rot")
            {
                ThingDef race = DefDatabase<ThingDef>.GetNamed("Muffalo");
                PawnKindDef pk = DefDatabase<PawnKindDef>.GetNamed("Muffalo");
                for (int i = 0; i < GsCorpses; i++)
                {
                    IntVec3 c = at + new IntVec3(i - GsCorpses / 2, 0, 0) * 2;
                    Pawn animal = PawnGenerator.GeneratePawn(new PawnGenerationRequest(pk, null, fixedBiologicalAge: 5f, fixedChronologicalAge: 5f));
                    GenSpawn.Spawn(animal, c, map);
                    animal.Kill(null);
                    Corpse corpse = animal.Corpse;
                    CompRottable rot = corpse?.TryGetComp<CompRottable>();
                    if (rot != null)
                    {
                        rot.RotProgress = rot.PropsRot.TicksToRotStart + 1f;
                    }
                    Log.Message(L + "rot: corpse of " + race.defName + " at " + c + " stage " + corpse?.GetRotStage());
                }
            }
            else if (kind == "gas")
            {
                GasUtility.AddGas(at, map, GasType.BlindSmoke, GsCloudRadius);
                Log.Message(L + "gas: BlindSmoke at " + at);
            }
            if (kind != "none")
            {
                while (!map.gasGrid.AnyGasAt(at) && ticks < GsMaxTicks)
                {
                    for (int i = 0; i < GsTicksPerFrame && !map.gasGrid.AnyGasAt(at); i++)
                    {
                        Find.TickManager.DoSingleTick();
                        ticks++;
                    }
                    yield return 1;
                }
                Check("gas " + label + " made", map.gasGrid.AnyGasAt(at), "after " + ticks + " ticks");
                for (int t = 0; t < settle;)
                {
                    for (int i = 0; i < GsTicksPerFrame && t < settle; i++, t++)
                    {
                        Find.TickManager.DoSingleTick();
                    }
                    yield return 1;
                }
            }
            foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
            {
                tp.Drawer.tweener.ResetTweenedPosToRoot();
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            int cells = 0;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(at, 20f, true))
            {
                if (c.InBounds(map) && map.gasGrid.AnyGasAt(c))
                {
                    cells++;
                }
            }
            Log.Message(L + "ready at tick " + Find.TickManager.TicksGame + ": " + cells + " gas cells within 20, " + GsStats());
            yield return 5;   // the blocks rebuilt

            Vector3 look = new Vector3(at.x + 0.5f, 0f, at.z + 0.5f);
            Floors.Placed(ref look);
            float baseYaw = gsYaw;
            float hour = -1f;
            bool was3D = View3D.Active;
            foreach (string v in views)
            {
                if (v == "2")
                {
                    View3D.Active = false;
                    yield return GsHourSettle;
                    Log.Message(L + "2D view: active " + View3D.Active);
                    continue;
                }
                if (v == "f")
                {
                    if (hour != GsDay)
                    {
                        SetLocalHour(GsDay);
                        hour = GsDay;
                        yield return GsHourSettle;
                    }
                    HookMouse();
                    fakeMouse = new Vector2(2f, 2f);   // the pointer in a corner: no roof lifted by it (the real one is anywhere)
                    FwEnterFirstPerson(map, at);
                    yield return GsSettle * 2;
                    GsShot(dir, label, v);
                    Log.Message(L + "first person on " + (Api.FirstPersonPawn != null ? Api.FirstPersonPawn.LabelShort : "nobody") + ", eye " + View3D.Eye.ToString("F3"));
                    yield return 2;
                    if (FirstPerson.On)
                    {
                        FirstPerson.Toggle();
                    }
                    fakeMouse = null;
                    yield return GsFpLeave;
                    continue;
                }
                if (v == "P")
                {
                    IEnumerator<int> perf = GsPerf(map, at, look, baseYaw, L);
                    while (perf.MoveNext())
                    {
                        yield return perf.Current;
                    }
                    continue;
                }
                float h = v[0] == 'n' ? GsNight : GsDay;
                float size = v[1] == 'c' ? GsClose : (v[1] == 'w' ? GsWide : GsPlay);
                float yaw = baseYaw + (v[2] == '1' ? 90f : 0f);
                bool pointer = v.Length > 3 && v[3] == 'o';
                if (h != hour)
                {
                    SetLocalHour(h);
                    hour = h;
                    yield return GsHourSettle;
                }
                HookMouse();
                fakeMouse = pointer ? new Vector2(Screen.width * 0.5f, Screen.height * 0.5f) : new Vector2(2f, 2f);
                IDisposable pose = null;
                if (View3D.Active)
                {
                    ViewPose p = new ViewPose { Yaw = yaw, Pitch = gsPitch, Perspective = gsPersp, Focus = look, OrthoSize = size };
                    pose = Api.PushViewPose(typeof(Probe), () => p, new ViewPoseOptions { CountsAsTurning = false });
                }
                else
                {
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(look.x, 0f, look.z), size);
                }
                yield return GsSettle;
                GsShot(dir, label, v);
                yield return 2;
                pose?.Dispose();
                fakeMouse = null;
            }
            if (was3D && !View3D.Active)
            {
                View3D.Active = true;
                yield return GsHourSettle;
            }
            Log.Message(L + "done, " + GsStats());
        }

        private static void GsShot(string dir, string label, string view)
        {
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "gas_" + label + "_" + view + ".png"));
        }

        // GasSheets' totals (read by name: the base build has no GasSheets).
        private static string GsStats()
        {
            Type t = AccessTools.TypeByName("IsoRim.GasSheets");
            if (t == null)
            {
                return "no GasSheets (base build)";
            }
            long ticks = (long)AccessTools.Field(t, "Ticks").GetValue(null);
            return "GasSheets blocks " + AccessTools.Field(t, "Blocks").GetValue(null) + ", quads in " + AccessTools.Field(t, "QuadsIn").GetValue(null)
                + ", sheet quads " + AccessTools.Field(t, "QuadsOut").GetValue(null) + ", " + (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.00") + " ms in all";
        }

        // P: the scene's gas blocks rebuilt and timed, their vertices counted; then the frame times at play zoom.
        private static IEnumerator<int> GsPerf(Map map, IntVec3 at, Vector3 look, float yaw, string L)
        {
            HashSet<Section> sections = new HashSet<Section>();
            foreach (IntVec3 c in GenRadial.RadialCellsAround(at, GsCloudRadius + 6f, true))
            {
                if (c.InBounds(map) && map.gasGrid.AnyGasAt(c))
                {
                    sections.Add(map.mapDrawer.SectionAt(c));
                }
            }
            int verts = 0, subs = 0, gasCells = 0;
            double ms = 0;
            foreach (Section s in sections)
            {
                SectionLayer layer = s.GetLayer(typeof(SectionLayer_Gas));
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < GsRegenRuns; i++)
                {
                    layer.Regenerate();
                }
                sw.Stop();
                ms += sw.Elapsed.TotalMilliseconds / GsRegenRuns;
                foreach (LayerSubMesh sm in layer.subMeshes)
                {
                    if (sm.finalized && !sm.disabled)
                    {
                        verts += sm.verts.Count;
                        subs++;
                    }
                }
                foreach (IntVec3 c in s.CellRect)
                {
                    gasCells += map.gasGrid.AnyGasAt(c) ? 1 : 0;
                }
            }
            Log.Message(L + "perf regen: " + sections.Count + " gas blocks, " + gasCells + " gas cells, " + subs + " sub-meshes drawn, " + verts + " vertices; Regenerate "
                + ms.ToString("0.000") + " ms per rebuild of all of them (mean of " + GsRegenRuns + ")");
            IDisposable pose = null;
            if (View3D.Active)
            {
                ViewPose p = new ViewPose { Yaw = yaw, Pitch = gsPitch, Perspective = gsPersp, Focus = look, OrthoSize = GsPlay };
                pose = Api.PushViewPose(typeof(Probe), () => p, new ViewPoseOptions { CountsAsTurning = false });
            }
            yield return GsSettle;
            Scene("perf", new[] { GsPerfFrames.ToString(), "gas" });
            yield return GsPerfFrames + 20;
            pose?.Dispose();
        }
    }
}
