using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace IsoRim
{
    // Weapons other mods draw themselves, on pawns drawn as 2D cards (2026-11-12, card weapons).
    //
    // The game's weapon drawing (PawnRenderUtility.DrawEquipmentAndApparelExtras, and in it DrawEquipmentAiming and
    // DrawCarriedWeapon) goes through DrawHook (Patches.RedirectDrawCalls), so on a card the weapon stands up with the card. A
    // mod that draws the weapon itself, with its own Graphics.DrawMesh, left it lying flat at the card's feet: in a patch on
    // one of those three (a prefix that skips the game's draw, a postfix, a finalizer), in a helper such a patch calls, or in
    // a helper its transpiler puts into them.
    //
    // Once every mod is loaded (the last step after the static constructors), those methods are read from Harmony's own patch
    // info: every prefix, postfix and finalizer of another owner, the methods each of them calls (one level), and the methods a
    // transpiler of another owner added to the three (calls in their current code that are not in the game's original; not
    // followed further). Their Graphics.DrawMesh calls are pointed at CardDraw by the same rule and rewriting as the game's
    // (Patches.RedirectCardDraws): while a card is drawn they are DrawHook's own draws (everything such a method draws then,
    // not only the weapon); everywhere else (3D figures, the 2D view, any other caller) they go to Graphics.DrawMesh exactly as
    // before.
    //
    // Left alone:
    //   - methods registered through Api 54 (FigureWeapons.Catches, the one registry): their own draws are Api 54's (its
    //     fallback stands them up on cards). The methods they call are looked at as any patch's helpers, so a helper is
    //     redirected the same whether the method was registered before or after the scan;
    //   - IsoRim's own methods, and methods IsoRim already rewrites (Patches.RewritesDrawing);
    //   - generic methods and methods of generic types;
    //   - anything outside the running mods' assemblies (the game's, Unity's, the runtime's).
    // A method that cannot be read or patched is skipped with one dev-mode warning naming it; the rest go on. Not covered:
    // draws other than Graphics.DrawMesh (DrawMeshInstanced, command buffers, GenDraw), helpers more than one call deep, and
    // patches on the three made after the scan.
    internal static class CardWeapons
    {
        private static readonly List<MethodBase> drawers = new List<MethodBase>();
        private static readonly List<MethodInfo> redirected = new List<MethodInfo>();

        // Patches.ApplyAll (a static constructor): the game's weapon drawing methods; scanned once every mod is loaded.
        public static void ScanWhenLoaded(params MethodBase[] weaponDrawers)
        {
            foreach (MethodBase m in weaponDrawers)
            {
                if (m != null)
                {
                    drawers.Add(m);
                }
            }
            if (drawers.Count == 0)
            {
                return;
            }
            try
            {
                // Queued from inside the queue: it runs after everything queued while the static constructors ran (a pack's
                // own deferred Api 54 registration too), as the last step of loading.
                LongEventHandler.ExecuteWhenFinished(() => LongEventHandler.ExecuteWhenFinished(Scan));
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Card weapons: the scan could not be queued: " + e.Message);
            }
        }

        private static void Scan()
        {
            Stopwatch watch = Stopwatch.StartNew();
            CardDraw.CheckOverloads();
            HashSet<Assembly> mods = ModAssemblies();
            List<MethodInfo> candidates = new List<MethodInfo>();
            List<MethodInfo> leftToApi54 = new List<MethodInfo>();
            foreach (MethodBase drawer in drawers)
            {
                HarmonyLib.Patches info;
                try
                {
                    info = Harmony.GetPatchInfo(drawer);
                }
                catch (Exception e)
                {
                    ModLog.Warn("[IsoRim] Card weapons: could not read the patches on " + drawer.DeclaringType + "." + drawer.Name + ": " + e.Message);
                    continue;
                }
                if (info == null)
                {
                    continue;
                }
                foreach (Patch p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    Consider(p.PatchMethod, mods, candidates, leftToApi54, true);
                }
                if (info.Transpilers.Any(t => t.owner != Patches.HarmonyId))
                {
                    List<MethodInfo> added;
                    try
                    {
                        added = AddedCalls(drawer);
                    }
                    catch (Exception e)
                    {
                        ModLog.Warn("[IsoRim] Card weapons: could not read the transpiled code of " + drawer.DeclaringType + "." + drawer.Name + ": " + e.Message);
                        continue;
                    }
                    foreach (MethodInfo m in added)
                    {
                        Consider(m, mods, candidates, leftToApi54, false);
                    }
                }
            }
            foreach (MethodInfo m in candidates)
            {
                if (Patches.RedirectCardDraws(m))
                {
                    redirected.Add(m);
                }
            }
            watch.Stop();
            ModLog.Note("[IsoRim] Card weapons: drawing redirected for cards in " + redirected.Count + " methods ("
                + watch.Elapsed.TotalMilliseconds.ToString("0.0") + " ms, " + candidates.Count + " looked at)"
                + (redirected.Count > 0 ? ": " + string.Join(", ", redirected.Select(Name)) : "")
                + (leftToApi54.Count > 0 ? "; left to Api 54: " + string.Join(", ", leftToApi54.Select(Name)) : ""));
        }

        // A method to look at (and with helpers, the methods it calls), unless it is left alone (see above). Fails soft for
        // this method alone: one warning naming it.
        private static void Consider(MethodInfo m, HashSet<Assembly> mods, List<MethodInfo> candidates, List<MethodInfo> leftToApi54, bool helpers)
        {
            try
            {
                if (m == null || m.DeclaringType == null || !mods.Contains(m.DeclaringType.Assembly) || m.DeclaringType.Assembly == typeof(CardWeapons).Assembly)
                {
                    return;
                }
                if (m.IsGenericMethod || m.DeclaringType.IsGenericType || m.DeclaringType.ContainsGenericParameters)
                {
                    return;
                }
                bool caught = FigureWeapons.Catches(m);
                if (!caught && (Patches.RewritesDrawing(m) || Contains(candidates, m)))
                {
                    return;
                }
                // Its code read first: a method that cannot be read is left out whole (one warning, below).
                List<MethodInfo> callees = new List<MethodInfo>();
                if (helpers && m.GetMethodBody() != null)
                {
                    foreach (KeyValuePair<OpCode, object> instruction in PatchProcessor.ReadMethodBody(m))
                    {
                        MethodInfo callee = instruction.Value as MethodInfo;
                        if (callee != null && (instruction.Key == OpCodes.Call || instruction.Key == OpCodes.Callvirt))
                        {
                            callees.Add(callee);
                        }
                    }
                }
                if (caught)
                {
                    if (!Contains(leftToApi54, m))
                    {
                        leftToApi54.Add(m);
                    }
                }
                else
                {
                    candidates.Add(m);
                }
                foreach (MethodInfo callee in callees)
                {
                    Consider(callee, mods, candidates, leftToApi54, false);
                }
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Card weapons: could not look at " + (m != null && m.DeclaringType != null ? Name(m) : "a method") + ": " + (e.InnerException ?? e).Message);
            }
        }

        // The methods a transpiler put into the game's method: called in its current code, not in its original.
        private static List<MethodInfo> AddedCalls(MethodBase drawer)
        {
            HashSet<MethodInfo> original = new HashSet<MethodInfo>();
            foreach (CodeInstruction ci in PatchProcessor.GetOriginalInstructions(drawer))
            {
                MethodInfo callee = ci.operand as MethodInfo;
                if (callee != null)
                {
                    original.Add(callee);
                }
            }
            List<MethodInfo> added = new List<MethodInfo>();
            foreach (CodeInstruction ci in PatchProcessor.GetCurrentInstructions(drawer))
            {
                MethodInfo callee = ci.operand as MethodInfo;
                if (callee != null && (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && !original.Contains(callee) && !added.Contains(callee))
                {
                    added.Add(callee);
                }
            }
            return added;
        }

        // FigureWeapons.CatchDrawsOf, before Api 54 patches a method registered after the scan: the card redirect is taken off
        // (Api 54's transpiler must see the method's own Graphics.DrawMesh calls).
        internal enum Handover
        {
            NotRedirected,   // nothing to take off
            Released,        // taken off: Restore if Api 54's own patch then fails
            Kept             // it could not be taken off (Harmony leaves a failed unpatch as it was): it stays a card redirect
        }

        internal static Handover Release(MethodBase method)
        {
            for (int i = 0; i < redirected.Count; i++)
            {
                if (!Patches.SameMethod(redirected[i], method))
                {
                    continue;
                }
                try
                {
                    Patches.UnredirectCardDraws(redirected[i]);
                }
                catch (Exception e)
                {
                    ModLog.Warn("[IsoRim] Card weapons: could not hand " + Name(redirected[i]) + " over to Api 54 (it stays redirected for cards): " + (e.InnerException ?? e).Message);
                    return Handover.Kept;
                }
                redirected.RemoveAt(i);
                return Handover.Released;
            }
            return Handover.NotRedirected;
        }

        // Api 54 could not patch a method Release took the card redirect off: it is put back (fails soft, one warning).
        internal static void Restore(MethodInfo method)
        {
            if (method != null && Patches.RedirectCardDraws(method))
            {
                redirected.Add(method);
            }
        }

        // The assemblies of the running mods (the game's own list).
        private static HashSet<Assembly> ModAssemblies()
        {
            HashSet<Assembly> set = new HashSet<Assembly>();
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                foreach (Assembly a in mod.assemblies.loadedAssemblies)
                {
                    set.Add(a);
                }
            }
            return set;
        }

        private static bool Contains(List<MethodInfo> list, MethodInfo m)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (Patches.SameMethod(list[i], m))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Name(MethodInfo m) => m.DeclaringType.FullName + "." + m.Name;
    }
}
