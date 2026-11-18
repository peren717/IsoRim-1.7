using System;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace IsoRim
{
    // Other mods' Harmony patches on a method whose body IsoRim skips: Section.RegenerateDirtyLayers held back for a frame
    // (SectionSlicer) and LayerSubMesh.FinalizeMesh written by IsoRim itself (MeshFill). Skipping a body is only right
    // while nothing of another mod's depends on it running, so both ask here and turn themselves off for the session when
    // something does. One place for the question, so both answer it the same way.
    //
    // "Another mod's" is a patch whose method is not in IsoRim's own assembly (IsoRim patches under several Harmony ids,
    // so the id says nothing).
    //
    // When to look (Schedule.Due): at first use, then every RecheckSeconds of real time, and after a map was built (a
    // load, a new map: MapBuilt). Patches are added at startup in practice, and a look is not free: Harmony reads the
    // method's patch record from a byte array each time (measured 2026-11-17 on the user's mod list, see the hand-back
    // of perfslice round 2 for the figures), which is why it is not done every frame.
    internal static class ForeignPatches
    {
        // Real seconds between two looks at the same method. Our own value: short enough that a patch added in play (a
        // mod's late setup, a dev tool) is seen within moments, long enough that the look's cost and garbage vanish.
        internal const float RecheckSeconds = 5f;

        private static int builds;   // maps built so far (written from any thread)

        // A map's drawing is about to be built (MapDrawer.RegenerateEverythingNow, possibly on the loading thread).
        internal static void MapBuilt()
        {
            Interlocked.Increment(ref builds);
        }

        // When one user of this class last looked.
        internal sealed class Schedule
        {
            private bool looked;
            private float next;
            private int seen;

            // True when it is time to look again (and notes that the caller now does).
            internal bool Due()
            {
                float now = Time.realtimeSinceStartup;
                int built = Volatile.Read(ref builds);
                if (looked && now < next && built == seen)
                {
                    return false;
                }
                looked = true;
                next = now + RecheckSeconds;
                seen = built;
                return true;
            }
        }

        [Flags]
        internal enum Kind
        {
            Prefix = 1,
            Postfix = 2,
            Transpiler = 4,
            Finalizer = 8,
            Inner = 16,   // Harmony 3's patches on calls inside the body (inner prefixes, postfixes, finalizers)
            InBody = Transpiler | Inner,   // what lives in the body and is bypassed with it
            All = Prefix | Postfix | Transpiler | Finalizer | Inner
        }

        internal static bool Ours(Patch patch)
        {
            MethodInfo m = patch != null ? patch.PatchMethod : null;
            return m != null && m.DeclaringType != null && m.DeclaringType.Assembly == typeof(ForeignPatches).Assembly;
        }

        // A patch that asks Harmony whether the body ran (a parameter named __runOriginal): it gets "no" for a skipped one.
        internal static readonly Predicate<Patch> AsksRunOriginal = delegate (Patch patch)
        {
            ParameterInfo[] ps = patch.PatchMethod != null ? patch.PatchMethod.GetParameters() : null;
            for (int i = 0; ps != null && i < ps.Length; i++)
            {
                if (ps[i].Name == "__runOriginal")
                {
                    return true;
                }
            }
            return false;
        };

        // The first patch of the given kinds on a method that is not IsoRim's (and, with only, that only matches), as
        // "its Harmony id: Type.Method"; null when there is none.
        internal static string First(MethodBase method, Kind kinds, Predicate<Patch> only = null)
        {
            return First(method != null ? Harmony.GetPatchInfo(method) : null, kinds, only);
        }

        // The same on a patch record already read (Harmony.GetPatchInfo: each read costs, so one read serves every question).
        internal static string First(HarmonyLib.Patches info, Kind kinds, Predicate<Patch> only = null)
        {
            if (info == null)
            {
                return null;
            }
            string who = null;
            if ((kinds & Kind.Prefix) != 0)
            {
                who = In(info.Prefixes, only);
            }
            if (who == null && (kinds & Kind.Postfix) != 0)
            {
                who = In(info.Postfixes, only);
            }
            if (who == null && (kinds & Kind.Transpiler) != 0)
            {
                who = In(info.Transpilers, only);
            }
            if (who == null && (kinds & Kind.Finalizer) != 0)
            {
                who = In(info.Finalizers, only);
            }
            if (who == null && (kinds & Kind.Inner) != 0)
            {
                InnerLists();
                for (int i = 0; who == null && i < inner.Length; i++)
                {
                    who = In(inner[i] != null ? inner[i].GetValue(info) as ReadOnlyCollection<Patch> : null, only);
                }
            }
            return who;
        }

        private static string In(ReadOnlyCollection<Patch> patches, Predicate<Patch> only)
        {
            for (int i = 0; patches != null && i < patches.Count; i++)
            {
                Patch p = patches[i];
                if (p == null || Ours(p) || (only != null && !only(p)))
                {
                    continue;
                }
                MethodInfo m = p.PatchMethod;
                return p.owner + ": " + (m != null ? (m.DeclaringType != null ? m.DeclaringType.FullName + "." : "") + m.Name : "?");
            }
            return null;
        }

        // Harmony 3's inner patch lists, by name: a Harmony without them (an older one loaded first) has none to look at.
        private static FieldInfo[] inner;

        private static void InnerLists()
        {
            if (inner != null)
            {
                return;
            }
            string[] names = { "InnerPrefixes", "InnerPostfixes", "InnerFinalizers" };
            FieldInfo[] found = new FieldInfo[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                FieldInfo f = typeof(HarmonyLib.Patches).GetField(names[i], BindingFlags.Public | BindingFlags.Instance);
                found[i] = f != null && typeof(ReadOnlyCollection<Patch>).IsAssignableFrom(f.FieldType) ? f : null;
            }
            inner = found;
        }
    }
}
