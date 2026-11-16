using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness probe for screen points of map points on stacked floors (2026-11-15, dragbox): the selection box's start, the
    // game's map-to-interface helper and text drawn at a map point (the case found: a mod that transpiled those helpers in its
    // mod constructor, before IsoRim hooked Camera.WorldToScreenPoint). Runs in the pointer test queue (ProbeInteract).
    //   boxcheck=1               every later drag / dragpx job also checks its box: the box (DragBox.ScreenRect) spans the
    //                            screen points of its two ends, and a click (both ends at one point) is no box
    //                            (DragBox.IsValid false), a drag longer than BoxCheckDrag interface pixels is one (CHECK)
    //   w2s=x,z,h,tag            a map point (h its height; U<z>: a floor 2 row) turned into a screen point three ways:
    //                            Camera.WorldToScreenPoint called here, the game's UI.MapToUIPosition, and where IsoRim
    //                            draws it (ScreenOf); CHECK: MapToUIPosition at the drawn point. Also logs who patches
    //                            Camera.WorldToScreenPoint and UI.MapToUIPosition
    //   textmote=x,z,text,tag    a text mote thrown at a map point (MoteMaker.ThrowText; the game draws it with
    //                            GenMapUI.DrawText in its own interface pass); where its label is drawn is read from
    //                            Widgets.Label; CHECK: the label's anchor at the drawn point of the mote; shot ix_<tag>_mote.png
    //   midstep=tag,dir          (ProjectZ) the drag with that tag steps the viewed floor mid-drag, as the floor keys do;
    //                            CHECK: the box's start corner stays at the click, on screen (2026-11-15, dragstale)
    public static partial class Probe
    {
        private static bool boxCheck;
        private const float BoxCheckTolerance = 2f;   // interface pixels a box edge or a label may be off
        private const float BoxCheckDrag = 50f;       // a drag at least this long (interface pixels) must be a box

        // textmote: the mote being watched, and its label as last drawn.
        private static string moteText;
        private static Thing moteThing;
        private static bool moteHooked;
        private static int moteSeenFrame = -1;
        private static Vector2 moteAnchor;      // the label's anchor (x middle, y top + 2, as GenMapUI.DrawText sets it)
        private static Vector2 moteAnchorAbs;   // the same in whole-screen interface coordinates (GUI groups undone)
        private static Vector2 moteWant;        // the drawn point of the mote, in interface coordinates
        private static int moteUiHeight;        // UI.screenHeight while the label was drawn (logged)

        private static bool DragBoxKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "boxcheck":
                    boxCheck = a[0] == "1";
                    return true;
                case "w2s":
                    HookMouse();
                    ixJobs.Add(new IxJob { kind = "w2s", tag = a[3], a = new Vector3(F(a[0]), F(a[2]), F(a[1])) });
                    return true;
                case "textmote":
                    HookMouse();
                    ixJobs.Add(new IxJob { kind = "tmote", tag = a[3], mode = a[2], a = new Vector3(F(a[0]), 0f, F(a[1])) });
                    return true;
                case "midstep":
                    midSteps[a[0]] = I(a[1]);
                    return true;
            }
            return false;
        }

        // midstep=tag,dir: in the drag with that tag, while the button is held (MidStepFrame), the viewed floor is stepped
        // as ProjectZ's floor keys step it (FloorView.StepShown(dir)). At MidStepCheckFrame the box's start corner is checked
        // against the pointer's spot at the click (it stays where the drag began), and logged with the floor now viewed.
        private static readonly Dictionary<string, int> midSteps = new Dictionary<string, int>();
        private static Vector2 midStepClick;
        private const int MidStepFrame = 4;
        private const int MidStepCheckFrame = 12;   // (DragStep takes its box shot on this frame)

        private static void MidStepStep(IxJob j)
        {
            int dir;
            if (!midSteps.TryGetValue(j.tag, out dir))
            {
                return;
            }
            if (j.frame == 0)
            {
                midStepClick = UiOf(Input.mousePosition);
                Log.Message("[IsoRim probe] midstep " + j.tag + ": click at UI " + midStepClick + ", start " + Find.Selector.dragBox.start
                    + " (floor of row " + Floors.FloorOfRow(Mathf.FloorToInt(Find.Selector.dragBox.start.z)) + "), floor viewed " + Floors.PointerTop);
            }
            if (j.frame == MidStepFrame)
            {
                Type fv = AccessTools.TypeByName("ProjectZ.FloorView");
                int was = Floors.PointerTop;
                if (fv == null)
                {
                    Log.Warning("[IsoRim probe] midstep: ProjectZ not loaded");
                    return;
                }
                AccessTools.Method(fv, "StepShown").Invoke(null, new object[] { dir });
                Log.Message("[IsoRim probe] midstep " + j.tag + ": FloorView.StepShown(" + dir + ") mid-drag, floor viewed " + was + " -> (next frame)");
            }
            if (j.frame == MidStepCheckFrame)
            {
                Rect box = Find.Selector.dragBox.ScreenRect;
                Vector2 mouse = Verse.UI.MousePositionOnUIInverted;
                // the corner of the box opposite the pointer
                Vector2 corner = new Vector2(Mathf.Abs(box.xMin - mouse.x) < Mathf.Abs(box.xMax - mouse.x) ? box.xMax : box.xMin,
                    Mathf.Abs(box.yMin - mouse.y) < Mathf.Abs(box.yMax - mouse.y) ? box.yMax : box.yMin);
                float off = (corner - midStepClick).magnitude;
                Rect screen = new Rect(0f, 0f, Verse.UI.screenWidth, Verse.UI.screenHeight);
                Check("ix " + j.tag + " start corner stays at the click (floor viewed " + Floors.PointerTop + ")", off <= BoxCheckTolerance && screen.Contains(corner),
                    "box (UI) " + box + ", start corner " + corner + ", click " + midStepClick + ", off by " + off.ToString("0.0") + ", pointer cell now " + Verse.UI.MouseCell());
            }
        }

        // (DragStep, frame 16) the box of a drag against its two ends.
        private static void BoxCheckStep(IxJob j, Vector2 pa, Vector2 pb, bool valid)
        {
            if (!boxCheck)
            {
                return;
            }
            Vector2 ua = UiOf(pa);
            Vector2 ub = UiOf(pb);
            Rect want = UnityEngine.Rect.MinMaxRect(Mathf.Min(ua.x, ub.x), Mathf.Min(ua.y, ub.y), Mathf.Max(ua.x, ub.x), Mathf.Max(ua.y, ub.y));
            Rect got = j.box;
            float off = Mathf.Max(Mathf.Max(Mathf.Abs(got.xMin - want.xMin), Mathf.Abs(got.xMax - want.xMax)),
                Mathf.Max(Mathf.Abs(got.yMin - want.yMin), Mathf.Abs(got.yMax - want.yMax)));
            Check("ix " + j.tag + " box (" + Yp() + ")", off <= BoxCheckTolerance, "box (UI) " + got + ", ends at " + want + ", off by " + off.ToString("0.0"));
            float length = (ua - ub).magnitude;
            if (length < 0.01f)
            {
                Check("ix " + j.tag + " click is no box", !valid, "valid " + valid);
            }
            else if (length > BoxCheckDrag)
            {
                Check("ix " + j.tag + " drag is a box", valid, "valid " + valid + ", length " + length.ToString("0"));
            }
        }

        private static void W2sStep(IxJob j)
        {
            if (j.frame != 0)
            {
                return;
            }
            Vector3 p = j.a;
            Vector3 direct = Find.Camera.WorldToScreenPoint(p);
            Vector2 viaMap = p.MapToUIPosition();
            Vector2 want = UiOf(ScreenOf(p));
            float off = (viaMap - want).magnitude;
            Log.Message("[IsoRim probe] w2s " + j.tag + " (" + Yp() + ", floors on " + Floors.On + ", floor of row " + Floors.FloorOfRow(Mathf.FloorToInt(p.z))
                + "): map " + p + ", WorldToScreenPoint here " + direct + " = UI " + UiOf(direct) + ", MapToUIPosition " + viaMap + ", drawn at UI " + want
                + "; " + PatchOwners());
            Check("w2s " + j.tag + " MapToUIPosition at the drawn point", off <= BoxCheckTolerance, "off by " + off.ToString("0.0"));
        }

        private static string PatchOwners()
        {
            MethodInfo w2s = AccessTools.Method(typeof(Camera), nameof(Camera.WorldToScreenPoint), new[] { typeof(Vector3) });
            MethodInfo m2u = AccessTools.Method(typeof(Verse.UI), nameof(Verse.UI.MapToUIPosition));
            return "Camera.WorldToScreenPoint prefixes [" + Owners(w2s, i => i.Prefixes) + "], UI.MapToUIPosition transpilers [" + Owners(m2u, i => i.Transpilers)
                + "] prefixes [" + Owners(m2u, i => i.Prefixes) + "]";
        }

        private static string Owners(MethodInfo m, Func<HarmonyLib.Patches, IEnumerable<Patch>> which)
        {
            HarmonyLib.Patches info = m != null ? Harmony.GetPatchInfo(m) : null;
            return info == null ? "" : string.Join(", ", which(info).Select(p => p.owner + ":" + p.PatchMethod.Name));
        }

        private static void TextMoteStep(Map map, IxJob j)
        {
            if (j.frame == 0)
            {
                CloseWindows();
                Find.UIRoot.screenshotMode.Active = false;
                if (!moteHooked)
                {
                    moteHooked = true;
                    new Harmony("peren.isorim17.probe.textmote").Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.Label), new[] { typeof(Rect), typeof(string) }),
                        prefix: new HarmonyMethod(typeof(Probe), nameof(MoteLabel_Prefix)));
                }
                moteText = j.mode;
                moteSeenFrame = -1;
                MoteMaker.ThrowText(j.a, map, j.mode, Color.white, 30f);
                IntVec3 c = j.a.ToIntVec3();
                moteThing = c.InBounds(map) ? c.GetThingList(map).OfType<MoteText>().LastOrDefault(m => m.text == j.mode) : null;
                Log.Message("[IsoRim probe] textmote " + j.tag + ": thrown at " + j.a + " (cell " + c + ", floor of row " + Floors.FloorOfRow(c.z) + "), mote " + (moteThing != null));
            }
            if (j.frame == 10)
            {
                IxShot(j, "mote");
            }
            if (j.frame == 12)
            {
                bool seen = moteSeenFrame >= 0;
                float off = seen ? (moteAnchorAbs - moteWant).magnitude : float.MaxValue;
                Log.Message("[IsoRim probe] textmote " + j.tag + " (" + Yp() + "): label anchor " + (seen ? moteAnchor + " (whole screen " + moteAnchorAbs + ")" : "not seen")
                    + ", mote drawn at " + moteWant + " (whole screen, interface units), UI scale " + Prefs.UIScale + ", UI.screenHeight then " + moteUiHeight + ", Screen.height " + Screen.height);
                Check("textmote " + j.tag + " label at the mote", seen && off <= BoxCheckTolerance, seen ? "off by " + off.ToString("0.0") : "label not drawn");
                if (moteThing != null && moteThing.Spawned)
                {
                    moteThing.Destroy();
                }
                moteThing = null;
                moteText = null;
            }
        }

        // The watched mote's label (GenMapUI.DrawText: anchor x in the middle, y 2 above the top), and where the mote is
        // drawn at that moment (it drifts), both on the whole screen: the label with GUI groups undone, the mote flipped
        // against the screen's own height (UI.screenHeight may be changed while the interface is drawn).
        public static void MoteLabel_Prefix(Rect rect, string label)
        {
            if (moteText == null || label != moteText || moteThing == null)
            {
                return;
            }
            moteAnchor = new Vector2(rect.x + rect.width / 2f, rect.y + 2f);
            moteAnchorAbs = GUIUtility.GUIToScreenPoint(moteAnchor);
            Vector3 at = ((Mote)moteThing).exactPosition;
            Vector2 s = ScreenOf(new Vector3(at.x, 0f, at.z));
            moteWant = new Vector2(s.x, Screen.height - s.y) / Prefs.UIScale;
            moteUiHeight = Verse.UI.screenHeight;
            moteSeenFrame = Time.frameCount;
        }
    }
}
