using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace IsoRim
{
    // Harness-only checks for Api 70 (Api.WatchCopy, Api.CopyHash; CopyWatch.cs). Warnings are counted in the game's log
    // (Log.Messages) by note. PASS/FAIL lines, then "total n PASS, m FAIL".
    //
    //   watchcopycheck=label   in game (no loading step: every registration is checked at once), once per launch (its
    //   fixtures get patched), notes unique to the label. Every check has its own game method, none patched by IsoRim:
    //     right        GenText.ToStringPercent(float) with its hash now                           -> no warning
    //     wrong        GenGrid.InBounds(Vector3, Map) with a wrong hash, registered twice          -> exactly one warning
    //     null         a null method                                                              -> exactly one warning
    //     missing      AccessTools.Method of a method that does not exist (null)                   -> exactly one warning
    //     patched      GenSight.LineOfSight(IntVec3, IntVec3, Map), an empty postfix put on it after its hash was taken,
    //                  registered with that hash                                                  -> no warning, the same hash
    //     prefix       GenMath.WeightedMedian: an empty prefix by one owner, then one by a second owner
    //     postfix      GenMath.CirclesOverlapArea: the same with postfixes
    //     transpiler   GenMath.GetFactorInInterval: the same with transpilers (each adds a nop: the body really changes)
    //     mixed        GenMath.Stddev (has a try/finally): prefix + postfix + transpiler by one owner, then the three again
    //                  by the second                                                              -> each of the four: the hash
    //                  before == after the first owner == after the second, both readings agree before the first patch,
    //                  and a watch registered with the hash before gives no warning
    //     Every patched fixture (patched, the four above, wrongpatched) also needs what reflection alone reads to change
    //     with each patch while the runtime's reader is in use: a patch that did not replace the body proves nothing.
    //     wrongpatched GenMath.DHondtDistribution with a prefix and a transpiler on it, a wrong hash, registered twice
    //                                                                                             -> exactly one warning
    //     fallback     GenMath.SphericalDistance (never patched), read through reflection only (CopyWatch.ReflectionOnly,
    //                  a host without the runtime's reader): the same hash as through the runtime's reader, counted as a
    //                  reflection read; a watch registered meanwhile with that hash               -> no warning
    //     host         the runtime's reader is in use (CopyWatch.HasHostReader). On a host without it this one fails, and
    //                  so do the patched ones: the hash follows the patches there, as before 2026-11-18.
    //   The hashes now are logged with those of the IsoRim 1.7 build of 2026-11-10 (game 1.7.4976) for comparison (info only:
    //   a game update may change them), and for each patched fixture what reflection alone reads (info: it follows the
    //   patches on the 1.7 host).
    //
    //   watchcopysweep=label[,every]   every method and constructor of the game's assembly and the engine's (the assembly
    //   of UnityEngine.Mesh) that has IL: its hash through the runtime's reader against its hash through reflection only.
    //   PASS: the runtime's reader is in use, it answered every method without a Harmony patch that has IL (reflection
    //   took over for none), and none of those differs (a method that is not patched hashes as it did before the
    //   runtime's reader). Counts: methods, those with try clauses, those the reader did not answer, patched ones (and
    //   how many of those read differently by reflection). Then the
    //   cost of one hash each way: the unpatched methods, every 'every'-th (default 8), three rounds each way in turn.
    //
    //   watchcopylist=label    the debug action's list, then one line per watch (see WatchCopyList).
    public static partial class Probe
    {
        private static Harmony watchCopyHarmony;
        private static bool watchCopyRan;

        public static void WatchCopyNoop_Postfix()
        {
        }

        public static void WatchCopyNoop_Prefix()
        {
        }

        public static IEnumerable<CodeInstruction> WatchCopyNop_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            yield return new CodeInstruction(OpCodes.Nop);
            foreach (CodeInstruction i in instructions)
            {
                yield return i;
            }
        }

        // (the second owner's: other methods, so the two owners' patches are two patches whatever Harmony does with a
        // method given twice)
        public static void WatchCopyNoop2_Postfix()
        {
        }

        public static void WatchCopyNoop2_Prefix()
        {
        }

        public static IEnumerable<CodeInstruction> WatchCopyNop2_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            yield return new CodeInstruction(OpCodes.Nop);
            foreach (CodeInstruction i in instructions)
            {
                yield return i;
            }
        }

        // The hash as a host without the runtime's reader gives it.
        private static string WatchCopyReflected(MethodBase method)
        {
            bool was = CopyWatch.ReflectionOnly;
            CopyWatch.ReflectionOnly = true;
            try
            {
                return Api.CopyHash(method);
            }
            finally
            {
                CopyWatch.ReflectionOnly = was;
            }
        }

        // watchcopylist=label: the debug action's list ("Log copied method hashes"), then one tab-separated line per watch:
        // number, note, method, hash now, hash registered, same or differs, and who has patched the method (Harmony
        // owners with their kinds of patch; "-" none).
        private static void WatchCopyList(string label)
        {
            string L = "[IsoRim probe] watchcopylist " + label + "\t";
            CopyWatch.LogHashes();
            int n = 0, same = 0;
            CopyWatch.Each((method, expected, note) =>
            {
                n++;
                string now = method == null ? null : Api.CopyHash(method);
                bool ok = now != null && now == expected;
                same += ok ? 1 : 0;
                string name = method == null ? "(not found)" : (method.DeclaringType == null ? "" : method.DeclaringType.FullName + ".") + method.Name;
                Log.Message(L + n + "\t" + note + "\t" + name + "\t" + (now ?? "-") + "\t" + (expected.Length == 0 ? "-" : expected) + "\t" + (ok ? "same" : "differs")
                    + "\t" + WatchCopyOwners(method));
            });
            Log.Message(L + "total " + n + " watches, " + same + " same, " + (n - same) + " differs; runtime reader " + (CopyWatch.HasHostReader ? "in use" : "not in use"));
        }

        private static string WatchCopyOwners(MethodBase method)
        {
            HarmonyLib.Patches info = method == null ? null : Harmony.GetPatchInfo(method);
            if (info == null)
            {
                return "-";
            }
            Dictionary<string, List<string>> byOwner = new Dictionary<string, List<string>>();
            void Kind(IEnumerable<Patch> patches, string kind)
            {
                foreach (Patch p in patches)
                {
                    if (!byOwner.TryGetValue(p.owner, out List<string> kinds))
                    {
                        byOwner[p.owner] = kinds = new List<string>();
                    }
                    kinds.Add(kind);
                }
            }
            Kind(info.Prefixes, "prefix");
            Kind(info.Postfixes, "postfix");
            Kind(info.Transpilers, "transpiler");
            Kind(info.Finalizers, "finalizer");
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, List<string>> o in byOwner)
            {
                parts.Add(o.Key + " [" + string.Join(", ", o.Value) + "]");
            }
            parts.Sort(System.StringComparer.Ordinal);
            return parts.Count == 0 ? "-" : string.Join("; ", parts);
        }

        private static void WatchCopySweep(string label, int every)
        {
            string L = "[IsoRim probe] watchcopysweep " + label + ": ";
            HashSet<MethodBase> patchedSet = new HashSet<MethodBase>(Harmony.GetAllPatchedMethods());
            List<MethodBase> plain = new List<MethodBase>();
            int methods = 0, noBody = 0, same = 0, differ = 0, patched = 0, patchedDiffer = 0, hostAnswered = 0, fellBack = 0, clauses = 0, clausesHost = 0, typeErrors = 0;
            List<string> examples = new List<string>();
            const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (Assembly asm in new[] { typeof(Thing).Assembly, typeof(UnityEngine.Mesh).Assembly })
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = Array.FindAll(e.Types, t => t != null);
                    typeErrors++;
                }
                foreach (Type t in types)
                {
                    List<MethodBase> members = new List<MethodBase>();
                    try
                    {
                        members.AddRange(t.GetMethods(Declared));
                        members.AddRange(t.GetConstructors(Declared));
                    }
                    catch (Exception)
                    {
                        typeErrors++;
                        continue;
                    }
                    foreach (MethodBase m in members)
                    {
                        methods++;
                        string reflected = WatchCopyReflected(m);
                        int before = CopyWatch.HostReads;
                        string now = Api.CopyHash(m);
                        bool host = CopyWatch.HostReads != before;
                        if (reflected == null && now == null)
                        {
                            noBody++;
                            continue;
                        }
                        if (patchedSet.Contains(m))
                        {
                            patched++;
                            patchedDiffer += now != reflected ? 1 : 0;
                            continue;
                        }
                        int clauseCount = 0;
                        try
                        {
                            clauseCount = m.GetMethodBody()?.ExceptionHandlingClauses.Count ?? 0;
                        }
                        catch (Exception)
                        {
                        }
                        clauses += clauseCount > 0 ? 1 : 0;
                        clausesHost += clauseCount > 0 && host ? 1 : 0;
                        hostAnswered += host ? 1 : 0;
                        fellBack += host ? 0 : 1;
                        if (now == reflected)
                        {
                            same++;
                            plain.Add(m);
                        }
                        else
                        {
                            differ++;
                            if (examples.Count < 10)
                            {
                                examples.Add((m.DeclaringType?.FullName ?? "?") + "." + m.Name + " " + (now ?? "null") + " vs " + (reflected ?? "null"));
                            }
                        }
                    }
                }
            }
            bool ok = differ == 0 && same > 0 && CopyWatch.HasHostReader && fellBack == 0;
            Log.Message(L + "methods " + methods + ", without IL " + noBody + ", patched " + patched + " (" + patchedDiffer + " of them read differently by reflection), not patched "
                + (same + differ) + ": same hash both ways " + same + ", different " + differ + "; the runtime's reader answered " + hostAnswered + ", reflection took over " + fellBack
                + "; with try clauses " + clauses + " (" + clausesHost + " answered by the reader); types that could not be listed " + typeErrors
                + "; runtime reader " + (CopyWatch.HasHostReader ? "in use, decided in " + CopyWatch.HostReaderMs.ToString("0.00") + " ms" : "not in use")
                + " -> " + (ok ? "PASS" : "FAIL"));
            foreach (string e in examples)
            {
                Log.Message(L + "differs: " + e);
            }
            // The cost of one hash each way (the reflection way is the whole of the reading before the runtime's reader).
            List<MethodBase> timed = new List<MethodBase>();
            for (int i = 0; i < plain.Count; i += Math.Max(1, every))
            {
                timed.Add(plain[i]);
            }
            double bestHost = double.MaxValue, bestReflected = double.MaxValue;
            for (int round = 0; round < 3 && timed.Count > 0; round++)
            {
                long t0 = Stopwatch.GetTimestamp();
                foreach (MethodBase m in timed)
                {
                    WatchCopyReflected(m);
                }
                long t1 = Stopwatch.GetTimestamp();
                foreach (MethodBase m in timed)
                {
                    Api.CopyHash(m);
                }
                long t2 = Stopwatch.GetTimestamp();
                double reflectedUs = (t1 - t0) * 1e6 / Stopwatch.Frequency / timed.Count, hostUs = (t2 - t1) * 1e6 / Stopwatch.Frequency / timed.Count;
                bestReflected = Math.Min(bestReflected, reflectedUs);
                bestHost = Math.Min(bestHost, hostUs);
                Log.Message(L + "cost round " + round + ": " + timed.Count + " methods, one hash through reflection " + reflectedUs.ToString("0.00") + " us, through the runtime's reader " + hostUs.ToString("0.00") + " us");
            }
            Log.Message(L + "cost (best of 3): reflection " + bestReflected.ToString("0.00") + " us, runtime's reader " + bestHost.ToString("0.00") + " us a hash");
            Log.Message(L + "total " + (ok ? "1 PASS, 0 FAIL" : "0 PASS, 1 FAIL"));
        }

        private static bool WatchCopyKey(string key, string[] a, Map map)
        {
            if (key == "watchcopylist")
            {
                WatchCopyList(a[0]);
                return true;
            }
            if (key == "watchcopysweep")
            {
                WatchCopySweep(a[0], a.Length > 1 && int.TryParse(a[1], out int every) ? every : 8);
                return true;
            }
            if (key != "watchcopycheck")
            {
                return false;
            }
            string label = a[0];
            string L = "[IsoRim probe] watchcopycheck " + label + ": ";
            string N = "IsoRim probe watchcopy " + label + " ";
            if (watchCopyRan)
            {
                Log.Message(L + "already run in this launch (its fixtures are patched): not run again");
                return true;
            }
            watchCopyRan = true;
            int pass = 0, fail = 0;

            MethodBase right = AccessTools.Method(typeof(GenText), nameof(GenText.ToStringPercent), new[] { typeof(float) });
            MethodBase wrong = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.InBounds), new[] { typeof(UnityEngine.Vector3), typeof(Map) });
            MethodBase patched = AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map) });
            MethodBase missing = AccessTools.Method(typeof(GenSight), "NoSuchMethodForWatchCopy");
            MethodBase fxPrefix = AccessTools.Method(typeof(GenMath), nameof(GenMath.WeightedMedian));
            MethodBase fxPostfix = AccessTools.Method(typeof(GenMath), nameof(GenMath.CirclesOverlapArea));
            MethodBase fxTranspiler = AccessTools.Method(typeof(GenMath), nameof(GenMath.GetFactorInInterval));
            MethodBase fxMixed = AccessTools.Method(typeof(GenMath), nameof(GenMath.Stddev));
            MethodBase fxWrongPatched = AccessTools.Method(typeof(GenMath), nameof(GenMath.DHondtDistribution));
            MethodBase fxFallback = AccessTools.Method(typeof(GenMath), nameof(GenMath.SphericalDistance));

            Harmony first = watchCopyHarmony = new Harmony("peren.isorim.probe.watchcopy");
            Harmony second = new Harmony("peren.isorim.probe.watchcopy.second");
            HarmonyMethod Own(string name) => new HarmonyMethod(typeof(Probe), name);

            string rightHash = Api.CopyHash(right);
            string patchedBefore = Api.CopyHash(patched), patchedReflectedBefore = WatchCopyReflected(patched);
            first.Patch(patched, postfix: Own(nameof(WatchCopyNoop_Postfix)));
            string patchedAfter = Api.CopyHash(patched), patchedReflectedAfter = WatchCopyReflected(patched);
            // (not inert: with the runtime's reader in use the patch replaced the body, so reflection alone reads another)
            bool patchedReplaced = !CopyWatch.HasHostReader || (patchedReflectedBefore != null && patchedReflectedAfter != null && patchedReflectedAfter != patchedReflectedBefore);
            int postfixes = Harmony.GetPatchInfo(patched)?.Postfixes.Count ?? 0;
            Log.Message(L + "Api.Version " + Api.Version + ", hashes: ToStringPercent " + rightHash + " (2026-11-10: 5407a64463aa6ffa), InBounds "
                + Api.CopyHash(wrong) + " (c97033b38f89c7a0), LineOfSight " + patchedBefore + " (f3b1c560f0d168f4), CopyHash(null) " + (Api.CopyHash(null) ?? "null")
                + "; LineOfSight by reflection alone, patched: " + patchedReflectedAfter);

            Api.WatchCopy(right, rightHash, N + "right");
            Api.WatchCopy(wrong, "0000000000000000", N + "wrong");
            Api.WatchCopy(wrong, "0000000000000000", N + "wrong");
            Api.WatchCopy(null, "0123456789abcdef", N + "null");
            Api.WatchCopy(missing, "0123456789abcdef", N + "missing");
            Api.WatchCopy(patched, patchedBefore, N + "patched");

            // A fixture patched by one owner, then by a second: its hash before, after one and after two; both readings
            // before; what Harmony holds after. Registered with the hash before, once both owners are on.
            Dictionary<string, (bool ok, string detail)> twice = new Dictionary<string, (bool, string)>();
            void Twice(string which, MethodBase m, bool pre, bool post, bool trans)
            {
                string h0 = Api.CopyHash(m), r0 = WatchCopyReflected(m);
                first.Patch(m, prefix: pre ? Own(nameof(WatchCopyNoop_Prefix)) : null, postfix: post ? Own(nameof(WatchCopyNoop_Postfix)) : null,
                    transpiler: trans ? Own(nameof(WatchCopyNop_Transpiler)) : null);
                string h1 = Api.CopyHash(m), r1 = WatchCopyReflected(m);
                second.Patch(m, prefix: pre ? Own(nameof(WatchCopyNoop2_Prefix)) : null, postfix: post ? Own(nameof(WatchCopyNoop2_Postfix)) : null,
                    transpiler: trans ? Own(nameof(WatchCopyNop2_Transpiler)) : null);
                string h2 = Api.CopyHash(m), r2 = WatchCopyReflected(m);
                HarmonyLib.Patches info = Harmony.GetPatchInfo(m);
                int want = (pre ? 2 : 0) * 100 + (post ? 2 : 0) * 10 + (trans ? 2 : 0);
                int got = info == null ? -1 : info.Prefixes.Count * 100 + info.Postfixes.Count * 10 + info.Transpilers.Count;
                int owners = info?.Owners.Count ?? 0;
                Api.WatchCopy(m, h0, N + which);
                // (the patches are not inert: with the runtime's reader in use each one replaced the body, so what
                // reflection alone reads changed with each)
                bool replaced = !CopyWatch.HasHostReader || (r0 != null && r1 != null && r2 != null && r1 != r0 && r2 != r1 && r2 != r0);
                twice[which] = (h0 != null && h0 == h1 && h0 == h2 && h0 == r0 && got == want && owners == 2 && replaced,
                    ", hash before " + h0 + ", after one owner " + h1 + ", after two " + h2 + ", reflection alone " + r0 + " / " + r1 + " / " + r2
                    + ", owners " + owners + ", prefixes/postfixes/transpilers " + (got < 0 ? "-" : got.ToString("000")));
            }
            Twice("prefix", fxPrefix, true, false, false);
            Twice("postfix", fxPostfix, false, true, false);
            Twice("transpiler", fxTranspiler, false, false, true);
            Twice("mixed", fxMixed, true, true, true);

            string wrongPatchedReflectedBefore = WatchCopyReflected(fxWrongPatched);
            first.Patch(fxWrongPatched, prefix: Own(nameof(WatchCopyNoop_Prefix)), transpiler: Own(nameof(WatchCopyNop_Transpiler)));
            string wrongPatchedReflectedAfter = WatchCopyReflected(fxWrongPatched);
            bool wrongPatchedReplaced = !CopyWatch.HasHostReader
                || (wrongPatchedReflectedBefore != null && wrongPatchedReflectedAfter != null && wrongPatchedReflectedAfter != wrongPatchedReflectedBefore);
            int wrongPatchedPatches = (Harmony.GetPatchInfo(fxWrongPatched)?.Prefixes.Count ?? 0) + (Harmony.GetPatchInfo(fxWrongPatched)?.Transpilers.Count ?? 0);
            Api.WatchCopy(fxWrongPatched, "0000000000000000", N + "wrongpatched");
            Api.WatchCopy(fxWrongPatched, "0000000000000000", N + "wrongpatched");

            string fallbackHost = Api.CopyHash(fxFallback);
            int hostReads = CopyWatch.HostReads, reflectionReads = CopyWatch.ReflectionReads;
            string fallbackReflected;
            CopyWatch.ReflectionOnly = true;
            try
            {
                fallbackReflected = Api.CopyHash(fxFallback);
                Api.WatchCopy(fxFallback, fallbackHost, N + "fallback");
            }
            finally
            {
                CopyWatch.ReflectionOnly = false;
            }
            int hostReadsAfter = CopyWatch.HostReads - hostReads, reflectionReadsAfter = CopyWatch.ReflectionReads - reflectionReads;
            bool fallbackUnpatched = Harmony.GetPatchInfo(fxFallback) == null;

            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (LogMessage m in Log.Messages)
            {
                string t = m.text;
                if (t == null || !t.StartsWith("[IsoRim] " + N, System.StringComparison.Ordinal) || !t.EndsWith("it may need an update.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                string which = t.Substring(("[IsoRim] " + N).Length).Split(':')[0];
                counts[which] = (counts.TryGetValue(which, out int c) ? c : 0) + m.repeats;   // repeated lines are folded into one entry
            }
            void Check(string which, int want, string extra = "", bool ok = true)
            {
                int got = counts.TryGetValue(which, out int c) ? c : 0;
                bool good = got == want && ok;
                if (good)
                {
                    pass++;
                }
                else
                {
                    fail++;
                }
                Log.Message(L + which + ": warnings " + got + ", expected " + want + extra + " -> " + (good ? "PASS" : "FAIL"));
            }
            Check("right", 0, ", hash " + rightHash, rightHash != null);
            Check("wrong", 1, " (registered twice)");
            Check("null", 1);
            Check("missing", 1, ", lookup " + (missing == null ? "null" : "found"), missing == null);
            Check("patched", 0, ", postfixes " + postfixes + ", hash before patch " + patchedBefore + " after " + patchedAfter
                + ", reflection alone " + patchedReflectedBefore + " / " + patchedReflectedAfter,
                postfixes > 0 && patchedBefore != null && patchedBefore == patchedAfter && patchedReplaced);
            foreach (string which in new[] { "prefix", "postfix", "transpiler", "mixed" })
            {
                Check(which, 0, twice[which].detail, twice[which].ok);
            }
            Check("wrongpatched", 1, " (registered twice), patches on it " + wrongPatchedPatches + ", reflection alone " + wrongPatchedReflectedBefore + " / " + wrongPatchedReflectedAfter,
                wrongPatchedPatches == 2 && wrongPatchedReplaced);
            Check("fallback", 0, ", hash through the runtime's reader " + fallbackHost + ", through reflection only " + fallbackReflected
                + ", reads meanwhile: runtime " + hostReadsAfter + ", reflection " + reflectionReadsAfter + ", patched " + (fallbackUnpatched ? "no" : "yes"),
                fallbackHost != null && fallbackHost == fallbackReflected && hostReadsAfter == 0 && reflectionReadsAfter == 2 && fallbackUnpatched);
            Check("host", 0, ", the runtime's reader " + (CopyWatch.HasHostReader ? "in use, decided in " + CopyWatch.HostReaderMs.ToString("0.00") + " ms" : "NOT in use (reflection: the hash follows the patches)"),
                CopyWatch.HasHostReader);
            Log.Message(L + "total " + pass + " PASS, " + fail + " FAIL");
            return true;
        }
    }
}
