using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld.Planet;
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
    //   PASS/FAIL lines, then "total n PASS, m FAIL".
    public static partial class Probe
    {
        private const int SeedParityGravshipId = 4242;   // any ID: the check compares bodies on the same input

        private static readonly int[] recordedInputs = new int[3];
        private static int recordedCount;
        private static bool seedParityPatched;

        private static bool SeedKey(string key, string[] a, Map map)
        {
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

        // The original IL against the same IL with QuickTestSeed's swap applied.
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
            int differing = 0;
            bool shapesOk = true;
            for (int i = 0; i < original.Count; i++)
            {
                if (original[i].opcode == swapped[i].opcode && Equals(original[i].operand, swapped[i].operand))
                {
                    continue;
                }
                differing++;
                MethodInfo was = original[i].operand as MethodInfo, now = swapped[i].operand as MethodInfo;
                shapesOk &= original[i].opcode == OpCodes.Call && swapped[i].opcode == OpCodes.Call && was != null && now != null
                    && was.DeclaringType == typeof(HashCode) && was.Name == nameof(HashCode.Combine)
                    && now.DeclaringType == typeof(QuickTestSeed) && now.Name == nameof(QuickTestSeed.Combine)
                    && was.GetGenericArguments().SequenceEqual(now.GetGenericArguments()) && was.ReturnType == now.ReturnType;
            }
            detail = "instructions " + original.Count + ", swapped " + n + " (expected " + t.calls + "), differing " + differing
                + (shapesOk ? ", each a HashCode.Combine with the same type arguments" : ", NOT only HashCode.Combine calls");
            return n == t.calls && differing == n && shapesOk;
        }

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
