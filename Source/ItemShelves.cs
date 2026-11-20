using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 82) Items stored on a building whose model has boards for them (ModelDef.itemShelves): they stand on the boards
    // and the top, spread along the building, instead of all on the top where the game puts them. THE one place for the
    // rule; everything that draws, labels or picks an item asks here (through Forms.BaseUnder and the game's own item
    // centre).
    //
    // Which building: a storage building (the game's Building_Storage) drawn as its model whose ModelDef lists boards
    //   (Boards). A thing in a style that has a def of its own (ModelDef.style: another model) has only that def's
    //   boards, none if it lists none; a style model inside the thing's def (byStyle) has the def's. Never a shape drawn
    //   in place of a model (ModelDef.shape). Its tiers are the top (Forms.SurfaceOf, tier 0) and the boards under it,
    //   going down.
    // Which tier, which place (Slot): the n items of the item's cell (Count: counted and ordered as the game does) are
    //   split evenly over the tiers, the upper tiers taking the extra ones (two tiers: the top half rounded up), and
    //   handed out in the game's order, the top first. So 1 item is on the top, and any n and any number of tiers work.
    // Where (Row): each tier's items of a cell stand in one row along the building's long side (a square one: its width),
    //   from the left as seen from its front, turned with the building (the game's own rotation). The row is as long as
    //   the building (or the board's width), centred on it; every cell has its share of it and its k items stand at
    //   (i + 0.5) / k of that share, in the middle of the cell's depth (or moved toward the front by the board's forward).
    //   A building deeper than wide has its rows from the front to the back, and forward moves them to its right.
    //   An item's centre never leaves its own cell (SpotOf), since the game and IsoRim take an item's cell from its centre:
    //   where its share of the row lies outside its cell it stops at the cell's edge. That happens with a row longer
    //   than the building, a row moved far, and also with a row shorter than a building of three or more cells (the end
    //   cells' shares then lie over the middle cells); several items of a cell can then stand on one spot.
    // Readers:
    //   - Forms.BaseUnder (RestOn): the height. Every drawer of an item reads it there.
    //   - Patches.ItemCenterAt_Postfix (Place): the place, given as the game's own answer for an item's centre (x and z
    //     only; the game's y is kept), so the map mesh, items drawn every frame, labels and overlays follow.
    //   - Interaction.PickUnderPointer (PickSample, Pick): the pointer picks an item where it stands (SpotOf, as Place).
    //   - Patches.LabelDrawPosFor_Prefix (LabelRise): an item's label (stack count, quality) at its board.
    //   - Api.ItemRestHeight.
    // Off, and then exactly as before (all on the top, the game's places): the 3D view off; no boards; the hook on the
    //   game's item centre not in (Hooked false: boards with the game's places would be a look of its own).
    // Threads: the game asks an item's centre off the main thread too (its parallel pre-draw places a corpse's body:
    //   DynamicDrawManager.PreDrawThings), and the answer must be the same on every thread. So Place reads nothing that
    //   is written while the game draws: which defs have boards comes from a table made on the main thread and swapped
    //   in whole (Models; Forms' own cache is the main thread's); the rest are plain reads of the map's grids, the
    //   thing's fields and the model entries, which are written at load. The height (RestOn) is asked on the main thread.
    // Redraw: an item coming or going changes where the cell's other items stand. The game marks the cell's block for the
    //   items it prints into the map mesh (Thing.DirtyMapMesh), not for those it draws every frame: CellChanged marks it
    //   for every item on such a building. A stack growing or shrinking changes neither n nor the order: nothing moves.
    // Cost: no allocation (the table: once per change of the view's state). A storage building without boards: one
    //   table lookup in RestOn. The hook, for every spawned item whose centre is asked: the view on, one cell lookup
    //   (items on the ground or in a stockpile end there), one table lookup. On boards: the cell's thing list walked
    //   twice (the game's count, then the order), one style and fit read, a few sums.
    public static class ItemShelves
    {
        // An item drawn thinner than this is picked up to this far over its board: one step of the pointer's ray.
        private const float PickRise = Interaction.PickStep;

        // An item's centre stays this far inside its own cell (Place).
        private const float CellEdge = 0.001f;

        // CopyHash of GenThing.ItemCenterAt when Count's order was copied from it (RimWorld 1.7.5014); checked once loading
        // is done (CopyWatch: one warning if the game changed it).
        internal const string CountSourceHash = "6449477487b6b051";

        // The hook on the game's item centre is in (Patches). False: the whole rule is off.
        internal static bool Hooked;

        // ---- which buildings

        // The storage defs whose buildings are drawn as a model and have boards (their def's, or some style def's of
        // theirs), with the entry they are drawn from: Forms' answer per def, read on the main thread for one state of the
        // view (View3D.Stamp, on or off) and never changed after it is swapped in, so any thread may read it.
        private sealed class Shelved
        {
            public int stamp;
            public bool active;
            public readonly Dictionary<ThingDef, ModelEntry> models = new Dictionary<ThingDef, ModelEntry>();
        }

        private static volatile Shelved shelved = new Shelved { stamp = int.MinValue };

        private static Dictionary<ThingDef, ModelEntry> Models()
        {
            Shelved s = shelved;
            if ((s.stamp != View3D.Stamp || s.active != View3D.Active) && UnityData.IsInMainThread)
            {
                s = new Shelved { stamp = View3D.Stamp, active = View3D.Active };
                if (Hooked && s.active)
                {
                    List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
                    for (int i = 0; i < defs.Count; i++)
                    {
                        ThingDef def = defs[i];
                        if (def.thingClass == null || !typeof(Building_Storage).IsAssignableFrom(def.thingClass))
                        {
                            continue;
                        }
                        try
                        {
                            ThingForm f = Forms.For(def);
                            if (f.form == Form3D.Model && f.model != null && (HasBoards(f.model.def) || ModelLibrary.StyleHasItemShelves(def)))
                            {
                                s.models[def] = f.model;
                            }
                        }
                        catch (Exception e)
                        {
                            // (left out: its items on its top as before; the table is made all the same, or every frame
                            // would try again and stop View3D's own work behind it)
                            Log.ErrorOnce("[IsoRim] Reading how " + def.defName + " is drawn failed; its stored items stay on its top: " + e, Gen.HashCombineInt(93682006, def.shortHash));
                        }
                    }
                }
                shelved = s;
            }
            return s.models;
        }

        // View3D, every frame on the main thread right after its check of the view's state: from here on the table is
        // that state's. Nothing more is promised: a thread that cannot make the table reads the one there is, so the
        // game's parallel pre-draw may still read the old one in the frame of a change (and main-thread readers before
        // this call make the new one themselves). Never throws (a def that cannot be read is left out, Models).
        internal static void Sync()
        {
            Models();
        }

        // (a shape in place of a model, Api 52: a solid box, no boards; the binder makes one from a clone of the model's def)
        internal static bool HasBoards(ModelDef md)
        {
            return md.itemShelves != null && md.itemShelves.Count > 0 && md.shape == ModelShape.None;
        }

        // The entry whose def lists the boards of building b; null without boards (not a storage building drawn as a
        // model, or no list). A thing in a style with a def of its own is drawn as that def's model: only its list counts.
        private static ModelEntry Boards(Building b)
        {
            ModelEntry drawn;
            if (!Models().TryGetValue(b.def, out drawn))
            {
                return null;
            }
            ModelEntry e = ModelLibrary.StyledEntry(b) ?? drawn;
            return HasBoards(e.def) ? e : null;
        }

        // The storage building with boards a spawned item is stored on, and the entry that lists them; null without one.
        private static Building Holder(Thing item, out ModelEntry e)
        {
            e = null;
            if (!Hooked || !View3D.Active || item.def.category != ThingCategory.Item)
            {
                return null;
            }
            Map map = item.Map;
            if (map == null || !item.Position.InBounds(map))
            {
                return null;
            }
            Building b = item.Position.GetEdifice(map);
            if (b == null)
            {
                return null;
            }
            e = Boards(b);
            return e != null ? b : null;
        }

        // ModelLibrary, as an entry is made: one warning per def whose boards are not listed lowest first, each below
        // the top (the tiers are read in that order). Not for an entry whose model did not load (nothing is drawn from
        // it, and its top is unknown). A style def that sets no surfaceHeight of its own rests things on the thing's own
        // surface (Forms.SurfaceOf), which is not known here: only its order is checked.
        private static readonly HashSet<string> warned = new HashSet<string>();

        internal static void CheckBoards(ModelEntry entry)
        {
            ModelDef md = entry != null ? entry.def : null;
            if (md == null || !HasBoards(md) || !entry.HasModel)
            {
                return;
            }
            bool ownTop = md.style.NullOrEmpty() || md.surfaceHeight >= 0f;
            float top = entry.SurfaceHeight, last = 0f;
            bool ok = true;
            for (int i = 0; i < md.itemShelves.Count; i++)
            {
                ModelItemShelf board = md.itemShelves[i];
                ok &= board != null && board.height >= last && (i == 0 || board.height > last) && (!ownTop || board.height < top);
                last = board != null ? board.height : last;
            }
            if (ok || md.defName == null || !warned.Add(md.defName))
            {
                return;
            }
            List<string> heights = new List<string>();
            for (int i = 0; i < md.itemShelves.Count; i++)
            {
                heights.Add(md.itemShelves[i] != null ? md.itemShelves[i].height.ToString("0.###") : "(empty)");
            }
            ModLog.Warn("[IsoRim] ModelDef " + md.defName + ": itemShelves must list the boards lowest first" + (ownTop ? ", each below the top (" + top.ToString("0.###") + ", surfaceHeight)" : "")
                + "; it lists " + string.Join(", ", heights) + ". Items may be drawn on the wrong board.");
            BoardWarnings++;
        }

        // The harness: board warnings logged so far, and whether one was logged for a def.
        internal static int BoardWarnings;

        internal static bool BoardWarned(string modelDefName)
        {
            return modelDefName != null && warned.Contains(modelDefName);
        }

        // ---- the rule

        // How many items the item's cell holds and how many of them come before it. n is the game's own count
        // (GridsUtility.GetItemCount, which its 0.8 size for several items in a cell asks too). The order is copied from
        // RimWorld GenThing.ItemCenterAt (1.7.5014): of the cell's things (GridsUtility.GetThingList) with def.category
        // Item, one comes before another by its lower thingIDNumber (the game keeps that in a local, with no getter).
        // Watched (CopyWatch).
        private static void Count(Thing item, out int n, out int before)
        {
            Map map = item.Map;
            IntVec3 cell = item.Position;
            n = cell.GetItemCount(map);
            before = 0;
            List<Thing> things = cell.GetThingList(map);
            int id = item.thingIDNumber;
            for (int i = 0; i < things.Count; i++)
            {
                Thing o = things[i];
                if (o.def.category == ThingCategory.Item && o.thingIDNumber < id)
                {
                    before++;
                }
            }
        }

        // n items over the tiers (0 the top): each takes n / tiers, the upper n % tiers of them one more; the items go to
        // the top first. tier: where the item with `before` items ahead of it rests; index: its place in that tier's row
        // of inRow items.
        internal static void Split(int n, int before, int tiers, out int tier, out int index, out int inRow)
        {
            int each = n / tiers, extra = n % tiers, first = 0;
            for (tier = 0; tier < tiers; tier++)
            {
                inRow = each + (tier < extra ? 1 : 0);
                if (before < first + inRow)
                {
                    index = before - first;
                    return;
                }
                first += inRow;
            }
            tier = 0;   // (not one of the cell's items: alone on the top)
            index = 0;
            inRow = 1;
        }

        // Where an item on a building with these tiers rests: its tier and its place in the row.
        private static void Slot(Thing item, int tiers, out int tier, out int index, out int inRow)
        {
            int n, before;
            Count(item, out n, out before);
            Split(n, before, tiers, out tier, out index, out inRow);
        }

        // The height of a tier of building b (form f, boards from e), cells over the ground. Main thread (Forms).
        private static float TierHeight(Building b, ThingForm f, ModelEntry e, int tier)
        {
            List<ModelItemShelf> boards = e.def.itemShelves;
            return tier == 0 ? Forms.SurfaceOf(b, f) : Mathf.Max(0f, boards[boards.Count - tier].height) * e.Fit(b.def).y;
        }

        // A place in a tier's row, on the map.
        private struct RowAt
        {
            public Vector3 centre;   // the building's middle
            public IntVec3 along;    // the row's direction
            public IntVec3 side;     // across the row: the way the building faces (a deep one: its right)
            public float from, to;   // the item's share of the row: cells from the middle, along
            public float across;     // the row: cells from the middle, along side
        }

        // The share of a tier's row that the index-th of inRow items of a cell has. Any thread.
        private static RowAt Row(Building b, ModelEntry e, int tier, IntVec3 cell, int index, int inRow)
        {
            ModelDef md = e.def;
            ThingDef def = b.def;
            Rot4 rot = b.Rotation;
            bool wide = def.size.x >= def.size.z;   // the long side, as placed facing north: across (x) or deep (z)
            float width = md.itemTopWidth, forward = md.itemTopForward;
            if (tier > 0)
            {
                ModelItemShelf board = md.itemShelves[md.itemShelves.Count - tier];
                width = board.width;
                forward = board.forward;
            }
            // (the model's own axis along the row: its x, unless it is turned a quarter to fit the footprint)
            Vector3 fit = e.Fit(def);
            bool modelX = wide != e.Swapped(def);
            int cells = wide ? def.size.x : def.size.z;
            float length = width >= 0f ? width * (modelX ? fit.x : fit.z) : cells;

            RowAt r;
            r.centre = GenThing.TrueCenter(b.Position, rot, def.size, 0f);
            // from the left as seen from the front (a deep one: from the front to the back), turned as the game turns it
            r.along = (wide ? IntVec3.West : IntVec3.South).RotatedBy(rot);
            r.side = (wide ? IntVec3.North : IntVec3.East).RotatedBy(rot);
            Vector3 mid = cell.ToVector3Shifted();
            float dx = mid.x - r.centre.x, dz = mid.z - r.centre.z;
            float step = dx * r.along.x + dz * r.along.z + (cells - 1) * 0.5f;   // which of the row's cells: 0 the first
            r.from = length * ((step + index / (float)inRow) / cells - 0.5f);
            r.to = length * ((step + (index + 1) / (float)inRow) / cells - 0.5f);
            r.across = dx * r.side.x + dz * r.side.z + forward * (modelX ? fit.z : fit.x);
            return r;
        }

        // Where an item stands: the middle of its share of the row, kept inside its own cell, and how far its share
        // reaches either side of that spot along the row. THE one answer for where it is drawn (Place) and where the
        // pointer finds it (Pick). Any thread.
        private struct SpotAt
        {
            public float x, z;      // on the map
            public IntVec3 along;   // the row's direction
            public float half;      // half its share of the row, cells
        }

        private static SpotAt SpotOf(Building b, ModelEntry e, int tier, IntVec3 cell, int index, int inRow)
        {
            RowAt r = Row(b, e, tier, cell, index, inRow);
            float along = (r.from + r.to) * 0.5f;
            SpotAt s;
            s.x = Mathf.Clamp(r.centre.x + r.along.x * along + r.side.x * r.across, cell.x + CellEdge, cell.x + 1f - CellEdge);
            s.z = Mathf.Clamp(r.centre.z + r.along.z * along + r.side.z * r.across, cell.z + CellEdge, cell.z + 1f - CellEdge);
            s.along = r.along;
            s.half = (r.to - r.from) * 0.5f;
            return s;
        }

        // ---- readers

        // Forms.BaseUnder, for an item on storage building b drawn as model form f: the height it rests at.
        internal static float RestOn(Thing item, Building b, ThingForm f)
        {
            ModelEntry e = Hooked && item.def.category == ThingCategory.Item ? Boards(b) : null;
            if (e == null)
            {
                return Forms.SurfaceOf(b, f);
            }
            int tier, index, inRow;
            Slot(item, e.def.itemShelves.Count + 1, out tier, out index, out inRow);
            return TierHeight(b, f, e, tier);
        }

        // Patches.ItemCenterAt_Postfix: the game's centre of a spawned item moved to its place in its row (x and z; SpotOf:
        // kept inside the item's own cell). Any thread (see Threads above).
        internal static void Place(Thing item, ref Vector3 at)
        {
            ModelEntry e;
            Building b = Holder(item, out e);
            if (b == null)
            {
                return;
            }
            int tier, index, inRow;
            Slot(item, e.def.itemShelves.Count + 1, out tier, out index, out inRow);
            SpotAt s = SpotOf(b, e, tier, item.Position, index, inRow);
            at.x = s.x;
            at.z = s.z;
        }

        // Patches.LabelDrawPosFor_Prefix: how high the label of a thing is raised: an item on boards has its label at its own
        // board (the labels of a cell's items would else all stand in one line on the ground under the shelf, the two
        // tiers' mixed). 0 for anything else: its label where it was.
        internal static float LabelRise(Thing t)
        {
            try
            {
                ModelEntry e;
                return t.Spawned && Holder(t, out e) != null ? Forms.BaseUnder(t, true) : 0f;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[IsoRim] Raising a stored item's label to its shelf's board failed: " + ex, 93682002);
                return 0f;
            }
        }

        // Patches.SpawnSetup_Postfix / DeSpawn_Prefix: an item came to a cell or is about to leave it. On a building with
        // boards the cell's other items stand elsewhere now: its block is marked as the game marks it for the items it
        // prints (for those the mark is made twice, which costs nothing; for an item drawn every frame only here).
        internal static void CellChanged(Thing item, Map map)
        {
            if (!Hooked || !View3D.Active || map == null || item.def.category != ThingCategory.Item || Current.ProgramState != ProgramState.Playing
                || !item.Position.InBounds(map))
            {
                return;
            }
            Building b = item.Position.GetEdifice(map);
            if (b != null && Boards(b) != null)
            {
                map.mapDrawer.MapMeshDirty(item.Position, MapMeshFlagDefOf.Things);
            }
        }

        // ---- the pointer (Interaction.PickUnderPointer: one line of sight, sampled from high to low)

        private static Building rayHolder;   // the building with boards the line is in now
        private static float rayEntry;       // the height it came into that building at

        // Before a line's first sample, and when PickUnderPointer is done (nothing of a map is kept).
        internal static void PickStart()
        {
            rayHolder = null;
        }

        // Every sample of the line (cell c, height h), before its things are tested.
        internal static void PickSample(Map map, IntVec3 c, float h)
        {
            if (!Hooked)
            {
                return;
            }
            try
            {
                Building b = c.GetEdifice(map);
                if (b == rayHolder)
                {
                    return;
                }
                rayHolder = null;
                if (b != null && Boards(b) != null)
                {
                    rayHolder = b;
                    rayEntry = h;
                }
            }
            catch (Exception e)
            {
                rayHolder = null;   // (this building's items are picked by the pointer's own rule)
                Log.ErrorOnce("[IsoRim] Finding a shelf's boards under the pointer failed: " + e, 93682003);
            }
        }

        // A thing of the sample's cell. False: not an item on boards (the caller's own rule). True: hit says whether the
        // line at point p (x, z) and height h is on the item: within half its share of the row either side of where it
        // stands (SpotOf: the place it is drawn at, also where that is not the middle of its share), between its board and its top
        // (pickTop; at least PickRise), and not come down through the board over it (the line came into the building
        // under that board). holder: the building, which the pointer finds first (its body starts at the ground): what
        // stands on its boards is in front of it.
        internal static bool Pick(Thing item, Vector3 p, float h, float pickTop, out bool hit, out Building holder)
        {
            hit = false;
            holder = rayHolder;
            if (holder == null || item.def.category != ThingCategory.Item)
            {
                return false;
            }
            try
            {
                ModelEntry e = Boards(holder);
                if (e == null)
                {
                    return false;
                }
                ThingForm f = Forms.For(holder.def);
                int tier, index, inRow;
                Slot(item, e.def.itemShelves.Count + 1, out tier, out index, out inRow);
                float board = TierHeight(holder, f, e, tier);
                if (h < board || h > Mathf.Max(pickTop, board + PickRise) || (tier > 0 && rayEntry >= TierHeight(holder, f, e, tier - 1)))
                {
                    return true;
                }
                SpotAt s = SpotOf(holder, e, tier, item.Position, index, inRow);
                hit = Mathf.Abs((p.x - s.x) * s.along.x + (p.z - s.z) * s.along.z) <= s.half;
                return true;
            }
            catch (Exception e)
            {
                hit = false;
                Log.ErrorOnce("[IsoRim] Picking an item on a shelf's board failed: " + e, 93682004);
                return false;   // (the pointer's own rule for this thing)
            }
        }
    }
}
