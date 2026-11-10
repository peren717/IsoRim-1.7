using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace IsoRim
{
    // Built-in adapter for Melee Animation (packageId co.uk.epicguru.meleeanimation; user-approved exception to "no mod names
    // in core", 2026-11-09). That mod draws its weapons and fighters as flat sprites of its own, which lie on the ground in the
    // 3D view. While the 3D view is on (View3D.Active) everything it draws is switched off; in 2D it is untouched.
    //   - Idle weapon (its AnimateAtIdle): through its public hook IdleControllerComp.ShouldDrawAdditional, a static
    //     List<IdleControllerDrawDelegate>, delegate (IdleControllerComp comp, ref bool shouldBeActive, ref bool doDefaultDraw).
    //     Ours says not active and default draw: its idle animation is cleared on the pawn's next tick and the game's own
    //     DrawEquipmentAiming runs, so the weapon is IsoRim's 3D one in the hand.
    //   - Where its features start, so nothing is tried (and nothing logs that it failed):
    //       IdleControllerComp.NotifyPawnDidMeleeAttack (its swing on every melee hit): skipped;
    //       JoyGiver_FriendlyDuel.TryGiveJob (colonists picking a friendly duel as recreation): no job;
    //       Building_DuelSpot.GetGizmos: its "Start duel" button (the one calling ClickedStartDuel) left out;
    //       DraftedFloatMenuOptionsUI.GenerateMenuOptions (its right-click options: execute, lasso, skills): none;
    //       MapPawnProcessor.Tick (its scan for automatic executions and lassos): skipped;
    //       DuelSectionWorkerBase.StartEndExecution (the winner's animation when a duel that was playing ends): skipped.
    //   - Every animation starts in AnimRenderer.Register(), the safety net for any other way in: a prefix answers false and
    //     marks that renderer destroyed, as Register does itself when it refuses, so none starts.
    //     A renderer that has already started (one loaded from a save, re-registered after loading) is let through:
    //     animations already playing play to their end. Lassos already flying land; jobs already running end as they do.
    // The delegate type lives in the other assembly, so ours is bound with Delegate.CreateDelegate to a static method taking
    // object for the comp (a reference parameter may be wider than the delegate's) and the same two ref bools.
    // Both read only View3D.Active (a plain bool), so they are safe on any thread.
    //
    // Only hooked when that mod is active; fails soft: each half is looked up by name and, if it is not found, one log line
    // and that half stays off, nothing else changes.
    internal static class MeleeAnimOff
    {
        public const string PackageId = "co.uk.epicguru.meleeanimation";

        public static bool IdleHooked;
        public static bool RegisterHooked;

        private static FieldInfo hasStartedField;   // AM.AnimRenderer.hasStarted (private bool)
        private static MethodInfo destroyMethod;    // AM.AnimRenderer.Destroy()

        // Harness counters: Register calls refused in the 3D view, let through (already started) in the 3D view.
        public static int Refused;
        public static int LetThrough;

        public static bool Apply(Harmony harmony)
        {
            if (!ModsConfig.IsActive(PackageId))
            {
                return false;
            }
            HookIdle();
            HookRegister(harmony);
            HookSources(harmony);
            return IdleHooked || RegisterHooked || SourcesHooked > 0;
        }

        // How many of the six starting points were hooked (each on its own).
        public static int SourcesHooked;

        private static void HookSources(Harmony harmony)
        {
            HookSource(harmony, "AM.Idle.IdleControllerComp", "NotifyPawnDidMeleeAttack", nameof(Skip_Prefix), false, "its swing on melee hits");
            HookSource(harmony, "AM.AutoDuel.JoyGiver_FriendlyDuel", "TryGiveJob", nameof(NoJob_Prefix), false, "friendly duels as recreation");
            HookSource(harmony, "AM.Buildings.Building_DuelSpot", "GetGizmos", nameof(DuelSpotGizmos_Postfix), true, "the duel spot's Start duel button");
            HookSource(harmony, "AM.UI.DraftedFloatMenuOptionsUI", "GenerateMenuOptions", nameof(NoOptions_Prefix), false, "its right-click options");
            HookSource(harmony, "AM.Processing.MapPawnProcessor", "Tick", nameof(Skip_Prefix), false, "automatic executions and lassos");
            HookSource(harmony, "AM.Events.Workers.DuelSectionWorkerBase", "StartEndExecution", nameof(Skip_Prefix), false, "the winner's move at the end of a duel");
        }

        private static void HookSource(Harmony harmony, string type, string method, string patch, bool postfix, string what)
        {
            try
            {
                Type t = AccessTools.TypeByName(type);
                MethodInfo m = t != null ? AccessTools.DeclaredMethod(t, method) : null;
                if (m == null)
                {
                    Log.Warning("[IsoRim] Melee Animation's " + type + "." + method + " was not found: " + what + " stay on in the 3D view (its animations still do not start).");
                    return;
                }
                HarmonyMethod hm = new HarmonyMethod(AccessTools.Method(typeof(MeleeAnimOff), patch));
                if (postfix)
                {
                    harmony.Patch(m, postfix: hm);
                }
                else
                {
                    harmony.Patch(m, prefix: hm);
                }
                SourcesHooked++;
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Could not hook Melee Animation's " + method + ": " + what + " stay on in the 3D view (" + e.Message + ").");
            }
        }

        // Its swing on a melee hit, its scan: nothing in the 3D view.
        public static bool Skip_Prefix()
        {
            return !View3D.Active;
        }

        // Its friendly-duel recreation: no job in the 3D view.
        public static bool NoJob_Prefix(ref Verse.AI.Job __result)
        {
            if (!View3D.Active)
            {
                return true;
            }
            __result = null;
            return false;
        }

        // Its right-click options: none in the 3D view.
        public static bool NoOptions_Prefix(ref System.Collections.Generic.IEnumerable<FloatMenuOption> __result)
        {
            if (!View3D.Active)
            {
                return true;
            }
            __result = System.Linq.Enumerable.Empty<FloatMenuOption>();
            return false;
        }

        // The duel spot's "Start duel" button (its action is the spot's ClickedStartDuel) left out in the 3D view.
        public static void DuelSpotGizmos_Postfix(ref System.Collections.Generic.IEnumerable<Gizmo> __result)
        {
            if (View3D.Active)
            {
                __result = WithoutStartDuel(__result);
            }
        }

        private static System.Collections.Generic.IEnumerable<Gizmo> WithoutStartDuel(System.Collections.Generic.IEnumerable<Gizmo> gizmos)
        {
            foreach (Gizmo g in gizmos)
            {
                Command_Action c = g as Command_Action;
                if (c != null && c.action != null && c.action.Method.Name == "ClickedStartDuel")
                {
                    continue;
                }
                yield return g;
            }
        }

        private static void HookIdle()
        {
            try
            {
                Type comp = AccessTools.TypeByName("AM.Idle.IdleControllerComp");
                Type del = AccessTools.TypeByName("AM.Idle.IdleControllerDrawDelegate");
                FieldInfo listField = comp != null ? AccessTools.Field(comp, "ShouldDrawAdditional") : null;
                IList list = listField != null && listField.IsStatic ? listField.GetValue(null) as IList : null;
                if (del == null || list == null)
                {
                    Log.Warning("[IsoRim] Melee Animation is active but its idle hook (IdleControllerComp.ShouldDrawAdditional) was not found: its idle weapon stays flat in the 3D view.");
                    return;
                }
                // Throws if the delegate's shape changed (then the catch below).
                Delegate d = Delegate.CreateDelegate(del, AccessTools.Method(typeof(MeleeAnimOff), nameof(IdleDraw)));
                list.Add(d);
                IdleHooked = true;
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Could not hook Melee Animation's idle weapon: it stays flat in the 3D view (" + e.Message + ").");
            }
        }

        private static void HookRegister(Harmony harmony)
        {
            try
            {
                Type renderer = AccessTools.TypeByName("AM.AnimRenderer");
                MethodInfo register = renderer != null ? AccessTools.DeclaredMethod(renderer, "Register", Type.EmptyTypes) : null;
                hasStartedField = renderer != null ? AccessTools.Field(renderer, "hasStarted") : null;
                destroyMethod = renderer != null ? AccessTools.DeclaredMethod(renderer, "Destroy", Type.EmptyTypes) : null;
                if (register == null || register.IsStatic || register.ReturnType != typeof(bool) || hasStartedField == null || hasStartedField.FieldType != typeof(bool) || destroyMethod == null)
                {
                    Log.Warning("[IsoRim] Melee Animation is active but its animation start (AnimRenderer.Register) was not found: its animations play flat in the 3D view.");
                    return;
                }
                harmony.Patch(register, prefix: new HarmonyMethod(AccessTools.Method(typeof(MeleeAnimOff), nameof(Register_Prefix))));
                RegisterHooked = true;
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Could not hook Melee Animation's animation start: its animations play flat in the 3D view (" + e.Message + ").");
            }
        }

        // Melee Animation's IdleControllerDrawDelegate: in the 3D view its idle animation is off and the game draws the weapon.
        public static void IdleDraw(object comp, ref bool shouldBeActive, ref bool doDefaultDraw)
        {
            if (View3D.Active)
            {
                shouldBeActive = false;
                doDefaultDraw = true;
            }
        }

        // In the 3D view a new animation does not start: false, as when Register refuses itself (the renderer marked destroyed).
        public static bool Register_Prefix(object __instance, ref bool __result)
        {
            if (!View3D.Active)
            {
                return true;
            }
            try
            {
                if ((bool)hasStartedField.GetValue(__instance))
                {
                    LetThrough++;
                    return true;
                }
                destroyMethod.Invoke(__instance, null);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Holding back a Melee Animation animation in the 3D view failed: " + e, 93619531);
                return true;
            }
            Refused++;
            __result = false;
            return false;
        }
    }
}
