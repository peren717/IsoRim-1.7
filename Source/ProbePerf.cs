using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only keys for performance runs (see Probe; measure with perf=N,label in the same pass):
    //   window=<seconds>                      how long each pass's own change is measured (default 1.5)
    //   campath=x0,z0,s0,x1,z1,s1,frames      the camera glides from one place and root size to the other
    //   spin=frames,every                     the camera turns an eighth every that many frames (Q/E held)
    //   tilt=p0,p1,frames                     the isometric pitch swept from p0 to p1 (tilting with the mouse)
    //   walls=x0,z0,x1,z1[,<Stuff>]           walls built at once over a rectangle (its outline)
    //   clear=x0,z0,x1,z1                     every building in a rectangle destroyed (deconstructed at once)
    //   save=<name>                           the game saved (temp file in the save folder: delete it after the run)
    //   loadtest=<name>                       that save loaded with 3D on, then off, then the game quits (see LoadWatch)
    //   cull=0|1                              culling (Cull) off or on from now on
    //   cullshrink=f                          culling against a screen f times the real one (f < 1: a negative control)
    //   cullcheck=0|1                         pawns and doors culled whole are drawn anyway; their model parts that show are counted
    //   batch=0|1                             instanced drawing of moving model parts (PartBatches) off or on from now on
    //   abswitch=cull|batch                   what cullab turns off and on (default cull)
    //   cullab=x,z,size,tag                   camera held there; three screenshots 5 frames apart: culling off, on, off
    //                                         (renders<harness tag>cullab_<tag>_off1/_on/_off2.png)
    //   speed=0..4                            the game speed (0 paused, 1 normal, 2 fast, 3 superfast) held from now on,
    //                                         against the harness's pauses
    //   -isorimbiome=<BiomeDef> (command line) the quick-test map on a flat tile of that biome (after Capture17's pick);
    //                                         with -isorimhills=<Hilliness> (e.g. Mountainous) a tile that hilly
    public static partial class Probe
    {
        private static int heldSpeed = -1;

        // -isorimbiome=<BiomeDef>: patched in at start-up (harness only: nobody else passes it).
        public static void BiomeHook(HarmonyLib.Harmony h)
        {
            string biome;
            if (!GenCommandLine.TryGetCommandLineArg("isorimbiome", out biome))
            {
                return;
            }
            h.Patch(HarmonyLib.AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay)),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(BiomePrefix)),
                postfix: new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(BiomePostfix)) { priority = HarmonyLib.Priority.Last });
        }

        // -isorimseed=N with it: the same world, tile and map every run (the quick test's world seed is drawn from Rand).
        public static void BiomePrefix()
        {
            string seed;
            if (GenCommandLine.TryGetCommandLineArg("isorimseed", out seed))
            {
                Rand.Seed = int.Parse(seed);
            }
        }

        public static void BiomePostfix()
        {
            string name;
            GenCommandLine.TryGetCommandLineArg("isorimbiome", out name);
            BiomeDef biome = DefDatabase<BiomeDef>.GetNamedSilentFail(name);
            if (biome == null)
            {
                Log.Warning("[IsoRim probe] no biome " + name);
                return;
            }
            // -isorimhills=<Hilliness> with it: a tile that hilly instead of flat (Mountainous: a mountain map, fogged inside).
            string hillsName;
            RimWorld.Planet.Hilliness hills = RimWorld.Planet.Hilliness.Flat;
            if (GenCommandLine.TryGetCommandLineArg("isorimhills", out hillsName))
            {
                hills = (RimWorld.Planet.Hilliness)Enum.Parse(typeof(RimWorld.Planet.Hilliness), hillsName);
            }
            foreach (RimWorld.Planet.SurfaceTile t in Find.WorldGrid.Tiles.InRandomOrder())
            {
                if (t.PrimaryBiome == biome && t.hilliness == hills && RimWorld.Planet.TileFinder.IsValidTileForNewSettlement(t.tile))
                {
                    Find.GameInitData.startingTile = t.tile;
                    Log.Message("[IsoRim probe] quick-test tile " + t.tile + " (" + biome.defName + ", " + hills + ")");
                    return;
                }
            }
            Log.Warning("[IsoRim probe] no " + hills + " " + name + " tile");
        }
        private static Vector3 camFrom, camTo;
        private static int camFrames, camFrame = -1;
        private static int spinLeft, spinEvery, spinFrame;
        private static float tiltFrom, tiltTo;
        private static int tiltFrames, tiltFrame = -1;

        private static void PerfKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "window":
                    Window = F(a[0]);
                    break;
                case "cull":
                    Cull.Enabled = a[0] == "1";
                    break;
                case "batch":
                    PartBatches.Enabled = a[0] == "1";
                    break;
                case "abswitch":
                    abSwitch = a[0];
                    break;
                case "speed":
                    heldSpeed = I(a[0]);
                    break;
                case "cullcheck":
                    Cull.checkThings = a[0] == "1";
                    break;
                case "cullshrink":
                    Cull.Shrink = F(a[0]);
                    break;
                case "cullab":
                    abQueue.Add(new KeyValuePair<Vector3, string>(new Vector3(F(a[0]), F(a[1]), F(a[2])), a[3]));
                    if (abFrame < 0)
                    {
                        abFrame = -20;   // waits for a turn to end, then 20 frames
                    }
                    break;
                case "campath":
                    camFrom = new Vector3(F(a[0]), F(a[1]), F(a[2]));
                    camTo = new Vector3(F(a[3]), F(a[4]), F(a[5]));
                    camFrames = Math.Max(1, I(a[6]));
                    camFrame = 0;
                    break;
                case "spin":
                    spinLeft = I(a[0]);
                    spinEvery = Math.Max(1, I(a[1]));
                    spinFrame = 0;
                    break;
                case "tilt":
                    tiltFrom = F(a[0]);
                    tiltTo = F(a[1]);
                    tiltFrames = Math.Max(1, I(a[2]));
                    tiltFrame = 0;
                    break;
                case "walls":
                {
                    ThingDef stuff = DefDatabase<ThingDef>.GetNamed(a.Length > 4 ? a[4] : "BlocksGranite");
                    CellRect r = Rect(a, 0);
                    int n = 0;
                    foreach (IntVec3 c in r.EdgeCells)
                    {
                        if (c.InBounds(map) && c.GetEdifice(map) == null)
                        {
                            Thing w = ThingMaker.MakeThing(ThingDefOf.Wall, stuff);
                            w.SetFaction(Faction.OfPlayer);
                            GenSpawn.Spawn(w, c, map, Rot4.North);
                            n++;
                        }
                    }
                    Log.Message("[IsoRim probe] walls: " + n + " built");
                    break;
                }
                case "save":
                {
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    GameDataSaveLoader.SaveGame(a[0]);
                    Log.Message("[IsoRim probe] saved " + a[0] + " in " + sw.ElapsedMilliseconds + " ms");
                    break;
                }
                case "loadtest":
                    loadName = a[0];
                    loadSeq = 0;
                    StartLoad(true);
                    break;
                case "clear":
                {
                    List<Thing> gone = new List<Thing>();
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        if (c.InBounds(map))
                        {
                            gone.AddRange(c.GetThingList(map).Where(t => t is Building && t.def.destroyable));
                        }
                    }
                    foreach (Thing t in gone.Distinct().ToList())
                    {
                        if (!t.Destroyed)
                        {
                            t.Destroy(DestroyMode.Deconstruct);
                        }
                    }
                    Log.Message("[IsoRim probe] clear: " + gone.Distinct().Count() + " buildings destroyed");
                    break;
                }
            }
        }

        private static readonly List<KeyValuePair<Vector3, string>> abQueue = new List<KeyValuePair<Vector3, string>>();
        private static int abFrame = -1;
        private static string abSwitch = "cull";   // what cullab turns off and on: "cull" (Cull) or "batch" (PartBatches)

        private static void CullAbTick()
        {
            if (abQueue.Count == 0)
            {
                return;
            }
            Vector3 abCam = abQueue[0].Key;
            string abTag = abQueue[0].Value;
            Find.CameraDriver.SetRootPosAndSize(new Vector3(abCam.x, 0f, abCam.y), abCam.z);
            if (abFrame < 0)
            {
                if (!View3D.Turning)
                {
                    abFrame++;
                }
                return;
            }
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                Find.WindowStack.TryRemove(w, doCloseSound: false);
            }
            bool abOn = abFrame >= 5 && abFrame < 10;
            if (abSwitch == "batch")
            {
                PartBatches.Enabled = abOn;
            }
            else
            {
                Cull.Enabled = abOn;
            }
            if (abFrame == 2 || abFrame == 7 || abFrame == 12)
            {
                string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
                System.IO.Directory.CreateDirectory(dir);
                string name = "cullab_" + abTag + (abFrame == 2 ? "_off1" : abFrame == 7 ? "_on" : "_off2");
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
                Log.Message("[IsoRim probe] " + name + ": yaw " + View3D.Yaw.ToString("0") + ", pitch " + View3D.Pitch.ToString("0.0") + ", cull " + Cull.Enabled + ", batch " + PartBatches.Enabled
                    + ", sections tested " + Cull.sectionsTested + " culled " + Cull.sectionsCulled + " unknown " + Cull.sectionsUnknown + ", model parts culled " + Cull.culledParts);
            }
            if (++abFrame >= 15)
            {
                abQueue.RemoveAt(0);
                abFrame = abQueue.Count > 0 ? -5 : -1;
                Cull.Enabled = true;
                PartBatches.Enabled = true;
            }
        }

        // Every frame (from Sticky).
        private static void MotionTick()
        {
            if (heldSpeed >= 0 && Find.TickManager != null)
            {
                Find.TickManager.CurTimeSpeed = (TimeSpeed)heldSpeed;   // held against the harness's pauses
            }
            CullAbTick();
            if (camFrame >= 0)
            {
                float f = camFrame / (float)camFrames;
                Vector3 p = Vector3.Lerp(camFrom, camTo, f);
                Find.CameraDriver.SetRootPosAndSize(new Vector3(p.x, 0f, p.y), p.z);
                if (++camFrame > camFrames)
                {
                    camFrame = -1;
                }
            }
            if (spinLeft > 0)
            {
                if (spinFrame++ % spinEvery == 0)
                {
                    IsoRimMod.Settings.turn++;
                }
                spinLeft--;
            }
            if (tiltFrame >= 0)
            {
                IsoRimMod.Settings.isoPitch = Mathf.Lerp(tiltFrom, tiltTo, tiltFrame / (float)tiltFrames);
                if (++tiltFrame > tiltFrames)
                {
                    tiltFrame = -1;
                }
            }
        }

        private static bool loadLogged;

        // Once, at the first frame of play: when the map was ready (map load time = this minus the log's
        // "Initializing new game" stamp) and the memory then.
        private static void LogLoaded()
        {
            LoadWatch();
            TubeReloadTick();
            BgReloadTick();
            if (loadLogged || Find.CurrentMap == null)
            {
                return;
            }
            loadLogged = true;
            System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess();
            Log.Message("[IsoRim probe] first frame of play at " + Time.realtimeSinceStartup.ToString("0.00") + " s, " + DateTime.Now.ToString("HH:mm:ss.fff")
                + ", map " + Find.CurrentMap.Size + ", 3D " + View3D.Active + ", things " + Find.CurrentMap.listerThings.AllThings.Count
                + ", pawns " + Find.CurrentMap.mapPawns.AllPawnsSpawnedCount + ", managed " + (GC.GetTotalMemory(false) / 1048576.0).ToString("0")
                + " MB, working set " + (proc.WorkingSet64 / 1048576.0).ToString("0") + " MB");
        }

        // loadtest=<save>: the save loaded with the 3D view on, then again with it off, then the game quits
        // (the harness would otherwise rebuild its scene over the loaded map). Logged per load: seconds
        // to the first frame of play, the slowest of the next 120 frames and their total, memory.
        private static string loadName;
        private static int loadSeq = -1;
        private static Game loadGame;
        private static float loadStart;
        private static float loadReady;
        private static int loadFrames = -1;
        private static float loadMax;
        private static float loadSum;

        private static void StartLoad(bool on)
        {
            // The harness would rebuild its scene over the loaded map: switched off for the rest of the run.
            System.Reflection.FieldInfo cap = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("Capture17.Main"), "Active");
            if (cap != null)
            {
                cap.SetValue(null, false);
            }
            View3D.Active = on;
            loadGame = Current.Game;
            loadStart = Time.realtimeSinceStartup;
            loadFrames = -1;
            Log.Message("[IsoRim probe] loading " + loadName + " with 3D " + on);
            GameDataSaveLoader.LoadGame(loadName);
        }

        private static void LoadWatch()
        {
            if (loadSeq < 0 || Current.Game == loadGame || LongEventHandler.ShouldWaitForEvent || Find.CurrentMap == null
                || Find.CurrentMap.listerThings.AllThings.Count == 0)
            {
                return;
            }
            if (loadFrames < 0)
            {
                loadReady = Time.realtimeSinceStartup;
                loadFrames = 0;
                loadMax = 0f;
                loadSum = 0f;
                return;
            }
            float dt = Time.unscaledDeltaTime * 1000f;
            loadMax = Mathf.Max(loadMax, dt);
            loadSum += dt;
            if (++loadFrames < 120)
            {
                return;
            }
            System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess();
            Log.Message("[IsoRim probe] load (3D " + View3D.Active + "): " + (loadReady - loadStart).ToString("0.00") + " s to the first frame of play; next 120 frames "
                + (loadSum / 1000f).ToString("0.00") + " s, slowest " + loadMax.ToString("0") + " ms; things " + Find.CurrentMap.listerThings.AllThings.Count
                + ", managed " + (GC.GetTotalMemory(false) / 1048576.0).ToString("0") + " MB, working set " + (proc.WorkingSet64 / 1048576.0).ToString("0")
                + " MB, private " + (proc.PrivateMemorySize64 / 1048576.0).ToString("0") + " MB");
            if (loadSeq == 0)
            {
                loadSeq = 1;
                StartLoad(false);
            }
            else
            {
                loadSeq = -1;
                Log.Message("[IsoRim probe] loadtest done, quitting");
                Root.Shutdown();
            }
        }

        // ---- render side of a perf window (Waylith): draw commands submitted per frame (all, by the map
        // mesh, by the dynamic things), main-thread CaptureFrame (one draw item per command), and on the
        // render thread RecordFrame (Vulkan recording) and the whole RenderFrame (+ acquire/submit waits).
        private static System.Collections.IList drawCommands;
        private static long capStart, rtStart, recStart;
        private static double capMs, rtSum, recSum;
        private static int rtCount;
        private static int cmdAtMesh, cmdAtDyn, cmdMesh, cmdDyn, cmdTotal;
        private static readonly List<float> perfCap = new List<float>();
        private static readonly List<float> perfCmd = new List<float>();
        private static readonly List<float> perfCmdMesh = new List<float>();
        private static readonly List<float> perfCmdDyn = new List<float>();
        private static bool histNext;
        private static string hist = "";

        private static void RenderHooks(HarmonyLib.Harmony h)
        {
            drawCommands = HarmonyLib.AccessTools.Field(typeof(Waylith.Rendering.MeshDrawSubmission), "Commands").GetValue(null) as System.Collections.IList;
            h.Patch(HarmonyLib.AccessTools.Method(typeof(Waylith.Rendering.FrameRenderer), "CaptureFrame"),
                new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(CapBegin)), new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(CapEnd)));
            h.Patch(HarmonyLib.AccessTools.Method(typeof(Waylith.Rendering.RenderThread), "RenderFrame"),
                new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(RtBegin)), new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(RtEnd)));
            h.Patch(HarmonyLib.AccessTools.Method(typeof(Waylith.Rendering.FrameRenderer), "RecordFrame"),
                new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(RecBegin)), new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(RecEnd)));
            h.Patch(HarmonyLib.AccessTools.DeclaredMethod(typeof(MapDrawLayer), "DrawLayer"),
                new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(CatLayerBegin)), new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(CatLayerEnd)));
            h.Patch(HarmonyLib.AccessTools.Method(typeof(PartBatches), nameof(PartBatches.Flush)),
                new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(FlushCmdBegin)), new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(FlushCmdEnd)));
            h.Patch(HarmonyLib.AccessTools.Method(typeof(Thing), "DynamicDrawPhase"),
                new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(CatThingBegin)), new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(CatThingEnd)));
            // Map block rebuilds: every layer's own Regenerate, timed by layer type.
            foreach (Type t in typeof(SectionLayer).AllSubclasses())   // (abstract ones too: SectionLayer_Things holds ThingsGeneral's)
            {
                MethodInfo m = HarmonyLib.AccessTools.DeclaredMethod(t, "Regenerate");
                if (m != null && !m.IsAbstract)
                {
                    try
                    {
                        h.Patch(m, new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(RegenBegin)), new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(RegenEnd)));
                    }
                    catch (Exception e)
                    {
                        Log.Warning("[IsoRim probe] regen hook " + t.Name + ": " + e.Message);
                    }
                }
            }
            h.Patch(HarmonyLib.AccessTools.Method(typeof(MapDrawer), "MapMeshDrawerUpdate_First"),
                new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(MeshUpdBegin)), new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(MeshUpdEnd)));
        }

        // ---- map block rebuilds in a perf window: per layer type count and ms, the game's per-frame update of
        // dirty blocks (MapMeshDrawerUpdate_First) and gen-0 collections.
        private static readonly Dictionary<string, double[]> regen = new Dictionary<string, double[]>();
        private static readonly Stack<long> regenStarts = new Stack<long>();
        private static long meshUpdStart;
        private static double meshUpdMs, meshUpdMax;
        private static readonly List<float> perfMeshUpd = new List<float>();
        private static int gcAtStart;

        public static void RegenBegin() { regenStarts.Push(System.Diagnostics.Stopwatch.GetTimestamp()); }

        public static void RegenEnd(SectionLayer __instance)
        {
            if (regenStarts.Count == 0)
            {
                return;
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - regenStarts.Pop()) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (perfLeft <= 0)
            {
                return;
            }
            string k = __instance.GetType().Name.Replace("SectionLayer_", "");
            double[] v;
            if (!regen.TryGetValue(k, out v))
            {
                v = new double[3];
                regen[k] = v;
            }
            v[0]++;
            v[1] += ms;
            v[2] = Math.Max(v[2], ms);
        }

        public static void MeshUpdBegin() { meshUpdStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static void MeshUpdEnd() { meshUpdMs += (System.Diagnostics.Stopwatch.GetTimestamp() - meshUpdStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

        private static string RegenReport()
        {
            List<float> u = new List<float>(perfMeshUpd);
            u.Sort();
            int n = u.Count;
            string s = "block rebuilds: MapMeshDrawerUpdate_First median " + Median(perfMeshUpd).ToString("0.000") + " p95 " + (n > 0 ? u[Mathf.Min(n - 1, (int)(n * 0.95f))] : 0f).ToString("0.000")
                + " max " + (n > 0 ? u[n - 1] : 0f).ToString("0.000") + " ms, gen0 GCs " + (GC.CollectionCount(0) - gcAtStart) + "; layers (count/total ms/max ms): "
                + string.Join(", ", regen.OrderByDescending(kv => kv.Value[1]).Take(10).Select(kv => kv.Key + " " + kv.Value[0] + "/" + kv.Value[1].ToString("0.0") + "/" + kv.Value[2].ToString("0.0")));
            return s;
        }

        private static int CmdCount
        {
            get { return drawCommands != null ? drawCommands.Count : 0; }
        }

        public static void MeshCmdBegin() { cmdAtMesh = CmdCount; CatMark("map:other"); }
        public static void MeshCmdEnd() { cmdMesh += CmdCount - cmdAtMesh; CatMark("other"); }
        public static void DynCmdBegin() { cmdAtDyn = CmdCount; CatMark("dyn:other"); }
        public static void DynCmdEnd() { cmdDyn += CmdCount - cmdAtDyn; CatMark("other"); }

        // PartBatches.Flush runs in DrawDynamicThings' finalizer, after the postfix above: counted with the dynamic things.
        private static int cmdAtFlush;
        public static void FlushCmdBegin() { cmdAtFlush = CmdCount; CatMark("dyn:batched"); }
        public static void FlushCmdEnd() { cmdDyn += CmdCount - cmdAtFlush; CatMark("other"); }

        // ---- draw commands by who submitted them (harness): markers (command index, category) of the
        // frame, read with the command list by Histogram.
        private static readonly List<int> catIdx = new List<int>();
        private static readonly List<string> catName = new List<string>();
        private static readonly Dictionary<string, int> catThings = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> catThingsFrame = new Dictionary<string, int>();
        private static int catThingAt;
        private static string catThing;

        private static void CatMark(string cat)
        {
            catIdx.Add(CmdCount);
            catName.Add(cat);
        }

        public static void CatLayerBegin(MapDrawLayer __instance) { CatMark("map:" + __instance.GetType().Name.Replace("SectionLayer_", "")); }
        public static void CatLayerEnd() { CatMark("map:other"); }

        public static void CatThingBegin(Thing __instance, DrawPhase phase)
        {
            if (phase != DrawPhase.Draw)
            {
                return;
            }
            catThing = ThingCat(__instance);
            catThingAt = CmdCount;
            CatMark("dyn:" + catThing);
        }

        public static void CatThingEnd(DrawPhase phase)
        {
            if (phase != DrawPhase.Draw || catThing == null)
            {
                return;
            }
            if (CmdCount > catThingAt)
            {
                int n;
                catThingsFrame.TryGetValue(catThing, out n);
                catThingsFrame[catThing] = n + 1;
            }
            catThing = null;
            CatMark("dyn:other");
        }

        private static string ThingCat(Thing t)
        {
            Pawn p = t as Pawn;
            if (p != null)
            {
                return p.RaceProps == null ? "pawn" : p.RaceProps.Humanlike ? "humanlike" : p.RaceProps.Animal ? "animal" : p.RaceProps.IsMechanoid ? "mech" : "pawn";
            }
            if (t is Building_Door) return "door";
            if (t is Building_Turret) return "turret";
            if (t is Corpse) return "corpse";
            if (t is Plant) return "plant";
            if (t is Building) return "building";
            return t.def.category.ToString().ToLowerInvariant();
        }

        private static void CatClear()
        {
            catIdx.Clear();
            catName.Clear();
            catThingsFrame.Clear();
        }

        public static void CapBegin()
        {
            cmdTotal = CmdCount;
            Camera cam = Current.CameraDriver != null ? Current.CameraDriver.MyCamera : null;
            if (cam != null && View3D.Ready && !Cull.SameCamera(cam.projectionMatrix * cam.worldToCameraMatrix))
            {
                camMismatch++;
            }
            if (histNext)
            {
                histNext = false;
                hist = Histogram();
            }
            capStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public static void CapEnd() { capMs += (System.Diagnostics.Stopwatch.GetTimestamp() - capStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; CatClear(); }
        public static void RtBegin() { rtStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static void RtEnd() { rtSum += (System.Diagnostics.Stopwatch.GetTimestamp() - rtStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; rtCount++; }
        public static void RecBegin() { recStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static void RecEnd() { recSum += (System.Diagnostics.Stopwatch.GetTimestamp() - recStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

        private static void RenderPerfStart()
        {
            perfCap.Clear();
            perfCmd.Clear();
            perfCmdMesh.Clear();
            perfCmdDyn.Clear();
            rtSum = recSum = 0;
            rtCount = 0;
            regen.Clear();
            perfMeshUpd.Clear();
            meshUpdMs = 0;
            gcAtStart = GC.CollectionCount(0);
            hist = "";
            cullTested = cullCulled = cullUnknown = cullParts = cullThings = cullViol = camMismatch = 0;
        }

        // Once per frame of a perf window (from PerfFrame).
        private static int cullTested, cullCulled, cullUnknown, cullParts, cullThings, cullViol, camMismatch;

        private static void CullCountsReset()
        {
            Cull.sectionsTested = Cull.sectionsCulled = Cull.sectionsUnknown = Cull.culledParts = 0;
            Cull.thingsCulled = Cull.thingViolations = 0;
        }

        private static void RenderPerfFrame(int left)
        {
            cullTested += Cull.sectionsTested;
            cullCulled += Cull.sectionsCulled;
            cullUnknown += Cull.sectionsUnknown;
            cullParts += Cull.culledParts;
            cullThings += Cull.thingsCulled;
            cullViol += Cull.thingViolations;
            CullCountsReset();
            perfCap.Add((float)capMs);
            perfCmd.Add(cmdTotal);
            perfCmdMesh.Add(cmdMesh);
            perfCmdDyn.Add(cmdDyn);
            perfMeshUpd.Add((float)meshUpdMs);
            meshUpdMs = 0;
            capMs = 0;
            cmdMesh = cmdDyn = 0;
            if (left == 20)
            {
                histNext = true;
            }
        }

        private static void RenderPerfIdle()
        {
            CullCountsReset();
            meshUpdMs = 0;
            capMs = 0;
            cmdMesh = cmdDyn = 0;
        }

        private static string RenderPerfReport()
        {
            string s = "render: commands median " + Median(perfCmd).ToString("0") + " (map mesh " + Median(perfCmdMesh).ToString("0") + ", dynamic things "
                + Median(perfCmdDyn).ToString("0") + "), CaptureFrame median " + Median(perfCap).ToString("0.000") + " ms; render thread RenderFrame mean "
                + (rtCount > 0 ? rtSum / rtCount : 0).ToString("0.000") + " ms (RecordFrame " + (rtCount > 0 ? recSum / rtCount : 0).ToString("0.000") + ") over " + rtCount + " frames"
                + "; batch " + PartBatches.Enabled + "; cull " + Cull.Enabled + " per frame: sections tested " + (cullTested / (float)Math.Max(1, perfCmd.Count)).ToString("0.0")
                + ", culled " + (cullCulled / (float)Math.Max(1, perfCmd.Count)).ToString("0.0") + ", unknown " + (cullUnknown / (float)Math.Max(1, perfCmd.Count)).ToString("0.0")
                + ", model parts culled " + (cullParts / (float)Math.Max(1, perfCmd.Count)).ToString("0")
                + ", frames rendered with another camera than culled against " + camMismatch
                + ", things culled " + (cullThings / (float)Math.Max(1, perfCmd.Count)).ToString("0.0") + (Cull.checkThings ? ", CHECK: visible parts of culled things " + cullViol : "")
                + "; " + RegenReport();
            if (hist.Length > 0)
            {
                Log.Message("[IsoRim probe] perf " + perfLabel + " draw commands by shader (one frame): " + hist);
            }
            return s;
        }


        // Draw commands of the current frame grouped by shader: count and triangles.
        private static string Histogram()
        {
            if (drawCommands == null || drawCommands.Count == 0)
            {
                return "";
            }
            PropertyInfo cmdProp = null;
            Dictionary<string, int[]> by = new Dictionary<string, int[]>();
            Dictionary<string, int> byCat = new Dictionary<string, int>();
            int idx = -1, mk = 0;
            string cat = "other";
            foreach (object o in drawCommands)
            {
                idx++;
                while (mk < catIdx.Count && catIdx[mk] <= idx)
                {
                    cat = catName[mk++];
                }
                if (cmdProp == null)
                {
                    cmdProp = o.GetType().GetProperty("Command");
                }
                Waylith.Rendering.MeshDrawCommand c = (Waylith.Rendering.MeshDrawCommand)cmdProp.GetValue(o);
                string key = cat + "|" + (c.Material != null && c.Material.Shader != null ? c.Material.Shader.Name : "?");
                int cn;
                byCat.TryGetValue(cat, out cn);
                byCat[cat] = cn + 1;
                int[] v;
                if (!by.TryGetValue(key, out v))
                {
                    v = new int[2];
                    by[key] = v;
                }
                v[0]++;
                v[1] += c.Mesh != null ? c.Mesh.IndexCount / 3 : 0;
            }
            return "BY CATEGORY: " + string.Join(", ", byCat.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value
                + (catThingsFrame.ContainsKey(kv.Key.Replace("dyn:", "")) ? " (" + catThingsFrame[kv.Key.Replace("dyn:", "")] + " things)" : "")))
                + " || BY CATEGORY AND SHADER: " + string.Join(", ", by.OrderByDescending(kv => kv.Value[0]).Select(kv => kv.Key + " " + kv.Value[0] + "/" + (kv.Value[1] / 1000) + "k tris"));
        }
    }
}
