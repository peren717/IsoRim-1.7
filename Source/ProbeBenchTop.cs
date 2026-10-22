using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for items on work tables and tables (BenchTops.cs). Cells are map cells. The bills are worked the
    // game's way: a bill on the bench, the colonist given the job its work giver makes (as the player's "prioritize"),
    // real ticks until the ingredients are brought.
    //   benchtops=0|1                items on furniture drawn on its top (BenchTops.On, default 1) or on the ground as
    //                                before (before/after shots); the map mesh is rebuilt
    //   bill=bx,bz,<RecipeDef>,px,pz the bench in (bx,bz) gets that bill for the colonist in (px,pz) only (made able: skills
    //                                10, undrafted), who is given the bill's job (WorkGiver_DoBill.JobOnThing, forced)
    //   tickbench=N                  game ticks (at most N) until every bill= colonist works at its bench with all
    //                                ingredients brought (no queue left, hands empty, on the interaction spot); a colonist
    //                                already working keeps its work from finishing (its workLeft held), so the shots show
    //                                the ingredients; logs each one's state
    //   corpse=x,z,<PawnKindDef>     a new pawn of that kind killed there (its corpse, for butchering)
    //   direct=x,z,<Def>[,<Stuff>]   a new item placed in the cell the game's way (GenPlace Direct; a full cell pushes the
    //                                item already there aside, GenSpawn), forbidden so the map's colonists leave it
    //   benchcheck=1                 logs every item in a bill giver's or table's cells and within 2 cells round it: its
    //                                rest height; CHECK lines: no cell over the game's item limit, on a top (surface > 0)
    //                                at the surface, else (and beside it) on the ground unless other furniture carries it
    public static partial class Probe
    {
        private static readonly List<KeyValuePair<Pawn, Building>> benchCrafters = new List<KeyValuePair<Pawn, Building>>();

        private static bool BenchKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "benchtops":
                    BenchTops.On = a[0] != "0";
                    map.mapDrawer.RegenerateEverythingNow();
                    Log.Message("[IsoRim probe] benchtops " + BenchTops.On);
                    return true;
                case "bill":
                {
                    Building bench = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    Pawn p = PawnAt(map, I(a[3]), I(a[4]));
                    IBillGiver giver = bench as IBillGiver;
                    if (giver == null || p == null)
                    {
                        Log.Warning("[IsoRim probe] bill: no bench or colonist (" + bench + ", " + p + ")");
                        return true;
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    if (p.skills != null)
                    {
                        foreach (SkillRecord s in p.skills.skills)
                        {
                            s.Level = 10;
                        }
                    }
                    Bill bill = DefDatabase<RecipeDef>.GetNamed(a[2]).MakeNewBill();
                    giver.BillStack.AddBill(bill);
                    bill.SetPawnRestriction(p);
                    Job job = null;
                    foreach (WorkGiverDef wg in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                    {
                        if (wg.fixedBillGiverDefs != null && wg.fixedBillGiverDefs.Contains(bench.def) && wg.Worker is WorkGiver_DoBill w)
                        {
                            job = w.JobOnThing(p, bench, true);
                            if (job != null)
                            {
                                break;
                            }
                        }
                    }
                    bool taken = job != null && p.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    if (taken)
                    {
                        benchCrafters.Add(new KeyValuePair<Pawn, Building>(p, bench));
                    }
                    Log.Message("[IsoRim probe] bill " + a[2] + " at " + bench.def.defName + " " + bench.Position + " for " + p.LabelShort + ": job "
                        + (job != null ? job.def.defName : "none") + ", ordered " + taken);
                    return true;
                }
                case "tickbench":
                {
                    int max = I(a[0]);
                    int n = 0;
                    for (; n < max; n++)
                    {
                        bool all = true;
                        foreach (KeyValuePair<Pawn, Building> kv in benchCrafters)
                        {
                            JobDriver_DoBill d = kv.Key.jobs != null ? kv.Key.jobs.curDriver as JobDriver_DoBill : null;
                            if (Working(kv.Key, kv.Value))
                            {
                                d.workLeft = Mathf.Max(d.workLeft, 1000000f);   // held: the shots show the ingredients
                            }
                            else if (d != null)
                            {
                                all = false;
                            }
                        }
                        if (all)
                        {
                            break;
                        }
                        Find.TickManager.DoSingleTick();
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                    lastTicks = Find.TickManager.TicksGame;   // these ticks are not the clock jump that starts the next pass
                    foreach (KeyValuePair<Pawn, Building> kv in benchCrafters)
                    {
                        Pawn p = kv.Key;
                        Log.Message("[IsoRim probe] tickbench after " + n + " ticks: " + p.LabelShort + " at " + p.Position + " job "
                            + (p.CurJobDef != null ? p.CurJobDef.defName : "none") + " carrying " + (p.carryTracker.CarriedThing != null ? p.carryTracker.CarriedThing.LabelCap : "nothing")
                            + ", working at " + kv.Value.def.defName + " " + Working(p, kv.Value));
                        Log.Message("[IsoRim probe] CHECK " + (Working(p, kv.Value) ? "PASS" : "FAIL") + " " + p.LabelShort + " brought every ingredient to " + kv.Value.def.defName);
                    }
                    return true;
                }
                case "corpse":
                {
                    PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed(a[2]);
                    Rand.PushState(Gen.HashCombineInt(I(a[0]), I(a[1]) * 7919 + 23));
                    Pawn dead = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, null, PawnGenerationContext.NonPlayer));
                    Rand.PopState();
                    GenSpawn.Spawn(dead, new IntVec3(I(a[0]), 0, I(a[1])), map);
                    dead.Kill(null);
                    if (dead.Corpse != null)
                    {
                        dead.Corpse.SetForbidden(false, false);
                    }
                    Log.Message("[IsoRim probe] corpse " + a[2] + " at " + (dead.Corpse != null ? dead.Corpse.Position.ToString() : "none"));
                    return true;
                }
                case "direct":
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamed(a[2]);
                    ThingDef stuff = a.Length > 3 && a[3] != "-" ? DefDatabase<ThingDef>.GetNamed(a[3]) : (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    Thing t = ThingMaker.MakeThing(def, stuff);
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    Thing placed;
                    bool ok = GenPlace.TryPlaceThing(t, c, map, ThingPlaceMode.Direct, out placed);
                    if (placed != null)
                    {
                        placed.SetForbidden(true, false);   // left where it is (not hauled off by the map's colonists)
                    }
                    Log.Message("[IsoRim probe] direct " + a[2] + " at " + c + ": " + ok + (placed != null ? " -> " + placed.Position : ""));
                    return true;
                }
                case "benchcheck":
                {
                    foreach (Building b in map.listerBuildings.allBuildingsColonist.ToList())
                    {
                        if (!(b is IBillGiver) && b.def.surfaceType == SurfaceType.None && !(b is Building_WorkTable))
                        {
                            continue;
                        }
                        bool carries = BenchTops.Carries(b);
                        float top = BenchTops.TopOf(b);
                        int onTop = 0, beside = 0, ground = 0;
                        CellRect rect = b.OccupiedRect();
                        foreach (IntVec3 c in rect)
                        {
                            int inCell = c.GetItemCount(map);
                            Log.Message("[IsoRim probe] CHECK " + (inCell <= c.GetMaxItemsAllowedInCell(map) ? "PASS" : "FAIL") + " " + b.def.defName + " cell " + c
                                + " holds " + inCell + " item(s), the game's limit " + c.GetMaxItemsAllowedInCell(map));
                            foreach (Thing t in c.GetThingList(map).ToList())
                            {
                                if (t.def.category != ThingCategory.Item)
                                {
                                    continue;
                                }
                                float rest = Forms.BaseUnder(t, true);
                                bool pass;
                                string how;
                                if (carries && top > 0f)
                                {
                                    pass = Mathf.Abs(rest - top) < 0.001f && top > 0.3f;
                                    how = "on the top";
                                    onTop++;
                                }
                                else
                                {
                                    pass = rest == 0f;
                                    how = "on the ground";
                                    ground++;
                                }
                                Log.Message("[IsoRim probe] benchcheck " + b.def.defName + " " + b.Position + " cell " + c + " " + t.def.defName + " x" + t.stackCount
                                    + " id " + t.thingIDNumber + ": rest " + rest.ToString("0.00") + " (top " + top.ToString("0.00") + "), " + how);
                                Log.Message("[IsoRim probe] CHECK " + (pass ? "PASS" : "FAIL") + " " + b.def.defName + " " + t.def.defName + " " + how);
                            }
                        }
                        // What the game laid beside it (the top full: ingredients round the interaction spot, items pushed aside):
                        // on the ground, unless another piece of furniture with a top carries it.
                        foreach (IntVec3 c in rect.ExpandedBy(2))
                        {
                            if (rect.Contains(c) || !c.InBounds(map))
                            {
                                continue;
                            }
                            foreach (Thing t in c.GetThingList(map).ToList())
                            {
                                if (t.def.category != ThingCategory.Item)
                                {
                                    continue;
                                }
                                Building under = c.GetEdifice(map);
                                float rest = Forms.BaseUnder(t, true);
                                bool expectTop = under != null && (under is Building_Storage || BenchTops.Carries(under));
                                bool pass = expectTop || rest == 0f;
                                beside++;
                                Log.Message("[IsoRim probe] benchcheck " + b.def.defName + " " + b.Position + " beside " + c + (c == b.InteractionCell ? " (interaction spot)" : "")
                                    + " " + t.def.defName + " x" + t.stackCount + ": rest " + rest.ToString("0.00") + (under != null ? " on " + under.def.defName : " on the ground"));
                                Log.Message("[IsoRim probe] CHECK " + (pass ? "PASS" : "FAIL") + " " + b.def.defName + " " + t.def.defName + " beside it at its height");
                            }
                        }
                        Log.Message("[IsoRim probe] benchcheck " + b.def.defName + " " + b.Position + " surfaceType " + b.def.surfaceType + " form " + Forms.For(b.def).form
                            + " top " + top.ToString("0.00") + ": " + onTop + " on top, " + beside + " beside, " + ground + " on the ground");
                    }
                    return true;
                }
            }
            return false;
        }

        private static bool Working(Pawn p, Building bench)
        {
            Job j = p.CurJob;
            return j != null && j.def == JobDefOf.DoBill && p.jobs.curDriver is JobDriver_DoBill && p.Position == bench.InteractionCell
                && p.carryTracker.CarriedThing == null && j.GetTargetQueue(TargetIndex.B).Count == 0 && !p.pather.Moving;
        }
    }
}
