using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Hovering animals and mechs (Api 32; ModelDef.hoverHeight, hoverBob).
    //
    // While the pawn is up and about (standing, walking, working; not flying, carried or attached) its whole figure floats
    // hoverHeight cells over the ground, times its size, drifting up and down by hoverBob cells (a slow swell, its own phase
    // per pawn). Downed, dead, asleep or resting it settles onto the ground; getting up it rises again. Both are eased over
    // a fraction of a second by game ticks, so they stop with the game paused. The ground shadow stays on the ground under
    // it, a little smaller the higher it floats (Mechs, Animals). Pawns without the fields pay nothing.
    public static class Hover
    {
        private class State
        {
            public float up;     // 0 on the ground .. 1 fully afloat
            public int tick;     // the game tick it was last moved on
        }

        private static readonly Dictionary<int, State> states = new Dictionary<int, State>();

        private const float RiseTicks = 45f;        // ticks to rise or settle all the way
        private const float BobTicks = 160f;        // one swell, in ticks (about 2.7 s at normal speed)
        private const int ForgetAfter = 2500;       // states not moved on for this long are dropped when the table grows
        private const int MaxStates = 4000;

        // True if the def hovers at all.
        public static bool Hovers(ModelDef def)
        {
            return def != null && (def.hoverHeight > 0f || def.hoverBob > 0f);
        }

        // How far the pawn's figure is raised now, in cells (its size already applied). afloat: whether it is up and about
        // now (the target); advance: from its own draw this frame (moves the easing on; anything else only reads it).
        public static float Lift(Pawn pawn, ModelDef def, float size, bool afloat, bool advance)
        {
            if (pawn == null || !Hovers(def))
            {
                return 0f;
            }
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            State s;
            if (!states.TryGetValue(pawn.thingIDNumber, out s))
            {
                if (!advance)
                {
                    return 0f;
                }
                if (states.Count >= MaxStates)
                {
                    Forget(now);
                }
                s = new State { up = afloat && !pawn.Dead ? 1f : 0f, tick = now };
                states[pawn.thingIDNumber] = s;
            }
            if (advance)
            {
                int dt = now - s.tick;
                s.tick = now;
                float target = afloat && !pawn.Dead ? 1f : 0f;
                if (dt < 0 || dt > 600)
                {
                    s.up = target;   // (a reload, or not drawn for a long while)
                }
                else if (dt > 0)
                {
                    s.up = Mathf.MoveTowards(s.up, target, dt / RiseTicks);
                }
            }
            return HeightAt(pawn, def, size, s.up, now);
        }

        // The pawn's current hover height (cells) as last drawn; 0 if it does not hover or has not been drawn.
        public static float Current(Pawn pawn)
        {
            ModelEntry entry;
            State s;
            if (pawn == null || pawn.def == null || !states.TryGetValue(pawn.thingIDNumber, out s) || !ModelLibrary.TryGet(pawn.def, out entry)
                || !Hovers(entry.def))
            {
                return 0f;
            }
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            return HeightAt(pawn, entry.def, entry.def.scale * Animals.SizeOf(pawn), s.up, now);
        }

        // The ground shadow's scale under a figure raised this far: a little smaller the higher it is.
        public static float ShadowScale(float lift)
        {
            return lift > 0f ? Mathf.Lerp(1f, 0.7f, Mathf.Clamp01(lift / 1.2f)) : 1f;
        }

        private static float HeightAt(Pawn pawn, ModelDef def, float size, float up, int now)
        {
            if (up <= 0f)
            {
                return 0f;
            }
            float eased = up * up * (3f - 2f * up);
            float phase = (now + (pawn.thingIDNumber & 0xFFFF) * 37) * (2f * Mathf.PI / BobTicks);
            float h = def.hoverHeight + def.hoverBob * Mathf.Sin(phase);
            return Mathf.Max(0f, h) * size * eased;
        }

        private static void Forget(int now)
        {
            List<int> old = new List<int>();
            foreach (KeyValuePair<int, State> kv in states)
            {
                if (now - kv.Value.tick > ForgetAfter || now < kv.Value.tick)
                {
                    old.Add(kv.Key);
                }
            }
            for (int i = 0; i < old.Count; i++)
            {
                states.Remove(old[i]);
            }
            if (states.Count >= MaxStates)
            {
                states.Clear();
            }
        }

        // Tests (Api.Dev): the easing state of a pawn (-1 none).
        internal static float UpOf(Pawn pawn)
        {
            State s;
            return pawn != null && states.TryGetValue(pawn.thingIDNumber, out s) ? s.up : -1f;
        }
    }
}
