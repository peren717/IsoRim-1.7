using System;
using UnityEngine;
using Verse;

namespace IsoRim
{
    public static partial class Api
    {
        // ---- 3D explosions (Version 74)

        /// <summary>
        /// (Version 74) Gives a damage def's 3D explosions their own colours: fire from the hottest (the light core) to the coldest
        /// (where a shrinking piece ends), evenly spaced, at least one; dust the ground ring's and the debris' colour. smoke is
        /// ignored (since 2026-11-12 the smoke is the game's own, stood up: nothing of IsoRim's to colour). Without it a def's
        /// colours come from its own explosionColorCenter and explosionColorEdge (docs part 52). Call at startup (main thread); a
        /// second call for the same def replaces the first.
        /// </summary>
        public static void AddExplosionLook(DamageDef def, Color[] fire, Color smoke, Color dust)
        {
            ExplosionFx.SetLook(def, fire, smoke, dust);
        }

        /// <summary>
        /// (Version 74) Explosions of this damage def are never drawn in 3D: the game's own flat explosion with all its flecks, as
        /// without IsoRim (nothing held back, nothing drawn, its smoke not stood up). Call at startup (main thread).
        /// </summary>
        public static void SkipExplosionFx(DamageDef def)
        {
            ExplosionFx.Skip(def);
        }

        /// <summary>
        /// (Version 74) A filter asked for every explosion IsoRim would draw in 3D, as it starts (main thread, while the game
        /// ticks): true keeps that explosion the game's own flat one, as without IsoRim. A filter that throws is warned about
        /// once and ignored from then on (counts as false). Only standard System.Func, so code calling it after an
        /// Api.Version &gt;= 74 check never breaks on an older IsoRim. Call at startup (main thread).
        /// </summary>
        public static void AddExplosionFxFilter(Func<Explosion, bool> skip)
        {
            ExplosionFx.AddFilter(skip, skip != null ? ModelRules.OwnerOf(skip) : "?");
        }

        /// <summary>
        /// (Version 74) For a mod whose explosion does not run the game's DamageWorker.ExplosionStart: draws IsoRim's 3D
        /// fireball and ground ring at the cell's middle, on the cell's floor (no stair or bridge rise), at full size at once, in
        /// the def's colours (no smoke or debris: those come from the game's own flecks, which stay the mod's). Does nothing
        /// (false) with the option off, the 3D view off, on a map not shown, with a null map or def, a radius of 0 or less, a
        /// def opted out by SkipExplosionFx, 48 bursts already alive, or after the 3D explosions failed. Main thread only (where
        /// the explosion starts, while the game ticks).
        /// </summary>
        public static bool DrawExplosion3D(Map map, IntVec3 cell, float radius, DamageDef def)
        {
            return ExplosionFx.Draw3D(map, cell, radius, def);
        }

        public static partial class Dev
        {
            // (Api 74) For tests: bursts made so far, the game's explosion flecks replaced so far (made already at the end of their
            // life: the flash and the cells' dust puffs; since 2026-11-12 not the smoke, which is stood up), explosions left flat by
            // an opt-out so far, bursts alive, and parts submitted in the last frame.
            public static int ExplosionBursts => ExplosionFx.Made;
            public static int ExplosionFlecksHeld => ExplosionFx.Held;
            public static int ExplosionsOptedOut => ExplosionFx.OptedOut;
            public static int ExplosionBurstsLive => ExplosionFx.Live;
            public static int ExplosionPartsDrawn => ExplosionFx.DrawnLast;
        }
    }
}
