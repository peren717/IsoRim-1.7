using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Camera pose sources (Api 66, Api.PushViewPose): another mod's camera (a free-flying camera, a photo, a cinematic)
    // decides where the 3D view looks from, for as long as it is pushed. One central switch in View3D: ApplyCamera takes
    // the pose resolved here (else first person, else the player's own camera) and derives every view-dependent value from
    // it (camera matrices, culling, view globals, lighting overlay pull, upright cards, sky and fog, pointer rays, labels),
    // Pitch and Yaw report it, Turning is true while it holds (nothing built for the facing is rebuilt). The player's own
    // settings and turn state are never written: when the last pose goes, the view is the player's again.
    //
    // Order: the highest Priority first, then the newest push. A source whose callback returns null gives way to the
    // next one that frame. A callback that throws or returns an unusable pose: one warning, that source is removed.
    // Nothing pushed: Resolve returns on its first lines and the view is exactly as before.
    internal static class ViewPoses
    {
        private sealed class Source : IDisposable
        {
            public object owner;
            public Func<ViewPose?> pose;
            public bool blockPlayer;
            public bool countsAsTurning;
            public int priority;
            public long order;
            public bool removed;
            public bool usedLast;   // its pose was the one applied last frame (or it was just pushed)

            public void Dispose()
            {
                Remove(this);
            }
        }

        private static readonly List<Source> sources = new List<Source>();
        private static long nextOrder;

        // The pose applied this frame (Resolve); only valid while Active.
        public static bool Active;
        public static ViewPose Pose;
        public static object Owner;
        private static bool countsAsTurning;

        // Harness/diagnostics: frames a pose was applied.
        public static int AppliedFrames;

        public static int Count
        {
            get { return sources.Count; }
        }

        public static bool CountsAsTurning
        {
            get { return Active && countsAsTurning; }
        }

        // The player's camera keys and free look are left alone this frame (HandleKeys runs before this frame's Resolve, so
        // it asks the sources as they stood: one that held the view last frame or was pushed since, and blocks).
        public static bool BlocksPlayer
        {
            get
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    Source s = sources[i];
                    if (s.blockPlayer && s.usedLast)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public static IDisposable Push(object owner, Func<ViewPose?> pose, ViewPoseOptions options)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }
            if (pose == null)
            {
                throw new ArgumentNullException(nameof(pose));
            }
            ViewPoseOptions o = options ?? new ViewPoseOptions();
            Source s = new Source
            {
                owner = owner,
                pose = pose,
                blockPlayer = o.BlockPlayerCamera,
                countsAsTurning = o.CountsAsTurning,
                priority = o.Priority,
                order = ++nextOrder,
                usedLast = true
            };
            // Kept sorted: highest priority first, newest first within a priority.
            int at = 0;
            while (at < sources.Count && (sources[at].priority > s.priority))
            {
                at++;
            }
            sources.Insert(at, s);
            return s;
        }

        public static int RemoveOwner(object owner)
        {
            int n = 0;
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                if (Equals(sources[i].owner, owner))
                {
                    sources[i].removed = true;
                    sources.RemoveAt(i);
                    n++;
                }
            }
            return n;
        }

        private static void Remove(Source s)
        {
            if (s.removed)
            {
                return;
            }
            s.removed = true;
            sources.Remove(s);
        }

        // A new game: no pose from the last one is kept.
        public static void Reset()
        {
            for (int i = 0; i < sources.Count; i++)
            {
                sources[i].removed = true;
            }
            sources.Clear();
            Active = false;
            Owner = null;
        }

        // Once a frame before the camera turns (View3D.TickInner), and when the view is set up for a switch-on: asks the
        // sources in order, the first pose wins.
        public static void Resolve()
        {
            if (sources.Count == 0)
            {
                Active = false;
                Owner = null;
                return;
            }
            Active = false;
            Owner = null;
            if (!View3D.Active)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    sources[i].usedLast = false;
                }
                return;
            }
            for (int i = 0; i < sources.Count; i++)
            {
                Source s = sources[i];
                s.usedLast = false;
                if (Active)
                {
                    continue;
                }
                ViewPose? got;
                try
                {
                    got = s.pose();
                }
                catch (Exception e)
                {
                    Log.Warning("[IsoRim] A camera pose from " + s.owner + " failed and is removed: " + e);
                    Remove(s);
                    i--;
                    continue;
                }
                if (!got.HasValue)
                {
                    continue;
                }
                ViewPose p;
                if (!Clean(got.Value, out p))
                {
                    Log.Warning("[IsoRim] A camera pose from " + s.owner + " is not usable (not a number, or out of range) and is removed.");
                    Remove(s);
                    i--;
                    continue;
                }
                Active = true;
                Pose = p;
                Owner = s.owner;
                countsAsTurning = s.countsAsTurning;
                s.usedLast = true;
            }
            if (Active)
            {
                AppliedFrames++;
            }
        }

        // The pose in IsoRim's ranges: yaw 0-360, pitch -89..89 for perspective (1..89 orthographic), sizes non-negative.
        internal static bool Clean(ViewPose v, out ViewPose p)
        {
            p = v;
            if (!Finite(v.Yaw) || !Finite(v.Pitch) || !Finite(v.Fov) || !Finite(v.OrthoSize) || !Finite(v.FocusDistance) || !Finite(v.Near) || !Finite(v.Far))
            {
                return false;
            }
            if (v.Eye.HasValue && !Finite(v.Eye.Value))
            {
                return false;
            }
            if (v.Focus.HasValue && !Finite(v.Focus.Value))
            {
                return false;
            }
            p.Yaw = Mathf.Repeat(v.Yaw, 360f);
            p.Pitch = v.Perspective ? Mathf.Clamp(v.Pitch, -89f, 89f) : Mathf.Clamp(v.Pitch, 1f, 89f);
            p.Fov = v.Fov > 0f ? Mathf.Clamp(v.Fov, 5f, 150f) : 0f;
            p.OrthoSize = Mathf.Max(0f, v.OrthoSize);
            p.FocusDistance = Mathf.Max(0f, v.FocusDistance);
            p.Near = Mathf.Max(0f, v.Near);
            p.Far = Mathf.Max(0f, v.Far);
            if (p.Far > 0f && p.Near > 0f && p.Far <= p.Near)
            {
                return false;
            }
            return true;
        }

        private static bool Finite(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
        }

        private static bool Finite(Vector3 v)
        {
            return Finite(v.x) && Finite(v.y) && Finite(v.z);
        }

        // ---- BeforeViewUpdate: once a frame where the camera's inputs are read, before the pose is resolved.

        private static readonly List<Action> before = new List<Action>();
        private static readonly List<Action> beforeRun = new List<Action>();

        public static void AddBefore(Action a)
        {
            if (a != null)
            {
                before.Add(a);
            }
        }

        public static void RemoveBefore(Action a)
        {
            if (a != null)
            {
                before.Remove(a);
            }
        }

        public static void RaiseBefore()
        {
            if (before.Count == 0)
            {
                return;
            }
            // A copy, so a handler may add or remove handlers (or itself) while running.
            beforeRun.Clear();
            beforeRun.AddRange(before);
            for (int i = 0; i < beforeRun.Count; i++)
            {
                Action a = beforeRun[i];
                try
                {
                    a();
                }
                catch (Exception e)
                {
                    Log.Warning("[IsoRim] A BeforeViewUpdate handler (" + (a.Method != null ? a.Method.DeclaringType + "." + a.Method.Name : "?") + ") failed and is removed: " + e);
                    before.Remove(a);
                }
            }
        }
    }
}
