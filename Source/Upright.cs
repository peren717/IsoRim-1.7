using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The addon drawing scopes, one stack (Api 12: upright; Api 20: flat transform and flat skip). While a scope is open,
    // the flat 2D meshes IsoRim routes through DrawHook (fillable bars, Graphic and GenDraw.DrawMeshNowOrLater draws,
    // Api.DrawMesh) are changed: stood up as one card facing the camera at a given height, as IsoRim stands up its own
    // status icons and bubbles (upright: DrawHook.billboard with a pivot); drawn with a given matrix on top of their own
    // (transform: DrawHook.xform); or not drawn at all (skip: DrawHook.skip). Each Begin saves DrawHook's state and each
    // End puts it back, so scopes nest and leave whatever was in charge before (a thing's lift) as it was. A scope left
    // open is closed when the thing whose drawing opened it is done (Patches.ThingDraw_Prefix / Scope_Finalizer) and,
    // outside a thing's drawing, at the next frame (Begin, and DrawDynamicThings). Costs nothing when no addon opens one.
    internal static class UprightScopes
    {
        private struct Saved
        {
            public int id;
            public bool changed;
            public bool active, billboard, anchored, pivoted, pivotOnFirst, pointer, skip;
            public float height;
            public Matrix4x4 xform;
            public Vector3 anchor, pivot;
        }

        private static readonly List<Saved> stack = new List<Saved>();
        private static int openedFrame = -1;
        private static int lastId;

        public static int Depth => stack.Count;

        // Opens an upright scope; returns its handle (UprightScope).
        public static UprightScope Begin(float height, bool hasPivot, Vector3 pivot)
        {
            bool changed = Push(out int level, out int id);
            if (changed)
            {
                DrawHook.active = true;
                DrawHook.billboard = true;
                DrawHook.billboardHeight = height;
                DrawHook.hasAnchor = false;
                DrawHook.hasPointerAt = false;
                DrawHook.hasPivot = hasPivot;
                DrawHook.billboardPivot = pivot;
                DrawHook.pivotOnFirst = !hasPivot;   // without a pivot: hinged on the first mesh drawn (DrawHook.Transform)
            }
            return new UprightScope(level, id);
        }

        // (Api 20) Opens a flat transform scope: matrix (drawn space) applied on top of each mesh's own, replacing an outer
        // upright scope or a thing's lift until it ends.
        public static FlatScope BeginTransform(Matrix4x4 matrix)
        {
            bool changed = Push(out int level, out int id);
            if (changed)
            {
                DrawHook.active = true;
                DrawHook.billboard = false;
                DrawHook.hasAnchor = false;
                DrawHook.hasPivot = false;
                DrawHook.pivotOnFirst = false;
                DrawHook.hasPointerAt = false;
                DrawHook.xform = matrix;
            }
            return new FlatScope(level, id);
        }

        // (Api 20) Opens a flat skip scope: nothing routed through DrawHook is drawn until it ends.
        public static FlatScope BeginSkip()
        {
            bool changed = Push(out int level, out int id);
            if (changed)
            {
                DrawHook.skip = true;
            }
            return new FlatScope(level, id);
        }

        // Saves DrawHook's state as a new stack entry; true when the scope changes anything (3D view on, main thread).
        private static bool Push(out int level, out int id)
        {
            if (stack.Count > 0 && openedFrame != Time.frameCount)
            {
                Leaked();
            }
            if (stack.Count == 0)
            {
                openedFrame = Time.frameCount;
            }
            Saved s = default(Saved);
            s.id = ++lastId;
            if (View3D.Ready && UnityData.IsInMainThread)
            {
                s.changed = true;
                s.active = DrawHook.active;
                s.xform = DrawHook.xform;
                s.billboard = DrawHook.billboard;
                s.height = DrawHook.billboardHeight;
                s.anchored = DrawHook.hasAnchor;
                s.anchor = DrawHook.billboardAnchor;
                s.pivoted = DrawHook.hasPivot;
                s.pivot = DrawHook.billboardPivot;
                s.pivotOnFirst = DrawHook.pivotOnFirst;
                s.pointer = DrawHook.hasPointerAt;
                s.skip = DrawHook.skip;
            }
            stack.Add(s);
            level = stack.Count;
            id = s.id;
            return s.changed;
        }

        // A handle's scope (and any opened inside it), if it is still open.
        public static void Close(int level, int id)
        {
            if (level > 0 && stack.Count >= level && stack[level - 1].id == id)
            {
                UnwindTo(level - 1);
            }
        }

        // Closes the innermost scope (none open: nothing).
        public static void End()
        {
            if (stack.Count > 0)
            {
                UnwindTo(stack.Count - 1);
            }
        }

        // Closes scopes until depth are left open, innermost first.
        public static void UnwindTo(int depth)
        {
            while (stack.Count > depth && stack.Count > 0)
            {
                Saved s = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (s.changed)
                {
                    DrawHook.active = s.active;
                    DrawHook.xform = s.xform;
                    DrawHook.billboard = s.billboard;
                    DrawHook.billboardHeight = s.height;
                    DrawHook.hasAnchor = s.anchored;
                    DrawHook.billboardAnchor = s.anchor;
                    DrawHook.hasPivot = s.pivoted;
                    DrawHook.billboardPivot = s.pivot;
                    DrawHook.pivotOnFirst = s.pivotOnFirst;
                    DrawHook.hasPointerAt = s.pointer;
                    DrawHook.skip = s.skip;
                }
            }
        }

        // A scope a thing's drawing opened and did not close (the thing's draw scope ends): closed with a warning.
        public static void CloseFrom(int depth)
        {
            if (stack.Count > depth)
            {
                Log.WarningOnce("[IsoRim] A drawing scope (Api.BeginUpright, BeginFlatTransform or BeginSkipFlat) was left open by a thing's drawing; IsoRim closed it.", 93611204);
                UnwindTo(depth);
            }
        }

        // Once a frame, before the things are drawn: a scope still open from an earlier frame is closed.
        public static void FrameCheck()
        {
            if (stack.Count > 0 && openedFrame != Time.frameCount)
            {
                Leaked();
            }
        }

        private static void Leaked()
        {
            Log.WarningOnce("[IsoRim] A drawing scope (Api.BeginUpright, BeginFlatTransform or BeginSkipFlat) was left open past the end of a frame; IsoRim closed it.", 93611205);
            UnwindTo(0);
        }
    }

    /// <summary>
    /// An open upright scope (Api 12, <see cref="Api.Upright(float)"/>): disposing it closes the scope and any opened
    /// inside it. Disposing it again, or after IsoRim closed the scope, does nothing.
    /// </summary>
    public readonly struct UprightScope : System.IDisposable
    {
        private readonly int level, id;

        internal UprightScope(int level, int id)
        {
            this.level = level;
            this.id = id;
        }

        public void Dispose()
        {
            UprightScopes.Close(level, id);
        }
    }

    /// <summary>
    /// An open flat transform or flat skip scope (Api 20, <see cref="Api.FlatTransform"/>, <see cref="Api.SkipFlat"/>):
    /// disposing it closes the scope and any opened inside it. Disposing it again, or after IsoRim closed the scope, does
    /// nothing.
    /// </summary>
    public readonly struct FlatScope : System.IDisposable
    {
        private readonly int level, id;

        internal FlatScope(int level, int id)
        {
            this.level = level;
            this.id = id;
        }

        public void Dispose()
        {
            UprightScopes.Close(level, id);
        }
    }
}
