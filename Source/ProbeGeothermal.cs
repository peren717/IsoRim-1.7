using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Harness keys for the geothermal generator remake (real construction and the game's own steam sprays):
    //   buildall=N                       every free colonist made a builder (Construction 15, construction work first), then
    //                                    game ticks until no blueprint or frame of the player's is left on the map (at most N);
    //                                    the storyteller is held off meanwhile
    //   tickspray=x,z,N                  game ticks until the steam generator in the cell is spraying (its IntermittentSteamSprayer,
    //                                    CompPowerPlantSteam), then 40 more so the puffs are up (at most N); logs how many puffs
    //                                IsoRim stood up at model nozzles (Emitters.Count)
    public static partial class Probe
    {
        private static bool GeoKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "buildall":
                {
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
                    {
                        if (p.drafter != null)
                        {
                            p.drafter.Drafted = false;
                        }
                        SkillRecord sk = p.skills?.GetSkill(SkillDefOf.Construction);
                        if (sk != null)
                        {
                            sk.Level = 15;
                        }
                        if (p.workSettings != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                        {
                            p.workSettings.EnableAndInitialize();
                            p.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                        }
                    }
                    int limit = System.Math.Max(1, I(a[0])), done = 0;
                    System.Func<bool> left = () => map.listerThings.AllThings.Any(t => t.Faction == Faction.OfPlayer && (t is Blueprint || t is Frame));
                    HoldStoryteller(() =>
                    {
                        while (done < limit && left())
                        {
                            Find.TickManager.DoSingleTick();
                            done++;
                        }
                    });
                    Log.Message("[IsoRim probe] buildall: " + done + " ticks, all built " + !left() + "; buildings: "
                        + string.Join(", ", map.listerBuildings.allBuildingsColonist.Select(b => b.def.defName + " " + b.Position)));
                    TweenToRoot(map);
                    return true;
                }
                case "tickspray":
                {
                    Building b = new IntVec3(I(a[0]), 0, I(a[1])).GetThingList(map).OfType<Building>().FirstOrDefault(t => t.GetComp<CompPowerPlantSteam>() != null);   // not the geyser under it
                    CompPowerPlantSteam comp = b?.GetComp<CompPowerPlantSteam>();
                    if (comp == null)
                    {
                        Log.Warning("[IsoRim probe] tickspray: no steam generator at " + a[0] + "," + a[1]);
                        return true;
                    }
                    int limit = System.Math.Max(1, I(a[2])), done = 0, after = -1;
                    System.Func<int> left = () =>
                    {
                        object sprayer = Traverse.Create(comp).Field("steamSprayer").GetValue();
                        return sprayer == null ? 0 : Traverse.Create(sprayer).Field("sprayTicksLeft").GetValue<int>();
                    };
                    HoldStoryteller(() =>
                    {
                        while (done < limit && (after < 0 || after < 40))
                        {
                            Find.TickManager.DoSingleTick();
                            done++;
                            if (after >= 0)
                            {
                                after++;
                            }
                            else if (left() > 0)
                            {
                                after = 0;
                            }
                        }
                    });
                    Log.Message("[IsoRim probe] tickspray " + b.def.defName + " " + b.Position + ": " + done + " ticks, spraying " + (left() > 0)
                        + ", building centre " + b.TrueCenter().ToString("F2") + ", nozzle puffs " + Emitters.Count);
                    TweenToRoot(map);
                    return true;
                }
            }
            return false;
        }
    }
}
