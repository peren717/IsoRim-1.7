using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for wounds on 3D figures and animals (WoundMarks). Real damage and real tending jobs, run with real ticks.
    //   woundhit=x,z,<DamageDef>,<amount>,<count>[,<BodyPartDef>|-[,<seed>]]
    //                     the pawn in the cell takes count hits of that damage the game's way (Thing.TakeDamage with a
    //                     DamageInfo, the damage worker picks the part unless one is named), each under its own fixed seed
    //   woundstrip=x,z    every garment of the pawn in the cell taken off and destroyed (a bare body)
    //   woundhold=1       every free colonist drafted (they stand still while the game ticks)
    //   woundtend=dx,dz,px,pz  the colonist in (dx,dz) ordered to tend the pawn in (px,pz) without medicine (TendPatient,
    //                     as the player's order; run tick= after it)
    //   woundstop=x,z     the pawn in the cell stops its jobs (a shooter after its burst)
    //   wounddoctorsgone=1  the doctors ordered by woundtend= taken off the map (for clear shots of their patients)
    //   woundclean=x0,z0,x1,z1  every filth in the rectangle, every fleck and mote on the map removed (blood, tend reports), for clean shots
    //   woundmarks=0|1    the 3D wound marks off or on (before/after in one run)
    //   woundcheck=1      logs every pawn on the map with wounds: the game's wound hediffs and the marks placed
    public static partial class Probe
    {
        private static readonly System.Collections.Generic.List<Pawn> doctors = new System.Collections.Generic.List<Pawn>();

        private static bool WoundKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "woundhit":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    DamageDef def = DefDatabase<DamageDef>.GetNamedSilentFail(a[2]);
                    if (p == null || def == null)
                    {
                        Log.Warning("[IsoRim probe] woundhit: no pawn or damage " + a[2]);
                        return true;
                    }
                    float amount = F(a[3]);
                    int count = Mathf.Clamp(I(a[4]), 1, 50);
                    BodyPartDef partDef = a.Length > 5 && a[5] != "-" ? DefDatabase<BodyPartDef>.GetNamedSilentFail(a[5]) : null;
                    int seed = a.Length > 6 ? I(a[6]) : 7;
                    for (int k = 0; k < count && !p.Dead; k++)
                    {
                        BodyPartRecord part = partDef != null ? p.health.hediffSet.GetNotMissingParts().FirstOrDefault(x => x.def == partDef) : null;
                        Rand.PushState(Gen.HashCombineInt(seed, k * 7919 + p.Position.x * 131 + p.Position.z));   // (not the thing id: the same hits every run)
                        try
                        {
                            DamageInfo dinfo = new DamageInfo(def, amount, 0f, -1f, null, part);
                            p.TakeDamage(dinfo);
                        }
                        finally
                        {
                            Rand.PopState();
                        }
                    }
                    Log.Message("[IsoRim probe] woundhit " + p.LabelShort + " (" + p.def.defName + ") " + def.defName + " x" + count + ": downed " + p.Downed + ", dead " + p.Dead
                        + ", wounds: " + string.Join("; ", p.health.hediffSet.hediffs.Where(h => h.def.displayWound && h.Part != null).Select(h => h.def.defName + "@" + h.Part.def.defName)));
                    return true;
                }
                case "woundstrip":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p != null && p.apparel != null)
                    {
                        p.apparel.DestroyAll();
                        p.Drawer.renderer.SetAllGraphicsDirty();
                    }
                    return true;
                }
                case "woundhold":
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.Where(x => !x.Downed && x.drafter != null))
                    {
                        p.drafter.Drafted = true;
                    }
                    return true;
                case "woundtend":
                {
                    Pawn doctor = PawnAt(map, I(a[0]), I(a[1]));
                    Pawn patient = PawnAt(map, I(a[2]), I(a[3]));
                    if (doctor == null || patient == null)
                    {
                        return true;
                    }
                    Job job = JobMaker.MakeJob(JobDefOf.TendPatient, patient);
                    job.count = 1;
                    job.playerForced = true;
                    job.draftedTend = doctor.Drafted;
                    doctor.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    doctors.Add(doctor);
                    Log.Message("[IsoRim probe] woundtend " + doctor.LabelShort + " -> " + patient.LabelShort + ": job " + (doctor.CurJobDef != null ? doctor.CurJobDef.defName : "-"));
                    return true;
                }
                case "wounddoctorsgone":
                    // wounddoctorsgone=1: the doctors of woundtend= taken off the map (they stand over their patients).
                    foreach (Pawn d in doctors)
                    {
                        if (!d.Destroyed)
                        {
                            d.Destroy(DestroyMode.Vanish);
                        }
                    }
                    doctors.Clear();
                    return true;
                case "woundstop":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p != null && p.jobs != null)
                    {
                        p.jobs.StopAll();
                        p.pather?.StopDead();
                    }
                    return true;
                }
                case "woundclean":
                {
                    // every filth in the rectangle (the hits' blood)
                    CellRect r = CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3]));
                    int gone = 0;
                    foreach (Filth f in map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).OfType<Filth>().ToList())
                    {
                        if (r.Contains(f.Position))
                        {
                            f.Destroy(DestroyMode.Vanish);
                            gone++;
                        }
                    }
                    // and the hits' blood splashes (flecks, which stay while the harness game is paused)
                    int flecks = 0;
                    foreach (FleckSystem fs in map.flecks.Systems)
                    {
                        foreach (string list in new[] { "dataGametime", "dataRealtime" })
                        {
                            if (HarmonyLib.Traverse.Create(fs).Field(list).GetValue() is System.Collections.IList live)
                            {
                                flecks += live.Count;
                                live.Clear();
                            }
                        }
                    }
                    // and the tend reports over the patients (text motes)
                    int motes = 0;
                    foreach (Mote m in map.listerThings.AllThings.OfType<Mote>().ToList())
                    {
                        m.Destroy(DestroyMode.Vanish);
                        motes++;
                    }
                    Log.Message("[IsoRim probe] woundclean: " + gone + " filth, " + flecks + " flecks, " + motes + " motes removed");
                    return true;
                }
                case "woundmarks":
                    WoundMarks.Enabled = a[0] == "1";
                    return true;
                case "woundcheck":
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        var wounds = p.health.hediffSet.hediffs.Where(h => h.def.displayWound && h.Part != null && h.Visible).ToList();
                        if (wounds.Count == 0)
                        {
                            continue;
                        }
                        Log.Message("[IsoRim probe] woundcheck " + p.LabelShort + " (" + p.def.defName + ") at " + p.Position + " rot " + p.Rotation + " posture " + p.GetPosture()
                            + " downed " + p.Downed + ": " + string.Join("; ", wounds.Select(h => h.def.defName + "@" + h.Part.def.defName + (h.IsTended() ? " tended" : "")
                            + (h is Hediff_MissingPart mp ? (mp.IsFresh ? " fresh" : " old") : "")))
                            + " | 3D: " + WoundMarks.Report(p));
                    }
                    return true;
            }
            return false;
        }
    }
}
