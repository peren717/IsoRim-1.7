using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only checks of hair tucked under headgear (HairTuck.cs) and of the option hairUnderHats.
    // Keys:
    //   tucktrace=1          every tuck worked out is logged: hair, covering, triangles before/after, ms
    //   tuckoff=0|1          HairTuck.Off (hair drawn whole, as before the tuck); every figure's meshes made again
    //   tuckreport=label     per colonist: hair shown, head mesh triangles (solid + outline), and the tuck totals so far
    //   tucksettings=label   the settings written to their file and read back: hairUnderHats in the file and as read
    public static partial class Probe
    {
        private static bool HairTuckKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "tucktrace":
                    HairTuck.OnBuilt = a[0] == "1" ? (System.Action<Model, Model, Model, double>)TuckTrace : null;
                    return true;
                case "tuckoff":
                    HairTuck.Off = a[0] == "1";
                    ForgetFigures();
                    return true;
                case "tuckreport":
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps.Humanlike).OrderBy(x => x.Position.z).ThenBy(x => x.Position.x))
                    {
                        People.Look look = People.LastLook(p);
                        if (look == null)
                        {
                            continue;
                        }
                        int solid = 0, outline = 0;
                        List<DynamicModels.Part> parts;
                        if (look.meshes.TryGetValue(2, out parts))
                        {
                            foreach (DynamicModels.Part part in parts)
                            {
                                int t = part.mesh != null ? part.mesh.vertexCount / 3 : 0;
                                if (part.outline) outline += t; else solid += t;
                            }
                        }
                        Log.Message("[IsoRim probe] tuckreport " + a[0] + " " + p.Position.x + "," + p.Position.z + " hair " + (p.story.hairDef != null ? p.story.hairDef.defName : "-")
                            + " -> " + Name(look.hair) + (look.hairTop != null ? "+" + Name(look.hairTop) : "") + " shown " + look.hairShown
                            + ", hat " + Name(look.hat) + ", mouth " + Name(look.mouthWear) + ", eyes " + Name(look.eyeWear) + ", beard " + Name(look.beard)
                            + "; head tris solid " + solid + " outline " + outline);
                    }
                    Log.Message("[IsoRim probe] tuckreport " + a[0] + " totals: " + HairTuck.Built + " tucks, " + HairTuck.BuiltMs.ToString("0.00") + " ms"
                        + ", option hairUnderHats " + IsoRimMod.Settings.hairUnderHats + ", off " + HairTuck.Off);
                    return true;
                case "tucksettings":
                {
                    Mod mod = LoadedModManager.GetMod<IsoRimMod>();
                    mod.WriteSettings();
                    string file = Path.Combine(GenFilePaths.ConfigFolderPath, GenText.SanitizeFilename("Mod_" + mod.Content.FolderName + "_" + mod.GetType().Name + ".xml"));
                    string text = File.Exists(file) ? File.ReadAllText(file) : "";
                    int i = text.IndexOf("<hairUnderHats>");
                    string inFile = i >= 0 ? text.Substring(i, text.IndexOf("</hairUnderHats>", i) - i + 16) : "(absent: default)";
                    IsoRimSettings read = LoadedModManager.ReadModSettings<IsoRimSettings>(mod.Content.FolderName, mod.GetType().Name);
                    bool ok = read.hairUnderHats == IsoRimMod.Settings.hairUnderHats;
                    Log.Message("[IsoRim probe] tucksettings " + a[0] + ": live " + IsoRimMod.Settings.hairUnderHats + ", file " + Path.GetFileName(file) + " " + inFile
                        + ", read back " + read.hairUnderHats + " -> " + (ok ? "CHECK ok" : "CHECK FAILED"));
                    return true;
                }
            }
            return false;
        }

        private static void TuckTrace(Model hair, Model cover, Model result, double ms)
        {
            Log.Message("[IsoRim probe] tuck " + Name(hair) + " under " + Name(cover) + ": tris " + hair.positions.Length / 3 + " -> "
                + (result == hair ? "unchanged" : (result.positions.Length / 3).ToString()) + ", " + ms.ToString("0.00") + " ms");
        }

        // Every figure's meshes made again on its next draw.
        private static void ForgetFigures()
        {
            Dictionary<int, People.Look> looks = (Dictionary<int, People.Look>)AccessTools.Field(typeof(People), "looks").GetValue(null);
            foreach (People.Look l in looks.Values)
            {
                l.Forget();
            }
        }

        private static string Name(Model m)
        {
            if (m == null)
            {
                return "-";
            }
            Dictionary<string, Model> models = (Dictionary<string, Model>)AccessTools.Field(typeof(People), "models").GetValue(null);
            foreach (KeyValuePair<string, Model> kv in models)
            {
                if (kv.Value == m)
                {
                    return kv.Key.Replace("person_", "");
                }
            }
            return "#" + m.id + "(" + m.positions.Length / 3 + " tris)";
        }
    }
}
