using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The binder's shape row and what each shape shows (Api 52). Model is the file and placement panel; Raised is a def with no
    // model and no shape (the automatic look, its height and offset set); Card and Box are ModelDef.shape (Shapes.cs).
    public partial class BinderWindow
    {
        public enum PanelShape { Model, Raised, Card, Box }

        private static readonly string[] FaceKeys = { "Top", "North", "East", "South", "West" };
        private static readonly int[] NetColumn = { 1, 1, 2, 1, 0 };
        private static readonly int[] NetRow = { 1, 0, 1, 2, 1 };

        // The templates in pick list order (Models/Templates/<name>.obj).
        private static readonly string[] TemplateOrder =
        {
            "slab", "cylinder", "cone", "dome", "sphere", "pyramid", "wedge",
            "table", "stool", "shelf", "cabinet", "crate", "barrel", "pillar", "lamp",
            "machine", "vat", "tank", "console", "pipe",
        };

        private PanelShape panelShape;
        private ModelDef beforeShape;   // the def as the Model panel left it, for going back
        private BoxSide faceSelected;
        private Rect faceDropdown;
        private bool faceMenuPending;
        private bool fitPending;        // the shape changed: the window takes its size once

        private static string TemplatesDir
        {
            get { return Path.Combine(ModelBinder.IsoRimContent.RootDir, "Models", "Templates"); }
        }

        private void PanelFromLive()
        {
            bool file = !live.model.NullOrEmpty() || (live.models != null && live.models.Count > 0);
            panelShape = live.shape == ModelShape.Card ? PanelShape.Card : live.shape == ModelShape.Box ? PanelShape.Box
                : file || !Allowed(PanelShape.Raised) ? PanelShape.Model : PanelShape.Raised;
        }

        // Which shapes this thing can take. Walls and other linked things have no one sprite to shape; Raised is what the game's
        // map mesh gives (not things drawn every frame, or spots on the floor); a Box stands on a footprint (no items).
        public bool Allowed(PanelShape s)
        {
            if (s == PanelShape.Model)
            {
                return true;
            }
            bool linked = def.graphicData != null && def.graphicData.linkType != LinkDrawerType.None;
            if (linked || WallBoxes.Fill(def) > 0f)
            {
                return false;
            }
            switch (s)
            {
                case PanelShape.Raised:
                    return def.drawerType != DrawerType.RealtimeOnly && (def.category == ThingCategory.Item || def.altitudeLayer > AltitudeLayer.FloorEmplacement);
                case PanelShape.Box:
                    return def.category == ThingCategory.Building;
                default:
                    return true;
            }
        }

        public PanelShape Shape
        {
            get { return panelShape; }
            set
            {
                if (value == panelShape || !Allowed(value))
                {
                    return;
                }
                if (panelShape == PanelShape.Model)
                {
                    // a shape starts from nothing: the model's surface, turns and fittings are the model's
                    beforeShape = live;
                    live = ModelBinder.Fresh(def);
                }
                panelShape = value;
                buffers.Clear();
                fitPending = true;
                if (value == PanelShape.Model)
                {
                    if (beforeShape != null)
                    {
                        live = beforeShape;
                        beforeShape = null;
                    }
                    live.shape = ModelShape.None;
                    pathText = ModelBinder.FullPath(live, live.model) ?? "";
                    NoteFile();
                }
                else
                {
                    live.shape = value == PanelShape.Card ? ModelShape.Card : value == PanelShape.Box ? ModelShape.Box : ModelShape.None;
                    live.builtFor = new Vector2(def.size.x, def.size.z);
                }
                Apply();
            }
        }

        private bool HasFacings
        {
            get { return thing.Graphic is Graphic_Multi; }
        }

        public void SelectFace(BoxSide side)
        {
            faceSelected = side;
        }

        public void SetFace(BoxSide side, BoxLook look, bool mirror)
        {
            List<BoxFace> faces = live.boxFaces != null ? new List<BoxFace>(live.boxFaces) : new List<BoxFace>();
            faces.RemoveAll(f => f == null || f.side == side);
            if (look != BoxLook.Sprite || mirror)
            {
                faces.Add(new BoxFace { side = side, look = look, mirror = mirror });
            }
            live.boxFaces = faces.Count > 0 ? faces : null;
            Apply();
        }

        private void DrawShapeRow(ref float y, float w)
        {
            Widgets.Label(new Rect(0f, y, 90f, 24f), "R3D_Binder_Shape".Translate());
            float bw = (w - 90f) / 4f;
            float x = 90f;
            foreach (PanelShape s in Enum.GetValues(typeof(PanelShape)))
            {
                if (!Allowed(s))
                {
                    continue;
                }
                Rect r = new Rect(x, y, bw - 3f, 26f);
                x += bw;
                bool on = Shape == s;
                if (Widgets.ButtonText(r, ("R3D_Binder_Shape" + s).Translate(), true, true, on ? new Color(1f, 0.85f, 0.45f) : Color.white))
                {
                    Shape = s;
                }
                if (on)
                {
                    GUI.color = new Color(1f, 0.85f, 0.45f);
                    Widgets.DrawBox(r, 2);
                    GUI.color = Color.white;
                }
            }
            y += 34f;
        }

        // What Raised, Card and Box show.
        private void DrawShapeControls(ref float y, float w)
        {
            switch (Shape)
            {
                case PanelShape.Raised:
                    HeightSlider(ref y, w, Forms.For(def).height);
                    break;
                case PanelShape.Card:
                    FieldSlider(ref y, w, "R3D_Binder_Scale", "scale", 0.25f, 3f);
                    break;
                case PanelShape.Box:
                    HeightSlider(ref y, w, ShapeBoxes.HeightOf(live));
                    FieldSlider(ref y, w, "R3D_Binder_TopInset", "topInset", 0f, 0.5f);
                    FieldSlider(ref y, w, "R3D_Binder_Scale", "scale", 0.25f, 1.5f);
                    break;
            }
            Vector3 off = live.offset;
            if (Triple(ref y, w, "R3D_Binder_Offset", "offset", ref off))
            {
                live.offset = off;
                Apply();
            }
            y += 4f;
            if (Shape == PanelShape.Box)
            {
                DrawFaces(ref y, w);
            }
            if (Shape != PanelShape.Card)
            {
                bool raised = Shape == PanelShape.Raised;
                float outline = raised ? live.raisedOutline : live.outline;
                Widgets.Label(new Rect(0f, y, 90f, 24f), "R3D_Binder_Outline".Translate());
                if (Num(new Rect(90f, y, 60f, 24f), "outline", ref outline))
                {
                    if (raised)
                    {
                        live.raisedOutline = outline;
                    }
                    else
                    {
                        live.outline = outline;
                    }
                    Apply();
                }
                y += 26f;
            }
            bool shadow = live.groundShadow;
            Widgets.CheckboxLabeled(new Rect(0f, y, 150f, 24f), "R3D_Binder_GroundShadow".Translate(), ref shadow);
            if (shadow != live.groundShadow)
            {
                live.groundShadow = shadow;
                Apply();
            }
            y += 32f;
        }

        private void HeightSlider(ref float y, float w, float shown)
        {
            float v = live.height > 0f ? live.height : shown;
            if (Slider(ref y, w, "R3D_Binder_Height", "height", ref v, 0.05f, 2f))
            {
                live.height = v;
                Apply();
            }
        }

        private void FieldSlider(ref float y, float w, string label, string field, float min, float max)
        {
            System.Reflection.FieldInfo f = typeof(ModelDef).GetField(field);
            float v = (float)f.GetValue(live);
            if (Slider(ref y, w, label, field, ref v, min, max))
            {
                f.SetValue(live, v);
                Apply();
            }
        }

        // Label, number, slider on one row.
        private bool Slider(ref float y, float w, string label, string id, ref float value, float min, float max)
        {
            Widgets.Label(new Rect(0f, y, 90f, 24f), label.Translate());
            bool changed = Num(new Rect(90f, y, 60f, 24f), id, ref value);
            float v = Widgets.HorizontalSlider(new Rect(160f, y + 4f, w - 164f, 18f), value, min, max, false, null, null, null, 0.01f);
            if (!Mathf.Approximately(v, value))
            {
                value = v;
                buffers.Remove(id);
                changed = true;
            }
            y += 28f;
            return changed;
        }

        // The box unfolded (north above the top, west left, east right, south below), each cell showing what is on that face;
        // the selected face's settings beside it.
        private void DrawFaces(ref float y, float w)
        {
            const float Cell = 44f;
            Widgets.Label(new Rect(0f, y, 90f, 24f), "R3D_Binder_Faces".Translate());
            float nx = 90f;
            for (int i = 0; i < 5; i++)
            {
                Rect r = new Rect(nx + NetColumn[i] * (Cell + 2f), y + NetRow[i] * (Cell + 2f), Cell, Cell);
                DrawFaceCell(r, (BoxSide)i);
                if (Widgets.ButtonInvisible(r))
                {
                    faceSelected = (BoxSide)i;
                }
            }

            float ex = nx + 3f * (Cell + 2f) + 10f;
            float ew = w - ex;
            float ey = y;
            bool mirror;
            BoxLook look = ShapeBoxes.LookOf(live, faceSelected, out mirror);
            Widgets.Label(new Rect(ex, ey, ew, 24f), ("R3D_Binder_Face" + FaceKeys[(int)faceSelected]).Translate());
            ey += 25f;
            faceDropdown = new Rect(ex, ey, ew, 26f);
            if (Widgets.ButtonText(faceDropdown, LookLabel(look)) || faceMenuPending)
            {
                faceMenuPending = false;
                ShowFaceMenu();
            }
            ey += 30f;
            if (look < BoxLook.MeanColour)
            {
                bool m = mirror;
                Widgets.CheckboxLabeled(new Rect(ex, ey, ew, 24f), "R3D_Binder_FaceMirror".Translate(), ref m);
                if (m != mirror)
                {
                    SetFace(faceSelected, look, m);
                }
            }
            ey += 28f;
            if (faceSelected != BoxSide.Top && Widgets.ButtonText(new Rect(ex, ey, ew, 26f), "R3D_Binder_FaceAllSides".Translate()))
            {
                for (int i = 1; i < 5; i++)
                {
                    SetFace((BoxSide)i, look, mirror);
                }
            }
            y += 3f * (Cell + 2f) + 8f;
        }

        private void DrawFaceCell(Rect r, BoxSide side)
        {
            bool mirror;
            BoxLook look = ShapeBoxes.LookOf(live, side, out mirror);
            Widgets.DrawBoxSolid(r, new Color(0.13f, 0.14f, 0.16f));
            if (look == BoxLook.MeanColour || look == BoxLook.Edge)
            {
                Widgets.DrawBoxSolid(r.ContractedBy(3f), SpriteColors.MeanFor(thing));
            }
            if (look != BoxLook.None && look != BoxLook.MeanColour)
            {
                Rot4 rot = look >= BoxLook.SpriteNorth && look <= BoxLook.SpriteWest ? new Rot4((int)look - 1) : thing.Rotation;
                Material mat = thing.Graphic.MatAt(rot, thing);
                Rect t = r.ContractedBy(3f);
                if (look == BoxLook.Edge)
                {
                    t = new Rect(t.x, t.y + t.height * 0.4f, t.width, t.height * 0.2f);   // a strip of it
                }
                Rect coords = mirror ? new Rect(1f, 0f, -1f, 1f) : new Rect(0f, 0f, 1f, 1f);
                GUI.color = mat.color;
                GUI.DrawTextureWithTexCoords(t, mat.mainTexture, coords);
                GUI.color = Color.white;
            }
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;
            string tag = ("R3D_Binder_FaceTag" + FaceKeys[(int)side]).Translate();
            Vector2 size = Text.CalcSize(tag);
            Rect tr = new Rect(r.x + 1f, r.y + 1f, size.x + 3f, size.y);
            Widgets.DrawBoxSolid(tr, new Color(0f, 0f, 0f, 0.7f));
            Widgets.Label(new Rect(tr.x + 2f, tr.y, size.x + 2f, size.y), tag);
            Text.Font = GameFont.Small;
            bool selected = side == faceSelected;
            GUI.color = selected ? new Color(1f, 0.85f, 0.45f) : new Color(1f, 1f, 1f, 0.3f);
            Widgets.DrawBox(r, selected ? 2 : 1);
            GUI.color = Color.white;
            Widgets.DrawHighlightIfMouseover(r);
        }

        private static string LookLabel(BoxLook look)
        {
            return ("R3D_Binder_FaceMode" + look).Translate();
        }

        // Opens the selected face's dropdown on the next draw, when its place is known (harness probe).
        public void OpenFaceMenu()
        {
            faceMenuPending = true;
        }

        private void ShowFaceMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            BoxSide side = faceSelected;
            foreach (BoxLook look in Enum.GetValues(typeof(BoxLook)))
            {
                if ((!HasFacings && look >= BoxLook.SpriteNorth && look <= BoxLook.SpriteWest) || (look == BoxLook.Edge && side == BoxSide.Top))
                {
                    continue;
                }
                BoxLook l = look;
                options.Add(new FloatMenuOption(LookLabel(l), () =>
                {
                    bool mirror;
                    ShapeBoxes.LookOf(live, side, out mirror);
                    SetFace(side, l, mirror && l < BoxLook.MeanColour);
                }));
            }
            FloatMenu menu = new FloatMenu(options);
            menu.vanishIfMouseDistant = false;
            Find.WindowStack.Add(menu);
            menu.windowRect.x = windowRect.x + Margin + faceDropdown.x;
            menu.windowRect.y = windowRect.y + Margin + 35f + faceDropdown.yMax - scroll.y;
        }

        // The Pick list: the templates first, under their own names, then the files as before.
        private List<BinderList.Entry> PickEntries()
        {
            List<BinderList.Entry> list = new List<BinderList.Entry>();
            string dir = TemplatesDir;
            foreach (string name in TemplateOrder)
            {
                string path = Path.Combine(dir, name + ".obj");
                if (File.Exists(path))
                {
                    list.Add(new BinderList.Entry
                    {
                        group = "R3D_Binder_Templates".Translate(),
                        label = ("R3D_Binder_Template_" + name).Translate(),
                        tip = path,
                        pick = () => SetFile(path),
                    });
                }
            }
            foreach (ModelBinder.ObjFile f in ModelBinder.ObjFiles())
            {
                string path = f.path;
                if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new BinderList.Entry { group = f.group, label = f.name, tip = path, pick = () => SetFile(path) });
                }
            }
            return list;
        }

        // A template is built for one cell, and fills the footprint; made of no stuff, it takes the sprite's colour.
        private void TemplatePicked(string full)
        {
            if (full.StartsWith(TemplatesDir, StringComparison.OrdinalIgnoreCase))
            {
                live.builtFor = Vector2.one;
                live.spriteTint = !def.MadeFromStuff;
                buffers.Clear();
            }
        }
    }
}
