using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Body animations other mods give the game's pawn sprite, mapped onto a standing 3D figure (2026-10-17).
    // A crawling figure (Crawl) gets them too, as its sprite lies on the ground: over the ground and about the vertical (ReadCrawl).
    //
    // Such mods (Yayo's Animation, for one) patch PawnRenderer.GetBodyPos and PawnRenderer.BodyAngle, which the game uses to
    // place and turn the sprite. For a standing pawn the game's own answers are fixed: the drawn position unchanged and no
    // turn. Whatever a patched method gives on top of that is the mod's animation, read here (not copied: whatever the mod
    // works out, by whatever means) and put on the figure as the sprite would show it on screen: the shift across the
    // picture along the camera's right, up the picture as height; the sprite's turn as a lean forward or back along the
    // figure's own facing (ForwardLean), about the body's middle. Picture units are scaled to the figure's (as its weapon).
    // When no other mod patches either method nothing is read at all (Active: decided once, after every mod has loaded).
    public static class BodyAnims
    {
        private const float Picture = 1.12f / 1.5f;   // the game's picture units to figure units (FigureWeapons)
        private const float Middle = 0.56f;           // the body's middle on a figure of size 1

        private delegate Vector3 BodyPosFn(PawnRenderer renderer, Vector3 drawLoc, PawnPosture posture, out bool showBody);

        private static BodyPosFn bodyPos;
        private static bool posPatched, anglePatched;
        private static volatile int active = -1;   // -1 not decided yet, 0 off, 1 on (also read on the game's worker threads)
        private static readonly object deciding = new object();

        public static bool Active
        {
            get
            {
                if (active < 0)
                {
                    Decide();
                }
                return active == 1;
            }
        }

        // Decided once, on first use (after every mod has loaded); the game's worker threads may ask at the same time.
        private static void Decide()
        {
            lock (deciding)
            {
                if (active >= 0)
                {
                    return;
                }
                int on = 0;
                try
                {
                    MethodInfo pos = AccessTools.Method(typeof(PawnRenderer), "GetBodyPos");
                    MethodInfo angle = AccessTools.Method(typeof(PawnRenderer), "BodyAngle");
                    posPatched = PatchedByOthers(pos);
                    anglePatched = PatchedByOthers(angle);
                    if (posPatched)
                    {
                        bodyPos = AccessTools.MethodDelegate<BodyPosFn>(pos);
                    }
                    on = posPatched || anglePatched ? 1 : 0;
                    if (on == 1)
                    {
                        ModLog.Note("[IsoRim] Body animations from other mods mapped onto 3D figures (" + (posPatched ? "GetBodyPos " : "") + (anglePatched ? "BodyAngle" : "") + ").");
                    }
                }
                catch (Exception ex)
                {
                    on = 0;
                    Log.ErrorOnce("[IsoRim] Looking for other mods' body animations failed: " + ex, 93611506);
                }
                active = on;
            }
        }

        private static bool PatchedByOthers(MethodInfo m)
        {
            if (m == null)
            {
                return false;
            }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(m);
            if (info == null)
            {
                return false;
            }
            foreach (HarmonyLib.Patch p in info.Prefixes)
            {
                if (p.owner != IsoRim.Patches.HarmonyId)
                {
                    return true;
                }
            }
            foreach (HarmonyLib.Patch p in info.Postfixes)
            {
                if (p.owner != IsoRim.Patches.HarmonyId)
                {
                    return true;
                }
            }
            foreach (HarmonyLib.Patch p in info.Transpilers)
            {
                if (p.owner != IsoRim.Patches.HarmonyId)
                {
                    return true;
                }
            }
            return false;
        }

        // The figure's pose (figure space, before its turn by yaw and its size) with the animation put on: pose unchanged
        // when there is none.
        public static Matrix4x4 Apply(Pawn pawn, Vector3 drawLoc, float yaw, float size, Matrix4x4 pose)
        {
            if (!Active)
            {
                return pose;
            }
            Vector3 shift;
            float turn;
            if (!Read(pawn, drawLoc, out shift, out turn) || (shift.sqrMagnitude < 1e-8f && Mathf.Abs(turn) < 0.01f))
            {
                return pose;
            }
            if (Probe.On)
            {
                Probe.LeanSample(pawn, turn, ForwardLean(pawn.Rotation, turn));   // (harness: leancheck)
            }
            Vector3 right = Quaternion.AngleAxis(View3D.Yaw, Vector3.up) * Vector3.right;
            float k = Picture * size;
            Vector3 move = right * (shift.x * k) + Vector3.up * (shift.z * k);
            float middle = Middle * size;
            // The sprite's turn is a lean the figure makes along its own facing, forward or back, as much as the sprite
            // turns (a turn in the camera's view leaned the figure sideways whenever it faced away from the screen's sides).
            Matrix4x4 lean = Matrix4x4.Translate(new Vector3(0f, middle, 0f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(ForwardLean(pawn.Rotation, turn), Vector3.right))
                * Matrix4x4.Translate(new Vector3(0f, -middle, 0f));
            Quaternion facing = Quaternion.AngleAxis(yaw, Vector3.up);
            return Matrix4x4.Rotate(Quaternion.Inverse(facing)) * Matrix4x4.Translate(move) * Matrix4x4.Rotate(facing) * lean * pose;
        }

        // The sprite's turn (degrees clockwise) as a lean forward (positive) along the pawn's facing. Such mods draw a
        // pawn facing east as the model: clockwise is a lean forward; facing west the turn is mirrored; facing south and
        // north the sprite only tilts sideways, mirrored for north (Yayo's Animation: Core.Ani), so the same holds.
        public static float ForwardLean(Rot4 facing, float turn)
        {
            return facing == Rot4.West || facing == Rot4.North ? -turn : turn;
        }

        // The same for a crawling pawn (Crawl), whose sprite lies on the ground: what the patched methods add to the game's
        // own answers (the drawn position; CrawlingBodyAngle of its facing) as a shift over the ground (cells) and a turn
        // about the vertical (degrees clockwise from above). False when there is none.
        public static bool ReadCrawl(Pawn pawn, Vector3 drawLoc, out Vector3 shift, out float turn)
        {
            shift = Vector3.zero;
            turn = 0f;
            PawnRenderer renderer = pawn.Drawer != null ? pawn.Drawer.renderer : null;
            if (!Active || renderer == null)
            {
                return false;
            }
            try
            {
                if (posPatched)
                {
                    bool show;
                    shift = bodyPos(renderer, drawLoc, pawn.GetPosture(), out show) - drawLoc;   // the game: drawLoc
                    shift.y = 0f;
                }
                if (anglePatched)
                {
                    turn = Mathf.DeltaAngle(PawnRenderUtility.CrawlingBodyAngle(pawn.Rotation), renderer.BodyAngle(PawnRenderFlags.None));
                }
                return shift.sqrMagnitude > 1e-8f || Mathf.Abs(turn) > 0.01f;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading another mod's body animation failed: " + e, 93611507);
                return false;
            }
        }

        // What the patched methods add to the game's own answers for a standing pawn: shift (x across, z up the picture,
        // cells) and turn (degrees clockwise).
        public static bool Read(Pawn pawn, Vector3 drawLoc, out Vector3 shift, out float turn)
        {
            shift = Vector3.zero;
            turn = 0f;
            PawnRenderer renderer = pawn.Drawer != null ? pawn.Drawer.renderer : null;
            if (renderer == null)
            {
                return false;
            }
            try
            {
                if (posPatched)
                {
                    bool show;
                    shift = bodyPos(renderer, drawLoc, PawnPosture.Standing, out show) - drawLoc;   // the game: drawLoc
                    shift.y = 0f;
                }
                if (anglePatched)
                {
                    turn = Mathf.DeltaAngle(0f, renderer.BodyAngle(PawnRenderFlags.None));   // the game: 0 standing
                }
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading another mod's body animation failed: " + e, 93611507);
                return false;
            }
        }
    }
}
