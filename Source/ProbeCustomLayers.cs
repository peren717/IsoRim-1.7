using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Harness-only (ap9clsweep, ProbeApi9.cs): apparel on layers IsoRim does not know (CustomLayers). Writes every apparel
    // def that has a layer the game does not define (its mod, layers with draw order, body part groups, class, how
    // CustomLayers takes it) to IsoRimCustomLayers.tsv, and logs the dressed look of each ap9fix pawn.
    public static partial class Probe
    {
        private static void CustomLayerSweep(string label)
        {
            StringBuilder sb = new StringBuilder("defName\tmod\tlayers\tgroups\tclass\ttaken\n");
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                ApparelProperties p = d.apparel;
                if (p == null || p.layers.NullOrEmpty())
                {
                    continue;
                }
                bool custom = p.layers.Any(l => l != null && !(l.defName == "OnSkin" || l.defName == "Middle" || l.defName == "Shell" || l.defName == "Belt"
                    || l.defName == "Overhead" || l.defName == "EyeCover"));
                if (!custom)
                {
                    continue;
                }
                sb.Append(d.defName).Append('\t').Append(d.modContentPack != null ? d.modContentPack.PackageId : "-").Append('\t')
                    .Append(string.Join(",", p.layers.Select(l => l.defName + ":" + l.drawOrder))).Append('\t')
                    .Append(p.bodyPartGroups != null ? string.Join(",", p.bodyPartGroups.Select(g => g.defName)) : "").Append('\t')
                    .Append(d.thingClass != null ? d.thingClass.FullName : "").Append('\t')
                    .Append(CustomLayers.Describe(d)).Append('\n');
            }
            File.WriteAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, "IsoRimCustomLayers.tsv"), sb.ToString());
            string L = "[IsoRim probe] ap9clsweep " + label + ": ";
            Log.Message(L + "Api.Version " + Api.Version + ", bindings " + ApparelModels.Count + ", pawns " + ap9Spawned.Count);
            foreach (Pawn pawn in ap9Spawned.OrderBy(x => ap9Roles[x.thingIDNumber]))
            {
                People.Look look = People.LastLook(pawn);
                string worn = pawn.apparel != null ? string.Join("+", pawn.apparel.WornApparel.Select(x => x.def.defName)) : "";
                Log.Message(L + ap9Roles[pawn.thingIDNumber] + " | worn " + worn + " | " + (look == null ? "no look" : LookParts(look)));
            }
        }

        private static string LookParts(People.Look look)
        {
            List<string> s = new List<string>();
            void Add(string n, Model m)
            {
                if (m != null)
                {
                    s.Add(n + " " + m.id);
                }
            }
            Add("hat", look.hat);
            Add("mouth", look.mouthWear);
            Add("eyes", look.eyeWear);
            Add("neck", look.neckWear);
            Add("coat", look.coat);
            Add("underCoat", look.underCoat);
            Add("mid", look.mid);
            Add("shirtWear", look.shirtWear);
            Add("overCoat", look.overCoat);
            Add("legArmour", look.legArmour);
            Add("shield", look.shield);
            s.Add("gear " + look.gear.Count);
            s.Add("upper " + look.upperColor.ToString("0.00") + (look.upperWorn ? "" : " bare"));
            return string.Join(", ", s);
        }
    }
}
