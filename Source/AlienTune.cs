using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 25, dev mode only: the alien tuning window (Debug actions > IsoRim > Alien tuning...). For one race pawn: live sliders for
    // the alien pack's placing fields (scale, head scale and offset), every anchor and the fits, drawn at once on every pawn of
    // that race (a live copy of its AlienModelDef, AlienFigures.SetLive; dropped with the game or by Reset). Sliders start from
    // the figure as drawn now (measured values for fields the pack leaves out); only moved ones are written. "Copy XML" puts
    // the AlienModelDef fields and anchors on the clipboard (and in the log).
    internal static class AlienTune
    {
        [DebugAction("IsoRim", "Alien tuning...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void OpenFromDebugMenu()
        {
            Pawn sel = Find.Selector.SingleSelectedThing as Pawn;
            if (Tunable(sel))
            {
                Open(sel);
                return;
            }
            DebugTools.curTool = new DebugTool("R3D_AlienTune_Pick".Translate(), () =>
            {
                Map map = Find.CurrentMap;
                IntVec3 c = UI.MouseCell();
                if (map == null || !c.InBounds(map))
                {
                    return;
                }
                foreach (Thing t in c.GetThingList(map))
                {
                    if (Tunable(t as Pawn))
                    {
                        DebugTools.curTool = null;
                        Open((Pawn)t);
                        return;
                    }
                }
            });
        }

        internal static bool Tunable(Pawn p)
        {
            return p != null && p.RaceProps != null && p.RaceProps.Humanlike && p.story != null && p.def.defName != "Human" && p.def.defName != "CreepJoiner";
        }

        internal static AlienTuneWindow Open(Pawn p)
        {
            Find.WindowStack.TryRemove(typeof(AlienTuneWindow));
            AlienTuneWindow w = new AlienTuneWindow(p);
            Find.WindowStack.Add(w);
            return w;
        }

        // A copy of an alien pack entry (its lists shared, its anchors copied) to change live.
        internal static AlienModelDef Clone(AlienModelDef src)
        {
            AlienModelDef d = new AlienModelDef();
            foreach (FieldInfo f in typeof(AlienModelDef).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                f.SetValue(d, f.GetValue(src));
            }
            d.anchors = new AlienAnchors();
            if (src.anchors != null)
            {
                foreach (FieldInfo f in typeof(AlienAnchors).GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    f.SetValue(d.anchors, f.GetValue(src.anchors));
                }
            }
            return d;
        }
    }

    internal class AlienTuneWindow : Window
    {
        private readonly ThingDef race;
        private readonly Pawn pawn;
        private readonly AlienModelDef live;
        private Vector2 scroll;
        private string note;

        // One row: a field of the live def as a vector (k components shown), its range, and whether the figure uses it.
        private sealed class Row
        {
            public string name;
            public int k;
            public float min, max;
            public Func<AlienModelDef, Vector3> get;     // NaN components: not set in the pack
            public Action<AlienModelDef, Vector3> set;
            public Func<AlienLook, Vector3> now;         // as drawn now (the start for unset fields)
        }

        private static readonly string[] Anchors = { "head", "face", "neck", "back", "waist", "handLeft", "handRight", "tailRoot" };
        private readonly List<Row> rows = new List<Row>();

        public AlienTuneWindow(Pawn p)
        {
            pawn = p;
            race = p.def;
            AlienModelDef pack = AlienFigures.PackFor(race);
            live = pack != null ? AlienTune.Clone(pack) : new AlienModelDef { defName = race.defName + "_AlienPack", alien = race.defName, figure = FigureMode.Human, anchors = new AlienAnchors() };
            if (live.figure == FigureMode.Card)
            {
                live.figure = FigureMode.Human;
            }
            AlienFigures.SetLive(race, live);
            optionalTitle = "R3D_AlienTune_Title".Translate() + ": " + race.LabelCap + " (" + race.defName + ")";
            closeOnClickedOutside = false;
            closeOnCancel = true;
            draggable = true;
            preventCameraMotion = false;
            doCloseX = true;
            Rows();
        }

        public override Vector2 InitialSize => new Vector2(470f, 620f);

        protected override void SetInitialSizeAndPosition()
        {
            windowRect = new Rect(10f, Mathf.Max(10f, (UI.screenHeight - InitialSize.y) / 2f - 40f), InitialSize.x, InitialSize.y);
        }

        private void Rows()
        {
            rows.Add(new Row { name = "scale", k = 1, min = 0.3f, max = 2f, get = d => new Vector3(d.scale, 0f, 0f), set = (d, v) => d.scale = v.x, now = r => new Vector3(r.scale, 0f, 0f) });
            rows.Add(new Row { name = "headScale", k = 1, min = 0.3f, max = 2f, get = d => new Vector3(d.headScale, 0f, 0f), set = (d, v) => d.headScale = v.x, now = r => new Vector3(r.headScale, 0f, 0f) });
            rows.Add(new Row { name = "headOffset", k = 3, min = -0.5f, max = 0.5f, get = d => d.headOffset, set = (d, v) => d.headOffset = new Vector3(0f, v.y, v.z), now = r => r.headShift });
            foreach (string a in Anchors)
            {
                FieldInfo f = typeof(AlienAnchors).GetField(a);
                string n = AlienFigures.Norm(a);
                bool head = n == "head" || n == "face";
                rows.Add(new Row
                {
                    name = a, k = 3, min = -0.8f, max = 1.8f,
                    get = d => (Vector3)f.GetValue(d.anchors),
                    set = (d, v) => f.SetValue(d.anchors, v),
                    now = r =>
                    {
                        Vector3 p;
                        if (!r.anchors.TryGetValue(n, out p))
                        {
                            return Vector3.zero;
                        }
                        return head ? r.head.MultiplyPoint3x4(p) : p;   // head ones from the head frame to the race's figure space
                    }
                });
            }
            rows.Add(new Row { name = "headFit", k = 3, min = 0.3f, max = 2f, get = d => d.anchors.headFit, set = (d, v) => d.anchors.headFit = v, now = r => r.headFit });
            rows.Add(new Row { name = "bodyFit", k = 3, min = 0.3f, max = 2f, get = d => d.anchors.bodyFit, set = (d, v) => d.anchors.bodyFit = v, now = r => r.bodyFit });
            rows.Add(new Row { name = "faceFit", k = 3, min = 0.3f, max = 2f, get = d => d.anchors.faceFit, set = (d, v) => d.anchors.faceFit = v, now = r => r.headFit });
        }

        // The value shown: the pack's (or what was moved), else the figure's as drawn now.
        private Vector3 Value(Row row)
        {
            Vector3 v = row.get(live);
            People.Look look = People.LastLook(pawn);
            Vector3 now = look != null && look.alien != null ? row.now(look.alien) : Vector3.zero;
            for (int i = 0; i < 3; i++)
            {
                if (float.IsNaN(v[i]))
                {
                    v[i] = now[i];
                }
            }
            return v;
        }

        // A slider moved (also the harness probe, ProbeAliens): the field set, every pawn of the race drawn again.
        internal void Set(string name, int component, float value)
        {
            // (figure, faceMode: the two mode buttons; any other name: a row)
            foreach (Row row in rows)
            {
                if (string.Equals(row.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    Vector3 v = Value(row);
                    v[component] = value;
                    row.set(live, v);
                    AlienFigures.SetLive(race, live);
                    return;
                }
            }
            if (name == "figure")
            {
                live.figure = (FigureMode)(int)value;
                AlienFigures.SetLive(race, live);
            }
            else if (name == "hairMode")
            {
                live.hair = (AlienHair)(int)value;
                AlienFigures.SetLive(race, live);
            }
            else if (name == "faceMode")
            {
                live.face = (AlienFace)(int)value;
                AlienFigures.SetLive(race, live);
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            float y = inRect.y;
            Rect bar = new Rect(inRect.x, y, inRect.width, 28f);
            float w = (bar.width - 12f) / 4f;
            if (Widgets.ButtonText(new Rect(bar.x, y, w, 28f), live.figure.ToString()))
            {
                Set("figure", 0, live.figure == FigureMode.Human ? (int)FigureMode.Own : (int)FigureMode.Human);
            }
            if (Widgets.ButtonText(new Rect(bar.x + w + 4f, y, w, 28f), "face " + live.face))
            {
                Set("faceMode", 0, live.face == AlienFace.IsoRim ? (int)AlienFace.None : (int)AlienFace.IsoRim);
            }
            if (Widgets.ButtonText(new Rect(bar.x + 2f * (w + 4f), y, w, 28f), "R3D_AlienTune_Copy".Translate()))
            {
                GUIUtility.systemCopyBuffer = Xml();
                note = "R3D_AlienTune_Copied".Translate();
                Log.Message("[IsoRim] Alien tuning, " + race.defName + ":\n" + GUIUtility.systemCopyBuffer);
            }
            if (Widgets.ButtonText(new Rect(bar.x + 3f * (w + 4f), y, w, 28f), "R3D_AlienTune_Reset".Translate()))
            {
                AlienFigures.SetLive(race, null);
                Close();
                return;
            }
            y += 32f;
            if (note != null)
            {
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f), note);
                y += 22f;
            }
            Rect outer = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
            Rect view = new Rect(0f, 0f, outer.width - 16f, rows.Count * 52f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float ry = 0f;
            foreach (Row row in rows)
            {
                Vector3 v = Value(row);
                Vector3 given = row.get(live);
                GUI.color = float.IsNaN(given.x) && float.IsNaN(given.y) ? new Color(0.7f, 0.7f, 0.7f) : Color.white;
                Widgets.Label(new Rect(0f, ry, view.width, 22f), row.name + "  " + Show(v, row.k));
                GUI.color = Color.white;
                float sw = row.k == 1 ? view.width : (view.width - 8f) / 3f;
                for (int i = 0; i < row.k; i++)
                {
                    if (row.name == "headOffset" && i == 0)
                    {
                        continue;   // (x unused)
                    }
                    Rect s = new Rect(i * (sw + 4f), ry + 24f, sw, 20f);
                    float nv = Widgets.HorizontalSlider(s, v[i], row.min, row.max, false, null, null, null, 0.005f);
                    if (Mathf.Abs(nv - v[i]) > 0.0001f)
                    {
                        Set(row.name, i, nv);
                    }
                }
                ry += 52f;
            }
            Widgets.EndScrollView();
        }

        private static string F(float f)
        {
            return f.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Show(Vector3 v, int k)
        {
            return k == 1 ? F(v.x) : "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")";
        }

        // The live entry's fields and anchors as AlienModelDef XML (only what the pack sets or the sliders moved).
        internal string Xml()
        {
            StringBuilder s = new StringBuilder();
            s.AppendLine("<IsoRim.AlienModelDef>");
            s.AppendLine("  <defName>" + live.defName + "</defName>");
            s.AppendLine("  <alien>" + race.defName + "</alien>");
            s.AppendLine("  <figure>" + live.figure + "</figure>");
            if (!live.modPackageId.NullOrEmpty())
            {
                s.AppendLine("  <modPackageId>" + live.modPackageId + "</modPackageId>");
            }
            if (live.hair != AlienHair.IsoRim)
            {
                s.AppendLine("  <hair>" + live.hair + "</hair>");
            }
            if (live.face != AlienFace.IsoRim)
            {
                s.AppendLine("  <face>" + live.face + "</face>");
            }
            if (!float.IsNaN(live.scale))
            {
                s.AppendLine("  <scale>" + F(live.scale) + "</scale>");
            }
            if (!float.IsNaN(live.headScale))
            {
                s.AppendLine("  <headScale>" + F(live.headScale) + "</headScale>");
            }
            if (!float.IsNaN(live.headOffset.y))
            {
                s.AppendLine("  <headOffset>" + Show(new Vector3(0f, live.headOffset.y, float.IsNaN(live.headOffset.z) ? 0f : live.headOffset.z), 3) + "</headOffset>");
            }
            s.AppendLine("  <!-- build, bodies, heads, addons: as in your pack -->");
            s.AppendLine("  <anchors>");
            foreach (string a in Anchors)
            {
                Vector3 v = (Vector3)typeof(AlienAnchors).GetField(a).GetValue(live.anchors);
                if (AlienAnchors.Set(v))
                {
                    s.AppendLine("    <" + a + ">" + Show(v, 3) + "</" + a + ">");
                }
            }
            foreach (string a in new[] { "headFit", "bodyFit", "faceFit" })
            {
                Vector3 v = (Vector3)typeof(AlienAnchors).GetField(a).GetValue(live.anchors);
                if (AlienAnchors.Set(v) && (a == "faceFit" || v != Vector3.one))
                {
                    s.AppendLine("    <" + a + ">" + Show(v, 3) + "</" + a + ">");
                }
            }
            s.AppendLine("  </anchors>");
            s.Append("</IsoRim.AlienModelDef>");
            return s.ToString();
        }
    }
}
