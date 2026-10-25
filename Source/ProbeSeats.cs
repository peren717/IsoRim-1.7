using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for pawns posed on furniture (Poses.cs). Cells are map cells. The jobs are the game's own,
    // given as the player or the pawn's own joy/need givers would, and run with real ticks (tick= after them).
    //   place=<Def>,x,z,<rot 0-3>[,<Stuff>|-]   a building (the player's) facing rot
    //   sit=px,pz,<kind>[,tx,tz]     the colonist in (px,pz) undrafted and given a job that seats them, as the game gives it:
    //                                eat (a simple meal put in its cell, Ingest: it finds a chair at a table itself),
    //                                read[:<book def>] (a novel, or a book of that kind, put in its cell, Reading: it finds a
    //                                chair itself), tv (watch the
    //                                television at tx,tz: WatchBuildingUtility picks the seat), reign (on the throne at
    //                                tx,tz), meditate (on the cell tx,tz)
    //   walkover=px,pz,tx,tz[,N]     the colonist in (px,pz) walks to (tx,tz) (Goto); ticks until it is on a seat while
    //                                walking (at most N, default 400), then stops ticking: it must not be seated
    //   poses=0|1                    pawns posed on furniture (Poses.On, default 1) or not (before/after shots)
    //   settle=1                     every pawn's drawn position put where the game's tween would have eased it to (the
    //                                tween stands still while the harness keeps the game paused)
    //   lovers=x1,z1,x2,z2[,spouse]  the two colonists made lovers (so they may share a bed); spouse: married instead (an ideo that
    //                                 lets only spouses share a bed then allows it too)
    //   seatcheck=1                  logs each colonist: job, cell, seat (Poses.Report), drawn position; with
    //                                seatcheck=expect, CHECK lines: a sitting job's pawn is seated, a walking one is not
    public static partial class Probe
    {
        private static bool SeatKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "place":
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamed(a[0]);
                    ThingDef stuff = a.Length > 4 && a[4] != "-" ? DefDatabase<ThingDef>.GetNamed(a[4]) : (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    Thing t = ThingMaker.MakeThing(def, stuff);
                    if (t.def.CanHaveFaction)
                    {
                        t.SetFaction(Faction.OfPlayer);
                    }
                    GenSpawn.Spawn(t, new IntVec3(I(a[1]), 0, I(a[2])), map, new Rot4(I(a[3])));
                    Log.Message("[IsoRim probe] place " + def.defName + " at " + t.Position + " rot " + t.Rotation + " cells " + t.OccupiedRect());
                    return true;
                }
                case "sit":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    IntVec3 tc = a.Length > 4 ? new IntVec3(I(a[3]), 0, I(a[4])) : IntVec3.Invalid;
                    Job job = null;
                    string note = "";
                    switch (a[2].Split(':')[0])
                    {
                        case "eat":
                        {
                            Thing meal = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.MealSimple), p.Position, map);
                            if (p.needs.food != null)
                            {
                                p.needs.food.CurLevel = 0.2f;
                            }
                            job = JobMaker.MakeJob(JobDefOf.Ingest, meal);
                            job.count = 1;
                            break;
                        }
                        case "read":
                        {
                            ThingDef bookDef = a[2].Contains(":") ? DefDatabase<ThingDef>.GetNamed(a[2].Split(':')[1]) : ThingDefOf.Novel;   // read:<book def> (a book of that kind, as BookUtility makes it)
                            Thing book = GenSpawn.Spawn(typeof(Book).IsAssignableFrom(bookDef.thingClass) ? BookUtility.MakeBook(bookDef, ArtGenerationContext.Colony) : ThingMaker.MakeThing(bookDef), p.Position, map);
                            if (p.needs.joy != null)
                            {
                                p.needs.joy.CurLevel = 0.1f;
                            }
                            job = JobMaker.MakeJob(JobDefOf.Reading, book);
                            break;
                        }
                        case "tv":
                        {
                            Building tv = tc.GetFirstBuilding(map);
                            if (p.needs.joy != null)
                            {
                                p.needs.joy.CurLevel = 0.1f;
                            }
                            IntVec3 cell;
                            Building chair;
                            if (tv != null && WatchBuildingUtility.TryFindBestWatchCell(tv, p, true, out cell, out chair))
                            {
                                JoyGiverDef giver = DefDatabase<JoyGiverDef>.AllDefs.FirstOrDefault(g => g.thingDefs != null && g.thingDefs.Contains(tv.def));
                                job = JobMaker.MakeJob(giver != null ? giver.jobDef : DefDatabase<JobDef>.GetNamed("WatchTelevision"), tv, cell, chair);
                                note = " watch cell " + cell + " chair " + (chair != null ? chair.def.defName : "none");
                            }
                            break;
                        }
                        case "reign":
                        {
                            Building throne = tc.GetFirstBuilding(map);
                            if (throne != null)
                            {
                                job = JobMaker.MakeJob(JobDefOf.Reign, throne, null, throne);
                                note = " interaction cell " + throne.InteractionCell;
                            }
                            break;
                        }
                        case "meditate":
                            job = JobMaker.MakeJob(JobDefOf.Meditate, tc, null, null);
                            break;
                    }
                    bool taken = job != null && p.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    Log.Message("[IsoRim probe] sit " + a[2] + " " + p.LabelShort + " at " + p.Position + ": ordered " + taken + note);
                    return true;
                }
                case "walkover":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    Job gj = JobMaker.MakeJob(JobDefOf.Goto, new IntVec3(I(a[2]), 0, I(a[3])));
                    gj.playerForced = true;
                    p.jobs.StartJob(gj, JobCondition.InterruptForced);
                    int max = a.Length > 4 ? I(a[4]) : 400;
                    int n = 0;
                    for (; n < max; n++)
                    {
                        Find.TickManager.DoSingleTick();
                        Building e = p.Position.GetEdifice(map);
                        if (e != null && e.def.building != null && e.def.building.isSittable && p.pather.MovingNow && p.pather.MovePercentage > 0.2f)
                        {
                            break;
                        }
                    }
                    Log.Message("[IsoRim probe] walkover " + p.LabelShort + " after " + n + " ticks at " + p.Position + ": " + Poses.Report(p));
                    return true;
                }
                case "poses":
                    Poses.On = a[0] != "0";
                    return true;
                case "settle":
                    // The game's tween stands still while paused (its spring scales with the tick rate), so after ticks run in
                    // one frame every pawn is put where the game would have eased it to (PawnTweener.ResetTweenedPosToRoot).
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        if (p.Drawer != null && p.Drawer.tweener != null)
                        {
                            p.Drawer.tweener.ResetTweenedPosToRoot();
                        }
                    }
                    return true;
                case "lovers":
                {
                    Pawn p1 = PawnAt(map, I(a[0]), I(a[1]));
                    Pawn p2 = PawnAt(map, I(a[2]), I(a[3]));
                    if (p1 != null && p2 != null && p1.relations != null)
                    {
                        p1.relations.AddDirectRelation(a.Length > 4 && a[4] == "spouse" ? PawnRelationDefOf.Spouse : PawnRelationDefOf.Lover, p2);
                    }
                    return true;
                }
                case "seatcheck":
                {
                    bool expect = a.Length > 0 && a[0] == "expect";
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
                    {
                        string job = p.CurJobDef != null ? p.CurJobDef.defName : "none";
                        Log.Message("[IsoRim probe] seatcheck " + p.LabelShort + " " + p.Position + " job " + job + " rot " + p.Rotation
                            + " drawPos " + p.DrawPos.ToString("F2") + ": " + Poses.Report(p) + (p.CurrentBed() != null ? ", bed " + p.CurrentBed().def.defName
                            + " flat lying height " + Poses.FlatLyingHeight(p.CurrentBed(), p).ToString("0.00") : ""));
                        if (!expect)
                        {
                            continue;
                        }
                        Poses.Seat s;
                        bool seated = Poses.Seated(p, out s);
                        bool sitJob = job == "Ingest" || job == "Reading" || job == "WatchTelevision" || job == "Reign" || job == "Meditate";
                        if (p.CurJobDef == JobDefOf.Goto)
                        {
                            Log.Message("[IsoRim probe] CHECK " + (!seated ? "PASS" : "FAIL") + " walking " + p.LabelShort + " not seated");
                        }
                        else if (sitJob && p.Position.GetEdifice(map) != null && p.Position.GetEdifice(map).def.building.isSittable)
                        {
                            Log.Message("[IsoRim probe] CHECK " + (seated && s.amount > 0.95f ? "PASS" : "FAIL") + " " + job + " " + p.LabelShort + " seated on "
                                + p.Position.GetEdifice(map).def.defName + " (amount " + s.amount.ToString("0.00") + ", seat " + s.height.ToString("0.00") + ")");
                        }
                        else if (sitJob)
                        {
                            Log.Message("[IsoRim probe] CHECK FAIL " + job + " " + p.LabelShort + " not on a seat at " + p.Position);
                        }
                    }
                    return true;
                }
            }
            return false;
        }
    }
}
