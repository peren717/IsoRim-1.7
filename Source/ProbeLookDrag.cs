using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness probe for the free look drag modes (View3D.LookHeld, IsoRimSettings.lookMode 0/1/2): a real middle-button drag,
    // the input faked where the game and IsoRim read it (Input.mousePosition, Input.GetMouseButton(2), and the game's own
    // input wrapper UnityGUIBugsFixer.MouseDrag(2) / CurrentEventDelta, which its map drag reads from the GUI events); the
    // game's camera driver and IsoRim's own per-frame code do the rest. The probe reads whether the view turned (free look
    // yaw or pitch) and whether the map moved (the camera driver's root).
    //   ldrag=mode,view,tag,turn,move   lookMode 0|1|2, view 3d|flat, expected turn 1|0 and map move 1|0
    //   ldcheck=1                       after the jobs: "ldragcheck: total N PASS, M FAIL"
    // Each job: the camera root, the free look yaw and pitch and the view as they were before it are put back after.
    public static partial class Probe
    {
        private class LdJob
        {
            public int mode;
            public bool flat;
            public string tag;
            public bool turn, move;
            public int frame = -1, phase, phaseFrame;
            public int modeWas;
            public bool freeLookWas;
            public float yawWas, isoPitchWas, pitchWas;
            public Vector3 root0;
            public float yaw0, pitch0;
            public Vector2 from;
        }

        private static readonly List<LdJob> ldJobs = new List<LdJob>();
        private static bool ldTotals, ldHooked, ldButton;
        private static Vector2 ldDelta;
        private static int ldDeltaFrame = -1;
        private static int ldPass, ldFail;
        private static readonly AccessTools.FieldRef<CameraDriver, Vector3> ldRoot = AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");

        private const int LdSettle = 20;      // frames before the drag (the flat view: after the switch)
        private const int LdDrag = 24;        // frames the middle button is held, the pointer moving
        private const int LdAfter = 12;       // frames after letting go
        private static readonly Vector2 LdMove = new Vector2(240f, 80f);   // pixels the pointer moves during the drag

        private static void LdKey(string key, string[] a)
        {
            if (key == "ldcheck")
            {
                ldTotals = a[0] == "1";
                // the mode 2 tooltip as the settings page shows it (the game's own map move keys)
                string keys = (string)AccessTools.Method(typeof(SettingsWindow), "DollyKeysLabel").Invoke(null, null);
                Log.Message("[IsoRim probe] ldrag tooltip: " + "R3D_Settings_LookMiddleOnlyTip".Translate(keys) + " | keys '" + keys + "'");
                return;
            }
            HookMouse();
            LdHook();
            ldJobs.Add(new LdJob { mode = I(a[0]), flat = a[1] == "flat", tag = a[2], turn = a[3] == "1", move = a[4] == "1" });
        }

        private static void LdHook()
        {
            if (ldHooked)
            {
                return;
            }
            ldHooked = true;
            Harmony h = new Harmony("peren.isorim17.probe.ldrag");
            h.Patch(AccessTools.Method(typeof(Input), "GetMouseButton", new[] { typeof(int) }), postfix: new HarmonyMethod(typeof(Probe), nameof(LdButton)));
            h.Patch(AccessTools.Method(typeof(UnityGUIBugsFixer), "MouseDrag"), postfix: new HarmonyMethod(typeof(Probe), nameof(LdButton)));
            h.Patch(AccessTools.PropertyGetter(typeof(UnityGUIBugsFixer), "CurrentEventDelta"), postfix: new HarmonyMethod(typeof(Probe), nameof(LdEventDelta)));
            Log.Message("[IsoRim probe] ldrag: middle button hooks on Input.GetMouseButton, UnityGUIBugsFixer.MouseDrag and CurrentEventDelta");
        }

        public static void LdButton(int __0, ref bool __result)
        {
            if (ldButton && __0 == 2)
            {
                __result = true;
            }
        }

        // The pointer's move this frame, handed to the game's drag once a frame (the GUI runs several events a frame).
        public static void LdEventDelta(ref Vector2 __result)
        {
            if (!ldButton)
            {
                return;
            }
            __result = ldDeltaFrame == Time.frameCount ? Vector2.zero : ldDelta;
            ldDeltaFrame = Time.frameCount;
        }

        // Every frame (from Sticky).
        private static void LdTick(Map map)
        {
            if (ldJobs.Count == 0)
            {
                return;
            }
            LdJob j = ldJobs[0];
            HoldHarness();
            IsoRimSettings st = IsoRimMod.Settings;
            j.frame++;
            if (j.frame == 0)
            {
                CloseWindows();
                Find.Selector.ClearSelection();
                Find.DesignatorManager.Deselect();
                j.modeWas = st.lookMode;
                j.freeLookWas = st.freeLook;
                j.yawWas = st.freeYaw;
                j.isoPitchWas = st.isoPitch;
                j.pitchWas = st.pitch;
                j.root0 = ldRoot(Find.CameraDriver);
                st.lookMode = j.mode;
                st.freeLook = true;
                View3D.Active = !j.flat;
                return;
            }
            if (j.phase == 0)
            {
                bool ready = j.flat ? !View3D.Ready : View3D.Ready && !View3D.Turning;
                j.phaseFrame = ready ? j.phaseFrame + 1 : 0;
                if (j.phaseFrame < LdSettle && j.frame < 2000)
                {
                    return;
                }
                j.from = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                fakeMouse = j.from;
                j.root0 = ldRoot(Find.CameraDriver);
                j.yaw0 = st.freeYaw;
                j.pitch0 = st.isometric ? st.isoPitch : st.pitch;
                j.phase = 1;
                j.phaseFrame = 0;
                return;
            }
            if (j.phase == 1)
            {
                // the middle button held, the pointer moving LdMove over LdDrag frames
                j.phaseFrame++;
                ldButton = true;
                Vector2 was = fakeMouse ?? j.from;
                Vector2 now = j.from + LdMove * Mathf.Min(1f, j.phaseFrame / (float)(LdDrag - 4));
                fakeMouse = now;
                ldDelta = now - was;
                if (j.phaseFrame >= LdDrag)
                {
                    ldButton = false;
                    ldDelta = Vector2.zero;
                    j.phase = 2;
                    j.phaseFrame = 0;
                }
                return;
            }
            j.phaseFrame++;
            if (j.phaseFrame < LdAfter)
            {
                return;
            }
            Vector3 root = ldRoot(Find.CameraDriver);
            float dYaw = Mathf.Abs(Mathf.DeltaAngle(j.yaw0, st.freeYaw));
            float dPitch = Mathf.Abs((st.isometric ? st.isoPitch : st.pitch) - j.pitch0);
            float dRoot = new Vector2(root.x - j.root0.x, root.z - j.root0.z).magnitude;
            bool turned = dYaw > 1f || dPitch > 0.5f, moved = dRoot > 0.5f;
            string state = "lookMode " + j.mode + ", " + (j.flat ? "flat" : "3D") + " view, yaw turned " + dYaw.ToString("0.0", CultureInfo.InvariantCulture)
                + " deg, pitch " + dPitch.ToString("0.0", CultureInfo.InvariantCulture) + " deg, map moved " + dRoot.ToString("0.00", CultureInfo.InvariantCulture) + " cells";
            LdCheck(j.tag + (j.turn ? " turns" : " does not turn"), turned == j.turn, state);
            LdCheck(j.tag + (j.move ? " moves the map" : " does not move the map"), moved == j.move, state);
            // put everything back
            fakeMouse = null;
            st.lookMode = j.modeWas;
            st.freeLook = j.freeLookWas;
            st.freeYaw = j.yawWas;
            st.isoPitch = j.isoPitchWas;
            st.pitch = j.pitchWas;
            View3D.Active = true;
            Find.CameraDriver.SetRootPosAndSize(j.root0, Find.CameraDriver.RootSize);
            ldJobs.RemoveAt(0);
            if (ldJobs.Count == 0 && ldTotals)
            {
                Log.Message("[IsoRim probe] ldragcheck: total " + ldPass + " PASS, " + ldFail + " FAIL");
            }
        }

        private static void LdCheck(string what, bool ok, string detail)
        {
            if (ok)
            {
                ldPass++;
            }
            else
            {
                ldFail++;
            }
            Log.Message("[IsoRim probe] CHECK " + (ok ? "PASS" : "FAIL") + " ldrag " + what + ": " + detail);
        }
    }
}
