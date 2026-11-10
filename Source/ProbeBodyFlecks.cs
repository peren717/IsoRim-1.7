using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness keys for effects at a figure's body (BodyFlecks; bodyflecks, 2026-11-09):
    //   bodyflecks=0|1             0: as before (flat on the ground), for before / after sheets
    //   bodyfleckinv=start|reset|log[,tag]  the inventory of MoteOverhead flecks starting at a pawn (def, caller, pawn kind,
    //                              lifted or why not, how far up the picture, the height given): start noting, forget, log
    //   bfanimal=x,z,<PawnKindDef>[,rot]  a new animal of that kind (no faction) in the cell, facing rot
    //   bfmelee=x,z,tx,tz          the pawn in the first cell attacks the pawn in the second in melee (a real job)
    //   bfbuild=fx,fz,bx,bz,<Def>[,<Stuff>]  a frame of that building in (fx,fz) with all its materials in it, the colonist
    //                              in (bx,bz) ordered to finish it (a real job)
    //   bfuntil=x,z,<FleckDef|any>,max[,after]  game ticks one at a time until such a fleck starts at the pawn in the cell
    //                              (at most max), then after more ticks; each tick with the pawns' visuals (VisualTick)
    //   bftick=N                   N game ticks with the pawns' visuals (breath, footprints, splashes)
    //   bfcold=1                   the game's cold snap at its full strength, for good (pawns breathe below 0 C)
    //   bfhold=1                   drafted pawns kept drafted across the harness's day jumps (auto-undraft clock restarted)
    public static partial class Probe
    {
        private static bool BodyFleckKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "bodyflecks":
                    BodyFlecks.Off = a[0] == "0";
                    return true;
                case "bodyfleckinv":
                    if (a[0] == "start")
                    {
                        BodyFlecks.inventory = true;
                    }
                    else if (a[0] == "reset")
                    {
                        BodyFlecks.ResetInventory();
                    }
                    else
                    {
                        BodyFlecks.LogInventory(a.Length > 1 ? a[1] : "-");
                    }
                    return true;
                case "bfanimal":
                {
                    PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(a[2]);
                    if (kind == null)
                    {
                        Log.Warning("[IsoRim probe] bfanimal: no kind " + a[2]);
                        return true;
                    }
                    Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, null, PawnGenerationContext.NonPlayer, -1, true));
                    GenSpawn.Spawn(pawn, new IntVec3(I(a[0]), 0, I(a[1])), map, a.Length > 3 ? new Rot4(I(a[3])) : Rot4.South);
                    Log.Message("[IsoRim probe] bfanimal " + kind.defName + " at " + pawn.Position + ", 3D " + Patches.DrawnAsModel(pawn));
                    return true;
                }
                case "bfmelee":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    Pawn t = PawnAt(map, I(a[2]), I(a[3]));
                    if (p == null || t == null)
                    {
                        Log.Warning("[IsoRim probe] bfmelee: no pawn at " + a[0] + "," + a[1] + " or " + a[2] + "," + a[3]);
                        return true;
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    Job job = JobMaker.MakeJob(JobDefOf.AttackMelee, t);
                    job.playerForced = true;
                    job.killIncappedTarget = false;
                    job.expiryInterval = 100000;
                    p.jobs.StartJob(job, JobCondition.InterruptForced);
                    Log.Message("[IsoRim probe] bfmelee " + p.LabelShort + " -> " + t.LabelShort + ": " + (p.CurJob == job));
                    return true;
                }
                case "bfbuild":
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamed(a[4]);
                    ThingDef stuff = a.Length > 5 ? DefDatabase<ThingDef>.GetNamed(a[5]) : (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    Frame frame = (Frame)ThingMaker.MakeThing(def.frameDef, stuff);
                    frame.SetFaction(Faction.OfPlayer);
                    foreach (ThingDefCountClass need in def.CostListAdjusted(stuff))
                    {
                        Thing mat = ThingMaker.MakeThing(need.thingDef);
                        mat.stackCount = need.count;
                        frame.resourceContainer.TryAdd(mat);
                    }
                    GenSpawn.Spawn(frame, new IntVec3(I(a[0]), 0, I(a[1])), map, Rot4.North);
                    Pawn p = PawnAt(map, I(a[2]), I(a[3]));
                    bool ok = false;
                    if (p != null && p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                    {
                        // (that colonist cannot build: places swapped with a drafted one who can, outside the shots' cells)
                        Pawn other = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x != p && x.Drafted && !x.WorkTypeIsDisabled(WorkTypeDefOf.Construction)
                            && x.Position.x >= 84);
                        other = other ?? map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x != p && !x.WorkTypeIsDisabled(WorkTypeDefOf.Construction));
                        if (other != null)
                        {
                            IntVec3 was = other.Position;
                            other.Position = p.Position;
                            other.Notify_Teleported(true, true);
                            p.Position = was;
                            p.Notify_Teleported(true, true);
                            Log.Message("[IsoRim probe] bfbuild: " + p.LabelShort + " cannot build, " + other.LabelShort + " builds");
                            p = other;
                        }
                    }
                    if (p != null)
                    {
                        if (p.drafter != null)
                        {
                            p.drafter.Drafted = false;
                        }
                        p.skills?.GetSkill(SkillDefOf.Construction)?.Level = 12;   // (a low skill fails the frame at once)
                        Job job = JobMaker.MakeJob(JobDefOf.FinishFrame, frame);
                        job.playerForced = true;
                        p.jobs.StartJob(job, JobCondition.InterruptForced);
                        ok = p.CurJob == job;
                    }
                    Log.Message("[IsoRim probe] bfbuild " + def.defName + " at " + frame.Position + ", builder " + (p != null ? p.LabelShort : "none") + ": " + ok);
                    return true;
                }
                case "bfuntil":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    FleckDef def = a[2] == "any" ? null : DefDatabase<FleckDef>.GetNamedSilentFail(a[2]);
                    int max = Mathf.Clamp(I(a[3]), 1, 3000), after = a.Length > 4 ? Mathf.Clamp(I(a[4]), 0, 600) : 0, n = 0;
                    bool hit = false;
                    while (p != null && n < max && !hit)
                    {
                        BodyFlecks.ClearSeen();
                        VisualTick(map);
                        n++;
                        hit = BodyFlecks.SeenAt(p, def);
                    }
                    for (int i = 0; i < after; i++)
                    {
                        VisualTick(map);
                    }
                    Log.Message("[IsoRim probe] bfuntil " + (p != null ? p.LabelShort : "no pawn") + " " + a[2] + ": " + (hit ? "seen" : "NOT seen") + " after " + n + " ticks (+" + after
                        + "), lifted so far " + BodyFlecks.Lifted + ", pawn " + (p != null ? p.Rotation.ToStringHuman() + " " + p.GetPosture() + " job " + (p.CurJobDef?.defName ?? "-")
                        + " temp " + p.AmbientTemperature.ToString("0") : "-"));
                    return true;
                }
                case "bfcold":
                {
                    // bfcold=1: the game's cold snap on the map for good, already at its full -20 (past its 12000-tick fade-in)
                    if (!map.gameConditionManager.ConditionIsActive(GameConditionDefOf.ColdSnap))
                    {
                        GameCondition cold = GameConditionMaker.MakeConditionPermanent(GameConditionDefOf.ColdSnap);
                        map.gameConditionManager.RegisterCondition(cold);
                        cold.startTick = Find.TickManager.TicksGame - 13000;
                    }
                    Log.Message("[IsoRim probe] bfcold: outdoor " + map.mapTemperature.OutdoorTemp.ToString("0") + " C");
                    return true;
                }
                case "bfhold":
                {
                    // bfhold=1: every drafted pawn's auto-undraft clock started again (the harness jumps a day between passes,
                    // after which the game would undraft them at the first tick and they would wander off)
                    int held = 0;
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                    {
                        if (p.drafter != null && p.Drafted)
                        {
                            HarmonyLib.Traverse.Create(p.drafter).Field("autoUndrafter").Method("Notify_Drafted").GetValue();
                            held++;
                        }
                    }
                    Log.Message("[IsoRim probe] bfhold: " + held + " drafted pawns held");
                    return true;
                }
                case "bftick":
                {
                    // bftick=N: N game ticks, each with the pawns' visuals (VisualTick)
                    int n = Mathf.Clamp(I(a[0]), 1, 3000);
                    for (int i = 0; i < n; i++)
                    {
                        VisualTick(map);
                    }
                    Log.Message("[IsoRim probe] bftick " + n + ": lifted so far " + BodyFlecks.Lifted + ", seen " + BodyFlecks.Seen + "; "
                        + string.Join("; ", map.mapPawns.FreeColonistsSpawned.Select(x => x.LabelShort + " at " + x.Position + " drawn " + x.DrawPos.ToString("F2") + " on "
                        + x.Position.GetTerrain(map).defName + (x.pather.Moving ? " moving" : ""))));
                    return true;
                }
            }
            return false;
        }

        // One game tick, then what the game does for every spawned pawn in view after the ticks of a frame (Map.MapUpdate ->
        // PostTickVisuals -> Pawn_DrawTracker: breath, footprints, splashes, ripples, wiggles), as if all of the map were in
        // view. A bare DoSingleTick leaves those out. (The rotation tracker's visuals are left out: a drafted pawn keeps the
        // facing it was given, for the sheets' four facings.)
        private static void VisualTick(Map map)
        {
            Find.TickManager.DoSingleTick();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p.Spawned && !p.Suspended)
                {
                    // (the drawn place follows the pawn as the game moves it at its fastest speeds: the tweener only
                    // moves once a frame, and these ticks are all in one frame)
                    p.Drawer.tweener.ResetTweenedPosToRoot();
                    p.Drawer.ProcessPostTickVisuals(1);
                }
            }
        }
    }
}
