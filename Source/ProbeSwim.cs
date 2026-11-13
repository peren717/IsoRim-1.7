using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for people swimming (the game's GoSwimming job, Odyssey; Swimming.cs). Cells are map cells. The job is the
    // game's own, given as its debug action "Go swimming" gives it (an ordered job to a cell), and run with real ticks.
    //   swimgo=x,z,tx,tz[,mx,mz]  the person in (x,z) ordered to go swimming to (tx,tz) (joy set to 30%, as the debug action does,
    //                         so the job does not end on full joy); drafted people stay drafted, every other free colonist is
    //                         drafted (stands still, out of the way). (mx,mz): its mark on the way (default the job's cell). From the
    //                         first one on, IsoRim notes what each frame draws of every figure (Swimming.Reporting)
    //   swimuntil=N           ticks one at a time (at most N), the per-frame catch-up run each tick, until every person sent by
    //                         swimgo is as far from its start as its mark is, or off its job (an adult then waits on the job's cell,
    //                         still on the job, 240 ticks; a child, who has no joy need, ends the job on reaching its cell, as the
    //                         game's driver does: its mark is set short of the cell)
    //   swimtick=N            N ticks the same way (the swims end)
    //   swimcheck=label[,x0,z0,x1,z1]  every person on the map (in the rectangle, if given) whose figure was drawn since the last
    //                         swimuntil / swimtick: cell, water under them, job, the game's swimming state (Pawn.Swimming) and
    //                         what IsoRim's last frame drew of its figure (Swimming.drawn), with a verdict:
    //                           swimming     drawn swimming, nothing under the water line (its lowest point at the water, 0), the
    //                                        head over it, no headgear, nothing held, no ground shadow, the body not shown to
    //                                        other mods' parts;
    //                           not swimming drawn as before: feet on the ground, headgear as worn, held things, its shadow, the
    //                                        body shown.
    //                         Then "swimcheck <label>: total n PASS, m FAIL, k not drawn" and the source hash of the copied
    //                         render-flag rule.
    public static partial class Probe
    {
        private static readonly List<Pawn> swimmers = new List<Pawn>();
        private static readonly Dictionary<Pawn, IntVec3> swimStart = new Dictionary<Pawn, IntVec3>();
        private static readonly Dictionary<Pawn, IntVec3> swimMarkCell = new Dictionary<Pawn, IntVec3>();
        private static int swimMark = -1;   // the frame of the last swimuntil / swimtick: what was drawn before shows an older state

        private static void SwimTicks(Map map, int limit, System.Func<bool> done, out int n)
        {
            n = 0;
            CellRect all = CellRect.WholeMap(map);
            foreach (Pawn dp in map.mapPawns.AllPawnsSpawned.Where(x => x.Drafted))
            {
                // the harness clock jumps (Capture17 time) would count as hours stood drafted: no auto-undraft in these ticks
                Traverse.Create(dp.drafter).Field("autoUndrafter").GetValue<AutoUndrafter>()?.Notify_Drafted();
            }
            while (n < limit && !done())
            {
                Find.TickManager.DoSingleTick();
                foreach (Pawn tp in map.mapPawns.AllPawnsSpawned.ToList())
                {
                    tp.ProcessPostTickVisuals(1, all);
                }
                n++;
            }
            foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
            {
                tp.Drawer.tweener.ResetTweenedPosToRoot();
            }
            lastTicks = Find.TickManager.TicksGame;
            swimMark = Time.frameCount;
        }

        private static bool SwimKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "swimgo":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    Swimming.Reporting = true;
                    IntVec3 dest = new IntVec3(I(a[2]), 0, I(a[3]));
                    if (p.needs != null && p.needs.joy != null)
                    {
                        p.needs.joy.CurLevelPercentage = 0.3f;
                    }
                    Job job = JobMaker.MakeJob(JobDefOf.GoSwimming, dest);
                    job.locomotionUrgency = LocomotionUrgency.Walk;
                    job.targetQueueA = new List<LocalTargetInfo>();
                    bool taken = p.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    swimmers.Add(p);
                    swimStart[p] = p.Position;
                    swimMarkCell[p] = a.Length > 5 ? new IntVec3(I(a[4]), 0, I(a[5])) : dest;
                    foreach (Pawn other in map.mapPawns.FreeColonistsSpawned)
                    {
                        if (!swimmers.Contains(other) && other.drafter != null && !other.drafter.Drafted)
                        {
                            other.drafter.Drafted = true;
                        }
                    }
                    Log.Message("[IsoRim probe] swimgo " + p.LabelShort + " " + p.Position + " -> " + dest + ": " + taken
                        + ", outdoor temp " + map.mapTemperature.OutdoorTemp.ToString("0.0") + ", happy to swim outside " + JoyGiver_GoSwimming.HappyToSwimOutsideOnMap(map));
                    return true;
                }
                case "swimuntil":
                {
                    int done;
                    SwimTicks(map, Mathf.Clamp(I(a[0]), 1, 3000), () => swimmers.All(x => !x.Spawned || x.CurJobDef != JobDefOf.GoSwimming
                        || x.Position.DistanceTo(swimStart[x]) >= swimMarkCell[x].DistanceTo(swimStart[x]) - 0.01f), out done);
                    Log.Message("[IsoRim probe] swimuntil: " + done + " ticks; " + string.Join(", ", swimmers.Select(x => x.LabelShort + " " + x.Position
                        + " " + (x.CurJobDef != null ? x.CurJobDef.defName : "-") + (x.Swimming ? " swimming" : ""))));
                    return true;
                }
                case "swimtick":
                {
                    int done;
                    int n = Mathf.Clamp(I(a[0]), 1, 3000);
                    SwimTicks(map, n, () => false, out done);
                    Log.Message("[IsoRim probe] swimtick: " + done + " ticks");
                    return true;
                }
                case "swimcheck":
                {
                    int pass = 0, fail = 0, unseen = 0;
                    CellRect area = a.Length > 4 ? CellRect.FromLimits(I(a[1]), I(a[2]), I(a[3]), I(a[4])) : CellRect.WholeMap(map);
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps.Humanlike && area.Contains(x.Position)).OrderBy(x => x.Position.z).ThenBy(x => x.Position.x))
                    {
                        bool swimming = p.Swimming;
                        string line = "[IsoRim probe] swimcheck " + a[0] + " " + p.LabelShort + " at " + p.Position + " rot " + p.Rotation
                            + " water " + p.Position.GetTerrain(map).IsWater + " job " + (p.CurJobDef != null ? p.CurJobDef.defName : "-") + " swimming " + swimming;
                        Swimming.Drawn d;
                        if (!Swimming.drawn.TryGetValue(p.thingIDNumber, out d) || d.frame <= swimMark)
                        {
                            Log.Message(line + ": not drawn since the last ticks (not checked)");
                            unseen++;
                            continue;
                        }
                        bool worn = p.apparel != null && p.apparel.WornApparel.Any(x => x.def.apparel != null && x.def.apparel.LastLayer == ApparelLayerDefOf.Overhead);
                        bool armed = p.equipment != null && p.equipment.Primary != null;
                        List<string> bad = new List<string>();
                        if (d.swim != swimming)
                        {
                            bad.Add("drawn swimming " + d.swim);
                        }
                        if (swimming)
                        {
                            if (d.lowest < -0.002f || d.lowest > 0.02f)
                            {
                                bad.Add("lowest point not at the water line");
                            }
                            if (d.highest < 0.3f)
                            {
                                bad.Add("head not over the water");
                            }
                            if (d.hat)
                            {
                                bad.Add("headgear drawn");
                            }
                            if (d.held)
                            {
                                bad.Add("held things drawn");
                            }
                            if (d.shadow)
                            {
                                bad.Add("ground shadow drawn");
                            }
                            if (d.bodyShown)
                            {
                                bad.Add("body shown to other parts");
                            }
                        }
                        else
                        {
                            if (d.lowest < -0.002f || d.lowest > 0.06f)
                            {
                                bad.Add("feet not on the ground");
                            }
                            if (d.hat != worn)
                            {
                                bad.Add("headgear drawn " + d.hat + ", worn " + worn);
                            }
                            if (!d.held)
                            {
                                bad.Add("held things not drawn");
                            }
                            if (!d.shadow)
                            {
                                bad.Add("no ground shadow");
                            }
                            if (!d.bodyShown)
                            {
                                bad.Add("body not shown");
                            }
                        }
                        Log.Message(line + ": drawn swim " + d.swim + " water line " + d.waterLine.ToString("0.000") + " lowest " + d.lowest.ToString("0.000")
                            + " highest " + d.highest.ToString("0.000") + " headgear " + d.hat + " (worn " + worn + ") held " + d.held + " (armed " + armed + ") shadow "
                            + d.shadow + " body shown " + d.bodyShown + (bad.Count == 0 ? " PASS" : " FAIL: " + string.Join(", ", bad)));
                        if (bad.Count == 0)
                        {
                            pass++;
                        }
                        else
                        {
                            fail++;
                        }
                    }
                    Log.Message("[IsoRim probe] swimcheck " + a[0] + ": total " + pass + " PASS, " + fail + " FAIL, " + unseen + " not drawn; render flags source hash "
                        + Api.CopyHash(AccessTools.Method(typeof(PawnRenderer), "ParallelGetPreRenderResults")));
                    return true;
                }
            }
            return false;
        }
    }
}
