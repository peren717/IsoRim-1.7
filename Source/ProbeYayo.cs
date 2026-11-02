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
    //   swaycheck=1   (animbody, 2026-11-01) logs, per 3D animal read since the last swaycheck (BodyAnims.AnimalSway), its
    //                 kind, facing, job, whether its model opts in (ModelDef.bodyAnims: put on, or only read), the shift read
    //                 (x across, z up the picture, cells: min / max), the sprite turn and the lean made of it (pitch or roll),
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

        private class SwaySeen
        {
            public string name, job, axis;
            public Rot4 rot;
            public bool applied;
            public float xMin = float.MaxValue, xMax = float.MinValue, zMin = float.MaxValue, zMax = float.MinValue;
            public float turnMin = float.MaxValue, turnMax = float.MinValue, leanMin = float.MaxValue, leanMax = float.MinValue;
            public int n;
        }

        private static readonly Dictionary<int, SwaySeen> sways = new Dictionary<int, SwaySeen>();

        // Called by BodyAnims.AnimalSway while the probe runs: one 3D animal's read (applied: its model opts in).
        public static void SwaySample(Pawn pawn, Vector3 shift, float turn, float lean, bool pitch, bool applied)
        {
            lock (sways)
            {
                SwaySeen s;
                if (!sways.TryGetValue(pawn.thingIDNumber, out s))
                {
                    s = new SwaySeen { name = pawn.def.defName + " " + pawn.thingIDNumber };
                    sways[pawn.thingIDNumber] = s;
                }
                s.rot = pawn.Rotation;
                s.job = pawn.CurJobDef != null ? pawn.CurJobDef.defName : "-";
                s.applied = applied;
                s.axis = pitch ? "pitch" : "roll";
                s.xMin = Mathf.Min(s.xMin, shift.x);
                s.xMax = Mathf.Max(s.xMax, shift.x);
                s.zMin = Mathf.Min(s.zMin, shift.z);
                s.zMax = Mathf.Max(s.zMax, shift.z);
                s.turnMin = Mathf.Min(s.turnMin, turn);
                s.turnMax = Mathf.Max(s.turnMax, turn);
                s.leanMin = Mathf.Min(s.leanMin, lean);
                s.leanMax = Mathf.Max(s.leanMax, lean);
                s.n++;
            }
        }

        private static bool YayoKey(string key, string[] a, Map map)
        {
            if (key == "swaycheck")
            {
                lock (sways)
                {
                    foreach (KeyValuePair<int, SwaySeen> kv in sways.OrderBy(k => k.Value.name))
                    {
                        SwaySeen s = kv.Value;
                        Log.Message("[IsoRim probe] swaycheck " + s.name + " facing " + s.rot.ToStringHuman() + " job " + s.job + (s.applied ? " APPLIED" : " read only")
                            + ": shift x " + s.xMin.ToString("0.000") + " to " + s.xMax.ToString("0.000") + ", z " + s.zMin.ToString("0.000") + " to " + s.zMax.ToString("0.000")
                            + ", turn " + s.turnMin.ToString("0.0") + " to " + s.turnMax.ToString("0.0") + ", " + s.axis + " " + s.leanMin.ToString("0.0") + " to " + s.leanMax.ToString("0.0")
                            + " (" + s.n + " draws)");
                    }
                    Log.Message("[IsoRim probe] swaycheck: " + sways.Count + " animals read");
                    sways.Clear();
                }
                return true;
            }
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
