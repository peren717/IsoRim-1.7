using RimWorld;
using Verse;

namespace IsoRim
{
    // Harness-only key for female animal models (Api 38: ModelDef.femaleModel / femaleModels, femaleLegModel, femaleWingModel).
    // Spawn with a fixed gender: mech=x,z,<kind>,player,<age>,<rot>,[state],f|m (ProbeMechs).
    //   femalecheck=x,z   the pawn (or the corpse's pawn) in the cell: its gender, whether its entry has female models and
    //                     which body, leg and wing it is drawn with (female / male / none)
    public static partial class Probe
    {
        private static bool FemaleKey(string key, string[] a, Map map)
        {
            if (key != "femalecheck")
            {
                return false;
            }
            IntVec3 c = new IntVec3(int.Parse(a[0]), 0, int.Parse(a[1]));
            Pawn p = null;
            foreach (Thing t in c.GetThingList(map))
            {
                p = t as Pawn ?? (t as Corpse)?.InnerPawn;
                if (p != null)
                {
                    break;
                }
            }
            ModelEntry e;
            if (p == null || !ModelLibrary.TryGet(p.def, out e))
            {
                Log.Message("[IsoRim probe] femalecheck " + c + ": no animal with a model");
                return true;
            }
            Model body = e.Pick(p);
            Model leg = e.LegFor(p);
            Model wing = e.WingFor(p, false);
            string Which(Model m, Model[] fem, Model femOne, Model male)
            {
                if (m == null) return "none";
                if (fem != null && System.Array.IndexOf(fem, m) >= 0 || femOne != null && m == femOne) return "female";
                return m == male || e.variants != null && System.Array.IndexOf(e.variants, m) >= 0 ? "male" : "other";
            }
            Log.Message("[IsoRim probe] femalecheck " + p.def.defName + " " + c + " gender " + p.gender + " dead " + p.Dead + " entry " + e.def.defName
                + " hasFemale " + (e.female != null) + " body " + Which(body, e.female, null, null) + " leg " + Which(leg, null, e.femaleLeg, e.leg)
                + " wing " + Which(wing, null, e.femaleWing, e.wing));
            return true;
        }
    }
}
