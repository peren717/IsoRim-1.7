using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace IsoRim
{
    // -isorimseed=N on the command line (test harnesses only: players never pass it) is the one place that makes a quick-test
    // game deterministic: the same world, tile, colonists and map every run, and play starting from the same random state.
    // Without the argument nothing here is patched or registered. Public as Api.HarnessSeed (Version 72).
    //   1. World setup: Root_Play.SetupForQuickTestPlay runs under Rand state N. A Priority.First prefix pushes it, a finalizer
    //      (after every mod's postfix: their starting-tile picks, IsoRim's -isorimbiome) pops it. The world, the tile and the
    //      colonists come from N. The rest of the new game's loading (GameInitData.PrepForMapGen, which gives the colonists
    //      their needs, Scenario.PreMapGenerate, Game.InitNewGame around the map's own seeded generation) then runs from a
    //      fixed state too, StateFor(NewGame), set by the same finalizer, instead of the startup-time state.
    //   2. Generation seeds: vanilla seeds some Rand blocks with System.HashCode.Combine, which the runtime seeds at random per
    //      process (shrines and other BaseGen structures differ every launch). In the methods of Targets a transpiler swaps that
    //      call for Combine below: the same inputs (vanilla's own body computes them), the same per-value hashes as HashCode
    //      (GetHashCode, 0 for null), mixed by the game's own Gen.HashCombineInt instead. Nothing of vanilla is copied; each
    //      target is watched (Api.WatchCopy) because the swap relies on its body, and its count of swapped calls is checked.
    //   3. Start of play: once the quick test's new game has finished loading, Rand is set to a fixed state, StateFor(PlayStart),
    //      and the game is paused before its first tick, so a test runner starts from the same state.
    //   4. Sounds and music leave Rand's state as they found it (SoundEntries): they draw at real-time moments (sustainer
    //      samples, the next song), on mouse-over and under the sound slots' real-time limits, so they shifted the game's
    //      random stream by a different amount every run (seen: a mouse-over sound before the first tick). Only on the main
    //      thread while the game is not waiting for a loading event (the state is saved and restored, the stack untouched).
    // The parsed argument alone, so reading it runs nothing else. QuickTestSeed has no static initialisers: what it looks up is
    // resolved lazily, only once the argument was given.
    internal static class QuickTestSeedArg
    {
        internal const string Arg = "isorimseed";

        // The active seed: N from -isorimseed=N, null without it (or with a value that is not an integer: warned once).
        internal static readonly int? Value = Read();

        private static int? Read()
        {
            if (!GenCommandLine.TryGetCommandLineArg(Arg, out string text))
            {
                return null;
            }
            if (int.TryParse(text, out int n))
            {
                return n;
            }
            Log.Warning("[IsoRim] -" + Arg + "=" + text + " is not a whole number: ignored.");
            return null;
        }
    }

    public static class QuickTestSeed
    {
        internal const string Arg = QuickTestSeedArg.Arg;

        // What the fixed states after the world setup are derived from (with N, StateFor).
        internal const string NewGame = "new game";
        internal const string PlayStart = "play start";

        private const int SetStateWarningKey = 0x15E3D7;   // Log.WarningOnce key of SetState's warning

        internal static int? Value => QuickTestSeedArg.Value;

        // The methods whose HashCode.Combine calls seed Rand, how many such calls each has, and the CopyHash of the vanilla body
        // the swap was written against (game 1.7.5005). Not copies: vanilla's bodies run; watched so a change is noticed.
        internal sealed class Target
        {
            public string name;
            public MethodBase method;
            public int calls;
            public string hash;
        }

        // Resolved on first use, which is only with the argument (Apply, the transpiler, the parity probe).
        private static Target[] targets;
        private static MethodInfo combine2, combine3;
        private static Action<ulong> setRandState;
        private static Func<ulong> getRandState;
        private static bool randResolved;

        private static bool setupPushed;
        private static bool quickTestStarting;

        internal static Target[] Targets => targets ?? (targets = new[]
        {
            // Map.NextGenSeed: HashCode.Combine(tile hash or uniqueID, generatedId++, world seed); BaseGen's and the scatter
            // gen steps' Rand blocks.
            new Target { name = "Map.NextGenSeed", calls = 1, hash = "9067153624a4a743",
                method = AccessTools.PropertyGetter(typeof(Map), nameof(Map.NextGenSeed)) },
            // LayoutWorker.FillAllRooms: three RandBlocks of HashCode.Combine(structureSketch.id, room index) (pre-fill, fill,
            // post-fill of each room of a layout structure: ancient complexes and the like).
            new Target { name = "LayoutWorker.FillAllRooms", calls = 3, hash = "edd690e4d0112150",
                method = AccessTools.Method(typeof(LayoutWorker), "FillAllRooms",
                    new[] { typeof(LayoutStructureSketch), typeof(Map), typeof(IntVec3), typeof(bool), typeof(Faction), typeof(float?) }) },
            // Gravship.ConstantRandSeed: HashCode.Combine(ID, 18948292); a gravship's incident cycles (IncidentCycleUtility).
            new Target { name = "Gravship.ConstantRandSeed", calls = 1, hash = "7023a3bb4f6be3a6",
                method = AccessTools.PropertyGetter(typeof(Gravship), nameof(Gravship.ConstantRandSeed)) },
            // Building_Trap.KnowsOfTrap: Rand.ValueAsync(HashCode.Combine(faction or lord loadID, thingIDNumber)), whether a
            // raider knows of a trap.
            new Target { name = "Building_Trap.KnowsOfTrap", calls = 1, hash = "9978b0133b49c0a0",
                method = AccessTools.Method(typeof(Building_Trap), nameof(Building_Trap.KnowsOfTrap), new[] { typeof(Pawn) }) },
        });

        // The entry points of sounds and music (4): one-shots, new sustainers, the per-frame sound update, the music.
        private static MethodBase[] SoundEntries()
        {
            return new MethodBase[]
            {
                AccessTools.Method(typeof(Verse.Sound.SoundStarter), nameof(Verse.Sound.SoundStarter.PlayOneShot), new[] { typeof(SoundDef), typeof(Verse.Sound.SoundInfo) }),
                AccessTools.Method(typeof(Verse.Sound.SoundStarter), nameof(Verse.Sound.SoundStarter.TrySpawnSustainer), new[] { typeof(SoundDef), typeof(Verse.Sound.SoundInfo) }),
                AccessTools.Method(typeof(Verse.Sound.SoundRoot), nameof(Verse.Sound.SoundRoot.Update), Type.EmptyTypes),
                AccessTools.Method(typeof(MusicManagerPlay), nameof(MusicManagerPlay.MusicUpdate), Type.EmptyTypes),
            };
        }

        // Rand's private StateCompressed (seed and iterations) as delegates, looked up once; null when it cannot be found.
        private static void ResolveRand()
        {
            if (randResolved)
            {
                return;
            }
            randResolved = true;
            setRandState = RandStateAccessor<Action<ulong>>(AccessTools.PropertySetter(typeof(Rand), "StateCompressed"));
            getRandState = RandStateAccessor<Func<ulong>>(AccessTools.PropertyGetter(typeof(Rand), "StateCompressed"));
        }

        private static MethodInfo Combine2 => combine2 ?? (combine2 = AccessTools.FirstMethod(typeof(QuickTestSeed), m => m.Name == nameof(Combine) && m.GetGenericArguments().Length == 2));
        private static MethodInfo Combine3 => combine3 ?? (combine3 = AccessTools.FirstMethod(typeof(QuickTestSeed), m => m.Name == nameof(Combine) && m.GetGenericArguments().Length == 3));

        // A Rand seed derived from N for one purpose.
        internal static int StateFor(string purpose)
        {
            return Gen.HashCombineInt(Value.GetValueOrDefault(), GenText.StableStringHash(purpose));
        }

        // Rand's state set to StateFor(purpose), as Rand.Seed would (that seed, no iterations) but without touching the state
        // stack: the startup-time state it replaces is not wanted back.
        private static void SetState(string purpose)
        {
            ResolveRand();
            if (setRandState == null)
            {
                Log.WarningOnce("[IsoRim] -" + Arg + ": Rand's state could not be set; " + purpose + " runs from a random state.", SetStateWarningKey);
                return;
            }
            setRandState((uint)StateFor(purpose));
        }

        // Patched in at start-up (Patches.ApplyAll) with -isorimseed only.
        internal static void Apply(Harmony h)
        {
            if (!Value.HasValue)
            {
                return;
            }
            MethodBase setup = AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay), Type.EmptyTypes);
            h.Patch(setup, prefix: new HarmonyMethod(typeof(QuickTestSeed), nameof(Setup_Prefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(QuickTestSeed), nameof(Setup_Finalizer)));
            h.Patch(AccessTools.Method(typeof(Game), nameof(Game.InitNewGame), Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(QuickTestSeed), nameof(InitNewGame_Postfix)) { priority = Priority.Last });
            HarmonyMethod swap = new HarmonyMethod(typeof(QuickTestSeed), nameof(StableSeeds_Transpiler));
            foreach (Target t in Targets)
            {
                Api.WatchCopy(t.method, t.hash, "IsoRim -" + Arg + ", the stable seed in " + t.name);
                if (t.method != null)
                {
                    h.Patch(t.method, transpiler: swap);
                }
            }
            ResolveRand();
            if (getRandState != null && setRandState != null)
            {
                HarmonyMethod keep = new HarmonyMethod(typeof(QuickTestSeed), nameof(Sound_Prefix)) { priority = Priority.First };
                HarmonyMethod restore = new HarmonyMethod(typeof(QuickTestSeed), nameof(Sound_Finalizer));
                foreach (MethodBase m in SoundEntries())
                {
                    if (m != null)
                    {
                        h.Patch(m, prefix: keep, finalizer: restore);
                    }
                }
            }
            else
            {
                Log.Warning("[IsoRim] -" + Arg + ": Rand's state cannot be read or set; sounds and music are not kept out of it.");
            }
            Log.Message("[IsoRim] -" + Arg + "=" + Value.Value + ": quick test seeded (world, generation seeds, start of play; sounds kept out of the random stream).");
        }

        public static void Setup_Prefix()
        {
            Rand.PushState(Value.GetValueOrDefault());
            setupPushed = true;
            quickTestStarting = true;
        }

        public static void Setup_Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                quickTestStarting = false;   // no quick test follows: a later new game is not given the play-start state
            }
            if (setupPushed)
            {
                setupPushed = false;
                Rand.PopState();
                SetState(NewGame);
            }
        }

        // The quick test's new game (not a game started from the menus) is set up: play starts from StateFor(PlayStart),
        // paused, once the loading event has finished (as the game's own pause on load, LongEventHandler.ExecuteWhenFinished).
        public static void InitNewGame_Postfix()
        {
            if (!quickTestStarting)
            {
                return;
            }
            quickTestStarting = false;
            LongEventHandler.ExecuteWhenFinished(StartPlay);
        }

        private static void StartPlay()
        {
            SetState(PlayStart);
            if (Find.TickManager != null)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            }
        }

        // Rand's private StateCompressed (seed and iterations) as a delegate; null when it cannot be found.
        private static T RandStateAccessor<T>(MethodInfo accessor) where T : Delegate
        {
            try
            {
                return accessor == null ? null : (T)Delegate.CreateDelegate(typeof(T), accessor);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- sounds and music (4)

        public static void Sound_Prefix(out ulong? __state)
        {
            __state = UnityData.IsInMainThread && !LongEventHandler.ShouldWaitForEvent ? getRandState() : (ulong?)null;
        }

        public static void Sound_Finalizer(ulong? __state)
        {
            if (__state.HasValue)
            {
                setRandState(__state.Value);
            }
        }

        // ---- stable generation seeds

        public static IEnumerable<CodeInstruction> StableSeeds_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int swapped = Swap(list);
            Target t = Array.Find(Targets, x => x.method == original);
            if (t != null && swapped != t.calls)
            {
                Log.Warning("[IsoRim] -" + Arg + ": " + t.name + " has " + swapped + " HashCode.Combine calls, " + t.calls + " expected; its seeds may need a check.");
            }
            return list;
        }

        // Swaps every HashCode.Combine call of two or three values in the list for Combine; returns how many.
        internal static int Swap(List<CodeInstruction> list)
        {
            int swapped = 0;
            foreach (CodeInstruction ci in list)
            {
                MethodInfo stable = ci.opcode == OpCodes.Call ? StableFor(ci.operand as MethodInfo) : null;
                if (stable != null)
                {
                    ci.operand = stable;
                    swapped++;
                }
            }
            return swapped;
        }

        // The stable counterpart of a HashCode.Combine instantiation, null for anything else.
        internal static MethodInfo StableFor(MethodInfo m)
        {
            if (m == null || m.DeclaringType != typeof(HashCode) || m.Name != nameof(HashCode.Combine) || !m.IsGenericMethod)
            {
                return null;
            }
            Type[] args = m.GetGenericArguments();
            MethodInfo shim = args.Length == 2 ? Combine2 : args.Length == 3 ? Combine3 : null;
            return shim?.MakeGenericMethod(args);
        }

        // HashCode.Combine's inputs and per-value hashes, mixed by Gen.HashCombineInt: the same every process.
        public static int Combine<T1, T2>(T1 a, T2 b)
        {
            return Gen.HashCombineInt(HashOf(a), HashOf(b));
        }

        public static int Combine<T1, T2, T3>(T1 a, T2 b, T3 c)
        {
            return Gen.HashCombineInt(Gen.HashCombineInt(HashOf(a), HashOf(b)), HashOf(c));
        }

        internal static int HashOf<T>(T value)
        {
            return value == null ? 0 : value.GetHashCode();
        }
    }
}
