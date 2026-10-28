using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for how 3D figures hold their weapons (FigureWeapons.cs). Real jobs, run with real ticks.
    //   carrydowned=x,z,tx,tz      the drafted colonist in (x,z) given the game's drafted carry job (CarryDownedPawnDrafted)
    //                              for the downed pawn in (tx,tz)
    //   tickjitter=N,x,z,min       game ticks one at a time (at most N) until the pawn in (x,z) is pushed by its jitter
    //                              (a melee lunge, a hit) at least min cells
    //   shieldfull=x,z             the shield belt worn by the pawn in (x,z) charged full
    //   weaponcheck=1              logs every standing colonist's held weapon as drawn (FigureWeapons.Report, by reflection:
    //                              builds without it too), its jitter and lean
    //   cast=x,z,<AbilityDef>,tx,tz  the pawn in (x,z) given that ability (if it lacks it) and ordered to cast it at the pawn
    //                              in (tx,tz), else at the cell (the game's casting job: run tick= or burst= after it)
    //   equip=x,z,<ThingDef>[,Stuff]  the pawn in (x,z) (a mech too) given that weapon as its primary, its old one destroyed
    //   job=x,z,<JobDef>[,tx,tz]   the pawn in (x,z) undrafted and given that job (a thing or cell target when given), as the
    //                              game's own job (no work giver)
    //   weaponsizes=1              logs every weapon with a 3D model: its fit to its sprite (WeaponModels.Hold) and its length
    //                              held by an adult figure against lying on the ground (by reflection: builds without it too)
    //   beamcheck=1                logs FloorSprays' counters (sprays, beams between floors, streams, lines on one floor,
    //                              strips drawn) and every line mote between two ends on the map
    public static partial class Probe
    {
        private static readonly AccessTools.FieldRef<Pawn_DrawTracker, JitterHandler> probeJitter =
            AccessTools.FieldRefAccess<Pawn_DrawTracker, JitterHandler>("jitterer");

        private static bool WeaponKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "cast":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    AbilityDef def = DefDatabase<AbilityDef>.GetNamedSilentFail(a[2]);
                    if (p == null || def == null || p.abilities == null)
                    {
                        Log.Warning("[IsoRim probe] cast: no pawn or ability " + a[2]);
                        return true;
                    }
                    if (p.abilities.GetAbility(def) == null)
                    {
                        p.abilities.GainAbility(def);
                    }
                    Ability ab = p.abilities.GetAbility(def);
                    IntVec3 tc = new IntVec3(I(a[3]), 0, I(a[4]));
                    Pawn tp = tc.GetThingList(map).OfType<Pawn>().FirstOrDefault();
                    LocalTargetInfo target = tp != null ? new LocalTargetInfo(tp) : new LocalTargetInfo(tc);
                    ab.QueueCastingJob(target, LocalTargetInfo.Invalid);
                    Log.Message("[IsoRim probe] cast " + def.defName + " by " + p.LabelShort + " at " + target + ": job " + (p.CurJob != null ? p.CurJob.def.defName : "-"));
                    return true;
                }
                case "clearflecks":
                {
                    // clearflecks=1: every fleck on the map removed (burn marks and sparks of earlier passes, which a paused
                    // harness game never ages out)
                    int cleared = 0;
                    if (Traverse.Create(map.flecks).Field("systems").GetValue() is System.Collections.IDictionary systems)
                    {
                        foreach (object sys in systems.Values)
                        {
                            for (System.Type st = sys.GetType(); st != null && st != typeof(object); st = st.BaseType)
                            {
                                foreach (System.Reflection.FieldInfo f in AccessTools.GetDeclaredFields(st))
                                {
                                    if (!f.IsStatic && f.GetValue(sys) is System.Collections.IList list)
                                    {
                                        cleared += list.Count;
                                        list.Clear();
                                    }
                                }
                            }
                        }
                    }
                    Log.Message("[IsoRim probe] clearflecks: " + cleared + " removed");
                    return true;
                }
                case "beamcheck":
                {
                    Log.Message("[IsoRim probe] beamcheck: sprays " + FloorSprays.Sprays + ", beams between floors " + FloorSprays.Duals + ", streams between floors "
                        + FloorSprays.Streams + ", lines on one floor " + FloorSprays.Lines + ", strips drawn " + FloorSprays.Tilts);
                    foreach (Thing t in map.listerThings.AllThings.OfType<MoteDualAttached>().ToList())
                    {
                        Log.Message("[IsoRim probe] beamcheck mote " + t.def.defName + " layer " + t.def.altitudeLayer + " at " + ((Mote)t).exactPosition.ToString("F2")
                            + " rot " + ((Mote)t).exactRotation.ToString("0") + " scale " + ((Mote)t).ExactScale.ToString("F2"));
                    }
                    return true;
                }
                case "carrydowned":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    Pawn down = PawnAt(map, I(a[2]), I(a[3]));
                    if (p == null || down == null)
                    {
                        return true;
                    }
                    Job job = JobMaker.MakeJob(JobDefOf.CarryDownedPawnDrafted, down);
                    job.count = 1;
                    job.playerForced = true;
                    p.jobs.StartJob(job, JobCondition.InterruptForced);
                    Log.Message("[IsoRim probe] carrydowned: " + p.LabelShort + " (drafted " + p.Drafted + ") carries " + down.LabelShort + " (downed " + down.Downed + "): " + (p.CurJob == job));
                    return true;
                }
                case "tickjitter":
                {
                    Pawn p = PawnAt(map, I(a[1]), I(a[2]));
                    if (p == null)
                    {
                        return true;
                    }
                    int limit = Mathf.Clamp(I(a[0]), 1, 900), done = 0;
                    float min = F(a[3]);
                    while (done < limit && probeJitter(p.Drawer).CurrentOffset.magnitude < min)
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                    Log.Message("[IsoRim probe] tickjitter " + p.LabelShort + ": " + done + " ticks, jitter " + probeJitter(p.Drawer).CurrentOffset.ToString("F3"));
                    return true;
                }
                case "tickwarm":
                {
                    // tickwarm=N,x,z,left: game ticks one at a time (at most N) until the pawn in (x,z) is warming up a shot with
                    // at most `left` ticks to go (the shot leaves `left` ticks later; burstticks=1 burst= films it frame by frame).
                    Pawn p = PawnAt(map, I(a[1]), I(a[2]));
                    if (p == null)
                    {
                        return true;
                    }
                    int limit = Mathf.Clamp(I(a[0]), 1, 900), done = 0, left = I(a[3]);
                    while (done < limit && !(p.stances.curStance is Stance_Warmup w && w.ticksLeft <= left))
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                    Log.Message("[IsoRim probe] tickwarm " + p.LabelShort + ": " + done + " ticks, stance " + p.stances.curStance.GetType().Name
                        + (p.stances.curStance is Stance_Busy b ? " left " + b.ticksLeft : ""));
                    return true;
                }
                case "launchlog":
                    // launchlog=1: every projectile logs where its drawn flight starts when it is first drawn (Projectiles.LaunchLog).
                    Projectiles.LaunchLog = a[0] == "1";
                    return true;
                case "visuals":
                {
                    // visuals=N: the per-frame visual catch-up the game runs while unpaused (Pawn.ProcessPostTickVisuals: lean
                    // out of cover, jitter decay) for N ticks; the harness game is paused, so these never advance by themselves.
                    int n = Mathf.Clamp(I(a[0]), 1, 900);
                    CellRect all = CellRect.WholeMap(map);
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        p.ProcessPostTickVisuals(n, all);
                    }
                    return true;
                }
                case "shieldfull":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    CompShield shield = p != null && p.apparel != null ? p.apparel.WornApparel.Select(x => x.GetComp<CompShield>()).FirstOrDefault(x => x != null) : null;
                    if (shield == null)
                    {
                        Log.Warning("[IsoRim probe] shieldfull: no shield belt");
                        return true;
                    }
                    Traverse.Create(shield).Field("energy").SetValue(shield.parent.GetStatValue(StatDefOf.EnergyShieldEnergyMax));
                    Log.Message("[IsoRim probe] shieldfull " + p.LabelShort + ": energy " + shield.Energy.ToString("0.00") + ", state " + shield.ShieldState);
                    return true;
                }
                case "job":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    JobDef jd = DefDatabase<JobDef>.GetNamedSilentFail(a[2]);
                    if (p == null || jd == null)
                    {
                        Log.Warning("[IsoRim probe] job: no pawn at " + a[0] + "," + a[1] + " or no job " + a[2]);
                        return true;
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    Job job;
                    if (a.Length > 4)
                    {
                        IntVec3 tc = new IntVec3(I(a[3]), 0, I(a[4]));
                        Thing tt = tc.GetFirstPawn(map) ?? (Thing)tc.GetFirstBuilding(map);
                        job = JobMaker.MakeJob(jd, tt != null ? new LocalTargetInfo(tt) : new LocalTargetInfo(tc));
                    }
                    else
                    {
                        job = JobMaker.MakeJob(jd);
                    }
                    job.expiryInterval = 100000;
                    p.jobs.StartJob(job, JobCondition.InterruptForced);
                    Log.Message("[IsoRim probe] job " + p.LabelShort + " at " + p.Position + ": " + jd.defName + " " + (p.CurJob == job));
                    return true;
                }
                case "equip":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a[2]);
                    if (p == null || def == null || p.equipment == null)
                    {
                        Log.Warning("[IsoRim probe] equip: no pawn with equipment at " + a[0] + "," + a[1] + " or no def " + a[2]);
                        return true;
                    }
                    ThingDef stuff = a.Length > 3 ? DefDatabase<ThingDef>.GetNamedSilentFail(a[3]) : (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    if (p.equipment.Primary != null)
                    {
                        p.equipment.DestroyEquipment(p.equipment.Primary);
                    }
                    p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(def, stuff));
                    Log.Message("[IsoRim probe] equip " + p.LabelShort + " at " + p.Position + ": " + p.equipment.Primary.def.defName);
                    return true;
                }
                case "weaponsizes":
                {
                    System.Reflection.MethodInfo sizes = AccessTools.Method("IsoRim.WeaponModels:SizeReport");
                    string text = sizes != null ? (string)sizes.Invoke(null, new object[] { 1.12f / 1.5f }) : "no WeaponModels in this build";
                    foreach (string line in text.Split('\n'))
                    {
                        if (line.Length > 0)
                        {
                            Log.Message("[IsoRim probe] weaponsizes " + line);
                        }
                    }
                    return true;
                }
                case "weaponcheck":
                {
                    System.Reflection.MethodInfo report = AccessTools.Method("IsoRim.FigureWeapons:Report");
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                    {
                        if (p.GetPosture() != PawnPosture.Standing)
                        {
                            continue;
                        }
                        string drawn = report != null ? (string)report.Invoke(null, new object[] { p }) : "no FigureWeapons in this build";
                        Log.Message("[IsoRim probe] weaponcheck " + p.LabelShort + " at " + p.Position + " job " + (p.CurJob != null ? p.CurJob.def.defName : "-")
                            + " stance " + (p.stances != null && p.stances.curStance != null ? p.stances.curStance.GetType().Name : "-")
                            + " drafted " + p.Drafted + " carrying " + (p.carryTracker != null && p.carryTracker.CarriedThing != null ? p.carryTracker.CarriedThing.LabelShort : "-")
                            + " rot " + p.Rotation + " jitter " + probeJitter(p.Drawer).CurrentOffset.ToString("F3") + " lean " + p.Drawer.leaner.LeanOffset.ToString("F3")
                            + ": " + drawn);
                    }
                    return true;
                }
            }
            return false;
        }
    }
}
