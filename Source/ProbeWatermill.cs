using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness only (watermill test, Api 79): the watermill's wheel and spin parts on real game flows. Needs a river map
    // (command line -isorimriver with -isorimbiome). Built against master and ed75127 too (base runs): it reads IsoRim only
    // through members those have (Spinners.Report, SinkHoles.Report and Spinners' record switch by name, Api.PushViewPose,
    // ModelDef.spinPivot) and parses the report text.
    //   millscene=mills    watermills placed by the game's own placement rules (GenConstruct.CanPlaceBlueprintAt, the
    //                      watermill's PlaceWorker) along the map's real river, one per facing found, in a fixed scan order;
    //                      checks over real ticks: the drawn wheel angle is the game's spinPosition, it turns, it stops when
    //                      broken down (CompBreakdownable.DoBreakdown) and when its water is gone (terrain changed, the
    //                      game's own 1200-tick cache rebuild), direction follows spinRate, a second watermill on the same
    //                      water halves the first one's speed; shots of each facing, a 4-frame spin strip, the water line,
    //                      a colonist walking into the water beside a wheel, a wind turbine, 3D off; then 1.65 locked walls.
    //                      The game's flat blades (CompPowerPlantWater.PostDraw, its BladesMat made magenta for the run):
    //                      not drawn in 3D close from the game camera and low (its body not run for the mill, no magenta
    //                      pixel in the shot), drawn with 3D models off (the detector's control); and at the view's edge: a
    //                      mill whose Position is 3 cells off the game's view rect on its body's side, the game still
    //                      culling it in: the wheel is drawn and the flat blades are not (the rect is wider than the screen
    //                      in 3D, so this is read from the draw state, not the image).
    //   millscene=extras   on a strip of bare land near the map's middle: a wind turbine (its rotor through the shared
    //                      drawing), a solar generator (close and low: its swatch texture) and a pit gate (a sunk hole);
    //                      shots only, for base / head image compares. Built against master too (all by name).
    //   millscene=testmod  the test-only mod's things (tools/runners/watermill/testmod): an XML spin part on an axle with a
    //                      fixed speed (EndTable, Stool), a C# provider's angle (PlantPot); checks that each turns, shots
    //                      from the game camera and low (the paddles dipping below the ground: the ground hides them).
    // Lines: "[IsoRim probe] CHECK mill <name> PASS|FAIL ..." and "[IsoRim probe] mill ...".
    public static partial class Probe
    {
        private const int MsStartWait = 320;       // frames before the first scene (Capture17 builds and settles first)
        private const int MsSettle = 25;           // frames after a camera change before a screenshot
        private const int MsSamples = 6;           // angle samples per watermill
        private const int MsSampleTicks = 30;      // game ticks between samples
        private const float MsPlay = 7f, MsClose = 4f;   // zoom: half the screen's height in cells
        private const float MsLowPitch = 12f;      // the low camera's pitch, degrees
        private const string MsL = "[IsoRim probe] mill ";
        private const int MsTestWidth = 20;                        // the test things' strip of land, cells
        private static readonly int[] MsTestAt = { 1, 17, 9 };    // EndTable, Stool, PlantPot along it (as placed since the first runs)

        private const int MsEdgeOff = 3;           // the edge check: cells the mill's Position lies off the view (> the old rule's 2)

        private static readonly Queue<string[]> msQueue = new Queue<string[]>();
        private static bool msBladesPatched;
        private static Thing msBladesOf;           // the mill whose flat-blade drawing is counted
        private static int msBladeBodies;          // times its CompPowerPlantWater.PostDraw body ran (the nine blade meshes)
        private static CellRect msShotView;        // the game's view rect (CameraDriver.CurrentViewRect) when MsBladeShot shot
        private static bool msRunning;
        private static readonly List<Thing> msMills = new List<Thing>();
        private static IntVec3 msLand = IntVec3.Invalid;

        private static bool MillKey(string key, string[] a, Map map)
        {
            if (key != "millscene")
            {
                return false;
            }
            msQueue.Enqueue(a);
            if (!msRunning)
            {
                msRunning = true;
                Api17Patch();
                FwStart(MsRunner(map));
            }
            return true;
        }

        private static IEnumerator<int> MsRunner(Map map)
        {
            yield return MsStartWait;
            try
            {
                while (msQueue.Count > 0)
                {
                    string[] a = msQueue.Dequeue();
                    MsRecord(true);
                    IEnumerator<int> scene = a[0] == "testmod" ? MsTestMod(map) : a[0] == "extras" ? MsExtras(map) : MsMills(map);
                    try
                    {
                        while (scene.MoveNext())
                        {
                            yield return scene.Current;
                        }
                    }
                    finally
                    {
                        scene.Dispose();
                    }
                }
            }
            finally
            {
                msRunning = false;
                MsRecord(false);
            }
            Log.Message(MsL + "all scenes done");
        }

        private static void MsCheck(string name, bool pass, string detail)
        {
            Log.Message("[IsoRim probe] CHECK mill " + name + " " + (pass ? "PASS" : "FAIL") + " " + detail);
        }

        private static string MsDir()
        {
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static float MsGameDeg(Thing t)
        {
            CompPowerPlantWater c = t.TryGetComp<CompPowerPlantWater>();
            return c == null ? float.NaN : Traverse.Create(c).Field("spinPosition").GetValue<float>() * Mathf.Rad2Deg;
        }

        private static float MsSpinRate(Thing t)
        {
            CompPowerPlantWater c = t.TryGetComp<CompPowerPlantWater>();
            return c == null ? float.NaN : Traverse.Create(c).Field("spinRate").GetValue<float>();
        }

        // The angle IsoRim drew last for the thing's spin part ("drawn <x>" in Spinners.Report), NaN if none.
        private static float MsDrawn(Thing t, out string report)
        {
            report = Spinners.Report(t);
            int i = report.IndexOf(" drawn ", StringComparison.Ordinal);
            if (i < 0)
            {
                return float.NaN;
            }
            string rest = report.Substring(i + 7);
            int sp = rest.IndexOf(' ');
            float v;
            return float.TryParse(sp > 0 ? rest.Substring(0, sp) : rest, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : float.NaN;
        }

        private static float MsDiff(float a, float b)
        {
            return Mathf.Abs(Mathf.DeltaAngle(a, b));
        }

        // Spinners' harness record of what it drew (Spinners.Report's "drawn ..."), kept only while a probe asks for it; by
        // name (a build without the switch records always).
        private static void MsRecord(bool on)
        {
            var f = AccessTools.Field(typeof(Spinners), "record");
            if (f != null)
            {
                f.SetValue(null, on);
            }
        }

        // The frame Spinners last drew the thing's spin part in ("frame <n>" in Spinners.Report), -1 if none.
        private static int MsDrawnFrame(Thing t)
        {
            string report = Spinners.Report(t);
            int i = report.IndexOf(" frame ", StringComparison.Ordinal);
            if (i < 0)
            {
                return -1;
            }
            string rest = report.Substring(i + 7);
            int sp = rest.IndexOf(' ');
            int v;
            return int.TryParse(sp > 0 ? rest.Substring(0, sp) : rest, out v) ? v : -1;
        }

        // The wheel's axle point: the thing's centre and the binding's spinPivot along its facing (fitted as Spinners places it).
        private static Vector3 MsWheel(Thing t)
        {
            ModelEntry e;
            var pivot = AccessTools.Field(typeof(ModelDef), "spinPivot");   // by name: master has no such field
            float along = ModelLibrary.TryGet(t.def, out e) && pivot != null
                ? ((Vector3)pivot.GetValue(e.def)).z * (e.Fit(t.def) * e.def.scale).z + e.def.offset.z : 0f;
            return t.TrueCenter() + t.Rotation.FacingCell.ToVector3() * along;
        }

        // Counts the runs of CompPowerPlantWater.PostDraw's own body (its nine flat blade meshes) for msBladesOf.
        private static void MsBladesPatch()
        {
            if (msBladesPatched)
            {
                return;
            }
            msBladesPatched = true;
            new Harmony("peren.isorim17.probe.millblades").Patch(AccessTools.DeclaredMethod(typeof(CompPowerPlantWater), "PostDraw"),
                postfix: new HarmonyMethod(typeof(Probe), nameof(MsBlades_Postfix)));
        }

        public static void MsBlades_Postfix(ThingComp __instance, bool __runOriginal)
        {
            if (__runOriginal && __instance != null && msBladesOf != null && __instance.parent == msBladesOf)
            {
                msBladeBodies++;
            }
        }

        // Pixels of the game's blade colour (BladesMat made pure magenta for the run) in a screenshot; -1 if unreadable.
        private static int MsMagenta(string png)
        {
            if (!File.Exists(png))
            {
                return -1;
            }
            Texture2D tex = new Texture2D(2, 2);
            try
            {
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png), false))
                {
                    return -1;
                }
                int n = 0;
                foreach (Color32 c in tex.GetPixels32())
                {
                    if (c.r > 150 && c.b > 150 && c.g < 90)
                    {
                        n++;
                    }
                }
                return n;
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }

        // A shot of t with its flat-blade drawing counted over the frames before it; out: body runs, magenta pixels, frame
        // the spin part was last drawn, the frame of the shot.
        private static IEnumerator<int> MsBladeShot(string name, Thing t, Vector3 focus, float size, float pitch, int[] result)
        {
            IDisposable pose = MsPose(focus, size, pitch);
            yield return MsSettle * 3;
            msBladesOf = t;
            msBladeBodies = 0;
            yield return 10;
            result[2] = MsDrawnFrame(t);
            result[3] = Time.frameCount;
            msShotView = Find.CameraDriver.CurrentViewRect;
            string png = Path.Combine(MsDir(), "mill_" + name + ".png");
            ScreenCapture.CaptureScreenshot(png);
            yield return 4;
            result[0] = msBladeBodies;
            msBladesOf = null;
            result[1] = MsMagenta(png);
            for (int w = 0; w < 120 && result[1] < 0; w++)   // the screenshot is written a few frames later
            {
                yield return 1;
                result[1] = MsMagenta(png);
            }
            pose?.Dispose();
        }

        // How many cells p lies off the view on the side opposite to facing f (negative: inside).
        private static int MsOff(CellRect view, IntVec3 p, IntVec3 f)
        {
            if (f.z > 0)
            {
                return view.minZ - p.z;
            }
            if (f.z < 0)
            {
                return p.z - view.maxZ;
            }
            return f.x > 0 ? view.minX - p.x : p.x - view.maxX;
        }

        // The game's flat blades left out in 3D (close from the game camera and low; drawn with 3D off, the control), and at
        // the view's edge the wheel drawn while the mill's Position is off the view. BladesMat is magenta for the scene.
        private static IEnumerator<int> MsBlades(Thing mill)
        {
            MsBladesPatch();
            Material blades = CompPowerPlantWater.BladesMat;
            Texture was = blades.mainTexture;
            Texture2D magenta = new Texture2D(4, 4);
            Color32[] px = new Color32[16];
            for (int k = 0; k < px.Length; k++)
            {
                px[k] = new Color32(255, 0, 255, 255);
            }
            magenta.SetPixels32(px);
            magenta.Apply();
            blades.mainTexture = magenta;
            try
            {
                int[] r = new int[4];
                foreach (bool low in new[] { false, true })
                {
                    string name = low ? "flatblades_low" : "flatblades_game";
                    IEnumerator<int> sh = MsBladeShot(name, mill, MsWheel(mill), MsClose, low ? MsLowPitch : View3D.Pitch, r);
                    while (sh.MoveNext())
                    {
                        yield return sh.Current;
                    }
                    MsCheck(name, r[0] == 0 && r[1] == 0 && r[2] >= r[3] - 2, "3D on, close" + (low ? " and low" : "") + ": the game's blade drawing ran "
                        + r[0] + " times, " + r[1] + " blade (magenta) pixels; wheel drawn in frame " + r[2] + " (shot " + r[3] + ")");
                }
                // the view's edge
                {
                    IntVec3 f = mill.Rotation.FacingCell, p = mill.Position;
                    float shift = 0f;
                    for (int k = 0; k < 5; k++)
                    {
                        IDisposable ep = MsPose(MsWheel(mill) + f.ToVector3() * shift, MsClose, View3D.Pitch);
                        yield return MsSettle * 3;
                        int off = MsOff(Find.CameraDriver.CurrentViewRect, p, f);
                        ep?.Dispose();
                        if (off == MsEdgeOff)
                        {
                            break;
                        }
                        shift += MsEdgeOff - off;
                    }
                    IEnumerator<int> sh = MsBladeShot("edge", mill, MsWheel(mill) + f.ToVector3() * shift, MsClose, View3D.Pitch, r);
                    while (sh.MoveNext())
                    {
                        yield return sh.Current;
                    }
                    CellRect view = msShotView;
                    int offNow = MsOff(view, p, f);
                    bool oldOut = !view.ExpandedBy(2).Contains(p);
                    bool gameDraws = view.ExpandedBy(1).Overlaps(mill.OccupiedDrawRect());
                    MsCheck("edge", oldOut && gameDraws && r[2] >= r[3] - 2 && r[0] == 0 && r[1] == 0,
                        "Position " + p + " " + offNow + " cells off the view " + view + " (game culls it in: " + gameDraws + "): wheel drawn in frame "
                        + r[2] + " (shot " + r[3] + "), the game's blade drawing ran " + r[0] + " times, " + r[1] + " blade pixels");
                }
                // 3D models off (the 3D view on): the game draws its blades, and they are seen (the detector works). (3D off
                // cannot be framed here: Capture17's own shots keep its camera.)
                bool models = IsoRimMod.Settings.models3D;
                IsoRimMod.Settings.models3D = false;
                yield return 30;
                try
                {
                    IEnumerator<int> sh = MsBladeShot("flatblades_control", mill, MsWheel(mill), MsClose, View3D.Pitch, r);
                    while (sh.MoveNext())
                    {
                        yield return sh.Current;
                    }
                    MsCheck("flatblades_control", r[0] > 0 && r[1] > 0, "3D models off: the game's blade drawing ran " + r[0] + " times, " + r[1] + " blade pixels");
                }
                finally
                {
                    IsoRimMod.Settings.models3D = models;
                }
                yield return 30;
            }
            finally
            {
                blades.mainTexture = was;
                UnityEngine.Object.Destroy(magenta);
            }
        }

        private static IEnumerator<int> MsTicks(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Find.TickManager.DoSingleTick();
            }
            yield return 3;   // drawn
        }

        private static IDisposable MsPose(Vector3 focus, float size, float pitch)
        {
            if (!View3D.Active)
            {
                Find.CameraDriver.SetRootPosAndSize(new Vector3(focus.x, 0f, focus.z), size);
                return null;
            }
            ViewPose p = new ViewPose { Yaw = View3D.Yaw, Pitch = pitch, Perspective = false, Focus = focus, OrthoSize = size };
            return Api.PushViewPose(typeof(Probe), () => p, new ViewPoseOptions { CountsAsTurning = false });
        }

        private static IEnumerator<int> MsShot(string name, Vector3 focus, float size, float pitch)
        {
            IDisposable pose = MsPose(focus, size, pitch);
            yield return MsSettle * 3;   // the zoom eases in
            ScreenCapture.CaptureScreenshot(Path.Combine(MsDir(), "mill_" + name + ".png"));
            yield return 3;
            pose?.Dispose();
        }

        // The first place (fixed scan order) where the game lets a watermill stand with this facing, not touching the mills
        // already placed (nor their water, unless near is set: then its water must overlap near's).
        private static IntVec3 MsFind(Map map, Rot4 rot, Thing near)
        {
            ThingDef def = ThingDefOf.WatermillGenerator;
            for (int z = 4; z < map.Size.z - 4; z++)
            {
                for (int x = 4; x < map.Size.x - 4; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (!GenConstruct.CanPlaceBlueprintAt(def, c, rot, map).Accepted)
                    {
                        continue;
                    }
                    CellRect foot = GenAdj.OccupiedRect(c, rot, def.size);
                    CellRect water = CompPowerPlantWater.WaterUseRect(c, rot);
                    bool ok = true;
                    foreach (Thing m in msMills)
                    {
                        if (foot.ExpandedBy(2).Overlaps(m.OccupiedRect()))
                        {
                            ok = false;
                            break;
                        }
                        bool shares = water.Overlaps(CompPowerPlantWater.WaterUseRect(m.Position, m.Rotation));
                        if (m == near ? !shares : shares)
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (ok)
                    {
                        return c;
                    }
                }
            }
            return IntVec3.Invalid;
        }

        private static Thing MsSpawn(Map map, ThingDef def, ThingDef stuff, IntVec3 c, Rot4 rot)
        {
            Thing t = ThingMaker.MakeThing(def, stuff);
            t.SetFaction(Faction.OfPlayer);
            return GenSpawn.Spawn(t, c, map, rot);
        }

        // Samples the drawn angle against the game's over real ticks; returns the game angle's change per tick (degrees).
        private static IEnumerator<int> MsSample(Thing t, string tag, List<float> perTick)
        {
            float firstGame = MsGameDeg(t), worst = 0f;
            string rep = "";
            int n = 0;
            for (int s = 0; s < MsSamples; s++)
            {
                IEnumerator<int> tk = MsTicks(MsSampleTicks);
                while (tk.MoveNext())
                {
                    yield return tk.Current;
                }
                float game = MsGameDeg(t);
                float drawn = MsDrawn(t, out rep);
                worst = float.IsNaN(drawn) ? float.PositiveInfinity : Mathf.Max(worst, MsDiff(drawn, game));
                n++;
            }
            float lastGame = MsGameDeg(t);
            float total = Mathf.DeltaAngle(firstGame, lastGame);   // small steps: no wrap over MsSamples * MsSampleTicks ticks
            perTick.Add(total / (MsSamples * MsSampleTicks));
            Log.Message(MsL + tag + " " + t.Position + " " + t.Rotation.ToStringHuman() + ": " + rep + " | spinRate " + MsSpinRate(t).ToString("0.000")
                + " power " + t.TryGetComp<CompPowerTrader>().PowerOutput.ToString("0") + " worst diff " + worst.ToString("0.0000"));
            MsCheck("angle_" + tag, worst < 0.01f, "drawn wheel angle = game spinPosition over " + n + " samples (worst " + worst.ToString("0.0000") + " deg)");
        }

        private static IEnumerator<int> MsMills(Map map)
        {
            map.fogGrid.ClearAllFog();
            foreach (Pawn dp in map.mapPawns.FreeColonistsSpawned)
            {
                if (dp.drafter != null)
                {
                    dp.drafter.Drafted = true;
                }
            }
            msMills.Clear();
            ThingDef def = ThingDefOf.WatermillGenerator;
            foreach (Rot4 r in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                IntVec3 c = MsFind(map, r, null);
                if (c.IsValid)
                {
                    msMills.Add(MsSpawn(map, def, null, c, r));
                }
                Log.Message(MsL + "place " + r.ToStringHuman() + ": " + (c.IsValid ? c.ToString() : "none"));
            }
            MsCheck("placed", msMills.Count > 0, msMills.Count + " watermills placed on the river");
            if (msMills.Count == 0)
            {
                yield break;
            }
            yield return 10;
            Log.Message(MsL + "below-ground masks: " + AccessTools.Method(typeof(SinkHoles), "Report").Invoke(null, new object[] { map }));
            // spin: drawn = game, turning, direction
            Dictionary<Thing, float> speed = new Dictionary<Thing, float>();
            foreach (Thing m in msMills)
            {
                IDisposable pose = MsPose(m.TrueCenter(), MsPlay, View3D.Pitch);
                yield return MsSettle;
                List<float> pt = new List<float>();
                IEnumerator<int> sm = MsSample(m, "r" + m.Rotation.AsInt, pt);
                while (sm.MoveNext())
                {
                    yield return sm.Current;
                }
                pose?.Dispose();
                speed[m] = pt[0];
                MsCheck("turns_r" + m.Rotation.AsInt, Mathf.Abs(pt[0]) > 0.05f, "game angle per tick " + pt[0].ToString("0.000") + " deg");
                float rate = MsSpinRate(m);
                MsCheck("direction_r" + m.Rotation.AsInt, Mathf.Sign(pt[0]) == Mathf.Sign(rate), "angle change " + pt[0].ToString("0.000") + " follows spinRate " + rate.ToString("0.000"));
            }
            // shots: each facing (game camera), 3D off
            foreach (Thing m in msMills)
            {
                IEnumerator<int> sh = MsShot("r" + m.Rotation.AsInt, m.TrueCenter(), MsPlay, View3D.Pitch);
                while (sh.MoveNext())
                {
                    yield return sh.Current;
                }
            }
            Thing first = msMills[0];
            // spin strip: four frames about 10 degrees of game angle apart, close
            {
                float perTick = Mathf.Abs(speed[first]);
                int step = perTick > 0.01f ? Mathf.Max(1, Mathf.RoundToInt(10f / perTick)) : 26;
                IDisposable pose = MsPose(MsWheel(first), MsClose, View3D.Pitch);
                yield return MsSettle;
                for (int k = 0; k < 4; k++)
                {
                    float drawn;
                    string rep;
                    drawn = MsDrawn(first, out rep);
                    Log.Message(MsL + "strip " + k + " game " + MsGameDeg(first).ToString("0.000") + " drawn " + drawn.ToString("0.000"));
                    ScreenCapture.CaptureScreenshot(Path.Combine(MsDir(), "mill_strip" + k + ".png"));
                    yield return 3;
                    IEnumerator<int> tk = MsTicks(step);
                    while (tk.MoveNext())
                    {
                        yield return tk.Current;
                    }
                    yield return 5;
                }
                pose?.Dispose();
            }
            // the game's flat blades and the view's edge
            {
                IEnumerator<int> bl = MsBlades(first);
                while (bl.MoveNext())
                {
                    yield return bl.Current;
                }
            }
            // the water line, low and close; a colonist walks into the water beside the wheel
            {
                Vector3 wheel = MsWheel(first);
                IEnumerator<int> sh = MsShot("waterline", wheel, MsClose, MsLowPitch);
                while (sh.MoveNext())
                {
                    yield return sh.Current;
                }
                Pawn walker = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault();
                IntVec3 side = (wheel + first.Rotation.FacingCell.ToVector3() * 1.5f + first.Rotation.Rotated(RotationDirection.Clockwise).FacingCell.ToVector3() * 3f).ToIntVec3();
                if (walker != null && side.InBounds(map) && side.Standable(map))
                {
                    walker.drafter.Drafted = true;
                    walker.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, side));
                    int guard = 0;
                    while (walker.Position != side && guard++ < 40)
                    {
                        IEnumerator<int> tk = MsTicks(60);
                        while (tk.MoveNext())
                        {
                            yield return tk.Current;
                        }
                    }
                    walker.Drawer.tweener.ResetTweenedPosToRoot();
                    Log.Message(MsL + "walker at " + walker.Position + " (wanted " + side + ") terrain " + walker.Position.GetTerrain(map).defName + " swimming " + walker.Swimming);
                    IEnumerator<int> sw = MsShot("wader", wheel, MsPlay, View3D.Pitch);
                    while (sw.MoveNext())
                    {
                        yield return sw.Current;
                    }
                    IEnumerator<int> sl = MsShot("wader_low", wheel, MsClose + 1f, MsLowPitch);
                    while (sl.MoveNext())
                    {
                        yield return sl.Current;
                    }
                }
                else
                {
                    Log.Message(MsL + "no walker cell beside the wheel (" + side + ")");
                }
            }
            // a wind turbine on open land (its rotor through the shared drawing): base vs head image
            {
                IntVec3 wc = IntVec3.Invalid;
                for (int z = 10; z < map.Size.z - 10 && !wc.IsValid; z++)
                {
                    for (int x = 10; x < map.Size.x - 10; x++)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        if (GenConstruct.CanPlaceBlueprintAt(ThingDef.Named("WindTurbine"), c, Rot4.North, map).Accepted
                            && !msMills.Any(m => m.OccupiedRect().ExpandedBy(6).Contains(c)))
                        {
                            wc = c;
                            break;
                        }
                    }
                }
                if (wc.IsValid)
                {
                    Thing wt = MsSpawn(map, ThingDef.Named("WindTurbine"), null, wc, Rot4.North);
                    yield return 5;
                    Log.Message(MsL + "wind turbine at " + wc);
                    IEnumerator<int> sh = MsShot("turbine", wt.TrueCenter(), MsClose + 1f, View3D.Pitch);
                    while (sh.MoveNext())
                    {
                        yield return sh.Current;
                    }
                }
            }
            // half speed: a second watermill on the water of one placed (the first that has room for one)
            {
                IntVec3 c = IntVec3.Invalid;
                Thing host = null;
                foreach (Thing m in msMills.ToList())
                {
                    c = MsFind(map, m.Rotation, m);
                    if (c.IsValid)
                    {
                        host = m;
                        break;
                    }
                }
                if (c.IsValid)
                {
                    Thing first0 = first;
                    first = host;
                    float before = speed[first];
                    Thing twin = MsSpawn(map, def, null, c, first.Rotation);
                    msMills.Add(twin);
                    IEnumerator<int> tk = MsTicks(5);
                    while (tk.MoveNext())
                    {
                        yield return tk.Current;
                    }
                    IDisposable pose = MsPose(first.TrueCenter(), MsPlay + 3f, View3D.Pitch);
                    yield return MsSettle;
                    List<float> pt = new List<float>();
                    IEnumerator<int> sm = MsSample(first, "twin_first", pt);
                    while (sm.MoveNext())
                    {
                        yield return sm.Current;
                    }
                    pose?.Dispose();
                    float ratio = pt[0] / before;
                    MsCheck("halfspeed", Mathf.Abs(ratio - 0.5f) < 0.02f, "with a second watermill on its water the first turns at " + ratio.ToString("0.000") + " of its speed (twin at " + c + ")");
                    msMills.Remove(twin);
                    twin.Destroy();
                    first = first0;
                    yield return 5;
                }
                else
                {
                    Log.Message(MsL + "no place for a second watermill on the first one's water");
                }
            }
            // stop: broken down
            {
                CompBreakdownable br = first.TryGetComp<CompBreakdownable>();
                br.DoBreakdown();
                IEnumerator<int> tk = MsTicks(5);
                while (tk.MoveNext())
                {
                    yield return tk.Current;
                }
                IDisposable pose = MsPose(first.TrueCenter(), MsPlay, View3D.Pitch);
                yield return MsSettle;
                List<float> pt = new List<float>();
                IEnumerator<int> sm = MsSample(first, "broken", pt);
                while (sm.MoveNext())
                {
                    yield return sm.Current;
                }
                pose?.Dispose();
                MsCheck("stops_broken", Mathf.Abs(pt[0]) < 1e-5f, "broken down: game angle per tick " + pt[0].ToString("0.00000"));
                br.Notify_Repaired();
            }
            // stop: its water gone (the river cells under the wheel made dry ground; the game notices on its cache rebuild)
            if (msMills.Count > 1)
            {
                Thing dry = msMills[1];
                foreach (IntVec3 c in CompPowerPlantWater.WaterCells(dry.Position, dry.Rotation))
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                IEnumerator<int> tk = MsTicks(1300);
                while (tk.MoveNext())
                {
                    yield return tk.Current;
                }
                IDisposable pose = MsPose(dry.TrueCenter(), MsPlay, View3D.Pitch);
                yield return MsSettle;
                List<float> pt = new List<float>();
                IEnumerator<int> sm = MsSample(dry, "nowater", pt);
                while (sm.MoveNext())
                {
                    yield return sm.Current;
                }
                pose?.Dispose();
                MsCheck("stops_nowater", Mathf.Abs(pt[0]) < 1e-5f, "water gone: game angle per tick " + pt[0].ToString("0.00000"));
            }
            // 3D off: the game's own drawing
            {
                View3D.Active = false;
                yield return 60;
                IEnumerator<int> sh = MsShot("flat", first.TrueCenter(), MsPlay, View3D.Pitch);
                while (sh.MoveNext())
                {
                    yield return sh.Current;
                }
                View3D.Active = true;
                yield return 60;
            }
            // 1.65 locked walls
            {
                float was = IsoRimMod.Settings.wallHeight;
                IsoRimMod.Settings.wallHeight = 1.65f;
                yield return 120;
                IEnumerator<int> sh = MsShot("walls165", first.TrueCenter(), MsPlay, View3D.Pitch);
                while (sh.MoveNext())
                {
                    yield return sh.Current;
                }
                string rep;
                MsDrawn(first, out rep);
                Log.Message(MsL + "walls 1.65: " + rep);
                IEnumerator<int> sl = MsShot("walls165_low", MsWheel(first), MsClose + 1f, MsLowPitch);
                while (sl.MoveNext())
                {
                    yield return sl.Current;
                }
                IsoRimMod.Settings.wallHeight = 2.4f;
                yield return 120;
                IEnumerator<int> s2 = MsShot("walls240_low", MsWheel(first), MsClose + 1f, MsLowPitch);
                while (s2.MoveNext())
                {
                    yield return s2.Current;
                }
                MsDrawn(first, out rep);
                Log.Message(MsL + "walls 2.4: " + rep);
                IsoRimMod.Settings.wallHeight = was;
                yield return 120;
            }
            msLand = first.Position;
        }

        // The test-only mod's things on land near the first watermill (or the map's middle).
        private static IEnumerator<int> MsTestMod(Map map)
        {
            map.fogGrid.ClearAllFog();
            ThingDef[] defs = { ThingDef.Named("EndTable"), ThingDef.Named("Stool"), ThingDef.Named("PlantPot") };
            IntVec3 from = msLand.IsValid ? msLand : map.Center;
            IntVec3 at = IntVec3.Invalid;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(from, GenRadial.MaxRadialPatternRadius - 1f, true))
            {
                CellRect r = new CellRect(c.x, c.z, MsTestWidth, 3);
                if (r.InBounds(map) && r.Cells.All(x => x.Standable(map) && !x.GetTerrain(map).IsWater && x.GetEdifice(map) == null && x.GetFirstPawn(map) == null))
                {
                    at = c;
                    break;
                }
            }
            if (!at.IsValid)
            {
                MsCheck("testmod_place", false, "no land for the test things");
                yield break;
            }
            // a clear strip of bare soil (no grass in front of the paddles)
            foreach (IntVec3 c in new CellRect(at.x, at.z, MsTestWidth, 3).ExpandedBy(2).ClipInsideMap(map))
            {
                Plant pl = c.GetPlant(map);
                if (pl != null)
                {
                    pl.Destroy();
                }
                if (!c.GetTerrain(map).IsWater)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
            }
            List<Thing> things = new List<Thing>();
            for (int i = 0; i < defs.Length; i++)
            {
                things.Add(MsSpawn(map, defs[i], ThingDefOf.WoodLog, at + new IntVec3(MsTestAt[i], 0, 1), Rot4.North));
            }
            Vector3 mid = at.ToVector3Shifted() + new Vector3(MsTestWidth / 2f, 0f, 1.5f);
            IDisposable pose = MsPose(mid, MsPlay, View3D.Pitch);
            yield return MsSettle;
            Dictionary<Thing, float> first = new Dictionary<Thing, float>();
            foreach (Thing t in things)
            {
                string rep;
                first[t] = MsDrawn(t, out rep);
                Log.Message(MsL + "testmod " + t.def.defName + " " + rep);
            }
            for (int k = 0; k < 5; k++)   // 100 ticks: the fixed-speed wheels at 200 degrees, a paddle down and toward the camera
            {
                IEnumerator<int> tk = MsTicks(20);
                while (tk.MoveNext())
                {
                    yield return tk.Current;
                }
            }
            foreach (Thing t in things)
            {
                string rep;
                float now = MsDrawn(t, out rep);
                Log.Message(MsL + "testmod " + t.def.defName + " " + rep);
                bool turned = !float.IsNaN(now) && !float.IsNaN(first[t]) && MsDiff(now, first[t]) > 1f;
                MsCheck("testmod_turns_" + t.def.defName, turned, "spin part angle " + first[t].ToString("0.0") + " -> " + now.ToString("0.0") + " (" + rep + ")");
                if (t.def.defName == "PlantPot")
                {
                    float want = Mathf.Repeat(Find.TickManager.TicksGame * 3f, 360f);
                    MsCheck("testmod_provider", !float.IsNaN(now) && MsDiff(now, want) < 0.01f && rep.Contains("from provider"), "drawn " + now.ToString("0.000") + " provider " + want.ToString("0.000"));
                }
            }
            pose?.Dispose();
            IEnumerator<int> sh = MsShot("testmod", mid, MsClose + 1f, View3D.Pitch);
            while (sh.MoveNext())
            {
                yield return sh.Current;
            }
            // the paddles dipping below the ground, close (EndTable): the ground hides what is below it, nothing to set
            IEnumerator<int> sl = MsShot("testmod_dip", things[0].TrueCenter(), 1.6f, MsLowPitch);
            while (sl.MoveNext())
            {
                yield return sl.Current;
            }
            IEnumerator<int> sg = MsShot("testmod_dip_game", things[0].TrueCenter(), 1.6f, View3D.Pitch);
            while (sg.MoveNext())
            {
                yield return sg.Current;
            }
            Log.Message(MsL + "below-ground masks: " + AccessTools.Method(typeof(SinkHoles), "Report").Invoke(null, new object[] { map }));
        }

        // Extras for image compares (base vs head, the same seed): a wind turbine, a solar generator and a pit gate on bare
        // land near the map's middle, each shot close from the game camera (the solar generator low too). No ticks.
        private static IEnumerator<int> MsExtras(Map map)
        {
            map.fogGrid.ClearAllFog();
            const int w = 32, h = 9;
            IntVec3 at = IntVec3.Invalid;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(map.Center, GenRadial.MaxRadialPatternRadius - 1f, true))
            {
                CellRect r = new CellRect(c.x, c.z, w, h);
                if (r.InBounds(map) && r.Cells.All(x => !x.GetTerrain(map).IsWater && x.GetTerrain(map).passability != Traversability.Impassable
                    && x.GetEdifice(map) == null && x.GetFirstPawn(map) == null))
                {
                    at = c;
                    break;
                }
            }
            if (!at.IsValid)
            {
                MsCheck("extras_place", false, "no land for the extras");
                yield break;
            }
            foreach (IntVec3 c in new CellRect(at.x, at.z, w, h).ExpandedBy(2).ClipInsideMap(map))
            {
                foreach (Thing x in c.GetThingList(map).ToList())
                {
                    if (x is Plant || x.def.category == ThingCategory.Item)
                    {
                        x.Destroy();   // trees, grass, rock chunks
                    }
                }
                if (!c.GetTerrain(map).IsWater)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
            }
            string[] names = { "WindTurbine", "SolarGenerator", "PitGate" };
            int[] xs = { 4, 15, 26 };
            List<Thing> things = new List<Thing>();
            for (int i = 0; i < names.Length; i++)
            {
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(names[i]);
                if (d == null)
                {
                    Log.Message(MsL + "extras: no " + names[i]);
                    things.Add(null);
                    continue;
                }
                Thing t = ThingMaker.MakeThing(d, d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null);
                if (d.CanHaveFaction)
                {
                    t.SetFaction(Faction.OfPlayer);
                }
                things.Add(GenSpawn.Spawn(t, at + new IntVec3(xs[i], 0, h / 2), map, Rot4.North));
                Log.Message(MsL + "extras: " + names[i] + " at " + things[i].Position);
            }
            yield return 30;
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] == null)
                {
                    continue;
                }
                string tag = names[i].ToLowerInvariant();
                IEnumerator<int> sh = MsShot("extra_" + tag, things[i].TrueCenter(), MsClose + 1f, View3D.Pitch);
                while (sh.MoveNext())
                {
                    yield return sh.Current;
                }
                if (names[i] == "SolarGenerator")
                {
                    IEnumerator<int> sl = MsShot("extra_" + tag + "_low", things[i].TrueCenter(), MsClose, MsLowPitch);
                    while (sl.MoveNext())
                    {
                        yield return sl.Current;
                    }
                }
            }
            Log.Message(MsL + "extras done; sink holes: " + AccessTools.Method(typeof(SinkHoles), "Report").Invoke(null, new object[] { map }));
        }
    }
}
