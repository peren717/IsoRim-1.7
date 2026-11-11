using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only parity check for -isorimseed's stable generation seeds (QuickTestSeed.cs; run with -isorimseed=N):
    //   seedparity=label   for every QuickTestSeed target:
    //     watch      its hash now is the one registered (Api.WatchCopy gives no warning)
    //     patched    IsoRim's transpiler is on it
    //     swap       its original IL with the swap applied differs only in the swapped calls: as many as expected, each a
    //                HashCode.Combine<T..> replaced by QuickTestSeed.Combine<T..> with the same type arguments
    //   and value parity in this process, for Map.NextGenSeed (the current map) and Gravship.ConstantRandSeed (a gravship with
    //   a fixed ID): vanilla's original body (a reverse patch) and the same body with its HashCode.Combine recorded give the
    //   same value (so the inputs are vanilla's), the live patched getter gives QuickTestSeed.Combine of those inputs, and
    //   NextGenSeed advances the map's generatedId by one, as vanilla.
    //   The same watch, patched (prefix and transpiler) and swap checks (Time.deltaTime -> ldarg.0 + FixedFrameTime, the
    //   budget's Stopwatch.ElapsedMilliseconds -> NoBudget) for TickManager.TickManagerUpdate, the fixed ticks per frame, and
    //   that the swap is active; IsoRim's TickManager.DoSingleTick prefix runs first (Priority.First).
    //   PASS/FAIL lines, then "total n PASS, m FAIL".
    //   seedstate=label    one line for comparing runs: TicksGame, Rand's state, the map's things (count, position hash), the
    //                      water bodies with fish, and every spawned pawn's position and job.
    //   tickframes=label,<TimeSpeed>:<frames>,...   the game run at each speed for that many frames in turn (the game's own
    //                      frames: TickManager.TickManagerUpdate), then paused; per speed one line with every frame's ticks
    //                      (TickManager.TicksThisFrame) and PASS when each frame ticked the game's own multiplier
    //                      (TickRateMultiplier rounded, read before the frame's ticks; 0 paused), and per speed change PASS
    //                      when the first frame at the new speed followed it; then "tickframes label: total n PASS, m FAIL".
    //                      Fits a Capture17 pass (its 120 settle frames).
    //   tickstate=label    real ticks (DoSingleTick): a plain tick runs under StateFor(Tick, its number); two ticks under a
    //                      state the probe pushed around them, as a runner would, run under that state (the same roll at
    //                      different ticks); a plain tick after them is seeded again and the stack depth is back.
    //   framestate=label,ms,frames   paused for ms of real time (a different number of frames each run), then that many
    //                      frames at Normal, each frame's Rand state logged at its end; compare the "framestate label:" line
    //                      of two runs (the "wait:" line says how many paused frames the wait took).
    public static partial class Probe
    {
        private const int SeedParityGravshipId = 4242;   // any ID: the check compares bodies on the same input

        private static readonly int[] recordedInputs = new int[3];
        private static int recordedCount;
        private static bool seedParityPatched;

        private static bool SeedKey(string key, string[] a, Map map)
        {
            if (key == "seedstate")
            {
                SeedState(a[0], map ?? Find.CurrentMap);
                return true;
            }
            if (key == "tickframes")
            {
                TickFramesStart(a);
                return true;
            }
            if (key == "tickstate")
            {
                TickStateCheck(a[0]);
                return true;
            }
            if (key == "framestate")
            {
                FrameStateStart(a);
                return true;
            }
            if (key != "seedparity")
            {
                return false;
            }
            string L = "[IsoRim probe] seedparity " + a[0] + " ";
            int pass = 0, fail = 0;
            void C(string what, bool ok, string detail)
            {
                if (ok)
                {
                    pass++;
                }
                else
                {
                    fail++;
                }
                Log.Message(L + what + ": " + detail + " -> " + (ok ? "PASS" : "FAIL"));
            }
            C("seed", Api.HarnessSeed.HasValue, "Api.HarnessSeed " + (Api.HarnessSeed?.ToString() ?? "null") + ", Api.Version " + Api.Version);
            foreach (QuickTestSeed.Target t in QuickTestSeed.Targets)
            {
                string now = Api.CopyHash(t.method);
                C(t.name + " watch", now != null && now == t.hash, "hash now " + (now ?? "null") + ", registered " + t.hash);
                bool patched = t.method != null && (Harmony.GetPatchInfo(t.method)?.Transpilers.Any(p => p.owner == Patches.HarmonyId) ?? false);
                C(t.name + " patched", patched, "IsoRim transpiler " + (patched ? "on" : "missing"));
                C(t.name + " swap", SwapOnly(t, out string detail), detail);
            }
            MethodBase loop = QuickTestSeed.TickLoop;
            string loopNow = Api.CopyHash(loop);
            C("TickManager.TickManagerUpdate watch", loopNow != null && loopNow == QuickTestSeed.TickLoopHash, "hash now " + (loopNow ?? "null") + ", registered " + QuickTestSeed.TickLoopHash);
            HarmonyLib.Patches loopPatches = loop == null ? null : Harmony.GetPatchInfo(loop);
            bool loopPatched = loopPatches != null && loopPatches.Transpilers.Any(p => p.owner == Patches.HarmonyId) && loopPatches.Prefixes.Any(p => p.owner == Patches.HarmonyId);
            C("TickManager.TickManagerUpdate patched", loopPatched, "IsoRim prefix and transpiler " + (loopPatched ? "on" : "missing"));
            C("TickManager.TickManagerUpdate swap", TickLoopSwapOnly(loop, out string loopDetail), loopDetail);
            C("TickManager.TickManagerUpdate active", QuickTestSeed.TickLoopActive, "every expected call swapped: " + QuickTestSeed.TickLoopActive);
            HarmonyLib.Patch tickPrefix = Harmony.GetPatchInfo(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)))?.Prefixes
                .FirstOrDefault(p => p.owner == Patches.HarmonyId && p.PatchMethod.Name == nameof(QuickTestSeed.Tick_Prefix));
            C("TickManager.DoSingleTick prefix first", tickPrefix != null && tickPrefix.priority == Priority.First,
                "IsoRim's tick prefix " + (tickPrefix == null ? "missing" : "priority " + tickPrefix.priority + " (First " + Priority.First + ")"));
            try
            {
                SeedParityReversePatches();
                Map m = map ?? Find.CurrentMap;
                AccessTools.FieldRef<Map, int> genId = AccessTools.FieldRefAccess<Map, int>("generatedId");
                int id0 = genId(m);
                int vanilla = VanillaNextGenSeed(m);
                genId(m) = id0;
                recordedCount = 0;
                int recorded = RecordedNextGenSeed(m);
                genId(m) = id0;
                int[] inputs = recordedInputs.Take(recordedCount).ToArray();
                int live = m.NextGenSeed;
                int advanced = genId(m) - id0;
                genId(m) = id0;
                int stable = inputs.Length == 3 ? QuickTestSeed.Combine(inputs[0], inputs[1], inputs[2]) : 0;
                C("Map.NextGenSeed inputs", inputs.Length == 3 && recorded == vanilla, "inputs [" + string.Join(", ", inputs) + "], vanilla "
                    + vanilla + ", recorded body " + recorded);
                C("Map.NextGenSeed live", inputs.Length == 3 && live == stable && advanced == 1, "live " + live + ", stable of the inputs "
                    + stable + ", generatedId +" + advanced);

                Gravship g = (Gravship)RuntimeHelpers.GetUninitializedObject(typeof(Gravship));
                g.ID = SeedParityGravshipId;
                int gVanilla = VanillaGravshipSeed(g);
                recordedCount = 0;
                int gRecorded = RecordedGravshipSeed(g);
                int[] gInputs = recordedInputs.Take(recordedCount).ToArray();
                int gLive = g.ConstantRandSeed;
                int gStable = gInputs.Length == 2 ? QuickTestSeed.Combine(gInputs[0], gInputs[1]) : 0;
                C("Gravship.ConstantRandSeed inputs", gInputs.Length == 2 && gRecorded == gVanilla, "inputs [" + string.Join(", ", gInputs)
                    + "], vanilla " + gVanilla + ", recorded body " + gRecorded);
                C("Gravship.ConstantRandSeed live", gInputs.Length == 2 && gLive == gStable, "live " + gLive + ", stable of the inputs " + gStable);
            }
            catch (Exception e)
            {
                C("value parity", false, e.ToString());
            }
            Log.Message(L + "total " + pass + " PASS, " + fail + " FAIL");
            return true;
        }

        private static void SeedState(string label, Map m)
        {
            ulong? r = QuickTestSeed.RandStateNow();
            long hash = 0;
            foreach (Thing t in m.listerThings.AllThings)
            {
                hash += (long)Gen.HashCombineInt(Gen.HashCombineInt(t.def.shortHash, t.Position.x), t.Position.z) * 2654435761L;
            }
            int fishBodies = m.waterBodyTracker?.Bodies.Count(b => b.HasFish) ?? 0;
            string pawns = string.Join("; ", m.mapPawns.AllPawnsSpawned.OrderBy(p => p.thingIDNumber).Select(p => p.def.defName + "@" + p.Position + " " + (p.CurJobDef?.defName ?? "-")));
            Log.Message("[IsoRim probe] seedstate " + label + ": TicksGame " + Find.TickManager.TicksGame + ", rand " + (r.HasValue ? (uint)r.Value + "/" + (uint)(r.Value >> 32) : "?")
                + ", stack " + RandStackDepth() + ", things " + m.listerThings.AllThings.Count + " hash " + hash + ", water bodies with fish " + fishBodies + ", pawns [" + pawns + "]");
        }

        private static int RandStackDepth() => ((Stack<ulong>)AccessTools.Field(typeof(Rand), "stateStack").GetValue(null)).Count;

        // The original IL against the same IL with QuickTestSeed's swap applied: each differing instruction a HashCode.Combine
        // replaced by exactly QuickTestSeed.StableFor of it.
        private static bool SwapOnly(QuickTestSeed.Target t, out string detail)
        {
            if (t.method == null)
            {
                detail = "method not found";
                return false;
            }
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(t.method);
            List<CodeInstruction> swapped = original.Select(ci => ci.Clone()).ToList();
            int n = QuickTestSeed.Swap(swapped);
            bool only = DiffOnly(original, swapped, (o, s, j) =>
                o.opcode == OpCodes.Call && s[j].opcode == OpCodes.Call && o.operand is MethodInfo was
                && QuickTestSeed.StableFor(was) is MethodInfo stable && Equals(s[j].operand, stable) ? 1 : 0, out int differing);
            detail = "instructions " + original.Count + ", swapped " + n + " (expected " + t.calls + "), differing " + differing
                + (only ? ", each a HashCode.Combine replaced by its stable counterpart" : ", NOT only HashCode.Combine calls");
            return n == t.calls && differing == n && only;
        }

        // TickManagerUpdate's original IL against QuickTestSeed.SwapTickLoop of it: each differing instruction a Time.deltaTime
        // call replaced by ldarg.0 + FixedFrameTime, or a Stopwatch.ElapsedMilliseconds call replaced by NoBudget (the
        // resolved MethodInfos QuickTestSeed swaps with).
        private static bool TickLoopSwapOnly(MethodBase loop, out string detail)
        {
            if (loop == null)
            {
                detail = "method not found";
                return false;
            }
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(loop);
            List<CodeInstruction> swapped = QuickTestSeed.SwapTickLoop(original, out int delta, out int budget);
            bool only = DiffOnly(original, swapped, (o, s, j) =>
            {
                MethodInfo was = o.operand as MethodInfo;
                if (o.opcode == OpCodes.Call && was == QuickTestSeed.DeltaTimeGetter && s[j].opcode == OpCodes.Ldarg_0 && j + 1 < s.Count
                    && s[j + 1].opcode == OpCodes.Call && Equals(s[j + 1].operand, QuickTestSeed.FixedFrameTimeMethod))
                {
                    return 2;
                }
                return (o.opcode == OpCodes.Call || o.opcode == OpCodes.Callvirt) && was == QuickTestSeed.ElapsedGetter
                    && s[j].opcode == OpCodes.Call && Equals(s[j].operand, QuickTestSeed.NoBudgetMethod) ? 1 : 0;
            }, out int differing);
            detail = "instructions " + original.Count + " -> " + swapped.Count + ", deltaTime " + delta + " (expected " + QuickTestSeed.TickLoopDeltaCalls
                + "), budget " + budget + " (expected " + QuickTestSeed.TickLoopBudgetCalls + "), differing " + differing
                + (only ? ", each a swapped call" : ", NOT only the swapped calls");
            return delta == QuickTestSeed.TickLoopDeltaCalls && budget == QuickTestSeed.TickLoopBudgetCalls && differing == delta + budget && only;
        }

        // Walks an original instruction list and a swapped one together: equal instructions (opcode and operand) match one
        // for one; any other original instruction must be a replacement `allowed` accepts, which returns how many swapped
        // instructions (from index j) replace it, 0 when it does not. False on anything else or on leftovers.
        private static bool DiffOnly(List<CodeInstruction> original, List<CodeInstruction> swapped,
            Func<CodeInstruction, List<CodeInstruction>, int, int> allowed, out int differing)
        {
            differing = 0;
            int j = 0;
            for (int i = 0; i < original.Count; i++)
            {
                if (j < swapped.Count && original[i].opcode == swapped[j].opcode && Equals(original[i].operand, swapped[j].operand))
                {
                    j++;
                    continue;
                }
                int n = j < swapped.Count ? allowed(original[i], swapped, j) : 0;
                if (n <= 0)
                {
                    return false;
                }
                differing++;
                j += n;
            }
            return j == swapped.Count;
        }

        // ---- tickframes: ticks per game frame at given speeds

        private sealed class TickFrameSegment
        {
            public TimeSpeed speed;
            public int frames;
            public readonly List<int> ticks = new List<int>();
            public readonly List<int> expected = new List<int>();
        }

        private static List<TickFrameSegment> tickFrameSegments;
        private static bool tickFramesActive;
        private static int tickFrameIdx;
        private static string tickFrameLabel;
        private static int tickFrameStartTicks;
        private static bool tickFramesHooked;

        private static void TickFramesStart(string[] a)
        {
            tickFrameLabel = a[0];
            List<TickFrameSegment> segments = new List<TickFrameSegment>();
            for (int i = 1; i < a.Length; i++)
            {
                string[] sf = a[i].Split(':');
                if (sf.Length != 2 || !Enum.TryParse(sf[0], out TimeSpeed speed) || !int.TryParse(sf[1], out int n) || n < 1)
                {
                    Log.Message("[IsoRim probe] tickframes " + tickFrameLabel + ": bad segment '" + a[i] + "' (want <TimeSpeed>:<frames>) -> FAIL");
                    return;
                }
                segments.Add(new TickFrameSegment { speed = speed, frames = n });
            }
            if (segments.Count == 0)
            {
                Log.Message("[IsoRim probe] tickframes " + tickFrameLabel + ": no segments -> FAIL");
                return;
            }
            if (!tickFramesHooked)
            {
                tickFramesHooked = true;
                new Harmony("peren.isorim17.probetickframes").Patch(QuickTestSeed.TickLoop,
                    prefix: new HarmonyMethod(typeof(Probe), nameof(TickFrames_Prefix)) { priority = Priority.Last },
                    postfix: new HarmonyMethod(typeof(Probe), nameof(TickFrames_Postfix)));
            }
            tickFrameSegments = segments;
            tickFrameStartTicks = Find.TickManager.TicksGame;
            tickFrameIdx = 0;
            tickFramesActive = true;
            Find.TickManager.CurTimeSpeed = segments[0].speed;
        }

        // What this frame should tick, read from the game before the loop runs: none while paused, else its own speed
        // multiplier (TickRateMultiplier) rounded.
        public static void TickFrames_Prefix(TickManager __instance)
        {
            if (tickFramesActive)
            {
                tickFrameSegments[tickFrameIdx].expected.Add(__instance.Paused ? 0 : Mathf.RoundToInt(__instance.TickRateMultiplier));
            }
        }

        public static void TickFrames_Postfix(TickManager __instance)
        {
            if (!tickFramesActive)
            {
                return;
            }
            TickFrameSegment s = tickFrameSegments[tickFrameIdx];
            s.ticks.Add(__instance.TicksThisFrame);
            if (s.ticks.Count < s.frames)
            {
                return;
            }
            if (++tickFrameIdx < tickFrameSegments.Count)
            {
                __instance.CurTimeSpeed = tickFrameSegments[tickFrameIdx].speed;   // takes effect from the next frame
                return;
            }
            tickFramesActive = false;
            __instance.CurTimeSpeed = TimeSpeed.Paused;
            int pass = 0, fail = 0;
            void C(string what, bool ok)
            {
                if (ok)
                {
                    pass++;
                }
                else
                {
                    fail++;
                }
                Log.Message("[IsoRim probe] tickframes " + tickFrameLabel + " " + what + " -> " + (ok ? "PASS" : "FAIL"));
            }
            for (int i = 0; i < tickFrameSegments.Count; i++)
            {
                TickFrameSegment g = tickFrameSegments[i];
                C(g.speed + ": " + g.ticks.Count + " frames, ticks " + RunLengths(g.ticks) + ", the game's multiplier " + RunLengths(g.expected),
                    g.ticks.Count == g.expected.Count && g.ticks.SequenceEqual(g.expected));
                if (i > 0)
                {
                    // the speed changed between two frames: the first frame at the new speed already ticks its multiplier
                    TickFrameSegment p = tickFrameSegments[i - 1];
                    int before = p.ticks[p.ticks.Count - 1], after = g.ticks[0];
                    C("change " + p.speed + " -> " + g.speed + ": last frame before " + before + ", first frame after " + after
                        + " (the game's multiplier " + g.expected[0] + ")", after == g.expected[0] && before == p.expected[p.expected.Count - 1]);
                }
            }
            Log.Message("[IsoRim probe] tickframes " + tickFrameLabel + ": TicksGame " + tickFrameStartTicks + " -> " + __instance.TicksGame
                + ", total " + pass + " PASS, " + fail + " FAIL");
        }

        // 3,3,3,6 -> "3x3 6x1"
        private static string RunLengths(List<int> l)
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < l.Count;)
            {
                int j = i;
                while (j < l.Count && l[j] == l[i])
                {
                    j++;
                }
                parts.Add(l[i] + "x" + (j - i));
                i = j;
            }
            return parts.Count == 0 ? "-" : string.Join(" ", parts);
        }

        // ---- tickstate: whose random state a tick runs under (QuickTestSeed 6)

        private const int TickStateRunnerSeed = 424242;   // any seed: the checks compare against it

        private static bool tickStateArmed;
        private static ulong? tickStateSeen;
        private static float tickStateRoll;
        private static bool tickStateHooked;

        // Runs after every other DoSingleTick prefix (IsoRim's is Priority.First): records the state the tick runs under and the
        // first roll it would draw (taken on a saved copy, so the tick itself is unchanged).
        public static void TickState_Prefix()
        {
            if (!tickStateArmed)
            {
                return;
            }
            tickStateArmed = false;
            tickStateSeen = QuickTestSeed.RandStateNow();
            Rand.PushState();
            tickStateRoll = Rand.Value;
            Rand.PopState();
        }

        // One real tick (TickManager.DoSingleTick), with or without a state pushed around it as a runner would: the state the
        // tick ran under, its first roll and its tick number.
        private static (ulong? state, float roll, int tick) TickUnder(int? runnerState)
        {
            TickManager tm = Find.TickManager;
            int tick = tm.TicksGame;
            if (runnerState.HasValue)
            {
                Rand.PushState(runnerState.Value);
            }
            try
            {
                tickStateSeen = null;
                tickStateArmed = true;
                tm.DoSingleTick();
            }
            finally
            {
                tickStateArmed = false;
                if (runnerState.HasValue)
                {
                    Rand.PopState();
                }
            }
            return (tickStateSeen, tickStateRoll, tick);
        }

        // tickstate=label: a tick without a runner's state runs under StateFor(Tick, its tick number); a tick under a state a
        // runner pushed runs under that state (the same roll at another tick); afterwards plain ticks are seeded again.
        private static void TickStateCheck(string label)
        {
            if (!tickStateHooked)
            {
                tickStateHooked = true;
                new Harmony("peren.isorim17.probetickstate").Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)),
                    prefix: new HarmonyMethod(typeof(Probe), nameof(TickState_Prefix)) { priority = Priority.Last });
            }
            string L = "[IsoRim probe] tickstate " + label + " ";
            int pass = 0, fail = 0;
            void C(string what, bool ok, string detail)
            {
                if (ok)
                {
                    pass++;
                }
                else
                {
                    fail++;
                }
                Log.Message(L + what + ": " + detail + " -> " + (ok ? "PASS" : "FAIL"));
            }
            int depth0 = QuickTestSeed.RandDepth() ?? -1;
            var plain1 = TickUnder(null);
            ulong want1 = (uint)QuickTestSeed.StateFor(QuickTestSeed.Tick, plain1.tick);
            C("plain tick", plain1.state == want1, "tick " + plain1.tick + ": state " + RandText(plain1.state) + ", StateFor(tick) " + RandText(want1)
                + ", roll " + plain1.roll.ToString("0.000000"));
            var runA = TickUnder(TickStateRunnerSeed);
            var runB = TickUnder(TickStateRunnerSeed);
            ulong wantRunner = (uint)TickStateRunnerSeed;
            C("runner's state wins", runA.state == wantRunner && runB.state == wantRunner, "ticks " + runA.tick + " and " + runB.tick + ": states "
                + RandText(runA.state) + " and " + RandText(runB.state) + ", runner's " + RandText(wantRunner));
            C("runner's state steers the rolls", runA.roll == runB.roll && runA.roll != plain1.roll, "rolls " + runA.roll.ToString("0.000000") + " and "
                + runB.roll.ToString("0.000000") + " (same state, different ticks), plain tick's " + plain1.roll.ToString("0.000000"));
            var plain2 = TickUnder(null);
            ulong want2 = (uint)QuickTestSeed.StateFor(QuickTestSeed.Tick, plain2.tick);
            C("plain tick again", plain2.state == want2 && (QuickTestSeed.RandDepth() ?? -1) == depth0, "tick " + plain2.tick + ": state " + RandText(plain2.state)
                + ", StateFor(tick) " + RandText(want2) + "; stack depth " + depth0 + " -> " + (QuickTestSeed.RandDepth()?.ToString() ?? "?"));
            Log.Message(L + "total " + pass + " PASS, " + fail + " FAIL");
            foreach (Pawn tp in Find.CurrentMap.mapPawns.AllPawnsSpawned)
            {
                tp.Drawer.tweener.ResetTweenedPosToRoot();   // paused game: the drawn position follows the ticks
            }
            lastTicks = Find.TickManager.TicksGame;   // no clock jump (no next pass)
        }

        // ---- framestate: the frame states after a wait on real time (QuickTestSeed 5)

        private enum FrameStatePhase { None, Wait, Run }

        private static FrameStatePhase framePhase;
        private static string frameLabel;
        private static float frameWaitUntil;
        private static int frameWaited, frameLeft;
        private static List<string> frameStates;
        private static bool frameStateHooked;

        // framestate=label,ms,frames: the game paused for ms of real time (like a wait on a screenshot file: a different
        // number of frames every run), then running at Normal for that many frames, each frame's state logged at its end
        // (after the frame's own draws), then paused.
        private static void FrameStateStart(string[] a)
        {
            if (a.Length != 3 || !int.TryParse(a[1], out int ms) || !int.TryParse(a[2], out int n) || ms < 0 || n < 1)
            {
                Log.Message("[IsoRim probe] framestate " + a[0] + ": want framestate=label,ms,frames -> FAIL");
                return;
            }
            if (!frameStateHooked)
            {
                frameStateHooked = true;
                new Harmony("peren.isorim17.probeframestate").Patch(AccessTools.Method(typeof(Game), nameof(Game.UpdatePlay)),
                    postfix: new HarmonyMethod(typeof(Probe), nameof(FrameState_Postfix)) { priority = Priority.Last });
            }
            frameLabel = a[0];
            frameWaitUntil = Time.realtimeSinceStartup + ms / 1000f;
            frameWaited = 0;
            frameLeft = n;
            frameStates = new List<string>();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            framePhase = FrameStatePhase.Wait;
        }

        // End of a frame (before IsoRim's finalizer pops the frame's state).
        public static void FrameState_Postfix(Game __instance)
        {
            if (framePhase == FrameStatePhase.None)
            {
                return;
            }
            TickManager tm = __instance.tickManager;
            string state = "TicksGame " + tm.TicksGame + " rand " + RandText(QuickTestSeed.RandStateNow());
            if (framePhase == FrameStatePhase.Wait)
            {
                frameWaited++;
                if (Time.realtimeSinceStartup < frameWaitUntil)
                {
                    return;
                }
                frameStates.Add("paused " + state);
                tm.CurTimeSpeed = TimeSpeed.Normal;
                framePhase = FrameStatePhase.Run;
                return;
            }
            frameStates.Add(state);
            if (--frameLeft > 0)
            {
                return;
            }
            framePhase = FrameStatePhase.None;
            tm.CurTimeSpeed = TimeSpeed.Paused;
            Log.Message("[IsoRim probe] framestate " + frameLabel + " wait: " + frameWaited + " paused frames (real time; differs per run)");
            Log.Message("[IsoRim probe] framestate " + frameLabel + ": " + string.Join("; ", frameStates));
        }

        private static string RandText(ulong? s) => s.HasValue ? (uint)s.Value + "/" + (uint)(s.Value >> 32) : "?";

        private static void SeedParityReversePatches()
        {
            if (seedParityPatched)
            {
                return;
            }
            seedParityPatched = true;
            MethodInfo record = AccessTools.Method(typeof(Probe), nameof(SeedRecord_Transpiler));
            MethodBase nextGen = AccessTools.PropertyGetter(typeof(Map), nameof(Map.NextGenSeed));
            MethodBase gravship = AccessTools.PropertyGetter(typeof(Gravship), nameof(Gravship.ConstantRandSeed));
            Harmony.ReversePatch(nextGen, new HarmonyMethod(typeof(Probe), nameof(VanillaNextGenSeed)) { reversePatchType = HarmonyReversePatchType.Original });
            Harmony.ReversePatch(nextGen, new HarmonyMethod(typeof(Probe), nameof(RecordedNextGenSeed)) { reversePatchType = HarmonyReversePatchType.Original }, record);
            Harmony.ReversePatch(gravship, new HarmonyMethod(typeof(Probe), nameof(VanillaGravshipSeed)) { reversePatchType = HarmonyReversePatchType.Original });
            Harmony.ReversePatch(gravship, new HarmonyMethod(typeof(Probe), nameof(RecordedGravshipSeed)) { reversePatchType = HarmonyReversePatchType.Original }, record);
        }

        // Reverse-patch stand-ins (their bodies are replaced by the originals' before the first call).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int VanillaNextGenSeed(Map instance) => throw new NotImplementedException("reverse patch");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int RecordedNextGenSeed(Map instance) => throw new NotImplementedException("reverse patch");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int VanillaGravshipSeed(Gravship instance) => throw new NotImplementedException("reverse patch");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int RecordedGravshipSeed(Gravship instance) => throw new NotImplementedException("reverse patch");

        // In the recorded copies: HashCode.Combine<T..> becomes SeedRecord<T..>, which keeps the per-value hashes and returns
        // HashCode.Combine of the same values.
        public static IEnumerable<CodeInstruction> SeedRecord_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Call && ci.operand is MethodInfo m && m.DeclaringType == typeof(HashCode) && m.Name == nameof(HashCode.Combine) && m.IsGenericMethod)
                {
                    Type[] args = m.GetGenericArguments();
                    MethodInfo rec = AccessTools.FirstMethod(typeof(Probe), x => x.Name == nameof(SeedRecord) && x.GetGenericArguments().Length == args.Length);
                    if (rec != null)
                    {
                        ci.operand = rec.MakeGenericMethod(args);
                    }
                }
                yield return ci;
            }
        }

        public static int SeedRecord<T1, T2>(T1 a, T2 b)
        {
            recordedInputs[0] = QuickTestSeed.HashOf(a);
            recordedInputs[1] = QuickTestSeed.HashOf(b);
            recordedCount = 2;
            return HashCode.Combine(a, b);
        }

        public static int SeedRecord<T1, T2, T3>(T1 a, T2 b, T3 c)
        {
            recordedInputs[0] = QuickTestSeed.HashOf(a);
            recordedInputs[1] = QuickTestSeed.HashOf(b);
            recordedInputs[2] = QuickTestSeed.HashOf(c);
            recordedCount = 3;
            return HashCode.Combine(a, b, c);
        }
    }
}
