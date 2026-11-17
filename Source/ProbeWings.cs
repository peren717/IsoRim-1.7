using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for animal wings in flight (Api 14 wings, Api 80 wingsInFlightOnly; Animals.DrawWings). Spawn with
    // mech=x,z,<kind>,player,<age>,<rot>,,f|m (ProbeMechs); a standing take-off is animalpose=x,z,fly,<rot> (ProbeSmoothAnimals).
    //   flygoto=x,z,tx,tz   the pawn in (x,z) given the game's wander walk (GotoWander, a job the game lets a bird take off on)
    //                       to (tx,tz), player-forced (walkfilm follow); its flight cooldown cleared and its race's
    //                       flightStartChanceOnJobStart held at 1 for that job start only, so the game's own
    //                       Pawn_FlightTracker.Notify_JobStarted takes off (vanilla: a 5 % chance on such a job)
    //   flyland=x,z         the flying pawn in (x,z) put at the end of its flight time (flyingTicks = MaxFlightTicks - 1): the
    //                       game's own FlightTick lands it on the next tick (the whole landing curve)
    //   wingcheck=label     every spawned pawn whose model has wings: flying, airborne factor, how far its wings are open,
    //                       whether its wing models are drawn (Animals.WingOpen) and at what scale
    public static partial class Probe
    {
        private static bool WingKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "flygoto":
                {
                    Pawn p = PawnAt(map, int.Parse(a[0]), int.Parse(a[1]));
                    if (p == null || p.flight == null)
                    {
                        return true;
                    }
                    AccessTools.Field(typeof(Pawn_FlightTracker), "flightCooldownTicks").SetValue(p.flight, 0);
                    Job j = JobMaker.MakeJob(JobDefOf.GotoWander, new IntVec3(int.Parse(a[2]), 0, int.Parse(a[3])));
                    j.playerForced = true;
                    float was = p.RaceProps.flightStartChanceOnJobStart;
                    p.RaceProps.flightStartChanceOnJobStart = 1f;
                    try
                    {
                        p.jobs.StartJob(j, JobCondition.InterruptForced);
                    }
                    finally
                    {
                        p.RaceProps.flightStartChanceOnJobStart = was;
                    }
                    Log.Message("[IsoRim probe] flygoto " + p.def.defName + " " + p.Position + ": job " + p.CurJobDef?.defName + " tryStartFlying "
                        + j.def.tryStartFlying + " flying " + p.flight.Flying + " job.flying " + j.flying + " chance back to " + was);
                    return true;
                }
                case "flyland":
                {
                    Pawn p = PawnAt(map, int.Parse(a[0]), int.Parse(a[1]));
                    if (p == null || p.flight == null || !p.flight.Flying)
                    {
                        Log.Warning("[IsoRim probe] flyland: no flying pawn at " + a[0] + "," + a[1]);
                        return true;
                    }
                    AccessTools.Field(typeof(Pawn_FlightTracker), "flyingTicks").SetValue(p.flight, p.flight.MaxFlightTicks - 1);
                    Log.Message("[IsoRim probe] flyland " + p.def.defName + " " + p.Position + ": flyingTicks " + (p.flight.MaxFlightTicks - 1));
                    return true;
                }
                case "wingcheck":
                {
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.OrderBy(x => x.Position.z).ThenBy(x => x.Position.x))
                    {
                        ModelEntry e;
                        if (!ModelLibrary.TryGet(p.def, out e) || e.def.wings == null)
                        {
                            continue;
                        }
                        float air = Animals.AirborneFactor(p);
                        bool drawn;
                        float open = Animals.WingOpen(e.def, air, out drawn);
                        Log.Message("[IsoRim probe] wingcheck " + a[0] + " " + p.def.defName + " " + p.gender + " " + p.Position + " posture " + p.GetPosture()
                            + " dead " + p.Dead + " flying " + (p.flight != null && p.flight.Flying) + " air " + air.ToString("0.000") + " open " + open.ToString("0.000")
                            + " inFlightOnly " + e.def.wingsInFlightOnly + " wingsDrawn " + drawn
                            + " scale " + (drawn ? (e.def.wingsInFlightOnly ? open : 1f).ToString("0.000") : "-")
                            + " wing " + WhichWing(e, e.WingFor(p, false)));
                    }
                    foreach (Corpse c in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>())
                    {
                        ModelEntry e;
                        if (c.InnerPawn == null || !ModelLibrary.TryGet(c.InnerPawn.def, out e) || e.def.wings == null)
                        {
                            continue;
                        }
                        bool drawn;
                        float open = Animals.WingOpen(e.def, Animals.AirborneFactor(c.InnerPawn), out drawn);
                        Log.Message("[IsoRim probe] wingcheck " + a[0] + " corpse " + c.InnerPawn.def.defName + " " + c.Position + " open " + open.ToString("0.000")
                            + " wingsDrawn " + drawn);
                    }
                    return true;
                }
            }
            return false;
        }

        private static string WhichWing(ModelEntry e, Model w)
        {
            return w == null ? "none" : w == e.wing ? "wingModel" : w == e.femaleWing ? "femaleWingModel" : "byBodyGraphic";
        }
    }
}
