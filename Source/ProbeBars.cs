using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Probe keys for the work progress bars (Bars).
    //   craft=bx,bz,<RecipeDef>,px,pz,<seed>,<ingredient>:<count>[;<ingredient>:<count>...]
    //       a new colonist (generated from the seed, able to do the bench's work) stands in cell px,pz with the
    //       ingredients at its feet; a bill of the recipe is added to the bench in cell bx,bz and the colonist is ordered
    //       to do it as a player's "prioritize" order (the bench's WorkGiver_DoBill job): fetching, carrying and working
    //       are the game's own job (run tick= after it).
    //   barcheck=1: every progress bar on the map: its thing, place, progress, and the last card IsoRim drew.
    public static partial class Probe
    {
        private static bool BarKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "craft":
                    Craft(map, a);
                    return true;
                case "barshow":
                    Bars.ShowAll = a[0] == "1";
                    return true;
                case "barfix":
                    Bars.Off = a[0] == "0";
                    return true;
                case "barcheck":
                    foreach (MoteProgressBar bar in map.dynamicDrawManager.DrawThings.OfType<MoteProgressBar>())
                    {
                        Log.Message("[IsoRim probe] barcheck " + bar.def.defName + " on " + (bar.link1.Target.HasThing ? bar.link1.Target.Thing.def.defName + " " + bar.link1.Target.Thing.Position : bar.link1.Target.Cell.ToString())
                            + " at " + bar.exactPosition.ToString("F2") + " progress " + bar.progress.ToString("0.00") + " offsetZ " + bar.offsetZ.ToString("0.00")
                            + " | cards " + Bars.Drawn + " last hinge " + Bars.LastPivot.ToString("F2") + " height " + Bars.LastHeight.ToString("0.00")
                            + " | root size " + Find.CameraDriver.RootSize.ToString("0.0") + " zoom " + Find.CameraDriver.CurrentZoom);
                    }
                    return true;
            }
            return false;
        }

        private static void Craft(Map map, string[] a)
        {
            IntVec3 bc = new IntVec3(I(a[0]), 0, I(a[1]));
            Building bench = bc.GetFirstBuilding(map);
            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(a[2]);
            IntVec3 pc = new IntVec3(I(a[3]), 0, I(a[4]));
            if (!(bench is IBillGiver giver) || recipe == null)
            {
                Log.Warning("[IsoRim probe] craft: no bill giver at " + bc + " or no recipe " + a[2]);
                return;
            }
            WorkGiverDef wgDef = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(w => w.fixedBillGiverDefs != null
                && w.fixedBillGiverDefs.Contains(bench.def) && w.Worker is WorkGiver_DoBill);
            if (wgDef == null)
            {
                Log.Warning("[IsoRim probe] craft: no bill work giver for " + bench.def.defName);
                return;
            }
            Pawn worker = null;
            Rand.PushState(I(a[5]));
            try
            {
                for (int i = 0; i < 30 && worker == null; i++)
                {
                    Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, fixedBiologicalAge: 30f));
                    if (!p.WorkTypeIsDisabled(wgDef.workType))
                    {
                        worker = p;
                    }
                }
            }
            finally
            {
                Rand.PopState();
            }
            if (worker == null)
            {
                Log.Warning("[IsoRim probe] craft: no colonist able to do " + wgDef.workType.defName);
                return;
            }
            GenSpawn.Spawn(worker, pc, map, Rot4.North);
            foreach (string part in a[6].Split(';'))
            {
                string[] kv = part.Split(':');
                Thing ing = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(kv[0]), DefDatabase<ThingDef>.GetNamed(kv[0]).MadeFromStuff ? GenStuff.DefaultStuffFor(DefDatabase<ThingDef>.GetNamed(kv[0])) : null);
                ing.stackCount = I(kv[1]);
                GenPlace.TryPlaceThing(ing, pc, map, ThingPlaceMode.Near);
            }
            giver.BillStack.AddBill(recipe.MakeNewBill());
            Job job = ((WorkGiver_DoBill)wgDef.Worker).JobOnThing(worker, bench, true);
            bool ok = job != null && worker.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            Log.Message("[IsoRim probe] craft: " + worker.LabelShort + " (" + wgDef.defName + ") " + recipe.defName + " at " + bench.def.defName + " " + bc + ": " + (job == null ? "no job" : job.def.defName) + " " + ok);
        }
    }
}
