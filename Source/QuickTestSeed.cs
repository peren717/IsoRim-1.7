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
    //   4. Work that draws from Rand at moments that differ per run leaves Rand's state as it found it (saved and restored,
    //      the stack untouched). Sounds and music (SoundEntries: real-time sustainer samples and songs, mouse-over sounds, the
    //      sound slots' real-time limits; seen: a mouse-over sound before the first tick): on the main thread while the game
    //      is not waiting for a loading event. The fish shadow flecks (FishEntry: FishShadowComponent.MapComponentTick, timed
    //      by WaterBody.GetHashCode, which differs per launch): on the main thread, also inside a loading event.
    //   5. Frames: each Game.UpdatePlay (the game's ticks, every map's and component's once-a-frame update and drawing, and
    //      test runners driven from it) runs under StateFor(Frame, TicksGame at the frame's start), pushed and popped: what
    //      the frame draws no longer depends on how far the shared state was moved outside it (UI, frame timing). Keyed on the
    //      game tick, not on a frame count: frames with no tick in between (paused, waiting on a screenshot file or on real
    //      time) share one state, so how many of them there were changes nothing; a running game's frames differ by tick.
    //   6. Ticks: each TickManager.DoSingleTick runs under StateFor(Tick, TicksGame), pushed by a Priority.First prefix and
    //      popped by a finalizer, so other mods' prefixes and postfixes run under it too. A runner's explicit state wins:
    //      when Rand's stack is deeper than after our innermost push (ownDepth, 0 outside our pushes), someone pushed a state
    //      around the tick on purpose and nothing is pushed. A tick's randomness then depends on N, the tick number and the
    //      game state (or on the runner's state), not on how the ticks fell into frames.
    //   7. Loads: each Game.LoadGame runs under StateFor(Load, load number this session), pushed and popped: what a load
    //      draws starts from the same state every run.
    //   8. Ticks per frame: while the game runs, each frame advances TicksPerFrame game ticks (the game's own speed multiplier,
    //      TickRateMultiplier, rounded), not as many as real time allows. Vanilla's TickManager.TickManagerUpdate is the one
    //      place real time becomes ticks and keeps its own loop: a transpiler swaps its two Time.deltaTime reads for
    //      FixedFrameTime(this) and its real-time budget (clock.ElapsedMilliseconds) for none, and a prefix clears the time
    //      carried over from the last frame, so the loop runs exactly TicksPerFrame ticks. All or nothing: if the expected
    //      calls are not all found, the loop is left as it is (warned once). Paused stays paused (vanilla's own checks, also a
    //      pause forced mid-loop). Ticks a runner steps itself (DoSingleTick) are not counted or limited.
    // Each part is patched on its own (PatchPart): a missing method skips that part with a warning, not the rest.
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
        internal const string Frame = "frame";
        internal const string Tick = "tick";
        internal const string Load = "load";

        // Fixed ticks per frame (8): the frame time given is this much of a tick short of TicksPerFrame ticks, so the loop's
        // float sums never round to one tick more or less.
        private const float TickMargin = 0.5f;

        // TickManager.TickManagerUpdate (8): its CopyHash when the swap was written (game 1.7.5005), and how many calls the
        // swap replaces (Time.deltaTime twice, the budget's Stopwatch.ElapsedMilliseconds once). Not a copy: vanilla's loop
        // runs; watched so a change is noticed.
        internal const string TickLoopHash = "b6229658ac9d7002";
        internal const int TickLoopDeltaCalls = 2;
        internal const int TickLoopBudgetCalls = 1;

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
        private static Stack<ulong> randStack;   // Rand's private stateStack (static readonly: the same instance all session)
        private static bool randResolved;

        private static bool setupPushed;
        private static bool quickTestStarting;
        private static int loads;      // Game.LoadGame calls this session
        private static int ownDepth;   // Rand's stack depth right after our innermost frame, tick or load push (0 outside them)
        private static AccessTools.FieldRef<TickManager, float> carriedRealTime;   // TickManager.realTimeToTickThrough
        private static Func<TickManager, float> timePerTick;                       // TickManager.CurTimePerTick
        private static MethodInfo deltaTimeGetter, elapsedGetter, fixedFrameTime, noBudget;
        private static bool tickLoopActive;   // the transpiler swapped every expected call (else the prefix does nothing)
        private static bool tickLoopWarned;

        internal static bool TickLoopActive => tickLoopActive;

        // A pushed-state finalizer's __state when the prefix pushed nothing (any other value: ownDepth before the push).
        private const int NotPushed = -1;

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

        // The sound work kept out of the random stream (4): one-shot sounds, new sustainers, the per-frame sound update, the
        // music. The fish shadow flecks (FishEntry) have their own gate.
        private static (string, MethodBase)[] SoundEntries()
        {
            return new (string, MethodBase)[]
            {
                ("SoundStarter.PlayOneShot", AccessTools.Method(typeof(Verse.Sound.SoundStarter), nameof(Verse.Sound.SoundStarter.PlayOneShot), new[] { typeof(SoundDef), typeof(Verse.Sound.SoundInfo) })),
                ("SoundStarter.TrySpawnSustainer", AccessTools.Method(typeof(Verse.Sound.SoundStarter), nameof(Verse.Sound.SoundStarter.TrySpawnSustainer), new[] { typeof(SoundDef), typeof(Verse.Sound.SoundInfo) })),
                ("SoundRoot.Update", AccessTools.Method(typeof(Verse.Sound.SoundRoot), nameof(Verse.Sound.SoundRoot.Update), Type.EmptyTypes)),
                ("MusicManagerPlay.MusicUpdate", AccessTools.Method(typeof(MusicManagerPlay), nameof(MusicManagerPlay.MusicUpdate), Type.EmptyTypes)),
            };
        }

        private static MethodBase FishEntry => AccessTools.Method(typeof(FishShadowComponent), nameof(FishShadowComponent.MapComponentTick), Type.EmptyTypes);

        // Rand's private StateCompressed (seed and iterations) as delegates and its state stack, looked up once; null when
        // they cannot be found.
        private static void ResolveRand()
        {
            if (randResolved)
            {
                return;
            }
            randResolved = true;
            setRandState = RandStateAccessor<Action<ulong>>(AccessTools.PropertySetter(typeof(Rand), "StateCompressed"));
            getRandState = RandStateAccessor<Func<ulong>>(AccessTools.PropertyGetter(typeof(Rand), "StateCompressed"));
            randStack = AccessTools.Field(typeof(Rand), "stateStack")?.GetValue(null) as Stack<ulong>;
        }

        // Rand's state stack depth now; null when the stack cannot be read.
        internal static int? RandDepth()
        {
            ResolveRand();
            return randStack?.Count;
        }

        private static MethodInfo Combine2 => combine2 ?? (combine2 = AccessTools.FirstMethod(typeof(QuickTestSeed), m => m.Name == nameof(Combine) && m.GetGenericArguments().Length == 2));
        private static MethodInfo Combine3 => combine3 ?? (combine3 = AccessTools.FirstMethod(typeof(QuickTestSeed), m => m.Name == nameof(Combine) && m.GetGenericArguments().Length == 3));

        // A Rand seed derived from N for one purpose.
        internal static int StateFor(string purpose)
        {
            return Gen.HashCombineInt(Value.GetValueOrDefault(), GenText.StableStringHash(purpose));
        }

        // The n-th of a series of seeds for one purpose (frames, ticks, loads): mixed with n, so neighbours are unrelated.
        internal static int StateFor(string purpose, int n)
        {
            return Gen.HashCombineInt(StateFor(purpose), n);
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
            // Each part on its own: a part whose method is missing (or fails to patch) is skipped with a warning, the rest applies.
            PatchPart(h, "world setup", AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay), Type.EmptyTypes),
                prefix: Hook(nameof(Setup_Prefix), Priority.First), finalizer: Hook(nameof(Setup_Finalizer)));
            PatchPart(h, "start of play", AccessTools.Method(typeof(Game), nameof(Game.InitNewGame), Type.EmptyTypes),
                postfix: Hook(nameof(InitNewGame_Postfix), Priority.Last));
            foreach (Target t in Targets)
            {
                Api.WatchCopy(t.method, t.hash, "IsoRim -" + Arg + ", the stable seed in " + t.name);
                PatchPart(h, "the stable seed in " + t.name, t.method, transpiler: Hook(nameof(StableSeeds_Transpiler)));
            }
            ResolveRand();
            if (randStack == null)
            {
                Log.Warning("[IsoRim] -" + Arg + ": Rand's state stack cannot be read; every tick is seeded, also inside a runner's own state.");
            }
            PatchPart(h, "frames", AccessTools.Method(typeof(Game), nameof(Game.UpdatePlay), Type.EmptyTypes),
                prefix: Hook(nameof(Frame_Prefix), Priority.First), finalizer: Hook(nameof(Pushed_Finalizer)));
            PatchPart(h, "ticks", AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick), Type.EmptyTypes),
                prefix: Hook(nameof(Tick_Prefix), Priority.First), finalizer: Hook(nameof(Pushed_Finalizer)));
            PatchPart(h, "loads", AccessTools.Method(typeof(Game), nameof(Game.LoadGame), Type.EmptyTypes),
                prefix: Hook(nameof(Load_Prefix), Priority.First), finalizer: Hook(nameof(Pushed_Finalizer)));
            MethodBase tickLoop = TickLoop;
            Api.WatchCopy(tickLoop, TickLoopHash, "IsoRim -" + Arg + ", fixed ticks per frame in TickManager.TickManagerUpdate");
            if (ResolveTickLoop())
            {
                PatchPart(h, "fixed ticks per frame", tickLoop, prefix: Hook(nameof(TickLoop_Prefix)), transpiler: Hook(nameof(TickLoop_Transpiler)));
            }
            else
            {
                Log.Warning("[IsoRim] -" + Arg + ": TickManager's carried time or time per tick, Time.deltaTime or Stopwatch.ElapsedMilliseconds not found; ticks per frame follow real time.");
            }
            if (getRandState != null && setRandState != null)
            {
                foreach ((string name, MethodBase m) in SoundEntries())
                {
                    PatchPart(h, "keeping " + name + " out of the random stream", m, prefix: Hook(nameof(Keep_Prefix), Priority.First), finalizer: Hook(nameof(Keep_Finalizer)));
                }
                PatchPart(h, "keeping the fish shadows out of the random stream", FishEntry, prefix: Hook(nameof(KeepFish_Prefix), Priority.First), finalizer: Hook(nameof(Keep_Finalizer)));
            }
            else
            {
                Log.Warning("[IsoRim] -" + Arg + ": Rand's state cannot be read or set; sounds, music and fish shadows are not kept out of it.");
            }
            Log.Message("[IsoRim] -" + Arg + "=" + Value.Value + ": quick test seeded (world, generation seeds, start of play, frames, ticks, loads, fixed ticks per frame; sounds and fish shadows kept out of the random stream).");
        }

        private static HarmonyMethod Hook(string name, int priority = -1)
        {
            return new HarmonyMethod(typeof(QuickTestSeed), name) { priority = priority };
        }

        // One part of Apply: patched, or skipped with a warning when its method is missing or the patch fails.
        private static void PatchPart(Harmony h, string part, MethodBase method, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
        {
            if (method == null)
            {
                Log.Warning("[IsoRim] -" + Arg + ": the method for " + part + " was not found; that part is skipped.");
                return;
            }
            try
            {
                h.Patch(method, prefix, postfix, transpiler, finalizer);
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] -" + Arg + ": " + part + " could not be patched (" + e.Message + "); that part is skipped.");
            }
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

        // Rand's state now (seed in the low half, iterations in the high half), null when it cannot be read (the probe's seedstate).
        internal static ulong? RandStateNow()
        {
            ResolveRand();
            return getRandState?.Invoke();
        }

        // ---- frames, ticks, loads (5, 6, 7): pushed in the prefix (PushOwn), popped by Pushed_Finalizer

        // Pushes one of our states and records the stack depth after it (ownDepth); returns the ownDepth to restore.
        private static int PushOwn(int seed)
        {
            int before = ownDepth;
            Rand.PushState(seed);
            ownDepth = randStack?.Count ?? 0;
            return before;
        }

        // Keyed on the game tick the frame starts at, not on a frame count: frames without ticks in between (paused, waiting
        // on a file or on real time) share one state, so how many there were changes nothing.
        public static void Frame_Prefix(Game __instance, out int __state)
        {
            __state = PushOwn(StateFor(Frame, __instance.tickManager.TicksGame));
        }

        // A runner's own state wins: when Rand's stack is deeper than after our innermost push (0 outside our pushes), someone
        // pushed a state around this tick on purpose, and the tick runs under it. Otherwise StateFor(Tick, TicksGame).
        public static void Tick_Prefix(TickManager __instance, out int __state)
        {
            if (randStack != null && randStack.Count > ownDepth)
            {
                __state = NotPushed;
                return;
            }
            __state = PushOwn(StateFor(Tick, __instance.TicksGame));
        }

        public static void Load_Prefix(out int __state)
        {
            __state = PushOwn(StateFor(Load, loads++));
        }

        public static void Pushed_Finalizer(int __state)
        {
            if (__state != NotPushed)
            {
                Rand.PopState();
                ownDepth = __state;
            }
        }

        // ---- kept out of the random stream (4)

        // Sounds: on the main thread, not while a loading event runs (its own sounds belong to it).
        public static void Keep_Prefix(out ulong? __state)
        {
            __state = UnityData.IsInMainThread && !LongEventHandler.ShouldWaitForEvent ? getRandState() : (ulong?)null;
        }

        // Fish shadow flecks: a map component tick, also inside a loading event's ticks; only the main thread's state is
        // shared, so the main thread is the one gate.
        public static void KeepFish_Prefix(out ulong? __state)
        {
            __state = UnityData.IsInMainThread ? getRandState() : (ulong?)null;
        }

        public static void Keep_Finalizer(ulong? __state)
        {
            if (__state.HasValue)
            {
                setRandState(__state.Value);
            }
        }

        // ---- fixed ticks per frame (8)

        internal static MethodBase TickLoop => AccessTools.Method(typeof(TickManager), nameof(TickManager.TickManagerUpdate), Type.EmptyTypes);

        // TickManager's carried real time (realTimeToTickThrough) and time per tick (CurTimePerTick, private), looked up once;
        // false when either is missing (then nothing is patched).
        private static bool ResolveTickLoop()
        {
            if (AccessTools.Field(typeof(TickManager), "realTimeToTickThrough")?.FieldType == typeof(float))
            {
                carriedRealTime = AccessTools.FieldRefAccess<TickManager, float>("realTimeToTickThrough");
            }
            MethodInfo perTick = AccessTools.PropertyGetter(typeof(TickManager), "CurTimePerTick");
            timePerTick = perTick?.ReturnType == typeof(float)
                ? (Func<TickManager, float>)Delegate.CreateDelegate(typeof(Func<TickManager, float>), perTick) : null;
            return carriedRealTime != null && timePerTick != null && DeltaTimeGetter != null && ElapsedGetter != null;
        }

        // The calls the tick loop's swap replaces, and what replaces them (resolved once; the parity probe compares these).
        internal static MethodInfo DeltaTimeGetter => deltaTimeGetter ?? (deltaTimeGetter = AccessTools.PropertyGetter(typeof(UnityEngine.Time), nameof(UnityEngine.Time.deltaTime)));
        internal static MethodInfo ElapsedGetter => elapsedGetter ?? (elapsedGetter = AccessTools.PropertyGetter(typeof(System.Diagnostics.Stopwatch), nameof(System.Diagnostics.Stopwatch.ElapsedMilliseconds)));
        internal static MethodInfo FixedFrameTimeMethod => fixedFrameTime ?? (fixedFrameTime = AccessTools.Method(typeof(QuickTestSeed), nameof(FixedFrameTime)));
        internal static MethodInfo NoBudgetMethod => noBudget ?? (noBudget = AccessTools.Method(typeof(QuickTestSeed), nameof(NoBudget)));

        // Game ticks per frame while the game runs: the game's own speed multiplier, TickManager.TickRateMultiplier (ticks per
        // 1/60 s of play; for example Normal 1, Fast 3), rounded, at least one. The one place this rule lives.
        internal static int TicksPerFrame(TickManager tm)
        {
            return Math.Max(1, (int)Math.Round(tm.TickRateMultiplier));
        }

        // The frame time TickManagerUpdate adds instead of Time.deltaTime (both reads: its "close to one tick" test and the sum):
        // TicksPerFrame ticks of the game's own time per tick, less TickMargin, so its loop (tick while time is left) runs
        // exactly TicksPerFrame times. tm: the TickManager whose update runs (the swap passes it, ldarg.0).
        public static float FixedFrameTime(TickManager tm)
        {
            return (TicksPerFrame(tm) - TickMargin) * timePerTick(tm);
        }

        // Instead of the loop's real-time budget (clock.ElapsedMilliseconds, which ends a frame's ticks early on a slow frame):
        // no time spent, so the budget never ends the loop.
        public static long NoBudget(System.Diagnostics.Stopwatch clock)
        {
            return 0L;
        }

        // Each frame starts from no carried time (vanilla keeps a remainder from the last frame's real time). Only when the
        // transpiler swapped every expected call: otherwise vanilla's loop runs untouched.
        public static void TickLoop_Prefix(TickManager __instance)
        {
            if (tickLoopActive)
            {
                carriedRealTime(__instance) = 0f;
            }
        }

        // All or nothing: with every expected call found the swapped body, otherwise the original instructions, untouched.
        public static IEnumerable<CodeInstruction> TickLoop_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = new List<CodeInstruction>(instructions);
            List<CodeInstruction> swapped = SwapTickLoop(original, out int delta, out int budget);
            tickLoopActive = delta == TickLoopDeltaCalls && budget == TickLoopBudgetCalls;
            if (tickLoopActive)
            {
                return swapped;
            }
            if (!tickLoopWarned)
            {
                tickLoopWarned = true;
                Log.Warning("[IsoRim] -" + Arg + ": TickManager.TickManagerUpdate has " + delta + " Time.deltaTime and " + budget
                    + " budget reads, " + TickLoopDeltaCalls + " and " + TickLoopBudgetCalls + " expected; left as it is, ticks per frame follow real time.");
            }
            return original;
        }

        // A new list with each Time.deltaTime read replaced by ldarg.0 (the TickManager; it takes over the call's labels) and
        // a call of FixedFrameTime, and each Stopwatch.ElapsedMilliseconds read by a call of NoBudget (the Stopwatch the
        // instance call took becomes its argument). The given list and its instructions are left unchanged.
        internal static List<CodeInstruction> SwapTickLoop(List<CodeInstruction> list, out int delta, out int budget)
        {
            List<CodeInstruction> result = new List<CodeInstruction>(list.Count + TickLoopDeltaCalls);
            delta = budget = 0;
            foreach (CodeInstruction ci in list)
            {
                if (ci.opcode == OpCodes.Call && ci.operand is MethodInfo m && m == DeltaTimeGetter)
                {
                    result.Add(new CodeInstruction(OpCodes.Ldarg_0) { labels = new List<Label>(ci.labels), blocks = new List<ExceptionBlock>(ci.blocks) });
                    result.Add(new CodeInstruction(OpCodes.Call, FixedFrameTimeMethod));
                    delta++;
                }
                else if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo e && e == ElapsedGetter)
                {
                    result.Add(new CodeInstruction(OpCodes.Call, NoBudgetMethod) { labels = new List<Label>(ci.labels), blocks = new List<ExceptionBlock>(ci.blocks) });
                    budget++;
                }
                else
                {
                    result.Add(ci);
                }
            }
            return result;
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
