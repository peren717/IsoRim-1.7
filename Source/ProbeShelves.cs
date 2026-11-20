using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only key (see Probe): stored items on a shelf's boards (ItemShelves, Api 82), in one seeded run
    // (-isorimseed) on the quick-test map.
    //   shelfcheck=main      the script below (the game's own shelves)
    //   shelfcheck=cap,N     run with a harness mod that sets the shelf's maxItemsInCell to N: the game's count is followed
    //   shelfcheck=nohook    run with -isorimshelfhook=0: without the hook stored items are drawn as before
    //   shelfcheck=fixtures  run with the harness mod _ShelfTiersFix (fixture storage buildings and ModelDefs): the paths
    //                        the game's shelves do not reach (deep, square, one cell with row settings, three tiers,
    //                        a model stretched to its footprint and turned a quarter to fit it, style defs with and
    //                        without boards of their own, a row that would leave its cell)
    // Every check has a shelf and cells of its own (built by the script in the layout's cleared ground; the layout only
    // gives the colonists), and what a check expects is worked out from its own shelf, never from map coordinates. The
    // script takes the harness's scene loop out (Capture17 off), holds the game paused and steps the game's own ticks
    // itself (TickManager.DoSingleTick). What brings an item to a shelf, takes it away or merges it is always a
    // colonist's own haul job (HaulAIUtility.HaulToCellStorageJob, ordered as a player orders it) run to its end; the
    // items themselves are laid on the ground next to the shelf first.
    // What is checked of an item: the height it rests at (Api.ItemRestHeight) and its place (the game's own
    // Thing.TrueCenter and DrawPos), against the rule worked out here on its own (ShelfWant, ShelfWantPlace: the design's
    // words, not ItemShelves' code); "the game's place" is the game's unpatched GenThing.ItemCenterAt (a reverse patch).
    // "As a fresh rebuild": the lists of the map-mesh layers that hold items in the cell's block hashed (SliceHash), the
    // layers rebuilt by the game (Section.RegenerateSingleLayer) and hashed again: a difference is an item still drawn
    // where it was.
    // What is drawn every frame is read where it is drawn: an item's own drawing (ItemDraw.Draw's place), a figure
    // IsoRim draws (the place PawnRenderer.RenderPawnAt is given) and a body the game draws (the place its parallel
    // pre-draw worked out, PawnRenderer's silhouettePos).
    // The pointer: Input.mousePosition faked and the game's own Selector.SelectUnderMouse called (as ProbeInteract's click).
    // Lines: "[IsoRim probe] CHECK PASS|FAIL shelfcheck ...", then "[IsoRim probe] shelfcheck <part> done: N pass, M fail";
    // done.txt is written and the game quits.
    public static partial class Probe
    {
        private const int ShelfJobTicks = 8000;       // ticks one haul may take
        private const float ShelfEps = 0.0005f;        // cells: a height or a place may be this far off
        private const float ShelfEdge = 0.001f;        // an item's centre stays this far inside its cell (the documented rule)
        private const float ShelfNear = 4.5f;          // root size of the camera on one fixture
        private const float ShelfFar = 16f;            // root size of the camera on all of them

        private static bool shelfReversed;

        // The game's own GenThing.ItemCenterAt, without any patch (reverse patch).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Vector3 ShelfGameCentre(Thing thing) => throw new NotImplementedException("reverse patch");

        private static bool ShelfKey(string[] a, Map map)
        {
            Api17Patch();
            string part = a.Length > 0 && a[0].Length > 0 ? a[0] : "main";
            int cap = a.Length > 1 ? I(a[1]) : 0;
            IEnumerator<int> script = part == "nohook" ? ShelfNoHookScript(map) : part == "cap" ? ShelfCapScript(map, cap)
                : part == "fixtures" ? ShelfFixtureScript(map) : ShelfScript(map);
            FwStart(ShelfGuard(script, part + (cap > 0 ? " " + cap : "")));
            return true;
        }

        private static IEnumerator<int> ShelfGuard(IEnumerator<int> script, string part)
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
                    Check("shelfcheck " + part + " script", false, "stopped by an error: " + e);
                    more = false;
                }
                if (!more)
                {
                    break;
                }
                yield return script.Current;
            }
            fakeMouse = null;
            Log.Message("[IsoRim probe] shelfcheck " + part + " done: " + checksPassed + " pass, " + checksFailed + " fail");
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "done.txt"), "shelfcheck " + part + " " + checksPassed + " pass, " + checksFailed + " fail");
            yield return 30;
            Root.Shutdown();
        }

        // ---------------------------------------------------------------- the rule, as the design states it

        // n items of a cell in the game's order over the tiers (0 the top): dealt one at a time to the top, the next tier
        // down and so on round again (so the tiers hold the same number and the upper ones the extra); the items go to the
        // top first. Where the rank-th item rests: its tier, its place in the tier's row, how many the row has.
        private static void ShelfWant(int n, int rank, int tiers, out int tier, out int index, out int inRow)
        {
            int[] holds = new int[tiers];
            for (int k = 0; k < n; k++)
            {
                holds[k % tiers]++;
            }
            int first = 0;
            for (tier = 0; tier < tiers; tier++)
            {
                if (rank < first + holds[tier])
                {
                    index = rank - first;
                    inRow = holds[tier];
                    return;
                }
                first += holds[tier];
            }
            throw new InvalidOperationException("rank " + rank + " of " + n);
        }

        // The place of the index-th of inRow items of a cell, on a tier whose row is `length` cells long (below 0: the
        // building's whole length) and moved `forward` cells.
        //   A building at least as wide as deep: the row runs along its width, left to right as seen from its front (facing
        //   south: west to east; north: east to west; east: south to north; west: north to south), and forward is toward the
        //   side it faces.
        //   A building deeper than wide: the row runs from its front to its back (facing south: south to north; north:
        //   north to south; east: east to west; west: west to east), and forward is to its right (east while it faces north,
        //   turned with it).
        // The row is centred on the building; each of the building's cells along the row has an equal share of it, in the
        // cells' own order; the item stands at (index + 0.5) / inRow of its cell's share, in the middle of the cell across
        // the row. Its centre never leaves its own cell (it stops ShelfEdge inside).
        private static Vector3 ShelfWantPlace(Building shelf, IntVec3 cell, int index, int inRow, float length = -1f, float forward = 0f)
        {
            float f = (index + 0.5f) / inRow;
            int rot = shelf.Rotation.AsInt;
            bool wide = shelf.def.size.x >= shelf.def.size.z;
            bool alongX, rising;
            float fx = 0f, fz = 0f;
            if (wide)
            {
                alongX = rot == 0 || rot == 2;
                rising = rot == 2 || rot == 1;
                fz = rot == 0 ? forward : rot == 2 ? -forward : 0f;
                fx = rot == 1 ? forward : rot == 3 ? -forward : 0f;
            }
            else
            {
                alongX = rot == 1 || rot == 3;
                rising = rot == 2 || rot == 3;
                fx = rot == 0 ? forward : rot == 2 ? -forward : 0f;
                fz = rot == 1 ? -forward : rot == 3 ? forward : 0f;
            }
            CellRect r = shelf.OccupiedRect();
            int cells = alongX ? r.Width : r.Height;
            float lowEnd = alongX ? r.minX : r.minZ;
            float len = length < 0f ? cells : length;
            float rowLow = lowEnd + cells * 0.5f - len * 0.5f;
            float share = len / cells;
            float shareLow = rowLow + ((alongX ? cell.x : cell.z) - lowEnd) * share;
            float along = rising ? shareLow + f * share : shareLow + share - f * share;
            Vector3 at = alongX ? new Vector3(along, 0f, cell.z + 0.5f + fz) : new Vector3(cell.x + 0.5f + fx, 0f, along);
            at.x = Mathf.Clamp(at.x, cell.x + ShelfEdge, cell.x + 1f - ShelfEdge);
            at.z = Mathf.Clamp(at.z, cell.z + ShelfEdge, cell.z + 1f - ShelfEdge);
            return at;
        }

        // What a check expects of a shelf: its tiers (0 the top) with each one's height (cells), row length (cells; below 0
        // the building's) and move forward (cells). boards false: every item on the top at the game's own places.
        private sealed class ShelfExpect
        {
            public bool boards = true;
            public float[] tiers;
            public float[] length;
            public float[] forward;

            public static ShelfExpect Of(params float[] tiers)
            {
                ShelfExpect e = new ShelfExpect { tiers = tiers, length = new float[tiers.Length], forward = new float[tiers.Length] };
                for (int i = 0; i < tiers.Length; i++)
                {
                    e.length[i] = -1f;
                }
                return e;
            }

            public ShelfExpect Row(int tier, float len, float fwd)
            {
                length[tier] = len;
                forward[tier] = fwd;
                return this;
            }

            public static ShelfExpect OnTop(float top)
            {
                ShelfExpect e = Of(top);
                e.boards = false;
                return e;
            }
        }

        // A shelf of the game's (its model as large as its footprint: model units are cells) as its binding gives it: the
        // top the building's own surface (Api.SurfaceHeight), the boards and row settings from its ModelDef.
        private static ShelfExpect ShelfBound(Building shelf)
        {
            ModelEntry e;
            List<ModelItemShelf> boards = ModelLibrary.TryGet(shelf.def, out e) ? e.def.itemShelves : null;
            int lower = boards != null ? boards.Count : 0;
            float[] h = new float[lower + 1];
            h[0] = Api.SurfaceHeight(shelf);
            for (int t = 1; t <= lower; t++)
            {
                h[t] = boards[lower - t].height;
            }
            ShelfExpect x = ShelfExpect.Of(h);
            if (e != null)
            {
                x.Row(0, e.def.itemTopWidth, e.def.itemTopForward);
                for (int t = 1; t <= lower; t++)
                {
                    x.Row(t, boards[lower - t].width, boards[lower - t].forward);
                }
            }
            return x;
        }

        private static List<Thing> ShelfItems(Map map, IntVec3 cell)
        {
            return cell.GetThingList(map).Where(t => t.def.category == ThingCategory.Item).OrderBy(t => t.thingIDNumber).ToList();
        }

        private static string ShelfName(Thing t)
        {
            return t.def.defName + "#" + t.thingIDNumber + (t.stackCount > 1 ? "x" + t.stackCount : "");
        }

        private static string P2(Vector3 v)
        {
            return v.x.ToString("0.###") + "," + v.z.ToString("0.###");
        }

        // Where the rule puts an item of a cell: its tier's height and its place (y: the game's own).
        private static void ShelfWantOf(Map map, Building shelf, ShelfExpect x, Thing item, out float height, out Vector3 place, out string said)
        {
            IntVec3 cell = item.Position;
            List<Thing> items = ShelfItems(map, cell);
            Vector3 game = ShelfGameCentre(item);
            int tier = 0, index = 0, inRow = 1;
            place = game;
            if (x.boards)
            {
                ShelfWant(items.Count, items.IndexOf(item), x.tiers.Length, out tier, out index, out inRow);
                place = ShelfWantPlace(shelf, cell, index, inRow, x.length[tier], x.forward[tier]);
                place.y = game.y;
            }
            height = x.tiers[tier];
            said = x.boards ? "tier " + tier + " place " + (index + 1) + "/" + inRow : "on the top";
        }

        // Every item of a cell against the rule.
        private static bool ShelfCellOk(Map map, Building shelf, IntVec3 cell, ShelfExpect x, out string detail)
        {
            List<Thing> items = ShelfItems(map, cell);
            bool all = true;
            List<string> parts = new List<string>();
            foreach (Thing t in items)
            {
                float height;
                Vector3 want;
                string said;
                ShelfWantOf(map, shelf, x, t, out height, out want, out said);
                float rest = Api.ItemRestHeight(t);
                Vector3 at = t.TrueCenter(), drawn = t.DrawPos;
                bool ok = Mathf.Abs(rest - height) < ShelfEps && Mathf.Abs(at.x - want.x) < ShelfEps && Mathf.Abs(at.z - want.z) < ShelfEps
                    && at.y == want.y && drawn == at && at.ToIntVec3() == cell;   // (its centre inside its own cell)
                all &= ok;
                parts.Add(ShelfName(t) + " " + said + ": rests " + rest.ToString("0.###") + (ok ? "" : " (want " + height.ToString("0.###") + ")") + " at " + P2(at)
                    + (ok ? "" : " (want " + P2(want) + "; y " + at.y + " game " + want.y + "; DrawPos " + drawn + ")"));
            }
            detail = shelf.def.defName + " " + shelf.Rotation.ToStringHuman() + " cell " + cell + ", " + items.Count + " item(s): " + (parts.Count > 0 ? string.Join("; ", parts) : "none");
            return all;
        }

        // ---------------------------------------------------------------- the map mesh

        private static readonly Type[] ShelfLayers = { typeof(SectionLayer_ThingsGeneral), typeof(SectionLayer_IsoRimBodies) };

        // Frames until the blocks of a building's cells have no layer left to rebuild (the camera must show them).
        private static IEnumerator<int> ShelfSettle(Map map, Building shelf)
        {
            List<Section> blocks = shelf.OccupiedRect().Cells.Select(c => map.mapDrawer.SectionAt(c)).Distinct().ToList();
            int quiet = 0, frames = 0;
            while (quiet < 4 && frames++ < 600)
            {
                quiet = !blocks.Any(s => Cull.anyDirtyOf(s)) && SectionSlicer.Held == 0 ? quiet + 1 : 0;
                yield return 1;
            }
        }

        // The item layers of the building's blocks as they are against the game's rebuild of them now.
        private static bool ShelfAsFresh(Map map, Building shelf, out string detail)
        {
            bool same = true;
            detail = "";
            foreach (Section s in shelf.OccupiedRect().Cells.Select(c => map.mapDrawer.SectionAt(c)).Distinct())
            {
                foreach (Type type in ShelfLayers)
                {
                    SectionLayer layer = s.GetLayer(type);
                    if (layer == null)
                    {
                        same = false;
                        detail += type.Name + " missing; ";
                        continue;
                    }
                    ulong held = SliceHash(layer);
                    s.RegenerateSingleLayer(layer);
                    ulong fresh = SliceHash(layer);
                    same &= held == fresh;
                    detail += type.Name + (held == fresh ? " as fresh; " : " NOT as fresh (" + held.ToString("x") + " / " + fresh.ToString("x") + "); ");
                }
            }
            return same;
        }

        // ---------------------------------------------------------------- fixtures and the game's changes

        private static IntVec3 shelfNext;      // where the next fixture goes
        private static IntVec3 shelfOrigin;
        private static int shelfColumn;
        private static Vector3 shelfOverview;  // the camera's place for all of them

        // A shelf of its own for a check: built for the player, its storage open to everything its def may hold. The cells
        // round it are the fixture's (the items wait south of it). Fixtures stand 7 cells apart, 6 to a row.
        private static Building ShelfBuild(Map map, string defName, Rot4 rot, string style = null)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamed(defName);
            Building b = (Building)ThingMaker.MakeThing(def, ThingDefOf.WoodLog);
            b.SetFaction(Faction.OfPlayer);
            if (style != null)
            {
                b.SetStyleDef(DefDatabase<ThingStyleDef>.GetNamed(style));
            }
            IntVec3 at = shelfNext;
            if (++shelfColumn >= 6)
            {
                shelfColumn = 0;
                shelfNext = new IntVec3(shelfOrigin.x, 0, shelfNext.z - 7);
            }
            else
            {
                shelfNext = new IntVec3(shelfNext.x + 7, 0, shelfNext.z);
            }
            GenSpawn.Spawn(b, at, map, rot);
            Building_Storage store = (Building_Storage)b;
            store.settings.filter.SetAllowAll(store.GetParentStoreSettings().filter);
            return b;
        }

        // The building's cells, west to east, then south to north.
        private static List<IntVec3> ShelfCells(Building shelf)
        {
            return shelf.OccupiedRect().Cells.OrderBy(c => c.x).ThenBy(c => c.z).ToList();
        }

        private static Thing ShelfMake(string defName, int count = 0, string stuff = null)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamed(defName);
            Thing t = typeof(Book).IsAssignableFrom(def.thingClass) ? BookUtility.MakeBook(def, ArtGenerationContext.Colony)
                : ThingMaker.MakeThing(def, stuff != null ? DefDatabase<ThingDef>.GetNamed(stuff) : def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            t.stackCount = count > 0 ? Mathf.Min(count, def.stackLimit) : def.stackLimit;
            return t;
        }

        // An item laid on the ground south of the shelf's block (no storage there), for a colonist to bring.
        private static Thing ShelfLay(Map map, Building shelf, string defName, int count = 0, string stuff = null)
        {
            return ShelfLayThing(map, shelf, ShelfMake(defName, count, stuff));
        }

        private static Thing ShelfLayThing(Map map, Building shelf, Thing t)
        {
            CellRect r = shelf.OccupiedRect();
            foreach (IntVec3 c in new CellRect(r.minX - 1, r.minZ - 3, r.Width + 2, 2))
            {
                if (c.InBounds(map) && c.Standable(map) && c.GetItemCount(map) == 0 && c.GetEdifice(map) == null)
                {
                    return GenSpawn.Spawn(t, c, map);
                }
            }
            throw new InvalidOperationException("no free ground south of " + shelf);
        }

        private static Pawn ShelfWorker(Map map, Thing item)
        {
            return map.mapPawns.FreeColonistsSpawned.Where(p => p.RaceProps.Humanlike && !p.Downed && !p.InMentalState
                    && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && p.CanReach(item, PathEndMode.Touch, Danger.Deadly))
                .OrderBy(p => p.Position.DistanceToSquared(item.Position)).ThenBy(p => p.thingIDNumber).FirstOrDefault();
        }

        // The colonists do nothing of their own while the script runs (no hauling or eating between the ordered jobs).
        private static void ShelfIdle(Map map)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.drafter != null)
                {
                    p.drafter.Drafted = false;
                }
                if (p.workSettings != null)
                {
                    p.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
                    p.workSettings.DisableAll();
                }
                if (p.needs != null)
                {
                    foreach (Need need in p.needs.AllNeeds)
                    {
                        need.CurLevelPercentage = 1f;
                    }
                }
            }
        }

        // A colonist's own haul job to its end: the item to that storage cell, ordered as a player orders it, then the
        // game's ticks until the item lies there (or is gone: merged into a stack there) or the limit.
        private static IEnumerator<int> ShelfHaul(Map map, Thing item, IntVec3 cell, Action<bool, string> result)
        {
            int ticks = 0, orders = 0;
            Pawn pawn = null;
            Job job = null;
            string why = "";
            Func<bool> done = () => item.Destroyed || (item.Spawned && item.Position == cell);
            ShelfIdle(map);
            while (!done() && ticks < ShelfJobTicks)
            {
                if (pawn == null || pawn.CurJob != job || pawn.Downed || pawn.InMentalState)
                {
                    if (orders++ >= 4 || !item.Spawned)
                    {
                        why = item.Spawned ? "ordered " + (orders - 1) + " times" : "the item is not on the map (held by " + item.ParentHolder + ")";
                        break;
                    }
                    pawn = ShelfWorker(map, item);
                    if (pawn == null)
                    {
                        why = "no colonist can reach it";
                        break;
                    }
                    if (!StoreUtility.IsGoodStoreCell(cell, map, item, pawn, pawn.Faction))
                    {
                        why = "the game refuses the cell (StoreUtility.IsGoodStoreCell)";
                        break;
                    }
                    job = HaulAIUtility.HaulToCellStorageJob(pawn, item, cell, false);
                    job.playerForced = true;
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }
                for (int i = 0; i < 60 && !done(); i++)
                {
                    Find.TickManager.DoSingleTick();
                    ticks++;
                }
                yield return 1;
            }
            foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
            {
                tp.Drawer.tweener.ResetTweenedPosToRoot();
            }
            result(done(), ShelfName(item) + " to " + cell + ": " + (done() ? (item.Destroyed ? "merged" : "there") : "NOT there (" + why + ")") + " after " + ticks + " ticks");
        }

        // Brings the items to the cell one after the other (the list's order: not the order they were made in). False if a
        // haul fails.
        private static IEnumerator<int> ShelfBring(Map map, IntVec3 cell, IEnumerable<Thing> items, Action<bool, string> result)
        {
            bool all = true;
            List<string> said = new List<string>();
            foreach (Thing item in items)
            {
                bool ok = false;
                string what = null;
                foreach (int w in Sub(ShelfHaul(map, item, cell, (o, s) => { ok = o; what = s; }))) yield return w;
                all &= ok;
                said.Add(what);
            }
            result(all, string.Join("; ", said));
        }

        // The colonists walk off (their own Goto job, the game's ticks) until none stands within 3 cells of the shelf: the
        // pointer checks need the shelf to themselves (a colonist beside it is under the pointer too).
        private static IEnumerator<int> ShelfAlone(Map map, Building shelf)
        {
            CellRect near = shelf.OccupiedRect().ExpandedBy(3);
            List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned.Where(p => near.Contains(p.Position)).OrderBy(p => p.thingIDNumber).ToList();
            int k = 0;
            foreach (Pawn p in pawns)
            {
                IntVec3 to = new IntVec3(shelf.Position.x - 5 - k++, 0, shelf.Position.z - 5);
                p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, to), JobTag.Misc);
            }
            for (int ticks = 0; ticks < 2000 && pawns.Any(p => near.Contains(p.Position) || p.pather.Moving); ticks += 30)
            {
                for (int i = 0; i < 30; i++)
                {
                    Find.TickManager.DoSingleTick();
                }
                yield return 1;
            }
            foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
            {
                tp.Drawer.tweener.ResetTweenedPosToRoot();
            }
        }

        private static readonly string[] ShelfKinds = { "Steel", "Cloth", "MedicineIndustrial", "WoodLog", "ComponentIndustrial", "Plasteel", "Silver", "Gold" };

        // n stacks of different kinds laid by the shelf (made in the kinds' order) and brought to the cell last made first.
        private static IEnumerator<int> ShelfStock(Map map, Building shelf, IntVec3 cell, int n, Action<bool, string> result, int firstKind = 0)
        {
            List<Thing> lay = new List<Thing>();
            for (int k = 0; k < n; k++)
            {
                lay.Add(ShelfLay(map, shelf, ShelfKinds[(firstKind + k) % ShelfKinds.Length]));
            }
            lay.Reverse();
            return ShelfBring(map, cell, lay, result);
        }

        private static IEnumerator<int> ShelfLook(Map map, Vector3 at, float size)
        {
            Find.CameraDriver.SetRootPosAndSize(at, size);
            yield return 8;
        }

        private static IEnumerator<int> ShelfLookAll(Map map)
        {
            return ShelfLook(map, shelfOverview, ShelfFar);
        }

        // Start of every part: the harness's loop out, the game paused, windows shut, the fixtures' ground, the camera on it.
        private static IEnumerator<int> ShelfStart(Map map, string T)
        {
            TreeHarnessOff();
            TreeNameColony(map);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            if (!shelfReversed)
            {
                Harmony.ReversePatch(AccessTools.Method(typeof(GenThing), "ItemCenterAt"), new HarmonyMethod(typeof(Probe), nameof(ShelfGameCentre)) { reversePatchType = HarmonyReversePatchType.Original });
                shelfReversed = true;
            }
            // (the layout's cleared ground: 44 x 30 cells from 58,54; the colonists stand in its south-west corner)
            shelfOrigin = shelfNext = new IntVec3(62, 0, 80);
            shelfColumn = 0;
            shelfOverview = new Vector3(80f, 0f, 68f);
            ShelfIdle(map);
            foreach (int w in Sub(ShelfLookAll(map))) yield return w;
            map.mapDrawer.RegenerateEverythingNow();
            yield return 10;
            Log.Message("[IsoRim probe] " + T + "start: Api " + Api.Version + ", hook in " + ItemShelves.Hooked + ", CopyHash of GenThing.ItemCenterAt "
                + Api.CopyHash(AccessTools.Method(typeof(GenThing), "ItemCenterAt")) + " (watched: " + ItemShelves.CountSourceHash + "), colonists "
                + map.mapPawns.FreeColonistsSpawned.Count + ", tick " + Find.TickManager.TicksGame + ", 3D " + View3D.Active + ", pitch " + View3D.Pitch.ToString("0.#"));
        }

        // A building's cells checked after a change: the rule, then the map mesh settled and as a fresh rebuild.
        private static IEnumerator<int> ShelfVerify(Map map, string what, Building shelf, ShelfExpect x = null)
        {
            foreach (int w in Sub(ShelfSettle(map, shelf))) yield return w;
            x = x ?? ShelfBound(shelf);
            bool ok = true;
            List<string> said = new List<string>();
            foreach (IntVec3 c in ShelfCells(shelf))
            {
                if (ShelfItems(map, c).Count == 0)
                {
                    continue;
                }
                string d;
                ok &= ShelfCellOk(map, shelf, c, x, out d);
                said.Add(d);
            }
            string fresh;
            bool asFresh = ShelfAsFresh(map, shelf, out fresh);
            Check("shelfcheck " + what, ok && asFresh && said.Count > 0, string.Join(" | ", said) + " | map mesh: " + fresh);
        }

        private static readonly System.Reflection.FieldInfo shelfApplied = AccessTools.Field(typeof(View3D), "appliedSig");

        // Frames until the view has taken a changed state in: it notes the change at once (View3D.Stamp), waits a third of
        // a second of real time for more changes and then rebuilds the maps (its own rule: nothing is hurried here).
        private static IEnumerator<int> ShelfStateApplied()
        {
            yield return 3;
            for (int k = 0; k < 2400 && (int)shelfApplied.GetValue(null) != View3D.Stamp; k++)
            {
                yield return 1;
            }
            yield return 3;
        }

        private static void ShelfDirty(Map map, Building shelf)
        {
            foreach (IntVec3 c in ShelfCells(shelf))
            {
                map.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Things);
            }
        }

        // ---------------------------------------------------------------- what is drawn every frame, read where it is drawn

        private static bool shelfDrawHooked;
        private static Thing shelfWatchItem;      // an item IsoRim draws every frame (ItemDraw.Draw)
        private static Vector3 shelfItemAt;       // its underside as last drawn (x, z; y the height)
        private static int shelfItemDraws;
        private static Pawn shelfWatchPawn;       // a pawn (a corpse's) whose drawing is asked (PawnRenderer.RenderPawnAt)
        private static Vector3 shelfPawnAt;       // the place the game handed its drawing
        private static int shelfPawnDraws;
        private static AccessTools.FieldRef<PawnRenderer, Pawn> shelfPawnOf;
        private static AccessTools.FieldRef<PawnRenderer, Vector3> shelfBodyAt;   // where the game's own drawing put the body

        private static void ShelfDrawHook()
        {
            if (shelfDrawHooked)
            {
                return;
            }
            shelfDrawHooked = true;
            shelfPawnOf = AccessTools.FieldRefAccess<PawnRenderer, Pawn>("pawn");
            shelfBodyAt = AccessTools.FieldRefAccess<PawnRenderer, Vector3>("silhouettePos");
            Harmony h = new Harmony("peren.isorim17.probe.shelfdraws");
            h.Patch(AccessTools.Method(typeof(ItemDraw), nameof(ItemDraw.Draw)), prefix: new HarmonyMethod(typeof(Probe), nameof(ShelfItemDrawn)));
            h.Patch(AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt)), prefix: new HarmonyMethod(typeof(Probe), nameof(ShelfPawnDrawn)) { priority = Priority.First });
        }

        public static void ShelfItemDrawn(Thing __0, Vector3 __1)
        {
            if (__0 != null && __0 == shelfWatchItem)
            {
                shelfItemAt = __1;
                shelfItemDraws++;
            }
        }

        public static void ShelfPawnDrawn(PawnRenderer __instance, Vector3 __0)
        {
            if (shelfWatchPawn != null && shelfPawnOf(__instance) == shelfWatchPawn)
            {
                shelfPawnAt = __0;
                shelfPawnDraws++;
            }
        }

        // ---------------------------------------------------------------- the pointer

        // A click with the pointer on a map point (x, z; y its height): what the game selects.
        private static IEnumerator<int> ShelfClick(Vector3 point, Action<List<object>> got, bool again = false)
        {
            HookMouse();
            CloseWindows();
            Find.UIRoot.screenshotMode.Active = false;
            if (!again)
            {
                Find.Selector.ClearSelection();   // (again: a second click on the same spot, the first one's selection kept)
            }
            fakeMouse = ScreenOf(point);
            yield return 12;
            fakeMouse = ScreenOf(point);
            selectUnderMouse.Invoke(Find.Selector, null);
            got(Find.Selector.SelectedObjects.ToList());
            yield return 4;
        }

        // The stack labels as the game draws them: GenMapUI.DrawThingLabel(Thing, string, Color) asks for the label's place
        // (LabelDrawPosFor) and hands it on; the place each thing's label was last drawn at is kept (interface coordinates).
        private static readonly Dictionary<int, Vector2> shelfLabels = new Dictionary<int, Vector2>();
        private static bool shelfLabelHooked;
        private static Thing shelfLabelOf;

        private static void ShelfLabelHook()
        {
            shelfLabels.Clear();
            if (shelfLabelHooked)
            {
                return;
            }
            shelfLabelHooked = true;
            Harmony h = new Harmony("peren.isorim17.probe.shelflabels");
            h.Patch(AccessTools.Method(typeof(GenMapUI), nameof(GenMapUI.DrawThingLabel), new[] { typeof(Thing), typeof(string), typeof(Color) }),
                prefix: new HarmonyMethod(typeof(Probe), nameof(ShelfLabelThing)) { priority = Priority.Last }, finalizer: new HarmonyMethod(typeof(Probe), nameof(ShelfLabelDone)));
            h.Patch(AccessTools.Method(typeof(GenMapUI), nameof(GenMapUI.DrawThingLabel), new[] { typeof(Vector2), typeof(string), typeof(Color) }),
                prefix: new HarmonyMethod(typeof(Probe), nameof(ShelfLabelAt)));
        }

        public static void ShelfLabelThing(Thing __0)
        {
            shelfLabelOf = __0;
        }

        public static void ShelfLabelDone()
        {
            shelfLabelOf = null;
        }

        public static void ShelfLabelAt(Vector2 __0)
        {
            if (shelfLabelOf != null)
            {
                shelfLabels[shelfLabelOf.thingIDNumber] = __0;
            }
        }

        private static void ShelfShot(string name)
        {
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png"));
        }

        // A point on an item to click: over its place (moved `south` cells), halfway up its own height.
        private static Vector3 ShelfPoint(Thing item, float south = 0f)
        {
            Vector3 at = item.TrueCenter();
            float rest = Api.ItemRestHeight(item);
            float top = Mathf.Max(Interaction.PickHeight(item), rest + Interaction.PickStep);
            return new Vector3(at.x, (rest + top) * 0.5f, at.z - south);
        }

        // ---------------------------------------------------------------- the measurement (design point 4)

        // Every item kind as IsoRim draws it lying on a shelf beside other items: how tall (cells over what it rests on).
        private static void ShelfMeasure()
        {
            List<KeyValuePair<float, string>> all = new List<KeyValuePair<float, string>>();
            Dictionary<string, int> byForm = new Dictionary<string, int>();
            int failed = 0;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading.OrderBy(d => d.defName))
            {
                if (def.category != ThingCategory.Item || def.IsCorpse || typeof(MinifiedThing).IsAssignableFrom(def.thingClass) || def.thingClass == null
                    || typeof(Pawn).IsAssignableFrom(def.thingClass))
                {
                    continue;
                }
                try
                {
                    Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    t.stackCount = def.stackLimit;
                    ThingForm f = Forms.For(t);
                    float h = 0f;
                    string how = f.form.ToString();
                    if (f.shape == ModelShape.Card)
                    {
                        how = "Card";
                        h = f.height;
                    }
                    else if (f.form == Form3D.Model && f.model != null && f.model.HasModel)
                    {
                        ModelEntry e = f.model;
                        Model m = e.Pick(t);
                        // several items in a cell: the game's 0.8, where IsoRim's models follow it (ModelBaker.Place)
                        float several = e.def.overArt || WeaponModels.Uses(t) ? 0.8f : 1f;
                        Vector3 fit = e.Fit(def) * e.def.scale * several;
                        h = e.def.layFlat ? 2f * m.halfDepth * fit.y + e.def.offset.y : m.maxY * fit.y + e.def.offset.y;
                        how = e.def.layFlat ? "Model laid flat" : several < 1f ? "Model x0.8" : "Model";
                    }
                    else if (f.form == Form3D.Worn)
                    {
                        h = GroundGear.Height;
                    }
                    else if (f.form == Form3D.Extruded)
                    {
                        h = f.height;
                    }
                    all.Add(new KeyValuePair<float, string>(h, def.defName + " (" + how + ")"));
                    int n;
                    byForm.TryGetValue(how, out n);
                    byForm[how] = n + 1;
                    // (never spawned; a kind the game does not let be destroyed is left to the collector: asking logs an error)
                    if (!t.Destroyed && t.def.destroyable)
                    {
                        t.Destroy();
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    Log.Message("[IsoRim probe] shelfmeasure " + def.defName + " not measured: " + e.Message);
                }
            }
            all.Sort((x, y) => y.Key.CompareTo(x.Key));
            Log.Message("[IsoRim probe] shelfmeasure: " + all.Count + " item kinds (" + failed + " not measured); by form: "
                + string.Join(", ", byForm.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value))
                + "; taller than 0.20: " + all.Count(kv => kv.Key > 0.20f) + "; taller than 0.40: " + all.Count(kv => kv.Key > 0.40f));
            Log.Message("[IsoRim probe] shelfmeasure tallest: " + string.Join(", ", all.Take(40).Select(kv => kv.Value + " " + kv.Key.ToString("0.00"))));
            foreach (float cut in new[] { 0.10f, 0.15f, 0.20f, 0.24f, 0.30f, 0.40f, 0.46f })
            {
                Log.Message("[IsoRim probe] shelfmeasure taller than " + cut.ToString("0.00") + ": " + all.Count(kv => kv.Key > cut));
            }
        }

        // ---------------------------------------------------------------- the script: the game's own shelves

        private static IEnumerator<int> ShelfScript(Map map)
        {
            const string T = "shelfcheck ";
            foreach (int w in Sub(ShelfStart(map, T))) yield return w;
            ShelfMeasure();
            ShelfDrawHook();
            Check(T + "hook", ItemShelves.Hooked && Api.Version >= 82, "the hook on GenThing.ItemCenterAt is in: " + ItemShelves.Hooked + ", Api.Version " + Api.Version);
            bool ok = false;
            string what = null;
            Action<bool, string> note = (o, s) => { ok = o; what = s; };

            // 0. The binding: the game's shelves have one board under the top, at the heights of their models.
            {
                Building probe = ShelfBuild(map, "Shelf", Rot4.South);
                Building small = ShelfBuild(map, "ShelfSmall", Rot4.South);
                float[] a = ShelfBound(probe).tiers, b = ShelfBound(small).tiers;
                Check(T + "binding", a.Length == 2 && b.Length == 2 && Mathf.Abs(a[0] - 0.60f) < ShelfEps && Mathf.Abs(a[1] - 0.14f) < ShelfEps
                        && Mathf.Abs(b[0] - 0.60f) < ShelfEps && Mathf.Abs(b[1] - 0.14f) < ShelfEps,
                    "Shelf tiers " + string.Join(" / ", a.Select(x => x.ToString("0.###"))) + ", ShelfSmall " + string.Join(" / ", b.Select(x => x.ToString("0.###")))
                    + "; the game's limit per cell " + probe.Position.GetMaxItemsAllowedInCell(map));
            }

            // 1. One cell filled one item at a time (mixed kinds), brought in another order than they were made in; then the
            //    other cell with three full stacks of one kind.
            {
                Building b = ShelfBuild(map, "Shelf", Rot4.South);
                List<IntVec3> cells = ShelfCells(b);
                Thing steel = ShelfLay(map, b, "Steel"), cloth = ShelfLay(map, b, "Cloth"), med = ShelfLay(map, b, "MedicineIndustrial");
                Check(T + "an item on the ground", Api.ItemRestHeight(steel) == 0f && steel.TrueCenter() == ShelfGameCentre(steel) && Api.ItemRestHeight(null) == 0f && Api.ItemRestHeight(b) == 0f,
                    ShelfName(steel) + " on the ground at " + steel.Position + ": rests " + Api.ItemRestHeight(steel) + ", at the game's place " + (steel.TrueCenter() == ShelfGameCentre(steel)));
                Thing[] order = { med, steel, cloth };
                for (int k = 0; k < order.Length; k++)
                {
                    foreach (int w in Sub(ShelfHaul(map, order[k], cells[0], note))) yield return w;
                    Check(T + "haul " + (k + 1) + " of 3", ok, what);
                    foreach (int w in Sub(ShelfVerify(map, (k + 1) + " item(s) in a cell, mixed", b))) yield return w;
                }
                List<Thing> same = new List<Thing> { ShelfLay(map, b, "Steel"), ShelfLay(map, b, "Steel"), ShelfLay(map, b, "Steel") };
                foreach (int w in Sub(ShelfBring(map, cells[1], same, note))) yield return w;
                Check(T + "haul three stacks of one kind", ok && ShelfItems(map, cells[1]).Count == 3, what);
                foreach (int w in Sub(ShelfVerify(map, "both cells full (3 mixed, 3 of one kind)", b))) yield return w;
            }

            // 2. The pointer, the labels and the target box, on a shelf of their own with three items in a cell.
            {
                Building b = ShelfBuild(map, "Shelf", Rot4.South);
                List<IntVec3> cells = ShelfCells(b);
                foreach (int w in Sub(ShelfStock(map, b, cells[0], 3, note))) yield return w;
                List<Thing> items = ShelfItems(map, cells[0]);
                foreach (int w in Sub(ShelfAlone(map, b))) yield return w;
                Check(T + "haul for the pointer", ok && items.Count == 3 && !map.mapPawns.AllPawnsSpawned.Any(p => b.OccupiedRect().ExpandedBy(3).Contains(p.Position)),
                    what + "; pawns within 3 cells of the shelf " + map.mapPawns.AllPawnsSpawned.Count(p => b.OccupiedRect().ExpandedBy(3).Contains(p.Position)));
                foreach (int w in Sub(ShelfLook(map, b.TrueCenter(), ShelfNear))) yield return w;
                foreach (int w in Sub(ShelfSettle(map, b))) yield return w;
                string[] names = { "top, first", "top, second", "lower board" };
                for (int k = 0; k < items.Count; k++)
                {
                    // (the lower board's item shows under the top only at the shelf's open front: clicked there)
                    Vector3 p = ShelfPoint(items[k], k == 2 ? 0.38f : 0f);
                    // The game's own click rule (Selector.SelectUnderMouse): a storage building comes first, so the first
                    // click on a shelf takes the shelf and the next click on the same spot the thing under the pointer.
                    List<object> first = null, got = null;
                    foreach (int w in Sub(ShelfClick(p, g => first = g))) yield return w;
                    foreach (int w in Sub(ShelfClick(p, g => got = g, true))) yield return w;
                    List<Thing> picks = Interaction.PickUnderPointer(map);
                    Check(T + "click on the item on the " + names[k], first.Count == 1 && first[0] == b && got.Count == 1 && got[0] == items[k] && picks.Count > 0 && picks[0] == items[k],
                        "pointer at " + p + ": first click " + string.Join(", ", first.Select(Desc)) + ", second click " + string.Join(", ", got.Select(o => o is Thing ? ShelfName((Thing)o) : Desc(o)))
                        + ", wanted " + ShelfName(items[k]) + "; 3D pick " + string.Join(", ", picks.Take(5).Select(x => ShelfName(x))));
                    if (k == 2)
                    {
                        yield return 3;
                        ShelfShot("shelf_click_lower");
                        yield return 3;
                    }
                }
                // the pointer on the top board straight over the lower board's item: the board hides that item (the top's
                // items and the shelf are found, not the item under the board)
                {
                    Vector3 under = items[2].TrueCenter();
                    Vector3 over = new Vector3(under.x, Api.SurfaceHeight(b) + 0.02f, under.z);
                    List<object> got = null;
                    foreach (int w in Sub(ShelfClick(over, g => got = g))) yield return w;
                    List<Thing> picks = Interaction.PickUnderPointer(map);
                    Check(T + "the pointer on the top board is not on the item under the board", picks.Contains(b) && !picks.Contains(items[2]),
                        "pointer at " + over + ": 3D pick " + string.Join(", ", picks.Take(5).Select(x => ShelfName(x))) + "; the click selected " + string.Join(", ", got.Select(Desc))
                        + " (the item under the board: " + ShelfName(items[2]) + ")");
                }

                // The stack labels: each at its item, on its board; do the labels of a cell overlap?
                {
                    Find.UIRoot.screenshotMode.Active = false;
                    Find.Selector.ClearSelection();
                    fakeMouse = null;
                    // (the game draws stack labels at its closest zoom only: the closest zoom as in play, IsoRim's minZoom;
                    // the harness lets its own shots come much closer)
                    float harnessMin = Find.CameraDriver.config.sizeRange.min;
                    Find.CameraDriver.config.sizeRange.min = IsoRimMod.Settings.minZoom;
                    Find.CameraDriver.SetRootPosAndSize(b.TrueCenter(), IsoRimMod.Settings.minZoom + 0.5f);
                    ShelfLabelHook();
                    yield return 12;
                    Log.Message("[IsoRim probe] " + T + "labels: zoom " + Find.CameraDriver.CurrentZoom + " (root size " + Find.CameraDriver.RootSize.ToString("0.#") + "), thingLabels "
                        + IsoRimMod.Settings.thingLabels + ", labels drawn last frame " + shelfLabels.Count);
                    bool placed = true;
                    List<Rect> rects = new List<Rect>();
                    List<string> said = new List<string>();
                    ShelfExpect x = ShelfBound(b);
                    Text.Font = GameFont.Tiny;
                    Vector2 firstOff = Vector2.zero;
                    for (int k = 0; k < items.Count; k++)
                    {
                        Thing t = items[k];
                        Vector2 label;
                        bool drawn = shelfLabels.TryGetValue(t.thingIDNumber, out label);
                        float height;
                        Vector3 place;
                        string tierSaid;
                        ShelfWantOf(map, b, x, t, out height, out place, out tierSaid);
                        Vector3 at = place + View3D.GroundForward * -0.4f;
                        Vector2 onGround = UiOf(ScreenOf(new Vector3(at.x, 0f, at.z)));
                        Vector2 onBoard = UiOf(ScreenOf(new Vector3(at.x, height, at.z)));
                        Vector2 size = Text.CalcSize(t.stackCount.ToStringCached());
                        rects.Add(new Rect(label.x - size.x * 0.5f - 4f, label.y, size.x + 8f, 12f));   // (the label's grey plate, GenMapUI.DrawThingLabel)
                        if (k == 0)
                        {
                            firstOff = label - onBoard;
                        }
                        placed &= drawn && (label - onBoard - firstOff).magnitude < 1.5f && (label - onBoard).magnitude < 4f;
                        said.Add(ShelfName(t) + (drawn ? " label drawn at " + label.x.ToString("0.#") + "," + label.y.ToString("0.#") : " NO label drawn") + " (0.4 in front of its place: on the ground "
                            + onGround.x.ToString("0.#") + "," + onGround.y.ToString("0.#") + ", on its board " + onBoard.x.ToString("0.#") + "," + onBoard.y.ToString("0.#") + "), text " + size.x.ToString("0") + " wide");
                    }
                    Text.Font = GameFont.Small;
                    int overlaps = 0;
                    for (int i = 0; i < rects.Count; i++)
                    {
                        for (int k = i + 1; k < rects.Count; k++)
                        {
                            overlaps += rects[i].Overlaps(rects[k]) ? 1 : 0;
                        }
                    }
                    Check(T + "labels stand at their items, on their boards", placed, string.Join("; ", said));
                    Check(T + "labels of a full cell do not overlap", overlaps == 0, overlaps + " pair(s) of " + rects.Count + " labels overlap at root size " + Find.CameraDriver.RootSize.ToString("0.#"));
                    yield return 3;
                    ShelfShot("shelf_labels");
                    yield return 3;
                    Find.CameraDriver.config.sizeRange.min = harnessMin;
                }

                // The selection bracket of the lower item (shot), and the target box of the designator that takes things:
                // with the top items forbidden, the forbid designator's thing is the lower one.
                {
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(items[2], false, false);
                    yield return 6;
                    ShelfShot("shelf_bracket_lower");
                    yield return 3;
                    Find.Selector.ClearSelection();
                    items[0].SetForbidden(true, false);
                    items[1].SetForbidden(true, false);
                    Designator d = DefDatabase<DesignationCategoryDef>.AllDefs.SelectMany(c => c.AllResolvedDesignators).FirstOrDefault(x => x is Designator_Forbid) ?? new Designator_Forbid();
                    Find.DesignatorManager.Select(d);
                    HookMouse();
                    Vector3 mid = cells[0].ToVector3Shifted();
                    fakeMouse = ScreenOf(new Vector3(mid.x, 0f, mid.z));
                    int drawn0 = TargetBox.Drawn;
                    yield return 14;
                    Thing boxed = TargetBox.Target;
                    float boxTop = TargetBox.LastHeight;
                    Rect foot = TargetBox.LastFootprint;
                    Vector3 at = items[2].TrueCenter();
                    bool boxOk = boxed == items[2] && TargetBox.Drawn > drawn0 && Mathf.Abs(boxTop - Mathf.Max(Interaction.PickHeight(items[2]), 0.2f)) < 0.001f
                        && boxTop < Api.SurfaceHeight(b) && foot.Contains(new Vector2(at.x, at.z));
                    Check(T + "target box on the lower board's item", boxOk, "boxed " + (boxed != null ? ShelfName(boxed) : "nothing") + ", wanted " + ShelfName(items[2])
                        + "; box top " + boxTop.ToString("0.###") + " (the item's top " + Interaction.PickHeight(items[2]).ToString("0.###") + ", the shelf's top " + Api.SurfaceHeight(b).ToString("0.###")
                        + "), footprint " + foot + ", boxes drawn " + (TargetBox.Drawn - drawn0));
                    ShelfShot("shelf_targetbox_lower");
                    yield return 3;
                    Find.DesignatorManager.Deselect();
                    items[0].SetForbidden(false, false);
                    items[1].SetForbidden(false, false);
                    fakeMouse = null;
                    Find.UIRoot.screenshotMode.Active = true;
                }
                foreach (int w in Sub(ShelfLookAll(map))) yield return w;
            }

            // 3. One item taken away by a colonist (to another shelf): the rest of the cell stands anew.
            {
                Building b = ShelfBuild(map, "Shelf", Rot4.South);
                Building other = ShelfBuild(map, "ShelfSmall", Rot4.South);
                IntVec3 cell = ShelfCells(b)[0];
                foreach (int w in Sub(ShelfStock(map, b, cell, 3, note))) yield return w;
                Check(T + "haul three before one is taken", ok && ShelfItems(map, cell).Count == 3, what);
                foreach (int w in Sub(ShelfVerify(map, "three items before one is taken away", b))) yield return w;
                Thing first = ShelfItems(map, cell)[0];
                foreach (int w in Sub(ShelfHaul(map, first, other.Position, note))) yield return w;
                Check(T + "haul one away", ok && ShelfItems(map, cell).Count == 2, what);
                foreach (int w in Sub(ShelfVerify(map, "one of three taken away: the two left", b))) yield return w;
                foreach (int w in Sub(ShelfVerify(map, "the item taken away, alone on a small shelf", other))) yield return w;
            }

            // 4. A stack merged into: nothing moves.
            {
                Building b = ShelfBuild(map, "Shelf", Rot4.South);
                IntVec3 cell = ShelfCells(b)[0];
                Thing steel = ShelfLay(map, b, "Steel", 30), cloth = ShelfLay(map, b, "Cloth"), more = ShelfLay(map, b, "Steel", 20);
                foreach (int w in Sub(ShelfBring(map, cell, new[] { steel, cloth }, note))) yield return w;
                bool brought = ok;
                string broughtSaid = what;
                foreach (int w in Sub(ShelfVerify(map, "two stacks before a merge", b))) yield return w;
                Vector3 steelAt = steel.TrueCenter(), clothAt = cloth.TrueCenter();
                float steelRest = Api.ItemRestHeight(steel), clothRest = Api.ItemRestHeight(cloth);
                foreach (int w in Sub(ShelfHaul(map, more, cell, note))) yield return w;
                bool still = steel.TrueCenter() == steelAt && cloth.TrueCenter() == clothAt && Api.ItemRestHeight(steel) == steelRest && Api.ItemRestHeight(cloth) == clothRest;
                Check(T + "merge moves nothing", brought && ok && more.Destroyed && steel.stackCount == 50 && ShelfItems(map, cell).Count == 2 && still,
                    broughtSaid + "; " + what + "; the stack now " + steel.stackCount + ", items in the cell " + ShelfItems(map, cell).Count + ", places and heights as before " + still);
                foreach (int w in Sub(ShelfVerify(map, "two stacks after the merge", b))) yield return w;
            }

            // 5. Weapons and apparel on a small shelf.
            {
                Building b = ShelfBuild(map, "ShelfSmall", Rot4.South);
                List<Thing> gear = new List<Thing> { ShelfLay(map, b, "Gun_BoltActionRifle"), ShelfLay(map, b, "Apparel_SimpleHelmet", 0, "Steel"), ShelfLay(map, b, "Gun_Revolver") };
                foreach (int w in Sub(ShelfBring(map, b.Position, new[] { gear[2], gear[0], gear[1] }, note))) yield return w;
                Check(T + "haul weapons and apparel", ok, what);
                foreach (int w in Sub(ShelfVerify(map, "two weapons and a helmet on a small shelf", b))) yield return w;
            }

            // 6. An item the game draws every frame (a book) beside items of the map mesh: its coming and going must redraw
            //    them (the game marks nothing for it), and the book itself is drawn at its place on its board.
            {
                Building b = ShelfBuild(map, "Shelf", Rot4.South);
                Building other = ShelfBuild(map, "ShelfSmall", Rot4.South);
                IntVec3 cell = ShelfCells(b)[1];
                Thing steel = ShelfLay(map, b, "Steel"), cloth = ShelfLay(map, b, "Cloth");
                Thing book = ShelfLay(map, b, "Novel");
                bool live = LiveItems.Is(book.def);
                foreach (int w in Sub(ShelfBring(map, cell, new[] { steel, cloth }, note))) yield return w;
                bool brought = ok;
                string broughtSaid = what;
                foreach (int w in Sub(ShelfVerify(map, "two map-mesh items before a book comes", b))) yield return w;
                foreach (int w in Sub(ShelfHaul(map, book, cell, note))) yield return w;
                Check(T + "haul a book (drawn every frame: " + live + ", drawerType " + book.def.drawerType + ")", brought && ok && live, broughtSaid + "; " + what);
                foreach (int w in Sub(ShelfVerify(map, "a book came: the map-mesh items beside it", b))) yield return w;
                foreach (int w in Sub(ShelfLook(map, b.TrueCenter(), ShelfNear))) yield return w;
                float height;
                Vector3 place;
                string tierSaid;
                ShelfWantOf(map, b, ShelfBound(b), book, out height, out place, out tierSaid);
                shelfWatchItem = book;
                shelfItemDraws = 0;
                shelfItemAt = Vector3.zero;
                yield return 10;
                shelfWatchItem = null;
                Check(T + "the book is drawn every frame at its place on its board", shelfItemDraws >= 8 && Mathf.Abs(shelfItemAt.x - place.x) < ShelfEps && Mathf.Abs(shelfItemAt.z - place.z) < ShelfEps
                        && Mathf.Abs(shelfItemAt.y - height) < ShelfEps && height < Api.SurfaceHeight(b),
                    "drawn " + shelfItemDraws + " times in 10 frames, last with its underside at " + P2(shelfItemAt) + " height " + shelfItemAt.y.ToString("0.###") + "; the rule: " + tierSaid + " at "
                    + P2(place) + " height " + height.ToString("0.###"));
                foreach (int w in Sub(ShelfLookAll(map))) yield return w;
                foreach (int w in Sub(ShelfHaul(map, book, other.Position, note))) yield return w;
                Check(T + "haul the book away", ok, what);
                foreach (int w in Sub(ShelfVerify(map, "the book went: the map-mesh items left", b))) yield return w;
            }

            // 7. A corpse IsoRim draws as its model (an item to the game): the figure is drawn at the item's place, raised to
            //    the item's board.
            {
                Building b = ShelfBuild(map, "Shelf", Rot4.South);
                IntVec3 cell = ShelfCells(b)[0];
                Thing steel = ShelfLay(map, b, "Steel");
                Rand.PushState(8217);
                Pawn rat = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named("Rat"), null, PawnGenerationContext.NonPlayer));
                Rand.PopState();
                ShelfLayThing(map, b, rat);
                rat.Kill(null);
                Corpse corpse = rat.Corpse;
                foreach (int w in Sub(ShelfBring(map, cell, new Thing[] { corpse, steel }, note))) yield return w;
                Check(T + "haul a corpse and a stack", ok, what);
                foreach (int w in Sub(ShelfVerify(map, "a corpse and a stack in a cell", b))) yield return w;
                foreach (int w in Sub(ShelfLook(map, b.TrueCenter(), ShelfNear))) yield return w;
                float height;
                Vector3 place;
                string tierSaid;
                ShelfWantOf(map, b, ShelfBound(b), corpse, out height, out place, out tierSaid);
                shelfWatchPawn = rat;
                shelfPawnDraws = 0;
                shelfPawnAt = Vector3.zero;
                yield return 10;
                shelfWatchPawn = null;
                float lift = Corpses.RestHeight(rat);   // (what the figure's drawers raise it by)
                Check(T + "the corpse's figure is drawn at the item's place, on its board", Patches.DrawnAsModel(rat) && shelfPawnDraws >= 8
                        && Mathf.Abs(shelfPawnAt.x - place.x) < ShelfEps && Mathf.Abs(shelfPawnAt.z - place.z) < ShelfEps && Mathf.Abs(lift - height) < ShelfEps && height < Api.SurfaceHeight(b),
                    "drawn as its model " + Patches.DrawnAsModel(rat) + ", " + shelfPawnDraws + " times in 10 frames at " + P2(shelfPawnAt) + ", raised " + lift.ToString("0.###") + "; the rule: " + tierSaid + " at "
                    + P2(place) + " height " + height.ToString("0.###"));
                foreach (int w in Sub(ShelfLookAll(map))) yield return w;
            }

            // 7b. A corpse the GAME draws (animals as models off: its flat body, placed by the game's parallel pre-draw, off
            //     the main thread): frame after frame the body is drawn at the item's place in its row.
            {
                Building b = ShelfBuild(map, "Shelf", Rot4.South);
                IntVec3 cell = ShelfCells(b)[0];
                Thing steel = ShelfLay(map, b, "Steel");
                Rand.PushState(8219);
                Pawn rat = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named("Rat"), null, PawnGenerationContext.NonPlayer));
                Rand.PopState();
                ShelfLayThing(map, b, rat);
                rat.Kill(null);
                Corpse corpse = rat.Corpse;
                foreach (int w in Sub(ShelfBring(map, cell, new Thing[] { corpse, steel }, note))) yield return w;
                bool models = IsoRimMod.Settings.animals3D;
                IsoRimMod.Settings.animals3D = false;
                int drawnFrames = 0, off = 0;
                Vector3 worst = Vector3.zero, row = Vector3.zero, game = Vector3.zero;
                try
                {
                    foreach (int w in Sub(ShelfLook(map, b.TrueCenter(), ShelfNear))) yield return w;
                    yield return 30;
                    foreach (int w in Sub(ShelfSettle(map, b))) yield return w;
                    Vector3 none = new Vector3(-7f, -7f, -7f);
                    for (int k = 0; k < 30; k++)
                    {
                        shelfBodyAt(rat.Drawer.renderer) = none;   // (PawnRenderer.RenderPawnAt notes where it drew the body: results.bodyPos)
                        yield return 1;
                        Vector3 body = shelfBodyAt(rat.Drawer.renderer);
                        row = corpse.TrueCenter();
                        game = ShelfGameCentre(corpse);
                        if (body == none)
                        {
                            continue;   // not drawn by the game in this frame
                        }
                        drawnFrames++;
                        if (Mathf.Abs(body.x - row.x) > ShelfEps || Mathf.Abs(body.z - row.z) > ShelfEps)
                        {
                            off++;
                            worst = body;
                        }
                    }
                }
                finally
                {
                    IsoRimMod.Settings.animals3D = models;
                }
                string cellSaid;
                bool placed = ShelfCellOk(map, b, cell, ShelfBound(b), out cellSaid);
                Check(T + "a corpse the game draws: its body at the item's place in every frame", ok && placed && drawnFrames >= 25 && off == 0 && (row - game).MagnitudeHorizontal() > 0.05f,
                    what + "; frames the game drew the body " + drawnFrames + " of 30, of them off the row place " + off + (off > 0 ? " (body at " + P2(worst) + ")" : "")
                    + "; row place " + P2(row) + ", the game's own place " + P2(game) + "; " + cellSaid);
                // The same question without any timing in it: the corpse's centre asked from another thread while this one
                // waits for it (as the game's parallel pre-draw asks it), against the answer on this thread.
                {
                    Vector3 here = corpse.TrueCenter(), there = new Vector3(-7f, -7f, -7f);
                    Exception failed = null;
                    Thread asker = new Thread(() =>
                    {
                        try
                        {
                            there = corpse.TrueCenter();
                        }
                        catch (Exception e)
                        {
                            failed = e;
                        }
                    });
                    asker.Start();
                    asker.Join();
                    float height;
                    Vector3 place;
                    string tierSaid;
                    ShelfWantOf(map, b, ShelfBound(b), corpse, out height, out place, out tierSaid);
                    Check(T + "a corpse's centre asked from another thread: the same as on the main thread, its place in its row", failed == null && there == here
                            && Mathf.Abs(here.x - place.x) < ShelfEps && Mathf.Abs(here.z - place.z) < ShelfEps && (here - ShelfGameCentre(corpse)).MagnitudeHorizontal() > 0.05f,
                        "main thread " + here + ", another thread " + there + (failed != null ? " (threw " + failed + ")" : "") + "; the rule: " + tierSaid + " at " + P2(place)
                        + "; the game's own place " + P2(ShelfGameCentre(corpse)));
                }
                foreach (int w in Sub(ShelfLookAll(map))) yield return w;
                yield return 20;
            }

            // 8. All four rotations, both cells (3 and 2 items).
            for (int r = 0; r < 4; r++)
            {
                Building b = ShelfBuild(map, "Shelf", new Rot4(r));
                List<IntVec3> cells = ShelfCells(b);
                foreach (int w in Sub(ShelfStock(map, b, cells[0], 3, note))) yield return w;
                bool first = ok;
                string firstSaid = what;
                foreach (int w in Sub(ShelfStock(map, b, cells[1], 2, note, 3))) yield return w;
                Check(T + "haul onto a shelf facing " + b.Rotation.ToStringHuman(), first && ok, firstSaid + "; " + what);
                foreach (int w in Sub(ShelfVerify(map, "shelf facing " + b.Rotation.ToStringHuman() + " (3 and 2 items)", b))) yield return w;
            }

            // 9. A Techist shelf (Ideology's style, a model inside the shelf's own def: byStyle; it has the same lower board).
            if (DefDatabase<ThingStyleDef>.GetNamedSilentFail("Techist_Shelf") != null)
            {
                Building b = ShelfBuild(map, "Shelf", Rot4.South, "Techist_Shelf");
                ModelEntry e;
                ModelLibrary.TryGet(b.def, out e);
                bool styledModel = e != null && e.ForStyle(b.StyleDef, b) != null && e.Pick(b) == e.ForStyle(b.StyleDef, b);
                foreach (int w in Sub(ShelfStock(map, b, ShelfCells(b)[0], 3, note))) yield return w;
                Check(T + "haul onto a Techist shelf", ok && styledModel, what + "; drawn as its style's model " + styledModel);
                foreach (int w in Sub(ShelfVerify(map, "Techist shelf (3 items)", b))) yield return w;
            }
            else
            {
                Log.Message("[IsoRim probe] " + T + "Techist shelf: the style is not loaded, left out");
            }

            // 10. Unchanged: (a) a storage building whose ModelDef has no boards (the shelf's own def with its list taken
            //     out for the length of the check: the binding as it was before Api 82); (b) the 3D view off.
            {
                Building b = ShelfBuild(map, "Shelf", Rot4.South);
                List<IntVec3> cells = ShelfCells(b);
                foreach (int w in Sub(ShelfStock(map, b, cells[0], 3, note))) yield return w;
                bool first = ok;
                foreach (int w in Sub(ShelfStock(map, b, cells[1], 2, note, 3))) yield return w;
                Check(T + "haul for the unchanged cases", first && ok, what);
                ModelEntry e;
                ModelLibrary.TryGet(b.def, out e);
                List<ModelItemShelf> kept = e.def.itemShelves;
                e.def.itemShelves = null;
                try
                {
                    ShelfDirty(map, b);
                    foreach (int w in Sub(ShelfVerify(map, "no boards in the ModelDef: all on the top at the game's places", b, ShelfExpect.OnTop(Api.SurfaceHeight(b))))) yield return w;
                }
                finally
                {
                    e.def.itemShelves = kept;
                }
                ShelfDirty(map, b);
                foreach (int w in Sub(ShelfVerify(map, "the boards back", b))) yield return w;

                View3D.Active = false;
                yield return 20;
                bool flat = true;
                List<string> said = new List<string>();
                foreach (IntVec3 c in cells)
                {
                    foreach (Thing t in ShelfItems(map, c))
                    {
                        Vector3 game = ShelfGameCentre(t), at = t.TrueCenter();
                        bool same = at == game && t.DrawPos == game && Api.ItemRestHeight(t) == 0f;
                        flat &= same;
                        said.Add(ShelfName(t) + " at " + P2(at) + (same ? "" : " (the game: " + game + ", rest " + Api.ItemRestHeight(t) + ")"));
                    }
                }
                Check(T + "3D view off: the game's places", flat && said.Count == 5, string.Join("; ", said));
                View3D.Active = true;
                yield return 30;
                foreach (int w in Sub(ShelfVerify(map, "3D view on again", b))) yield return w;
            }

            // 11. The row settings (ModelItemShelf.width / forward, ModelDef.itemTopWidth / itemTopForward), set on the
            //     shelf's def for the length of the check: a shorter row on the top, a shorter one moved to the front
            //     below; on shelves of their own facing south, east and north.
            {
                List<Building> facing = new List<Building>();
                foreach (Rot4 rot in new[] { Rot4.South, Rot4.East, Rot4.North })
                {
                    Building b = ShelfBuild(map, "Shelf", rot);
                    facing.Add(b);
                    foreach (int w in Sub(ShelfStock(map, b, ShelfCells(b)[0], 3, note))) yield return w;
                    Check(T + "haul for the row settings, shelf facing " + rot.ToStringHuman(), ok, what);
                }
                ModelEntry e;
                ModelLibrary.TryGet(facing[0].def, out e);
                ModelItemShelf board = e.def.itemShelves[0];
                float w0 = board.width, f0 = board.forward, tw0 = e.def.itemTopWidth, tf0 = e.def.itemTopForward;
                board.width = 1f;
                board.forward = 0.2f;
                e.def.itemTopWidth = 1.6f;
                try
                {
                    foreach (Building b in facing)
                    {
                        ShelfDirty(map, b);
                        foreach (int w in Sub(ShelfVerify(map, "row settings (top 1.6 long; board 1.0 long, 0.2 to the front), shelf facing " + b.Rotation.ToStringHuman(), b))) yield return w;
                    }
                    // the south-facing shelf in plain numbers, from its own west cell: the top's row from 0.2 to 1.8 of
                    // the shelf (this cell 0.2 to 1.0: its two items at 0.4 and 0.8), the board's from 0.5 to 1.5 (this
                    // cell 0.5 to 1.0: its item at 0.75), 0.2 south of the cell's middle
                    Building south = facing[0];
                    IntVec3 west = ShelfCells(south)[0];
                    List<Thing> items = ShelfItems(map, west);
                    Vector3 top = items[0].TrueCenter(), low = items[2].TrueCenter();
                    Vector3 topWant = new Vector3(west.x + 0.4f, 0f, west.z + 0.5f), lowWant = new Vector3(west.x + 0.75f, 0f, west.z + 0.3f);
                    Check(T + "row settings: where they put the items of the south-facing shelf", Mathf.Abs(low.x - lowWant.x) < ShelfEps && Mathf.Abs(low.z - lowWant.z) < ShelfEps
                            && Mathf.Abs(top.x - topWant.x) < ShelfEps && Mathf.Abs(top.z - topWant.z) < ShelfEps,
                        "cell " + west + ": the board's item at " + P2(low) + " (want " + P2(lowWant) + "), the top's first at " + P2(top) + " (want " + P2(topWant) + ")");
                }
                finally
                {
                    board.width = w0;
                    board.forward = f0;
                    e.def.itemTopWidth = tw0;
                    e.def.itemTopForward = tf0;
                }
                foreach (Building b in facing)
                {
                    ShelfDirty(map, b);
                }
                foreach (int w in Sub(ShelfVerify(map, "row settings back", facing[0]))) yield return w;
            }

            // 12. The view's state changes while stocked shelves stand on the map (View3D.Stamp: the "3D models" option off
            //     and on again, set as an addon sets an option, Api.SetOption; the 3D view stays on). Off: the shelf is not
            //     drawn as its model, so no boards: all on its top at the game's places. On again: back on the tiers.
            {
                Building b = ShelfBuild(map, "Shelf", Rot4.South);
                List<IntVec3> cells = ShelfCells(b);
                foreach (int w in Sub(ShelfStock(map, b, cells[0], 3, note))) yield return w;
                bool first = ok;
                string firstSaid = what;
                foreach (int w in Sub(ShelfStock(map, b, cells[1], 2, note, 3))) yield return w;
                Check(T + "haul for the change of the view's state", first && ok, firstSaid + "; " + what);
                foreach (int w in Sub(ShelfVerify(map, "before the change of the view's state: on the tiers", b))) yield return w;
                int stampOn = View3D.Stamp, stampOff = stampOn;
                bool modelOn = Forms.For(b.def).form == Form3D.Model, modelOff = true, activeOff = false, appliedOff = false;
                object was = Api.SetOption("models3D", false);
                try
                {
                    foreach (int w in Sub(ShelfStateApplied())) yield return w;
                    stampOff = View3D.Stamp;
                    appliedOff = (int)shelfApplied.GetValue(null) == stampOff;
                    modelOff = Forms.For(b.def).form == Form3D.Model;
                    activeOff = View3D.Active;
                    foreach (int w in Sub(ShelfVerify(map, "3D models off (another state of the view): all on the top at the game's places", b, ShelfExpect.OnTop(Api.SurfaceHeight(b))))) yield return w;
                }
                finally
                {
                    Api.SetOption("models3D", was);
                }
                foreach (int w in Sub(ShelfStateApplied())) yield return w;
                Check(T + "the view's state changed and came back, the 3D view on throughout", was is bool && (bool)was && modelOn && !modelOff && activeOff && View3D.Active && stampOff != stampOn
                        && View3D.Stamp == stampOn && appliedOff && (int)shelfApplied.GetValue(null) == stampOn,
                    "the option was " + was + "; the shelf drawn as its model: " + modelOn + ", with the option off " + modelOff + "; View3D.Stamp " + stampOn + ", off " + stampOff + " (taken in: " + appliedOff
                    + "), on again " + View3D.Stamp + " (taken in: " + ((int)shelfApplied.GetValue(null) == stampOn) + ")");
                foreach (int w in Sub(ShelfVerify(map, "3D models on again: back on the tiers", b))) yield return w;
            }
        }

        // ---------------------------------------------------------------- the script: fixture buildings (harness mod _ShelfTiersFix)

        // The fixture defs (tools\runners\shelftiers\mods\_ShelfTiersFix): what each one's ModelDef says, in cells on the map.
        // A model built for 2 x 1 on a 3-cell footprint is stretched 1.5 along its length and sqrt(1.5) in height.
        private const float ShelfStretch = 1.5f;
        private static readonly float ShelfStretchUp = Mathf.Sqrt(1.5f);

        private static IEnumerator<int> ShelfFixtureScript(Map map)
        {
            const string T = "shelfcheck fixtures ";
            foreach (int w in Sub(ShelfStart(map, T))) yield return w;
            bool ok = false;
            string what = null;
            Action<bool, string> note = (o, s) => { ok = o; what = s; };
            bool loaded = DefDatabase<ThingDef>.GetNamedSilentFail("STF_Deep") != null && DefDatabase<ThingStyleDef>.GetNamedSilentFail("STF_StyleList") != null;
            Check(T + "the fixture mod is loaded", loaded && ItemShelves.Hooked, "STF_Deep " + (DefDatabase<ThingDef>.GetNamedSilentFail("STF_Deep") != null) + ", hook in " + ItemShelves.Hooked);
            if (!loaded)
            {
                yield break;
            }

            // F0. The load-time check of a def's boards (ItemShelves.CheckBoards): the fixture that lists them highest first
            //     was warned about, once (asked again here: no second warning); no other def was, neither the game's
            //     shelves nor the well-formed fixtures (among them the style def without a surfaceHeight of its own whose
            //     board is over its own model's top: under the shelf's own top it rests things on).
            {
                string[] sound = { "R3D_Shelf", "R3D_ShelfSmall", "R3D_STF_Deep", "R3D_STF_Square", "R3D_STF_One", "R3D_STF_Clamp", "R3D_STF_Three", "R3D_STF_Stretch", "R3D_STF_Swap",
                    "R3D_STF_ShortRow", "R3D_STF_StyleList", "R3D_STF_StyleNoList", "R3D_STF_StyleBelow" };
                List<string> missing = sound.Where(n => DefDatabase<ModelDef>.GetNamedSilentFail(n) == null).ToList();
                List<string> wrongly = sound.Where(n => ItemShelves.BoardWarned(n)).ToList();
                int atLoad = ItemShelves.BoardWarnings;
                ModelEntry bad;
                bool have = ModelLibrary.TryGet(DefDatabase<ThingDef>.GetNamed("STF_BadOrder"), out bad) && bad.def.defName == "R3D_STF_BadOrder" && bad.HasModel;
                ItemShelves.CheckBoards(bad);
                Check(T + "boards listed highest first: warned about once at load, no other def", have && ItemShelves.BoardWarned("R3D_STF_BadOrder") && atLoad == 1 && ItemShelves.BoardWarnings == 1
                        && missing.Count == 0 && wrongly.Count == 0,
                    "R3D_STF_BadOrder warned about " + ItemShelves.BoardWarned("R3D_STF_BadOrder") + "; warnings at load " + atLoad + ", after asking again " + ItemShelves.BoardWarnings
                    + "; of " + sound.Length + " sound defs warned about: " + (wrongly.Count > 0 ? string.Join(", ", wrongly) : "none") + (missing.Count > 0 ? "; NOT LOADED: " + string.Join(", ", missing) : ""));
            }

            // F1. Deeper than wide (1 x 2): rows from the front to the back.
            foreach (Rot4 rot in new[] { Rot4.South, Rot4.East })
            {
                Building b = ShelfBuild(map, "STF_Deep", rot);
                List<IntVec3> cells = ShelfCells(b);
                foreach (int w in Sub(ShelfStock(map, b, cells[0], 3, note))) yield return w;
                bool first = ok;
                foreach (int w in Sub(ShelfStock(map, b, cells[1], 2, note, 3))) yield return w;
                Check(T + "haul onto the deep rack facing " + rot.ToStringHuman(), first && ok, what);
                foreach (int w in Sub(ShelfVerify(map, "deep rack (1 x 2) facing " + rot.ToStringHuman() + ", 3 and 2 items", b, ShelfExpect.Of(0.7f, 0.2f)))) yield return w;
            }

            // F2. Square (2 x 2): a row along its width in every cell, in the front row and in the back row.
            {
                Building b = ShelfBuild(map, "STF_Square", Rot4.South);
                List<IntVec3> cells = ShelfCells(b);   // west column south to north, then the east column
                foreach (int w in Sub(ShelfStock(map, b, cells[0], 3, note))) yield return w;
                bool first = ok;
                foreach (int w in Sub(ShelfStock(map, b, cells[3], 3, note, 3))) yield return w;
                Check(T + "haul onto the square rack", first && ok, what);
                foreach (int w in Sub(ShelfVerify(map, "square rack (2 x 2), 3 items in a front cell and 3 in the opposite back cell", b, ShelfExpect.Of(0.8f, 0.25f)))) yield return w;
            }

            // F3. One cell with row settings: the top's row 0.8 long and 0.1 back, the board's 0.6 long and 0.15 forward.
            foreach (Rot4 rot in new[] { Rot4.South, Rot4.West })
            {
                Building b = ShelfBuild(map, "STF_One", rot);
                foreach (int w in Sub(ShelfStock(map, b, b.Position, 3, note))) yield return w;
                Check(T + "haul onto the one-cell rack facing " + rot.ToStringHuman(), ok, what);
                foreach (int w in Sub(ShelfVerify(map, "one-cell rack with row settings facing " + rot.ToStringHuman(), b, ShelfExpect.Of(0.7f, 0.2f).Row(0, 0.8f, -0.1f).Row(1, 0.6f, 0.15f)))) yield return w;
            }

            // F4. Three tiers (two boards), the game's limit 5 per cell: 1 to 5 items.
            {
                Building b = ShelfBuild(map, "STF_Three", Rot4.South);
                IntVec3 cell = ShelfCells(b)[0];
                int limit = cell.GetMaxItemsAllowedInCell(map);
                List<Thing> lay = new List<Thing>();
                for (int k = 0; k < 5; k++)
                {
                    lay.Add(ShelfLay(map, b, ShelfKinds[k]));
                }
                int[][] want = { new[] { 1, 0, 0 }, new[] { 1, 1, 0 }, new[] { 1, 1, 1 }, new[] { 2, 1, 1 }, new[] { 2, 2, 1 } };
                ShelfExpect x = ShelfExpect.Of(0.95f, 0.55f, 0.15f);
                for (int k = 0; k < 5; k++)
                {
                    foreach (int w in Sub(ShelfHaul(map, lay[4 - k], cell, note))) yield return w;
                    int[] on = new int[3];
                    foreach (Thing t in ShelfItems(map, cell))
                    {
                        float rest = Api.ItemRestHeight(t);
                        on[Mathf.Abs(rest - 0.95f) < ShelfEps ? 0 : Mathf.Abs(rest - 0.55f) < ShelfEps ? 1 : 2]++;
                    }
                    Check(T + "three tiers, " + (k + 1) + " item(s): how many on each", ok && limit == 5 && on.SequenceEqual(want[k]),
                        what + "; top / middle / lowest " + string.Join(" / ", on) + ", want " + string.Join(" / ", want[k]) + "; the game's limit " + limit);
                    foreach (int w in Sub(ShelfVerify(map, "three tiers, " + (k + 1) + " item(s)", b, x))) yield return w;
                }
            }

            // F5. A model built for 2 x 1 on a 3 x 1 footprint (stretched along its length and in height): heights times
            //     sqrt(1.5); the lowest board's row 1.6 model units long = 2.4 cells.
            {
                Building b = ShelfBuild(map, "STF_Stretch", Rot4.South);
                List<IntVec3> cells = ShelfCells(b);
                foreach (int w in Sub(ShelfStock(map, b, cells[0], 3, note))) yield return w;
                bool first = ok;
                foreach (int w in Sub(ShelfStock(map, b, cells[1], 3, note, 3))) yield return w;
                Check(T + "haul onto the stretched rack", first && ok, what);
                ShelfExpect x = ShelfExpect.Of(0.95f * ShelfStretchUp, 0.55f * ShelfStretchUp, 0.15f * ShelfStretchUp).Row(2, 1.6f * ShelfStretch, 0f);
                foreach (int w in Sub(ShelfVerify(map, "stretched rack (model for 2 x 1 on 3 x 1), 3 items in an end cell and 3 in the middle cell", b, x))) yield return w;
            }

            // F6. The same model on a 1 x 3 footprint: turned a quarter to fit (its length along the building's depth), rows
            //     from the front to the back; the lowest board's row 2.4 cells, the middle board's 0.1 to the right.
            foreach (Rot4 rot in new[] { Rot4.South, Rot4.East })
            {
                Building b = ShelfBuild(map, "STF_Swap", rot);
                List<IntVec3> cells = ShelfCells(b);
                foreach (int w in Sub(ShelfStock(map, b, cells[0], 3, note))) yield return w;
                bool first = ok;
                foreach (int w in Sub(ShelfStock(map, b, cells[2], 3, note, 3))) yield return w;
                Check(T + "haul onto the turned rack facing " + rot.ToStringHuman(), first && ok, what);
                ShelfExpect x = ShelfExpect.Of(0.95f * ShelfStretchUp, 0.55f * ShelfStretchUp, 0.15f * ShelfStretchUp).Row(2, 1.6f * ShelfStretch, 0f).Row(1, -1f, 0.1f);
                foreach (int w in Sub(ShelfVerify(map, "turned rack (model for 2 x 1 on 1 x 3) facing " + rot.ToStringHuman() + ", 3 items in each end cell", b, x))) yield return w;
            }

            // F7. Style defs (ModelDef.style: another model for the game's shelf in a style). One lists boards of its own:
            //     those, not the shelf's. One lists none: no boards, all on its top at the game's places (as before Api 82).
            {
                Building b = ShelfBuild(map, "Shelf", Rot4.South, "STF_StyleList");
                foreach (int w in Sub(ShelfStock(map, b, ShelfCells(b)[0], 3, note))) yield return w;
                Check(T + "haul onto the shelf in a style with boards of its own", ok && ModelLibrary.StyledEntry(b) != null, what + "; its style has a def of its own " + (ModelLibrary.StyledEntry(b) != null));
                foreach (int w in Sub(ShelfVerify(map, "style def with its own boards: its three tiers", b, ShelfExpect.Of(0.95f, 0.55f, 0.15f)))) yield return w;

                Building c = ShelfBuild(map, "Shelf", Rot4.South, "STF_StyleNoList");
                foreach (int w in Sub(ShelfStock(map, c, ShelfCells(c)[0], 3, note))) yield return w;
                Check(T + "haul onto the shelf in a style without boards", ok && ModelLibrary.StyledEntry(c) != null, what + "; its style has a def of its own " + (ModelLibrary.StyledEntry(c) != null));
                foreach (int w in Sub(ShelfVerify(map, "style def with a model and no boards: all on its top (0.95) at the game's places", c, ShelfExpect.OnTop(0.95f)))) yield return w;

                Building d = ShelfBuild(map, "Shelf", Rot4.South);
                foreach (int w in Sub(ShelfStock(map, d, ShelfCells(d)[0], 3, note))) yield return w;
                foreach (int w in Sub(ShelfVerify(map, "the same shelf in no style: its own board", d, ShelfExpect.Of(0.6f, 0.14f)))) yield return w;

                // a style def that sets no surfaceHeight: things rest on the shelf's own top (0.6), not on its low model's
                // (0.4); its boards at 0.15 and 0.5 are both under that top
                Building low = ShelfBuild(map, "Shelf", Rot4.South, "STF_StyleBelow");
                foreach (int w in Sub(ShelfStock(map, low, ShelfCells(low)[0], 3, note))) yield return w;
                Check(T + "haul onto the shelf in a style without a top of its own", ok && ModelLibrary.StyledEntry(low) != null && ModelLibrary.StyledEntry(low).def.surfaceHeight < 0f,
                    what + "; its style has a def of its own " + (ModelLibrary.StyledEntry(low) != null));
                foreach (int w in Sub(ShelfVerify(map, "style def without a surfaceHeight: the shelf's own top (0.6), its boards at 0.5 and 0.15", low, ShelfExpect.Of(0.6f, 0.5f, 0.15f)))) yield return w;
            }

            // F8. A row that would leave its cell (the top's 3 cells long on a one-cell rack, the board's 0.9 forward):
            //     each item's centre stops inside its own cell.
            {
                Building b = ShelfBuild(map, "STF_Clamp", Rot4.South);
                IntVec3 cell = b.Position;
                foreach (int w in Sub(ShelfStock(map, b, cell, 3, note))) yield return w;
                Check(T + "haul onto the rack with rows too long", ok, what);
                foreach (int w in Sub(ShelfVerify(map, "rows too long for the cell: centres kept inside it", b, ShelfExpect.Of(0.7f, 0.2f).Row(0, 3f, 0f).Row(1, -1f, 0.9f)))) yield return w;
                List<Thing> items = ShelfItems(map, cell);
                Vector3 a0 = items[0].TrueCenter(), a1 = items[1].TrueCenter(), a2 = items[2].TrueCenter();
                Check(T + "rows too long: at the cell's edges, in plain numbers", Mathf.Abs(a0.x - (cell.x + ShelfEdge)) < ShelfEps && Mathf.Abs(a1.x - (cell.x + 1f - ShelfEdge)) < ShelfEps
                        && Mathf.Abs(a2.z - (cell.z + ShelfEdge)) < ShelfEps && items.All(t => t.TrueCenter().ToIntVec3() == cell),
                    "cell " + cell + ": top items at " + P2(a0) + " and " + P2(a1) + ", the board's at " + P2(a2));
            }

            // F9. A row shorter than a building of three cells (the lowest board's: 0.6 model units = 0.9 cells, all of it
            //     over the middle cell): the end cells' items of that board stop at their own cell's inner edge, and the
            //     pointer finds them there, where they are drawn (not over the middle cell, where their share of the row is).
            {
                Building b = ShelfBuild(map, "STF_ShortRow", Rot4.South);
                List<IntVec3> cells = ShelfCells(b);   // west to east
                foreach (int w in Sub(ShelfStock(map, b, cells[0], 3, note))) yield return w;
                bool first = ok;
                string firstSaid = what;
                foreach (int w in Sub(ShelfStock(map, b, cells[2], 3, note, 3))) yield return w;
                Check(T + "haul onto the rack with a short lowest row", first && ok && cells.Count == 3, firstSaid + "; " + what);
                ShelfExpect x = ShelfExpect.Of(0.95f * ShelfStretchUp, 0.55f * ShelfStretchUp, 0.15f * ShelfStretchUp).Row(2, 0.6f * ShelfStretch, 0f);
                foreach (int w in Sub(ShelfVerify(map, "short lowest row (0.9 cells on 3 cells), 3 items in each end cell", b, x))) yield return w;
                Thing westLow = ShelfItems(map, cells[0])[2], eastLow = ShelfItems(map, cells[2])[2];
                Vector3 westAt = westLow.TrueCenter(), eastAt = eastLow.TrueCenter();
                Check(T + "short lowest row: the end cells' items at their cell's inner edge, in plain numbers", Mathf.Abs(westAt.x - (cells[0].x + 1f - ShelfEdge)) < ShelfEps
                        && Mathf.Abs(eastAt.x - (cells[2].x + ShelfEdge)) < ShelfEps && Mathf.Abs(Api.ItemRestHeight(westLow) - 0.15f * ShelfStretchUp) < ShelfEps,
                    "cells " + cells[0] + " and " + cells[2] + ": the lowest board's items at " + P2(westAt) + " and " + P2(eastAt));
                foreach (int w in Sub(ShelfAlone(map, b))) yield return w;
                foreach (int w in Sub(ShelfLook(map, b.TrueCenter(), ShelfNear))) yield return w;
                foreach (int w in Sub(ShelfSettle(map, b))) yield return w;
                foreach (Thing low in new[] { westLow, eastLow })
                {
                    // at the rack's open front (the boards over it hide it from above), a little inside its own cell
                    Vector3 p = ShelfPoint(low, 0.38f);
                    p.x += low == westLow ? -0.05f : 0.05f;
                    List<object> one = null, two = null;
                    foreach (int w in Sub(ShelfClick(p, g => one = g))) yield return w;
                    foreach (int w in Sub(ShelfClick(p, g => two = g, true))) yield return w;
                    List<Thing> picks = Interaction.PickUnderPointer(map);
                    Check(T + "short lowest row: click on the " + (low == westLow ? "west" : "east") + " cell's item where it is drawn",
                        one.Count == 1 && one[0] == b && two.Count == 1 && two[0] == low && picks.Count > 0 && picks[0] == low,
                        "pointer at " + p + ": first click " + string.Join(", ", one.Select(Desc)) + ", second click " + string.Join(", ", two.Select(o => o is Thing ? ShelfName((Thing)o) : Desc(o)))
                        + ", wanted " + ShelfName(low) + "; 3D pick " + string.Join(", ", picks.Take(5).Select(t => ShelfName(t))));
                }
                fakeMouse = null;
                Find.Selector.ClearSelection();
                foreach (int w in Sub(ShelfLookAll(map))) yield return w;
            }
        }

        // ---------------------------------------------------------------- the scripts: capacity, no hook

        // With a harness mod that sets the shelf's maxItemsInCell: the game's own limit and count are followed.
        private static IEnumerator<int> ShelfCapScript(Map map, int cap)
        {
            string T = "shelfcheck cap " + cap + " ";
            foreach (int w in Sub(ShelfStart(map, T))) yield return w;
            Building b = ShelfBuild(map, "Shelf", Rot4.South);
            List<IntVec3> cells = ShelfCells(b);
            int limit = cells[0].GetMaxItemsAllowedInCell(map);
            Check(T + "the game's limit", limit == cap && ItemShelves.Hooked, "GetMaxItemsAllowedInCell " + limit + " (ThingDef maxItemsInCell " + b.def.building.maxItemsInCell + "), wanted " + cap);
            List<Thing> lay = new List<Thing>();
            for (int k = 0; k < limit && k < ShelfKinds.Length; k++)
            {
                lay.Add(ShelfLay(map, b, ShelfKinds[k]));
            }
            for (int k = 0; k < lay.Count; k++)
            {
                bool ok = false;
                string what = null;
                foreach (int w in Sub(ShelfHaul(map, lay[lay.Count - 1 - k], cells[0], (o, s) => { ok = o; what = s; }))) yield return w;
                Check(T + "haul " + (k + 1) + " of " + lay.Count, ok, what);
                foreach (int w in Sub(ShelfVerify(map, "limit " + cap + ": " + (k + 1) + " item(s) in the cell", b))) yield return w;
            }
            // one more than the game allows: the game itself refuses the cell
            {
                Thing extra = ShelfLay(map, b, ShelfKinds[Mathf.Min(limit, ShelfKinds.Length - 1)]);
                bool ok = false;
                string what = null;
                foreach (int w in Sub(ShelfHaul(map, extra, cells[0], (o, s) => { ok = o; what = s; }))) yield return w;
                Check(T + "one more than the limit is refused by the game", !ok && ShelfItems(map, cells[0]).Count == limit, what);
            }
            foreach (int w in Sub(ShelfLook(map, b.TrueCenter(), ShelfNear))) yield return w;
            foreach (int w in Sub(ShelfSettle(map, b))) yield return w;
            yield return 5;
            ShelfShot("shelf_cap" + cap);
            yield return 5;
        }

        // Run with -isorimshelfhook=0: the hook is not in, and stored items are drawn as before Api 82.
        private static IEnumerator<int> ShelfNoHookScript(Map map)
        {
            const string T = "shelfcheck nohook ";
            foreach (int w in Sub(ShelfStart(map, T))) yield return w;
            Check(T + "the hook is left out", !ItemShelves.Hooked && ShelfHookTestArg.Off, "hooked " + ItemShelves.Hooked + ", -" + ShelfHookTestArg.Arg + "=0 given " + ShelfHookTestArg.Off);
            Building b = ShelfBuild(map, "Shelf", Rot4.South);
            List<IntVec3> cells = ShelfCells(b);
            List<Thing> a = new List<Thing> { ShelfLay(map, b, "Steel"), ShelfLay(map, b, "Cloth"), ShelfLay(map, b, "MedicineIndustrial") };
            List<Thing> c = new List<Thing> { ShelfLay(map, b, "Gun_BoltActionRifle"), ShelfLay(map, b, "Gun_Revolver") };
            bool ok = false, ok2 = false;
            string what = null, what2 = null;
            foreach (int w in Sub(ShelfBring(map, cells[0], a, (o, s) => { ok = o; what = s; }))) yield return w;
            foreach (int w in Sub(ShelfBring(map, cells[1], c, (o, s) => { ok2 = o; what2 = s; }))) yield return w;
            Check(T + "haul", ok && ok2, what + "; " + what2);
            foreach (int w in Sub(ShelfVerify(map, "nohook: all on the top at the game's places", b, ShelfExpect.OnTop(Api.SurfaceHeight(b))))) yield return w;
        }
    }
}
