using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness keys for Api 24 (young crops, sheet colours), real game flows:
    //   trackyoung=1                     from now on note how each plant with a young stage is built (StateModels.YoungBaked)
    //   sowfield=x0,z0,x1,z1,<PlantDef>  a growing zone over the rectangle set to that plant; every free colonist undrafted,
    //                                    Plants skill 15 and Growing work first, so they sow it (run ticksown after it)
    //   ticksown=x0,z0,x1,z1,N           game ticks until every zone cell in the rectangle holds the zone's plant (at most N)
    //   tickgrow=x0,z0,x1,z1,<g>,N       game ticks (the plants' own growth, TickLong) until every plant in the rectangle has
    //                                    growth >= g (at most N); the storyteller is held off while it runs (no raids or events)
    //   plantcheck=x0,z0,x1,z1           logs each plant: def, growth, harvestable, young now (StateModels.Immature), built young
    //   makeguest=x,z,prisoner|slave     the colonist in the cell made a prisoner / slave of the player (from the pirates)
    //   legcut=x,z                       the colonist in the cell gets a small leg cut (needs tending: medical beds valid for them)
    //   bedtype=x,z,colonist|prisoner|slave[,med|nomed]  the bed's owner type and medical toggle, as the player's buttons
    //   bedtint=1                        logs each bed: owner type, medical, DrawColorTwo, the sheet tint IsoRim takes
    public static partial class Probe
    {
        private static bool Young24Key(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "trackyoung":
                    StateModels.TrackYoung = a[0] != "0";
                    return true;
                case "sowfield":
                {
                    ThingDef plantDef = DefDatabase<ThingDef>.GetNamedSilentFail(a[4]);
                    if (plantDef == null || plantDef.plant == null)
                    {
                        Log.Warning("[IsoRim probe] sowfield: no plant " + a[4]);
                        return true;
                    }
                    Zone_Growing z = new Zone_Growing(map.zoneManager);
                    map.zoneManager.RegisterZone(z);
                    int cells = 0;
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        if (c.InBounds(map) && map.zoneManager.ZoneAt(c) == null)
                        {
                            z.AddCell(c);
                            cells++;
                        }
                    }
                    z.SetPlantDefToGrow(plantDef);
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
                    {
                        if (p.drafter != null)
                        {
                            p.drafter.Drafted = false;
                        }
                        SkillRecord sk = p.skills?.GetSkill(SkillDefOf.Plants);
                        if (sk != null)
                        {
                            sk.Level = 15;
                        }
                        if (p.workSettings != null)
                        {
                            p.workSettings.EnableAndInitialize();
                            p.workSettings.SetPriority(WorkTypeDefOf.Growing, 1);
                        }
                    }
                    Log.Message("[IsoRim probe] sowfield " + plantDef.defName + ": " + cells + " cells, sowMinSkill " + plantDef.plant.sowMinSkill);
                    return true;
                }
                case "ticksown":
                {
                    CellRect r = Rect(a, 0);
                    int limit = Mathf.Max(1, I(a[4])), done = 0;
                    System.Func<bool> sown = () => r.Cells.All(c => !c.InBounds(map) || !(map.zoneManager.ZoneAt(c) is Zone_Growing g)
                        || c.GetPlant(map)?.def == g.GetPlantDefToGrow());
                    HoldStoryteller(() =>
                    {
                        while (done < limit && !sown())
                        {
                            Find.TickManager.DoSingleTick();
                            done++;
                        }
                    });
                    Log.Message("[IsoRim probe] ticksown: " + done + " ticks, all sown " + sown());
                    TweenToRoot(map);
                    return true;
                }
                case "tickgrow":
                {
                    CellRect r = Rect(a, 0);
                    float target = float.Parse(a[4], System.Globalization.CultureInfo.InvariantCulture);
                    int limit = Mathf.Max(1, I(a[5])), done = 0;
                    List<Plant> plants = r.Cells.Where(c => c.InBounds(map)).Select(c => c.GetPlant(map)).Where(p => p != null).ToList();
                    float from = plants.Count > 0 ? plants.Min(p => p.Growth) : 0f;
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    HoldStoryteller(() =>
                    {
                        while (done < limit && plants.Any(p => !p.Destroyed && p.Growth < target))
                        {
                            Find.TickManager.DoSingleTick();
                            done++;
                        }
                    });
                    Log.Message("[IsoRim probe] tickgrow: " + done + " ticks (" + sw.ElapsedMilliseconds + " ms), " + plants.Count + " plants, growth "
                        + from.ToString("0.000") + " -> " + (plants.Count > 0 ? plants.Where(p => !p.Destroyed).DefaultIfEmpty().Min(p => p != null ? p.Growth : 0f) : 0f).ToString("0.000")
                        + ", hour " + GenLocalDate.HourFloat(map).ToString("0.0") + ", outdoor temp " + map.mapTemperature.OutdoorTemp.ToString("0.0"));
                    TweenToRoot(map);
                    return true;
                }
                case "plantcheck":
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        Plant p = c.InBounds(map) ? c.GetPlant(map) : null;
                        if (p == null)
                        {
                            continue;
                        }
                        bool? built = StateModels.YoungBaked(p);
                        ModelEntry entry;
                        bool hasYoung = ModelLibrary.TryGet(p.def, out entry) && entry.immature != null;
                        Log.Message("[IsoRim probe] plantcheck " + p.def.defName + " " + c + ": growth " + p.Growth.ToString("0.000") + " (min harvest "
                            + p.def.plant.harvestMinGrowth.ToString("0.00") + "), harvestable " + p.HarvestableNow + ", young stage " + (hasYoung ? "yes" : "none")
                            + ", young now " + StateModels.Immature(p) + ", built " + (built == null ? "not noted" : built.Value ? "young" : "mature"));
                    }
                    return true;
                case "makeguest":
                {
                    Pawn gp = map.thingGrid.ThingsListAt(new IntVec3(I(a[0]), 0, I(a[1]))).OfType<Pawn>().FirstOrDefault();
                    Faction pirates = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Pirate)
                        ?? Find.FactionManager.AllFactions.FirstOrDefault(f => !f.IsPlayer && f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction);
                    if (gp == null || pirates == null)
                    {
                        Log.Warning("[IsoRim probe] makeguest: no pawn at " + a[0] + "," + a[1] + " or no hostile faction");
                        return true;
                    }
                    if (gp.drafter != null)
                    {
                        gp.drafter.Drafted = false;
                    }
                    gp.SetFaction(pirates);
                    gp.guest.SetGuestStatus(Faction.OfPlayer, a[2] == "slave" ? GuestStatus.Slave : GuestStatus.Prisoner);
                    Log.Message("[IsoRim probe] makeguest " + gp.LabelShort + ": prisoner " + gp.IsPrisonerOfColony + ", slave " + gp.IsSlaveOfColony);
                    return true;
                }
                case "legcut":
                {
                    // legcut=x,z: the colonist in the cell gets a small cut on a leg (a real wound needing tending, not downed), so a
                    // medical bed is valid for them (RestUtility.CanUseBedNow: HealthAIUtility.ShouldSeekMedicalRest).
                    Pawn hp = map.thingGrid.ThingsListAt(new IntVec3(I(a[0]), 0, I(a[1]))).OfType<Pawn>().FirstOrDefault();
                    BodyPartRecord leg = hp?.health.hediffSet.GetNotMissingParts().FirstOrDefault(p => p.def == BodyPartDefOf.Leg);
                    if (hp == null || leg == null)
                    {
                        Log.Warning("[IsoRim probe] legcut: no pawn with a leg at " + a[0] + "," + a[1]);
                        return true;
                    }
                    Rand.PushState(2401);
                    hp.TakeDamage(new DamageInfo(DamageDefOf.Cut, 4f, 999f, -1f, null, leg));
                    Rand.PopState();
                    Log.Message("[IsoRim probe] legcut " + hp.LabelShort + ": downed " + hp.Downed + ", seeks medical rest " + HealthAIUtility.ShouldSeekMedicalRest(hp));
                    return true;
                }
                case "bedtype":
                {
                    Building_Bed bed = map.thingGrid.ThingsListAt(new IntVec3(I(a[0]), 0, I(a[1]))).OfType<Building_Bed>().FirstOrDefault();
                    if (bed == null)
                    {
                        Log.Warning("[IsoRim probe] bedtype: no bed at " + a[0] + "," + a[1]);
                        return true;
                    }
                    bed.ForOwnerType = a[2] == "prisoner" ? BedOwnerType.Prisoner : a[2] == "slave" ? BedOwnerType.Slave : BedOwnerType.Colonist;
                    if (a.Length > 3)
                    {
                        bed.Medical = a[3] == "med";
                    }
                    // As the player's toggle does after setting the type (Building_Bed.SetBedOwnerTypeByInterface): the bed's district
                    // and room are told, so the room becomes (or stops being) a prison cell.
                    District district = bed.GetDistrict();
                    Room room = district?.Room;
                    if (room != null && !room.TouchesMapEdge)
                    {
                        district.Notify_RoomShapeOrContainedBedsChanged();
                        room.Notify_RoomShapeChanged();
                    }
                    return true;
                }
                case "bedtint":
                    foreach (Building_Bed bed in map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().ToList())
                    {
                        Color two = bed.DrawColorTwo, tint = R3DTextures.SheetTint(bed);
                        ModelEntry be;
                        bool sheet = ModelLibrary.TryGet(bed.def, out be) && be.HasModel && R3DTextures.HasSheet(be.variants[0]);
                        Pawn sleeper = bed.CurOccupants.FirstOrDefault();
                        Log.Message("[IsoRim probe] bedtint " + bed.def.defName + " " + bed.Position + ": owner " + bed.ForOwnerType + ", medical " + bed.Medical
                            + ", DrawColorTwo " + Hex(two) + ", sheet tint " + Hex(tint) + ", model sheet parts " + sheet
                            + ", sleeper " + (sleeper == null ? "none" : sleeper.LabelShort + (sleeper.IsPrisonerOfColony ? " (prisoner)" : sleeper.IsSlaveOfColony ? " (slave)" : "")
                            + " " + sleeper.GetPosture()));
                        if (bed.ForPrisoners)
                        {
                            Room pr = bed.GetRoom();
                            Log.Message("[IsoRim probe] bedtint   room " + (pr == null ? "none" : "cells " + pr.CellCount + ", proper " + pr.ProperRoom + ", huge " + pr.IsHuge + ", role " + pr.Role?.defName + ", prison " + pr.IsPrisonCell + ", edge " + pr.TouchesMapEdge));
                            Log.Message("[IsoRim probe] bedtint   prison cell " + bed.Position.IsInPrisonCell(map) + "; guests: " + string.Join(", ", map.mapPawns.AllPawnsSpawned.Where(p => p.IsPrisonerOfColony).Select(p => p.LabelShort + " " + p.Position + " job " + (p.CurJobDef?.defName ?? "none") + " can use " + RestUtility.CanUseBedNow(bed, p, false))));
                        }
                    }
                    return true;
            }
            return false;
        }

        private static string Hex(Color c)
        {
            return ColorUtility.ToHtmlStringRGB(c);
        }

        // The storyteller held off while fn runs game ticks (no raids or other events in a test fixture), then restored.
        private static void HoldStoryteller(System.Action fn)
        {
            Storyteller st = Find.Storyteller;
            List<StorytellerComp> comps = st != null ? st.storytellerComps : null;
            List<StorytellerComp> kept = comps != null ? new List<StorytellerComp>(comps) : null;
            comps?.Clear();
            try
            {
                fn();
            }
            finally
            {
                if (kept != null)
                {
                    comps.AddRange(kept);
                }
            }
        }

        private static void TweenToRoot(Map map)
        {
            foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
            {
                tp.Drawer.tweener.ResetTweenedPosToRoot();
            }
        }
    }
}
