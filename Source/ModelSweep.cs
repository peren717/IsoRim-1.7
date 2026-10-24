using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Harness only (probe key modelsweep=1): for every vanilla and DLC building, plant and item,
    // which 3D form IsoRim gives it, which model entry it has and how that entry was found, and
    // what draws it. Writes IsoRimModelSweep.tsv (one row per def) and IsoRimModelSweep.txt (counts
    // and the flagged rows) to the save data folder. Flags:
    //   NOT_DRAWN      has a hand-made model, but nothing draws it (the sprite shows instead)
    //   MISSING_FILE   has an entry naming a model file that did not load
    //   NAME_CHANGED   the <match> rule picks a different entry than plain substring matching did
    //                  (oldMatch / newMatch columns), e.g. AncientCardboardBox once took AncientCar
    //   REALTIME       drawn by the game every frame (drawerType RealtimeOnly) and has a model
    //   ITEM_ART       an item with a model entry that is made from its sprite instead (setting
    //                  "Items and weapons use the game's art"); by design, listed for completeness
    //   GEAR_ART       apparel or a weapon with a model entry of IsoRim's own (or a category's) that lies on the ground as
    //                  GroundGear draws it instead (the worn clothes laid down, or its sprite made solid); by design
    // Also lists every apparel def (all mods) with how it lies on the ground with the setting off (GroundGear.LookOf): its
    // worn clothes laid down, or its sprite (plain: only colours the body; none: no 3D part on a figure). And every item
    // (all mods) that can be equipped but is no gear to GroundGear (GroundGear.IsGear: wood, tusks, beer ...).
    // Never runs in normal play (Probe.On needs dev mode, -isorim3d and a probe file).
    public static class ModelSweep
    {
        public static void Run()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            List<ModelDef> modelDefs = DefDatabase<ModelDef>.AllDefsListForReading;
            StringBuilder tsv = new StringBuilder();
            tsv.Append("defName\tpack\tcategory\tthingClass\tdrawerType\tform\theight\tentry\thow\tmodelFile\thasModel\tdrawnBy\toldMatch\tnewMatch\tflags\n");
            Dictionary<string, int> counts = new Dictionary<string, int>();
            Dictionary<string, int> forms = new Dictionary<string, int>();
            List<string> flagged = new List<string>();
            int rows = 0;

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs.OrderBy(d => d.defName))
            {
                if (def.modContentPack == null || !def.modContentPack.IsOfficialMod || def.IsBlueprint || def.IsFrame
                    || (def.category != ThingCategory.Building && def.category != ThingCategory.Plant && def.category != ThingCategory.Item))
                {
                    continue;
                }
                rows++;
                ThingForm form = Forms.For(def);
                ModelEntry entry;
                bool listed = ModelLibrary.TryGet(def, out entry);
                string how = ModelLibrary.HowBound(def);
                bool hasModel = listed && entry.HasModel;
                bool flat = listed && entry.def.flat;
                string file = listed ? (entry.def.model ?? (entry.def.models != null ? string.Join(",", entry.def.models)
                    : entry.def.stackModels != null ? string.Join(",", entry.def.stackModels) : "")) : "";

                string drawnBy = "sprite";
                if (form.form == Form3D.Model)
                {
                    drawnBy = def.drawerType == DrawerType.RealtimeOnly ? "model (realtime body layer)" : "model (map mesh)";
                }
                else if (form.form != Form3D.Flat)
                {
                    drawnBy = form.form.ToString().ToLowerInvariant();
                }
                bool door = typeof(Building_Door).IsAssignableFrom(def.thingClass) && def.size.x == 1 && def.size.z == 1 && st.doors3D;
                if (door)
                {
                    drawnBy = typeof(Building_SupportedDoor).IsAssignableFrom(def.thingClass) && listed && entry.HasModel && st.models3D
                        ? "Doors.DrawSupported" : "Doors.Draw";
                }
                else if (typeof(Building_MultiTileDoor).IsAssignableFrom(def.thingClass) && listed && entry.doorLeaf != null && entry.HasModel && st.doors3D)
                {
                    drawnBy = "Doors.DrawWide";
                    door = true;   // drawn every frame by the door code, not baked
                }
                else if (Ghosts.WideSupported(def) && listed && entry.HasModel && st.doors3D && st.models3D)
                {
                    drawnBy = "Doors.DrawSupported";   // (Api 29) a multi-cell supported door
                    door = true;
                }

                // Which name entry the <match> lists pick, with the old substring rule and with the
                // current word rule (only for things with no entry of their own or of a category).
                string oldMatch = "", newMatch = "";
                bool nameScope = (how == null || how.StartsWith("match:")) && def.category != ThingCategory.Plant;
                if (nameScope)
                {
                    oldMatch = FirstMatch(modelDefs, def.defName, true);
                    newMatch = FirstMatch(modelDefs, def.defName, false);
                }

                List<string> flags = new List<string>();
                bool item = def.category == ThingCategory.Item;
                bool plantOff = def.category == ThingCategory.Plant
                    && (def.plant != null && def.plant.IsTree ? !st.trees3D : !st.smallPlants3D);
                if (hasModel && !flat && form.form != Form3D.Model && !door && st.models3D && !plantOff)
                {
                    if (item && st.itemsFromArt && !Forms.IsChunk(def) && !Forms.OverArt(entry))
                    {
                        flags.Add("ITEM_ART");
                    }
                    else if (item && GroundGear.Replaces(def, entry))
                    {
                        flags.Add("GEAR_ART");
                    }
                    else
                    {
                        flags.Add("NOT_DRAWN");
                    }
                }
                if (listed && !hasModel && (!entry.def.model.NullOrEmpty() || entry.def.models != null || entry.def.stackModels != null))
                {
                    flags.Add("MISSING_FILE");
                }
                if (oldMatch != newMatch)
                {
                    flags.Add("NAME_CHANGED");
                }
                if (hasModel && def.drawerType == DrawerType.RealtimeOnly)
                {
                    flags.Add("REALTIME");
                }

                string formName = form.form.ToString();
                forms[formName] = (forms.TryGetValue(formName, out int fc) ? fc : 0) + 1;
                foreach (string f in flags)
                {
                    counts[f] = (counts.TryGetValue(f, out int c) ? c : 0) + 1;
                }
                string line = def.defName + "\t" + def.modContentPack.PackageId + "\t" + def.category + "\t" + (def.thingClass != null ? def.thingClass.Name : "")
                    + "\t" + def.drawerType + "\t" + formName + "\t" + form.height.ToString("0.00") + "\t" + (listed ? entry.def.defName : "")
                    + "\t" + (how ?? "") + "\t" + file + "\t" + (hasModel ? "1" : "0") + "\t" + drawnBy + "\t" + oldMatch + "\t" + newMatch
                    + "\t" + string.Join(",", flags) + "\n";
                tsv.Append(line);
                if (flags.Count > 0 && !(flags.Count == 1 && (flags[0] == "ITEM_ART" || flags[0] == "GEAR_ART")))
                {
                    flagged.Add(line);
                }
            }

            StringBuilder txt = new StringBuilder();
            txt.Append("IsoRim model sweep, vanilla + DLC buildings/plants/items: ").Append(rows).Append(" defs\n");
            txt.Append("settings: models3D=").Append(st.models3D).Append(" itemsFromArt=").Append(st.itemsFromArt)
                .Append(" trees3D=").Append(st.trees3D).Append(" smallPlants3D=").Append(st.smallPlants3D).Append(" doors3D=").Append(st.doors3D).Append('\n');
            txt.Append("forms: ").Append(string.Join(", ", forms.OrderBy(p => p.Key).Select(p => p.Key + " " + p.Value))).Append('\n');
            txt.Append("flags: ").Append(string.Join(", ", counts.OrderBy(p => p.Key).Select(p => p.Key + " " + p.Value))).Append('\n');
            txt.Append("\nflagged rows (ITEM_ART- and GEAR_ART-only rows left out):\n");
            foreach (string l in flagged)
            {
                txt.Append(l);
            }
            GroundLooks(txt);
            NotGear(txt);
            string dir = GenFilePaths.SaveDataFolderPath;
            File.WriteAllText(Path.Combine(dir, "IsoRimModelSweep.tsv"), tsv.ToString());
            File.WriteAllText(Path.Combine(dir, "IsoRimModelSweep.txt"), txt.ToString());
            Log.Message("[IsoRim probe] model sweep: " + rows + " defs; forms " + string.Join(", ", forms.Select(p => p.Key + " " + p.Value))
                + "; flags " + string.Join(", ", counts.Select(p => p.Key + " " + p.Value)));
        }

        // Every apparel def (all mods) and how it lies on the ground with the game's art off (GroundGear): counts per look,
        // the rows that keep their sprite, and the laid triangles against the worn parts' for the rest.
        private static void GroundLooks(StringBuilder txt)
        {
            if (!IsoRimMod.Settings.pawns3D)
            {
                txt.Append("\nground apparel: 3D people off, every apparel lies as its sprite\n");
                return;
            }
            List<string> sprite = new List<string>(), worn = new List<string>();
            Dictionary<string, int> looks = new Dictionary<string, int>();
            long laidSum = 0, wornSum = 0;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs.Where(d => d.IsApparel && d.category == ThingCategory.Item).OrderBy(d => d.defName))
            {
                string look;
                int laid, wornTris;
                try
                {
                    look = GroundGear.LookOf(def, out laid, out wornTris);
                }
                catch (Exception e)
                {
                    look = "ERROR " + e.GetType().Name;
                    laid = wornTris = 0;
                }
                looks[look] = (looks.TryGetValue(look, out int c) ? c : 0) + 1;
                string row = def.defName + "\t" + def.label + "\t" + (def.modContentPack != null ? def.modContentPack.PackageId : "") + "\t" + look
                    + (GroundGear.ChildOnly(def) ? "\tchild" : "\t");
                if (look == "worn")
                {
                    worn.Add(row + "\t" + laid + "/" + wornTris + "\n");
                    laidSum += laid;
                    wornSum += wornTris;
                }
                else
                {
                    sprite.Add(row + "\n");
                }
            }
            txt.Append("\nground apparel (game's art off): ").Append(string.Join(", ", looks.OrderBy(p => p.Key).Select(p => p.Key + " " + p.Value)))
                .Append("; laid triangles (lit + outline) ").Append(laidSum).Append(" against ").Append(wornSum).Append(" worn\n");
            txt.Append("\nkept as sprite (plain: only colours the body; none: no 3D part):\n");
            foreach (string r in sprite)
            {
                txt.Append(r);
            }
            txt.Append("\nlaid as worn clothes (laid tris / worn tris):\n");
            foreach (string r in worn)
            {
                txt.Append(r);
            }
        }

        // Every item def (all mods) that can be equipped (equipmentType Primary) but is no gear to GroundGear, so it keeps its
        // own look on the ground and in hands: its pack, categories and model entry.
        private static void NotGear(StringBuilder txt)
        {
            List<string> rows = new List<string>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs.Where(d => d.category == ThingCategory.Item && !d.IsApparel
                && d.equipmentType == EquipmentType.Primary && !GroundGear.IsGear(d)).OrderBy(d => d.defName))
            {
                ModelEntry entry;
                bool listed = ModelLibrary.TryGet(def, out entry);
                rows.Add(def.defName + "\t" + (def.modContentPack != null ? def.modContentPack.PackageId : "") + "\t"
                    + (def.thingCategories != null ? string.Join(",", def.thingCategories.Select(c => c.defName)) : "") + "\t"
                    + (listed ? entry.def.defName + (entry.HasModel ? "" : " (no model)") + " " + ModelLibrary.HowBound(def) : "-") + "\n");
            }
            txt.Append("\nequippable but no gear (GroundGear.IsGear false; own look kept): ").Append(rows.Count).Append('\n');
            foreach (string r in rows)
            {
                txt.Append(r);
            }
        }

        // The first name entry whose <match> list names this defName (same order as ModelLibrary).
        private static string FirstMatch(List<ModelDef> all, string defName, bool substring)
        {
            for (int i = 0; i < all.Count; i++)
            {
                ModelDef md = all[i];
                if (md.match == null)
                {
                    continue;
                }
                for (int k = 0; k < md.match.Count; k++)
                {
                    string part = md.match[k];
                    bool hit = substring
                        ? !part.NullOrEmpty() && defName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0
                        : ModelLibrary.NameMatches(defName, part);
                    if (hit)
                    {
                        return md.defName + ":" + part;
                    }
                }
            }
            return "";
        }
    }
}
