using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Camera state and the maths shared by every other part of the mod.
    //
    // The game draws everything as flat quads on the ground and uses height (Y) only to decide
    // what is drawn on top of what. Seen from a tilted camera those layers would drift apart, so
    // the camera's view matrix multiplies Y by Squash. Anything that should really stand up has
    // its height divided by Squash to cancel that out. "Apparent space" below means the world
    // after squashing, which is what ends up on screen.
    public static class View3D
    {
        // Free look (IsoRimSettings.freeLook) opens up the range of camera angles.
        public static float MinPitch
        {
            get { return IsoRimMod.Settings.freeLook ? 5f : 30f; }
        }
        public static float MaxPitch
        {
            get { return IsoRimMod.Settings.freeLook ? 89f : 85f; }
        }
        public const float IsoYaw = 45f;   // how far the isometric camera is turned from north, clockwise seen from above

        // How far the lighting overlay is pulled toward the camera, and how much depth is kept
        // free either side of the visible ground.
        private const float OverlayPull = 30f;
        private const float DepthPad = 45f;

        public static bool Active;

        // Set by a gameplay addon that needs the 3D view (IsoRim ProjectZ): the view starts on and the 0 key, the
        // corner button and the options button no longer turn it off. LockedBy names the addon in the tooltip.
        public static bool LockedOn;
        public static string LockedBy = "another mod";

        // Set by a gameplay addon whose rules depend on the wall height (IsoRim ProjectZ: storeys stand on the walls):
        // the wall height is fixed at this value (cells) and the option is greyed out. 0 = the option decides.
        public static float LockedWallHeight;

        // Set by a gameplay addon whose world needs the fog lid on or off (Api.LockFogLid): the lid follows LockedFogLid
        // instead of the player's option, which is kept as it is (it is back when the addon unlocks), and the option is
        // greyed out with FogLidLockTip as its tooltip.
        public static bool FogLidLocked;
        public static bool LockedFogLid;
        public static string FogLidLockTip;

        // The fog lid is drawn: the option, or the locked value. Every reader of the fog lid option goes through this.
        public static bool FogLid
        {
            get { return FogLidLocked ? LockedFogLid : IsoRimMod.Settings.fogLid; }
        }

        // Changes whenever a setting that affects the shape of things changes.
        public static int Stamp;

        public static Matrix4x4 LightingMatrix = Matrix4x4.identity;

        // Outline shells of moving models: how far (in cells) they are pushed straight away from the
        // camera (R3DMaterials.OutlineMoving, IsoRim.vert.glsl).
        public const float OutlinePush = 0.20f;

        private static bool haveFrame;
        private static Vector3 eye;
        private static readonly int isoEyeId = Shader.PropertyToID("_IsoEye");
        private static Vector3 fwd = Vector3.down;
        private static Vector3 up = Vector3.forward;
        private static Vector3 right = Vector3.right;
        private static float orthoSize = 20f;
        private static float aspect = 1.6f;
        private static bool persp;            // this frame uses a perspective camera (IsoRimSettings.perspective)
        private static float tanHalf = 0.36f; // tan of half the vertical field of view
        private static float camDist = 50f;   // from the eye to the focus point on the ground

        // A perspective camera (Rim3D-like) instead of the orthographic one. The game's zoom
        // (orthographicSize) sets the distance, so the ground at the centre of the screen keeps its scale.
        public static bool Perspective
        {
            get { return persp; }
        }

        // The vertical field of view (degrees) the perspective camera used this frame (Api 20, Api.FieldOfView).
        public static float FieldOfView { get; private set; } = 40f;

        private static Camera[] cams;
        private static bool camerasOverridden;

        private static bool lastActive;
        private static int appliedSig;
        private static int pendingSig;
        private static float pendingSince;

        private static readonly MethodInfo regenMethod = AccessTools.Method(typeof(MapDrawer), "RegenerateEverythingNow");

        public static bool Ready
        {
            get { return Active && haveFrame; }
        }

        // While a gravship cutscene runs, the game takes the camera from the player (Game.PlayerHasControl) unless its
        // free-cam config is set; IsoRim's own view keys and drags are locked with it (ShipFlight: the turn, tilt and 3D
        // view stay as they were when it started).
        public static bool ControlsLocked
        {
            get
            {
                return WorldComponent_GravshipController.CutsceneInProgress && Find.CameraDriver != null && !Find.CameraDriver.config.gravshipFreeCam;
            }
        }

        // The camera this frame (true space: height in cells): where the eye is and which way it looks.
        public static Vector3 EyePosition
        {
            get { return eye; }
        }

        public static Vector3 Forward
        {
            get { return fwd; }
        }

        public static Vector3 Right
        {
            get { return right; }
        }

        public static Vector3 Up
        {
            get { return up; }
        }

        // How far a point (true space) is in front of the eye, along the line of sight.
        public static float DepthOf(Vector3 p)
        {
            return Vector3.Dot(p - eye, fwd);
        }

        // A depth just past the near plane: a screen card there is in front of everything.
        public static float NearCard
        {
            get { return nearNow * 1.5f + 0.05f; }
        }

        // Where a point (true space) shows in the viewport, 0-1 from the bottom left (as Camera.WorldToViewportPoint).
        public static Vector2 WorldToViewport(Vector3 p)
        {
            Vector2 px = WorldToScreen(p);
            return new Vector2(px.x / Mathf.Max(1, Screen.width), px.y / Mathf.Max(1, Screen.height));
        }

        // A square plane (MeshPool.plane10) stood square to the camera at that depth and stretched to fill the screen
        // exactly, its texture x to the right and y up the screen: for the game's screen-space effects, which draw on a
        // plane the size of the screen (GravshipRenderer.DrawLayer) and read their position from its texture coordinates.
        // Drawn space (the height squashed).
        public static Matrix4x4 ScreenCard(float depth)
        {
            depth = Mathf.Max(depth, nearNow * 1.2f);
            float half = persp ? depth * tanHalf : orthoSize;
            Matrix4x4 t = Matrix4x4.TRS(eye + fwd * depth, Quaternion.LookRotation(up, -fwd), new Vector3(2f * half * aspect, 1f, 2f * half));
            return Matrix4x4.Scale(new Vector3(1f, 1f / Squash, 1f)) * t;
        }

        private static float nearNow = 0.5f;
        // How high the camera stands over the ground (cells): the eye in perspective and first person; for the flat camera
        // the height a 40-degree perspective camera would have at the same zoom and pitch (Silhouettes3D's trigger).
        public static float Altitude { get; private set; }
        public const float DefaultTanHalf = 0.36397f;   // tan(20 deg)

        // How steeply the camera looks down, in degrees: 90 is straight down. An external camera pose (Api 66, ViewPoses)
        // wins over first person and the player's camera while it is pushed.
        public static float Pitch
        {
            get
            {
                if (photo)
                {
                    return photoFrame.pitch;   // (Api 76) a photo's draws are being made from its own camera
                }
                if (ViewPoses.Active)
                {
                    return ViewPoses.Pose.Pitch;
                }
                if (FirstPerson.On)
                {
                    return FirstPerson.Pitch;
                }
                IsoRimSettings st = IsoRimMod.Settings;
                return Mathf.Clamp(st.isometric ? st.isoPitch : st.pitch, MinPitch, MaxPitch);
            }
        }

        // Which way the camera faces right now, in degrees clockwise from north seen from above.
        // 0 is the game's usual "north is up"; the isometric view starts out looking north-east.
        // It glides to YawTarget when the view is turned.
        public static float Yaw
        {
            get { return photo ? photoFrame.yaw : ViewPoses.Active ? ViewPoses.Pose.Yaw : yawNow; }
        }

        // Where the camera is turning to: the base direction plus the eighth turns asked for.
        public static float YawTarget
        {
            get { return Mathf.Repeat(BaseYaw + 45f * Step + IsoRimMod.Settings.freeYaw, 360f); }
        }

        // The direction the view starts from before any turn: north-east when isometric, north otherwise.
        private static float BaseYaw
        {
            get { return IsoRimMod.Settings.isometric ? IsoYaw : 0f; }
        }

        // The eighth of a turn the camera is nearest to now (0 to 7, 0 = north). What is built for
        // the camera's direction (map edge, cut walls) is redrawn when this changes.
        public static int Facing
        {
            get { return Mathf.RoundToInt(Mathf.Repeat(Yaw, 360f) / 45f) % 8; }
        }

        // How many eighth turns clockwise the view has been turned from its starting direction (0 to 7).
        // With 90 degree snaps this only ever takes even values.
        public static int Step
        {
            get { return ((IsoRimMod.Settings.turn % 8) + 8) % 8; }
        }

        // True while the camera is still swinging toward YawTarget, and while an external pose holds the view (Api 66): what
        // is built for the camera's direction is rebuilt once the player's camera is back.
        public static bool Turning
        {
            get { return ViewPoses.CountsAsTurning || lookDragging || FirstPerson.On || yawSet && Mathf.Abs(Mathf.DeltaAngle(yawNow, YawTarget)) > 0.05f; }
        }

        private static float yawNow;
        private static bool yawSet;
        private const float TurnSpeed = 300f;   // degrees a second: an eighth turn takes about a sixth of a second

        private static void TurnCamera()
        {
            if (FirstPerson.On)
            {
                yawNow = FirstPerson.Yaw;
                yawSet = true;
                return;
            }
            float target = YawTarget;
            if (!yawSet || !Active || lookDragging || IsoRimMod.Settings.instantTurn)
            {
                // While the view is dragged round with the mouse it follows at once, with no easing; with instantTurn every turn does.
                yawNow = target;
                yawSet = true;
            }
            else
            {
                // Eased: fast in the middle of the swing, gentle at the end, so a turn reads as smooth.
                float left = Mathf.Abs(Mathf.DeltaAngle(yawNow, target));
                float speed = TurnSpeed * Mathf.Clamp(left / 30f, 0.25f, 1.6f);
                yawNow = Mathf.Repeat(Mathf.MoveTowardsAngle(yawNow, target, speed * Time.unscaledDeltaTime), 360f);
            }
            // The light no longer follows the camera: the sun stays put in the world while the
            // camera turns round it (see SunStep), so turning never redraws anything.
        }

        // The direction along the ground that runs up the screen, away from the camera.
        public static Vector3 GroundForward
        {
            get
            {
                float y = Yaw * Mathf.Deg2Rad;
                return new Vector3(Mathf.Sin(y), 0f, Mathf.Cos(y));
            }
        }

        public static float Squash
        {
            // Kept very small: the game spaces its flat drawing layers far apart in height, and
            // whatever height is left after squashing shows as overlays (zone and drag
            // previews, field edges) floating off the ground.
            get { return Mathf.Clamp(IsoRimMod.Settings.squash, 0.002f, 0.004f); }
        }

        // The direction "up the sprite" points for anything stood up to face the camera.
        public static Vector3 CardUp
        {
            get
            {
                float p = Pitch * Mathf.Deg2Rad;
                float y = Yaw * Mathf.Deg2Rad;
                return new Vector3(Mathf.Sin(p) * Mathf.Sin(y), Mathf.Cos(p), Mathf.Sin(p) * Mathf.Cos(y));
            }
        }

        // How much of a label to show for something at a map position (1 = all, 0 = none). Only with
        // perspective: labels keep their size on screen while what they belong to shrinks, so beyond
        // labelFade times the distance to the screen centre they fade out over the next 40 %.
        // Whether a map position is in front of the perspective camera. Behind it, the projection
        // mirrors points up into the sky, so labels for them must not be drawn. Always true orthographic.
        // Where a thing is drawn, for the label checks below: its game position moved onto its stacked floor (a ProjectZ
        // floor's rows lie far north of the ground in the game's space, so the game position would read as far away or
        // behind the camera and the label would fade out or vanish when zoomed in or turned).
        public static Vector3 LabelPos(Thing t)
        {
            return t.DrawPos + Floors.DrawnOffset(t);
        }

        public static bool InFront(Vector3 pos)
        {
            if (!persp)
            {
                return true;
            }
            Vector3 d = new Vector3(pos.x, 0f, pos.z) - eye;
            return Vector3.Dot(d, fwd) > 0.2f;
        }

        public static float LabelAlpha(Vector3 pos)
        {
            float fade = IsoRimMod.Settings.labelFade;
            if (!persp || fade >= 4f)
            {
                return 1f;
            }
            float d = Vector3.Distance(eye, new Vector3(pos.x, 0f, pos.z)) / Mathf.Max(1f, camDist);
            return Mathf.Clamp01(1f - (d - fade) / (fade * 0.4f));
        }

        // How far past the focus point the perspective camera shows the map, in cells.
        public static float ViewDistance
        {
            get { return Mathf.Clamp(IsoRimMod.Settings.viewDistance, 40f, 400f); }
        }

        public static float WallHeight
        {
            get { return Mathf.Clamp(LockedWallHeight > 0f ? LockedWallHeight : IsoRimMod.Settings.wallHeight, 0.5f, 4f); }
        }

        // Called once per frame from the map camera's update.
        public static void Tick()
        {
            Probe.Begin();
            try
            {
                SpritePixels.Tick();
                Probe.Lap(0);
                TickInner();
            }
            finally
            {
                Probe.End();
            }
        }

        private static void TickInner()
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null)
            {
                return;
            }
            // Like the game's own camera update: nothing while a long event (loading a save, making
            // a map) runs, as that work changes the maps on another thread. The game draws the maps
            // when the event ends, and the state check below then rebuilds them for 3D if needed.
            if (LongEventHandler.ShouldWaitForEvent)
            {
                return;
            }

            HandleKeys();
            FirstPerson.Update();
            ViewPoses.RaiseBefore();   // (Api 66) BeforeViewUpdate: another mod's camera reads its input here
            ViewPoses.Resolve();
            TurnCamera();

            Camera main = Find.Camera;
            if (main == null)
            {
                return;
            }

            // Switching on rebuilds the whole map for 3D (a second or more on a big base). That runs in a
            // short long event with a message ("Preparing 3D…"); the flat view stays until it runs.
            if (Active && !lastActive)
            {
                if (!switchQueued)
                {
                    switchQueued = true;
                    LongEventHandler.QueueLongEvent(SwitchOnNow, "R3D_Preparing3D", false, null);
                }
                if (camerasOverridden)
                {
                    ResetCamera();
                }
                return;
            }

            if (Active)
            {
                ApplyCamera(main);
                SunShadowsSS.Prepare(main);
                AmbientOcclusion.Draw(main);
                SunShadowsSS.Draw(main);
                TiltShift.Draw(main);
                AntiAlias.Draw(main);
                Grade.Draw(main);
            }
            else if (camerasOverridden)
            {
                ResetCamera();
            }
            SceneImage.Frame();   // (Api 66) a clean picture of the map view asked for: its late grab, its readback
            Photos.Frame();       // (Api 76) a photo drawn last frame: read back and handed over
            Floors.BeginFrame();
            FullWalls.Refresh();
            Probe.Lap(1);

            RegenerateIfStateChanged();
            RecachePawnsIfReadyChanged();
            FallbackLog.Frame();   // diagnostics: flips of the view and model settings, the fallback summary
            Probe.Lap(2);
            MapSections.Process(8);
            Probe.Lap(3);
            Ground.Sync();
            Aniso.Sync();
            Probe.Lap(4);
            Cutaway.Process();
            Probe.Lap(5);
            if (Ready)
            {
                Cutaway.TrackPointer();
                Probe.Lap(6);
                Beams.Update();   // (Api 33) flickering and pulsing beam parts
                DrawFrameParts();
                Silhouettes3D.DrawAll();
                CollapseDebris.Frame();
                Probe.Lap(7);
                try
                {
                    if (!SceneImage.SkipOverlays)   // (Api 66) a picture without map overlays is being taken
                    {
                        Interaction.DrawDesignationDrag();
                        Interaction.DrawDragGhosts();
                        MineXray.Draw();
                        TargetBox.Draw();
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Drawing the drag box or ghosts failed: " + e, 93611163);
                }
            }
        }

        private static void HandleKeys()
        {
            // Keys are ignored while a text box has focus, so typing a 0 does not flip the view.
            bool typing = GUIUtility.keyboardControl != 0;
            if (ControlsLocked)
            {
                lookDragging = false;   // a gravship cutscene: no turn, tilt, look, first person, 3D or isometric switch
                return;
            }

            if (!typing && R3DKeyDefOf.R3D_ToggleView != null && R3DKeyDefOf.R3D_ToggleView.JustPressed)
            {
                if (LockedOn)
                {
                    Messages.Message("R3D_Msg_AlwaysOnWith".Translate(LockedBy), MessageTypeDefOf.SilentInput, false);
                }
                else
                {
                    Active = !Active;
                    Messages.Message((Active ? "R3D_Msg_ViewOn" : "R3D_Msg_ViewOff").Translate(), MessageTypeDefOf.SilentInput, false);
                }
            }

            if (!typing && R3DKeyDefOf.R3D_Isometric != null && R3DKeyDefOf.R3D_Isometric.JustPressed)
            {
                // From the flat game this goes straight to isometric; after that it swaps
                // between isometric and the straight-on 3D view.
                IsoRimSettings st = IsoRimMod.Settings;
                st.isometric = !Active || !st.isometric;
                Active = true;
                Messages.Message((st.isometric ? "R3D_Msg_Isometric" : "R3D_Msg_StraightOn").Translate(), MessageTypeDefOf.SilentInput, false);
            }

            if (!typing && R3DKeyDefOf.R3D_FirstPerson != null && R3DKeyDefOf.R3D_FirstPerson.JustPressed)
            {
                FirstPerson.Toggle();
            }

            if (!Active || typing)
            {
                lookDragging = false;
                return;
            }

            if (ViewPoses.BlocksPlayer)
            {
                // (Api 66) Another mod's camera pose holds the view: the player's look, turn, tilt and reset keys wait, so the
                // player's camera is as it was when the pose goes.
                lookDragging = false;
                return;
            }

            HandleLook();
            if (R3DKeyDefOf.R3D_ResetCamera != null && R3DKeyDefOf.R3D_ResetCamera.JustPressed)
            {
                if (FirstPerson.On)
                {
                    FirstPerson.ResetLook();
                }
                else
                {
                    ResetLook();
                }
            }

            // Q and E turn the view an eighth or a quarter turn (see the options). The game uses the
            // same keys to turn a building that is being placed, so they are left alone while
            // something is selected to build.
            if (!FirstPerson.On && (Find.DesignatorManager == null || Find.DesignatorManager.SelectedDesignator == null))
            {
                IsoRimSettings turnSettings = IsoRimMod.Settings;
                if (R3DKeyDefOf.R3D_TurnLeft != null && R3DKeyDefOf.R3D_TurnLeft.JustPressed)
                {
                    SnapTurn(turnSettings, -1);
                }
                if (R3DKeyDefOf.R3D_TurnRight != null && R3DKeyDefOf.R3D_TurnRight.JustPressed)
                {
                    SnapTurn(turnSettings, 1);
                }
            }
            if (R3DKeyDefOf.R3D_ToggleRoofs != null && R3DKeyDefOf.R3D_ToggleRoofs.JustPressed)
            {
                Roofs.Toggle();
            }

            float dir = 0f;
            if (R3DKeyDefOf.R3D_TiltUp != null && R3DKeyDefOf.R3D_TiltUp.IsDown)
            {
                dir += 1f;
            }
            if (R3DKeyDefOf.R3D_TiltDown != null && R3DKeyDefOf.R3D_TiltDown.IsDown)
            {
                dir -= 1f;
            }
            if (dir != 0f && !FirstPerson.On)
            {
                IsoRimSettings s = IsoRimMod.Settings;
                float change = dir * 40f * Time.unscaledDeltaTime;
                if (s.isometric)
                {
                    s.isoPitch = Mathf.Clamp(s.isoPitch + change, MinPitch, MaxPitch);
                }
                else
                {
                    s.pitch = Mathf.Clamp(s.pitch + change, MinPitch, MaxPitch);
                }
            }
        }

        // ---------------------------------------------------------------- free look

        private static bool lookDragging;
        private static Vector3 lookLastMouse;

        // True while the view is being dragged round with the mouse (read by the map drag in
        // Patches, which then stays out of the way).
        public static bool LookDragging
        {
            get { return lookDragging; }
        }

        // Whether the mouse buttons and keys for free look are held now (see IsoRimSettings.lookMode):
        // 0 = the middle button with the look modifier (Ctrl by default, a key binding in the game's keyboard options; none
        // if unbound); 1 = right button while nothing is selected and no building is being placed. The drag button is a
        // mod setting, not a key binding: the game's keyboard options can't take a mouse button.
        public static bool LookHeld()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (FirstPerson.Engaged)
            {
                // Nothing to drag the map for in first person: the drag button alone looks round.
                return Input.GetMouseButton(st.lookMode == 1 ? 1 : 2);
            }
            if (!st.freeLook || !Active)
            {
                return false;
            }
            if (st.lookMode == 1)
            {
                return Input.GetMouseButton(1)
                    && (Find.DesignatorManager == null || Find.DesignatorManager.SelectedDesignator == null)
                    && (Find.Selector == null || Find.Selector.NumSelected == 0);
            }
            // The modifier is a key binding of its own (Ctrl by default: Alt is the game's cell inspector).
            return Input.GetMouseButton(2) && BindingHeld(R3DKeyDefOf.R3D_LookModifier, true);
        }

        // Whether a key binding is held now. Mouse buttons count too (the game's KeyBindingDef.IsDown only reads keys
        // on 1.7). A binding with no key at all counts as held when unboundHeld.
        public static bool BindingHeld(KeyBindingDef def, bool unboundHeld)
        {
            if (def == null || KeyPrefs.KeyPrefsData == null)
            {
                return false;
            }
            KeyCode a = KeyPrefs.KeyPrefsData.GetBoundKeyCode(def, KeyPrefs.BindingSlot.A);
            KeyCode b = KeyPrefs.KeyPrefsData.GetBoundKeyCode(def, KeyPrefs.BindingSlot.B);
            if (a == KeyCode.None && b == KeyCode.None)
            {
                return unboundHeld;
            }
            if (Find.WindowStack != null && Find.WindowStack.AnySearchWidgetFocused)
            {
                return false;
            }
            return KeyHeld(a) || KeyHeld(b);
        }

        private static bool KeyHeld(KeyCode k)
        {
            if (k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6)
            {
                return Input.GetMouseButton(k - KeyCode.Mouse0);
            }
            return k != KeyCode.None && Input.GetKey(k);
        }

        // Dragging turns the camera round (left and right) and tilts it (up and down). A drag only
        // starts with the pointer over the map, not over a window.
        private static void HandleLook()
        {
            if (!LookHeld())
            {
                lookDragging = false;
                return;
            }
            Vector3 mouse = Input.mousePosition;
            if (!lookDragging)
            {
                if (Find.WindowStack != null && Find.WindowStack.GetWindowAt(UI.MousePositionOnUIInverted) != null)
                {
                    return;
                }
                lookDragging = true;
                lookLastMouse = mouse;
                return;
            }
            Vector3 d = mouse - lookLastMouse;
            lookLastMouse = mouse;
            if (d.x == 0f && d.y == 0f)
            {
                return;
            }
            IsoRimSettings st = IsoRimMod.Settings;
            float sens = Mathf.Clamp(st.lookSensitivity, 0.02f, 2f);
            if (FirstPerson.On)
            {
                FirstPerson.Look(d.x * sens, (st.invertY ? 1f : -1f) * d.y * sens);
                return;
            }
            st.freeYaw = Mathf.Repeat(st.freeYaw + d.x * sens, 360f);
            float tilt = (st.invertY ? 1f : -1f) * d.y * sens;
            if (st.isometric)
            {
                st.isoPitch = Mathf.Clamp(st.isoPitch + tilt, MinPitch, MaxPitch);
            }
            else
            {
                st.pitch = Mathf.Clamp(st.pitch + tilt, MinPitch, MaxPitch);
            }
        }

        // Q and E after a free turn: the next eighth (or quarter) in that direction from where the
        // camera faces now, so the view never stays half way between steps.
        private static void SnapTurn(IsoRimSettings st, int dir)
        {
            float k = Mathf.Repeat(YawTarget - BaseYaw, 360f) / 45f;   // eighths turned toward, 0 to 8 (the target, so quick presses add up)
            int unit = st.snap45 ? 1 : 2;
            float q = k / unit;
            int next = dir > 0 ? Mathf.FloorToInt(q + 0.05f) + 1 : Mathf.CeilToInt(q - 0.05f) - 1;
            st.turn = ((next * unit) % 8 + 8) % 8;
            st.freeYaw = 0f;
        }

        // The reset key: the starting direction and angle.
        public static void ResetLook()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            st.freeYaw = 0f;
            st.turn = 0;
            if (st.isometric)
            {
                st.isoPitch = 35.3f;
            }
            else
            {
                st.pitch = 55f;
            }
            Messages.Message("R3D_Msg_CameraReset".Translate(), MessageTypeDefOf.SilentInput, false);
        }

        // One step left (-1) or right (+1) from an eighth-turn count. With quarter snaps an odd
        // count (left over from the eighth snaps) lands on the nearest quarter in that direction.
        public static int TurnBy(int turn, int dir, bool eighths)
        {
            turn = ((turn % 8) + 8) % 8;
            if (eighths)
            {
                return (turn + dir + 8) % 8;
            }
            if (dir > 0)
            {
                return ((turn / 2) + 1) * 2 % 8;
            }
            return (turn % 2 == 1 ? turn - 1 : turn - 2 + 8) % 8;
        }

        // The camera for this frame. The pose comes from one place: an external pose (Api 66, ViewPoses) if one is pushed,
        // else first person, else the player's camera (settings, turn, the game's zoom and ground point). Everything set
        // here (the matrices of the main and helper cameras, culling, view globals, the lighting overlay's pull, upright
        // cards, sky and fog) and every reader of these fields (pointer rays, labels, the view rect, weather, silhouettes...)
        // follows that one pose.
        private static void ApplyCamera(Camera main)
        {
            if (cams == null || cams.Length == 0 || cams[0] == null)
            {
                // The main camera plus the helper cameras the game hangs under it (water depth etc.),
                // which have to look from the same place or their effects stop lining up.
                cams = main.GetComponentsInChildren<Camera>(true);
            }

            float pitchDeg = Pitch;
            float s = Squash;

            // The game keeps the camera directly above the point at the centre of the screen.
            // The transform is left alone so everything that reads it keeps working.
            bool posed = ViewPoses.Active;
            bool fp = FirstPerson.On && !posed;
            ViewFrame f = Solve(posed ? ViewPoses.Pose : (ViewPose?)null, fp, pitchDeg, Yaw, main.orthographicSize, main.aspect, GameFocus(main));
            Install(f);

            for (int i = 0; i < cams.Length; i++)
            {
                Camera c = cams[i];
                if (c == null)
                {
                    continue;
                }
                float size = posed ? c.orthographicSize * f.sizeScale : c.orthographicSize;
                Matrix4x4 proj = f.Projection(size, c.aspect);
                c.worldToCameraMatrix = f.worldToCamera;
                c.projectionMatrix = proj;
                c.cullingMatrix = proj * f.worldToCamera;
            }
            camerasOverridden = true;
            haveFrame = true;
            Cull.SetCamera(main.projectionMatrix * main.worldToCameraMatrix, s);

            // The shaders work out face normals in true space and turn them toward the camera.
            R3DMaterials.SetView(-fwd, s);
            Shader.SetGlobalVector(isoEyeId, f.isoEye);   // moving outlines (IsoRim.vert.glsl)
            UprightCards.SetCamera(pitchDeg, Yaw);

            if (persp)
            {
                DrawSky(main, f);
            }
        }

        // The point the game's camera looks down on (the game keeps it straight above the middle of the screen).
        private static Vector3 GameFocus(Camera main)
        {
            Vector3 tp = main.transform.position;
            return new Vector3(tp.x, 0f, tp.z);
        }

        // The sky and the distance fog of a perspective frame (Sky.Draw), from the frame's basis.
        private static void DrawSky(Camera cam, ViewFrame f)
        {
            // The fog is solid a little before the reach of the pointer rays and of the drawn ground
            // (view depth is shorter than the distance along the ground toward the screen corners).
            float reach = f.camDist + ViewDistance;
            float fogEnd = reach * 0.92f;
            float fogStart = Mathf.Lerp(Mathf.Min(f.camDist * 0.8f, fogEnd * 0.5f), fogEnd, Mathf.Clamp(IsoRimMod.Settings.fogStart, 0f, 0.95f));
            Sky.Draw(cam, f.fwd, f.right, f.up, f.tanHalf * f.aspect, f.tanHalf, f.near, f.far, fogStart, fogEnd);
        }

        // One camera worked out from where it looks from: everything IsoRim derives from the pose (View3D.Solve). The shown
        // view's is installed into View3D's fields every frame (ApplyCamera); a photo's (Api 76, Photos) only while the
        // photo's draws are made.
        internal struct ViewFrame
        {
            public float pitch, yaw;
            public bool persp;
            public Vector3 eye, fwd, up, right;
            public float orthoSize, aspect, tanHalf, camDist, near, far, fov, altitude;
            public float sizeScale;          // an external pose's zoom over the game's (helper cameras keep their ratio)
            public Matrix4x4 worldToCamera;  // with the squash
            public Matrix4x4 lighting;       // LightingMatrix
            public Vector4 isoEye;           // _IsoEye: the eye in drawn space (w 1) for perspective, else zero

            public Matrix4x4 Projection(float size, float asp)
            {
                return persp ? Matrix4x4.Perspective(fov, asp, near, far) : Matrix4x4.Ortho(-size * asp, size * asp, -size, size, near, far);
            }
        }

        // The camera for a pose: an external pose (pose set), else first person (fp), else the player's camera, at this pitch
        // and yaw, with the game's zoom (gameOrtho: half the screen height in cells), the screen's aspect and the game camera's
        // ground point. The one place a pose becomes a camera: ApplyCamera (the shown view) and Photos (Api 76).
        internal static ViewFrame Solve(ViewPose? pose, bool fp, float pitchDeg, float yawDeg, float gameOrtho, float aspectNow, Vector3 focus)
        {
            ViewFrame f = new ViewFrame();
            float p = pitchDeg * Mathf.Deg2Rad;
            float s = Squash;
            f.pitch = pitchDeg;
            f.yaw = yawDeg;
            f.orthoSize = gameOrtho;
            f.aspect = aspectNow;
            f.sizeScale = 1f;
            // An orthographic camera has no field of view or eye distance of its own: it keeps the last ones (as the fields
            // always did; View3D.CamDistance and the label fade read them).
            f.tanHalf = tanHalf;
            f.camDist = camDist;

            Quaternion look = Quaternion.Euler(pitchDeg, yawDeg, 0f);
            f.fwd = look * Vector3.forward;
            f.up = look * Vector3.up;
            f.right = look * Vector3.right;
            Vector3 fwdNow = f.fwd;

            IsoRimSettings st = IsoRimMod.Settings;
            float fovNow;
            if (pose.HasValue)
            {
                ViewPose v = pose.Value;
                f.persp = v.Perspective;
                fovNow = v.Fov > 0f ? v.Fov : Mathf.Clamp(st.fov, 15f, 100f);
                if (v.OrthoSize > 0f)
                {
                    f.sizeScale = v.OrthoSize / Mathf.Max(0.01f, f.orthoSize);
                    f.orthoSize = v.OrthoSize;
                }
                if (v.Focus.HasValue)
                {
                    focus = new Vector3(v.Focus.Value.x, 0f, v.Focus.Value.z);
                }
                if (f.persp)
                {
                    f.tanHalf = Mathf.Tan(fovNow * 0.5f * Mathf.Deg2Rad);
                    if (v.Eye.HasValue)
                    {
                        // A free eye: "how far the view looks" (label fading, pointer reach, fog) is the distance to the ground
                        // at the middle of the screen, within 10-150 cells, unless the pose says.
                        f.eye = v.Eye.Value;
                        f.camDist = v.FocusDistance > 0f ? v.FocusDistance : (fwdNow.y < -0.01f ? Mathf.Clamp(f.eye.y / -fwdNow.y, 10f, 150f) : 150f);
                        f.near = v.Near > 0f ? v.Near : Mathf.Clamp(0.05f + f.eye.y * 0.01f, 0.08f, 1f);
                    }
                    else
                    {
                        // Swung round the focus point as the player's perspective camera is, at the pose's zoom.
                        f.camDist = v.FocusDistance > 0f ? v.FocusDistance : f.orthoSize / f.tanHalf;
                        f.eye = focus - fwdNow * f.camDist;
                        f.near = v.Near > 0f ? v.Near : Mathf.Clamp(f.camDist * 0.02f, 0.3f, 2f);
                    }
                    f.far = v.Far > 0f ? v.Far : f.camDist + ViewDistance + 2f * DepthPad;
                }
                else
                {
                    if (v.Eye.HasValue && !v.Focus.HasValue)
                    {
                        // Orthographic: only the line of sight matters; the view is centred where the eye's line meets the ground.
                        Vector3 e = v.Eye.Value;
                        focus = fwdNow.y < -0.01f ? new Vector3(e.x - fwdNow.x * e.y / fwdNow.y, 0f, e.z - fwdNow.z * e.y / fwdNow.y) : new Vector3(e.x, 0f, e.z);
                    }
                    float half = f.orthoSize / Mathf.Tan(p) + DepthPad;
                    f.eye = focus - fwdNow * (half + 5f);
                    f.near = v.Near > 0f ? v.Near : 0.5f;
                    f.far = v.Far > 0f ? v.Far : 2f * half + 10f;
                }
            }
            else
            {
                f.persp = st.perspective || fp;
                fovNow = Mathf.Clamp(fp ? st.fpFov : st.fov, 15f, 100f);
                if (fp)
                {
                    // In the colonist's head. camDist stands for "near things" (label fading, ray reach).
                    f.tanHalf = Mathf.Tan(fovNow * 0.5f * Mathf.Deg2Rad);
                    f.camDist = 10f;
                    f.eye = FirstPerson.Eye;
                    f.near = 0.08f;
                    f.far = ViewDistance + 2f * DepthPad;
                }
                else if (f.persp)
                {
                    float fov = fovNow;
                    f.tanHalf = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
                    f.camDist = f.orthoSize / f.tanHalf;
                    f.eye = focus - fwdNow * f.camDist;
                    f.near = Mathf.Clamp(f.camDist * 0.02f, 0.3f, 2f);
                    f.far = f.camDist + ViewDistance + 2f * DepthPad;
                }
                else
                {
                    float half = f.orthoSize / Mathf.Tan(p) + DepthPad;
                    f.eye = focus - fwdNow * (half + 5f);
                    f.near = 0.5f;
                    f.far = 2f * half + 10f;
                }
            }
            f.fov = fovNow;
            f.altitude = f.persp ? f.eye.y : f.orthoSize / DefaultTanHalf * Mathf.Sin(p);
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f))
                * Matrix4x4.TRS(f.eye, look, Vector3.one).inverse;
            f.worldToCamera = view * Matrix4x4.Scale(new Vector3(1f, s, 1f));

            // Moving something straight toward an orthographic camera does not move it on screen,
            // it only puts it in front. That is how the lighting overlay gets to darken upright things.
            // With perspective the same is a shrink toward the eye (drawn space: the eye's height unsquashed).
            if (f.persp)
            {
                float k = Mathf.Clamp(1f - OverlayPull / f.camDist, 0.3f, 0.9f);
                Vector3 eyeDrawn = new Vector3(f.eye.x, f.eye.y / s, f.eye.z);
                f.isoEye = new Vector4(eyeDrawn.x, eyeDrawn.y, eyeDrawn.z, 1f);
                f.lighting = Matrix4x4.Translate(eyeDrawn) * Matrix4x4.Scale(new Vector3(k, k, k)) * Matrix4x4.Translate(-eyeDrawn);
            }
            else
            {
                f.isoEye = Vector4.zero;
                f.lighting = Matrix4x4.Translate(new Vector3(-fwdNow.x * OverlayPull, -fwdNow.y * OverlayPull / s, -fwdNow.z * OverlayPull));
            }
            return f;
        }

        // The frame's camera into the fields every reader of the view uses (pointer rays, labels, the view rect, culling
        // boxes, upright sprites, the lighting overlay's pull...).
        private static void Install(ViewFrame f)
        {
            eye = f.eye;
            fwd = f.fwd;
            up = f.up;
            right = f.right;
            orthoSize = f.orthoSize;
            aspect = f.aspect;
            persp = f.persp;
            tanHalf = f.tanHalf;
            camDist = f.camDist;
            nearNow = f.near;
            farNow = f.far;
            FieldOfView = f.fov;
            Altitude = f.altitude;
            LightingMatrix = f.lighting;
        }

        // The fields as installed now (the shown view's frame outside a photo's draws).
        private static ViewFrame Installed()
        {
            return new ViewFrame
            {
                pitch = Pitch, yaw = Yaw, persp = persp, eye = eye, fwd = fwd, up = up, right = right, orthoSize = orthoSize, aspect = aspect,
                tanHalf = tanHalf, camDist = camDist, near = nearNow, far = farNow, fov = FieldOfView, altitude = Altitude, lighting = LightingMatrix
            };
        }

        // ---- a photo's camera (Api 76, Photos): installed only while the photo's own draws are made, the shown view's
        // frame put back right after, in the same frame. No shader global and no camera of the shown view is touched.

        private static bool photo;
        private static ViewFrame photoFrame;
        private static ViewFrame shownFrame;

        // A photo's camera is installed (its draws are being made).
        internal static bool InPhoto
        {
            get { return photo; }
        }

        // The camera for a photo pose (cleaned as a pushed pose is: ViewPoses.Clean) at this aspect, with the game's zoom and
        // ground point for what the pose leaves at 0.
        internal static ViewFrame PhotoFrame(ViewPose pose, float aspectNow)
        {
            Camera main = Find.Camera;
            return Solve(pose, false, pose.Pitch, pose.Yaw, main.orthographicSize, aspectNow, GameFocus(main));
        }

        internal static void BeginPhoto(ViewFrame f)
        {
            shownFrame = Installed();
            photoFrame = f;
            photo = true;
            Install(f);
            Cull.SetCamera(f.Projection(f.orthoSize, f.aspect) * f.worldToCamera, Squash);
        }

        internal static void EndPhoto()
        {
            if (!photo)
            {
                return;
            }
            photo = false;
            Install(shownFrame);
            Camera main = Find.Camera;
            if (main != null)
            {
                Cull.SetCamera(main.projectionMatrix * main.worldToCameraMatrix, Squash);
            }
        }

        // IsoRim's own parts drawn every frame on top of the game's things (live flames, turning and bobbing parts, pulses,
        // activity glows, glass, work panels): for the shown view (TickInner) and for a photo's draws (Photos). Each is
        // culled by the game's view rect (the installed camera's) and moves by game ticks, so drawing it twice in a frame
        // changes nothing.
        internal static void DrawFrameParts()
        {
            Effects.DrawAll();
            Spinners.DrawAll();
            Bobbers.DrawAll();
            Pulses.DrawAll();
            ActivityGlow.DrawAll();
            Glass.DrawAll();
            WorkPanels.DrawAll();
        }

        // The per-view shader values of a frame, which the shown view sets as globals (ApplyCamera: R3DMaterials.SetView,
        // _IsoEye, UprightCards.SetCamera): into a photo's property block, so they reach only its draws (Api 76).
        internal static void ViewValues(ViewFrame f, MaterialPropertyBlock into)
        {
            into.SetVector(R3DMaterials.ViewId, R3DMaterials.ViewVector(-f.fwd, Squash));
            into.SetVector(isoEyeId, f.isoEye);
            into.SetVector(UprightCards.CardId, UprightCards.CardVector(f.pitch, f.yaw));
        }

        // The sky of the photo's frame (perspective photos), its values handed over per draw (Sky.Draw under Photos).
        internal static void DrawPhotoSky(Camera cam)
        {
            if (photo && photoFrame.persp)
            {
                DrawSky(cam, photoFrame);
            }
        }

        private static float farNow = 100f;

        // (Api 66) The camera this frame as a pose (Api.CurrentViewPose).
        public static ViewPose PoseNow()
        {
            return new ViewPose
            {
                Yaw = Yaw,
                Pitch = Pitch,
                Perspective = persp,
                Eye = eye,
                Focus = persp ? (Vector3?)null : ScreenToGround(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f)),
                Fov = FieldOfView,
                OrthoSize = orthoSize,
                FocusDistance = persp ? camDist : 0f,
                Near = nearNow,
                Far = farNow
            };
        }

        // (Api 66) The player's own camera turned and tilted to these angles (null keeps one): the settings the player's keys
        // and drags write (freeYaw on top of the base direction and eighth turns; the pitch of the current mode, within its
        // range). snap: no swing to the new yaw.
        public static void SetPlayerView(float? yaw, float? pitch, bool snap)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (yaw.HasValue && !float.IsNaN(yaw.Value) && !float.IsInfinity(yaw.Value))
            {
                st.freeYaw = Mathf.Repeat(yaw.Value - BaseYaw - 45f * Step, 360f);
                if (snap)
                {
                    yawNow = YawTarget;
                    yawSet = true;
                }
            }
            if (pitch.HasValue && !float.IsNaN(pitch.Value) && !float.IsInfinity(pitch.Value))
            {
                float clamped = Mathf.Clamp(pitch.Value, MinPitch, MaxPitch);
                if (st.isometric)
                {
                    st.isoPitch = clamped;
                }
                else
                {
                    st.pitch = clamped;
                }
            }
        }

        private static void ResetCamera()
        {
            if (cams != null)
            {
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera c = cams[i];
                    if (c == null)
                    {
                        continue;
                    }
                    c.ResetWorldToCameraMatrix();
                    c.ResetProjectionMatrix();
                    c.ResetCullingMatrix();
                }
            }
            cams = null;
            camerasOverridden = false;
            haveFrame = false;
            Cull.Reset();
        }

        // Baked map geometry (walls, models, plants) depends on these values, so the map is
        // rebuilt when they change (a settings change, after 0.35 s without another). The camera's
        // pitch and turn are not part of this: the little that depends on them is redrawn on its
        // own (see Relight).
        private static int StateSignature()
        {
            if (!Active)
            {
                return 0;
            }
            IsoRimSettings st = IsoRimMod.Settings;
            unchecked
            {
                int h = 17;
                h = h * 31 + Mathf.RoundToInt(Squash * 10000f);
                h = h * 31 + Mathf.RoundToInt(WallHeight * 100f);
                h = h * 31 + (st.uprightPlants ? 1 : 0);
                h = h * 31 + Mathf.RoundToInt(st.plantCardSize * 100f);
                h = h * 31 + (st.walls3D ? 1 : 0);
                h = h * 31 + (st.texturedWalls ? 1 : 0);
                h = h * 31 + (st.autoExtrude ? 1 : 0);
                h = h * 31 + (st.thickItems ? 1 : 0);
                h = h * 31 + (st.itemsFromArt ? 1 : 0);
                h = h * 31 + (st.pawns3D ? 1 : 0);   // apparel on the ground is dressed as the figures (GroundGear)
                h = h * 31 + (st.rawModels ? 1 : 0);
                h = h * 31 + (st.weaponModels3D ? 1 : 0);   // weapons lying about as their models (WeaponModels)
                h = h * 31 + (st.apparelModels3D ? 1 : 0);   // apparel lying about as laid clothes (GroundGear)
                h = h * 31 + Mathf.RoundToInt(st.itemThickness * 20f);
                h = h * 31 + (st.ghosts3D ? 1 : 0);
                h = h * 31 + (st.cutRock ? 1 : 0);
                h = h * 31 + (SunShadowsSS.ReplacesGround ? 1 : 0);   // with walls3D (above), what the shadow layer keeps   // the game's and the tree shadows left out (baked)
                h = h * 31 + (st.mapEdge3D ? 1 : 0);
                h = h * 31 + (st.damage3D ? 1 : 0);
                h = h * 31 + (st.outlines ? 1 : 0);
                h = h * 31 + (st.texturedModels ? 1 : 0);
                h = h * 31 + (st.ruggedRock ? 1 : 0);
                h = h * 31 + (st.trees3D ? 1 : 0);
                h = h * 31 + (st.smallPlants3D ? 1 : 0);
                h = h * 31 + (st.crops3D ? 1 : 0);
                h = h * 31 + (st.models3D ? 1 : 0);
                h = h * 31 + (st.effects3D ? 1 : 0);   // live flames: torch and brazier models are baked without their own
                h = h * 31 + (st.groundShadows ? 1 : 0);
                h = h * 31 + (st.waterStyle ? 1 : 0);
                h = h * 31 + (st.bridges3D ? 1 : 0);
                h = h * 31 + (st.cutRooms ? 1 : 0);
                h = h * 31 + Mathf.RoundToInt(st.cutHeight * 100f);
                h = h * 31 + (st.treeShadows ? 1 : 0);
                h = h * 31 + Mathf.RoundToInt(st.treeShadowLength * 100f);
                h = h * 31 + ModelLibrary.OverrideGeneration;   // the dev-mode model binder changed a model (Binder/)
                return h == 0 ? 1 : h;
            }
        }

        // Crash trail (log only): what the 3D view runs with, for graphics crash reports.
        private static void NoteSwitch()
        {
            IsoRimSettings ts = IsoRimMod.Settings;
            Map tm = Find.CurrentMap;
            CrashTrail.Note("3D view " + (Active ? "on" : "off") + ": grade " + ts.grade + ", shader cut " + ShaderCut.On
                + ", fog lid " + FogLid + (FogLidLocked ? " (locked)" : "") + ", screen " + Screen.width + "x" + Screen.height
                + ", map " + (tm != null ? tm.Size.x + "x" + tm.Size.z : "none"));
        }

        // -isorim3d (screenshot harness): every game starts with the 3D view on, as with a lock.
        public static bool HarnessStartsOn;

        private static bool switchQueued;

        // A new or loaded game (Game constructor, possibly off the main thread): the 3D view starts off unless an addon
        // locks it on. Nothing of the last game's view is kept (its cameras are gone with its scene).
        public static void NewGame()
        {
            FullWalls.Reset();
            FirstPerson.Reset();
            ViewPoses.Reset();
            SceneImage.Reset();
            Photos.Reset();
            ModelLibrary.ClearOverrides();   // the dev-mode model binder's changes last one game only
            FallbackLog.NewGame();
            Active = LockedOn || HarnessStartsOn;
            lastActive = false;
            appliedSig = 0;
            pendingSig = 0;
            haveFrame = false;
            cams = null;
            camerasOverridden = false;
            switchQueued = false;
            stale.Clear();
        }

        // The long event of a switch-on (TickInner): the map rebuilt for 3D while "Preparing 3D…" shows.
        private static void SwitchOnNow()
        {
            switchQueued = false;
            Camera main = Find.Camera;
            if (!Active || lastActive || main == null || Current.ProgramState != ProgramState.Playing || Current.Game == null)
            {
                return;
            }
            BeginView(main);
            RegenerateIfStateChanged();
        }

        // The 3D view set up for the build that switches it on, shared by a switch-on (SwitchOnNow) and a game that
        // starts with 3D on (PrepareForFirstBuild): the camera turned straight to where it points, then placed; and the
        // facing noted that the parts built for the camera's direction (the map edge, cut walls) are built for, so the
        // first frame finds nothing turned. Before, the load-time build ran before the first frame had set the turn at
        // all (yaw 0 on a fresh start): the map edge was built for a north-facing camera and its south-west sides were
        // missing until the next turn.
        private static void BeginView(Camera main)
        {
            yawSet = false;   // no swing left over: TurnCamera goes straight to the target
            TurnCamera();
            ApplyCamera(main);
            litFacing = Facing;
            wasTurning = false;
        }

        private static readonly AccessTools.FieldRef<MapDrawer, Map> drawerMap = AccessTools.FieldRefAccess<MapDrawer, Map>("map");

        // Before the game builds a map's drawing (MapDrawer.RegenerateEverythingNow prefix). A game that starts with the
        // 3D view on (locked by an addon) builds its maps at the end of loading, still inside the loading screen: the
        // 3D state is set up here first, so that build is the 3D one and the first frame of play has nothing to rebuild
        // (before, the map was built flat while loading and again for 3D on the first frame of play: a stall of a second
        // or more right after the loading screen). Exactly what the first frame would do: camera, light, rebuild.
        public static void PrepareForFirstBuild(MapDrawer drawer)
        {
            if (!Active || haveFrame || lastActive || !UnityData.IsInMainThread)
            {
                return;   // flat, an ordinary rebuild, or this game's 3D is already set up
            }
            Camera main = Find.Camera;
            Map map = drawerMap(drawer);
            if (main == null || Current.Game == null || map == null || Find.CurrentMap == null)
            {
                return;   // the first frame of play switches on as usual ("Preparing 3D…")
            }
            BeginView(main);
            lastActive = true;
            NoteSwitch();
            int sig = StateSignature();
            Stamp = sig;
            appliedSig = sig;
            pendingSig = sig;
            R3DMaterials.SetLight(LightNow());
            MapSections.Clear();
            stale.Clear();
            IsoCellMap.Rebuilt(map);
        }

        private static bool lastReady;

        // The game keeps each pawn's list of what to draw (PawnRenderTree.ParallelPreDraw) until its draw parameters change
        // (facing, posture...); a switch of the 3D view changes none of them. Nodes whose CanDrawNow follows the view (IsoRim's
        // own, an addon's such as the Giddy-Up pack's rider nodes) would keep the other view's answer on a pawn standing still
        // (a rider gone in 2D until its mount turns). When Ready flips, every spawned pawn's list is rebuilt on its next draw
        // (the same work as a pawn turning; the graphics are kept).
        private static void RecachePawnsIfReadyChanged()
        {
            bool ready = Ready;
            if (ready == lastReady)
            {
                return;
            }
            lastReady = ready;
            List<Map> maps = Current.Game != null ? Current.Game.Maps : null;
            for (int m = 0; maps != null && m < maps.Count; m++)
            {
                IReadOnlyList<Pawn> pawns = maps[m].mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    PawnRenderNode root = pawns[i].Drawer?.renderer?.renderTree?.rootNode;
                    if (root != null)
                    {
                        root.requestRecache = true;
                    }
                }
            }
        }

        private static void RegenerateIfStateChanged()
        {
            int sig = StateSignature();
            Stamp = sig;
            bool toggled = Active != lastActive;
            lastActive = Active;
            if (toggled)
            {
                NoteSwitch();
            }

            if (sig == appliedSig)
            {
                pendingSig = sig;
                RebuildStaleShownMaps();
                Relight();
                return;
            }

            if (!toggled)
            {
                if (sig != pendingSig)
                {
                    pendingSig = sig;
                    pendingSince = Time.realtimeSinceStartup;
                    return;
                }
                if (Time.realtimeSinceStartup - pendingSince < 0.35f)
                {
                    return;
                }
            }

            appliedSig = sig;
            pendingSig = sig;
            R3DMaterials.SetLight(LightNow());
            MapSections.Clear();
            RegenerateAllMaps();
        }

        // The sun moves smoothly with the time of day. Every face is lit per pixel by the shader
        // from the sun direction set here each frame (see R3DMaterials.SetSunHour), so nothing is
        // redrawn or rebuilt as it moves.
        //
        // A tilt redraws nothing: plants stood up as cards turn in their shader (UprightCards).
        // A turn redraws only what was built for the camera's direction, once the swing has ended:
        // - the edge of the map, which is built only on the sides facing the camera;
        // - with "cut walls in front of rooms" on, the walls (which are cut depends on the turn).
        // The blocks are redrawn a few per frame, those in view first.
        private static void Relight()
        {
            if (!Active)
            {
                return;
            }
            R3DMaterials.SetLight(LightNow());

            // A turn counts once the swing ends, also one that came back to where it started
            // (anything redrawn during the swing was built at an angle in between).
            bool turned = false;
            bool turning = Turning;
            int step = Facing;
            if (litFacing < 0)
            {
                litFacing = step;
            }
            else if (!turning && (step != litFacing || wasTurning))
            {
                litFacing = step;
                turned = true;
            }
            wasTurning = turning;

            if (!turned)
            {
                return;
            }
            IsoRimSettings st = IsoRimMod.Settings;
            foreach (Map map in Find.Maps)
            {
                bool here = Shown(map);
                if (st.mapEdge3D)
                {
                    MapSections.QueueAll(map, typeof(SectionLayer_IsoRimEdge), here, true);
                }
                if (st.cutRooms && ShaderCut.On)
                {
                    IsoCellMap.Rebuilt(map);   // (shader cut-away) which walls hide a room depends on the facing: worked out again, nothing rebuilt
                }
                else if (st.cutRooms)
                {
                    MapSections.QueueAll(map, typeof(SectionLayer_IsoRimWalls), here);
                    MapSections.QueueAll(map, typeof(SectionLayer_IsoRimDamage), here);
                    if (!st.texturedWalls)
                    {
                        // Untextured walls are in the game's things layer.
                        MapSections.QueueAll(map, MapSections.ThingsLayer, here);
                    }
                }
            }
        }

        private static bool wasTurning;
        private static int litFacing = -1;

        // The hour the sun is at: the current map's time of day, or mid-morning with
        // "Sun moves through the day" off.
        // The direction of the light now: IsoRim's sun by day, the game's moon at night (see
        // R3DMaterials.LightAt); with "Sun moves" off, the fixed mid-morning sun.
        public static Vector3 LightNow()
        {
            Map map = Find.CurrentMap;
            if (!IsoRimMod.Settings.sunMoves || map == null)
            {
                return R3DMaterials.SunDirAt(9.5f);
            }
            float hour = GenLocalDate.HourFloat(map);
            Vector3 dir = R3DMaterials.LightAt(map, hour);
            if (ModLog.Diag)
            {
                int h = GenLocalDate.HourOfDay(map);
                if (h != loggedHour)
                {
                    loggedHour = h;
                    float glow = GenCelestial.CurCelestialSunGlow(map);
                    Log.Message("[IsoRim] light at " + hour.ToString("0.00") + " h: sun glow " + glow.ToString("0.000") + ", night " + R3DMaterials.NightWeight(glow).ToString("0.00")
                        + ", dir (" + dir.x.ToString("0.000") + ", " + dir.y.ToString("0.000") + ", " + dir.z.ToString("0.000") + ")"
                        + ", azimuth " + Mathf.Repeat(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, 360f).ToString("0") + ", elevation " + (Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg).ToString("0"));
                }
            }
            return dir;
        }

        // With ModLog.Diag the light is logged once per game hour.
        private static int loggedHour = -1;

        public static float SunHour()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            Map map = Find.CurrentMap;
            if (!st.sunMoves || map == null)
            {
                return 9.5f;
            }
            return GenLocalDate.HourFloat(map);
        }

        // Rebuilds the maps on screen now and the others when they are next shown, so a settings
        // change costs one map's rebuild rather than every map's.
        private static void RegenerateAllMaps()
        {
            stale.Clear();
            Floors.RedirtyOverlays();   // the whole-map overlays follow (3D on or off, bridge decks lifted or not)
            foreach (Map map in Find.Maps)
            {
                if (Shown(map))
                {
                    Regenerate(map);
                }
                else
                {
                    stale.Add(map);
                }
            }
        }

        private static readonly HashSet<Map> stale = new HashSet<Map>();

        private static void RebuildStaleShownMaps()
        {
            if (stale.Count == 0)
            {
                return;
            }
            stale.RemoveWhere(m => !Find.Maps.Contains(m));
            foreach (Map map in Find.Maps)
            {
                if (stale.Contains(map) && Shown(map))
                {
                    stale.Remove(map);
                    Regenerate(map);
                }
            }
        }

        // Whether a map is drawn now. Only the current map today; with z-levels (design §11) the
        // levels drawn with it would count too.
        public static bool Shown(Map map)
        {
            return map != null && map == Find.CurrentMap;
        }

        private static void Regenerate(Map map)
        {
            if (regenMethod == null)
            {
                Log.ErrorOnce("[IsoRim] MapDrawer.RegenerateEverythingNow was not found, so walls and models will not refresh.", 93611001);
                return;
            }
            if (MapSections.SectionsOf(map) == null)
            {
                // Not drawn yet: the game draws the whole map itself when it is ready. Try again then.
                stale.Add(map);
                return;
            }
            try
            {
                IsoCellMap.Rebuilt(map);
                regenMethod.Invoke(map.mapDrawer, null);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Rebuilding the map drawing failed: " + e, 93611002);
            }
        }

        // Screen pixel (origin bottom left) to the point on the ground under it.
        public static Vector3 ScreenToGround(Vector2 px)
        {
            return ScreenToHeight(px, 0f);
        }

        // Where the line of sight through a screen point (pixels, from the bottom left) is at a
        // given height above the ground, in cells.
        public static Vector3 ScreenToHeight(Vector2 px, float height)
        {
            float nx = px.x / Screen.width * 2f - 1f;
            float ny = px.y / Screen.height * 2f - 1f;
            if (persp)
            {
                // A ray from the eye. Toward the horizon (or above it) it is stopped at the view distance.
                Vector3 dir = fwd + right * (nx * tanHalf * aspect) + up * (ny * tanHalf);
                float reach = camDist + ViewDistance;
                float flat = new Vector2(dir.x, dir.z).magnitude;
                float tMax = flat > 1e-5f ? reach / flat : reach;
                float tr = dir.y < -1e-5f ? (height - eye.y) / dir.y : tMax;
                if (tr < 0f || tr > tMax)
                {
                    tr = tMax;
                }
                Vector3 h = eye + dir * tr;
                return new Vector3(h.x, height, h.z);
            }
            Vector3 origin = eye + right * (nx * orthoSize * aspect) + up * (ny * orthoSize);
            float t = (height - origin.y) / fwd.y;
            Vector3 hit = origin + fwd * t;
            return new Vector3(hit.x, height, hit.z);
        }

        // The other way round: where a point (map x and z, real height in cells as y) shows on
        // screen, in pixels from the bottom left.
        public static Vector2 WorldToScreen(Vector3 p)
        {
            Vector3 d = p - eye;
            float nx, ny;
            if (persp)
            {
                float z = Mathf.Max(0.01f, Vector3.Dot(d, fwd));
                nx = Vector3.Dot(d, right) / (z * tanHalf * aspect);
                ny = Vector3.Dot(d, up) / (z * tanHalf);
            }
            else
            {
                nx = Vector3.Dot(d, right) / (orthoSize * aspect);
                ny = Vector3.Dot(d, up) / orthoSize;
            }
            return new Vector2((nx + 1f) * 0.5f * Screen.width, (ny + 1f) * 0.5f * Screen.height);
        }

        // How many cells one screen pixel spans at a point (map x and z, real height in cells as y), for lines kept a fixed
        // number of pixels wide (TargetBox), with the perspective camera too.
        internal static float CellsPerPixel(Vector3 p)
        {
            float h = Mathf.Max(1f, Screen.height);
            return persp ? 2f * Mathf.Max(0.01f, Vector3.Dot(p - eye, fwd)) * tanHalf / h : 2f * orthoSize / h;
        }

        // The block of map cells that can be seen, used by the game to decide what to draw.
        public static CellRect GroundRect()
        {
            // With the camera turned, the screen is a slanted rectangle on the ground, so all
            // four corners count.
            Vector3 a = ScreenToGround(new Vector2(0f, 0f));
            Vector3 b = ScreenToGround(new Vector2(Screen.width, Screen.height));
            Vector3 c = ScreenToGround(new Vector2(0f, Screen.height));
            Vector3 d = ScreenToGround(new Vector2(Screen.width, 0f));
            CellRect r = new CellRect();
            r.minX = Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x))) - 2;
            r.maxX = Mathf.CeilToInt(Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x))) + 2;
            r.minZ = Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z))) - 2;
            r.maxZ = Mathf.CeilToInt(Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z))) + 2;

            // Extra room on the sides nearest the camera: tall things just off those screen
            // edges still poke into view.
            Vector3 f = GroundForward;
            if (f.z > 0.3f)
            {
                r.minZ -= 6;
            }
            else if (f.z < -0.3f)
            {
                r.maxZ += 6;
            }
            if (f.x > 0.3f)
            {
                r.minX -= 6;
            }
            else if (f.x < -0.3f)
            {
                r.maxX += 6;
            }
            return r;
        }

        // The sprite facing that shows a thing facing world way "facing" on a card stood up square to the camera:
        // the game picks a sprite (back for north, side for east and west) assuming north is up the screen, so
        // with the camera turned the facing is turned back by the camera's quarter turns. On a diagonal (45 degree
        // snap) the quarter the camera has turned less into is kept, so the starting isometric view (45) shows the
        // game's own facings. Used for upright pawn sprites (Patches.PreRenderResults_Prefix) and outfit stands.
        public static Rot4 SeenFacing(Rot4 facing)
        {
            int turns = Mathf.FloorToInt((Mathf.Repeat(Yaw, 360f) + 44f) / 90f) & 3;
            return new Rot4((facing.AsInt - turns + 4) & 3);
        }

        // The direction a map-space aim shows on screen, as an angle on a card stood up square to the camera (degrees
        // clockwise from up the card, the game's convention for a flat sprite seen with north up). flat is the aim along
        // the ground, rise how far the target is above the shooter (cells, between floors). At yaw 0 looking straight
        // down this is the map angle itself. Orthographic: the ground is foreshortened by sin(pitch), height by cos(pitch).
        public static float ScreenAim(Vector3 flat, float rise)
        {
            float y = Yaw * Mathf.Deg2Rad;
            float p = Pitch * Mathf.Deg2Rad;
            float sx = flat.x * Mathf.Cos(y) - flat.z * Mathf.Sin(y);
            float sy = (flat.x * Mathf.Sin(y) + flat.z * Mathf.Cos(y)) * Mathf.Sin(p) + rise * Mathf.Cos(p);
            if (sx * sx + sy * sy < 1e-8f)
            {
                return 0f;
            }
            return Mathf.Repeat(Mathf.Atan2(sx, sy) * Mathf.Rad2Deg, 360f);
        }

        // Stands a flat sprite up so that it faces the camera, hinged at pivot.
        public static Matrix4x4 UprightMatrix(Vector3 pivot)
        {
            return Matrix4x4.Translate(pivot)
                * Matrix4x4.Scale(new Vector3(1f, 1f / Squash, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(Pitch - 90f, Yaw, 0f))
                * Matrix4x4.Translate(-pivot);
        }

        // Stands a flat quad up vertically over pivot, turned toward the camera about the world vertical only (no lean
        // back by the pitch): the game's "up the screen" (+z of a flat drawing) becomes straight up, its east stays to
        // the right on screen. For columns from the sky (SkyStrikes), which must not tip over when the camera pitches.
        public static Matrix4x4 VerticalMatrix(Vector3 pivot)
        {
            return Matrix4x4.Translate(pivot)
                * Matrix4x4.Scale(new Vector3(1f, 1f / Squash, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(-90f, Yaw, 0f))
                * Matrix4x4.Translate(-pivot);
        }

        // Turns a movement given in screen terms (x to the right, y up the screen) into one along
        // the ground. With exact set, a movement up the screen is also stretched to make up for
        // the ground being seen at a slant, so that a dragged map stays under the pointer.
        public static Vector2 ScreenMoveToGround(Vector2 move, bool exact)
        {
            float y = move.y;
            if (exact)
            {
                y /= Mathf.Max(0.2f, Mathf.Sin(Pitch * Mathf.Deg2Rad));
            }
            float yaw = Yaw * Mathf.Deg2Rad;
            float cy = Mathf.Cos(yaw);
            float sy = Mathf.Sin(yaw);
            return new Vector2(move.x * cy + y * sy, -move.x * sy + y * cy);
        }

        // A screen-filling quad (Weather.Curtain's mesh, 1 by 1 on the XZ plane) stood up facing the camera,
        // just in front of it. Drawn space, so it is lined up for the squashed view matrix.
        public static Matrix4x4 CurtainMatrix()
        {
            float depth, height;
            if (persp)
            {
                depth = Mathf.Max(1f, camDist * 0.15f);
                height = 2f * depth * tanHalf;
            }
            else
            {
                depth = 2f;
                height = 2f * orthoSize;
            }
            Vector3 at = eye + fwd * depth;
            return Matrix4x4.Translate(new Vector3(at.x, at.y / Squash, at.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / Squash, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(Pitch - 90f, Yaw, 0f))
                * Matrix4x4.Scale(new Vector3(height * aspect, 1f, height));
        }

        // The camera this frame, in true space: the eye, where it looks, its right and up, and how far the
        // eye is from the focus point on the ground (perspective).
        public static Vector3 Eye { get { return eye; } }
        public static float CamDistance { get { return camDist; } }
        public static float OrthoSize { get { return orthoSize; } }

        // The screen's width over its height.
        public static float Aspect
        {
            get { return aspect; }
        }

        // Raises something by a real height, in cells, without tilting it.
        public static Matrix4x4 LiftMatrix(float height)
        {
            return Matrix4x4.Translate(new Vector3(0f, height / Squash, 0f));
        }
    }
}
