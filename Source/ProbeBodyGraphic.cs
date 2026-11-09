using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only keys for animals by body graphic (Api 65: ModelDef.byBodyGraphic). Lines start "[IsoRim bodygraphic]".
    // Spawn with mech=x,z,<kind>,player,<age>,<rot>,[state],f|m (ProbeMechs). The pawn is found in the cell (or a corpse's pawn) the first
    // time and remembered by its ID, so later checks (after ticks, after a reload) follow it wherever it walked.
    //   bgwant=x,z,<model>,<texture>|-,<rrggbb>|-,<label>   PASS if it is drawn with a body whose file name holds <model>
    //                     (a byBodyGraphic copy: its source's), whose own textures are <texture> (- any) and in that tint (- any)
    //   bglog=x0,z0,x1,z1,<label>   every pawn in the rectangle remembered and logged (always PASS: read the log)
    //   bgwantid=<label>,<model>,<texture>|-,<rrggbb>|-   the labelled want's pawn (by ID, wherever it is) expected to look so from now on
    //   bgcheck=<label>   every remembered want checked again
    //   bgrot=x,z,<ticks> the corpse in the cell given that rot progress (CompRottable.RotProgress)
    //   bgreload=<save>,cx,cz,size   (last pass) that save loaded; every want checked again by ID, a shot, the game quits
    public static partial class Probe
    {
        private sealed class BgWant
        {
            public int id;
            public string model, texture, tint, label;
        }

        private static readonly List<BgWant> bgWants = new List<BgWant>();
        private static string bgReload;
        private static Game bgReloadFrom;
        private static int bgReloadFrames = -1;
        private static Vector3 bgShot;

        private static bool BodyGraphicKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "bgwant":
                {
                    Pawn p = BgPawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        Log.Message("[IsoRim bodygraphic] FAIL " + a[5] + ": no pawn at " + a[0] + "," + a[1]);
                        return true;
                    }
                    BgWant w = new BgWant { id = p.thingIDNumber, model = a[2], texture = a[3], tint = a[4], label = a[5] };
                    bgWants.RemoveAll(x => x.label == w.label);
                    bgWants.Add(w);
                    BgCheck(w, "");
                    return true;
                }
                case "bglog":
                {
                    // bglog=x0,z0,x1,z1,<label>: every pawn (or corpse) in the rectangle remembered and logged (no expectation)
                    for (int x = I(a[0]); x <= I(a[2]); x++)
                    {
                        for (int z = I(a[1]); z <= I(a[3]); z++)
                        {
                            Pawn p = BgPawnAt(map, x, z);
                            if (p != null)
                            {
                                BgWant w = new BgWant { id = p.thingIDNumber, model = "", texture = "-", tint = "-", label = a[4] + "@" + x + "," + z };
                                bgWants.RemoveAll(o => o.label == w.label);
                                bgWants.Add(w);
                                BgCheck(w, "");
                            }
                        }
                    }
                    return true;
                }
                case "bgwantid":
                {
                    // bgwantid=<label>,<model>,<texture>|-,<rrggbb>|-: the labelled want's pawn (by ID) expected to look so from now on
                    BgWant w = bgWants.FirstOrDefault(x => x.label == a[0]);
                    if (w == null)
                    {
                        Log.Message("[IsoRim bodygraphic] FAIL " + a[0] + ": no such want");
                        return true;
                    }
                    w.model = a[1];
                    w.texture = a[2];
                    w.tint = a[3];
                    BgCheck(w, "");
                    return true;
                }
                case "bgcheck":
                    foreach (BgWant w in bgWants)
                    {
                        BgCheck(w, a[0] + ": ");
                    }
                    return true;
                case "bgrot":
                {
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    Corpse corpse = c.GetThingList(map).OfType<Corpse>().FirstOrDefault();
                    CompRottable rot = corpse != null ? corpse.GetComp<CompRottable>() : null;
                    if (rot != null)
                    {
                        rot.RotProgress = F(a[2]);
                        corpse.InnerPawn.Drawer.renderer.SetAllGraphicsDirty();
                    }
                    Log.Message("[IsoRim bodygraphic] rot " + c + ": " + (rot != null ? rot.Stage + " " + corpse.InnerPawn.Drawer.renderer.CurRotDrawMode : "no corpse"));
                    return true;
                }
                case "bgreload":
                {
                    System.Reflection.FieldInfo cap = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("Capture17.Main"), "Active");
                    if (cap != null)
                    {
                        cap.SetValue(null, false);
                    }
                    bgReload = a[0];
                    bgReloadFrom = Current.Game;
                    bgReloadFrames = 0;
                    bgShot = new Vector3(F(a[1]), F(a[3]), F(a[2]));
                    GameDataSaveLoader.LoadGame(a[0]);
                    return true;
                }
            }
            return false;
        }

        private static Pawn BgPawnAt(Map map, int x, int z)
        {
            foreach (Thing t in new IntVec3(x, 0, z).GetThingList(map))
            {
                Pawn p = t as Pawn ?? (t as Corpse)?.InnerPawn;
                if (p != null)
                {
                    return p;
                }
            }
            return null;
        }

        private static Pawn BgById(int id)
        {
            foreach (Map m in Find.Maps)
            {
                foreach (Thing t in m.mapPawns.AllPawns.Cast<Thing>().Concat(m.listerThings.ThingsInGroup(ThingRequestGroup.Corpse)))
                {
                    Pawn p = t as Pawn ?? (t as Corpse)?.InnerPawn;
                    if (p != null && p.thingIDNumber == id)
                    {
                        return p;
                    }
                }
            }
            return null;
        }

        // A body model's file name (a byBodyGraphic texture copy: its source's), or "?".
        private static string BgFile(Model m)
        {
            if (m == null)
            {
                return "none";
            }
            Model raw = m;
            System.Collections.IDictionary rotten = HarmonyLib.AccessTools.Field(typeof(Corpses), "rotten").GetValue(null) as System.Collections.IDictionary;
            foreach (System.Collections.DictionaryEntry e in rotten)
            {
                if (e.Value == m)
                {
                    raw = (Model)e.Key;   // a rotting copy: its fresh model
                }
            }
            raw = BodyGraphicModels.SourceOf(raw);
            System.Collections.IDictionary byPath = HarmonyLib.AccessTools.Field(typeof(ModelLibrary), "byPath").GetValue(null) as System.Collections.IDictionary;
            foreach (System.Collections.DictionaryEntry e in byPath)
            {
                if (e.Value == raw)
                {
                    return Path.GetFileName((string)e.Key);
                }
            }
            // merged with the def's parts (LoadBodies): no file of its own
            return "merged#" + raw.positions.Length;
        }

        private static string BgTextures(Model m)
        {
            if (m == null || m.materials == null)
            {
                return "-";
            }
            List<string> tex = (m.material ?? new int[0]).Distinct().Select(i => m.materials[i]).Where(x => x.texture != null).Select(x => x.texture).Distinct().ToList();
            return tex.Count == 0 ? "-" : string.Join("+", tex);
        }

        private static void BgCheck(BgWant w, string prefix)
        {
            Pawn p = BgById(w.id);
            ModelEntry e;
            if (p == null || !ModelLibrary.TryGet(p.def, out e))
            {
                Log.Message("[IsoRim bodygraphic] FAIL " + prefix + w.label + ": pawn " + w.id + " gone or without a model");
                return;
            }
            Model body = Corpses.Body(p, e.Pick(p));
            bool tintTex;
            Color tint = BodyGraphicModels.Tint(e, p, out tintTex);
            Color32 t32 = tint;
            string tintHex = t32.r.ToString("x2") + t32.g.ToString("x2") + t32.b.ToString("x2");
            string file = BgFile(body);
            string tex = BgTextures(body);
            Graphic drawn = p.Drawer?.renderer?.BodyGraphic;
            bool ok = file.IndexOf(w.model, StringComparison.OrdinalIgnoreCase) >= 0 && (w.texture == "-" || tex == w.texture)
                && (w.tint == "-" || string.Equals(tintHex, w.tint, StringComparison.OrdinalIgnoreCase));
            Log.Message("[IsoRim bodygraphic] " + (ok ? "PASS " : "FAIL ") + prefix + w.label + ": " + p.def.defName + " id " + p.thingIDNumber + " "
                + p.gender + " " + p.ageTracker.CurLifeStage.defName + " dead " + p.Dead + " rot " + p.Drawer.renderer.CurRotDrawMode
                + " drawn " + (drawn != null ? drawn.path : "null") + " living " + BodyGraphicModels.LivingPath(p, p.ageTracker.CurKindLifeStage)
                + " -> body " + file + " tex " + tex + " tint " + tintHex + (tintTex ? " (textured)" : "") + " leg " + BgFile(e.LegFor(p))
                + " shown " + Corpses.ShownAsModel(p) + "; want " + w.model + " " + w.texture + " " + w.tint);
        }

        // After bgreload: frames to settle, the checks, a shot, quit.
        private static void BgReloadTick()
        {
            if (bgReload == null || Current.Game == bgReloadFrom || LongEventHandler.ShouldWaitForEvent || Find.CurrentMap == null
                || Find.CurrentMap.listerThings.AllThings.Count == 0)
            {
                return;
            }
            bgReloadFrames++;
            if (bgReloadFrames == 2)
            {
                Find.WindowStack.Windows.ToList().ForEach(w => w.Close(false));
                View3D.Active = true;
                Find.CameraDriver.config.sizeRange.min = 1f;
                Find.CameraDriver.SetRootPosAndSize(new Vector3(bgShot.x, 0f, bgShot.z), bgShot.y);
            }
            if (bgReloadFrames == 90)
            {
                foreach (BgWant w in bgWants)
                {
                    BgCheck(w, "after reload: ");
                }
                string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", "bg_reload.png");
                Find.UIRoot.screenshotMode.Active = true;
                ScreenCapture.CaptureScreenshot(path);
            }
            if (bgReloadFrames == 150)
            {
                Log.Message("[IsoRim bodygraphic] reload checks done, quitting");
                Root.Shutdown();
            }
        }
    }
}
