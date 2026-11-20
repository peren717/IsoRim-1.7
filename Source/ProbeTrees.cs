using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only key (see Probe): the tree layer is rebuilt only when its trees change (SectionLayer_IsoRimTrees), in one
    // seeded run (-isorimseed) on the quick-test map, with -isorimslice=N so the slicer's own path runs too.
    //   treecheck=main     the whole script below; treecheck=nohook (run with -isorimtreehook=0): the layer behaves as
    //                      before when the hook on Plant.TickLong is not in.
    // Every check has blocks (17 x 17 cells) and trees of its own. The script takes the harness's scene loop out (Capture17
    // off, as fwgame), holds the game paused and steps the game's own ticks itself (TickManager.DoSingleTick).
    // What changes the map is always the game:
    //   cut       a colonist's own CutPlant job, to its end;
    //   sown      a colonist's own Sow job (a tree def of this map), to its end;
    //   burnt     a fire started on the tree by the game's FireUtility.TryStartFireIn (weather set clear, as the harness
    //             does at every scene time) and real ticks until the tree is gone; the fires left are then removed;
    //   leafless  the game's own Plant.MakeLeafless(Cold) on the tree: the one change not reached through a job or the
    //             weather, because cold cannot be kept to one block and the seeded map's climate does not reach it;
    //   another change in the block / the "seen once" preparation: a plant destroyed (Thing.Destroy, as slicecheck).
    // Zooming out is the mouse wheel through the game's input path (as slicecheck); zooming in sets the camera.
    // Growth and leaves coming back run under the game's own DebugSettings.fastEcology (lead's decision 2026-11-19):
    // every plant's real Plant.TickLong each tick and the clock 2000 ticks on per tick, so a day passes in 30 ticks.
    // Leaves back out of view: every block is seen once more one tick before the leaves return, and the fixture is a
    // block where nothing else changed in the tick they did, so only the tree's own mark can bring its rebuild; then
    // the wheel brings it into view (the check fails with the line that asks for the block taken out: run TT6noflag).
    // The model: the script gives one tree a state through the addons' Api (Api.AddModelState) and a state model on
    // its entry for the length of the check (put back after); the long tick that sees it is one fastEcology tick.
    // "As a fresh rebuild": the layer's lists hashed (SliceHash), the layer rebuilt by the game (Section
    // .RegenerateSingleLayer) and hashed again. Lines: "[IsoRim probe] CHECK PASS|FAIL treecheck ...", then
    // "[IsoRim probe] treecheck done: N pass, M fail"; done.txt is written and the game quits.
    public static partial class Probe
    {
        private const float TreeNear = 12f;            // root size of the near view
        private const int TreeJobTicks = 30000;        // ticks a colonist's job may take
        private const int TreeBurnTicks = 30000;       // ticks a tree may take to burn down
        private const string TreeSave = "_agent_treecheck";   // the save of the load check (the runner deletes it after)

        private sealed class TreeBlock
        {
            public Section s;
            public SectionLayer_IsoRimTrees layer;
            public int built;                      // the layer's bakes at the last note
            public HashSet<int> ids;               // its trees at the last note
            public override string ToString() { return s.botLeft.x / Section.Size + ":" + s.botLeft.z / Section.Size; }
        }

        private static int treeHeldMax;

        private static bool TreeKey(string[] a, Map map)
        {
            Api17Patch();
            bool wheel = SliceWheelHook();
            string part = a.Length > 0 && a[0].Length > 0 ? a[0] : "main";
            if (!wheel)
            {
                Check("treecheck " + part, false, "the host's input pass was not found, no wheel");
                return true;
            }
            FwStart(TreeGuard(part == "nohook" ? TreeNoHookScript(map) : TreeScript(map), part));
            return true;
        }

        // Runs the script; when it ends or throws, the result is written and the game quits (the harness's loop is off).
        private static IEnumerator<int> TreeGuard(IEnumerator<int> script, string part)
        {
            while (true)
            {
                bool more;
                try
                {
                    more = script.MoveNext();
                }
                catch (Exception e)
                {
                    Check("treecheck " + part + " script", false, "stopped by an error: " + e);
                    more = false;
                }
                if (!more)
                {
                    break;
                }
                yield return script.Current;
            }
            DebugSettings.fastEcology = false;
            slicePointer = null;
            TreeDone(part);
            yield return 30;
            Root.Shutdown();
        }

        // Windows that take the wheel away from the map are closed (the game asks for the colony's name after a day:
        // named at the start; letters and the log window of dev mode).
        private static void TreeCloseWindows()
        {
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                if (w.layer == WindowLayer.Dialog || w.GetType().Name.Contains("Log"))
                {
                    Find.WindowStack.TryRemove(w, false);
                }
            }
        }

        private static void TreeNameColony(Map map)
        {
            if (!Faction.OfPlayer.HasName)
            {
                Faction.OfPlayer.Name = "treecheck";
            }
            RimWorld.Planet.Settlement home = map.Parent as RimWorld.Planet.Settlement;
            if (home != null && !home.namedByPlayer)
            {
                home.Name = "treecheck";
                home.namedByPlayer = true;
            }
        }

        // ---------------------------------------------------------------- blocks, trees, views

        private static List<Plant> TreesOf(Map map, Section s)
        {
            List<Plant> list = new List<Plant>();
            foreach (IntVec3 c in s.CellRect)
            {
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i].Position == c && SectionLayer_IsoRimTrees.Handles(things[i]))
                    {
                        list.Add((Plant)things[i]);
                    }
                }
            }
            list.Sort((x, y) => x.thingIDNumber.CompareTo(y.thingIDNumber));
            return list;
        }

        // A tree that can lose its leaves and show it: standing, in leaf, with a leafless sprite of the game's and leaf parts
        // the model drops.
        private static bool TreeLeafy(Plant p)
        {
            ModelEntry e = Forms.For(p.def).model;
            return e != null && !e.def.keepLeaves && p.def.plant.leaflessGraphic != null && !p.LeaflessNow && !p.def.plant.dieIfLeafless;
        }

        private static TreeBlock TreeBlockOf(Section s)
        {
            return new TreeBlock { s = s, layer = SectionLayer_IsoRimTrees.Of(s) };
        }

        private static void TreeNote(Map map, TreeBlock b)
        {
            b.built = b.layer.built;
            b.ids = new HashSet<int>(TreesOf(map, b.s).Select(t => t.thingIDNumber));
        }

        // The rect the game's own map draw tests blocks against: its own getter (MapDrawer.ViewRect), as SectionSlicer reads it.
        private static Func<MapDrawer, CellRect> treeViewRect;

        private static CellRect TreeView(Map map)
        {
            if (treeViewRect == null)
            {
                treeViewRect = AccessTools.MethodDelegate<Func<MapDrawer, CellRect>>(AccessTools.PropertyGetter(typeof(MapDrawer), "ViewRect"));
            }
            return treeViewRect(map.mapDrawer);
        }

        // The state the script gives one tree through the addons' Api (Api.AddModelState), for the check of the model.
        private const string TreeState = "treecheck";
        private static Thing treeStateOf;
        private static bool treeStateAdded;

        private static List<Section> TreeDrawn(Map map)
        {
            CellRect view = TreeView(map);
            List<Section> list = new List<Section>();
            foreach (Section s in sliceSections(map.mapDrawer))
            {
                Vector3 shift;
                if (s != null && Cull.Drawn(ref view, s, out shift))
                {
                    list.Add(s);
                }
            }
            return list;
        }

        private static int TreeWaiting(Map map)
        {
            int n = 0;
            foreach (Section s in TreeDrawn(map))
            {
                if (Cull.anyDirtyOf(s))
                {
                    n++;
                }
            }
            return n;
        }

        // The layer as it is against the game's rebuild of it now. (Rebuilds the layer: counts as one bake.)
        private static bool TreeAsFresh(TreeBlock b)
        {
            ulong held = SliceHash(b.layer);
            b.s.RegenerateSingleLayer(b.layer);
            return SliceHash(b.layer) == held;
        }

        // The block's trees against how the layer holds them: trees held, the largest drift of a drawn size, trees whose
        // leaves or model are no longer as baked, trees the layer does not hold.
        private static void TreeHeld(Map map, TreeBlock b, out int held, out float drift, out int lookChanged, out int unknown)
        {
            held = lookChanged = unknown = 0;
            drift = 0f;
            foreach (Plant p in TreesOf(map, b.s))
            {
                float d;
                bool same, bare;
                if (!b.layer.Holds(p, out d, out same, out bare))
                {
                    unknown++;
                    continue;
                }
                held++;
                drift = Mathf.Max(drift, d);
                if (!same)
                {
                    lookChanged++;
                }
            }
        }

        private static bool TreeOtherChange(Map map, TreeBlock b)
        {
            foreach (IntVec3 c in b.s.CellRect)
            {
                Plant p = c.GetPlant(map);
                if (p != null && p.Position == c && p.def.plant != null && !p.def.plant.IsTree)
                {
                    p.Destroy();
                    return true;
                }
            }
            return false;
        }

        private static void TreeTicks(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Find.TickManager.DoSingleTick();
            }
        }

        // ---------------------------------------------------------------- camera

        private static IEnumerator<int> TreeZoomOut(Map map, Action eachFrame = null)
        {
            CameraDriver cam = Find.CameraDriver;
            int notches = 0, quiet = 0, frames = 0;
            while ((float)sliceDesired.GetValue(cam) < cam.config.sizeRange.max - 0.001f)
            {
                if (notches++ > 400)
                {
                    throw new InvalidOperationException("the wheel did not zoom the camera out");
                }
                TreeCloseWindows();
                slicePending = -1f;
                eachFrame?.Invoke();
                yield return 1;
            }
            while (quiet < SliceSettle)
            {
                if (frames++ > SliceLimit)
                {
                    throw new InvalidOperationException("blocks still waiting after " + frames + " frames (slicer holds " + SectionSlicer.Held + ")");
                }
                treeHeldMax = Math.Max(treeHeldMax, SectionSlicer.Held);
                eachFrame?.Invoke();
                bool arrived = cam.RootSize >= cam.config.sizeRange.max - 0.05f;
                quiet = arrived && SectionSlicer.Held == 0 && TreeWaiting(map) == 0 ? quiet + 1 : 0;
                yield return 1;
            }
        }

        private static IEnumerator<int> TreeZoomIn(Map map)
        {
            Find.CameraDriver.SetRootPosAndSize(map.Center.ToVector3Shifted(), TreeNear);
            yield return 6;
        }

        // ---------------------------------------------------------------- the game's changes

        private static Pawn TreeWorker(Map map, LocalTargetInfo target)
        {
            return map.mapPawns.FreeColonistsSpawned.Where(p => p.RaceProps.Humanlike && !p.Downed && !p.InMentalState
                    && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && p.CanReach(target, PathEndMode.Touch, Danger.Deadly))
                .OrderBy(p => p.thingIDNumber).FirstOrDefault();
        }

        // A colonist's own job to its end: ordered as a player orders it, then the game's ticks until done() or the limit.
        private static IEnumerator<int> TreeJob(Map map, LocalTargetInfo target, Func<Job> make, Func<bool> done)
        {
            int ticks = 0, orders = 0;
            Pawn pawn = null;
            Job job = null;
            while (!done() && ticks < TreeJobTicks)
            {
                if (pawn == null || pawn.CurJob != job || pawn.Downed || pawn.InMentalState)
                {
                    if (orders++ >= 6)
                    {
                        break;
                    }
                    pawn = TreeWorker(map, target);
                    if (pawn == null)
                    {
                        break;
                    }
                    job = make();
                    job.playerForced = true;
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }
                for (int i = 0; i < 100 && !done(); i++)
                {
                    Find.TickManager.DoSingleTick();
                    ticks++;
                }
                yield return 1;
            }
        }

        private static IEnumerator<int> TreeCut(Map map, Plant tree)
        {
            return TreeJob(map, tree, () => JobMaker.MakeJob(JobDefOf.CutPlant, tree), () => tree.Destroyed);
        }

        // A cell of the block where the colonist can sow the def now, and the job; done when the sapling is in.
        private static IntVec3 TreeSowCell(Map map, TreeBlock b, ThingDef def)
        {
            foreach (IntVec3 c in b.s.CellRect.ContractedBy(2))
            {
                if (c.GetThingList(map).Count == 0 && c.Standable(map) && def.CanNowPlantAt(c, map) && PlantUtility.AdjacentSowBlocker(def, c, map) == null
                    && TreeWorker(map, c) != null)
                {
                    return c;
                }
            }
            return IntVec3.Invalid;
        }

        private static bool TreeSown(Map map, IntVec3 c, ThingDef def)
        {
            Plant p = c.IsValid ? c.GetPlant(map) : null;
            return p != null && p.def == def && p.LifeStage != PlantLifeStage.Sowing;
        }

        private static IEnumerator<int> TreeSow(Map map, IntVec3 c, ThingDef def)
        {
            return TreeJob(map, c, () => { Job j = JobMaker.MakeJob(JobDefOf.Sow, c); j.plantDefToSow = def; return j; }, () => TreeSown(map, c, def));
        }

        private static IEnumerator<int> TreeBurn(Map map, Plant tree)
        {
            int ticks = 0;
            while (!tree.Destroyed && ticks < TreeBurnTicks)
            {
                map.weatherManager.curWeather = WeatherDefOf.Clear;   // (as the harness sets it at every scene time: rain puts a fire out)
                if (!tree.Position.GetThingList(map).Any(t => t is Fire) && !tree.IsBurning())
                {
                    FireUtility.TryStartFireIn(tree.Position, map, 1f, null);
                }
                for (int i = 0; i < 100 && !tree.Destroyed; i++)
                {
                    Find.TickManager.DoSingleTick();
                    ticks++;
                }
                yield return 1;
            }
            foreach (Thing f in map.listerThings.ThingsOfDef(ThingDefOf.Fire).ToList())
            {
                if (!f.Destroyed)
                {
                    f.Destroy();
                }
            }
            yield return 1;
        }

        // Runs a part of the script inside the script.
        private static IEnumerable<int> Sub(IEnumerator<int> e)
        {
            while (e.MoveNext())
            {
                yield return e.Current;
            }
        }

        private static void TreeDone(string what)
        {
            Log.Message("[IsoRim probe] treecheck " + what + " done: " + checksPassed + " pass, " + checksFailed + " fail");
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "done.txt"), "treecheck " + what + " " + checksPassed + " pass, " + checksFailed + " fail");
        }

        private static void TreeHarnessOff()
        {
            // The harness's scene loop would end the run after its last scene time and moves the clock: off for this run.
            FieldInfo cap = AccessTools.Field(AccessTools.TypeByName("Capture17.Main"), "Active");
            if (cap != null)
            {
                cap.SetValue(null, false);
            }
        }

        // The blocks of the near view (wholly inside it) and those out of it that the far view draws, each with trees.
        private static void TreeSort(Map map, CellRect near, List<Section> far, int minTrees, List<TreeBlock> inView, List<TreeBlock> off)
        {
            foreach (Section s in far.OrderBy(x => x.botLeft.x).ThenBy(x => x.botLeft.z))
            {
                TreeBlock b = TreeBlockOf(s);
                if (b.layer == null || TreesOf(map, s).Count < minTrees)
                {
                    continue;
                }
                CellRect r = s.CellRect;
                if (near.Contains(r.Min) && near.Contains(r.Max))
                {
                    inView.Add(b);
                }
                else if (!r.Overlaps(near.ExpandedBy(4)) && !s.Bounds.Overlaps(near.ExpandedBy(4)))
                {
                    off.Add(b);
                }
            }
            Vector3 centre = map.Center.ToVector3Shifted();
            inView.Sort((x, y) => (x.s.CellRect.CenterVector3 - centre).sqrMagnitude.CompareTo((y.s.CellRect.CenterVector3 - centre).sqrMagnitude));
        }

        // ---------------------------------------------------------------- the script

        private static readonly string[] TreeActs = { "cut", "sown", "leafless", "burnt" };

        // One change of the given kind in the block. target: the tree it is made on (cut, leafless, burnt); sown: the cell.
        private static IEnumerator<int> TreeAct(Map map, string act, TreeBlock b, ThingDef sowDef, Action<bool, string> result)
        {
            List<Plant> trees = TreesOf(map, b.s);
            if (act == "sown")
            {
                IntVec3 c = TreeSowCell(map, b, sowDef);
                if (!c.IsValid)
                {
                    result(false, "no cell to sow in");
                    yield break;
                }
                foreach (int w in Sub(TreeSow(map, c, sowDef)))
                {
                    yield return w;
                }
                result(TreeSown(map, c, sowDef), sowDef.defName + " at " + c);
                yield break;
            }
            Plant tree = act == "leafless" ? trees.FirstOrDefault(TreeLeafy) : act == "burnt" ? trees.FirstOrDefault() : trees.FirstOrDefault(t => TreeWorker(map, t) != null);
            if (tree == null)
            {
                result(false, "no tree for it");
                yield break;
            }
            if (act == "leafless")
            {
                tree.MakeLeafless(Plant.LeaflessCause.Cold, false);
                result(tree.LeaflessNow, tree.def.defName + " at " + tree.Position);
            }
            else
            {
                foreach (int w in Sub(act == "cut" ? TreeCut(map, tree) : TreeBurn(map, tree)))
                {
                    yield return w;
                }
                result(tree.Destroyed, tree.def.defName + " at " + tree.Position);
            }
        }

        private static IEnumerator<int> TreeScript(Map map)
        {
            const string T = "treecheck ";
            TreeHarnessOff();
            TreeNameColony(map);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            foreach (int w in Sub(TreeZoomIn(map))) yield return w;
            map.mapDrawer.RegenerateEverythingNow();
            yield return 10;
            Check(T + "hook", SectionLayer_IsoRimTrees.GrowthHooked && SliceTestArg.Blocks != null,
                "the hook on Plant.TickLong is in: " + SectionLayer_IsoRimTrees.GrowthHooked + "; blocks a frame asked of the slicer: " + (SliceTestArg.Blocks != null ? SliceTestArg.Blocks.Value.ToString() : "none"));

            // The views, with nothing changed: a zoom out rebuilds no tree layer.
            CellRect near = TreeView(map);
            int built0 = SectionLayer_IsoRimTrees.Built;
            foreach (int w in Sub(TreeZoomOut(map))) yield return w;
            List<Section> far = TreeDrawn(map);
            Check(T + "zoom out, nothing changed", SectionLayer_IsoRimTrees.Built == built0, "tree layers built " + (SectionLayer_IsoRimTrees.Built - built0) + ", blocks drawn " + far.Count);
            foreach (int w in Sub(TreeZoomIn(map))) yield return w;
            List<TreeBlock> inView = new List<TreeBlock>(), off = new List<TreeBlock>();
            TreeSort(map, near, far, 4, inView, off);
            ThingDef sowDef = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>()
                .Where(p => SectionLayer_IsoRimTrees.Handles(p) && p.def.plant.Sowable && p.def.plant.leaflessGraphic != null)
                .GroupBy(p => p.def).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.defName).Select(g => g.Key).FirstOrDefault();
            bool enough = inView.Count >= 5 && off.Count >= 20 && sowDef != null;
            Check(T + "fixtures", enough, "blocks with 4 trees or more: " + inView.Count + " in the near view " + near + ", " + off.Count + " out of it (of " + far.Count
                + " the far view draws); tree to sow: " + (sowDef != null ? sowDef.defName : "none") + "; tick " + Find.TickManager.TicksGame);
            if (!enough)
            {
                yield break;
            }
            // off: three blocks an act (b, c, d), then the block marked while the slicer holds it, then leaves coming back; the rest: the others.
            TreeBlock hold = off.Skip(12).OrderByDescending(b => (b.s.CellRect.CenterVector3 - map.Center.ToVector3Shifted()).sqrMagnitude).ThenBy(b => b.s.botLeft.x).ThenBy(b => b.s.botLeft.z).First();
            List<TreeBlock> rest = off.Skip(12).Where(b => b != hold).ToList();
            TreeBlock leafOff = rest[0];
            List<TreeBlock> others = rest.Skip(1).ToList();

            // 1. Seen once: a tree goes in every block out of view, and the wheel brings them into view.
            foreach (TreeBlock b in off)
            {
                TreeNote(map, b);
                TreesOf(map, b.s)[0].Destroy();
            }
            yield return 3;
            Check(T + "marked off screen", off.All(b => b.layer.Dirty && b.layer.built == b.built), "blocks " + off.Count + ": marked " + off.Count(b => b.layer.Dirty) + ", rebuilt before seen " + off.Count(b => b.layer.built != b.built));
            foreach (int w in Sub(TreeZoomOut(map))) yield return w;
            Check(T + "seen once", off.All(b => !b.layer.Dirty && b.layer.built == b.built + 1), "blocks " + off.Count + ": rebuilt once " + off.Count(b => b.layer.built == b.built + 1)
                + ", left marked after the rebuild " + off.Count(b => b.layer.Dirty) + "; slicer held at most " + treeHeldMax);
            foreach (int w in Sub(TreeZoomIn(map))) yield return w;

            // 2. (a) in view: cut, sown, leafless (burnt: 5).
            for (int k = 0; k < 3; k++)
            {
                string act = TreeActs[k];
                TreeBlock b = inView[k];
                TreeNote(map, b);
                bool ok = false;
                string what = null;
                foreach (int w in Sub(TreeAct(map, act, b, sowDef, (o, s) => { ok = o; what = s; }))) yield return w;
                yield return 3;
                int times = b.layer.built - b.built;
                bool clean = !b.layer.Dirty, fresh = TreeAsFresh(b);
                Check(T + act + " in view", ok && times > 0 && clean && fresh, "block " + b + ", " + what + ": done " + ok + ", layer rebuilt " + times + " times, left marked " + !clean + ", as a fresh rebuild " + fresh);
            }

            // 3. Out of view: (b) the change alone, (c) with another change in the block, (d) in a block seen once (all are);
            //    the others: only another change (the tree layer must be left alone); one block is marked while the slicer holds it.
            foreach (TreeBlock b in off)
            {
                TreeNote(map, b);
            }
            bool[] did = new bool[9];
            string[] whats = new string[9];
            for (int k = 0; k < 3; k++)
            {
                for (int v = 0; v < 3; v++)
                {
                    int n = k * 3 + v;
                    TreeBlock b = off[n];
                    foreach (int w in Sub(TreeAct(map, TreeActs[k], b, sowDef, (o, s) => { did[n] = o; whats[n] = s; }))) yield return w;
                    if (v == 1)
                    {
                        did[n] &= TreeOtherChange(map, b);
                    }
                }
            }
            int othersChanged = 0;
            foreach (TreeBlock b in others.Concat(new[] { hold }))
            {
                if (TreeOtherChange(map, b))
                {
                    othersChanged++;
                }
            }
            yield return 3;
            for (int n = 0; n < 9; n++)
            {
                Check(T + TreeActs[n / 3] + " off screen " + "bcd"[n % 3] + ", before it is seen", did[n] && off[n].layer.Dirty && off[n].layer.built == off[n].built,
                    "block " + off[n] + ", " + whats[n] + ": done " + did[n] + ", marked " + off[n].layer.Dirty + ", rebuilt " + (off[n].layer.built - off[n].built));
            }
            bool heldMark = false;
            int heldThen = 0;
            foreach (int w in Sub(TreeZoomOut(map, () =>
            {
                if (!heldMark && SectionSlicer.Held > 0 && Cull.anyDirtyOf(hold.s) && TreeDrawn(map).Contains(hold.s))
                {
                    heldMark = true;
                    heldThen = SectionSlicer.Held;
                    TreesOf(map, hold.s)[0].Destroy();   // a tree goes while the block waits for the slicer
                }
            }))) yield return w;
            for (int n = 0; n < 9; n++)
            {
                TreeBlock b = off[n];
                int times = b.layer.built - b.built;
                bool clean = !b.layer.Dirty, fresh = TreeAsFresh(b);
                Check(T + TreeActs[n / 3] + " off screen " + "bcd"[n % 3], did[n] && times == 1 && clean && fresh,
                    "block " + b + ": rebuilt " + times + " times when seen, left marked " + !clean + ", as a fresh rebuild " + fresh);
            }
            {
                int times = hold.layer.built - hold.built;
                bool clean = !hold.layer.Dirty, fresh = TreeAsFresh(hold);
                Check(T + "marked while the slicer held the block", heldMark && times == 1 && clean && !Cull.anyDirtyOf(hold.s) && fresh,
                    "block " + hold + ": a tree went while the slicer held " + heldThen + " blocks back: " + heldMark + "; rebuilt " + times + " times, left marked " + !clean + ", as a fresh rebuild " + fresh);
            }
            {
                int same = 0, alone = 0, cameOrWent = 0, rebuiltThen = 0, differ = 0, exact = 0, growthOnly = 0;
                float worst = 0f;
                foreach (TreeBlock b in others)
                {
                    HashSet<int> now = new HashSet<int>(TreesOf(map, b.s).Select(t => t.thingIDNumber));
                    if (now.SetEquals(b.ids))
                    {
                        same++;
                        if (b.layer.built == b.built)
                        {
                            alone++;
                        }
                    }
                    else
                    {
                        cameOrWent++;
                        if (b.layer.built > b.built)
                        {
                            rebuiltThen++;
                        }
                    }
                    int held, lookChanged, unknown;
                    float drift;
                    TreeHeld(map, b, out held, out drift, out lookChanged, out unknown);
                    worst = Mathf.Max(worst, drift);
                    bool within = drift < SectionLayer_IsoRimTrees.GrowthShown && lookChanged == 0 && unknown == 0;
                    bool marked = b.layer.Dirty;
                    if (TreeAsFresh(b))
                    {
                        exact++;
                    }
                    else if (within)
                    {
                        growthOnly++;
                    }
                    if (marked || !within)
                    {
                        differ++;
                    }
                }
                Check(T + "only another change in the block", same > 0 && alone == same && differ == 0 && cameOrWent == rebuiltThen,
                    "blocks " + others.Count + " (another plant destroyed in " + othersChanged + " blocks, the held one included): trees the same in " + same + ", tree layer left alone in " + alone
                    + " of them; a tree came or went by itself in " + cameOrWent + ", rebuilt " + rebuiltThen + " of them; as a fresh rebuild " + exact + ", differing only by growth under the threshold " + growthOnly
                    + " (largest drift " + worst.ToString("0.0000") + "; the colonists' jobs took ticks), left marked or with a tree past the threshold " + differ);
            }
            foreach (int w in Sub(TreeZoomIn(map))) yield return w;

            // 4. Leaves come back, under the game's fastEcology (a day passes in 30 ticks). In view the block is rebuilt at
            //    once. Out of view the block is asked for by the tree's own mark alone: every block is seen once more just
            //    before the leaves return, and the fixture is a block where nothing else changed in the tick they did.
            {
                TreeBlock b = inView[3];
                Plant leafIn = TreesOf(map, b.s).FirstOrDefault(TreeLeafy);
                List<TreeBlock> farBlocks = new[] { leafOff }.Concat(others).Where(x => TreesOf(map, x.s).Any(TreeLeafy)).Take(12).ToList();
                List<Plant> farTrees = farBlocks.Select(x => TreesOf(map, x.s).First(TreeLeafy)).ToList();
                if (leafIn == null || farBlocks.Count == 0)
                {
                    Check(T + "leaves back: fixtures", false, "no tree in leaf in block " + b + " or out of view (" + farBlocks.Count + " blocks)");
                }
                else
                {
                    leafIn.MakeLeafless(Plant.LeaflessCause.Cold, false);
                    foreach (Plant p in farTrees)
                    {
                        p.MakeLeafless(Plant.LeaflessCause.Cold, false);
                    }
                    yield return 3;
                    float d;
                    bool sameLook, bare;
                    b.layer.Holds(leafIn, out d, out sameLook, out bare);
                    Check(T + "leaves lost in view (the fixture of leaves back)", bare && leafIn.LeaflessNow, "block " + b + ", " + leafIn.def.defName + ": baked bare " + bare);
                    int tick0 = Find.TickManager.TicksGame, backAt = -1, shownAt = -1;
                    DebugSettings.fastEcology = true;
                    try
                    {
                        for (int k = 0; k < 29; k++)   // (the leaves are back 60000 ticks after they went: at the 30th tick)
                        {
                            Find.TickManager.DoSingleTick();
                            yield return 2;
                        }
                    }
                    finally
                    {
                        DebugSettings.fastEcology = false;
                    }
                    foreach (int w in Sub(TreeZoomOut(map))) yield return w;
                    foreach (int w in Sub(TreeZoomIn(map))) yield return w;
                    TreeNote(map, b);
                    foreach (TreeBlock x in farBlocks)
                    {
                        TreeNote(map, x);
                    }
                    int look0 = SectionLayer_IsoRimTrees.LookMarks, backFar = 0, notAlone = 0;
                    TreeBlock farBlock = null;
                    Plant farTree = null;
                    DebugSettings.fastEcology = true;
                    try
                    {
                        for (int k = 29; k < 45 && shownAt < 0; k++)
                        {
                            Find.TickManager.DoSingleTick();
                            if (backAt < 0 && !leafIn.Destroyed && !leafIn.LeaflessNow)
                            {
                                backAt = k + 1;
                            }
                            // right after the tick, before the game looks at any block: the trees out of view whose leaves are back
                            for (int i = 0; i < farTrees.Count && farBlock == null && backFar == 0; i++)
                            {
                                TreeBlock x = farBlocks[i];
                                if (farTrees[i].Destroyed || farTrees[i].LeaflessNow || !x.layer.Dirty)
                                {
                                    continue;
                                }
                                if ((x.s.dirtyFlags & ~MapSections.OwnFlag) == 0 && !Cull.anyDirtyOf(x.s) && x.layer.built == x.built)
                                {
                                    farBlock = x;
                                    farTree = farTrees[i];
                                }
                                else
                                {
                                    notAlone++;
                                }
                            }
                            if (farBlock != null || notAlone > 0)
                            {
                                backFar = 1;   // (looked at once, at the tick the leaves came back)
                            }
                            yield return 2;
                            if (backAt >= 0 && b.layer.built > b.built && !b.layer.Dirty)
                            {
                                shownAt = k + 1;   // (rebuilt since the leaves came back: the bake itself, not the layer's note of the tree)
                            }
                        }
                    }
                    finally
                    {
                        DebugSettings.fastEcology = false;
                    }
                    {
                        int times = b.layer.built - b.built;
                        bool clean = !b.layer.Dirty, fresh = TreeAsFresh(b);
                        Check(T + "leaves back in view", backAt > 0 && shownAt == backAt && times > 0 && clean && SectionLayer_IsoRimTrees.LookMarks > look0 && fresh,
                            "block " + b + ": leaves back at fastEcology tick " + backAt + " (clock +" + (Find.TickManager.TicksGame - tick0) + "), layer rebuilt by tick " + shownAt + ": " + times
                            + " times, left marked " + !clean + ", leaf or model marks +" + (SectionLayer_IsoRimTrees.LookMarks - look0) + ", as a fresh rebuild " + fresh);
                    }
                    if (farBlock == null)
                    {
                        Check(T + "leaves back off screen", false, "no fixture: of " + farTrees.Count + " trees out of view made leafless, " + farTrees.Count(p => p.Destroyed) + " died, " + farTrees.Count(p => !p.Destroyed && !p.LeaflessNow)
                            + " are in leaf again; blocks where something else changed in the same tick or that waited already: " + notAlone);
                    }
                    else
                    {
                        yield return 3;
                        bool asked = Cull.anyDirtyOf(farBlock.s);
                        foreach (int w in Sub(TreeZoomOut(map))) yield return w;
                        int times = farBlock.layer.built - farBlock.built;
                        bool alive = !farTree.Destroyed && !farTree.LeaflessNow, clean = !farBlock.layer.Dirty, fresh = TreeAsFresh(farBlock);
                        Check(T + "leaves back off screen", alive && times == 1 && clean && fresh,
                            "block " + farBlock + ", " + farTree.def.defName + " at " + farTree.Position + " (nothing else changed in the block in that tick; " + notAlone + " other fixtures passed over): in leaf again " + alive
                            + ", the block waits for its rebuild " + asked + "; brought into view by the wheel: layer rebuilt " + times + " times, left marked " + !clean + ", as a fresh rebuild " + fresh);
                        foreach (int w in Sub(TreeZoomIn(map))) yield return w;
                    }
                }
            }

            // 4b. The model: an addon's state (Api.AddModelState) gives one tree another model, and nothing tells the map. The
            //     tree's next long tick marks the layer and asks for the block; what the bake noted for the state trackers
            //     (StateModels, GraphicModels) is not touched by the question the tick asks.
            {
                TreeBlock b = inView[4];
                Plant tree = TreesOf(map, b.s).FirstOrDefault(t => Forms.For(t.def).model != null && Forms.For(t.def).model.HasModel);
                ModelEntry entry = tree != null ? Forms.For(tree.def).model : null;
                Model own = tree != null ? ModelBaker.ModelNow(tree, entry, false) : null;
                Model other = null;
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OrderBy(t => t.thingIDNumber))
                {
                    if (tree != null && t.def != tree.def && SectionLayer_IsoRimTrees.Handles(t))
                    {
                        Model m = ModelBaker.ModelNow(t, Forms.For(t.def).model, false);
                        if (m != null && !ReferenceEquals(m, own))
                        {
                            other = m;
                            break;
                        }
                    }
                }
                if (tree == null || other == null)
                {
                    Check(T + "model changed: fixtures", false, "no tree in block " + b + " or no second tree model on the map");
                }
                else
                {
                    Dictionary<string, Model> statesWere = entry.byState;
                    entry.byState = new Dictionary<string, Model> { { TreeState, other } };
                    treeStateOf = null;
                    if (!treeStateAdded)
                    {
                        treeStateAdded = true;
                        Api.AddModelState(t => treeStateOf != null && t == treeStateOf ? TreeState : null);
                    }
                    try
                    {
                        b.s.RegenerateSingleLayer(b.layer);   // baked with the state models there and the tree in no state
                        yield return 2;
                        TreeNote(map, b);
                        string stateBaked0 = StateModels.StateReport(tree), empty0 = StateModels.Report(tree);
                        bool? young0 = StateModels.YoungBaked(tree);
                        int? graphic0 = GraphicModels.BakedIndex(tree);
                        int look0 = SectionLayer_IsoRimTrees.LookMarks;
                        treeStateOf = tree;   // the state changes
                        yield return 4;
                        bool quiet = b.layer.built == b.built && !b.layer.Dirty;
                        DebugSettings.fastEcology = true;
                        try
                        {
                            Find.TickManager.DoSingleTick();   // the tree's long tick
                        }
                        finally
                        {
                            DebugSettings.fastEcology = false;
                        }
                        // right after the tick, before the game looks at the block
                        bool marked = SectionLayer_IsoRimTrees.LookMarks > look0 && b.layer.Dirty && (b.s.dirtyFlags & MapSections.OwnFlag) != 0;
                        string stateBaked1 = StateModels.StateReport(tree);
                        bool untouched = stateBaked1 == stateBaked0 && StateModels.Report(tree) == empty0 && StateModels.YoungBaked(tree) == young0 && GraphicModels.BakedIndex(tree) == graphic0;
                        yield return 3;
                        int times = b.layer.built - b.built;
                        bool clean = !b.layer.Dirty;
                        string stateBaked2 = StateModels.StateReport(tree);
                        bool fresh = TreeAsFresh(b);
                        Check(T + "model changed", stateBaked0 == "" && quiet && marked && untouched && times > 0 && clean && stateBaked2 == TreeState && fresh,
                            "block " + b + ", " + tree.def.defName + " at " + tree.Position + " given the state '" + TreeState + "': before its long tick nothing rebuilt or marked: " + quiet + "; at the tick: layer marked and block asked for "
                            + marked + " (leaf or model marks +" + (SectionLayer_IsoRimTrees.LookMarks - look0) + "), the state the bake noted still '" + stateBaked1 + "' and the other trackers' notes as before: " + untouched
                            + "; then rebuilt " + times + " times, left marked " + !clean + ", the bake noted '" + stateBaked2 + "', as a fresh rebuild " + fresh);
                    }
                    finally
                    {
                        treeStateOf = null;
                        entry.byState = statesWere;
                    }
                    b.s.RegenerateSingleLayer(b.layer);
                    yield return 2;
                }
            }

            // Growth in view: a tree grown by the threshold marks its layer and nothing more; the block's next change shows it.
            {
                List<TreeBlock> watch = inView.ToList();
                foreach (TreeBlock b in watch)
                {
                    TreeNote(map, b);
                }
                TreeBlock grown = null;
                int ticks = 0, marks0 = SectionLayer_IsoRimTrees.GrowthMarks;
                bool stayed = false, shown = false;
                string detail = "";
                DebugSettings.fastEcology = true;
                try
                {
                    while (grown == null && ticks < 900)
                    {
                        // (a mark for growth is seen only where the block has nothing else new: its layer marked, the block not asked for)
                        List<TreeBlock> clean = watch.Where(b => !b.layer.Dirty).ToList();
                        int m0 = SectionLayer_IsoRimTrees.GrowthMarks;
                        Find.TickManager.DoSingleTick();
                        ticks++;
                        if (SectionLayer_IsoRimTrees.GrowthMarks > m0)
                        {
                            grown = clean.FirstOrDefault(b => b.layer.Dirty && b.s.dirtyFlags == 0 && !Cull.anyDirtyOf(b.s));
                        }
                        if (grown == null)
                        {
                            yield return 1;
                        }
                    }
                }
                finally
                {
                    DebugSettings.fastEcology = false;
                }
                if (grown != null)
                {
                    int b0 = grown.layer.built;
                    yield return 4;
                    stayed = grown.layer.Dirty && grown.layer.built == b0;
                    bool other = TreeOtherChange(map, grown);
                    yield return 3;
                    int held, lookChanged, unknown;
                    float drift;
                    TreeHeld(map, grown, out held, out drift, out lookChanged, out unknown);
                    shown = other && grown.layer.built == b0 + 1 && !grown.layer.Dirty && drift < SectionLayer_IsoRimTrees.GrowthShown && lookChanged == 0 && TreeAsFresh(grown);
                    detail = "block " + grown + ": marked for growth at fastEcology tick " + ticks + "; four frames later still marked and not rebuilt: " + stayed + "; after another plant went: rebuilt "
                        + (grown.layer.built - b0) + " times (the fresh rebuild of the check included), trees held " + held + ", largest drift " + drift.ToString("0.000");
                }
                Check(T + "growth in view", grown != null && stayed && shown, grown != null ? detail + "; growth marks +" + (SectionLayer_IsoRimTrees.GrowthMarks - marks0)
                    : "no tree layer of the near view was marked for growth alone in " + ticks + " fastEcology ticks (growth marks +" + (SectionLayer_IsoRimTrees.GrowthMarks - marks0) + ")");
            }
            // The far view after it: every tree layer either waits (marked) or holds every tree within the threshold.
            foreach (int w in Sub(TreeZoomOut(map))) yield return w;
            {
                int layers = 0, waiting = 0, exact = 0, growthOnly = 0, bad = 0, trees = 0;
                float worst = 0f;
                foreach (Section s in TreeDrawn(map))
                {
                    TreeBlock b = TreeBlockOf(s);
                    if (b.layer == null)
                    {
                        continue;
                    }
                    layers++;
                    if (b.layer.Dirty)
                    {
                        waiting++;
                        continue;
                    }
                    int held, lookChanged, unknown;
                    float drift;
                    TreeHeld(map, b, out held, out drift, out lookChanged, out unknown);
                    trees += held;
                    worst = Mathf.Max(worst, drift);
                    bool within = drift < SectionLayer_IsoRimTrees.GrowthShown && lookChanged == 0 && unknown == 0;
                    if (TreeAsFresh(b))
                    {
                        exact++;
                    }
                    else if (within)
                    {
                        growthOnly++;
                    }
                    if (!within)
                    {
                        bad++;
                    }
                }
                Check(T + "far view after growth", layers > 0 && bad == 0, "tree layers " + layers + ": waiting for their block's next change (marked) " + waiting + "; not marked: as a fresh rebuild " + exact
                    + ", differing only by growth under " + SectionLayer_IsoRimTrees.GrowthShown.ToString("0.00") + " of the baked size " + growthOnly + " (largest drift " + worst.ToString("0.000") + " over " + trees
                    + " trees), a tree past the threshold, with other leaves or model, or not held " + bad);
            }
            foreach (int w in Sub(TreeZoomIn(map))) yield return w;

            // 5. Burnt: in view, then (b), (c), (d) out of view. Last, because fire spreads to other blocks.
            {
                List<TreeBlock> inNow = new List<TreeBlock>(), offNow = new List<TreeBlock>();
                TreeSort(map, near, TreeDrawnAtFar(map, far), 2, inNow, offNow);
                bool have = inNow.Count >= 1 && offNow.Count >= 3;
                if (!have)
                {
                    Check(T + "burnt: fixtures", false, "blocks with trees left: " + inNow.Count + " in view, " + offNow.Count + " out of it");
                }
                else
                {
                    TreeBlock b = inNow[0];
                    TreeNote(map, b);
                    bool ok = false;
                    string what = null;
                    foreach (int w in Sub(TreeAct(map, "burnt", b, sowDef, (o, s) => { ok = o; what = s; }))) yield return w;
                    yield return 3;
                    int times = b.layer.built - b.built;
                    bool clean = !b.layer.Dirty, fresh = TreeAsFresh(b);
                    Check(T + "burnt in view", ok && times > 0 && clean && fresh, "block " + b + ", " + what + ": done " + ok + ", layer rebuilt " + times + " times, left marked " + !clean + ", as a fresh rebuild " + fresh);
                    List<TreeBlock> three = offNow.OrderByDescending(x => (x.s.CellRect.CenterVector3 - map.Center.ToVector3Shifted()).sqrMagnitude).ThenBy(x => x.s.botLeft.x).ThenBy(x => x.s.botLeft.z).Take(3).ToList();
                    bool[] burnt = new bool[3];
                    for (int v = 0; v < 3; v++)
                    {
                        int n = v;
                        foreach (int w in Sub(TreeAct(map, "burnt", three[n], sowDef, (o, s) => { burnt[n] = o; }))) yield return w;
                        if (v == 1)
                        {
                            TreeOtherChange(map, three[n]);
                        }
                    }
                    yield return 3;
                    foreach (TreeBlock x in three)
                    {
                        TreeNote(map, x);
                    }
                    bool[] markedOff = three.Select(x => x.layer.Dirty).ToArray();
                    foreach (int w in Sub(TreeZoomOut(map))) yield return w;
                    for (int v = 0; v < 3; v++)
                    {
                        TreeBlock x = three[v];
                        int seen = x.layer.built - x.built;
                        bool cleanX = !x.layer.Dirty, freshX = TreeAsFresh(x);
                        Check(T + "burnt off screen " + "bcd"[v], burnt[v] && markedOff[v] && seen == 1 && cleanX && freshX,
                            "block " + x + ": burnt " + burnt[v] + ", marked before it was seen " + markedOff[v] + ", rebuilt " + seen + " times when seen, left marked " + !cleanX + ", as a fresh rebuild " + freshX);
                    }
                    foreach (int w in Sub(TreeZoomIn(map))) yield return w;
                }
            }

            // 6. The 3D view off and on: a tree goes meanwhile; everything is rebuilt when it comes back.
            {
                TreeBlock b = TreeBlocksIn(map, near).FirstOrDefault(x => TreesOf(map, x.s).Count >= 2);
                Plant gone = b != null ? TreesOf(map, b.s)[0] : null;
                View3D.Active = false;
                yield return 40;
                if (gone != null)
                {
                    gone.Destroy();
                }
                yield return 10;
                View3D.Active = true;
                yield return 90;
                bool sound = TreeViewSound(map, near, out string detail);
                Check(T + "3D off and on", gone != null && gone.Destroyed && sound, "a tree went in block " + b + " while the 3D view was off; back on: " + detail);
            }

            // 7. A setting the bake reads: everything is rebuilt.
            {
                bool was = IsoRimMod.Settings.outlines;
                int builtBefore = SectionLayer_IsoRimTrees.Built;
                IsoRimMod.Settings.outlines = !was;
                int frames = 0;
                while (SectionLayer_IsoRimTrees.Built == builtBefore && frames++ < 900)
                {
                    yield return 1;
                }
                yield return 20;
                bool sound = TreeViewSound(map, near, out string detail);
                Check(T + "a setting changed", SectionLayer_IsoRimTrees.Built > builtBefore && sound, "outlines " + was + " -> " + !was + ": tree layers built " + (SectionLayer_IsoRimTrees.Built - builtBefore) + " after " + frames + " frames; " + detail);
                builtBefore = SectionLayer_IsoRimTrees.Built;
                IsoRimMod.Settings.outlines = was;
                frames = 0;
                while (SectionLayer_IsoRimTrees.Built == builtBefore && frames++ < 900)
                {
                    yield return 1;
                }
                yield return 20;
            }

            // 8. The old cut-away (textured walls off): trees near the pointer are baked bare by a rebuild of their layer.
            {
                IsoRimSettings st = IsoRimMod.Settings;
                bool walls = st.texturedWalls, pointer = st.treesNearPointer;
                int builtBefore = SectionLayer_IsoRimTrees.Built, frames = 0;
                st.texturedWalls = false;
                st.treesNearPointer = true;
                slicePointer = new Vector2(8f, 8f);   // the pointer in a corner first
                while (SectionLayer_IsoRimTrees.Built == builtBefore && frames++ < 900)
                {
                    yield return 1;
                }
                yield return 30;
                TreeBlock b = TreeBlocksIn(map, near).OrderBy(x => (x.s.CellRect.CenterVector3 - map.Center.ToVector3Shifted()).sqrMagnitude).FirstOrDefault();
                Plant tree = b != null ? TreesOf(map, b.s).OrderBy(t => (t.Position - map.Center).LengthHorizontalSquared).First() : null;
                bool thinned = false, back = false, fresh = false, freshBack = false;
                int rebuilt = 0, rebuiltBack = 0;
                if (tree != null)
                {
                    b = TreeBlockOf(map.mapDrawer.SectionAt(tree.Position));
                    TreeNote(map, b);
                    Vector3 on = Find.Camera.WorldToScreenPoint(tree.TrueCenter());
                    slicePointer = new Vector2(on.x, on.y);
                    for (int k = 0; k < 120 && !thinned; k++)
                    {
                        yield return 1;
                        thinned = Cutaway.TreeThinned(map, tree.Position);
                    }
                    yield return 20;
                    rebuilt = b.layer.built - b.built;
                    fresh = TreeAsFresh(b);
                    TreeNote(map, b);
                    slicePointer = new Vector2(8f, 8f);
                    for (int k = 0; k < 240 && !back; k++)
                    {
                        yield return 1;
                        back = !Cutaway.TreeThinned(map, tree.Position);
                    }
                    yield return 40;
                    rebuiltBack = b.layer.built - b.built;
                    freshBack = TreeAsFresh(b);
                }
                Check(T + "old cut-away", tree != null && thinned && rebuilt > 0 && fresh && back && rebuiltBack > 0 && freshBack,
                    tree == null ? "no tree in view" : "block " + b + ", " + tree.def.defName + " at " + tree.Position + " under the pointer: thinned " + thinned + ", layer rebuilt " + rebuilt + " times, as a fresh rebuild " + fresh
                    + "; pointer away: no longer thinned " + back + ", rebuilt " + rebuiltBack + " times, as a fresh rebuild " + freshBack);
                slicePointer = null;
                builtBefore = SectionLayer_IsoRimTrees.Built;
                st.texturedWalls = walls;
                st.treesNearPointer = pointer;
                frames = 0;
                while (SectionLayer_IsoRimTrees.Built == builtBefore && frames++ < 900)
                {
                    yield return 1;
                }
                yield return 20;
            }

            // 9. Save and load: a bare tree stays bare, every layer holds its trees, and changes after the load are shown.
            {
                TreeBlock b = TreeBlocksIn(map, near).FirstOrDefault(x => TreesOf(map, x.s).Any(TreeLeafy));
                Plant bareTree = b != null ? TreesOf(map, b.s).First(TreeLeafy) : null;
                IntVec3 bareAt = bareTree != null ? bareTree.Position : IntVec3.Invalid;
                if (bareTree != null)
                {
                    bareTree.MakeLeafless(Plant.LeaflessCause.Cold, false);
                }
                yield return 5;
                int savedAt = Find.TickManager.TicksGame;
                GameDataSaveLoader.SaveGame(TreeSave);
                Game old = Current.Game;
                GameDataSaveLoader.LoadGame(TreeSave);
                int waited = 0;
                while ((Current.Game == old || LongEventHandler.ShouldWaitForEvent || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) && waited++ < 6000)
                {
                    yield return 1;
                }
                yield return 120;
                map = Find.CurrentMap;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                foreach (int w in Sub(TreeZoomIn(map))) yield return w;
                yield return 20;
                Plant bareNow = bareAt.IsValid ? bareAt.GetPlant(map) : null;
                float d = 0f;
                bool sameLook = false, bare = false;
                bool heldBare = bareNow != null && SectionLayer_IsoRimTrees.Of(map.mapDrawer.SectionAt(bareAt)).Holds(bareNow, out d, out sameLook, out bare) && bare && sameLook && bareNow.LeaflessNow;
                bool sound = TreeViewSound(map, near, out string detail, true);
                Check(T + "after a load", Current.Game != old && heldBare && sound, "loaded after " + waited + " frames, " + (Find.TickManager.TicksGame - savedAt) + " ticks ran before the script held the game; the tree made leafless before the save is baked bare: "
                    + heldBare + "; " + detail);
                // a change in view, and one out of view brought in by the wheel
                List<TreeBlock> inNow = new List<TreeBlock>(), offNow = new List<TreeBlock>();
                int builtBefore = SectionLayer_IsoRimTrees.Built;
                foreach (int w in Sub(TreeZoomOut(map))) yield return w;
                int builtByZoom = SectionLayer_IsoRimTrees.Built - builtBefore;
                List<Section> farNow = TreeDrawn(map);
                foreach (int w in Sub(TreeZoomIn(map))) yield return w;
                TreeSort(map, near, farNow, 2, inNow, offNow);
                if (inNow.Count < 1 || offNow.Count < 2)
                {
                    Check(T + "after a load: fixtures", false, "blocks with trees: " + inNow.Count + " in view, " + offNow.Count + " out of it");
                }
                else
                {
                    TreeBlock a = inNow[0], o1 = offNow[0], o2 = offNow[1];
                    TreeNote(map, a);
                    bool ok = false;
                    string what = null;
                    foreach (int w in Sub(TreeAct(map, "cut", a, sowDef, (o, s) => { ok = o; what = s; }))) yield return w;
                    yield return 3;
                    int times = a.layer.built - a.built;
                    bool clean = !a.layer.Dirty, fresh = TreeAsFresh(a);
                    Check(T + "after a load: cut in view", builtByZoom == 0 && ok && times > 0 && clean && fresh,
                        "the first zoom out after the load built " + builtByZoom + " tree layers; block " + a + ", " + what + ": done " + ok + ", layer rebuilt " + times + " times, left marked " + !clean + ", as a fresh rebuild " + fresh);
                    TreeNote(map, o1);
                    TreeNote(map, o2);
                    TreesOf(map, o1.s)[0].Destroy();
                    bool other = TreeOtherChange(map, o2);
                    yield return 3;
                    foreach (int w in Sub(TreeZoomOut(map))) yield return w;
                    bool sameTrees = new HashSet<int>(TreesOf(map, o2.s).Select(t => t.thingIDNumber)).SetEquals(o2.ids);
                    int seen1 = o1.layer.built - o1.built, seen2 = o2.layer.built - o2.built;
                    int held2, lookChanged2, unknown2;
                    float drift2;
                    TreeHeld(map, o2, out held2, out drift2, out lookChanged2, out unknown2);
                    bool within2 = drift2 < SectionLayer_IsoRimTrees.GrowthShown && lookChanged2 == 0 && unknown2 == 0;
                    bool clean12 = !o1.layer.Dirty && !o2.layer.Dirty, fresh1 = TreeAsFresh(o1), fresh2 = TreeAsFresh(o2);
                    Check(T + "after a load: off screen", seen1 == 1 && other && (!sameTrees || seen2 == 0) && clean12 && fresh1 && within2,
                        "block " + o1 + " (a tree went): rebuilt " + seen1 + " times when seen, as a fresh rebuild " + fresh1 + "; block " + o2 + " (another plant went, trees the same: " + sameTrees + "): tree layer rebuilt "
                        + seen2 + " times, as a fresh rebuild " + fresh2 + " (its trees have grown by at most " + drift2.ToString("0.0000") + " since the load, under the threshold: " + within2 + "); neither left marked: " + clean12);
                }
            }

        }

        // The far view's blocks again (the sections are the map's own; the list of the first zoom out still holds).
        private static List<Section> TreeDrawnAtFar(Map map, List<Section> far)
        {
            return far;
        }

        private static List<TreeBlock> TreeBlocksIn(Map map, CellRect near)
        {
            List<TreeBlock> list = new List<TreeBlock>();
            foreach (Section s in sliceSections(map.mapDrawer))
            {
                if (s == null || !near.Contains(s.CellRect.Min) || !near.Contains(s.CellRect.Max))
                {
                    continue;
                }
                TreeBlock b = TreeBlockOf(s);
                if (b.layer != null && TreesOf(map, s).Count > 0)
                {
                    list.Add(b);
                }
            }
            return list.OrderBy(b => b.s.botLeft.x).ThenBy(b => b.s.botLeft.z).ToList();
        }

        // Every tree layer of the near view: not marked, holds each of its trees as it is now, and is as a fresh rebuild.
        // ticksPassed (after a load the game runs some ticks before the script holds it): a tree may then have grown, by
        // less than the threshold, and its layer differ from a fresh rebuild by that alone.
        private static bool TreeViewSound(Map map, CellRect near, out string detail, bool ticksPassed = false)
        {
            int blocks = 0, marked = 0, notHeld = 0, drifted = 0, differ = 0, trees = 0, past = 0;
            float worst = 0f;
            foreach (TreeBlock b in TreeBlocksIn(map, near))
            {
                blocks++;
                if (b.layer.Dirty)
                {
                    marked++;
                }
                int held, lookChanged, unknown;
                float drift;
                TreeHeld(map, b, out held, out drift, out lookChanged, out unknown);
                trees += held;
                notHeld += unknown;
                worst = Mathf.Max(worst, drift);
                if (drift > 0f || lookChanged > 0)
                {
                    drifted++;
                }
                if (drift >= SectionLayer_IsoRimTrees.GrowthShown || lookChanged > 0)
                {
                    past++;
                }
                if (!TreeAsFresh(b))
                {
                    differ++;
                }
            }
            detail = "blocks with trees in the near view " + blocks + " (" + trees + " trees held): left marked " + marked + ", trees not held " + notHeld + ", blocks with a tree no longer exactly as baked " + drifted
                + " (largest drift of a drawn size " + worst.ToString("0.0000") + "; past the threshold or with other leaves or model: " + past + "), not as a fresh rebuild " + differ;
            return blocks > 0 && marked == 0 && notHeld == 0 && past == 0 && (ticksPassed ? differ <= drifted : drifted == 0 && differ == 0);
        }

        // -isorimtreehook=0: without the hook the layer keeps the mark the game's draw leaves on it, and is rebuilt with
        // the block's next change of any kind, as before.
        private static IEnumerator<int> TreeNoHookScript(Map map)
        {
            const string T = "treecheck nohook ";
            TreeHarnessOff();
            TreeNameColony(map);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            foreach (int w in Sub(TreeZoomIn(map))) yield return w;
            map.mapDrawer.RegenerateEverythingNow();
            yield return 10;
            Check(T + "hook left out", !SectionLayer_IsoRimTrees.GrowthHooked && TreeHookTestArg.Off, "hooked " + SectionLayer_IsoRimTrees.GrowthHooked);
            CellRect near = TreeView(map);
            foreach (int w in Sub(TreeZoomOut(map))) yield return w;
            List<Section> far = TreeDrawn(map);
            foreach (int w in Sub(TreeZoomIn(map))) yield return w;
            List<TreeBlock> inView = new List<TreeBlock>(), off = new List<TreeBlock>();
            TreeSort(map, near, far, 3, inView, off);
            foreach (TreeBlock b in off)
            {
                TreeNote(map, b);
                TreesOf(map, b.s)[0].Destroy();
            }
            yield return 3;
            foreach (int w in Sub(TreeZoomOut(map))) yield return w;
            Check(T + "seen once", off.Count > 0 && off.All(b => b.layer.built == b.built + 1 && b.layer.Dirty),
                "blocks " + off.Count + ": rebuilt once " + off.Count(b => b.layer.built == b.built + 1) + ", still marked after the game's rebuild at the draw (as before) " + off.Count(b => b.layer.Dirty));
            foreach (int w in Sub(TreeZoomIn(map))) yield return w;
            int changed = 0, again = 0, same = 0;
            foreach (TreeBlock b in off)
            {
                TreeNote(map, b);
                if (TreeOtherChange(map, b))
                {
                    changed++;
                }
            }
            yield return 3;
            foreach (int w in Sub(TreeZoomOut(map))) yield return w;
            foreach (TreeBlock b in off)
            {
                if (new HashSet<int>(TreesOf(map, b.s).Select(t => t.thingIDNumber)).SetEquals(b.ids))
                {
                    same++;
                    if (b.layer.built == b.built + 1)
                    {
                        again++;
                    }
                }
            }
            Check(T + "only another change in the block", same > 0 && again == same, "another plant destroyed in " + changed + " blocks; trees the same in " + same + ", tree layer rebuilt again in " + again + " of them (as before)");
        }
    }
}
