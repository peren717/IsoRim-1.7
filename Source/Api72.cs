namespace IsoRim
{
    public static partial class Api
    {
        // ---- the harness seed (Version 72)

        /// <summary>
        /// (Version 72) N when the game was started with -isorimseed=N (test harnesses only: players never pass it), else null.
        /// With it IsoRim makes a quick-test game deterministic: the world, starting tile, colonists and map are the same every
        /// run, and play starts paused before its first tick from a fixed random state. A test runner of an addon reads its seed
        /// here instead of an argument of its own, so one value seeds the whole run.
        /// </summary>
        public static int? HarnessSeed => QuickTestSeedArg.Value;
    }
}
