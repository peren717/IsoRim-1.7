using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness keys for other mods' body animations on 3D figures (BodyAnims; yayolean, 2026-10-23):
    //   leancheck=1   logs, per pawn drawn with another mod's body turn since the last leancheck, the facing its sprite is drawn
    //                 with, its job, the sprite turn seen (min / max, degrees clockwise) and how far the figure's feet-to-head
    //                 line turned on screen for it (yayoturn 2026-11-09: min / max, degrees clockwise, and the worst difference
    //                 from the sprite's turn; at the largest turn and the last drawn: both), then starts over
    //   swaycheck=1   (animbody, 2026-11-01) logs, per 3D animal read since the last swaycheck (BodyAnims.AnimalSway), its
    //                 kind, facing, job, whether its model opts in (ModelDef.bodyAnims: put on, or only read), the shift read
    //                 (x across, z up the picture, cells: min / max) and the sprite turn (yayoturn: put on as it is, SpriteMotion),
    //                 then starts over
    //   joy=x,z,<JobDef>,ax,az,bx,bz  (yayoturn) the pawn in (x,z) given a watch-building joy job (Play_Horseshoes): the building
    //                 in (ax,az), standing on (bx,bz) facing it; its joy emptied first so it plays on (a real job)
    //   yayoseek=x,z,min,max[,size]  (yayoturn) the game kept paused and run one tick every other frame (each drawn, so the
    //                 other mod works out its animation) until the pawn in (x,z) is drawn with a sprite turn of at least min
    //                 degrees either way, at most max ticks, the camera on it (size: as the game's zoom, 2.2 if not given); logs
    //                 the turn reached, then the turn it is drawn with (and its figure's on-screen turn) while the clock stays
    //                 (the pictures are taken then). Each walkfilm frame's log line also gets every standing person's sprite
    //                 turn (and the figure's on-screen turn, when drawn as a figure)
    public static partial class Probe
    {
        private class LeanSeen
        {
            public string name, job;
            public Rot4 rot;
            public float turnMin = float.MaxValue, turnMax = float.MinValue, screenMin = float.MaxValue, screenMax = float.MinValue;
            public float worst, peakTurn, peakScreen, lastTurn, lastScreen;
            public int n, lastFrame = -1;
        }

        private static readonly Dictionary<int, LeanSeen> leans = new Dictionary<int, LeanSeen>();

        // Called by BodyAnims.Apply while the probe runs: one figure drawn with a body turn; at is where it stands (true space),
        // before and after its pose without and with the animation (figure space, before its turn by yaw and its size).
        public static void LeanSample(Pawn pawn, float turn, Vector3 at, float yaw, float size, Matrix4x4 before, Matrix4x4 after)
        {
            float screen = 0f;
            if (View3D.Ready)
            {
                Quaternion r = Quaternion.AngleAxis(yaw, Vector3.up);
                screen = Mathf.DeltaAngle(ScreenAngle(at, r, after, size), ScreenAngle(at, r, before, size));   // clockwise: the angle falls
            }
            lock (leans)
            {
                LeanSeen s;
                if (!leans.TryGetValue(pawn.thingIDNumber, out s))
                {
                    s = new LeanSeen { name = pawn.LabelShort };
                    leans[pawn.thingIDNumber] = s;
                }
                s.rot = BodyAnims.Facing(pawn);
                s.job = pawn.CurJobDef != null ? pawn.CurJobDef.defName : "-";
                s.turnMin = Mathf.Min(s.turnMin, turn);
                s.turnMax = Mathf.Max(s.turnMax, turn);
                s.screenMin = Mathf.Min(s.screenMin, screen);
                s.screenMax = Mathf.Max(s.screenMax, screen);
                s.worst = Mathf.Max(s.worst, Mathf.Abs(screen - turn));
                if (Mathf.Abs(turn) >= Mathf.Abs(s.peakTurn))
                {
                    s.peakTurn = turn;
                    s.peakScreen = screen;
                }
                s.lastTurn = turn;
                s.lastScreen = screen;
                s.lastFrame = Time.frameCount;
                s.n++;
            }
        }

        // The on-screen angle (degrees counter-clockwise from the screen's right) of the line from the figure's feet to its head.
        private static float ScreenAngle(Vector3 at, Quaternion r, Matrix4x4 pose, float size)
        {
            Vector2 feet = View3D.WorldToScreen(at + r * pose.MultiplyPoint3x4(Vector3.zero));
            Vector2 head = View3D.WorldToScreen(at + r * pose.MultiplyPoint3x4(new Vector3(0f, 0.95f * size, 0f)));
            Vector2 d = head - feet;
            return Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        }

        private class SwaySeen
        {
            public string name, job;
            public Rot4 rot;
            public bool applied;
            public float xMin = float.MaxValue, xMax = float.MinValue, zMin = float.MaxValue, zMax = float.MinValue;
            public float turnMin = float.MaxValue, turnMax = float.MinValue;
            public int n;
        }

        private static readonly Dictionary<int, SwaySeen> sways = new Dictionary<int, SwaySeen>();

        // Called by BodyAnims.AnimalSway while the probe runs: one 3D animal's read (applied: its model opts in).
        public static void SwaySample(Pawn pawn, Vector3 shift, float turn, bool applied)
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
                s.xMin = Mathf.Min(s.xMin, shift.x);
                s.xMax = Mathf.Max(s.xMax, shift.x);
                s.zMin = Mathf.Min(s.zMin, shift.z);
                s.zMax = Mathf.Max(s.zMax, shift.z);
                s.turnMin = Mathf.Min(s.turnMin, turn);
                s.turnMax = Mathf.Max(s.turnMax, turn);
                s.n++;
            }
        }

        private static Pawn seekPawn;
        private static float seekMin;
        private static int seekLeft, seekRun;
        private static int seekHold;
        private static float seekCam;
        // after the seek: the pawn as it stays drawn until the game clock moves on (the harness's pictures are taken then)
        private static Pawn heldPawn;
        private static int heldTick, heldFrames;
        private static float heldMin, heldMax, heldScreen;

        // Once a frame (ProbeScene): one more tick until the sought pawn's sprite turn is reached (yayoseek); then, while the clock
        // stays, its sprite turn (and figure's on-screen turn) noted every frame, logged once the clock moves on.
        private static void YayoSeekTick()
        {
            if (heldPawn != null)
            {
                if (Find.TickManager.TicksGame != heldTick || !heldPawn.Spawned)
                {
                    Log.Message("[IsoRim probe] yayoseek " + heldPawn.LabelShort + " held " + heldFrames + " frames at tick " + heldTick + ": turn " + heldMin.ToString("0.0")
                        + " to " + heldMax.ToString("0.0") + ", figure's screen turn " + heldScreen.ToString("0.0"));
                    heldPawn = null;
                }
                else
                {
                    Vector3 hs;
                    float ht;
                    if (BodyAnims.Read(heldPawn, heldPawn.DrawPos, out hs, out ht))
                    {
                        heldMin = Mathf.Min(heldMin, ht);
                        heldMax = Mathf.Max(heldMax, ht);
                        heldFrames++;
                    }
                    lock (leans)
                    {
                        LeanSeen l;
                        if (leans.TryGetValue(heldPawn.thingIDNumber, out l) && Time.frameCount - l.lastFrame <= 2)
                        {
                            heldScreen = l.lastScreen;
                        }
                    }
                }
            }
            if (seekPawn == null)
            {
                return;
            }
            // (the camera on it: the other mod works out a pawn's animation only while it is drawn, on the game's worker threads;
            // a few frames drawn there first, and one after each tick, so what is read is what that tick draws)
            if (seekPawn.Spawned)
            {
                Find.CameraDriver.SetRootPosAndSize(seekPawn.DrawPos, seekCam);
            }
            if (seekHold > 0)
            {
                seekHold--;
                return;
            }
            Vector3 shift;
            float turn = 0f;
            bool read = seekPawn.Spawned && BodyAnims.Active && BodyAnims.Read(seekPawn, seekPawn.DrawPos, out shift, out turn);
            if (!read || Mathf.Abs(turn) >= seekMin || seekLeft <= 0)
            {
                Log.Message("[IsoRim probe] yayoseek " + (seekPawn.LabelShort ?? "?") + " at " + seekPawn.DrawPos.ToString("F2") + " facing " + BodyAnims.Facing(seekPawn).ToStringHuman() + " job "
                    + (seekPawn.CurJobDef != null ? seekPawn.CurJobDef.defName : "-") + ": turn " + turn.ToString("0.0") + " after " + seekRun + " ticks (tick "
                    + Find.TickManager.TicksGame + ")" + (read && Mathf.Abs(turn) >= seekMin ? "" : " NOT REACHED"));
                heldPawn = seekPawn;
                heldTick = Find.TickManager.TicksGame;
                heldFrames = 0;
                heldMin = float.MaxValue;
                heldMax = float.MinValue;
                heldScreen = float.NaN;
                seekPawn = null;
                return;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.TickManager.DoSingleTick();
            seekLeft--;
            seekRun++;
            seekHold = 1;
        }

        // Appended to each walkfilm frame's log line: every standing person's sprite turn, and its figure's on-screen turn.
        private static string YayoFilmNote()
        {
            if (!BodyAnims.Active || Find.CurrentMap == null)
            {
                return "";
            }
            string s = "";
            foreach (Pawn p in Find.CurrentMap.mapPawns.AllPawnsSpawned)
            {
                Vector3 shift;
                float turn;
                if (p.RaceProps != null && p.RaceProps.Humanlike && p.GetPosture() == PawnPosture.Standing && BodyAnims.Read(p, p.DrawPos, out shift, out turn))
                {
                    s += "; " + p.LabelShort + " " + BodyAnims.Facing(p).ToStringHuman() + " turn " + turn.ToString("0.0");
                    lock (leans)
                    {
                        LeanSeen l;
                        if (leans.TryGetValue(p.thingIDNumber, out l) && Time.frameCount - l.lastFrame <= 3)
                        {
                            s += " screen " + l.lastScreen.ToString("0.0");
                        }
                    }
                }
            }
            return s;
        }

        private static bool YayoKey(string key, string[] a, Map map)
        {
            if (key == "joy")
            {
                Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                JobDef jd = DefDatabase<JobDef>.GetNamedSilentFail(a[2]);
                Building b = new IntVec3(I(a[3]), 0, I(a[4])).GetFirstBuilding(map);
                if (p == null || jd == null || b == null)
                {
                    Log.Warning("[IsoRim probe] joy: no pawn, job " + a[2] + " or building");
                    return true;
                }
                if (p.drafter != null)
                {
                    p.drafter.Drafted = false;
                }
                if (p.needs != null && p.needs.joy != null)
                {
                    p.needs.joy.CurLevel = 0.05f;
                }
                Job job = JobMaker.MakeJob(jd, b, new IntVec3(I(a[5]), 0, I(a[6])));
                p.jobs.StartJob(job, JobCondition.InterruptForced);
                Log.Message("[IsoRim probe] joy " + p.LabelShort + " at " + p.Position + ": " + jd.defName + " " + (p.CurJob == job));
                return true;
            }
            if (key == "yayoseek")
            {
                seekPawn = PawnAt(map, I(a[0]), I(a[1]));
                seekMin = F(a[2]);
                seekLeft = I(a[3]);
                seekCam = a.Length > 4 ? F(a[4]) : 2.2f;
                seekRun = 0;
                seekHold = 3;
                return true;
            }
            if (key == "swaycheck")
            {
                lock (sways)
                {
                    foreach (KeyValuePair<int, SwaySeen> kv in sways.OrderBy(k => k.Value.name))
                    {
                        SwaySeen s = kv.Value;
                        Log.Message("[IsoRim probe] swaycheck " + s.name + " facing " + s.rot.ToStringHuman() + " job " + s.job + (s.applied ? " APPLIED" : " read only")
                            + ": shift x " + s.xMin.ToString("0.000") + " to " + s.xMax.ToString("0.000") + ", z " + s.zMin.ToString("0.000") + " to " + s.zMax.ToString("0.000")
                            + ", turn " + s.turnMin.ToString("0.0") + " to " + s.turnMax.ToString("0.0")
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
                        + s.turnMax.ToString("0.0") + ", screen turn " + s.screenMin.ToString("0.0") + " to " + s.screenMax.ToString("0.0") + ", worst off " + s.worst.ToString("0.0")
                        + "; at turn " + s.peakTurn.ToString("0.0") + " screen " + s.peakScreen.ToString("0.0") + "; last turn " + s.lastTurn.ToString("0.0")
                        + " screen " + s.lastScreen.ToString("0.0") + " (" + s.n + " draws)");
                }
                Log.Message("[IsoRim probe] leancheck: " + leans.Count + " figures with a body turn");
                leans.Clear();
            }
            return true;
        }
    }
}
