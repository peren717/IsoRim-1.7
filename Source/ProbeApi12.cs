using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only test of Api version 12 (the upright drawing scope), with a sample consumer that uses only the Api, as a
    // mod's DrawProgressBar would: around the battery in one cell's drawing (a Building_Battery.DrawAt postfix) it opens
    // Api.Upright(height) and draws a vanilla fillable bar (GenDraw.DrawFillableBar: magenta fill on dark blue, 60 %) and an
    // icon quad (Api.DrawMesh, the out-of-fuel icon) 0.35 cells north of the bar. Keys:
    //   upright=x,z,h | upright=off   the sample on the battery in that cell at height h (cells), or off
    //   upcheck=label,N               after N frames: where the bar and icon were drawn (Graphics' single draw entries,
    //                                 after the floors), with PASS/FAIL lines: in 3D each is one card facing the camera
    //                                 (its normal along the view, its edges level and upright on screen), at height h over
    //                                 the bar's cell, the fill inside the bar, the icon over the bar; DrawHook's state
    //                                 the same after each scope as before it. With the 3D view off: nothing changed (flat)
    //   uptest=1                      on the next frame, inside the battery's drawing: nesting, End without Begin, double
    //                                 Dispose, an exception in a using block, a scope left open by the thing's drawing
    //                                 (closed after it: the other battery and the next frame see none), and one left open
    //                                 outside any thing's drawing (closed by the next frame)
    public static partial class Probe
    {
        private struct UprState
        {
            public bool active, billboard, anchored, pivoted, pivotOnFirst, floor;
            public float height;
            public Matrix4x4 xform;

            public static UprState Now()
            {
                return new UprState
                {
                    active = DrawHook.active, billboard = DrawHook.billboard, anchored = DrawHook.hasAnchor, pivoted = DrawHook.hasPivot,
                    pivotOnFirst = DrawHook.pivotOnFirst, floor = DrawHook.floor, height = DrawHook.billboardHeight, xform = DrawHook.xform,
                };
            }

            public bool Same(UprState o)
            {
                return active == o.active && billboard == o.billboard && anchored == o.anchored && pivoted == o.pivoted && pivotOnFirst == o.pivotOnFirst
                    && floor == o.floor && height == o.height && xform == o.xform;
            }

            public override string ToString()
            {
                return "active " + active + " billboard " + billboard + " pivoted " + pivoted + " pivotOnFirst " + pivotOnFirst + " height " + height;
            }
        }

        private static bool uprPatched, uprOn, uprRecord;
        private static IntVec3 uprCell = IntVec3.Invalid;
        private static float uprHeight = 1.6f;
        private static Material uprFill, uprEmpty, uprIcon;
        private static readonly Dictionary<Material, Matrix4x4> uprDrawn = new Dictionary<Material, Matrix4x4>();
        private static readonly Dictionary<Material, int> uprDrawnFrame = new Dictionary<Material, int>();
        private static int uprFrames, uprCheckAt = -1, uprScopes, uprRestoreBad, uprInsideBad, uprTestStage;
        private static string uprLabel, uprInsideNote;
        private static Vector3 uprBarCentre;
        private const float UprBarW = 0.9f, UprBarH = 0.16f, UprMargin = 0.1f, UprFillPct = 0.6f, UprIconSize = 0.35f, UprIconNorth = 0.35f;

        private static bool Api12Key(string key, string[] a, Map map)
        {
            if (!key.StartsWith("up"))
            {
                return false;
            }
            switch (key)
            {
                case "upright":
                    Api12Patch();
                    uprOn = a[0] != "off";
                    if (uprOn)
                    {
                        uprCell = new IntVec3(I(a[0]), 0, I(a[1]));
                        uprHeight = a.Length > 2 ? F(a[2]) : 1.6f;
                    }
                    Log.Message("[IsoRim probe] upright sample " + (uprOn ? "on the battery at " + uprCell + ", height " + uprHeight : "off") + ", Api.Version " + Api.Version
                        + ", battery there: " + (uprOn && uprCell.GetFirstBuilding(map) is Building_Battery));
                    return true;
                case "upcheck":
                    Api12Patch();
                    uprLabel = a[0];
                    uprCheckAt = I(a[1]);
                    uprFrames = 0;
                    uprScopes = uprRestoreBad = uprInsideBad = 0;
                    uprInsideNote = null;
                    uprDrawn.Clear();
                    uprDrawnFrame.Clear();
                    return true;
                case "uptest":
                    Api12Patch();
                    uprTestStage = 1;
                    return true;
            }
            return false;
        }

        private static void Api12Patch()
        {
            if (uprPatched)
            {
                return;
            }
            uprPatched = true;
            uprFill = SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.95f, 0.15f, 0.75f));
            uprEmpty = SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.08f, 0.08f, 0.35f));
            uprIcon = MaterialPool.MatFrom("Things/Special/ForbiddenOverlay", ShaderDatabase.MetaOverlay);
            Harmony h = new Harmony("peren.isorim.probe.api12");
            h.Patch(AccessTools.DeclaredMethod(typeof(Building_Battery), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(Probe), nameof(Api12BatteryPrefix)), postfix: new HarmonyMethod(typeof(Probe), nameof(Api12BatteryPostfix)));
            h.Patch(AccessTools.Method(typeof(MapComponentUtility), nameof(MapComponentUtility.MapComponentUpdate)), postfix: new HarmonyMethod(typeof(Probe), nameof(Api12Update)));
            HarmonyMethod rec = new HarmonyMethod(typeof(Probe), nameof(Api12Record)) { priority = Priority.Last };
            int n = 0;
            foreach (System.Reflection.MethodInfo m in AccessTools.GetDeclaredMethods(typeof(Graphics)))
            {
                if (Floors.IsSingleEntry(m) && m.GetParameters().Any(p => p.Name == "material" && p.ParameterType == typeof(Material)))
                {
                    h.Patch(m, prefix: rec);
                    n++;
                }
            }
            Log.Message("[IsoRim probe] upright: recording " + n + " Graphics entries");
        }

        // What the bar and the icon were drawn with (after the floors), while the sample draws.
        public static void Api12Record(Matrix4x4 matrix, Material material)
        {
            if (uprRecord && material != null && (material == uprFill || material == uprEmpty || material == uprIcon))
            {
                uprDrawn[material] = matrix;
                uprDrawnFrame[material] = Time.frameCount;
            }
        }

        // ---- the sample consumer (Api only), as a mod's prefix/postfix pair around its DrawProgressBar

        private static int uprSeq, uprSeqA, uprLeakFrame = -1, uprLeakOutsideFrame = -1;
        private static string uprBNote;

        public static void Api12BatteryPrefix(Building_Battery __instance)
        {
            if (uprTestStage == 3 && __instance.Position != uprCell)
            {
                // the other battery while battery A left a scope open this frame (stage 3)
                uprBNote = (uprBNote ?? "") + "[frame " + (Time.frameCount - uprLeakFrame) + " after A " + (uprSeqA > 0 && Time.frameCount == uprLeakFrame) + ": depth " + UprightScopes.Depth + " billboard " + DrawHook.billboard + "]";
            }
        }

        public static void Api12BatteryPostfix(Building_Battery __instance, Vector3 drawLoc)
        {
            if (!uprOn || __instance.Position != uprCell)
            {
                return;
            }
            uprSeq++;
            UprState before = UprState.Now();
            uprRecord = true;
            uprBarCentre = drawLoc + Vector3.up * 0.1f;
            using (Api.Upright(uprHeight))
            {
                // the test's bookkeeping: inside, the scope is on (3D) or changes nothing (no 3D view)
                UprState inside = UprState.Now();
                bool ok = View3D.Ready ? inside.active && inside.billboard && inside.pivotOnFirst && !inside.pivoted && inside.height == uprHeight : inside.Same(before);
                if (!ok)
                {
                    uprInsideBad++;
                    uprInsideNote = inside.ToString();
                }

                GenDraw.DrawFillableBar(new GenDraw.FillableBarRequest
                {
                    center = uprBarCentre,
                    size = new Vector2(UprBarW, UprBarH),
                    fillPercent = UprFillPct,
                    filledMat = uprFill,
                    unfilledMat = uprEmpty,
                    margin = UprMargin,
                    rotation = Rot4.North,
                });
                Vector3 icon = uprBarCentre + new Vector3(0f, 0.02f, UprIconNorth);
                Api.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(icon, Quaternion.identity, new Vector3(UprIconSize, 1f, UprIconSize)), uprIcon);
            }
            uprRecord = false;
            uprScopes++;
            if (!UprState.Now().Same(before) || UprightScopes.Depth != 0)
            {
                uprRestoreBad++;
            }
            if (uprTestStage == 2)
            {
                Api12ScopeTests(before);
                uprTestStage = 3;
                uprLeakFrame = Time.frameCount;
                uprSeqA = uprSeq;
                Api.BeginUpright(0.5f);   // left open on purpose: closed after this battery's drawing
            }
        }

        private static void UprResult(string what, bool pass, string detail)
        {
            Log.Message("[IsoRim probe] upcheck " + uprLabel + " " + (pass ? "PASS" : "FAIL") + " " + what + (detail != null ? ": " + detail : ""));
        }

        private static void Api12ScopeTests(UprState s0)
        {
            string l = uprLabel;
            bool on = View3D.Ready;
            // nesting
            Api.BeginUpright(1f);
            bool n1 = UprightScopes.Depth == 1 && (!on || DrawHook.billboardHeight == 1f);
            Api.BeginUpright(2f, new Vector3(5f, 0f, 5f));
            bool n2 = UprightScopes.Depth == 2 && (!on || DrawHook.billboardHeight == 2f && DrawHook.hasPivot && DrawHook.billboardPivot.x == 5f);
            Api.EndUpright();
            bool n3 = UprightScopes.Depth == 1 && (!on || DrawHook.billboardHeight == 1f && !DrawHook.hasPivot && DrawHook.pivotOnFirst);
            Api.EndUpright();
            bool n4 = UprightScopes.Depth == 0 && UprState.Now().Same(s0);
            UprResult("nesting", n1 && n2 && n3 && n4, "inner " + n1 + " " + n2 + ", after inner End " + n3 + ", after outer End restored " + n4);
            // End without Begin
            Api.EndUpright();
            UprResult("End without Begin does nothing", UprightScopes.Depth == 0 && UprState.Now().Same(s0), null);
            // handles: double Dispose, an old handle after its scope closed
            UprightScope ha = Api.Upright(1f);
            ha.Dispose();
            UprightScope hb = Api.Upright(2f);
            ha.Dispose();
            bool h1 = UprightScopes.Depth == 1;
            hb.Dispose();
            hb.Dispose();
            UprResult("handles (double Dispose, stale handle)", h1 && UprightScopes.Depth == 0 && UprState.Now().Same(s0), "stale handle left the newer scope open: " + h1);
            // an exception inside a using block
            try
            {
                using (Api.Upright(1f))
                {
                    using (Api.Upright(3f))
                    {
                        throw new InvalidOperationException("test");
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            UprResult("exception in using blocks", UprightScopes.Depth == 0 && UprState.Now().Same(s0), null);
            // an outer handle closes inner scopes left open
            UprightScope outer = Api.Upright(1f);
            Api.BeginUpright(2f);
            Api.BeginUpright(3f);
            outer.Dispose();
            UprResult("outer Dispose closes inner scopes left open", UprightScopes.Depth == 0 && UprState.Now().Same(s0), null);
        }

        public static void Api12Update(Map map)
        {
            if (map != Find.CurrentMap)
            {
                return;
            }
            if (uprTestStage == 1)
            {
                uprTestStage = 2;   // the scope tests run inside the battery's next drawing
            }
            else if (uprTestStage == 3 && Time.frameCount > uprLeakFrame)
            {
                // the scope battery A left open: closed after its drawing (the other battery, drawn in that frame or the next, saw none)
                bool closed = uprBNote != null && !uprBNote.Contains("depth 1") && !uprBNote.Contains("billboard True");
                UprResult("scope left open by a thing's drawing is closed after it", closed && UprightScopes.Depth == 0, "other battery " + (uprBNote ?? "not drawn") + ", depth now " + UprightScopes.Depth);
                uprBNote = null;
                // one left open outside any thing's drawing (here, MapComponentUpdate)
                Api.BeginUpright(0.5f);
                uprLeakOutsideFrame = Time.frameCount;
                uprTestStage = 4;
            }
            else if (uprTestStage == 4 && Time.frameCount > uprLeakOutsideFrame + 1)
            {
                UprResult("scope left open outside a thing's drawing is closed by the next frame", UprightScopes.Depth == 0, "depth " + UprightScopes.Depth);
                uprTestStage = 0;
            }
            if (uprCheckAt < 0)
            {
                return;
            }
            uprFrames++;
            if (uprFrames < uprCheckAt)
            {
                return;
            }
            uprCheckAt = -1;
            Api12Check();
        }

        // The four corners of a plane10 drawn with m, in true space (drawn y times HeightSquash).
        private static Vector3[] UprCorners(Matrix4x4 m)
        {
            Vector3[] c = { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f) };
            float s = Api.HeightSquash;
            for (int i = 0; i < 4; i++)
            {
                Vector3 w = m.MultiplyPoint3x4(c[i]);
                c[i] = View3D.Ready ? new Vector3(w.x, w.y * s, w.z) : w;
            }
            return c;
        }

        private static void Api12Check()
        {
            UprResult("scopes drawn", uprScopes > 0, uprScopes + " scopes over the check");
            UprResult("DrawHook state the same after each scope, none left open", uprRestoreBad == 0, uprRestoreBad + " bad");
            UprResult(View3D.Ready ? "scope on inside (billboard, pivot on first, height)" : "no 3D view: scope changes nothing inside", uprInsideBad == 0, uprInsideNote);
            foreach (Material m in new[] { uprEmpty, uprFill, uprIcon })
            {
                string name = m == uprEmpty ? "bar" : m == uprFill ? "fill" : "icon";
                if (!uprDrawn.ContainsKey(m) || Time.frameCount - uprDrawnFrame[m] > 3)
                {
                    UprResult(name + " drawn", false, "not drawn lately");
                    return;
                }
            }
            Matrix4x4 bm = uprDrawn[uprEmpty], fm = uprDrawn[uprFill], im = uprDrawn[uprIcon];
            if (!View3D.Ready)
            {
                // flat, as the game draws it: the bar's own TRS (no stand-up, no lift)
                bool flat = Mathf.Abs(bm.m11 - 1f) < 1e-4f && Mathf.Abs(bm.m12) < 1e-4f && Mathf.Abs(bm.m10) < 1e-4f
                    && Mathf.Abs(bm.m03 - uprBarCentre.x) < 1e-3f && Mathf.Abs(bm.m23 - uprBarCentre.z) < 1e-3f && Mathf.Abs(bm.m13 - uprBarCentre.y) < 1e-3f;
                UprResult("no 3D view: the bar is drawn flat where the game puts it", flat, "matrix y axis (" + bm.m01 + ", " + bm.m11 + ", " + bm.m21 + "), at " + bm.GetColumn(3));
                return;
            }
            float yaw = View3D.Yaw * Mathf.Deg2Rad, pitch = View3D.Pitch * Mathf.Deg2Rad;
            Vector3 fwd = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), -Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch));
            Vector2[] bs = null, fs = null, ics = null;
            foreach (Material m in new[] { uprEmpty, uprFill, uprIcon })
            {
                string name = m == uprEmpty ? "bar" : m == uprFill ? "fill" : "icon";
                Vector3[] c = UprCorners(uprDrawn[m]);
                Vector3 n = Vector3.Cross(c[1] - c[0], c[3] - c[0]).normalized;
                float facing = Mathf.Abs(Vector3.Dot(n, fwd));
                Vector2[] sc = c.Select(View3D.WorldToScreen).ToArray();
                float level = Mathf.Max(Mathf.Abs(sc[0].y - sc[1].y), Mathf.Abs(sc[3].y - sc[2].y));
                float upright = Mathf.Max(Mathf.Abs(sc[0].x - sc[3].x), Mathf.Abs(sc[1].x - sc[2].x));
                bool upOnScreen = sc[3].y > sc[0].y && sc[1].x > sc[0].x;
                UprResult(name + " faces the camera (one card, level and upright on screen)", facing > 0.999f && level <= 1f && upright <= 1f && upOnScreen,
                    "normal . view " + facing.ToString("0.0000") + ", edges off level " + level.ToString("0.00") + " px, off upright " + upright.ToString("0.00")
                    + " px, screen " + sc[0].ToString("0.0") + ".." + sc[2].ToString("0.0") + ", yaw " + View3D.Yaw.ToString("0") + " pitch " + View3D.Pitch.ToString("0.0"));
                if (m == uprEmpty)
                {
                    bs = sc;
                    // wanted: height cells over the bar's cell, moved onto its stacked floor as anything drawn there
                    Vector3 want = new Vector3(uprBarCentre.x, uprHeight / Api.HeightSquash, uprBarCentre.z);
                    Floors.Drawn(ref want);
                    want.y *= Api.HeightSquash;
                    Vector3 mid = (c[0] + c[2]) * 0.5f;
                    bool at = Mathf.Abs(mid.y - want.y) < 0.05f && Mathf.Abs(mid.x - want.x) < 0.01f && Mathf.Abs(mid.z - want.z) < 0.01f;
                    UprResult("bar at the height over its cell" + (Floors.On ? " (on its floor: " + (want.y - uprHeight).ToString("0.00") + " up)" : ""), at,
                        "middle " + mid.ToString("0.000") + ", wanted " + want.ToString("0.000"));
                }
                else if (m == uprFill)
                {
                    fs = sc;
                }
                else
                {
                    ics = sc;
                }
            }
            float pxPerCell = (bs[1].x - bs[0].x) / (UprBarW + UprMargin);
            float wantLeft = bs[0].x + UprMargin * 0.5f * pxPerCell, wantRight = wantLeft + UprBarW * UprFillPct * pxPerCell;
            float barMidY = (bs[0].y + bs[3].y) * 0.5f, fillMidY = (fs[0].y + fs[3].y) * 0.5f;
            bool fillOk = Mathf.Abs(fs[0].x - wantLeft) < 1.5f && Mathf.Abs(fs[1].x - wantRight) < 1.5f && Mathf.Abs(fillMidY - barMidY) < 1.5f;
            UprResult("fill inside the bar from its left end (60 %)", fillOk, "fill x " + fs[0].x.ToString("0.0") + ".." + fs[1].x.ToString("0.0") + ", wanted " + wantLeft.ToString("0.0") + ".." + wantRight.ToString("0.0")
                + ", mid y " + fillMidY.ToString("0.0") + " vs bar " + barMidY.ToString("0.0"));
            float iconMidX = (ics[0].x + ics[1].x) * 0.5f, barMidX = (bs[0].x + bs[1].x) * 0.5f, iconMidY = (ics[0].y + ics[3].y) * 0.5f;
            float wantUp = UprIconNorth * pxPerCell;
            bool iconOk = Mathf.Abs(iconMidX - barMidX) < 1.5f && Mathf.Abs(iconMidY - barMidY - wantUp) < 1.5f;
            UprResult("icon over the bar (north = up the card)", iconOk, "icon mid " + iconMidX.ToString("0.0") + "," + iconMidY.ToString("0.0") + ", bar mid " + barMidX.ToString("0.0") + "," + barMidY.ToString("0.0")
                + ", wanted " + wantUp.ToString("0.0") + " px up");
        }
    }
}
