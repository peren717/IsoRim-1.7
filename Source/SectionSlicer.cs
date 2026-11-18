using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Blocks of the map that come into the 3D view with layers waiting are rebuilt a few a frame, not all in one frame.
    //
    // The game rebuilds a block that changed off screen at the moment it is drawn again (Section.DrawSection calls
    // Section.RegenerateDirtyLayers). A zoom out or a jump of the camera brings many such blocks into view at once, and in
    // the 3D view one block costs milliseconds (models, trees): a stall of up to a second. Here that call is held back once
    // the frame has spent its rebuild time (the frame's budget): the block is drawn as last built, its "dirty" mark stays,
    // and the game's own draw asks again next frame. Blocks held are rebuilt at the start of the next frames' map draw,
    // those nearest the centre of the screen first, by the game's own RegenerateDirtyLayers (called as it is, with every
    // hook on it). Only the moment of the rebuild moves: the rebuild is the game's, so what it builds is what it would
    // have built at once for the same state of the map.
    //
    // Every frame makes progress: at least one waiting block is rebuilt each frame, whatever the game's own rebuild of
    // changes in view already cost that frame (a fire or a fight in view rebuilds blocks every frame: without this the
    // waiting blocks could wait for as long as that lasts).
    //
    // What is never held back:
    // - a change inside the view: the game rebuilds it in Section.TryUpdate before the draw, as always. A block held here
    //   that gets a new change is rebuilt there in full (TryUpdate rebuilds every layer still marked dirty and clears the
    //   block's mark), and is then no longer asked for;
    // - everything outside the shown 3D view's own map draw: the 2D view, a photo's draws (Photos draws waiting blocks as
    //   last built on its own; the blocks held for the shown view are finished first, Finish), the gravship's capture and
    //   cutscene, and any other caller of the game's rebuilds (MapSections, RegenerateEverythingNow);
    // - while a picture of the shown view is being made (SceneImage) and in test harness runs (the probe's timed shots,
    //   -isorimseed's repeatable runs: a time budget makes the frame a block is rebuilt in follow the machine's speed),
    //   unless the harness asks for a fixed number of blocks a frame (-isorimslice=N, SliceTestArg: this class's own path
    //   with a count in place of the clock, so a seeded run repeats);
    // - when another mod patches Section.RegenerateDirtyLayers (ForeignPatches): a block held back would run that mod's
    //   hooks every frame without the game's code. Off for the session then, said once in the log.
    //
    // Only world changes reach a block this way (the game's change flags, or IsoRim's own marks of one layer): what a held
    // block shows is the map as it was when the block was last in view. Changes of the view itself (3D on or off,
    // settings, the camera's turn, floors) never wait on it: they rebuild through RegenerateEverythingNow or MapSections.
    //
    // Errors. An exception in this class's own bookkeeping turns it off for the session (the game's immediate rebuild is
    // back), said once. An exception the game's rebuild throws while this class calls it (RebuildHeld, Finish) is not
    // ours and is neither reported nor kept here: the block gets its "dirty" mark back (the game's body clears it first)
    // and the game's own call for it at the draw is let through unheld, where it throws the way it does without IsoRim.
    internal static class SectionSlicer
    {
        // The two tuning values (our own, perfslice 2026-11-17).
        // BudgetMs: rebuild time a frame may spend on blocks coming into view before the rest wait for the next frame, in
        // milliseconds. A block is started only while the frame's rebuilds (the game's own of changes in view included)
        // have taken less than the frame's budget, so a frame can run over by one block (a forest block takes 7 to 20
        // ms). Measured at 165 Hz: 3 ms gives hardly shorter frames (one forest block a frame is the floor) and a
        // catch-up a quarter longer, 10 ms frames a fifth longer.
        // FrameShare: a slow frame rate gets more. The frame's budget is the larger of BudgetMs and this share of what
        // the last frames took outside their rebuilds (the smaller of the last two, so one hitch does not echo), so the
        // catch-up takes less real time at a low frame rate. Rebuild time is left out of that measure: a bigger budget
        // cannot feed itself. 0.2 leaves BudgetMs in charge up to 30 ms of other work a frame: the catch-up's frames
        // at 165 Hz have 17 to 19 ms of it, 22 to 26 with the game at triple speed. Measured (perfslice round 2, same
        // save): with the game's frame cap at 20 the catch-up took 2.35 s without the share, 2.15 s with 0.2 and 1.5 s
        // with 0.3; but with 0.3 the two slow frames at the start of a zoom out (figures built at first sight) bought
        // the next two frames a bigger budget, and the first flick at 165 Hz had a frame or two more over 33 ms. 0.2
        // leaves that flick as it was.
        private const double BudgetMs = 6.0;
        private const double FrameShare = 0.2;
        private static readonly long Budget = (long)(BudgetMs * Stopwatch.Frequency / 1000.0);

        private static bool failed;    // off for the session (Fail: our own error; Stop: another mod's patch)
        private static bool slicing;   // the shown view's map draw is running and may hold rebuilds back
        private static bool own;       // our own call of the game's rebuild is running (never held)
        private static long spent;     // Stopwatch ticks this frame's rebuilds have taken (changes in view, then waiting blocks)
        private static long budget;    // this frame's budget, in Stopwatch ticks
        private static int done;       // waiting blocks rebuilt this frame (by the draw's own call or by ours)
        private static long updateStart, updateTicks;   // the game's rebuild of changes in view, this frame (BeginUpdate, EndUpdate)
        private static int updateFrame = -1;
        private static long drawStart, drawUpdate;      // this frame's BeginDraw, and the update time it began with
        private static int drawFrame = -1;
        private static long otherTicks, otherBefore;    // the last frame's time outside its rebuilds, and the one before
        private static readonly List<Section> held = new List<Section>();   // held back by this frame's draw
        // Blocks whose rebuild threw inside our own call: the game's own call for them is let through, once.
        private static readonly HashSet<Section> unheld = new HashSet<Section>();
        private static Section[] queue = new Section[64];
        private static float[] keys = new float[64];
        private static bool looked;
        private static MethodInfo target;                      // Section.RegenerateDirtyLayers
        private static Action<Section> rebuild;                // the same, as a call
        private static Func<MapDrawer, CellRect> viewRectOf;   // MapDrawer.ViewRect (what its draw tests blocks against)
        private static readonly ForeignPatches.Schedule looks = new ForeignPatches.Schedule();
        private static bool alone;     // the last look found no other mod's patch on the game's rebuild

        // The block whose waiting layers the game is rebuilding now (Section.RegenerateDirtyLayers, from its draw or
        // from here), else null. Cull reads it to measure only that block again.
        internal static Section Rebuilding;

        // Harness counters: blocks held back by the last draw, blocks rebuilt here so far, the time that took.
        public static int Held, Rebuilt;
        public static long Ticks;

        // Test harnesses take pictures a fixed number of frames after a camera move; -isorimseed runs must repeat. With
        // -isorimslice=N the harness asks for this class's own path with a count in place of the clock.
        private static bool Harness
        {
            get { return (Probe.On || QuickTestSeedArg.Value != null) && SliceTestArg.Blocks == null; }
        }

        private static bool Wanted
        {
            get
            {
                return View3D.Ready && !Harness && !SceneImage.Busy
                    && !WorldComponent_GravshipController.CutsceneInProgress && !WorldComponent_GravshipController.GravshipRenderInProgess;
            }
        }

        // Whether one more waiting block may be started this frame: the first always (progress every frame), then while
        // the frame's budget lasts; or, for the harness, exactly its number of blocks.
        private static bool MayStart
        {
            get
            {
                int? blocks = SliceTestArg.Blocks;
                return blocks != null ? done < blocks.Value : done == 0 || spent < budget;
            }
        }

        // MapDrawer.MapMeshDrawerUpdate_First begins and ends (prefix, finalizer): the game rebuilds the changes in view
        // there, right before the draw of the same frame. A block held here that gets a new change is rebuilt there in
        // full, so that time comes out of this frame's budget: without it such a frame did both.
        public static void BeginUpdate()
        {
            updateStart = Stopwatch.GetTimestamp();
        }

        public static void EndUpdate()
        {
            updateTicks = Stopwatch.GetTimestamp() - updateStart;
            updateFrame = Time.frameCount;
        }

        // MapDrawer.DrawMapMesh begins (prefix).
        public static void BeginDraw(MapDrawer drawer)
        {
            if (View3D.InPhoto)
            {
                return;   // a photo's own draw of the map: nothing is rebuilt in it (Photos)
            }
            slicing = false;
            if (failed)
            {
                return;
            }
            try
            {
                if (!Wanted || !Alone())
                {
                    Forget();   // the game's draw rebuilds every waiting block it draws, at once
                    return;
                }
                int frame = Time.frameCount;
                long now = Stopwatch.GetTimestamp();
                long update = updateFrame == frame ? updateTicks : 0;
                Pace(frame, now, update);
                spent = update;
                done = 0;
                RebuildHeld(drawer);
                slicing = true;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // This frame's budget (see FrameShare). The time from the last frame's BeginDraw to this one's holds that
        // frame's waiting-block rebuilds (in its draw) and this frame's rebuild of changes in view (in its update): both
        // are taken out, the rest is what the game and the picture cost.
        private static void Pace(int frame, long now, long update)
        {
            bool follows = drawFrame == frame - 1;
            long other = follows ? now - drawStart - (spent - drawUpdate) - update : 0;
            otherBefore = follows ? otherTicks : 0;
            otherTicks = other > 0 ? other : 0;
            drawFrame = frame;
            drawStart = now;
            drawUpdate = update;
            budget = Math.Max(Budget, (long)(Math.Min(otherTicks, otherBefore) * FrameShare));
        }

        // MapDrawer.DrawMapMesh has ended (finalizer).
        public static void EndDraw()
        {
            if (View3D.InPhoto)
            {
                return;
            }
            slicing = false;
            Held = held.Count;
        }

        // Section.RegenerateDirtyLayers is about to run (prefix): false holds it back for this frame.
        public static bool Allow(Section section, out long started)
        {
            started = 0;
            bool back = unheld.Count > 0 && unheld.Remove(section);
            if (!slicing || own || back)
            {
                Rebuilding = section;
                return true;
            }
            if (MayStart)
            {
                started = Stopwatch.GetTimestamp();
                Rebuilding = section;
                return true;
            }
            held.Add(section);
            return false;
        }

        // Section.RegenerateDirtyLayers has run or was held back (finalizer; started as Allow gave it).
        public static void Done(long started)
        {
            Rebuilding = null;
            if (started != 0)
            {
                Count(Stopwatch.GetTimestamp() - started);
            }
        }

        private static void Count(long ticks)
        {
            spent += ticks;
            done++;
            Ticks += ticks;
            Rebuilt++;
        }

        // Before a photo is drawn: the blocks the shown view's draw held back this frame are rebuilt now, so the photo
        // sees what the game would have built for the view (blocks outside the view stay as last built, as always).
        internal static void Finish()
        {
            if (failed || held.Count == 0)
            {
                return;
            }
            try
            {
                for (int i = 0; i < held.Count; i++)
                {
                    Rebuild(held[i]);
                }
                held.Clear();
                Held = 0;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // Nothing is kept of the blocks held back or handed back: the game is not in play (Patches.RootUpdate_Postfix sees
        // that every frame, a save being loaded included, so a map that is gone is not kept alive from here), or this
        // class is not wanted this frame. The blocks keep their own marks; the draw asks for them again.
        internal static void Forget()
        {
            if (held.Count > 0)
            {
                held.Clear();
            }
            if (unheld.Count > 0)
            {
                unheld.Clear();
            }
            Held = 0;
            drawFrame = -1;
        }

        // The blocks the last draw held back: those the game is about to draw again and that still wait, nearest the
        // centre of the screen first, while the frame's budget lasts (one at least). The rest are asked for again by the
        // draw itself.
        private static void RebuildHeld(MapDrawer drawer)
        {
            int n = held.Count;
            if (n == 0)
            {
                return;
            }
            Look();
            if (queue.Length < n)
            {
                queue = new Section[n * 2];
                keys = new float[n * 2];
            }
            CellRect view = viewRectOf != null ? viewRectOf(drawer) : default(CellRect);
            Vector3 centre = View3D.ScreenToGround(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                Section s = held[i];
                if (s.map == null || s.map.mapDrawer != drawer || !Cull.anyDirtyOf(s))
                {
                    continue;   // another map is shown, or a change in view has rebuilt it since (Section.TryUpdate)
                }
                Vector3 shift;
                if (viewRectOf != null ? !Cull.Drawn(ref view, s, out shift) : !Floors.SectionDrawShift(s, out shift))
                {
                    continue;   // no longer drawn (out of view, or its floor is hidden now): it waits until it is, as always
                }
                CellRect r = s.CellRect;
                float dx = (r.minX + r.maxX + 1) * 0.5f + shift.x - centre.x;
                float dz = (r.minZ + r.maxZ + 1) * 0.5f + shift.z - centre.z;
                queue[m] = s;
                keys[m] = dx * dx + dz * dz;
                m++;
            }
            held.Clear();
            Array.Sort(keys, queue, 0, m);
            for (int i = 0; i < m; i++)
            {
                if (MayStart)
                {
                    Rebuild(queue[i]);
                }
                queue[i] = null;   // (the others are asked for again by the draw, and held again there)
            }
        }

        // The game's own rebuild of a block's waiting layers, as its draw would have called it.
        private static void Rebuild(Section section)
        {
            Look();
            if (rebuild == null || !Cull.anyDirtyOf(section))
            {
                return;   // (not found: nothing was held either, Allow is never asked)
            }
            long t0 = Stopwatch.GetTimestamp();
            own = true;
            try
            {
                rebuild(section);
            }
            catch (Exception)
            {
                // The game's rebuild threw (its body; this class's own part of the call, Allow and Done, cannot). Not
                // ours to report or to keep: the block waits again and the game's own call for it runs unheld (Allow),
                // where the same error leaves the draw the way it does without IsoRim.
                Cull.anyDirtyOf(section) = true;
                unheld.Add(section);
                return;
            }
            finally
            {
                own = false;
            }
            Count(Stopwatch.GetTimestamp() - t0);
        }

        // No other mod's patch of any kind is on the game's rebuild (looked at when ForeignPatches.Schedule says).
        private static bool Alone()
        {
            if (!looks.Due())
            {
                return alone;
            }
            alone = false;
            if (!LookNow(out string who))
            {
                Stop("another mod patches Section.RegenerateDirtyLayers (" + who + ")");
                return false;
            }
            alone = true;
            return true;
        }

        // One look (also the harness's: the perf runner measures what a look costs).
        public static bool LookNow(out string who)
        {
            Look();
            who = ForeignPatches.First(target, ForeignPatches.Kind.All);
            return who == null;
        }

        private static void Look()
        {
            if (looked)
            {
                return;
            }
            looked = true;
            target = AccessTools.Method(typeof(Section), "RegenerateDirtyLayers");
            rebuild = target != null ? AccessTools.MethodDelegate<Action<Section>>(target) : null;
            MethodInfo view = AccessTools.PropertyGetter(typeof(MapDrawer), "ViewRect");
            viewRectOf = view != null ? AccessTools.MethodDelegate<Func<MapDrawer, CellRect>>(view) : null;
        }

        // Not an error: something of another mod's that holding the game's rebuild back would change.
        private static void Stop(string why)
        {
            failed = true;
            slicing = false;
            Forget();
            Log.Message("[IsoRim] Map blocks coming into view are rebuilt at once this session, as without IsoRim: " + why + ".");
        }

        private static void Fail(Exception e)
        {
            failed = true;
            slicing = false;
            own = false;
            Forget();
            Log.ErrorOnce("[IsoRim] Rebuilding map blocks a few a frame failed and is off until the game restarts (blocks are rebuilt at once, as before): " + e, 93611303);
        }
    }
}
