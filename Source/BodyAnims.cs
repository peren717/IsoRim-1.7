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
    // 3D animals only when their model opts in (ModelDef.bodyAnims, 2026-11-01: legless blobs; AnimalSway); others keep their own gait.
    //
    // Such mods (Yayo's Animation, for one) patch PawnRenderer.GetBodyPos and PawnRenderer.BodyAngle, which the game uses to
    // place and turn the sprite. For a standing pawn the game's own answers are fixed: the drawn position unchanged and no
    // turn. Whatever a patched method gives on top of that is the mod's animation, read here (not copied: whatever the mod
    // works out, by whatever means) and put on the figure as the sprite shows it on screen (SpriteMotion, 2026-11-09): the
    // shift across the picture along the camera's right, up the picture as height; the sprite's turn, a turn in the picture's
    // plane, as the same turn of the figure about the line from the body's middle toward the camera, clockwise on screen as
    // the sprite's, whichever way the figure faces (no reading of what the mod meant per facing). Picture units are scaled
    // to the figure's (as its weapon).
    // When no other mod patches either method nothing is read at all (Active: decided once, after every mod has loaded).
    public static class BodyAnims
    {
        private const float Picture = FigureWeapons.Picture;   // the game's picture units to figure units
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
        public static Matrix4x4 Apply(Pawn pawn, Vector3 drawLoc, float height, float yaw, float size, Matrix4x4 pose)
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
            // (the shift in picture units, scaled to the figure's; the turn about the body's middle)
            float middle = Middle * size;
            Matrix4x4 posed = SpriteMotion.Local(yaw, new Vector2(shift.x, shift.z) * (Picture * size), turn, new Vector3(0f, middle, 0f),
                new Vector3(drawLoc.x, height + middle, drawLoc.z)) * pose;
            if (Probe.On)
            {
                Probe.LeanSample(pawn, turn, new Vector3(drawLoc.x, height, drawLoc.z), yaw, size, pose, posed);   // (harness: leancheck)
            }
            return posed;
        }

        // The same for a 3D animal whose model opts in (ModelDef.bodyAnims; Animals.Draw, standing or walking): the whole animal
        // (pose space: before its turn by yaw, already sized) moved and turned as the sprite shows it (SpriteMotion), about the
        // middle of its trunk (pivot, height over its ground). The shift goes on as it is, in cells: such mods move an animal's
        // sprite by map cells whatever its size, and the model stands at the sprite's size.
        // apply false (the model has not opted in): nothing (harness: only noted). False, sway identity, when there is nothing.
        public static bool AnimalSway(Pawn pawn, Vector3 drawLoc, float ground, float yaw, float pivot, bool apply, out Matrix4x4 sway)
        {
            sway = Matrix4x4.identity;
            if (!Active || (!apply && !Probe.On))
            {
                return false;
            }
            Vector3 shift;
            float turn;
            if (!Read(pawn, drawLoc, out shift, out turn))
            {
                return false;
            }
            if (Probe.On)
            {
                Probe.SwaySample(pawn, shift, turn, apply);   // (harness: swaycheck)
            }
            if (!apply || (shift.sqrMagnitude < 1e-8f && Mathf.Abs(turn) < 0.01f))
            {
                return false;
            }
            sway = SpriteMotion.Local(yaw, new Vector2(shift.x, shift.z), turn, new Vector3(0f, pivot, 0f), new Vector3(drawLoc.x, ground + pivot, drawLoc.z));
            return true;
        }

        // The facing other mods give a standing pawn's sprite (PawnRenderer.ParallelGetPreRenderResults' rotOverride, set in
        // their prefixes: Patches.PreRenderResults_Prefix notes it, on the game's worker threads, before IsoRim's camera turn),
        // per pawn with the frame it was noted in. A figure faces it while it lasts (People: standing, not aiming).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> facings = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();

        public static void NoteFacing(Pawn pawn, Rot4? rot)
        {
            if (rot.HasValue || facings.ContainsKey(pawn.thingIDNumber))
            {
                facings[pawn.thingIDNumber] = (rot.HasValue ? rot.Value.AsInt + 1 : 0) | (RealTime.frameCount << 3);
            }
        }

        // The facing the game draws this pawn's sprite with: another mod's while it gives one (noted a moment ago), else its own.
        public static Rot4 Facing(Pawn pawn)
        {
            int v;
            if (facings.TryGetValue(pawn.thingIDNumber, out v) && (v & 7) != 0 && RealTime.frameCount - (v >> 3) <= 3)
            {
                return new Rot4((v & 7) - 1);
            }
            return pawn.Rotation;
        }

        // The same as a turn for the figure (degrees clockwise from north), only where it differs from the pawn's own facing.
        public static float? FacingTurn(Pawn pawn)
        {
            Rot4 f = Facing(pawn);
            return f != pawn.Rotation ? f.AsAngle : (float?)null;
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
