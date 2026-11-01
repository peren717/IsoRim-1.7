using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness keys for food bits and smoke at the mouth (IngestBits; eatfx, 2026-11-01). Cells are map cells (U: upper floor).
    //   eatfx=0|1              IngestBits off (as the game draws them, for before shots) or on (default)
    //   eatjob=px,pz,<ThingDef> the colonist in (px,pz) undrafted; a <ThingDef> (a meal, a joint, beer) put in its cell and the
    //                          game's Ingest job ordered on it, as the player orders it (it finds a table and chair itself)
    //   eatuntil=N,K           game ticks until every pawn on an Ingest job is in its chewing toil (at most N), then K more;
    //                          drawn positions settled after (the harness keeps the game paused)
    //   eatanimal=x,z,<Kind>,<food> a tame animal and the food in its cell, Ingest started on it
    //   eatreset=1             zeroes IngestBits' counters
    //   eatclear=1             removes every ingest effect mote on the map
    //   eatcheck=1|expect|none logs the counters, every pawn eating (job, posture, seat, 3D or card, mouth height, the head on
    //                          screen) and every ingest effect mote on the map (stood, height, on screen); expect: CHECK lines,
    //                          each ingest mote near a 3D eater stood at its mouth; none: CHECK no mote was stood this pass
    public static partial class Probe
    {
        private static HashSet<ThingDef> ingestMotes;

        private static bool IsIngestMote(ThingDef def)
        {
            if (ingestMotes == null)
            {
                ingestMotes = new HashSet<ThingDef>();
                foreach (EffecterDef e in DefDatabase<EffecterDef>.AllDefsListForReading)
                {
                    if (IngestBits.IsIngestEffect(e) && e.children != null)
                    {
                        foreach (SubEffecterDef s in e.children)
                        {
                            if (s.moteDef != null)
                            {
                                ingestMotes.Add(s.moteDef);
                            }
                        }
                    }
                }
            }
            return def != null && ingestMotes.Contains(def);
        }

        private static bool Eating(Pawn p)
        {
            return p.CurJob != null && p.jobs.curDriver != null
                && (p.jobs.curDriver is JobDriver_Ingest || p.jobs.curDriver is JobDriver_SocialRelax || p.jobs.curDriver is JobDriver_FoodFeedPatient);
        }

        private static string ToilOf(Pawn p)
        {
            Toil t = p.jobs.curDriver != null ? Traverse.Create(p.jobs.curDriver).Property("CurToil").GetValue<Toil>() : null;
            return t != null ? t.debugName ?? "?" : "none";
        }

        private static bool EatKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "eatfx":
                    IngestBits.Off = a[0] == "0";
                    return true;
                case "eatreset":
                    IngestBits.Stood = 0;
                    IngestBits.Flecks = 0;
                    return true;
                case "eatclear":
                {
                    // eatclear=1: every ingest effect mote on the map removed (so a pass shows only the motes made in it)
                    int n = 0;
                    foreach (Mote mote in map.dynamicDrawManager.DrawThings.OfType<Mote>().Where(m => IsIngestMote(m.def)).ToList())
                    {
                        mote.Destroy();
                        n++;
                    }
                    Log.Message("[IsoRim probe] eatclear " + n);
                    return true;
                }
                case "eatjob":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a[2]);
                    if (p == null || def == null || def.ingestible == null)
                    {
                        Log.Warning("[IsoRim probe] eatjob: no pawn at " + a[0] + "," + a[1] + " or no ingestible " + a[2]);
                        return true;
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    if (p.needs.food != null)
                    {
                        p.needs.food.CurLevel = 0.2f;
                    }
                    Thing item = GenSpawn.Spawn(ThingMaker.MakeThing(def), p.Position, map);
                    Job job = JobMaker.MakeJob(JobDefOf.Ingest, item);
                    job.count = 1;
                    Rand.PushState(Gen.HashCombineInt(p.Position.x, p.Position.z * 7919 + 29));   // fixed seed: the same run every time
                    bool taken = p.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    Rand.PopState();
                    Log.Message("[IsoRim probe] eatjob " + def.defName + " " + p.LabelShort + " at " + p.Position + ": ordered " + taken);
                    return true;
                }
                case "eatanimal":
                {
                    // eatanimal=x,z,<PawnKindDef>,<food ThingDef>: a tame animal of that kind (fixed seed per cell) and the food in
                    // its cell, and the game's Ingest job started on it, as its own food search would give it.
                    PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(a[2]);
                    ThingDef food = DefDatabase<ThingDef>.GetNamedSilentFail(a[3]);
                    if (kind == null || food == null)
                    {
                        Log.Warning("[IsoRim probe] eatanimal: no kind " + a[2] + " or food " + a[3]);
                        return true;
                    }
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    Rand.PushState(Gen.HashCombineInt(c.x, c.z * 7919 + 23));
                    Pawn an = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, fixedBiologicalAge: 5f, fixedChronologicalAge: 5f));
                    Rand.PopState();
                    GenSpawn.Spawn(an, c, map, Rot4.South);
                    if (an.needs.food != null)
                    {
                        an.needs.food.CurLevel = 0.1f;
                    }
                    Thing item = GenSpawn.Spawn(ThingMaker.MakeThing(food), c, map);
                    Job job = JobMaker.MakeJob(JobDefOf.Ingest, item);
                    job.count = 1;
                    an.jobs.StartJob(job, JobCondition.InterruptForced);
                    Log.Message("[IsoRim probe] eatanimal " + kind.defName + " " + food.defName + " at " + c + ": " + (an.CurJobDef != null ? an.CurJobDef.defName : "none") + " 3D " + Patches.DrawnAsModel(an));
                    return true;
                }
                case "eatuntil":
                {
                    int limit = Mathf.Clamp(I(a[0]), 1, 2000), more = a.Length > 1 ? Mathf.Clamp(I(a[1]), 0, 900) : 0, n = 0;
                    if (a.Length > 4)
                    {
                        // ,x,z,size: the camera looks there while the game ticks (the game makes motes only in view: the view
                        // rect the game caches for this frame is read anew)
                        Find.CameraDriver.SetRootPosAndSize(new Vector3(F(a[2]), 0f, F(a[3])), F(a[4]));
                        Traverse.Create(Find.CameraDriver).Field("lastViewRectGetFrame").SetValue(-1);
                        Log.Message("[IsoRim probe] eatuntil view " + Find.CameraDriver.CurrentViewRect);
                    }
                    System.Func<bool> chewing = () => map.mapPawns.AllPawnsSpawned.Where(Eating).All(p => ToilOf(p) == "ChewIngestible" || p.jobs.curDriver is JobDriver_SocialRelax);
                    // the ticks on a fixed seed (this key's arguments): chew spots, mote spread, the same every run
                    Rand.PushState(Gen.HashCombineInt(GenText.StableStringHash(string.Join(",", a)), 31));
                    try
                    {
                        while (n < limit && !chewing())
                        {
                            Find.TickManager.DoSingleTick();
                            n++;
                        }
                        for (int i = 0; i < more; i++)
                        {
                            Find.TickManager.DoSingleTick();
                        }
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                    Log.Message("[IsoRim probe] eatuntil: " + n + " ticks to chewing (" + chewing() + "), then " + more);
                    return true;
                }
                case "eatcheck":
                {
                    string mode = a.Length > 0 ? a[0] : "1";
                    Log.Message("[IsoRim probe] eatcheck stood " + IngestBits.Stood + " flecks " + IngestBits.Flecks + " motesStood(all) " + Emitters.MotesStood
                        + " off " + IngestBits.Off + " view " + View3D.Ready + " pawns3D " + IsoRimMod.Settings.pawns3D + " floors " + Floors.On);
                    List<Pawn> eaters = map.mapPawns.AllPawnsSpawned.Where(Eating).ToList();
                    foreach (Pawn p in eaters)
                    {
                        float h;
                        bool ok = p.RaceProps.Humanlike ? People.MouthHeight(p, out h) : Animals.MouthHeight(p, out h);
                        Poses.Seat s;
                        bool seated = Poses.Seated(p, out s);
                        Vector3 at = p.DrawPos;
                        Log.Message("[IsoRim probe] eatcheck eater " + p.LabelShort + " " + p.Position + " job " + p.CurJobDef.defName + " toil " + ToilOf(p)
                            + " posture " + p.GetPosture() + " seated " + seated + (seated ? " on " + s.thing.def.defName + " lift " + s.Lift.ToString("0.00") : "")
                            + " 3D " + Patches.DrawnAsModel(p) + " mouth " + (ok ? h.ToString("0.000") : "-") + " floorRows " + Floors.ExtraMatrix(p).Equals(Matrix4x4.identity)
                            + " mouth px " + (ok ? Px(new Vector3(at.x, h, at.z), at.y) : "-"));
                    }
                    int ingest = 0, stood = 0, near3d = 0, pass = 0;
                    foreach (Mote mote in map.dynamicDrawManager.DrawThings.OfType<Mote>().ToList())
                    {
                        if (!IsIngestMote(mote.def))
                        {
                            continue;
                        }
                        ingest++;
                        float h;
                        bool flat;
                        bool st = Emitters.MoteStandOf(mote, out h, out flat);
                        if (st)
                        {
                            stood++;
                        }
                        Vector3 at = mote.DrawPos;
                        Pawn eater = eaters.OrderBy(p => (p.DrawPos - at).MagnitudeHorizontal()).FirstOrDefault();
                        string line = "[IsoRim probe] eatcheck mote " + mote.def.defName + " " + mote.Position + " age " + mote.AgeSecs.ToString("0.00") + " stood " + st
                            + (st ? " h " + h.ToString("0.000") : "") + " px " + Px(new Vector3(at.x, st ? h : 0f, at.z), eater != null ? eater.DrawPos.y : at.y)
                            + " eater " + (eater != null ? eater.LabelShort : "-");
                        Log.Message(line);
                        float mh;
                        if (mode == "expect" && eater != null && Patches.DrawnAsModel(eater) && (eater.DrawPos - at).MagnitudeHorizontal() < 1.5f
                            && mote.AgeSecs < 1.5f && (eater.RaceProps.Humanlike ? People.MouthHeight(eater, out mh) : Animals.MouthHeight(eater, out mh)))
                        {
                            near3d++;
                            bool good = st && !flat && Mathf.Abs(h - mh) < 0.02f;
                            if (good)
                            {
                                pass++;
                            }
                            Log.Message("[IsoRim probe] CHECK " + (good ? "PASS" : "FAIL") + " " + mote.def.defName + " of " + eater.LabelShort + " stood at the mouth "
                                + mh.ToString("0.000") + " (" + (st ? h.ToString("0.000") : "not stood") + ")");
                        }
                    }
                    Log.Message("[IsoRim probe] eatcheck motes " + ingest + " stood " + stood + (mode == "expect" ? " near 3D eaters " + near3d + " pass " + pass : ""));
                    if (mode == "expect" && near3d == 0)
                    {
                        Log.Message("[IsoRim probe] CHECK FAIL no fresh ingest mote by a 3D eater");
                    }
                    if (mode == "none")
                    {
                        Log.Message("[IsoRim probe] CHECK " + (IngestBits.Stood == 0 && IngestBits.Flecks == 0 ? "PASS" : "FAIL") + " no mote stood this pass (" + IngestBits.Stood + ")");
                    }
                    return true;
                }
            }
            return false;
        }
    }
}
