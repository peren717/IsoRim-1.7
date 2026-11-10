using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    public static partial class Api
    {
        // ---- the 3D view, first person and keys for camera mods (Version 68)

        /// <summary>
        /// (Version 68) IsoRim's 3D view is switched on (the player's 3D view key, corner button or options button). It is the
        /// switch: the first frame after it is turned on rebuilds the map for 3D ("Preparing 3D..."), and <see cref="ViewReady"/>
        /// is true once a 3D frame has been set up. A newly started or loaded game starts flat, unless an addon keeps the view on.
        /// Set: switches the view as the player's corner button does (the same switch; the rebuild, first person, camera and
        /// everything else follow it on the next frame exactly as after the player's own key), without the key's on/off message.
        /// No effect, no error: no game being played, called off the main thread, during a gravship cutscene that holds the
        /// camera (<see cref="ControlsLocked"/>, the player's keys are locked too), or false while an addon keeps the view on.
        /// Read it back to see whether it took.
        /// </summary>
        public static bool View3DActive
        {
            get
            {
                return View3D.Active;
            }
            set
            {
                if (!UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing || Current.Game == null || View3D.ControlsLocked)
                {
                    return;
                }
                if (!value && View3D.LockedOn)
                {
                    return;
                }
                View3D.Active = value;
            }
        }

        /// <summary>(Version 68) A gravship cutscene holds the camera now (the game takes it from the player unless its free-cam
        /// option is set): IsoRim's own view keys, look drag, first person and 3D switch do nothing meanwhile, and its view turn,
        /// tilt and 3D state stay as they were when it started. A camera mod should leave the camera alone too.</summary>
        public static bool ControlsLocked => View3D.ControlsLocked;

        /// <summary>(Version 68) Ends IsoRim's first person if it is on, as IsoRim itself ends it for a gravship launch: the game
        /// camera is put over the colonist (when still on the shown map), no message. True if first person was on. Main thread
        /// (else false, nothing done).</summary>
        public static bool LeaveFirstPerson()
        {
            if (!UnityData.IsInMainThread || !FirstPerson.On)
            {
                return false;
            }
            FirstPerson.Leave(null);
            return true;
        }

        /// <summary>
        /// (Version 68) A key binding is held now, as IsoRim reads its own held bindings (its look modifier): either of the two
        /// keys bound to it in the game's keyboard options is down, and mouse buttons count (bound as Mouse0-Mouse6; the game's
        /// <c>KeyBindingDef.IsDown</c> only reads keys). False for a null binding, before the key settings are loaded, and while
        /// the search box of an open window has focus. A binding with no key in either slot answers unboundHeld (even while a
        /// search box has focus): true for a modifier that should not be needed when the player unbound it. Other text fields
        /// are not checked (check <c>GUIUtility.keyboardControl != 0</c> yourself if typing should block it).
        /// </summary>
        public static bool KeyHeld(KeyBindingDef key, bool unboundHeld = false)
        {
            return View3D.BindingHeld(key, unboundHeld);
        }

        /// <summary>(Version 68) Tan of half IsoRim's default vertical field of view (40 degrees, tan 20 = 0.36397): an eye at
        /// focus - forward * (orthoSize / PerspectiveTanHalf) with that field of view shows the ground at the focus at the same
        /// scale as the orthographic camera of that orthoSize. IsoRim's flat camera reports its height (Altitude) with it. A
        /// const: compiled into the caller.</summary>
        public const float PerspectiveTanHalf = View3D.DefaultTanHalf;

        /// <summary>
        /// (Version 68) Draws IsoRim's sleeper blanket over a person in a bed at the caller's placing: the bed's own top pushed up
        /// by the hidden body, with IsoRim's cache, build budget, look and the person's damage flash, as IsoRim draws it over its
        /// own figure in a bed that hides the body. Draws for any bed given: call it only when the bed hides the body
        /// (<see cref="FigurePlacement.HeadOnlyInBed"/>, the bed def's <c>building.bed_showSleeperBody</c> false), every frame
        /// the figure is drawn.
        /// at: the head's middle in true space (x, z map cells; y the bed's top plus any lift), IsoRim's <c>FigurePlacement.Position</c>.
        /// yaw: degrees; the turned +z runs from the head toward the feet (IsoRim: the bed's rotation angle, <c>FigurePlacement.Yaw</c>).
        /// size: the body's size, 1 for an adult (<c>FigurePlacement.Size</c>). ground: the height of the bed's top the sleeper lies
        /// on over the floor, cells (<c>FigurePlacement.Position.y - Lift</c>).
        /// start: cells from at toward the feet where the blanket begins; below 0: IsoRim's (0.14 x size x scale, just under the jaw).
        /// scale: the blanket made as for a body this many times bigger (taller, longer, as wide as the bed allows), kept within
        /// 0.25-3; 1, 0 or below: IsoRim's. With both IsoRim's the blanket is exactly IsoRim's own at that placing.
        /// True: drawn, or waiting for its build budget. False, nothing drawn: pawn or bed null, the bed not spawned, no blanket fits
        /// the bed, size not above 0, the 3D view not drawn this frame, or off the main thread.
        /// </summary>
        public static bool DrawSleeperBlanket(Pawn pawn, Building_Bed bed, Vector3 at, float yaw, float size, float ground,
            float start = -1f, float scale = -1f)
        {
            if (pawn == null || bed == null || !(size > 0f) || !UnityData.IsInMainThread || !View3D.Ready)
            {
                return false;
            }
            bool flashWas = HitFlash.Pause();
            if (FigureDrawers.FlashOf(pawn) > 0f)
            {
                HitFlash.Begin(pawn);   // (as FigureDrawContext.DrawSleeperBlanket: inside the figure's flash)
            }
            try
            {
                return SleeperBlanket.TryDraw(pawn, bed, at, yaw, size, ground, start, scale);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Api.DrawSleeperBlanket failed: " + e, 93618068);
                return false;
            }
            finally
            {
                HitFlash.Resume(flashWas);
            }
        }
    }
}
