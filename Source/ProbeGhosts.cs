using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Probe key ghostinv=1: every thing the player can place from the build menu (designationCategory set, a building or
    // a minified-able thing), how the 3D placement preview shows it (Ghosts.PreviewKind) and why, written to
    // <save data>\IsoRimGhostInventory.tsv.
    public static partial class Probe
    {
        // ghostgame=<Def>,x,z,rot: the game's own placement ghost (GhostDrawer.DrawGhostThing, green, default stuff) drawn there every
        // frame, as under the pointer while placing: its flat sprite and IsoRim's 3D preview (none: clear). ghostfix=0: the preview
        // as before doors and sprite-made buildings had 3D ghosts.
        private static readonly List<GhostSpec> gameGhosts = new List<GhostSpec>();

        private static void GameGhostTick()
        {
            for (int i = 0; i < gameGhosts.Count; i++)
            {
                ThingDef d = gameGhosts[i].def;
                GhostDrawer.DrawGhostThing(gameGhosts[i].cell, gameGhosts[i].rot, d, null, Designator_Place.CanPlaceColor, AltitudeLayer.Blueprint, null, true, d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null);
            }
        }

        private static bool GhostKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "ghostfix":
                    Ghosts.FixOff = a[0] == "0";
                    return true;
                case "ghostgame":
                    if (a[0] == "none")
                    {
                        gameGhosts.Clear();
                        return true;
                    }
                    if (DefDatabase<ThingDef>.GetNamedSilentFail(a[0]) == null)
                    {
                        Log.Warning("[IsoRim probe] ghostgame: no def " + a[0] + " (its mod not loaded), skipped");
                        return true;
                    }
                    gameGhosts.Add(new GhostSpec { def = DefDatabase<ThingDef>.GetNamed(a[0]), cell = new IntVec3(I(a[1]), 0, I(a[2])), rot = new Rot4(I(a[3])) });
                    return true;
                case "ghostinv":
                    break;
                default:
                    return false;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("defName\tpack\tpreview\tform\tdrawerType\tgraphicClass\tlinkType\tsize\taltitude\tthingClass\tmodelListed\thasModel");
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading.Where(x => x.designationCategory != null && !x.IsBlueprint && !x.IsFrame).OrderBy(x => x.defName))
            {
                string kind = Ghosts.PreviewKind(d);
                ThingForm f = Forms.For(d);
                ModelEntry e;
                bool listed = ModelLibrary.TryGet(d, out e);
                sb.Append(d.defName).Append('\t').Append(d.modContentPack != null ? d.modContentPack.PackageId : "-").Append('\t').Append(kind).Append('\t')
                    .Append(f.form).Append('\t').Append(d.drawerType).Append('\t').Append(d.graphicData != null && d.graphicData.graphicClass != null ? d.graphicData.graphicClass.Name : "-").Append('\t')
                    .Append(d.graphicData != null ? d.graphicData.linkType.ToString() : "-").Append('\t').Append(d.size.x + "x" + d.size.z).Append('\t').Append(d.altitudeLayer).Append('\t')
                    .Append(d.thingClass != null ? d.thingClass.Name : "-").Append('\t').Append(listed ? e.def.defName : "-").Append('\t').Append(listed && e.HasModel ? "1" : "0").AppendLine();
                string k = kind.Split(':')[0];
                counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
            }
            File.WriteAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, "IsoRimGhostInventory.tsv"), sb.ToString());
            Log.Message("[IsoRim probe] ghostinv: " + string.Join(", ", counts.OrderByDescending(x => x.Value).Select(x => x.Key + " " + x.Value)));
            return true;
        }
    }
}
