using System;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>(Api version 76) What a photo (<see cref="Api.CaptureView"/>) is like.</summary>
    public sealed class CaptureViewOptions
    {
        /// <summary>The photo's size in pixels, 16-4096. 0: the screen's width (or height).</summary>
        public int Width;

        /// <summary>See <see cref="Width"/>.</summary>
        public int Height;

        /// <summary>Map overlays are in the photo: selection brackets and rings, designations, status icons (as the game draws
        /// them for the shown view). False (the default): none of them, and nothing see-through round the pointer while
        /// placing.</summary>
        public bool IncludeOverlays;

        /// <summary>The shown view's colour passes as the player has them: anti-aliasing and sharpening, then the colour grade,
        /// and the engine's final saturation (default true). False: the picture as drawn.</summary>
        public bool ColourPost = true;

        /// <summary>The photo is taken from this person's eyes (a first-person photo): they are left out of it, as IsoRim's own
        /// first person leaves its colonist out. Null: everyone is drawn.</summary>
        public Pawn FirstPersonPawn;
    }

    public partial struct ViewPose
    {
        /// <summary>(Api version 76) A perspective eye looking at a point (true space): a selfie, a portrait, a shot of a
        /// thing from where you like.</summary>
        public static ViewPose LookAt(Vector3 eye, Vector3 target, float fov = 0f)
        {
            Vector3 d = target - eye;
            float flat = new Vector2(d.x, d.z).magnitude;
            float yaw = flat > 1e-5f ? Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg : 0f;
            float pitch = Mathf.Atan2(-d.y, Mathf.Max(flat, 1e-5f)) * Mathf.Rad2Deg;
            return FromEye(eye, yaw, pitch, fov);
        }

        /// <summary>(Api version 76) Through a person's eyes: the eye where IsoRim's first person puts it (their spot as drawn,
        /// the first-person eye height option, sitting, stacked floors; or the eye a provider gives, Api 77
        /// <see cref="Api.AddFirstPersonEye"/>), looking the way they face (their rotation) and pitch
        /// degrees below the horizon (IsoRim's first person starts at 8). Pair it with
        /// <see cref="CaptureViewOptions.FirstPersonPawn"/> so they are left out of the photo.</summary>
        public static ViewPose FirstPersonOf(Pawn pawn, float pitch = FirstPerson.StartPitch, float fov = 0f)
        {
            if (pawn == null)
            {
                throw new ArgumentNullException(nameof(pawn));
            }
            float facing = pawn.Rotation.AsAngle;
            return FromEye(FirstPerson.EyeOf(pawn, facing), facing, pitch, fov);
        }
    }

    public static partial class Api
    {
        // ---- photos (Version 76)

        /// <summary>
        /// (Version 76) A photo of a map from any pose, drawn offscreen: the player's screen does not change at all. The map's
        /// blocks, things, figures and models, cards and sprites stood up to the photo's camera, flecks, IsoRim's live parts,
        /// the sky (perspective) and, if asked, the map overlays, culled and drawn for the photo's camera; then the shown
        /// view's colour passes (<see cref="CaptureViewOptions.ColourPost"/>). Not in photos: ambient occlusion, screen-space
        /// sun shadows and subsurface light, tilt-shift and depth of field, the weather, any interface. Kept as built for the
        /// shown view: the cut-away walls and roofs, the map edge, the water's depth (water looks slightly off), the game's sun
        /// shadows. pose: as <see cref="PushViewPose"/>'s (0 or null fields take IsoRim's own value; OrthoSize is half the
        /// photo's height). done is called on the main thread one frame after the photo is drawn (usually the next frame or
        /// the one after) with a new RGBA32 texture (opaque, rows bottom first as Texture2D) that is the caller's to Destroy,
        /// or with null if it could not be taken (the map not shown in 3D for a while, an engine problem). Photos are taken one
        /// a frame, in the order asked. False (done is not called) when it can't be asked: no game, the 3D view off, a pose
        /// that is not a number, or photos turned off for this session after an engine problem. Main thread.
        /// </summary>
        public static bool CaptureView(Map map, ViewPose pose, CaptureViewOptions options, Action<Texture2D> done)
        {
            return Photos.Request(map, pose, options, done);
        }
    }
}
