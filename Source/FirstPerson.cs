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
        private static float lookPitch = 8f;   // degrees below the horizon (negative = up)

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
                float headRise;
                Vector3 p = Poses.DrawnSpot(pawn, out headRise);   // sitting: the eye comes down with the head
                Vector3 o = Floors.ThingOffset(pawn);
                float h = Mathf.Clamp(IsoRimMod.Settings.fpEyeHeight, 0.3f, 3f) + headRise;
                // A little forward, so the eye is not inside the back of the head.
                Vector3 f = Quaternion.Euler(0f, heading, 0f) * Vector3.forward * 0.12f;
                return new Vector3(p.x + o.x + f.x, h + o.y, p.z + o.z + f.z);
            }
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
            lookPitch = 8f;
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
            if (!View3D.Active || pawn.Destroyed || !pawn.Spawned || pawn.Map != Find.CurrentMap || pawn.Dead)
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
            lookPitch = 8f;
        }
    }
}
