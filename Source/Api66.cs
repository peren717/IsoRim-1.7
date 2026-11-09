using System;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>
    /// (Api version 66) Where the 3D view looks from: <see cref="Api.PushViewPose"/>, <see cref="Api.CurrentViewPose"/>.
    /// Positions are true space (x and z in map cells, y the height in cells). Fields left 0 or null take IsoRim's own value.
    /// </summary>
    public struct ViewPose
    {
        /// <summary>Which way the camera faces, degrees clockwise from north seen from above (0 = north up the screen).</summary>
        public float Yaw;

        /// <summary>How far the camera looks down, degrees below the horizon (90 = straight down; negative looks up).
        /// Perspective: -89..89; orthographic: 1..89.</summary>
        public float Pitch;

        /// <summary>A perspective camera (else orthographic).</summary>
        public bool Perspective;

        /// <summary>Perspective: the eye itself (a free camera). Null: the eye stands back from <see cref="Focus"/> along the line
        /// of sight as IsoRim's own perspective camera does at this zoom. Orthographic: the view is centred where the line of
        /// sight from this point meets the ground (when <see cref="Focus"/> is null).</summary>
        public Vector3? Eye;

        /// <summary>The ground point at the middle of the screen (y ignored) when there is no eye. Null: the game camera's
        /// point (what the player pans).</summary>
        public Vector3? Focus;

        /// <summary>Perspective: the vertical field of view in degrees, 5-150. 0: the player's option.</summary>
        public float Fov;

        /// <summary>The zoom: half the screen's height in cells (orthographic; perspective without an eye sets the distance
        /// from it, so the ground at the middle keeps this scale). 0: the game's zoom.</summary>
        public float OrthoSize;

        /// <summary>Perspective: how far the view looks, cells (label fading, how far pointer rays reach, the fog, the lighting
        /// overlay's pull). 0: with an eye, the distance along the line of sight to the ground within 10-150 (150 when looking
        /// level or up); without, the distance to the focus point.</summary>
        public float FocusDistance;

        /// <summary>The near and far clip planes, cells. 0: IsoRim's (perspective with an eye: near 0.08-1 by the eye's height,
        /// far = FocusDistance + the view distance option + 90).</summary>
        public float Near;

        /// <summary>See <see cref="Near"/>.</summary>
        public float Far;

        /// <summary>A free perspective eye looking along yaw and pitch.</summary>
        public static ViewPose FromEye(Vector3 eye, float yaw, float pitch, float fov = 0f)
        {
            return new ViewPose { Eye = eye, Yaw = yaw, Pitch = pitch, Perspective = true, Fov = fov };
        }

        /// <summary>A camera turned round a ground point, as IsoRim's own (orthoSize 0: the game's zoom; focus null: the game camera's point).</summary>
        public static ViewPose Orbit(Vector3? focus, float yaw, float pitch, bool perspective, float orthoSize = 0f, float fov = 0f)
        {
            return new ViewPose { Focus = focus, Yaw = yaw, Pitch = pitch, Perspective = perspective, OrthoSize = orthoSize, Fov = fov };
        }
    }

    /// <summary>(Api version 66) How a pushed pose holds the view (<see cref="Api.PushViewPose"/>).</summary>
    public sealed class ViewPoseOptions
    {
        /// <summary>The player's camera keys and free look (look drag, turn, tilt, reset) do nothing while this pose holds the
        /// view, so the player's camera is as it was when it goes. The 3D view, isometric, first person and roof keys still
        /// work, and the game's own panning and zoom are left alone (the pose may follow them: Focus/OrthoSize 0).</summary>
        public bool BlockPlayerCamera = true;

        /// <summary>The view counts as turning while the pose holds it: what IsoRim builds for the camera's direction (the map
        /// edge, cut-away walls) is not rebuilt for the pose, once when the player's camera is back. False: it follows the
        /// pose's facing (an eighth turn of the pose rebuilds those as a player's turn does).</summary>
        public bool CountsAsTurning = true;

        /// <summary>Higher wins; among equal priorities the newest push wins.</summary>
        public int Priority;
    }

    /// <summary>(Api version 66) What a scene image includes (<see cref="Api.RequestSceneImage"/>).</summary>
    public sealed class SceneImageOptions
    {
        /// <summary>Map overlays are in the picture: selection brackets and rings, designations, status icons, the designator in
        /// hand (its drag box, ghosts, placing previews), targeting highlights, room stats, group move markers, IsoRim's drag
        /// boxes and mining x-ray. False leaves them out (their draws are skipped for two frames, so on screen they blink once).
        /// Zone and area colours and the game's grid toggles (roofs, fertility, beauty...) stay either way.</summary>
        public bool IncludeOverlays = true;
    }

    public static partial class Api
    {
        // ---- camera poses (Version 66)

        /// <summary>
        /// (Version 66) Another mod's camera takes the 3D view: pose is asked once a frame (main thread, before the camera is
        /// set; see <see cref="BeforeViewUpdate"/>) and the view looks from what it returns; null gives the view back for that
        /// frame (to the next pose, else first person, else the player). Everything follows the pose as it follows IsoRim's
        /// own camera: culling and what the game draws, pointer rays and the cell under the mouse, labels, upright cards and
        /// sprites, sky, fog and weather, the lighting overlay, screen passes; <see cref="ViewPitch"/>, <see cref="ViewYaw"/>,
        /// <see cref="WorldToScreen"/>, <see cref="ScreenToGround"/> report it. Nothing is written to the player's settings and
        /// nothing is rebuilt for it (see <see cref="ViewPoseOptions.CountsAsTurning"/>); when the last pose is gone the view
        /// is the player's camera exactly as the player left it (use <see cref="SetPlayerView"/> to hand angles over).
        /// A pose wins over IsoRim's first person while pushed (the colonist is drawn again; first person resumes after).
        /// Only while the 3D view is on (a pose does not switch it on). Dispose the handle, or <see cref="RemoveViewPoses"/>,
        /// to give the view back; a new game removes every pose. A pose that throws or is not a number: one warning, removed.
        /// </summary>
        public static IDisposable PushViewPose(object owner, Func<ViewPose?> pose, ViewPoseOptions options = null)
        {
            return ViewPoses.Push(owner, pose, options);
        }

        /// <summary>(Version 66) Removes every pose this owner pushed; how many.</summary>
        public static int RemoveViewPoses(object owner)
        {
            return owner == null ? 0 : ViewPoses.RemoveOwner(owner);
        }

        /// <summary>(Version 66) Raised once a frame while a game is shown, before the camera pose is asked for and the view set
        /// (after IsoRim read its own view keys and first person moved on): the place for a camera mod's own input and
        /// movement. Main thread. A handler that throws: one warning, removed.</summary>
        public static event Action BeforeViewUpdate
        {
            add { ViewPoses.AddBefore(value); }
            remove { ViewPoses.RemoveBefore(value); }
        }

        /// <summary>(Version 66) A pushed pose holds the view this frame, and whose.</summary>
        public static bool ViewPoseActive => View3D.Ready && ViewPoses.Active;

        public static object ViewPoseOwner => ViewPoseActive ? ViewPoses.Owner : null;

        /// <summary>(Version 66) The camera this frame as a pose (a pushed one, first person or the player's), every field filled:
        /// the eye, Focus (orthographic: the ground at the middle of the screen; perspective: null), Fov, OrthoSize,
        /// FocusDistance, Near, Far. Valid while <see cref="ViewReady"/>.</summary>
        public static ViewPose CurrentViewPose => View3D.PoseNow();

        /// <summary>(Version 66) Turns the player's own camera to this yaw and tilts it to this pitch (null keeps it): written
        /// to the settings the player's own keys and drags write (the yaw on top of the eighth turns; the pitch of the current
        /// mode, within the player's allowed range). snap: no swing to the new yaw. For a camera mod handing its angles to the
        /// player's camera as it leaves.</summary>
        public static void SetPlayerView(float? yaw, float? pitch, bool snap = true)
        {
            View3D.SetPlayerView(yaw, pitch, snap);
        }

        // ---- scene images (Version 66)

        /// <summary>
        /// (Version 66) A picture of the map view as shown, without any interface: everything the map pass draws (the 3D view
        /// or the flat map, with IsoRim's ambient occlusion, sun shadows, tilt-shift, anti-aliasing and colour grade, sky, fog,
        /// weather, stacked floors, then the engine's final saturation as the screen gets it) and none of the IMGUI drawn after it (the game's and every mod's windows, buttons, labels,
        /// bars, messages). Screen size, RGBA32, opaque, rows bottom first as Texture2D. done is called on the main thread a
        /// few frames later (never in the frame it was asked) with a new texture that is the caller's (Destroy it when done),
        /// or null if it could not be taken. False (done is not called) when it can't be asked now: no game shown, or turned
        /// off for this session after an engine problem. Requests made before a picture is drawn share it (each caller gets its own texture). Drawn on top
        /// of the map's own draws (queue 4990): a mod material with a render queue above 4990 is not in it.
        /// </summary>
        public static bool RequestSceneImage(Action<Texture2D> done, SceneImageOptions options = null)
        {
            return SceneImage.Request(done, options == null || options.IncludeOverlays);
        }

        /// <summary>(Version 66) The last scene image on the GPU (R8G8B8A8, before the engine's final saturation step, the scene's
        /// alpha as it was), for drawing it rather than reading it: IsoRim's, valid until the next picture is taken; null before
        /// the first.</summary>
        public static RenderTexture SceneImageTexture => SceneImage.Texture;
    }
}
