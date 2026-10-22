using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using UnityEngine.InputSystem;
using Verse;

namespace IsoRim
{
    // Harness keys for the dev-mode model binder and the coverage report (Binder/). The binder is driven through its own entry
    // and handlers: binderopen runs the debug action itself with the thing selected, the other keys call what the panel's
    // fields and buttons call. bindertype types into the panel with real (queued OS) input.
    //   binderopen=x,z[,Def]          select the building/item on that cell (of that def) and run "IsoRim / Model binder..."
    //   binderrefuse=x,z[,Def]        log why the binder refuses that thing (empty: it takes it)
    //   binderfile=<path>             the model file (as typed into the file field)
    //   binderpick=<text>             the Pick list: the first .obj whose name holds text
    //   binderset=<field>,<value>     a panel field: offset.x, builtFor.y, rotation, rotationByFacing.1, scale, overArt ...
    //   bindercopy=<ThingDef>         "Copy from existing" with that thing's model
    //   binderturn=1 / binderrevert=1 / binderclose=1
    //   binderexport=<pack>           export into <save data>/IsoRimBinder/<pack>/ (logs the files and the Defs/Patches text)
    //   bindercheck=<label>,<ThingDef>[,x,z]   logs how the def is drawn: bound how, form, the model's placement (corners of
    //                                 its box on the map for the thing on x,z) - lines to compare between runs
    //   bindershot=<tag>              ~40 frames later a shot with the interface on (the panel): renders\<tag>\binder_<tag>.png
    //   bindertype=<field>,<text>     clicks into the panel's field, types text (real key presses), holds the camera's move
    //                                 key W for 40 frames while it has focus, then clicks the map and holds W again: logs the
    //                                 field text and how far the camera moved each time (CHECK ok: 0 while typing, > 0 after)
    //   coverage=<label>[,Def;Def...] build the coverage report, export it (logs the file), log the listed defs' rows
    public static partial class Probe
    {
        private static BinderWindow BinderOpen
        {
            get { return Find.WindowStack.WindowOfType<BinderWindow>(); }
        }

        private static Thing BinderThingAt(Map map, string[] a)
        {
            IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
            return c.GetThingList(map).FirstOrDefault(t => (a.Length < 3 || t.def.defName == a[2])
                && (t.def.category == ThingCategory.Building || t.def.category == ThingCategory.Item));
        }

        // The game's own debug actions tree (as Dialog_Debug builds it): IsoRim's action of that label, null if it is not there.
        private static DebugActionNode BinderMenuNode(string label)
        {
            DebugActionNode root = new DebugActionNode("root");
            DebugActionNode actions = new DebugTabMenu_Actions(DebugTabMenuDefOf.Actions, null, root).InitActions(root);
            foreach (DebugActionNode n in actions.children)
            {
                if (n.label == label && n.category == "IsoRim" && n.VisibleNow)
                {
                    return n;
                }
            }
            return null;
        }

        private static bool BinderKey(string key, string[] a, Map map)
        {
            if (key.StartsWith("binder"))
            {
                // The harness takes its shots a fixed number of frames after a pass: the view's rebuild after a model change is
                // not left to its 0.35 s debounce (frames come fast in the hidden window) but runs on the next frames.
                HookBinderFrames();
                binderFlush = 30;
            }
            switch (key)
            {
                case "binderopen":
                {
                    Thing t = BinderThingAt(map, a);
                    Find.Selector.ClearSelection();
                    if (t != null)
                    {
                        Find.Selector.Select(t, false, false);
                    }
                    DebugActionNode node = BinderMenuNode("Model binder...");
                    if (node != null)
                    {
                        node.Enter(null);   // the debug action, found and run as the game's debug actions menu does
                    }
                    else
                    {
                        Log.Error("[IsoRim probe] binderopen: no debug action IsoRim / Model binder... in the game's menu");
                    }
                    BinderWindow w = BinderOpen;
                    Log.Message("[IsoRim probe] binderopen " + (t != null ? t.def.defName + " at " + t.Position : "nothing") + ": panel "
                        + (w != null && w.Thing == t ? "open" : "NOT open") + ", status " + (w != null ? ModelLibrary.HowBound(t.def) ?? "no entry" : "-"));
                    Find.Selector.ClearSelection();
                    if (DebugTools.curTool != null)
                    {
                        DebugTools.curTool = null;
                    }
                    return true;
                }
                case "binderrefuse":
                {
                    Thing t = BinderThingAt(map, a);
                    string why = ModelBinder.Refusal(t);
                    Log.Message("[IsoRim probe] binderrefuse " + (t != null ? t.def.defName : "nothing") + ": " + (why ?? "(taken)"));
                    return true;
                }
                case "binderfile":
                {
                    bool ok = BinderOpen != null && BinderOpen.SetFile(string.Join(",", a));
                    Log.Message("[IsoRim probe] binderfile " + string.Join(",", a) + ": " + (ok ? "loaded" : "FAILED"));
                    return true;
                }
                case "binderpick":
                {
                    // binderpick=<text>: the panel's Pick list, the first .obj whose name holds text picked from it
                    BinderOpen?.OpenPicker();
                    BinderList list = Find.WindowStack.WindowOfType<BinderList>();
                    int n = list != null ? list.Count : 0;
                    bool ok = list != null && list.PickFirst(a[0]);
                    Log.Message("[IsoRim probe] binderpick " + a[0] + ": " + n + " files listed, " + (ok ? "picked " + BinderOpen.Live.model : "FAILED"));
                    return true;
                }
                case "binderset":
                {
                    bool ok = BinderOpen != null && BinderOpen.SetField(a[0], a[1]);
                    Log.Message("[IsoRim probe] binderset " + a[0] + "=" + a[1] + ": " + (ok ? "ok" : "FAILED"));
                    return true;
                }
                case "bindercopy":
                    if (BinderOpen != null)
                    {
                        BinderOpen.CopyFrom(DefDatabase<ThingDef>.GetNamed(a[0]));
                        Log.Message("[IsoRim probe] bindercopy " + a[0] + ": model " + BinderOpen.Live.model);
                    }
                    return true;
                case "binderturn":
                    Log.Message("[IsoRim probe] binderturn: " + (BinderOpen != null && BinderOpen.TurnThing() ? "turned to " + BinderOpen.Thing.Rotation : "FAILED"));
                    return true;
                case "binderrevert":
                    BinderOpen?.Revert();
                    Log.Message("[IsoRim probe] binderrevert");
                    return true;
                case "binderclose":
                    BinderOpen?.Close(false);
                    return true;
                case "binderexport":
                {
                    BinderWindow w = BinderOpen;
                    if (w == null)
                    {
                        Log.Error("[IsoRim probe] binderexport: no panel");
                        return true;
                    }
                    w.Pack = a[0];
                    string dir = w.Export();
                    if (dir == null)
                    {
                        Log.Error("[IsoRim probe] binderexport FAILED");
                        return true;
                    }
                    List<string> files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => f.Substring(dir.Length + 1)).ToList();
                    files.Sort();
                    Log.Message("[IsoRim probe] binderexport " + dir + ": " + string.Join(", ", files));
                    foreach (string f in files.Where(f => f.StartsWith("Defs") || f.StartsWith("Patches")))
                    {
                        Log.Message("[IsoRim probe] binderexport " + f + ":\n" + File.ReadAllText(Path.Combine(dir, f)));
                    }
                    return true;
                }
                case "bindercheck":
                    BinderCheck(map, a);
                    return true;
                case "bindershot":
                    Find.UIRoot.screenshotMode.Active = false;
                    binderShot = a[0];
                    binderShotFrame = Time.frameCount + 40;
                    HookBinderFrames();
                    return true;
                case "bindertype":
                    BinderType(a[0], a[1]);
                    return true;
                case "coverage":
                {
                    Log.Message("[IsoRim probe] coverage: debug action IsoRim / Model coverage... " + (BinderMenuNode("Model coverage...") != null ? "listed" : "MISSING"));
                    List<CoverageReport.ModSummary> mods = CoverageReport.Build();
                    string file = CoverageReport.Export(mods);
                    foreach (CoverageReport.ModSummary s in mods)
                    {
                        Log.Message("[IsoRim probe] coverage " + a[0] + " " + s.mod + ": buildings " + s.buildingsMissing + "/" + s.buildings
                            + " without a model, items " + s.itemsMissing + "/" + s.items);
                    }
                    if (a.Length > 1)
                    {
                        foreach (string d in a[1].Split(';'))
                        {
                            CoverageReport.Row r = mods.SelectMany(s => s.rows).FirstOrDefault(x => x.defName == d);
                            ModelEntry e;
                            ThingDef td = DefDatabase<ThingDef>.GetNamedSilentFail(d);
                            bool has = td != null && ModelLibrary.TryGet(td, out e) && e.HasModel;
                            Log.Message("[IsoRim probe] coverage row " + d + ": " + (r != null ? r.mod + " | " + r.kind + " | " + r.size + " | " + r.fallback + " | " + r.drawer : "not listed")
                                + " (own model: " + has + ", how " + (td != null ? ModelLibrary.HowBound(td) ?? "-" : "no such def") + ")");
                        }
                    }
                    Find.WindowStack.Add(new CoverageWindow(mods));   // the window too, for a shot
                    Log.Message("[IsoRim probe] coverage " + a[0] + " written to " + file);
                    return true;
                }
                case "coverageclose":
                    Find.WindowStack.TryRemove(typeof(CoverageWindow), false);
                    return true;
                case "coverageopen":
                    CoverageWindow cw = Find.WindowStack.WindowOfType<CoverageWindow>();
                    if (cw != null)
                    {
                        foreach (string m in a)
                        {
                            foreach (CoverageReport.ModSummary s in cw.Mods)
                            {
                                if (s.mod.IndexOf(m.Replace('_', ' '), StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    cw.Expand(s.mod);
                                }
                            }
                        }
                    }
                    return true;
            }
            return false;
        }

        private static void BinderCheck(Map map, string[] a)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a[1]);
            if (def == null)
            {
                Log.Error("[IsoRim probe] bindercheck: no def " + a[1]);
                return;
            }
            ModelEntry e;
            bool got = ModelLibrary.TryGet(def, out e);
            ThingForm f = Forms.For(def);
            string line = "[IsoRim probe] bindercheck " + a[0] + " " + def.defName + ": how " + (ModelLibrary.HowBound(def) ?? "none") + ", form " + f.form;
            if (got && e.HasModel)
            {
                ModelDef md = e.def;
                Model m = e.variants[0];
                line += ", def " + md.defName + ", file " + Path.GetFileName(md.model ?? "") + ", tris " + m.positions.Length / 3
                    + ", builtFor " + md.builtFor + ", offset " + md.offset.ToString("0.###") + ", rotation " + md.rotation
                    + ", byFacing " + (md.rotationByFacing != null ? string.Join("/", md.rotationByFacing) : "-") + ", cutaway " + md.cutaway + ", scale " + md.scale
                    + ", overArt " + md.overArt + ", height " + f.height.ToString("0.###");
                Thing t = a.Length > 3 ? BinderThingAt(map, new[] { a[2], a[3], a[1] }) : null;
                if (t != null)
                {
                    Vector3 fit;
                    float yaw;
                    Vector3 lo = ModelBaker.PointOnMap(t, e, m, new Vector3(m.minX, 0f, m.minZ), out fit, out yaw);
                    Vector3 hi = ModelBaker.PointOnMap(t, e, m, new Vector3(m.maxX, m.maxY, m.maxZ), out fit, out yaw);
                    line += ", thing " + t.Rotation + ": yaw " + yaw.ToString("0.##") + ", fit " + fit.ToString("0.###") + ", box " + lo.ToString("0.###") + " .. " + hi.ToString("0.###");
                }
            }
            Log.Message(line);
        }

        // ---- frame-driven parts (Waylith's PrePollEvents: once a frame, before input is read)

        private static bool binderHooked;
        private static string binderShot;
        private static int binderShotFrame = -1;
        private static readonly Queue<Action> binderInput = new Queue<Action>();

        private static void HookBinderFrames()
        {
            if (binderHooked)
            {
                return;
            }
            binderHooked = true;
            Waylith.EngineApplication.Instance.PrePollEvents += BinderFrame;
        }

        private static int binderFlush;
        private static readonly System.Reflection.FieldInfo pendingSince = AccessTools.Field(typeof(View3D), "pendingSince");

        private static void BinderFrame(Waylith.EngineApplication app)
        {
            if (binderFlush > 0)
            {
                binderFlush--;
                pendingSince.SetValue(null, -100f);
            }
            if (binderInput.Count > 0)
            {
                try
                {
                    binderInput.Dequeue()();
                }
                catch (Exception e)
                {
                    Log.Error("[IsoRim probe] binder input step failed: " + e);
                    binderInput.Clear();
                }
            }
            if (binderShot != null && Time.frameCount >= binderShotFrame)
            {
                string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
                Directory.CreateDirectory(dir);
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "binder_" + binderShot + ".png"));
                Log.Message("[IsoRim probe] binder shot " + binderShot);
                binderShot = null;
                binderInput.Enqueue(() => { });
                binderInput.Enqueue(() => { });
                binderInput.Enqueue(() => { });
                binderInput.Enqueue(() => Find.UIRoot.screenshotMode.Active = true);   // after the shot has been taken
            }
        }


        // A point in UI coordinates (from the top left) as the input system wants it.
        private static Vector2 UiToInput(Vector2 ui)
        {
            return new Vector2(ui.x * Prefs.UIScale, Screen.height - ui.y * Prefs.UIScale);
        }

        private static void BinderType(string field, string text)
        {
            HookBinderFrames();
            if (BinderOpen == null)
            {
                Log.Error("[IsoRim probe] bindertype: no panel");
                return;
            }
            // The panel is drawn (and its fields placed) only with the interface on.
            Find.UIRoot.screenshotMode.Active = false;
            Vector2 p = Vector2.zero;
            for (int i = 0; i < 4; i++)
            {
                binderInput.Enqueue(() => { });
            }
            binderInput.Enqueue(() =>
            {
                Rect r;
                if (BinderOpen == null || !BinderOpen.FieldRects.TryGetValue(field, out r))
                {
                    Log.Error("[IsoRim probe] bindertype: no panel field " + field);
                    binderInput.Clear();
                    Find.UIRoot.screenshotMode.Active = true;
                    return;
                }
                p = UiToInput(r.center);
                InputSystem.QueueMouseMove(p);
            });
            Vector3 start = Vector3.zero;
            int turn = 0;
            binderInput.Enqueue(() => { InputSystem.QueueMouseMove(p); InputSystem.QueueMouseButton(0, true, p); });
            binderInput.Enqueue(() => { });
            binderInput.Enqueue(() => { InputSystem.QueueMouseMove(p); InputSystem.QueueMouseButton(0, false, p); });
            binderInput.Enqueue(() => { });
            binderInput.Enqueue(() =>
            {
                start = camRoot(Find.CameraDriver);
                turn = IsoRimMod.Settings.turn;
                Log.Message("[IsoRim probe] bindertype focus: control " + GUIUtility.keyboardControl);
            });
            // Type the text, one key a frame (down with its character, then up).
            foreach (char ch in text)
            {
                char c = ch;
                Key k;
                bool known = Enum.TryParse(char.ToUpperInvariant(c).ToString(), out k);
                binderInput.Enqueue(() =>
                {
                    if (known)
                    {
                        InputSystem.QueueKeyChange(k, true);
                    }
                    InputSystem.QueueTextInput(c);
                });
                binderInput.Enqueue(() =>
                {
                    if (known)
                    {
                        InputSystem.QueueKeyChange(k, false);
                    }
                });
            }
            // Hold W (the game's camera key) while the field has focus.
            binderInput.Enqueue(() => InputSystem.QueueKeyChange(Key.W, true));
            for (int i = 0; i < 25; i++)
            {
                binderInput.Enqueue(() => { });
            }
            binderInput.Enqueue(() => InputSystem.QueueKeyChange(Key.W, false));
            for (int i = 0; i < 5; i++)
            {
                binderInput.Enqueue(() => { });
            }
            Vector2 mapPoint = UiToInput(new Vector2(UI.screenWidth * 0.75f, UI.screenHeight * 0.6f));
            float typedMove = 0f;
            binderInput.Enqueue(() =>
            {
                typedMove = (camRoot(Find.CameraDriver) - start).magnitude;
                BinderWindow bw = BinderOpen;
                string value = bw != null ? bw.FieldText(field) : "?";
                Log.Message("[IsoRim probe] bindertype " + field + ": text '" + value + "', camera moved " + typedMove.ToString("0.###")
                    + " while typing, turn " + turn + " -> " + IsoRimMod.Settings.turn + ", camera keys blocked " + (bw != null && bw.preventCameraMotion));
                start = camRoot(Find.CameraDriver);
            });
            // Click the map (the field loses focus), then hold W again.
            binderInput.Enqueue(() => InputSystem.QueueMouseMove(mapPoint));
            binderInput.Enqueue(() => { InputSystem.QueueMouseMove(mapPoint); InputSystem.QueueMouseButton(0, true, mapPoint); });
            binderInput.Enqueue(() => { });
            binderInput.Enqueue(() => { InputSystem.QueueMouseMove(mapPoint); InputSystem.QueueMouseButton(0, false, mapPoint); });
            binderInput.Enqueue(() => { });
            binderInput.Enqueue(() => { });
            binderInput.Enqueue(() => InputSystem.QueueKeyChange(Key.W, true));
            for (int i = 0; i < 25; i++)
            {
                binderInput.Enqueue(() => { });
            }
            binderInput.Enqueue(() => InputSystem.QueueKeyChange(Key.W, false));
            for (int i = 0; i < 5; i++)
            {
                binderInput.Enqueue(() => { });
            }
            binderInput.Enqueue(() =>
            {
                float after = (camRoot(Find.CameraDriver) - start).magnitude;
                BinderWindow bw = BinderOpen;
                bool ok = typedMove < 0.001f && after > 0.5f && turn == IsoRimMod.Settings.turn;
                Log.Message("[IsoRim probe] bindertype after clicking the map: focus control " + GUIUtility.keyboardControl + ", camera moved "
                    + after.ToString("0.###") + " -> " + (ok ? "CHECK ok" : "CHECK FAILED"));
                Find.CameraDriver.SetRootPosAndSize(start, Find.CameraDriver.RootSize);
                Find.Selector.ClearSelection();
                Find.UIRoot.screenshotMode.Active = true;
            });
        }
    }
}
