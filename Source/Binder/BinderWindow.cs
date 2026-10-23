using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The model binder's panel (ModelBinder.Open): a non-pausing dev window on one thing. Every change goes straight into a
    // clone of the thing's ModelDef (live) and is drawn through ModelLibrary.SetOverride; the map rebuild that follows is
    // debounced by the view (View3D.StateSignature). Closing the panel keeps the change for this game; Revert drops it.
    public class BinderWindow : EditWindow
    {
        private readonly Thing thing;
        private readonly ThingDef def;
        private readonly Game game;
        private ModelDef live;
        private ModelEntry entry;          // the override's entry (null until applied)
        private string pathText = "";      // the file field
        private string fileNote = "";      // readout under it
        private List<string> warnings = new List<string>();
        private string pack = "MyModelPack";
        private string lastExport;
        private DateTime fileTime;
        private int pollFrame;
        private Vector2 scroll;
        private float viewHeight = 600f;
        private readonly Dictionary<string, string> buffers = new Dictionary<string, string>();

        private const string Prefix = "IsoRimBinder_";
        private static readonly FieldInfo CutawayField = typeof(ModelDef).GetField("cutaway");   // (Api 20) if this IsoRim has it

        public Thing Thing
        {
            get { return thing; }
        }

        public ModelDef Live
        {
            get { return live; }
        }

        public string LastExport
        {
            get { return lastExport; }
        }

        public string Pack
        {
            get { return pack; }
            set { pack = value; }
        }

        // Where each text field was drawn last (UI coordinates, screen top left), by id: "pack", "path", "offset.x" ...
        // (the harness probe clicks into them).
        public readonly Dictionary<string, Rect> FieldRects = new Dictionary<string, Rect>();

        // What a text field shows now.
        public string FieldText(string id)
        {
            if (id == "pack")
            {
                return pack;
            }
            if (id == "path")
            {
                return pathText;
            }
            string text;
            return buffers.TryGetValue(id, out text) ? text : "";
        }

        private void NoteRect(string id, Rect r)
        {
            float top = Margin + (optionalTitle.NullOrEmpty() ? 0f : Margin + 25f);
            FieldRects[id] = new Rect(windowRect.x + Margin + r.x - scroll.x, windowRect.y + top + r.y - scroll.y, r.width, r.height);
        }

        public BinderWindow(Thing t)
        {
            thing = t;
            def = t.def;
            game = Current.Game;
            optionalTitle = "R3D_Binder_Title".Translate() + ": " + t.def.LabelCap + " (" + t.def.defName + ")";
            closeOnClickedOutside = false;
            closeOnCancel = true;
            closeOnAccept = false;   // Enter in a field keeps the panel open
            if (!ModelLibrary.IsOverridden(def))
            {
                openLive.Remove(def);   // a change from an earlier game (overrides are dropped with it)
            }
            ModelEntry own = ModelLibrary.IsOverridden(def) ? null : ModelLibrary.OriginalEntry(def);
            if (ModelLibrary.IsOverridden(def) && openLive.TryGetValue(def, out ModelDef kept))
            {
                live = kept;   // reopened on a thing changed earlier this game: carry on from there
            }
            else
            {
                live = own != null && own.HasModel ? ModelBinder.Clone(own.def) : ModelBinder.Fresh(def);
                live.defName = "R3D_" + def.defName;
                live.thingDef = def.defName;
            }
            pathText = ModelBinder.FullPath(live, live.model) ?? "";
            NoteFile();
            if (ModelLibrary.IsOverridden(def))
            {
                entry = ModelLibrary.TryGet(def, out ModelEntry e) ? e : null;
                Readout();
            }
        }

        // The live def of each thing changed this game (a panel reopened on it carries on).
        private static readonly Dictionary<ThingDef, ModelDef> openLive = new Dictionary<ThingDef, ModelDef>();

        public override Vector2 InitialSize
        {
            get { return new Vector2(470f, 560f); }
        }

        // At the left edge, so the thing in the middle of the screen stays in view.
        protected override void SetInitialSizeAndPosition()
        {
            Vector2 size = InitialSize;
            windowRect = new Rect(10f, Mathf.Max(10f, (UI.screenHeight - size.y) / 2f - 60f), size.x, size.y);
        }

        // ---- changes (also used by the harness probe, ProbeBinder.cs)

        // Draws the thing with live now (the map follows after the view's debounce).
        public void Apply()
        {
            if (live.model.NullOrEmpty() && (live.models == null || live.models.Count == 0))
            {
                return;   // nothing to draw yet
            }
            entry = ModelLibrary.SetOverride(def, live);
            openLive[def] = live;
            DynamicModels.Forget();
            Readout();
        }

        // The model file: a full path, or one relative to the live def's mod.
        public bool SetFile(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            pathText = path;
            if (path.Length == 0 || !File.Exists(path))
            {
                fileNote = "R3D_Binder_NoFile".Translate();
                warnings.Clear();
                return false;
            }
            string full = Path.GetFullPath(path);
            ModelLibrary.Forget(full);
            Model m = ModelLibrary.LoadFile(full);
            if (m == null || m.positions == null || m.positions.Length == 0)
            {
                fileNote = "R3D_Binder_BadFile".Translate();
                warnings.Clear();
                return false;
            }
            live.model = full;
            live.models = null;          // one file now: the copied def's variants and per-look models no longer fit
            live.stackModels = null;
            live.byAppearance = null;
            live.byState = null;
            live.immatureModels = null;  // (Api 24) the copied def's young stage; its immatureHide still applies to the new file
            live.byStyle = null;
            live.parts = null;
            pathText = full;
            NoteFile();
            Apply();
            return true;
        }

        // Reads the model file(s) again (after a new export from Blender).
        public void Reload()
        {
            foreach (string f in FilesOf(live))
            {
                ModelLibrary.Forget(f);
            }
            NoteFile();
            Apply();
        }

        public void Revert()
        {
            ModelLibrary.ClearOverride(def);
            openLive.Remove(def);
            ModelEntry own = ModelLibrary.OriginalEntry(def);
            live = own != null && own.HasModel ? ModelBinder.Clone(own.def) : ModelBinder.Fresh(def);
            live.defName = "R3D_" + def.defName;
            live.thingDef = def.defName;
            entry = null;
            pathText = ModelBinder.FullPath(live, live.model) ?? "";
            buffers.Clear();
            NoteFile();
            warnings.Clear();
            DynamicModels.Forget();
        }

        // Starts again from another thing's model (its file, placement and options), bound to this thing.
        public void CopyFrom(ThingDef source)
        {
            ModelEntry e = ModelLibrary.OriginalEntry(source);
            if (source == def && ModelLibrary.IsOverridden(def))
            {
                e = ModelLibrary.OriginalEntry(def);
            }
            else if (ModelLibrary.IsOverridden(source) && ModelLibrary.TryGet(source, out ModelEntry cur))
            {
                e = cur;
            }
            if (e == null || !e.HasModel)
            {
                return;
            }
            live = ModelBinder.Clone(e.def);
            live.defName = "R3D_" + def.defName;
            live.thingDef = def.defName;
            if (source != def)
            {
                live.builtFor = e.def.builtFor.x > 0f ? e.def.builtFor : new Vector2(source.size.x, source.size.z);
            }
            pathText = ModelBinder.FullPath(live, live.model) ?? "";
            buffers.Clear();
            NoteFile();
            Apply();
        }

        // Sets one field of the live def by name, as the panel's fields do (harness probe): offset.x, builtFor.y, rotation,
        // rotationByFacing.2, scale, wallMounted, cutaway ...
        public bool SetField(string name, string value)
        {
            string[] p = name.Split('.');
            FieldInfo f = typeof(ModelDef).GetField(p[0]);
            if (f == null)
            {
                return false;
            }
            float num;
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out num);
            if (f.FieldType == typeof(float))
            {
                f.SetValue(live, num);
            }
            else if (f.FieldType == typeof(bool))
            {
                f.SetValue(live, value == "1" || value == "true");
            }
            else if (f.FieldType == typeof(Vector3) && p.Length > 1)
            {
                Vector3 v = (Vector3)f.GetValue(live);
                v[p[1] == "x" ? 0 : p[1] == "y" ? 1 : 2] = num;
                f.SetValue(live, v);
            }
            else if (f.FieldType == typeof(Vector2) && p.Length > 1)
            {
                Vector2 v = (Vector2)f.GetValue(live);
                v[p[1] == "x" ? 0 : 1] = num;
                f.SetValue(live, v);
            }
            else if (f.FieldType.IsEnum)
            {
                f.SetValue(live, Enum.Parse(f.FieldType, value, true));
            }
            else if (f.FieldType == typeof(List<float>) && p.Length > 1)
            {
                SetFacing(int.Parse(p[1]), num);
            }
            else
            {
                return false;
            }
            buffers.Clear();
            Apply();
            return true;
        }

        private void SetFacing(int i, float v)
        {
            List<float> by = live.rotationByFacing != null ? new List<float>(live.rotationByFacing) : new List<float>();
            while (by.Count < 4)
            {
                by.Add(0f);
            }
            by[i] = v;
            live.rotationByFacing = by.TrueForAll(x => Mathf.Abs(x) < 0.001f) ? null : by;
        }

        // Turns the thing itself a quarter turn clockwise (rotatable buildings), to check each facing.
        public bool TurnThing()
        {
            if (!def.rotatable || def.category != ThingCategory.Building || !thing.Spawned)
            {
                return false;
            }
            Map map = thing.Map;
            IntVec3 pos = thing.Position;
            Rot4 rot = thing.Rotation;
            rot.Rotate(RotationDirection.Clockwise);
            foreach (IntVec3 c in GenAdj.OccupiedRect(pos, rot, def.Size))
            {
                if (!c.InBounds(map))
                {
                    Messages.Message("R3D_Binder_NoRoom".Translate(), MessageTypeDefOf.RejectInput, false);
                    return false;
                }
                foreach (Thing other in c.GetThingList(map))
                {
                    if (other != thing && other.def.category == ThingCategory.Building)
                    {
                        Messages.Message("R3D_Binder_NoRoom".Translate(), MessageTypeDefOf.RejectInput, false);
                        return false;
                    }
                }
            }
            bool selected = Find.Selector.IsSelected(thing);
            thing.DeSpawn();
            GenSpawn.Spawn(thing, pos, map, rot, WipeMode.VanishOrMoveAside);
            if (selected)
            {
                Find.Selector.Select(thing, false, false);
            }
            return true;
        }

        public string Export()
        {
            string dir;
            string error = BinderExport.Write(live, def, pack, out dir);
            if (error != null)
            {
                Messages.Message(error, MessageTypeDefOf.RejectInput, false);
                return null;
            }
            lastExport = dir;
            Messages.Message("R3D_Binder_Exported".Translate(dir), MessageTypeDefOf.TaskCompletion, false);
            return dir;
        }

        // ---- readout

        private static IEnumerable<string> FilesOf(ModelDef md)
        {
            if (!md.model.NullOrEmpty())
            {
                yield return ModelBinder.FullPath(md, md.model);
            }
            if (md.models != null)
            {
                foreach (string m in md.models)
                {
                    yield return ModelBinder.FullPath(md, m);
                }
            }
            if (md.immatureModels != null)
            {
                foreach (string m in md.immatureModels)
                {
                    yield return ModelBinder.FullPath(md, m);
                }
            }
        }

        private void NoteFile()
        {
            string full = ModelBinder.FullPath(live, live.model);
            fileTime = full != null && File.Exists(full) ? File.GetLastWriteTimeUtc(full) : default(DateTime);
            if (full == null)
            {
                fileNote = "R3D_Binder_NoFile".Translate();
            }
        }

        // Size, faces and the SOP checks of the model now drawn.
        private void Readout()
        {
            warnings.Clear();
            Model m = entry != null && entry.HasModel ? entry.variants[0] : null;
            if (m == null)
            {
                fileNote = "R3D_Binder_BadFile".Translate();
                return;
            }
            float minY = 0f;
            for (int i = 0; i < m.positions.Length; i++)
            {
                minY = Mathf.Min(minY, m.positions[i].y);
            }
            fileNote = "R3D_Binder_Readout".Translate(m.positions.Length / 3, F(m.maxX - m.minX), F(m.maxY), F(m.maxZ - m.minZ));
            if (minY < -0.01f)
            {
                warnings.Add("R3D_Binder_BelowGround".Translate());
            }
            Vector2 fp = live.builtFor.x > 0f ? live.builtFor : new Vector2(def.size.x, def.size.z);
            if (m.minX < -fp.x / 2f - 0.05f || m.maxX > fp.x / 2f + 0.05f || m.minZ < -fp.y / 2f - 0.05f || m.maxZ > fp.y / 2f + 0.05f)
            {
                warnings.Add("R3D_Binder_Overhang".Translate());
            }
            bool stuff = false;
            foreach (ModelMaterial mm in m.materials)
            {
                stuff |= mm.tinted;
            }
            if (stuff && !def.MadeFromStuff)
            {
                warnings.Add("R3D_Binder_StuffOnStuffless".Translate());
            }
        }

        private static string F(float v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // ---- drawing

        public override void DoWindowContents(Rect inRect)
        {
            if (Current.Game != game || thing.Destroyed)
            {
                Close(false);
                return;
            }
            // Typing in a field must not move the camera (the game reads its move keys whatever has focus).
            preventCameraMotion = GUI.GetNameOfFocusedControl().StartsWith(Prefix);
            PollFile();

            Text.Font = GameFont.Small;
            Rect outRect = inRect;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, viewHeight);
            Widgets.BeginScrollView(outRect, ref scroll, view);
            float y = 0f;
            float w = view.width;

            // Status: where the thing's look comes from now.
            string how = ModelLibrary.IsOverridden(def) ? "R3D_Binder_Live".Translate().ToString()
                : ModelLibrary.OriginalHow(def) != null ? "R3D_Binder_HasModel".Translate(ModelLibrary.OriginalHow(def)).ToString()
                : "R3D_Binder_NoModel".Translate().ToString();
            GUI.color = Color.grey;
            Widgets.Label(new Rect(0f, y, w, 22f), how);
            GUI.color = Color.white;
            y += 24f;

            // File row: path field, picker, reload.
            Widgets.Label(new Rect(0f, y, 90f, 24f), "R3D_Binder_File".Translate());
            float bx = w - 130f;
            NoteRect("path", new Rect(90f, y, bx - 94f, 24f));
            GUI.SetNextControlName(Prefix + "path");
            string np = Widgets.TextField(new Rect(90f, y, bx - 94f, 24f), pathText);
            if (np != pathText)
            {
                pathText = np;
                if (File.Exists(np.Trim().Trim('"')) && np.Trim().EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                {
                    SetFile(np);
                }
            }
            if (Widgets.ButtonText(new Rect(bx, y, 60f, 24f), "R3D_Binder_Pick".Translate()))
            {
                OpenPicker();
            }
            if (Widgets.ButtonText(new Rect(bx + 64f, y, 66f, 24f), "R3D_Binder_Reload".Translate()))
            {
                Reload();
            }
            y += 26f;
            Widgets.Label(new Rect(90f, y, w - 90f, 22f), fileNote ?? "");
            y += 22f;
            foreach (string warn in warnings)
            {
                GUI.color = new Color(1f, 0.8f, 0.35f);
                Widgets.Label(new Rect(90f, y, w - 90f, 22f), warn);
                GUI.color = Color.white;
                y += 22f;
            }
            y += 4f;

            // Placement.
            Vector2 bf = live.builtFor;
            if (Pair(ref y, w, "R3D_Binder_BuiltFor", "builtFor", ref bf.x, ref bf.y))
            {
                live.builtFor = bf;
                Apply();
            }
            Vector3 off = live.offset;
            if (Triple(ref y, w, "R3D_Binder_Offset", "offset", ref off))
            {
                live.offset = off;
                Apply();
            }
            float rot = live.rotation;
            Widgets.Label(new Rect(0f, y, 90f, 24f), "R3D_Binder_Rotation".Translate());
            if (Num(new Rect(90f, y, 70f, 24f), "rotation", ref rot))
            {
                live.rotation = rot;
                Apply();
            }
            float qx = 170f;
            foreach (int q in new[] { 0, 90, 180, 270 })
            {
                if (Widgets.ButtonText(new Rect(qx, y, 48f, 24f), q.ToString()))
                {
                    live.rotation = q;
                    buffers.Remove("rotation");
                    Apply();
                }
                qx += 52f;
            }
            y += 28f;

            // Per facing (N E S W), and turning the thing to look at each.
            Widgets.Label(new Rect(0f, y, 90f, 24f), "R3D_Binder_PerFacing".Translate());
            string[] facings = { "N", "E", "S", "W" };
            float fx = 90f;
            for (int i = 0; i < 4; i++)
            {
                Widgets.Label(new Rect(fx, y, 16f, 24f), facings[i]);
                float v = live.rotationByFacing != null && i < live.rotationByFacing.Count ? live.rotationByFacing[i] : 0f;
                if (Num(new Rect(fx + 16f, y, 44f, 24f), "rotationByFacing." + i, ref v))
                {
                    SetFacing(i, v);
                    Apply();
                }
                fx += 64f;
            }
            if (def.rotatable && def.category == ThingCategory.Building
                && Widgets.ButtonText(new Rect(fx, y, w - fx, 24f), "R3D_Binder_Turn".Translate()))
            {
                TurnThing();
            }
            y += 26f;
            if (entry != null && def.rotatable)
            {
                GUI.color = Color.grey;
                string yaw = "";
                for (int i = 0; i < 4; i++)
                {
                    float yw = Mathf.Repeat(entry.Yaw(new Rot4(i)) + (entry.Swapped(def) ? 90f : 0f), 360f);
                    yaw += (i > 0 ? ", " : "") + facings[i] + " " + F(yw);
                }
                Widgets.Label(new Rect(90f, y, w - 90f, 22f), "R3D_Binder_Yaw".Translate(yaw) + (thing.Rotation.IsValid ? "  (" + facings[thing.Rotation.AsInt] + ")" : ""));
                GUI.color = Color.white;
                y += 22f;
            }
            y += 4f;

            Single(ref y, w, "R3D_Binder_Scale", "scale");
            Single(ref y, w, "R3D_Binder_Surface", "surfaceHeight");
            if (def.building != null && def.building.isSittable)
            {
                Single(ref y, w, "R3D_Binder_Seat", "seatHeight");
            }
            if (def.IsBed)
            {
                Single(ref y, w, "R3D_Binder_Bedding", "beddingTop");
            }

            // Walls.
            if (def.category == ThingCategory.Building)
            {
                Single(ref y, w, "R3D_Binder_WallFit", "wallFit");
                Check(ref y, w, "R3D_Binder_WallMounted", "wallMounted");
                if (live.wallMounted)
                {
                    Single(ref y, w, "R3D_Binder_MountHeight", "mountHeight");
                }
                if (live.models != null && live.models.Count > 1)
                {
                    Check(ref y, w, "R3D_Binder_Linked", "linked");
                }
                if (CutawayField != null)
                {
                    Widgets.Label(new Rect(0f, y, 150f, 24f), "R3D_Binder_Cutaway".Translate());
                    object cur = CutawayField.GetValue(live);
                    if (Widgets.ButtonText(new Rect(150f, y, 120f, 24f), cur.ToString()))
                    {
                        List<FloatMenuOption> options = new List<FloatMenuOption>();
                        foreach (object v in Enum.GetValues(CutawayField.FieldType))
                        {
                            object val = v;
                            options.Add(new FloatMenuOption(v.ToString(), () =>
                            {
                                CutawayField.SetValue(live, val);
                                Apply();
                            }));
                        }
                        Find.WindowStack.Add(new FloatMenu(options));
                    }
                    y += 28f;
                }
            }
            else
            {
                Check(ref y, w, "R3D_Binder_OverArt", "overArt");
                Check(ref y, w, "R3D_Binder_LayFlat", "layFlat");
            }
            Single(ref y, w, "R3D_Binder_Outline", "outline");
            Check(ref y, w, "R3D_Binder_GroundShadow", "groundShadow");
            if (entry != null && Forms.For(def).form != Form3D.Model)
            {
                GUI.color = new Color(1f, 0.8f, 0.35f);
                Widgets.Label(new Rect(0f, y, w, 22f), "R3D_Binder_NotDrawn".Translate());
                GUI.color = Color.white;
                y += 24f;
            }
            y += 6f;

            // Start over.
            float half = (w - 4f) / 2f;
            if (Widgets.ButtonText(new Rect(0f, y, half, 28f), "R3D_Binder_Copy".Translate()))
            {
                OpenCopyList();
            }
            if (Widgets.ButtonText(new Rect(half + 4f, y, half, 28f), "R3D_Binder_Revert".Translate()))
            {
                Revert();
            }
            y += 34f;

            // Export.
            Widgets.Label(new Rect(0f, y, 90f, 24f), "R3D_Binder_Pack".Translate());
            NoteRect("pack", new Rect(90f, y, w - 200f, 24f));
            GUI.SetNextControlName(Prefix + "pack");
            pack = Widgets.TextField(new Rect(90f, y, w - 200f, 24f), pack);
            if (Widgets.ButtonText(new Rect(w - 106f, y, 106f, 24f), "R3D_Binder_Export".Translate()))
            {
                Export();
            }
            y += 28f;
            if (lastExport != null)
            {
                GUI.color = Color.grey;
                Widgets.Label(new Rect(0f, y, w, 40f), lastExport);
                GUI.color = Color.white;
                y += 40f;
            }
            viewHeight = y + 10f;
            Widgets.EndScrollView();
        }

        // Clicking outside the panel drops the focus of its fields, so the camera keys work again.
        public override void Notify_ClickOutsideWindow()
        {
            base.Notify_ClickOutsideWindow();
            if (GUI.GetNameOfFocusedControl().StartsWith(Prefix))
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
            }
        }

        private void PollFile()
        {
            if (Time.frameCount < pollFrame)
            {
                return;
            }
            pollFrame = Time.frameCount + 60;
            string full = ModelBinder.FullPath(live, live.model);
            if (full == null || fileTime == default(DateTime) || !File.Exists(full))
            {
                return;
            }
            if (File.GetLastWriteTimeUtc(full) != fileTime)
            {
                Reload();   // saved again (a new export from Blender)
            }
        }

        public void OpenPicker()
        {
            List<BinderList.Entry> list = new List<BinderList.Entry>();
            foreach (ModelBinder.ObjFile f in ModelBinder.ObjFiles())
            {
                string path = f.path;
                list.Add(new BinderList.Entry { group = f.group, label = f.name, tip = path, pick = () => SetFile(path) });
            }
            Find.WindowStack.Add(new BinderList("R3D_Binder_Pick".Translate(), list));
        }

        private void OpenCopyList()
        {
            List<BinderList.Entry> list = new List<BinderList.Entry>();
            foreach (ThingDef d in ModelBinder.ModelledDefs())
            {
                ThingDef source = d;
                string group = d == def ? "R3D_Binder_ThisThing".Translate().ToString()
                    : d.modContentPack != null ? d.modContentPack.Name : "";
                list.Add(new BinderList.Entry { group = group, label = d.defName + " (" + d.label + ")", pick = () => CopyFrom(source) });
            }
            list.SortBy(e => e.group == "R3D_Binder_ThisThing".Translate().ToString() ? 0 : 1, e => e.group, e => e.label);
            Find.WindowStack.Add(new BinderList("R3D_Binder_Copy".Translate(), list));
        }

        // ---- field helpers: text boxes that keep what is typed until it parses

        private bool Num(Rect r, string id, ref float value)
        {
            string text;
            if (!buffers.TryGetValue(id, out text))
            {
                text = F(value);
            }
            NoteRect(id, r);
            GUI.SetNextControlName(Prefix + id);
            string typed = Widgets.TextField(r, text);
            if (typed == text)
            {
                return false;
            }
            buffers[id] = typed;
            float v;
            if (float.TryParse(typed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v))
            {
                value = v;
                return true;
            }
            return false;
        }

        private void Single(ref float y, float w, string label, string field)
        {
            FieldInfo f = typeof(ModelDef).GetField(field);
            float v = (float)f.GetValue(live);
            Widgets.Label(new Rect(0f, y, 150f, 24f), label.Translate());
            if (Num(new Rect(150f, y, 80f, 24f), field, ref v))
            {
                f.SetValue(live, v);
                Apply();
            }
            y += 26f;
        }

        private void Check(ref float y, float w, string label, string field)
        {
            FieldInfo f = typeof(ModelDef).GetField(field);
            bool v = (bool)f.GetValue(live);
            bool was = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, 250f, 24f), label.Translate(), ref v);
            if (v != was)
            {
                f.SetValue(live, v);
                Apply();
            }
            y += 26f;
        }

        private bool Pair(ref float y, float w, string label, string id, ref float a, ref float b)
        {
            Widgets.Label(new Rect(0f, y, 90f, 24f), label.Translate());
            bool changed = false;
            Widgets.Label(new Rect(90f, y, 14f, 24f), "x");
            changed |= Num(new Rect(104f, y, 60f, 24f), id + ".x", ref a);
            Widgets.Label(new Rect(170f, y, 14f, 24f), "z");
            changed |= Num(new Rect(184f, y, 60f, 24f), id + ".y", ref b);
            y += 26f;
            return changed;
        }

        private bool Triple(ref float y, float w, string label, string id, ref Vector3 v)
        {
            Widgets.Label(new Rect(0f, y, 90f, 24f), label.Translate());
            bool changed = false;
            string[] axes = { "x", "y", "z" };
            float x = 90f;
            for (int i = 0; i < 3; i++)
            {
                float c = v[i];
                Widgets.Label(new Rect(x, y, 14f, 24f), axes[i]);
                if (Num(new Rect(x + 14f, y, 60f, 24f), id + "." + axes[i], ref c))
                {
                    v[i] = c;
                    changed = true;
                }
                x += 80f;
            }
            y += 26f;
            return changed;
        }
    }
}
