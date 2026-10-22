using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness keys for the cost of items on the ground (GroundGear): a stockpile filled with apparel, one map block rebuilt and
    // timed, and the real flow of colonists hauling into a nearly full stockpile (timed with perf= while the game runs).
    public static partial class Probe
    {
        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> sectionLayers = AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");

        private static bool GroundKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "groundfill":
                {
                    // groundfill=x0,z0,w,h,Def;Def;...: one of those things (default stuff) on every cell of the rectangle, the defs in turn.
                    Rand.PushState(7101);
                    int n = Fill(map, CellRect.FromLimits(I(a[0]), I(a[1]), I(a[0]) + I(a[2]) - 1, I(a[1]) + I(a[3]) - 1).Cells, a[4].Split(';'), int.MaxValue);
                    Rand.PopState();
                    Log.Message("[IsoRim probe] groundfill " + n + " things");
                    return true;
                }
                case "sectiontime":
                {
                    // sectiontime=x,z,reps[,label]: the map block holding cell x,z rebuilt reps times (all layers), the mean time logged.
                    Section sect = map.mapDrawer.SectionAt(new IntVec3(I(a[0]), 0, I(a[1])));
                    int reps = Mathf.Max(1, I(a[2]));
                    sect.RegenerateAllLayers();
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int k = 0; k < reps; k++)
                    {
                        sect.RegenerateAllLayers();
                    }
                    sw.Stop();
                    int items = 0;
                    foreach (IntVec3 sc in sect.CellRect)
                    {
                        items += sc.GetItemCount(map);
                    }
                    Log.Message("[IsoRim probe] sectiontime " + (a.Length > 3 ? a[3] : "") + " block " + sect.botLeft + ": " + items + " items, "
                        + (sw.Elapsed.TotalMilliseconds / reps).ToString("0.00") + " ms per rebuild (all layers, mean of " + reps + "); mesh verts " + SectionVerts(sect));
                    return true;
                }
                case "haulflow":
                {
                    // haulflow=x0,z0,w,h,loose,Def;Def;...: a stockpile on the rectangle filled with those things (default stuff) but for
                    // its last loose cells; loose more of them lie in a row two cells south of it, and three new colonists (fixed seed)
                    // who only haul and want no clothes stand beside them. Let the game run (speed=, perf=): they carry them in.
                    HaulFlow(map, CellRect.FromLimits(I(a[0]), I(a[1]), I(a[0]) + I(a[2]) - 1, I(a[1]) + I(a[3]) - 1), I(a[4]), a[5].Split(';'));
                    return true;
                }
                case "haulcount":
                {
                    // haulcount=x0,z0,w,h[,label]: how many things lie on the rectangle (did the hauling finish?).
                    int items = 0;
                    foreach (IntVec3 c in CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3])))
                    {
                        items += c.GetItemCount(map);
                    }
                    Log.Message("[IsoRim probe] haulcount " + (a.Length > 4 ? a[4] : "") + ": " + items + " things");
                    return true;
                }
            }
            return false;
        }

        // Spawns the defs in turn (default stuff, not forbidden) on the cells, at most max of them; how many it spawned.
        private static int Fill(Map map, IEnumerable<IntVec3> cells, string[] defs, int max)
        {
            int n = 0;
            foreach (IntVec3 c in cells)
            {
                if (n >= max)
                {
                    break;
                }
                ThingDef d = DefDatabase<ThingDef>.GetNamed(defs[n++ % defs.Length]);
                Thing made = ThingMaker.MakeThing(d, d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null);
                made.thingIDNumber = 9000000 + fillIds++;   // the same id every run: the game turns an item on the ground by its id
                Thing t = GenSpawn.Spawn(made, c, map);
                t.SetForbidden(false, false);
            }
            return n;
        }

        private static void HaulFlow(Map map, CellRect rect, int loose, string[] defs)
        {
            Rand.PushState(7102);
            try
            {
                Zone_Stockpile z = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(z);
                List<IntVec3> cells = new List<IntVec3>();
                foreach (IntVec3 c in rect)
                {
                    if (map.zoneManager.ZoneAt(c) == null && c.Standable(map))
                    {
                        z.AddCell(c);
                        cells.Add(c);
                    }
                }
                z.settings.filter.SetAllowAll(null);
                int filled = Fill(map, cells, defs, cells.Count - loose);
                List<IntVec3> row = new List<IntVec3>();
                for (int i = 0; i < loose; i++)
                {
                    row.Add(new IntVec3(rect.minX + i % rect.Width, 0, rect.minZ - 2 - i / rect.Width));
                }
                int spread = Fill(map, row, defs, loose);

                ApparelPolicy none = Current.Game.outfitDatabase.MakeNewOutfit();
                none.filter.SetDisallowAll();
                int made = 0;
                for (int seed = 0; made < 3 && seed < 50; seed++)
                {
                    Rand.PushState(7200 + seed);
                    Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                        fixedBiologicalAge: 30f, fixedChronologicalAge: 30f, developmentalStages: DevelopmentalStage.Adult));
                    Rand.PopState();
                    if (p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                    {
                        p.Destroy(DestroyMode.Vanish);
                        continue;
                    }
                    p.workSettings.EnableAndInitialize();
                    foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                    {
                        if (!p.WorkTypeIsDisabled(w))
                        {
                            p.workSettings.SetPriority(w, w == WorkTypeDefOf.Hauling ? 1 : 0);
                        }
                    }
                    p.outfits.CurrentApparelPolicy = none;
                    GenSpawn.Spawn(p, new IntVec3(rect.minX + rect.Width / 2 + made, 0, rect.minZ - 4), map, Rot4.North);
                    p.drafter.Drafted = false;
                    made++;
                }
                Log.Message("[IsoRim probe] haulflow: stockpile " + cells.Count + " cells, " + filled + " filled, " + spread + " loose, " + made + " haulers");
            }
            finally
            {
                Rand.PopState();
            }
        }

        // Every vertex of a map block's meshes, all layers.
        private static int fillIds;

        private static int SectionVerts(Section sect)
        {
            int n = 0;
            foreach (SectionLayer l in sectionLayers(sect))
            {
                foreach (LayerSubMesh sm in l.subMeshes)
                {
                    n += sm.mesh != null ? sm.mesh.vertexCount : 0;
                }
            }
            return n;
        }
    }
}
