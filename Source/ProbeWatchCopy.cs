using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace IsoRim
{
    // Harness-only check for Api 70 (Api.WatchCopy, Api.CopyHash; CopyWatch.cs):
    //   watchcopycheck=label   in game (no loading step: every registration is checked at once), with notes unique to the label:
    //     right      GenText.ToStringPercent with its hash now                                  -> no warning
    //     wrong      GenGrid.InBounds (Vector3, Map) with a wrong hash, registered twice         -> exactly one warning
    //     null       a null method                                                              -> exactly one warning
    //     missing    AccessTools.Method of a method that does not exist (null)                   -> exactly one warning
    //     patched    GenSight.LineOfSight (IntVec3, IntVec3, Map) Harmony-patched here (an empty postfix) after its hash
    //                was taken, registered with that hash                                       -> no warning, the same hash
    //   Warnings are counted in the game's log (Log.Messages) by note. PASS/FAIL lines, then "total n PASS, m FAIL".
    //   The hashes now are logged with those of the IsoRim 1.7 build of 2026-11-10 (game 1.7.4976) for comparison (info only:
    //   a game update may change them).
    public static partial class Probe
    {
        private static Harmony watchCopyHarmony;

        public static void WatchCopyNoop_Postfix()
        {
        }

        private static bool WatchCopyKey(string key, string[] a, Map map)
        {
            if (key != "watchcopycheck")
            {
                return false;
            }
            string label = a[0];
            string L = "[IsoRim probe] watchcopycheck " + label + ": ";
            string N = "IsoRim probe watchcopy " + label + " ";
            int pass = 0, fail = 0;

            MethodBase right = AccessTools.Method(typeof(GenText), nameof(GenText.ToStringPercent), new[] { typeof(float) });
            MethodBase wrong = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.InBounds), new[] { typeof(UnityEngine.Vector3), typeof(Map) });
            MethodBase patched = AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map) });
            MethodBase missing = AccessTools.Method(typeof(GenSight), "NoSuchMethodForWatchCopy");

            string rightHash = Api.CopyHash(right);
            string patchedBefore = Api.CopyHash(patched);
            if (watchCopyHarmony == null)
            {
                watchCopyHarmony = new Harmony("peren.isorim.probe.watchcopy");
                watchCopyHarmony.Patch(patched, postfix: new HarmonyMethod(typeof(Probe), nameof(WatchCopyNoop_Postfix)));
            }
            string patchedAfter = Api.CopyHash(patched);
            int postfixes = Harmony.GetPatchInfo(patched)?.Postfixes.Count ?? 0;
            Log.Message(L + "Api.Version " + Api.Version + ", hashes: ToStringPercent " + rightHash + " (2026-11-10: 5407a64463aa6ffa), InBounds "
                + Api.CopyHash(wrong) + " (c97033b38f89c7a0), LineOfSight " + patchedBefore + " (f3b1c560f0d168f4), CopyHash(null) " + (Api.CopyHash(null) ?? "null"));

            Api.WatchCopy(right, rightHash, N + "right");
            Api.WatchCopy(wrong, "0000000000000000", N + "wrong");
            Api.WatchCopy(wrong, "0000000000000000", N + "wrong");
            Api.WatchCopy(null, "0123456789abcdef", N + "null");
            Api.WatchCopy(missing, "0123456789abcdef", N + "missing");
            Api.WatchCopy(patched, patchedBefore, N + "patched");

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
            Check("patched", 0, ", postfixes " + postfixes + ", hash before patch " + patchedBefore + " after " + patchedAfter,
                postfixes > 0 && patchedBefore != null && patchedBefore == patchedAfter);
            Log.Message(L + "total " + pass + " PASS, " + fail + " FAIL");
            return true;
        }
    }
}
