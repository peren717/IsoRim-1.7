using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // First person (FP1): the camera rides in the head of one colonist, who goes on doing what they
    // were doing. The view turns smoothly toward where they walk; dragging with the middle button
    // (or the free look buttons) looks round on top of that. The colonist is not drawn.
    //
    // Only the camera changes: View3D.ApplyCamera puts the eye here when On, and everything that
    // turns the pointer into a map cell already follows the camera's rays.
    public static class FirstPerson
    {
        private static Pawn pawn;
        private static float heading;      // where the body faces, degrees clockwise from north
        private static bool headingSet;
        private static Vector3 lastPos;
        private static float lookYaw;      // looking round, on top of the heading
        public const float StartPitch = 8f;   // where the look starts and resets to: degrees below the horizon
        private static float lookPitch = StartPitch;   // degrees below the horizon (negative = up)

        private const float TurnRate = 5f;     // how fast the view follows the body (per second, exponential)
        public const float MinPitch = -60f;
        public const float MaxPitch = 75f;

        public static bool On
        {
            get { return pawn != null; }
        }

        public static Pawn Pawn
        {
            get { return pawn; }
        }

        // First person is on and the 3D view's switch is on: what the player sees whenever no external camera pose (Api 66)
        // holds the view, from the first 3D frame on (also while the map is still being prepared for 3D). With the switch
        // off first person ends at the next Update. Api 71 (Api.FirstPersonOn) reads this.
        public static bool Engaged
        {
            get { return pawn != null && View3D.Active; }
        }

        // The camera is in the colonist's head this frame: first person is on and no external camera pose (Api 66) holds
        // the view. What depends on the camera being in the head (the colonist and their label hidden, no tilt-shift, the
        // rain box round the eye) asks this; first person itself (following the colonist, its look) carries on underneath.
        public static bool Viewing
        {
            get { return pawn != null && !ViewPoses.Active; }
        }

        // The colonist whose head the camera is in, left out of the drawing. While a photo's draws are made (Api 76,
        // Photos) the photo's own: the one its options name (CaptureViewOptions.FirstPersonPawn), else none.
        public static Pawn ViewingPawn
        {
            get { return View3D.InPhoto ? Photos.EyesOf : Viewing ? pawn : null; }
        }

        public static float Yaw
        {
            get { return Mathf.Repeat(heading + lookYaw, 360f); }
        }

        public static float Pitch
        {
            get { return lookPitch; }
        }

        // The eye: on the colonist's spot (smooth, as drawn), at head height, on an upper floor if they are on one.
        public static Vector3 Eye
        {
            get
            {
                if (pawn == null)
                {
                    return Vector3.zero;
                }
                return EyeOf(pawn, heading);
            }
        }

        // Where first person puts the eye of a person facing this way (degrees clockwise from north): on their spot as drawn,
        // at head height, on an upper floor if they are on one, a little forward. Also for photos (Api 76, ViewPose.FirstPersonOf).
        // The one eye rule: an eye provider's answer (Api 77) wins, moved onto the person's floor like everything they draw;
        // then the eye height option, the head drop and the nudge forward are not used for them. Asked while a provider runs
        // (askingProviders: ViewPose.FirstPersonOf inside it), the answer is IsoRim's own eye in the providers' space (before
        // the floor's offset), so a provider can return it adjusted and the offset is added once.
        internal static Vector3 EyeOf(Pawn who, float facing)
        {
            Vector3 given;
            if (eyeProviders.Length != 0 && !askingProviders && ProvidedEye(who, out given))
            {
                return given + Floors.ThingOffset(who);
            }
            float headRise;
            Vector3 p = Poses.DrawnSpot(who, out headRise);   // sitting: the eye comes down with the head
            Vector3 o = askingProviders ? Vector3.zero : Floors.ThingOffset(who);
            float h = Mathf.Clamp(IsoRimMod.Settings.fpEyeHeight, 0.3f, 3f) + headRise;
            // A little forward, so the eye is not inside the back of the head.
            Vector3 f = Quaternion.Euler(0f, facing, 0f) * Vector3.forward * 0.12f;
            return new Vector3(p.x + o.x + f.x, h + o.y, p.z + o.z + f.z);
        }

        // ---- (Api 77) eye providers: other mods that draw their own figures say where a person's eye is

        private static Func<Pawn, Vector3?>[] eyeProviders = new Func<Pawn, Vector3?>[0];
        private static bool askingProviders;   // set while a provider runs: an eye asked inside it (FirstPersonOf) is IsoRim's own, in the providers' space

        internal static void AddEyeProvider(Func<Pawn, Vector3?> provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }
            if (Array.IndexOf(eyeProviders, provider) >= 0)
            {
                throw new ArgumentException("[IsoRim] Api.AddFirstPersonEye: this provider is added already.");
            }
            Func<Pawn, Vector3?>[] next = new Func<Pawn, Vector3?>[eyeProviders.Length + 1];
            eyeProviders.CopyTo(next, 0);
            next[eyeProviders.Length] = provider;
            eyeProviders = next;
        }

        internal static void RemoveEyeProvider(Func<Pawn, Vector3?> provider)
        {
            int i = Array.IndexOf(eyeProviders, provider);
            if (i < 0)
            {
                return;
            }
            Func<Pawn, Vector3?>[] next = new Func<Pawn, Vector3?>[eyeProviders.Length - 1];
            Array.Copy(eyeProviders, 0, next, 0, i);
            Array.Copy(eyeProviders, i + 1, next, i, eyeProviders.Length - i - 1);
            eyeProviders = next;
        }

        // The first provider's answer for this person, in the order added (true space before their floor's offset). A provider
        // that throws or answers a point that is not a number is removed with one warning. Off the main thread none is asked.
        private static bool ProvidedEye(Pawn who, out Vector3 eye)
        {
            eye = Vector3.zero;
            if (who == null || !UnityData.IsInMainThread)
            {
                return false;
            }
            Func<Pawn, Vector3?>[] all = eyeProviders;
            for (int i = 0; i < all.Length; i++)
            {
                Vector3? r;
                askingProviders = true;   // (a provider asking for an eye itself, e.g. ViewPose.FirstPersonOf, gets IsoRim's: no recursion)
                try
                {
                    r = all[i](who);
                }
                catch (Exception e)
                {
                    Log.Warning("[IsoRim] A first-person eye provider failed and is removed: " + e);
                    RemoveEyeProvider(all[i]);
                    continue;
                }
                finally
                {
                    askingProviders = false;
                }
                if (!r.HasValue)
                {
                    continue;
                }
                Vector3 v = r.Value;
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                {
                    Log.Warning("[IsoRim] A first-person eye provider answered " + v + " for " + who.LabelShort + " and is removed.");
                    RemoveEyeProvider(all[i]);
                    continue;
                }
                eye = v;
                return true;
            }
            return false;
        }

        // The first person key: in with the one selected person, out again.
        public static void Toggle()
        {
            if (pawn != null)
            {
                Leave("R3D_Msg_FirstPersonOff".Translate());
                return;
            }
            Pawn chosen = null;
            if (Find.Selector != null && Find.Selector.NumSelected == 1)
            {
                chosen = Find.Selector.SingleSelectedThing as Pawn;
            }
            if (chosen == null || !chosen.Spawned || chosen.Map != Find.CurrentMap || chosen.RaceProps == null || !chosen.RaceProps.Humanlike)
            {
                Messages.Message("R3D_Msg_SelectOnePerson".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            View3D.Active = true;
            pawn = chosen;
            headingSet = false;
            lookYaw = 0f;
            lookPitch = StartPitch;
            Messages.Message("R3D_Msg_FirstPersonWith".Translate(chosen.LabelShort), MessageTypeDefOf.SilentInput, false);
        }

        public static void Leave(string message)
        {
            if (pawn == null)
            {
                return;
            }
            Pawn was = pawn;
            pawn = null;
            if (was.Spawned && Find.CameraDriver != null && was.Map == Find.CurrentMap)
            {
                Find.CameraDriver.JumpToCurrentMapLoc(was.DrawPos);
            }
            if (message != null)
            {
                Messages.Message(message, MessageTypeDefOf.SilentInput, false);
            }
        }

        // A scene/game change must not keep the previous game's followed pawn. No camera or UI work here.
        internal static void Reset()
        {
            pawn = null;
            headingSet = false;
            ResetLook();
        }

        // Once a frame, before the camera is set: drops out if the colonist is gone, turns the body
        // heading toward where they walk, and keeps the game's camera over them (so the game draws
        // and updates the part of the map round them).
        public static void Update()
        {
            if (pawn == null)
            {
                return;
            }
            if (!Engaged || pawn.Destroyed || !pawn.Spawned || pawn.Map != Find.CurrentMap || pawn.Dead)
            {
                Leave("R3D_Msg_FirstPersonOff".Translate());
                return;
            }
            Vector3 p = pawn.DrawPos;
            if (!headingSet)
            {
                heading = pawn.Rotation.AsAngle;
                headingSet = true;
                lastPos = p;
            }
            Vector3 moved = p - lastPos;
            moved.y = 0f;
            float target = heading;
            if (moved.sqrMagnitude > 0.0004f)
            {
                target = Mathf.Atan2(moved.x, moved.z) * Mathf.Rad2Deg;
                lastPos = p;
            }
            else if (!pawn.pather.Moving)
            {
                // Standing (working, talking): face the way the game turns them.
                target = pawn.Rotation.AsAngle;
                lastPos = p;
            }
            float k = 1f - Mathf.Exp(-TurnRate * Time.unscaledDeltaTime);
            heading = Mathf.Repeat(heading + Mathf.DeltaAngle(heading, target) * k, 360f);

            try
            {
                if (Find.CameraDriver != null)
                {
                    Find.CameraDriver.JumpToCurrentMapLoc(new Vector3(p.x, 0f, p.z));
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: moving the camera failed: " + e, 93611901);
            }
        }

        // Mouse look, in degrees dragged.
        public static void Look(float dYaw, float dPitch)
        {
            lookYaw = Mathf.Repeat(lookYaw + dYaw + 180f, 360f) - 180f;
            lookPitch = Mathf.Clamp(lookPitch + dPitch, MinPitch, MaxPitch);
        }

        public static void ResetLook()
        {
            lookYaw = 0f;
            lookPitch = StartPitch;
        }
    }
}
