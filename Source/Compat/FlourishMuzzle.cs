using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Built-in adapter for Flourish - Combat Animations (packageId astryl.Flourish; user-approved exception to "no mod names in
    // core", 2026-10-31). Flourish finds a gun's muzzle in one place, Flourish.MuzzleAnchor.TryGet(Pawn, out Vector3 pos, out
    // float bearing): from the flat 2D weapon sprite it last saw drawn (its WeaponSamples, recorded in
    // PawnRenderUtility.DrawEquipmentAiming). Its muzzle effects use that point:
    //   - "muzzle": FleckMaker.Static(pos, ShotFlash) (AnimEventWorkers.DoMuzzle);
    //   - "bloom": MoteMaker.MakeAttachedOverlay(pawn, Flourish_Mote_Bloom, pos - pawn.DrawPos) (SpawnOverlay atMuzzle), a
    //     MoteOverhead glow IsoRim would otherwise float over the shooter's head like an icon in its cell;
    //   - "tracer": MoteMaker.MakeStaticMote(pos + dir * len / 2, ..., exactRot = dir) stretched from pos to the target
    //     (SpawnLine with useProjectile).
    // In the 3D view that flat point is under or inside the figure (a 3D figure never records a sample at all). Here TryGet
    // answers with IsoRim's 3D muzzle (Api.TryGetMuzzle, Api 44) and the effect made right after it at that point is stood at
    // the muzzle's height (Api.StandFleck, Api.StandMote): the flash and the bloom standing up at the barrel's end, the tracer
    // lying flat at that height along the shot. Flourish's own switch for this (muzzleAnchorEnabled) is followed.
    //
    // Only patched when Flourish is active; fails soft: if a target is missing (Flourish renamed it), one log line and the
    // adapter stays off, nothing else changes. Outside the 3D view Api.TryGetMuzzle returns false and Flourish is untouched.
    internal static class FlourishMuzzle
    {
        public const string PackageId = "astryl.Flourish";

        public static bool On;

        private static FieldInfo settingsField;     // Flourish.FlourishMod.Settings (static)
        private static FieldInfo anchorEnabledField; // Flourish.FlourishSettings.muzzleAnchorEnabled

        // The muzzle TryGet last answered with (the effect Flourish makes right after it, in the same tick, sits there).
        private static Pawn lastPawn;
        private static Map lastMap;
        private static Vector3 lastPos;     // as given to Flourish: x, z the muzzle's (the game's rows)
        private static float lastHeight;    // cells over the ground
        private static int lastTick = -1;

        // Harness counters: TryGet answers given, motes stood.
        public static int Answers;
        public static int Stood;

        public static bool Apply(Harmony harmony)
        {
            if (!ModsConfig.IsActive(PackageId))
            {
                return false;
            }
            MethodInfo tryGet = null;
            MethodInfo overlay = null;
            MethodInfo staticMote = null;
            try
            {
                Type anchor = AccessTools.TypeByName("Flourish.MuzzleAnchor");
                if (anchor != null)
                {
                    tryGet = AccessTools.DeclaredMethod(anchor, "TryGet", new[] { typeof(Pawn), typeof(Vector3).MakeByRefType(), typeof(float).MakeByRefType() });
                }
                overlay = AccessTools.DeclaredMethod(typeof(MoteMaker), nameof(MoteMaker.MakeAttachedOverlay),
                    new[] { typeof(Thing), typeof(ThingDef), typeof(Vector3), typeof(float), typeof(float) });
                staticMote = AccessTools.DeclaredMethod(typeof(MoteMaker), nameof(MoteMaker.MakeStaticMote),
                    new[] { typeof(Vector3), typeof(Map), typeof(ThingDef), typeof(float), typeof(bool), typeof(float) });
                Type mod = AccessTools.TypeByName("Flourish.FlourishMod");
                Type settings = AccessTools.TypeByName("Flourish.FlourishSettings");
                settingsField = mod != null ? AccessTools.Field(mod, "Settings") : null;
                anchorEnabledField = settings != null ? AccessTools.Field(settings, "muzzleAnchorEnabled") : null;
            }
            catch (Exception)
            {
                tryGet = null;
            }
            if (tryGet == null || tryGet.ReturnType != typeof(bool) || overlay == null || staticMote == null)
            {
                Log.Warning("[IsoRim] Flourish is active but its muzzle lookup (Flourish.MuzzleAnchor.TryGet) was not found: its muzzle effects stay where Flourish puts them.");
                return false;
            }
            MethodInfo tryGetPost = AccessTools.Method(typeof(FlourishMuzzle), nameof(TryGet_Postfix));
            MethodInfo overlayPost = AccessTools.Method(typeof(FlourishMuzzle), nameof(MakeAttachedOverlay_Postfix));
            MethodInfo staticPost = AccessTools.Method(typeof(FlourishMuzzle), nameof(MakeStaticMote_Postfix));
            try
            {
                harmony.Patch(overlay, postfix: new HarmonyMethod(overlayPost));
                harmony.Patch(staticMote, postfix: new HarmonyMethod(staticPost));
                harmony.Patch(tryGet, postfix: new HarmonyMethod(tryGetPost));
            }
            catch (Exception e)
            {
                // Take back whatever went in, only our own postfixes.
                try
                {
                    harmony.Unpatch(overlay, overlayPost);
                    harmony.Unpatch(staticMote, staticPost);
                    harmony.Unpatch(tryGet, tryGetPost);
                }
                catch (Exception)
                {
                }
                Log.Warning("[IsoRim] Could not hook Flourish's muzzle lookup: its muzzle effects stay where Flourish puts them (" + e.Message + ").");
                return false;
            }
            On = true;
            return true;
        }

        // Flourish's switch "muzzle anchor" (on unless the player turned it off; unknown counts as on).
        private static bool AnchorEnabled()
        {
            try
            {
                object st = settingsField != null ? settingsField.GetValue(null) : null;
                if (st != null && anchorEnabledField != null)
                {
                    return (bool)anchorEnabledField.GetValue(st);
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        public static void TryGet_Postfix(Pawn pawn, ref Vector3 pos, ref float bearing, ref bool __result)
        {
            try
            {
                if (!On || pawn == null || !View3D.Ready || !pawn.Spawned || pawn.equipment == null || pawn.equipment.Primary == null || !AnchorEnabled())
                {
                    return;
                }
                Vector3 muzzle;
                float aim;
                if (!Api.TryGetMuzzle(pawn.equipment.Primary, pawn, out muzzle, out aim))
                {
                    return;
                }
                // Flourish's own bearing (the drawn weapon's angle) when it had one; IsoRim's aim otherwise.
                if (!__result)
                {
                    bearing = aim;
                }
                pos = new Vector3(muzzle.x, __result ? pos.y : pawn.DrawPos.y, muzzle.z);
                __result = true;
                lastPawn = pawn;
                lastMap = pawn.Map;
                lastPos = pos;
                lastHeight = muzzle.y;
                lastTick = Find.TickManager.TicksGame;
                Answers++;
                // "muzzle": its flash, FleckMaker.Static(pos, ShotFlash), standing at the barrel's end as IsoRim's own.
                Api.StandFleck(muzzle, FleckDefOf.ShotFlash);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Putting Flourish's muzzle effect at the 3D muzzle failed: " + e, 93611942);
            }
        }

        private static bool Fresh(Map map)
        {
            return lastTick >= 0 && Find.TickManager != null && Find.TickManager.TicksGame == lastTick && map != null && map == lastMap;
        }

        // "bloom": the overlay attached at pos - pawn.DrawPos, right after TryGet.
        public static void MakeAttachedOverlay_Postfix(Thing thing, Vector3 offset, Mote __result)
        {
            if (__result == null || thing == null || thing != lastPawn || !Fresh(thing.MapHeld))
            {
                return;
            }
            Vector3 drawPos = thing.DrawPos;
            if (Mathf.Abs(drawPos.x + offset.x - lastPos.x) > 0.01f || Mathf.Abs(drawPos.z + offset.z - lastPos.z) > 0.01f)
            {
                return;
            }
            Api.StandMote(__result, lastHeight, false);
            Stood++;
        }

        // "tracer": a line from pos toward the target (its middle on that line, turned along it), right after TryGet.
        public static void MakeStaticMote_Postfix(Vector3 loc, Map map, float exactRot, Mote __result)
        {
            if (__result == null || !Fresh(map))
            {
                return;
            }
            Vector3 d = loc - lastPos;
            d.y = 0f;
            if (d.sqrMagnitude < 0.0025f || Mathf.Abs(Mathf.DeltaAngle(d.AngleFlat(), exactRot)) > 1f)
            {
                return;
            }
            Api.StandMote(__result, lastHeight, true);
            Stood++;
        }
    }
}
