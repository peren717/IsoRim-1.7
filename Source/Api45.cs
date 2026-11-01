using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 45: posture rules. An addon tells IsoRim, pawn by pawn, that a pawn crouches (how much, 0..1) or lies face down;
    // IsoRim shows it on the figure it already draws with transforms only (Poses.Crouched: one fixed crouch, squashed and
    // leaning forward; face down as a crawl), so other mods' bodies, apparel and races keep working. Generic: no mod
    // names.
    public static partial class Api
    {
        /// <summary>(Api version 45) What a posture rule returns for a pawn lying face down.</summary>
        public const float PostureProne = -1f;

        /// <summary>
        /// (Api version 45) Adds a posture rule: IsoRim asks it for a pawn about once a frame while it draws or measures the
        /// pawn (main thread). Return 0 to pass (standing), a crouch amount over 0 up to 1 (1: IsoRim's full crouch, one
        /// fixed pose wherever the pawn is; 0.5 half of it), or <see cref="PostureProne"/> (any
        /// negative value) to have it lie face down. Rules are asked in the order they were added: lying face down from any
        /// rule wins, else the largest crouch. Not applied to a pawn that is seated, attached to a host (Api 13: a rider), carried,
        /// in mid-jump, downed, dead, in a bed or a statue. IsoRim eases between postures (about 0.2 s). Keep the rule cheap: no
        /// allocation, no searches. A rule that throws is logged once and passes from then on. Only a standard
        /// <c>System.Func&lt;Pawn, float&gt;</c>, so code calling it after an <c>Api.Version &gt;= 45</c> check never breaks on
        /// an older IsoRim.
        /// </summary>
        public static void AddPostureRule(Func<Pawn, float> rule)
        {
            PostureRules.Add(rule);
        }

        /// <summary>
        /// (Api version 45) Adds a facing rule for pawns lying face down by a posture rule: return the map cell the pawn's head
        /// points to (where it hides from), or <c>IntVec3.Invalid</c> to pass (it keeps the facing it had). Asked like the
        /// posture rules, in order; the first valid cell wins.
        /// </summary>
        public static void AddProneFacingRule(Func<Pawn, IntVec3> rule)
        {
            PostureRules.AddFacing(rule);
        }

        /// <summary>
        /// (Api version 45) What the posture rules give the pawn now: crouch 0..1, prone (face down). False (0, false) when no
        /// rule gives it a posture. As the rules answer it, before IsoRim's own exceptions and easing; see
        /// <see cref="DrawnPostureOf"/> for what is drawn.
        /// </summary>
        public static bool PostureOf(Pawn pawn, out float crouch, out bool prone)
        {
            crouch = 0f;
            prone = false;
            if (pawn == null || !PostureRules.Any)
            {
                return false;
            }
            float v = PostureRules.Ask(pawn);
            prone = v < 0f;
            crouch = prone ? 0f : Mathf.Clamp01(v);
            return prone || crouch > 0f;
        }

        /// <summary>
        /// (Api version 45) The posture IsoRim draws the pawn in now (3D view on, main thread): crouch 0..1 and prone 0..1
        /// (face down), both eased; drop: how much lower its shoulders (and the gun on them) are than standing, cells. False when
        /// it is drawn standing (no rule, or one of the exceptions in <see cref="AddPostureRule"/>).
        /// </summary>
        public static bool DrawnPostureOf(Pawn pawn, out float crouch, out float prone, out float drop)
        {
            Poses.Crouch c;
            bool on = Poses.Crouched(pawn, out c);
            crouch = on ? c.amount : 0f;
            prone = on ? c.prone : 0f;
            drop = on ? c.drop * c.size : 0f;
            return on;
        }

        public static partial class Dev
        {
            // (Api 45) For tests: the posture state of a pawn as drawn (rules, easing, the numbers used).
            public static string PostureReport(Pawn pawn) => Postures.Report(pawn);

            // (Api 45) For tests: postures drawn at all (off: every pawn stands, as without rules), and the time spent working
            // them out (per pawn and frame) since the last reset, while timing is on.
            public static bool PosturesOn
            {
                get { return !Postures.Off; }
                set { Postures.Off = !value; }
            }

            public static bool PostureTiming
            {
                get { return Postures.Timing; }
                set { Postures.Timing = value; }
            }

            public static void PostureCost(out int works, out double ms)
            {
                works = Postures.Works;
                ms = Postures.WorkTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }

            public static void ResetPostureCost()
            {
                Postures.Works = 0;
                Postures.WorkTicks = 0;
            }
        }
    }

    // The registered posture and facing rules (Api 45).
    internal static class PostureRules
    {
        private sealed class Rule<T>
        {
            public T ask;
            public string owner;
            public bool failed;   // threw once: logged, passes from then on
        }

        private static readonly List<Rule<Func<Pawn, float>>> rules = new List<Rule<Func<Pawn, float>>>();
        private static readonly List<Rule<Func<Pawn, IntVec3>>> facings = new List<Rule<Func<Pawn, IntVec3>>>();

        // Nothing registered: no pawn is ever posed (one test, no cost).
        public static bool Any;

        public static void Add(Func<Pawn, float> rule)
        {
            if (rule == null)
            {
                Log.Warning("[IsoRim] Api.AddPostureRule: null rule ignored.");
                return;
            }
            foreach (Rule<Func<Pawn, float>> r in rules)
            {
                if (r.ask == rule)
                {
                    return;
                }
            }
            rules.Add(new Rule<Func<Pawn, float>> { ask = rule, owner = ModelRules.OwnerOf(rule) });
            Any = true;
        }

        public static void AddFacing(Func<Pawn, IntVec3> rule)
        {
            if (rule == null)
            {
                Log.Warning("[IsoRim] Api.AddProneFacingRule: null rule ignored.");
                return;
            }
            foreach (Rule<Func<Pawn, IntVec3>> r in facings)
            {
                if (r.ask == rule)
                {
                    return;
                }
            }
            facings.Add(new Rule<Func<Pawn, IntVec3>> { ask = rule, owner = ModelRules.OwnerOf(rule) });
        }

        // The rules' answer: negative lying face down, else the crouch 0..1.
        public static float Ask(Pawn pawn)
        {
            float crouch = 0f;
            for (int i = 0; i < rules.Count; i++)
            {
                Rule<Func<Pawn, float>> r = rules[i];
                if (r.failed)
                {
                    continue;
                }
                float v;
                try
                {
                    v = r.ask(pawn);
                }
                catch (Exception e)
                {
                    r.failed = true;
                    Log.Error("[IsoRim] A posture rule from " + r.owner + " threw (logged once; it passes from now on): " + e);
                    continue;
                }
                if (v < 0f)
                {
                    return -1f;
                }
                if (v > crouch)
                {
                    crouch = Mathf.Min(1f, v);
                }
            }
            return crouch;
        }

        // Where a prone pawn's head points, or Invalid.
        public static IntVec3 Facing(Pawn pawn)
        {
            for (int i = 0; i < facings.Count; i++)
            {
                Rule<Func<Pawn, IntVec3>> r = facings[i];
                if (r.failed)
                {
                    continue;
                }
                try
                {
                    IntVec3 c = r.ask(pawn);
                    if (c.IsValid)
                    {
                        return c;
                    }
                }
                catch (Exception e)
                {
                    r.failed = true;
                    Log.Error("[IsoRim] A prone facing rule from " + r.owner + " threw (logged once; it passes from now on): " + e);
                }
            }
            return IntVec3.Invalid;
        }
    }

    // Per pawn: the posture as drawn (Api 45), worked out once a frame and eased.
    internal static class Postures
    {
        // The look of a crouch (figure units, a figure of size 1; Poses.Crouch): see Poses.cs.
        internal const float Drop = 0.20f;        // how far a full crouch brings the shoulders (and the gun on them) down: one fixed pose
        internal const float Lean = 15f;          // degrees forward at a full crouch
        private const float CrouchEase = 0.2f;    // seconds from standing to a full crouch and back
        private const float DownEase = 0.2f;      // seconds to go down face first
        private const float UpEase = 0.35f;       // and to get up again

        private sealed class State
        {
            public int frame = -1;
            public bool on;
            public Poses.Crouch pose;
            public float crouch, prone;           // as shown, eased
            public float time = -1f;              // realtime of the last easing step
            public float yaw;                       // facing while prone (kept when no facing rule answers)
            public bool hasYaw;
            public float target;                    // the rules' last answer
        }

        private static readonly ConditionalWeakTable<Pawn, State> states = new ConditionalWeakTable<Pawn, State>();

        internal static bool Off;          // harness (Api.Dev.PosturesOn)
        internal static bool Timing;       // harness (Api.Dev.PostureTiming)
        internal static int Works;
        internal static long WorkTicks;

        // The posture as drawn now (Poses.Crouched).
        public static bool Of(Pawn pawn, out Poses.Crouch pose)
        {
            pose = default(Poses.Crouch);
            if (!PostureRules.Any || pawn == null || Off)
            {
                return false;
            }
            State s = states.GetValue(pawn, _ => new State());
            int frame = Time.frameCount;
            if (s.frame == frame || !UnityData.IsInMainThread)
            {
                pose = s.pose;
                return s.on;
            }
            s.frame = frame;
            long t0 = Timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            s.on = Work(pawn, s);
            if (Timing)
            {
                Works++;
                WorkTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            }
            pose = s.pose;
            return s.on;
        }

        private static bool Work(Pawn pawn, State s)
        {
            float now = Time.realtimeSinceStartup;
            float dt = s.time < 0f ? 1e6f : now - s.time;
            s.time = now;
            if (!Poses.On || !View3D.Active || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Map == null
                || Attachments.Any && Attachments.HostOf(pawn) != null || pawn.CurrentBed() != null || Poses.SeatUnder(pawn) != null)
            {
                s.crouch = s.prone = 0f;   // (none of these eases: they have their own poses)
                s.pose = default(Poses.Crouch);
                return false;
            }
            float target = PostureRules.Ask(pawn);
            s.target = target;
            bool proneTarget = target < 0f;
            bool standingPosture = pawn.GetPosture() == PawnPosture.Standing;
            float crouchTarget = proneTarget ? 1f : standingPosture ? target : 0f;
            if (dt > 0.5f)
            {
                s.crouch = crouchTarget;   // not drawn for a while: as it is now
                s.prone = proneTarget ? 1f : 0f;
            }
            else
            {
                s.crouch = Mathf.MoveTowards(s.crouch, crouchTarget, dt / CrouchEase);
                s.prone = Mathf.MoveTowards(s.prone, proneTarget ? 1f : 0f, dt / (proneTarget ? DownEase : UpEase));
            }
            if (proneTarget)
            {
                IntVec3 to = PostureRules.Facing(pawn);
                if (to.IsValid)
                {
                    Vector3 d = to.ToVector3Shifted() - pawn.DrawPos;
                    d.y = 0f;
                    if (d.sqrMagnitude > 0.01f)
                    {
                        s.yaw = d.AngleFlat();
                        s.hasYaw = true;
                    }
                }
            }
            else if (s.prone <= 0f)
            {
                s.hasYaw = false;
            }
            // Lying for any other reason (asleep on the ground, a posture no rule gave): as the game has it.
            if (!standingPosture && !proneTarget && s.prone <= 0f || s.crouch <= 0f && s.prone <= 0f && !proneTarget)
            {
                s.pose = default(Poses.Crouch);
                return false;
            }
            float size = AlienFigures.SizeOf(pawn);
            Poses.Crouch c = default(Poses.Crouch);
            c.amount = s.crouch;
            c.prone = s.prone;
            c.proneTarget = proneTarget;
            c.size = size;
            c.drop = s.crouch * Drop;
            c.squash = 1f - c.drop / Poses.ShoulderY;
            c.lean = s.crouch * Lean;
            c.hasYaw = s.hasYaw;
            c.yaw = s.yaw;
            s.pose = c;
            return true;
        }

        public static string Report(Pawn pawn)
        {
            if (pawn == null)
            {
                return "no pawn";
            }
            Poses.Crouch c;
            bool on = Of(pawn, out c);
            float crouch;
            bool prone;
            Api.PostureOf(pawn, out crouch, out prone);
            return "rules " + (prone ? "prone" : crouch.ToString("0.00")) + ", drawn " + (on ? "crouch " + c.amount.ToString("0.00") + " prone " + c.prone.ToString("0.00")
                + " drop " + c.drop.ToString("0.000") + " squash " + c.squash.ToString("0.000") + " lean " + c.lean.ToString("0.0")
                + (c.hasYaw ? " yaw " + c.yaw.ToString("0") : "") + " headRise " + c.HeadRise(Patches.DrawnAsModel(pawn)).ToString("0.000") : "standing")
                + ", job " + (pawn.CurJobDef != null ? pawn.CurJobDef.defName : "none") + ", posture " + pawn.GetPosture() + ", moving " + (pawn.pather != null && pawn.pather.MovingNow);
        }
    }
}
