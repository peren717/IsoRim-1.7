using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only test of Api version 17 (complete-wall requests, WallCutaway.cs) and of first person keeping walls whole.
    // Keys:
    //   fwfp=x,z | fwfp=off          the colonist in that cell is selected and the first-person key's action runs
    //                                (FirstPerson.Toggle), or it runs again to leave
    //   fwreq=name[,x,z]             Api.RequestFullWalls on the current map (following the pawn in that cell), kept as name
    //   fwrel=name                   that handle disposed
    //   fwcheck=label,frames,expect  after that many frames: CHECK that complete walls are (expect 1) or are not (0) in force,
    //                                and that no colonist wall is cut (1) or some are (0)
    //   fwsig=label                  logs a signature of every IsoRim setting (they must never change)
    //   fwdrain=label                frames until the wall rebuild queued by the last mode change has drained
    //   fwpawn=x,z                   a colonist (fixed seed) spawned in that cell (U/V coordinates: upper floors)
    //   fwring=x0,z0,x1,z1           granite walls round that rect, a wooden door at its south middle
    //   fwapi=x,z                    scripted Api checks (two requesters in both orders, overlap with first person on the colonist
    //                                in that cell, a followed pawn that dies, one that despawns, bad arguments)
    //   fwmapseq=x,z                 first person + a request, then a new map is made current and the home map again
    //   fwgame=x,z                   first person + a request, then the game is saved and loaded, then (first person again)
    //                                back to the main menu; checks in each state, then done.txt and quit
    public static partial class Probe
    {
        private static bool fwPatched;
        private static readonly Dictionary<string, IDisposable> fwHandles = new Dictionary<string, IDisposable>();
        private static readonly List<(int at, Action act)> fwTimed = new List<(int, Action)>();
        private static int fwDrainStart = -1, fwDrainSeen, fwDrainView = -1;
        private static string fwDrainLabel;
        private static float fwDrainMaxDt;
        private static IEnumerator<int> fwScript;
        private static int fwScriptWait;
        private static string fwSigStart;

        private static bool Api17Key(string key, string[] a, Map map)
        {
            if (!key.StartsWith("fw"))
            {
                return false;
            }
            Api17Patch();
            switch (key)
            {
                case "fwfp":
                    if (a[0] == "off")
                    {
                        if (FirstPerson.On)
                        {
                            FirstPerson.Toggle();
                        }
                    }
                    else
                    {
                        FwEnterFirstPerson(map, new IntVec3(I(a[0]), 0, I(a[1])));
                    }
                    Log.Message("[IsoRim probe] fw first person " + (FirstPerson.On ? "on with " + FirstPerson.Pawn.LabelShort : "off"));
                    return true;
                case "fwreq":
                {
                    Pawn p = a.Length > 2 ? new IntVec3(I(a[1]), 0, I(a[2])).GetFirstPawn(map) : null;
                    fwHandles[a[0]] = Api.RequestFullWalls(map, p);
                    Log.Message("[IsoRim probe] fw request " + a[0] + (p != null ? " following " + p.LabelShort : ""));
                    return true;
                }
                case "fwrel":
                    if (fwHandles.TryGetValue(a[0], out IDisposable h))
                    {
                        h.Dispose();
                        fwHandles.Remove(a[0]);
                    }
                    return true;
                case "fwcheck":
                {
                    string label = a[0];
                    bool expect = a[2] == "1";
                    fwTimed.Add((Time.frameCount + I(a[1]), () => FwCheckState(label, expect)));
                    return true;
                }
                case "fwsig":
                    Log.Message("[IsoRim probe] fw settings " + a[0] + ": " + FwSettingsSig());
                    return true;
                case "fwdrain":
                    fwDrainLabel = a[0];
                    fwDrainStart = Time.frameCount;
                    fwDrainSeen = FwPendingWallJobs(map);
                    fwDrainMaxDt = 0f;
                    fwDrainView = -1;
                    return true;
                case "fwpawn":
                {
                    Pawn p = FwTempPawn(map, new IntVec3(I(a[0]), 0, I(a[1])), 1719);
                    Log.Message("[IsoRim probe] fw pawn " + p.LabelShort + " at " + p.Position);
                    return true;
                }
                case "fwring":
                {
                    // granite walls round the rect x0,z0,x1,z1 (U/V coordinates for upper floors), a wooden door at its south middle
                    CellRect r = CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3]));
                    int n = 0;
                    foreach (IntVec3 c in r.EdgeCells)
                    {
                        bool door = c.z == r.minZ && c.x == r.CenterCell.x;
                        Thing t = ThingMaker.MakeThing(door ? ThingDefOf.Door : ThingDefOf.Wall, door ? ThingDefOf.WoodLog : ThingDefOf.BlocksGranite);
                        t.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(t, c, map, Rot4.North);
                        n++;
                    }
                    Log.Message("[IsoRim probe] fw ring " + r + ": " + n + " walls");
                    return true;
                }
                case "fwapi":
                    FwStart(FwApiScript(new IntVec3(I(a[0]), 0, I(a[1]))));
                    return true;
                case "fwmapseq":
                    FwStart(FwMapScript(new IntVec3(I(a[0]), 0, I(a[1]))));
                    return true;
                case "fwgame":
                    FwStart(FwGameScript(new IntVec3(I(a[0]), 0, I(a[1]))));
                    return true;
            }
            return false;
        }

        private static void Api17Patch()
        {
            if (fwPatched)
            {
                return;
            }
            fwPatched = true;
            new Harmony("peren.isorim.probe.api17").Patch(AccessTools.Method(typeof(Root), "Update"), postfix: new HarmonyMethod(typeof(Probe), nameof(Api17Frame)));
        }

        private static void FwStart(IEnumerator<int> script)
        {
            fwSigStart = FwSettingsSig();
            fwScript = script;
            fwScriptWait = Time.frameCount;
        }

        // Every frame (Root.Update, also in the main menu): timed checks, the drain counter and the scripts.
        public static void Api17Frame()
        {
            try
            {
                for (int i = fwTimed.Count - 1; i >= 0; i--)
                {
                    if (Time.frameCount >= fwTimed[i].at)
                    {
                        Action act = fwTimed[i].act;
                        fwTimed.RemoveAt(i);
                        act();
                    }
                }
                if (fwDrainStart >= 0 && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
                {
                    int frames = Time.frameCount - fwDrainStart;
                    fwDrainMaxDt = Mathf.Max(fwDrainMaxDt, Time.unscaledDeltaTime * 1000f);
                    int pending = FwPendingWallJobs(Find.CurrentMap, out int inView);
                    fwDrainSeen = Math.Max(fwDrainSeen, pending);
                    if (inView == 0 && fwDrainView < 0 && frames >= 2)
                    {
                        fwDrainView = frames;
                    }
                    if (frames >= 2 && pending == 0 || frames > 600)
                    {
                        Log.Message("[IsoRim probe] fw drain " + fwDrainLabel + ": wall blocks in view rebuilt after " + fwDrainView + " frames, all after " + frames + " frames (most queued "
                            + fwDrainSeen + " wall jobs), slowest frame " + fwDrainMaxDt.ToString("0.0") + " ms, textured walls " + IsoRimMod.Settings.texturedWalls
                            + ", complete walls " + FullWalls.Required(Find.CurrentMap) + ", cut walls now " + FwCutCount(Find.CurrentMap));
                        fwDrainStart = -1;
                    }
                }
                if (fwScript != null && Time.frameCount >= fwScriptWait)
                {
                    if (fwScript.MoveNext())
                    {
                        fwScriptWait = Time.frameCount + Math.Max(1, fwScript.Current);
                    }
                    else
                    {
                        fwScript = null;
                    }
                }
            }
            catch (Exception e)
            {
                fwScript = null;
                Check("fw script", false, "exception " + e);
            }
        }

        private static void FwEnterFirstPerson(Map map, IntVec3 c)
        {
            Pawn p = c.GetFirstPawn(map);
            if (p == null)
            {
                Log.Warning("[IsoRim probe] fwfp: no pawn at " + c);
                return;
            }
            if (FirstPerson.On)
            {
                FirstPerson.Toggle();
            }
            Find.Selector.ClearSelection();
            Find.Selector.Select(p, false, false);
            FirstPerson.Toggle();
        }

        // Colonist walls and doors that could be cut, and how many of them are cut now.
        private static int FwCutCount(Map map)
        {
            int n = 0;
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (b.Spawned && WallBoxes.CanBeCut(b.def) && !(b.def.building != null && b.def.building.isNaturalRock) && WallBoxes.CellCut(map, b.Position))
                {
                    n++;
                }
            }
            return n;
        }

        private static int FwRequestCount()
        {
            IList list = AccessTools.Field(typeof(FullWalls), "requests").GetValue(null) as IList;
            return list != null ? list.Count : -1;
        }

        private static string FwState(Map map)
        {
            Map shown = AccessTools.Field(typeof(FullWalls), "shownMap").GetValue(null) as Map;
            bool required = (bool)AccessTools.Field(typeof(FullWalls), "required").GetValue(null);
            return "first person " + FirstPerson.On + ", requests " + FwRequestCount() + ", shownMap " + (shown != null ? shown.uniqueID.ToString() : "null")
                + ", required " + required + (map != null ? ", Required(map " + map.uniqueID + ") " + FullWalls.Required(map) + ", cut walls " + FwCutCount(map) : "");
        }

        private static void FwCheckState(string label, bool expect)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                Check("fw " + label, false, "no map");
                return;
            }
            int cut = FwCutCount(map);
            bool req = FullWalls.Required(map);
            Check("fw " + label, req == expect && (expect ? cut == 0 : cut > 0), "expected complete walls " + expect + "; " + FwState(map)
                + ", textured walls " + IsoRimMod.Settings.texturedWalls + ", wall jobs pending " + FwPendingWallJobs(map));
        }

        private static string FwSettingsSig()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            List<string> parts = new List<string>();
            foreach (FieldInfo f in typeof(IsoRimSettings).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderBy(f => f.Name))
            {
                object v = f.GetValue(st);
                string s = v is IEnumerable e && !(v is string) ? "[" + string.Join(";", e.Cast<object>().Select(o => o != null ? o.ToString() : "null")) + "]" : (v != null ? v.ToString() : "null");
                parts.Add(f.Name + "=" + s);
            }
            string all = string.Join(" ", parts);
            return all.GetHashCode().ToString("X8") + " (" + parts.Count + " fields; cutHover " + st.cutHover + " cutRooms " + st.cutRooms + " cutNearColonists "
                + st.cutNearColonists + " texturedWalls " + st.texturedWalls + ")";
        }

        // Queued (not yet redrawn) wall, damage and things blocks of that map in MapSections.
        private static int FwPendingWallJobs(Map map)
        {
            return FwPendingWallJobs(map, out int _);
        }

        // inView: those of blocks overlapping the camera's view rect.
        private static int FwPendingWallJobs(Map map, out int inView)
        {
            inView = 0;
            CellRect view = Find.CameraDriver != null ? Find.CameraDriver.CurrentViewRect : CellRect.Empty;
            FieldInfo fSection = AccessTools.Field(AccessTools.Inner(typeof(MapSections), "Job"), "section");
            IList queue = AccessTools.Field(typeof(MapSections), "queue").GetValue(null) as IList;
            int head = (int)AccessTools.Field(typeof(MapSections), "head").GetValue(null);
            Type job = AccessTools.Inner(typeof(MapSections), "Job");
            FieldInfo fMap = AccessTools.Field(job, "map"), fLayer = AccessTools.Field(job, "layer");
            int n = 0;
            for (int i = head; i < queue.Count; i++)
            {
                object j = queue[i];
                Type l = fLayer.GetValue(j) as Type;
                if (fMap.GetValue(j) == map && (l == typeof(SectionLayer_IsoRimWalls) || l == typeof(SectionLayer_IsoRimDamage) || l == MapSections.ThingsLayer))
                {
                    n++;
                    if (fSection.GetValue(j) is Section s && new CellRect(s.botLeft.x, s.botLeft.z, 17, 17).Overlaps(view))
                    {
                        inView++;
                    }
                }
            }
            return n;
        }

        private static Pawn FwTempPawn(Map map, IntVec3 c, int seed)
        {
            Pawn p;
            Rand.PushState(seed);
            try
            {
                p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                    fixedGender: Gender.Female, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f, developmentalStages: DevelopmentalStage.Adult,
                    forceNoIdeo: true, forbidAnyTitle: true));
            }
            finally
            {
                Rand.PopState();
            }
            GenSpawn.Spawn(p, c, map, Rot4.South);
            return p;
        }

        private static void FwExpectThrow<T>(string what, Action act) where T : Exception
        {
            try
            {
                act();
                Check("fw " + what, false, "no exception");
            }
            catch (T e)
            {
                Check("fw " + what, true, e.GetType().Name);
            }
            catch (Exception e)
            {
                Check("fw " + what, false, "wrong exception " + e.GetType().Name);
            }
        }

        private const int FwSettle = 8;   // frames for FullWalls.Refresh (next camera frame) and the cell texture

        private static IEnumerator<int> FwApiScript(IntVec3 fpCell)
        {
            Map map = Find.CurrentMap;
            yield return FwSettle;
            FwCheckState("api baseline", false);
            int baseCut = FwCutCount(map);

            IDisposable a = Api.RequestFullWalls(map);
            yield return FwSettle;
            FwCheckState("api A held", true);
            IDisposable b = Api.RequestFullWalls(map);
            yield return FwSettle;
            FwCheckState("api A+B held", true);
            a.Dispose();
            yield return FwSettle;
            FwCheckState("api A released first, B held", true);
            b.Dispose();
            yield return FwSettle;
            FwCheckState("api B released last", false);
            Check("fw api cuts back as before", FwCutCount(map) == baseCut, "cut " + FwCutCount(map) + " vs baseline " + baseCut);

            a = Api.RequestFullWalls(map);
            b = Api.RequestFullWalls(map);
            yield return FwSettle;
            b.Dispose();
            yield return FwSettle;
            FwCheckState("api B released first, A held", true);
            a.Dispose();
            yield return FwSettle;
            FwCheckState("api A released last", false);
            a.Dispose();
            b.Dispose();
            Check("fw api double dispose", FwRequestCount() == 0, FwState(map));

            // Overlap with first person, both orders.
            FwEnterFirstPerson(map, fpCell);
            IDisposable c = Api.RequestFullWalls(map);
            yield return FwSettle;
            FwCheckState("api first person + C", true);
            FirstPerson.Toggle();
            yield return FwSettle;
            FwCheckState("api first person left, C held", true);
            c.Dispose();
            yield return FwSettle;
            FwCheckState("api C released after first person", false);
            c = Api.RequestFullWalls(map);
            FwEnterFirstPerson(map, fpCell);
            yield return FwSettle;
            c.Dispose();
            yield return FwSettle;
            FwCheckState("api C released, first person on", true);
            FirstPerson.Toggle();
            yield return FwSettle;
            FwCheckState("api first person left last", false);

            // A followed pawn that dies: the request expires, the handle stays harmless.
            Pawn victim = FwTempPawn(map, fpCell + IntVec3.East, 1717);
            IDisposable d = Api.RequestFullWalls(map, victim);
            yield return FwSettle;
            FwCheckState("api D following a pawn", true);
            victim.Kill(null);
            yield return FwSettle;
            FwCheckState("api D after its pawn died", false);
            Check("fw api D expired", FwRequestCount() == 0, FwState(map));
            d.Dispose();
            FwExpectThrow<ArgumentException>("api dead followed pawn rejected", () => Api.RequestFullWalls(map, victim));

            // One that despawns, then comes back: the request stays expired.
            Pawn walker = FwTempPawn(map, fpCell + IntVec3.West, 1718);
            IDisposable e = Api.RequestFullWalls(map, walker);
            IDisposable f = Api.RequestFullWalls(map);
            yield return FwSettle;
            FwCheckState("api E (pawn) + F held", true);
            f.Dispose();
            yield return FwSettle;
            FwCheckState("api F released, E held", true);
            IntVec3 at = walker.Position;
            walker.DeSpawn();
            yield return FwSettle;
            FwCheckState("api E after its pawn despawned", false);
            GenSpawn.Spawn(walker, at, map, Rot4.South);
            yield return FwSettle;
            FwCheckState("api E stays expired after the pawn is back", false);
            e.Dispose();
            walker.Destroy(DestroyMode.Vanish);
            Corpse body = victim.Corpse;
            if (body != null && !body.Destroyed)
            {
                body.Destroy(DestroyMode.Vanish);
            }

            FwExpectThrow<ArgumentNullException>("api null map rejected", () => Api.RequestFullWalls(null));
            yield return FwSettle;
            FwCheckState("api end", false);
            Check("fw api settings unchanged", FwSettingsSig() == fwSigStart, FwSettingsSig() + " vs " + fwSigStart);
            Log.Message("[IsoRim probe] fw api script done (" + checksPassed + " pass, " + checksFailed + " fail)");
        }

        private static IEnumerator<int> FwMapScript(IntVec3 fpCell)
        {
            Map home = Find.CurrentMap;
            yield return FwSettle;
            FwCheckState("map baseline", false);
            int baseCut = FwCutCount(home);
            FwEnterFirstPerson(home, fpCell);
            IDisposable m = Api.RequestFullWalls(home);
            yield return FwSettle;
            FwCheckState("map first person + request", true);

            Scene("spacemap", new[] { "50" });
            Map space = Find.CurrentMap;
            yield return FwSettle * 2;
            Check("fw map switched", space != home && Find.CurrentMap == space, "current " + Find.CurrentMap.uniqueID + " home " + home.uniqueID);
            Check("fw map change ends first person and requests", !FirstPerson.On && FwRequestCount() == 0 && !FullWalls.Required(space) && !FullWalls.Required(home), FwState(space));
            FwExpectThrow<InvalidOperationException>("map request for a map not viewed rejected", () => Api.RequestFullWalls(home));
            m.Dispose();

            Current.Game.CurrentMap = home;
            yield return FwSettle * 2;
            FwCheckState("map back home: cutaway as before", false);
            Check("fw map cuts back as before", FwCutCount(home) == baseCut, "cut " + FwCutCount(home) + " vs baseline " + baseCut);
            IDisposable n = Api.RequestFullWalls(home);
            yield return FwSettle;
            m.Dispose();
            yield return FwSettle;
            FwCheckState("map old handle does not cancel a new request", true);
            n.Dispose();
            yield return FwSettle;
            FwCheckState("map end", false);
            Check("fw map settings unchanged", FwSettingsSig() == fwSigStart, FwSettingsSig() + " vs " + fwSigStart);
            Log.Message("[IsoRim probe] fw map script done (" + checksPassed + " pass, " + checksFailed + " fail)");
        }

        private static IEnumerator<int> FwGameScript(IntVec3 fpCell)
        {
            Map map = Find.CurrentMap;
            FwEnterFirstPerson(map, fpCell);
            IDisposable g = Api.RequestFullWalls(map);
            yield return FwSettle;
            FwCheckState("game first person + request before save", true);

            // The harness would rebuild its scene over the loaded map: off for the rest of the run.
            FieldInfo cap = AccessTools.Field(AccessTools.TypeByName("Capture17.Main"), "Active");
            if (cap != null)
            {
                cap.SetValue(null, false);
            }
            string save = "fw_gametest";
            GameDataSaveLoader.SaveGame(save);
            Game old = Current.Game;
            Log.Message("[IsoRim probe] fw game saved " + save + " (first person on, one request held), loading it");
            GameDataSaveLoader.LoadGame(save);
            while (Current.Game == old || LongEventHandler.ShouldWaitForEvent || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
            {
                yield return 10;
            }
            yield return 60;
            map = Find.CurrentMap;
            Check("fw game loaded: no first person, no requests", !FirstPerson.On && FwRequestCount() == 0 && !FullWalls.Required(map), FwState(map));
            g.Dispose();
            IDisposable h = Api.RequestFullWalls(map);
            yield return FwSettle;
            g.Dispose();
            yield return FwSettle;
            Check("fw game old handle does not cancel a new request", FullWalls.Required(map) && FwRequestCount() == 1, FwState(map));
            h.Dispose();
            yield return FwSettle;
            Check("fw game request released", !FullWalls.Required(map) && FwRequestCount() == 0, FwState(map));

            Pawn colonist = map.mapPawns.FreeColonistsSpawned.Where(p => p.RaceProps.Humanlike).OrderBy(p => p.thingIDNumber).FirstOrDefault();
            if (colonist == null)
            {
                Check("fw game colonist for first person", false, "none");
                yield break;
            }
            FwEnterFirstPerson(map, colonist.Position);
            IDisposable k = Api.RequestFullWalls(map);
            yield return FwSettle;
            Check("fw game first person again in the loaded game", FirstPerson.On && FullWalls.Required(map), FwState(map));
            Log.Message("[IsoRim probe] fw game: to the main menu with first person on and one request held");
            GenScene.GoToMainMenu();
            while (Current.ProgramState == ProgramState.Playing || LongEventHandler.ShouldWaitForEvent)
            {
                yield return 10;
            }
            yield return 120;
            Check("fw game main menu: nothing left", !FirstPerson.On && FwRequestCount() == 0 && Current.Game == null, FwState(null) + ", state " + Current.ProgramState);
            k.Dispose();
            Check("fw game main menu: old handle dispose harmless", FwRequestCount() == 0, FwState(null));
            Log.Message("[IsoRim probe] fw game script done (" + checksPassed + " pass, " + checksFailed + " fail)");
            string tag = GenCommandLine.TryGetCommandLineArg("cap17tag", out string t) ? t : "out";
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", tag);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "done.txt"), "fwgame " + checksPassed + " pass, " + checksFailed + " fail");
            yield return 30;
            Root.Shutdown();
        }
    }
}
