using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 46: burning fires. IsoRim draws every burning fire (the game's Fire: on grass, buildings, items, pawns) as 3D
    // particles (FireFx). An addon can give a fire def its own colours, and route another mod's fire (its own thing class
    // and flicker graphic class, not the game's Fire and Graphic_Flicker) into the same 3D fire, its flat smoke and sparks
    // left out like the game's. Generic: no mod names.
    public static partial class Api
    {
        /// <summary>
        /// (Api version 46) Gives the burning fires of this def their own colours in 3D: flame from the hottest (where a piece
        /// is born, at the base) to the coldest (where it dies out at the top), evenly spaced, at least one; spark the sparks'
        /// colour, smoke the smoke's darkest shade (it lightens as it rises). Fires of other defs keep IsoRim's yellow, orange
        /// and red. Call once at startup (main thread); a second call for the same def replaces the first.
        /// </summary>
        public static void AddFireLook(ThingDef fireDef, Color[] flame, Color spark, Color smoke)
        {
            FireFx.SetLook(fireDef, flame, spark, smoke);
        }

        /// <summary>
        /// (Api version 46) Draws another mod's burning fire as IsoRim's 3D fire: while the 3D view is on, graphicClass's own
        /// <c>DrawWorker</c> (declared on it) is left out for every thing that fireSize gives a size for (the game's fire size,
        /// as <c>Fire.fireSize</c>: 0.1 small .. 1.75 big), and the 3D fire stands in, on what the thing burns as the game's
        /// fire does (on the ground or on top of what stands in its cell; on the pawn that is its
        /// <c>AttachableThing.parent</c>). Return a negative size to pass (the graphic draws as it does: e.g. a building's fire
        /// overlay that uses the same graphic class). thingClass (optional, may be null): while its own <c>TickInterval</c>
        /// (declared on it) runs, the game's flat smoke and micro sparks (<c>FleckMaker.ThrowSmoke</c>,
        /// <c>ThrowMicroSparks</c>) are left out while 3D fires draw, as for the game's fires; its fire glow is kept.
        /// Returns false (logged) when a class or method is missing or cannot be patched. Only standard
        /// <c>System.Func</c>, so code calling it after an <c>Api.Version &gt;= 46</c> check never breaks on an older IsoRim.
        /// </summary>
        public static bool AddBurningFire(Type graphicClass, Type thingClass, Func<Thing, float> fireSize)
        {
            return BurningFires.Add(graphicClass, thingClass, fireSize, fireSize != null ? ModelRules.OwnerOf(fireSize) : "?");
        }

        public static partial class Dev
        {
            // (Api 46) For tests: fire particles (flame pieces, sparks, puffs) and fires drawn in the last whole frame; flat
            // smoke and sparks left out so far (FireMotes).
            public static int FireParticlesDrawn => FireFx.DrawnLast;
            public static int FiresDrawn => FireFx.FiresLast;
            public static int FireMotesHidden => FireMotes.Hidden;
            public static int FireMotesPassed => FireMotes.Passed;   // the same calls let through (not in a fire's tick, or 3D fires off)
        }
    }

    // The registered fire graphics and thing classes (Api 46).
    internal static class BurningFires
    {
        private sealed class Route
        {
            public Func<Thing, float> size;
            public string owner;
            public bool failed;
        }

        private static readonly Dictionary<Type, List<Route>> byGraphic = new Dictionary<Type, List<Route>>();
        private static readonly HashSet<MethodBase> patched = new HashSet<MethodBase>();
        private static Harmony harmony;

        public static bool Add(Type graphicClass, Type thingClass, Func<Thing, float> size, string owner)
        {
            if (graphicClass == null || size == null)
            {
                Log.Warning("[IsoRim] Api.AddBurningFire (" + owner + "): no graphic class or size given.");
                return false;
            }
            MethodInfo draw = AccessTools.DeclaredMethod(graphicClass, "DrawWorker");
            if (draw == null)
            {
                Log.Warning("[IsoRim] Api.AddBurningFire (" + owner + "): " + graphicClass.FullName + " has no DrawWorker of its own.");
                return false;
            }
            harmony ??= new Harmony("isorim.api46");
            try
            {
                if (patched.Add(draw))
                {
                    harmony.Patch(draw, prefix: new HarmonyMethod(typeof(BurningFires), nameof(DrawWorker_Prefix)));
                }
                List<Route> list;
                if (!byGraphic.TryGetValue(graphicClass, out list))
                {
                    byGraphic[graphicClass] = list = new List<Route>();
                }
                list.Add(new Route { size = size, owner = owner });
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Api.AddBurningFire (" + owner + ") could not patch " + graphicClass.FullName + ".DrawWorker: " + e.Message);
                return false;
            }
            if (thingClass != null && !FireMotes.AddClass(thingClass, harmony, owner, size))
            {
                return false;
            }
            return true;
        }

        // The graphic's own draw: left out for a thing a route gives a size (the 3D fire stands in).
        public static bool DrawWorker_Prefix(Graphic __instance, Thing thing)
        {
            if (thing == null || !View3D.Ready || DrawHook.skip)
            {
                return true;
            }
            List<Route> list;
            if (!byGraphic.TryGetValue(__instance.GetType(), out list))
            {
                return true;
            }
            for (int i = 0; i < list.Count; i++)
            {
                Route r = list[i];
                if (r.failed)
                {
                    continue;
                }
                float s;
                try
                {
                    s = r.size(thing);
                }
                catch (Exception e)
                {
                    r.failed = true;
                    Log.Error("[IsoRim] An Api.AddBurningFire size (" + r.owner + ") failed and is ignored from now on: " + e);
                    continue;
                }
                if (s >= 0f)
                {
                    return !Effects.BurningThing(thing, s);
                }
            }
            return true;
        }
    }

    // The game's flat smoke and micro sparks of burning fires are left out while IsoRim's 3D fires draw (their own smoke and
    // sparks rise instead): while a fire ticks (Fire.TickInterval, and Api 46 thing classes) on the map in view, the two
    // FleckMaker calls it makes for them do nothing. Its fire glow (FleckMaker.ThrowFireGlow) and light are kept; other
    // smoke and sparks (explosions, campfires, guns) are not touched.
    internal static class FireMotes
    {
        [ThreadStatic] private static int ticking;
        internal static int Hidden, Passed;   // harness counts (Api.Dev)
        private static readonly Dictionary<Type, List<Func<Thing, float>>> classes = new Dictionary<Type, List<Func<Thing, float>>>();

        // At startup (Patches.ApplyAll): the game's Fire and the two FleckMaker calls.
        public static void ApplyCore(Harmony harmony)
        {
            try
            {
                HarmonyMethod skip = new HarmonyMethod(typeof(FireMotes), nameof(Throw_Prefix));
                harmony.Patch(AccessTools.Method(typeof(FleckMaker), nameof(FleckMaker.ThrowSmoke)), prefix: skip);
                harmony.Patch(AccessTools.Method(typeof(FleckMaker), nameof(FleckMaker.ThrowMicroSparks)), prefix: skip);
                AddClass(typeof(Fire), harmony, "IsoRim", null);
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Could not hook the fires' flat smoke and sparks (they show over the 3D fires): " + e.Message);
            }
        }

        // size: the route's size (its flat smoke left out only for things it draws, size >= 0); null: always (the game's Fire).
        public static bool AddClass(Type thingClass, Harmony harmony, string owner, Func<Thing, float> size)
        {
            MethodInfo tick = AccessTools.DeclaredMethod(thingClass, "TickInterval");
            if (tick == null)
            {
                Log.Warning("[IsoRim] Api.AddBurningFire (" + owner + "): " + thingClass.FullName + " has no TickInterval of its own; its flat smoke stays.");
                return false;
            }
            List<Func<Thing, float>> sizes;
            if (classes.TryGetValue(thingClass, out sizes))
            {
                if (size != null && sizes != null)
                {
                    sizes.Add(size);
                }
                return true;
            }
            classes[thingClass] = size != null ? new List<Func<Thing, float>> { size } : null;
            try
            {
                harmony.Patch(tick, prefix: new HarmonyMethod(typeof(FireMotes), nameof(Tick_Prefix)), finalizer: new HarmonyMethod(typeof(FireMotes), nameof(Tick_Finalizer)));
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Api.AddBurningFire (" + owner + ") could not patch " + thingClass.FullName + ".TickInterval: " + e.Message);
                return false;
            }
        }

        public static void Tick_Prefix(Thing __instance, out bool __state)
        {
            __state = __instance != null && __instance.Map != null && __instance.Map == Find.CurrentMap && View3D.Ready && Effects.ParticleFires && Drawn(__instance);
            if (__state)
            {
                ticking++;
            }
        }

        // Whether the 3D fire draws this thing: the game's Fire always; an Api 46 class when one of its sizes gives a size.
        private static bool Drawn(Thing t)
        {
            if (t is Fire)
            {
                return true;
            }
            List<Func<Thing, float>> sizes = null;
            Type ty = t.GetType();
            while (ty != null && !classes.TryGetValue(ty, out sizes))
            {
                ty = ty.BaseType;
            }
            if (sizes == null)
            {
                return ty == null ? false : true;
            }
            for (int i = 0; i < sizes.Count; i++)
            {
                try
                {
                    if (sizes[i](t) >= 0f)
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // logged by the draw (BurningFires)
                }
            }
            return false;
        }

        public static Exception Tick_Finalizer(Exception __exception, bool __state)
        {
            if (__state && ticking > 0)
            {
                ticking--;
            }
            return __exception;
        }

        // FleckMaker.ThrowSmoke, ThrowMicroSparks: false (left out) while a fire ticks with 3D fires on.
        public static bool Throw_Prefix()
        {
            if (ticking > 0)
            {
                Hidden++;
                return false;
            }
            Passed++;
            return true;
        }
    }
}
