using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness keys for other mods' body animations on 3D figures (BodyAnims; yayolean, 2026-10-23):
    //   leancheck=1   logs, per pawn drawn with another mod's body turn since the last leancheck, its facing, job, the
    //                 sprite turn seen (min / max, degrees clockwise) and the figure's forward lean made of it (min / max),
    //                 then starts over
    public static partial class Probe
    {
        private class LeanSeen
        {
            public string name, job;
            public Rot4 rot;
            public float turnMin = float.MaxValue, turnMax = float.MinValue, fwdMin = float.MaxValue, fwdMax = float.MinValue;
            public int n;
        }

        private static readonly Dictionary<int, LeanSeen> leans = new Dictionary<int, LeanSeen>();

        // Called by BodyAnims.Apply while the probe runs: one figure drawn with a body turn.
        public static void LeanSample(Pawn pawn, float turn, float forward)
        {
            lock (leans)
            {
                LeanSeen s;
                if (!leans.TryGetValue(pawn.thingIDNumber, out s))
                {
                    s = new LeanSeen { name = pawn.LabelShort };
                    leans[pawn.thingIDNumber] = s;
                }
                s.rot = pawn.Rotation;
                s.job = pawn.CurJobDef != null ? pawn.CurJobDef.defName : "-";
                s.turnMin = Mathf.Min(s.turnMin, turn);
                s.turnMax = Mathf.Max(s.turnMax, turn);
                s.fwdMin = Mathf.Min(s.fwdMin, forward);
                s.fwdMax = Mathf.Max(s.fwdMax, forward);
                s.n++;
            }
        }

        private static bool YayoKey(string key, string[] a, Map map)
        {
            if (key != "leancheck")
            {
                return false;
            }
            lock (leans)
            {
                foreach (KeyValuePair<int, LeanSeen> kv in leans.OrderBy(k => k.Value.name))
                {
                    LeanSeen s = kv.Value;
                    Log.Message("[IsoRim probe] leancheck " + s.name + " facing " + s.rot.ToStringHuman() + " job " + s.job + ": turn " + s.turnMin.ToString("0.0") + " to "
                        + s.turnMax.ToString("0.0") + ", forward lean " + s.fwdMin.ToString("0.0") + " to " + s.fwdMax.ToString("0.0") + " (" + s.n + " draws)");
                }
                Log.Message("[IsoRim probe] leancheck: " + leans.Count + " figures with a body turn");
                leans.Clear();
            }
            return true;
        }
    }
}
