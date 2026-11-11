using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Harness-only check for Api 71 (Api.FirstPersonOn = FirstPerson.Engaged):
    //   fponcheck=label   with the 3D view on: a colonist (fixed seed FpOnPawnSeed) spawned on the first free standable cell round
    //                     the map's centre (outside a Capture17 scene), the game unpaused, then over real frames (the colonist
    //                     is removed at the end):
    //     0  3D, no first person                                  -> FirstPersonOn false, FirstPersonPawn null
    //     1  first person entered as by the key (selected, FirstPerson.Toggle) -> true, the pawn
    //     2  a view pose pushed (Api 66)                          -> true, null
    //     3  the pose disposed                                    -> true, the pawn
    //     4  Api.LeaveFirstPerson                                 -> false, null (at once and after frames)
    //     c  a pose pushed first, then first person entered under it -> true, null; pose gone -> the pawn
    //     b  the 3D view switched off (Api.View3DActive) with first person on -> false at once (FirstPerson.On still
    //        true until the next frame), FirstPersonPawn null
    //     5  flat view, first person left by IsoRim                -> false, null
    //     a  first person entered from the flat view (Toggle switches 3D on) -> true at once with ViewReady false and
    //        FirstPersonPawn null; once ViewReady: the pawn
    //   CHECK lines (ProbeFixes.Check), then "[IsoRim probe] fponcheck <label>: total n PASS, m FAIL". Runs on the Api 17 frame
    //   script runner (FwStart).
    public static partial class Probe
    {
        private const int FpOnSettle = 10;            // frames between a change and its check
        private const int FpOnReadyMaxFrames = 600;   // the flat-to-3D rebuild must finish within this
        private const int FpOnPawnSeed = 7101;        // the probe colonist's generation seed
        private const float FpOnPawnRadius = 12f;     // cells round the map's centre searched for the colonist's free cell

        private static bool FpOnKey(string key, string[] a, Map map)
        {
            if (key != "fponcheck")
            {
                return false;
            }
            Api17Patch();
            FwStart(FpOnScript(a[0], map));
            return true;
        }

        private static string FpOnState(Pawn p)
        {
            Pawn fp = Api.FirstPersonPawn;
            return "FirstPersonOn " + Api.FirstPersonOn + ", FirstPersonPawn " + (fp == null ? "null" : fp == p ? "the pawn" : fp.LabelShort)
                + ", FirstPerson.On " + FirstPerson.On + ", ViewReady " + Api.ViewReady + ", View3DActive " + Api.View3DActive
                + ", ViewPoseActive " + Api.ViewPoseActive + ", frame " + UnityEngine.Time.frameCount;
        }

        private static IEnumerator<int> FpOnScript(string label, Map map)
        {
            string L = "fponcheck " + label + " ";
            int pass = 0, fail = 0;
            void C(string what, bool ok, Pawn who)
            {
                if (ok)
                {
                    pass++;
                }
                else
                {
                    fail++;
                }
                Check(L + what, ok, FpOnState(who));
            }

            IntVec3 cell = GenRadial.RadialCellsAround(map.Center, FpOnPawnRadius, true).First(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null);
            Pawn p = FwTempPawn(map, cell, FpOnPawnSeed);
            Log.Message("[IsoRim probe] " + L + "Api.Version " + Api.Version + ", pawn " + p.LabelShort + " at " + p.Position);
            if (FirstPerson.On)
            {
                FirstPerson.Leave(null);
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            yield return FpOnSettle;
            C("0 3D, no first person", Api.ViewReady && !Api.FirstPersonOn && Api.FirstPersonPawn == null, p);

            // 1: as the first person key (FirstPerson.Toggle with one colonist selected).
            FwEnterFirstPerson(map, p.Position);
            yield return FpOnSettle;
            C("1 first person via Toggle", Api.FirstPersonOn && Api.FirstPersonPawn == p && Api.ViewReady, p);

            // 2, 3: a pose holds the view, then goes.
            IDisposable pose = Api.PushViewPose(typeof(Probe), () => ViewPose.Orbit(null, 30f, 50f, false));
            yield return FpOnSettle;
            C("2 pose pushed", Api.FirstPersonOn && Api.FirstPersonPawn == null && Api.ViewPoseActive, p);
            pose.Dispose();
            yield return FpOnSettle;
            C("3 pose popped", Api.FirstPersonOn && Api.FirstPersonPawn == p && !Api.ViewPoseActive, p);

            // 4: leaving.
            bool left = Api.LeaveFirstPerson();
            C("4a LeaveFirstPerson, same frame", left && !Api.FirstPersonOn && Api.FirstPersonPawn == null, p);
            yield return FpOnSettle;
            C("4 after leaving", !Api.FirstPersonOn && Api.FirstPersonPawn == null && Api.ViewReady, p);

            // c: first person comes on behind a pose (what a camera that gives way watches for).
            pose = Api.PushViewPose(typeof(Probe), () => ViewPose.Orbit(null, 30f, 50f, false));
            yield return FpOnSettle;
            C("c1 pose, no first person", !Api.FirstPersonOn && Api.FirstPersonPawn == null && Api.ViewPoseActive, p);
            FwEnterFirstPerson(map, p.Position);
            C("c2 first person under the pose, same frame", Api.FirstPersonOn && Api.FirstPersonPawn == null, p);
            yield return FpOnSettle;
            C("c3 first person under the pose", Api.FirstPersonOn && Api.FirstPersonPawn == null && Api.ViewPoseActive, p);
            pose.Dispose();
            yield return FpOnSettle;
            C("c4 pose gone: in the head", Api.FirstPersonOn && Api.FirstPersonPawn == p, p);

            // b, 5: the 3D view switched off with first person on.
            Api.View3DActive = false;
            C("b switch off, same frame", !Api.FirstPersonOn && FirstPerson.On && Api.FirstPersonPawn == null, p);
            yield return FpOnSettle;
            C("5 flat view, first person left", !Api.FirstPersonOn && !FirstPerson.On && Api.FirstPersonPawn == null && !Api.View3DActive && !Api.ViewReady, p);

            // a: first person from the flat view (Toggle switches the view on; the map is prepared for 3D first).
            FwEnterFirstPerson(map, p.Position);
            C("a1 from flat, same frame", Api.FirstPersonOn && !Api.ViewReady && Api.FirstPersonPawn == null, p);
            int waited = 0;
            while (!Api.ViewReady && waited < FpOnReadyMaxFrames)
            {
                waited++;
                yield return 1;
            }
            C("a2 3D ready after " + waited + " frames", Api.FirstPersonOn && Api.FirstPersonPawn == p, p);

            Api.LeaveFirstPerson();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            yield return FpOnSettle;
            C("end left", !Api.FirstPersonOn && Api.FirstPersonPawn == null, p);
            if (!p.Destroyed)
            {
                p.Destroy(DestroyMode.Vanish);   // the probe colonist is temporary: despawned and gone from the game
            }
            Log.Message("[IsoRim probe] " + L + "probe colonist removed: spawned " + p.Spawned + ", destroyed " + p.Destroyed);
            Log.Message("[IsoRim probe] " + L.TrimEnd() + ": total " + pass + " PASS, " + fail + " FAIL");
        }
    }
}
