using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness keys for icons over pawns (Overhead; overhead, 2026-10-23):
    //   incident=Def[,x,z]      the incident fired on the map as the storyteller would (forced: no earliest day, points by
    //                           the colony); with x,z the pawns it brought who show the game's question mark (a creepjoiner,
    //                           a trader) are moved there (in a row east) to be filmed, their lord kept
    //   mentalstate=x,z,Def     the colonist in the cell starts that mental state
    //   hediff=x,z,Def,sev      the colonist in the cell gets that hediff at that severity (AlcoholHigh: drunk, its effecter)
    //   throwicon=x,z,FleckDef  the game's icon throw from that cell (FleckMaker.ThrowMetaIcon: the downed pawn's "!" is IncapIcon)
    //   tickmote=Def,max        ticks (at most max) until a mote of that def is spawned (Mote_BerserkBit)
    //   overheadfix=0|1         0: the icons as before the fix (flat), for before / after sheets
    //   tickfleck=Def,max       ticks (at most max) until an icon of that fleck def was thrown from a pawn's cell
    //   overheadcheck=1         logs every spawned humanlike: posture, downed, question mark, mental state, the icon height,
    //                           and the overhead icons stood up so far by def
    public static partial class Probe
    {
        private static bool OverheadKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "incident":
                {
                    IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail(a[0]);
                    if (def == null)
                    {
                        Log.Warning("[IsoRim probe] incident: no def " + a[0]);
                        return true;
                    }
                    HashSet<Pawn> before = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, map);
                    parms.forced = true;
                    bool ok = def.Worker.TryExecute(parms);
                    Log.Message("[IsoRim probe] incident " + def.defName + ": " + (ok ? "fired" : "did not fire"));
                    if (ok && a.Length >= 3)
                    {
                        OverheadBring(map, before, new IntVec3(I(a[1]), 0, I(a[2])));
                    }
                    return true;
                }
                case "bringmarked":
                {
                    // bringmarked=x,z: every spawned pawn showing the question mark moved to x,z (in a row east)
                    OverheadBring(map, new HashSet<Pawn>(), new IntVec3(I(a[0]), 0, I(a[1])));
                    return true;
                }
                case "mentalstate":
                {
                    Pawn p = map.thingGrid.ThingsListAt(new IntVec3(I(a[0]), 0, I(a[1]))).OfType<Pawn>().FirstOrDefault();
                    MentalStateDef def = DefDatabase<MentalStateDef>.GetNamedSilentFail(a[2]);
                    if (p != null && def != null)
                    {
                        if (p.drafter != null)
                        {
                            p.drafter.Drafted = false;
                        }
                        bool ok = p.mindState.mentalStateHandler.TryStartMentalState(def, null, true);
                        Log.Message("[IsoRim probe] mentalstate " + p.LabelShort + " " + def.defName + ": " + ok);
                    }
                    return true;
                }
                case "throwicon":
                {
                    // throwicon=x,z,FleckDef: the game's own icon throw (FleckMaker.ThrowMetaIcon) from that cell, as the downed
                    // pawn's wiggler does every 200 ticks on frames the pawn is in view
                    FleckDef def = DefDatabase<FleckDef>.GetNamedSilentFail(a[2]);
                    if (def != null)
                    {
                        FleckMaker.ThrowMetaIcon(new IntVec3(I(a[0]), 0, I(a[1])), map, def);
                    }
                    return true;
                }
                case "tickmote":
                {
                    // tickmote=Def,max: ticks (at most max) until a mote of that def is spawned (a berserk pawn's effecter bits)
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a[0]);
                    int max = Mathf.Clamp(I(a[1]), 1, 3000), n = 0;
                    while (def != null && n < max && map.listerThings.ThingsOfDef(def).Count == 0)
                    {
                        Find.TickManager.DoSingleTick();
                        n++;
                    }
                    Log.Message("[IsoRim probe] tickmote " + a[0] + ": " + n + " ticks, motes " + (def != null ? map.listerThings.ThingsOfDef(def).Count : 0));
                    return true;
                }
                case "overheadfix":
                    Overhead.Off = a[0] == "0";
                    return true;
                case "hediff":
                {
                    // hediff=x,z,Def,severity: the colonist in the cell gets that hediff (a drunk one: AlcoholHigh 0.6)
                    Pawn p = map.thingGrid.ThingsListAt(new IntVec3(I(a[0]), 0, I(a[1]))).OfType<Pawn>().FirstOrDefault();
                    HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(a[2]);
                    if (p != null && def != null)
                    {
                        Hediff h = HediffMaker.MakeHediff(def, p);
                        h.Severity = F(a[3]);
                        p.health.AddHediff(h);
                        Log.Message("[IsoRim probe] hediff " + p.LabelShort + " " + def.defName + " " + h.Severity.ToString("0.00"));
                    }
                    return true;
                }
                case "tickfleck":
                {
                    FleckDef def = DefDatabase<FleckDef>.GetNamedSilentFail(a[0]);
                    int max = Mathf.Clamp(I(a[1]), 1, 3000);
                    int start = Overhead.ThrownOf(def), n = 0;
                    while (n < max && Overhead.ThrownOf(def) == start)
                    {
                        Find.TickManager.DoSingleTick();
                        n++;
                    }
                    Log.Message("[IsoRim probe] tickfleck " + a[0] + ": " + n + " ticks, thrown " + (Overhead.ThrownOf(def) - start));
                    return true;
                }
                case "overheadcheck":
                {
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps.Humanlike).OrderBy(x => x.Position.x))
                    {
                        Log.Message("[IsoRim probe] overheadcheck " + p.LabelShort + " at " + p.Position + ": posture " + p.GetPosture() + ", downed " + p.Downed
                            + ", in bed " + (p.CurrentBed() != null) + ", question mark " + p.ShouldShowQuestionMark() + ", mental " + (p.MentalStateDef?.defName ?? "-")
                            + ", wants rescue " + HealthAIUtility.WantsToBeRescued(p) + ", icon height " + Overhead.Height(p).ToString("0.00"));
                    }
                    Log.Message("[IsoRim probe] overheadcheck: icons stood up " + Overhead.Thrown + " (" + Overhead.ThrownList() + ")");
                    return true;
                }
            }
            return false;
        }

        private static void OverheadBring(Map map, HashSet<Pawn> before, IntVec3 at)
        {
            int k = 0;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => !before.Contains(x) && x.ShouldShowQuestionMark()).ToList())
            {
                IntVec3 c = CellFinder.StandableCellNear(at + new IntVec3(2 * k, 0, 0), map, 3f);
                if (!c.IsValid)
                {
                    continue;
                }
                p.pather?.StopDead();
                p.Position = c;
                p.Notify_Teleported(true, true);
                Log.Message("[IsoRim probe] brought " + p.LabelShort + " (" + p.kindDef.defName + ", creepjoiner " + (p.creepjoiner != null) + ", trader " + p.CanTradeNow + ") to " + c);
                k++;
            }
        }
    }
}
