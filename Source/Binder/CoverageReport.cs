using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Dev-mode model coverage report (debug action "IsoRim / Model coverage..."): per loaded mod, the buildings and items that
    // have no 3D model, with what IsoRim draws instead (its automatic form with the current settings: the sprite raised up,
    // a block, the flat sprite, IsoRim's own door ...). Natural rock is left out.. Shown in a window and written as .tsv and .csv to <save data>/IsoRimBinder/Coverage/.
    public static class CoverageReport
    {
        public class Row
        {
            public string mod;
            public string packageId;
            public string defName;
            public string label;
            public string kind;       // Building, Item, Apparel, Weapon
            public string size;
            public string fallback;   // what is drawn instead: extruded, sprite, block, worn, ground
            public string drawer;     // the game's drawerType
        }

        public class ModSummary
        {
            public string mod;
            public int buildings, buildingsMissing, items, itemsMissing;
            public List<Row> rows = new List<Row>();
        }

        private static readonly MethodInfo Compute = AccessTools.Method(typeof(Forms), "Compute");

        [DebugAction("IsoRim", "Model coverage...", allowedGameStates = AllowedGameStates.Playing)]
        public static void OpenFromDebugMenu()
        {
            Find.WindowStack.Add(new CoverageWindow(Build()));
        }

        public static List<ModSummary> Build()
        {
            Dictionary<string, ModSummary> byMod = new Dictionary<string, ModSummary>();
            List<ModSummary> order = new List<ModSummary>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.category != ThingCategory.Building && def.category != ThingCategory.Item || def.IsBlueprint || def.IsFrame
                    || def.drawerType == DrawerType.None || def.IsCorpse || typeof(MinifiedThing).IsAssignableFrom(def.thingClass)
                    || typeof(UnfinishedThing).IsAssignableFrom(def.thingClass) || def.building != null && def.building.isNaturalRock)
                {
                    continue;
                }
                string modName = def.modContentPack != null ? def.modContentPack.Name : "(generated)";
                ModSummary s;
                if (!byMod.TryGetValue(modName, out s))
                {
                    s = new ModSummary { mod = modName };
                    byMod[modName] = s;
                    order.Add(s);
                }
                ModelEntry entry;
                // Covered: a model, or an entry that sets the look on purpose (a turret gun on a block, a door leaf, a wall top, kept
                // flat, an automatic shape of a given height).
                bool hasModel = ModelLibrary.TryGet(def, out entry) && (entry.HasModel || entry.top != null || entry.doorLeaf != null
                    || entry.def.wallTop != null || entry.def.flat || entry.def.height > 0f);
                bool building = def.category == ThingCategory.Building;
                if (building)
                {
                    s.buildings++;
                }
                else
                {
                    s.items++;
                }
                if (hasModel)
                {
                    continue;
                }
                if (building)
                {
                    s.buildingsMissing++;
                }
                else
                {
                    s.itemsMissing++;
                }
                s.rows.Add(new Row
                {
                    mod = modName,
                    packageId = def.modContentPack != null ? def.modContentPack.PackageIdPlayerFacing : "",
                    defName = def.defName,
                    label = def.label ?? "",
                    kind = building ? "Building" : def.IsApparel ? "Apparel" : def.IsWeapon ? "Weapon" : "Item",
                    size = def.size.x + "x" + def.size.z,
                    fallback = Fallback(def),
                    drawer = def.drawerType.ToString(),
                });
            }
            foreach (ModSummary s in order)
            {
                s.rows.SortBy(r => r.kind, r => r.defName);
            }
            return order;
        }

        // What IsoRim draws for a thing without a model, with the current settings (also while the 3D view is off).
        private static string Fallback(ThingDef def)
        {
            if (typeof(Building_Door).IsAssignableFrom(def.thingClass) && IsoRimMod.Settings.doors3D)
            {
                return "door";   // IsoRim's own 3D doors (Doors.cs)
            }
            ThingForm f = null;
            try
            {
                f = (ThingForm)Compute.Invoke(null, new object[] { def });
            }
            catch (Exception)
            {
            }
            if (f == null || f.form == Form3D.Flat)
            {
                return "sprite";
            }
            return f.form.ToString().ToLowerInvariant();
        }

        // Writes coverage_<stamp>.tsv and .csv; returns the .tsv path.
        public static string Export(List<ModSummary> mods)
        {
            string dir = Path.Combine(ModelBinder.Folder, "Coverage");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string[] head = { "mod", "packageId", "defName", "label", "kind", "size", "fallback", "drawerType" };
            StringBuilder tsv = new StringBuilder(string.Join("\t", head) + "\n");
            StringBuilder csv = new StringBuilder(string.Join(",", head) + "\n");
            foreach (ModSummary s in mods)
            {
                foreach (Row r in s.rows)
                {
                    string[] cells = { r.mod, r.packageId, r.defName, r.label, r.kind, r.size, r.fallback, r.drawer };
                    tsv.Append(string.Join("\t", Array.ConvertAll(cells, c => (c ?? "").Replace('\t', ' ').Replace('\n', ' ')))).Append('\n');
                    csv.Append(string.Join(",", Array.ConvertAll(cells, Csv))).Append('\n');
                }
            }
            string tsvPath = Path.Combine(dir, "coverage_" + stamp + ".tsv");
            File.WriteAllText(tsvPath, tsv.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "coverage_" + stamp + ".csv"), csv.ToString(), new UTF8Encoding(true));   // BOM: Excel reads it as UTF-8
            return tsvPath;
        }

        private static string Csv(string c)
        {
            c = c ?? "";
            return c.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c;
        }
    }

    public class CoverageWindow : EditWindow
    {
        private readonly List<CoverageReport.ModSummary> mods;
        private readonly HashSet<string> open = new HashSet<string>();
        private Vector2 scroll;
        private float viewHeight = 400f;
        private string saved;

        public CoverageWindow(List<CoverageReport.ModSummary> mods)
        {
            this.mods = mods;
            optionalTitle = "R3D_Coverage_Title".Translate();
            closeOnClickedOutside = false;
            closeOnAccept = false;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(640f, 600f); }
        }

        public List<CoverageReport.ModSummary> Mods
        {
            get { return mods; }
        }

        public string Save()
        {
            saved = CoverageReport.Export(mods);
            Messages.Message("R3D_Binder_Exported".Translate(saved), MessageTypeDefOf.TaskCompletion, false);
            return saved;
        }

        public void Expand(string mod)
        {
            open.Add(mod);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            if (Widgets.ButtonText(new Rect(inRect.x, inRect.y, 120f, 26f), "R3D_Binder_Export".Translate()))
            {
                Save();
            }
            if (saved != null)
            {
                GUI.color = Color.grey;
                Widgets.Label(new Rect(inRect.x + 128f, inRect.y + 2f, inRect.width - 128f, 24f), saved);
                GUI.color = Color.white;
            }
            Rect outRect = new Rect(inRect.x, inRect.y + 32f, inRect.width, inRect.height - 32f);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, viewHeight);
            Widgets.BeginScrollView(outRect, ref scroll, view);
            float y = 0f;
            float w = view.width;
            foreach (CoverageReport.ModSummary s in mods)
            {
                if (s.rows.Count == 0)
                {
                    continue;
                }
                Rect head = new Rect(0f, y, w, 24f);
                Widgets.DrawHighlightIfMouseover(head);
                bool isOpen = open.Contains(s.mod);
                Widgets.Label(head, (isOpen ? "- " : "+ ") + "R3D_Coverage_Mod".Translate(s.mod, s.buildingsMissing, s.buildings, s.itemsMissing, s.items));
                if (Widgets.ButtonInvisible(head))
                {
                    if (isOpen)
                    {
                        open.Remove(s.mod);
                    }
                    else
                    {
                        open.Add(s.mod);
                    }
                }
                y += 24f;
                if (!isOpen)
                {
                    continue;
                }
                foreach (CoverageReport.Row r in s.rows)
                {
                    if (y + 22f >= scroll.y && y <= scroll.y + outRect.height)
                    {
                        GUI.color = new Color(0.85f, 0.85f, 0.85f);
                        Widgets.Label(new Rect(16f, y, w * 0.42f, 22f), r.defName);
                        Widgets.Label(new Rect(16f + w * 0.42f, y, w * 0.18f, 22f), ("R3D_Coverage_" + r.kind).Translate());
                        Widgets.Label(new Rect(16f + w * 0.60f, y, w * 0.10f, 22f), r.size);
                        Widgets.Label(new Rect(16f + w * 0.70f, y, w * 0.28f, 22f), ("R3D_Coverage_" + r.fallback).Translate());
                        GUI.color = Color.white;
                        TooltipHandler.TipRegion(new Rect(16f, y, w - 16f, 22f), r.label);
                    }
                    y += 22f;
                }
            }
            viewHeight = y + 10f;
            Widgets.EndScrollView();
        }
    }
}
