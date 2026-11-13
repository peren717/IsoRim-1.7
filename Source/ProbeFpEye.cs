using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only check for Api 77 (Api.AddFirstPersonEye: eye providers asked by FirstPerson.EyeOf), real flow on the
    // quick-test map with the 3D view on (layout_fpeye):
    //   fpeyecheck=label,x,z   x,z: the adult's cell; a child stands FeSide cells east of it, a second adult FeSide cells west.
    //                          Three colonists spawned with fixed seeds (FeSeedAdult, FeSeedChild, FeSeedOther), facing south;
    //                          two dining chairs FeChairRows north of the adult's walk target and of the child.
    //   0  no provider, paused: first person (as by the key) on each of the three; the camera eye logged exactly ("R") and the
    //      screen shot (fp_<who>_iso.png). With FPEYE_BASE (a base build: no Api 77) the script ends here: those lines and
    //      shots are compared with a head build's, byte for byte.
    //   1  a fake figure mod's providers added (the public Api only), in order: one that throws, one that answers NaN, the
    //      figure provider (head at FeHead times the life stage's body size factor, FeSeatedShare of that on a seat, a little
    //      in front of pawn.DrawPos; null for the second adult): the camera eye equals the provider's answer, the throwing
    //      and the NaN one each warned once and were asked once; the second adult keeps the eye of step 0; removed, the adult's
    //      eye is step 0's again; a photo (Api 76, ViewPose.FirstPersonOf) has the provided eye and looks like the provided first
    //      person (luminance grid) more than IsoRim's; registration mistakes throw.
    //   2  running: the adult walks FeWalk cells west (a real Goto job); every frame the camera eye equals the provider's last
    //      answer, it moved with them, its height stayed the provider's.
    //   3  running: the adult and the child walk to their chairs (Goto, then Wait_MaintainPosture) and sit; paused, first
    //      person on each: the eye equals the provider's seated answer.
    //   CHECK lines (ProbeFixes.Check), then "[IsoRim probe] fpeyecheck <label>: total n PASS, m FAIL".
    // Output: renders/<tag>/fpeye/ (fp_<who>_<iso|prov|seat>.png, ref_adult_<iso|prov>.png, photo_adult.png).
    public static partial class Probe
    {
        private const int FeSeedAdult = 7701, FeSeedChild = 7702, FeSeedOther = 7703;   // the colonists' generation seeds
        private const float FeChildAge = 8f;          // the child's age, years
        private const int FeSide = 2;                 // cells between the colonists
        private const int FeWalk = 6;                 // cells the adult walks west in step 2
        private const int FeChairRows = 2;            // chairs this many cells north of where the adult and the child stand before sitting
        private const int FeStartWait = 320;          // frames before the script starts (Capture17 builds and settles first)
        private const int FeSettle = 30;              // frames after first person changes before a check or a shot
        private const int FeMaxWalk = 1500;           // frames a walk may take at most
        private const int FeWaitTicks = 20000;        // ticks a colonist waits where it walked to (longer than the script)
        private const int FeMaxPicture = 240;         // frames a scene image or a photo may take at most
        private const float FeTol = 1e-4f;            // cells: an eye equals another within this
        private const float FeHead = 1.25f;           // the fake figure mod's eye height of a standing adult, cells
        private const float FeSeatedShare = 0.72f;    // its seated eye height, share of the standing one
        private const float FeBaseLift = 0.1f;        // the base-eye provider raises IsoRim's own eye this much, cells
        private const float FeForward = 0.1f;         // its eye this far in front of the body, cells

        // The fake figure mod's state.
        private static Pawn feOther;
        private static readonly Dictionary<Pawn, Vector3> feLast = new Dictionary<Pawn, Vector3>();
        private static int feThrowCalls, feNaNCalls;

        private static bool FpEyeKey(string key, string[] a, Map map)
        {
            if (key != "fpeyecheck")
            {
                return false;
            }
            Api17Patch();
            FwStart(FpEyeScript(a[0], new IntVec3(I(a[1]), 0, I(a[2])), map));
            return true;
        }

        private static string FeV(Vector3 v)
        {
            return "(" + v.x.ToString("R") + ", " + v.y.ToString("R") + ", " + v.z.ToString("R") + ")";
        }

        private static Pawn FeSpawn(Map map, IntVec3 c, int seed, bool child)
        {
            Pawn p;
            Rand.PushState(seed);
            try
            {
                float age = child ? FeChildAge : 30f;
                p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                    fixedGender: Gender.Female, fixedBiologicalAge: age, fixedChronologicalAge: age,
                    developmentalStages: child ? DevelopmentalStage.Child : DevelopmentalStage.Adult, forceNoIdeo: true, forbidAnyTitle: true));
            }
            finally
            {
                Rand.PopState();
            }
            GenSpawn.Spawn(p, c, map, Rot4.South);
            return p;
        }

        private static Vector3 FeCameraEye()
        {
            ViewPose now = Api.CurrentViewPose;
            return now.Eye.HasValue ? now.Eye.Value : new Vector3(float.NaN, float.NaN, float.NaN);
        }

#if !FPEYE_BASE
        // The fake figure mod: its own head heights (as a mod drawing its own figures, Api 62, would know them).
        private static Vector3? FeFigureEye(Pawn p)
        {
            if (p == null || p == feOther || !p.Spawned)
            {
                return null;
            }
            float size = p.ageTracker.CurLifeStage.bodySizeFactor;
            Building seat = p.Position.GetEdifice(p.Map);
            bool seated = !p.pather.MovingNow && p.GetPosture() == PawnPosture.Standing && seat != null && seat.def.building != null && seat.def.building.isSittable;
            Vector3 at = p.DrawPos;
            Vector3 ahead = p.Rotation.FacingCell.ToVector3() * FeForward;
            Vector3 eye = new Vector3(at.x + ahead.x, FeHead * size * (seated ? FeSeatedShare : 1f), at.z + ahead.z);
            feLast[p] = eye;
            return eye;
        }

        private static Vector3? FeThrowingEye(Pawn p)
        {
            feThrowCalls++;
            throw new InvalidOperationException("fpeye probe: a provider that throws");
        }

        private static Vector3? FeNaNEye(Pawn p)
        {
            feNaNCalls++;
            return new Vector3(float.NaN, 1f, 0f);
        }

        // A provider that takes IsoRim's own eye (ViewPose.FirstPersonOf inside a provider) and raises it by FeBaseLift: it must
        // not recurse (feBaseDepth counts how deep it is entered).
        private static int feBaseDepth, feBaseMaxDepth, feBaseCalls;

        private static Vector3? FeBaseEye(Pawn p)
        {
            feBaseCalls++;
            feBaseDepth++;
            feBaseMaxDepth = Math.Max(feBaseMaxDepth, feBaseDepth);
            try
            {
                if (feBaseDepth > 3)
                {
                    return null;   // (a recursion guard of the probe's own, so a failure shows as a FAIL, not a crash)
                }
                Vector3? e = ViewPose.FirstPersonOf(p).Eye;
                return e.HasValue ? e.Value + new Vector3(0f, FeBaseLift, 0f) : (Vector3?)null;   // (as a consumer would: no offset handling)
            }
            finally
            {
                feBaseDepth--;
            }
        }

        private static int FeWarnings(string part)
        {
            int n = 0;
            foreach (LogMessage m in Log.Messages)
            {
                if (m.type == LogMessageType.Warning && m.text != null && m.text.Contains(part))
                {
                    n += m.repeats;
                }
            }
            return n;
        }

        private static float FeGridMae(Texture2D a, Texture2D b)
        {
            return a == null || b == null ? float.NaN : CvMae(CvGridOf(a), CvGridOf(b));
        }
#endif

        private static IEnumerator<int> FpEyeScript(string label, IntVec3 adultCell, Map map)
        {
            string L = "fpeyecheck " + label + " ";
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
                Check(L + what, ok, detail);
            }
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag(), "fpeye");
            Directory.CreateDirectory(dir);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            yield return FeStartWait;

            Pawn adult = FeSpawn(map, adultCell, FeSeedAdult, false);
            Pawn child = FeSpawn(map, adultCell + new IntVec3(FeSide, 0, 0), FeSeedChild, true);
            Pawn other = FeSpawn(map, adultCell + new IntVec3(-FeSide, 0, 0), FeSeedOther, false);
            feOther = other;
            Find.Selector.ClearSelection();
            Log.Message("[IsoRim probe] " + L + "Api.Version " + Api.Version + ", seed " + Api.HarnessSeed + ", fpEyeHeight " + IsoRimMod.Settings.fpEyeHeight.ToString("R")
                + ", adult " + adult.LabelShort + " at " + adult.Position + " (size factor " + adult.ageTracker.CurLifeStage.bodySizeFactor + ")"
                + ", child " + child.LabelShort + " at " + child.Position + " (" + child.ageTracker.CurLifeStage.defName + ", size factor " + child.ageTracker.CurLifeStage.bodySizeFactor + ")"
                + ", other " + other.LabelShort + " at " + other.Position);
            yield return FeSettle;

            // 0: no provider. IsoRim's own eye of each, paused.
            Pawn[] who = { adult, child, other };
            string[] names = { "adult", "child", "other" };
            Vector3[] isoEye = new Vector3[3];
            for (int i = 0; i < who.Length; i++)
            {
                FwEnterFirstPerson(map, who[i].Position);
                yield return FeSettle;
                isoEye[i] = FeCameraEye();
                Log.Message("[IsoRim probe] " + L + "0 eye " + names[i] + " " + FeV(isoEye[i]) + ", View3D.Eye " + FeV(View3D.Eye) + ", frame state: first person " + (Api.FirstPersonPawn == who[i]));
                C("0 first person on the " + names[i], Api.FirstPersonPawn == who[i], "FirstPersonPawn " + (Api.FirstPersonPawn != null ? Api.FirstPersonPawn.LabelShort : "null"));
                CvShot(dir, "fp_" + names[i] + "_iso");
                yield return 2;
            }
#if !FPEYE_BASE
            Texture2D refIso = null;
            {
                FwEnterFirstPerson(map, adult.Position);
                yield return FeSettle;
                bool got = false;
                Api.RequestSceneImage(t => { refIso = t; got = true; }, new SceneImageOptions { IncludeOverlays = false });
                for (int w = 0; !got && w < FeMaxPicture; w++)
                {
                    yield return 1;
                }
                CvSave(refIso, dir, "ref_adult_iso");
            }
#endif
            Api.LeaveFirstPerson();
            Find.Selector.ClearSelection();
            yield return FeSettle;
#if FPEYE_BASE
            Log.Message("[IsoRim probe] " + L.TrimEnd() + ": total " + pass + " PASS, " + fail + " FAIL (base build: step 0 only)");
            yield break;
#else
            // 1: the fake figure mod's providers.
            bool threw = false;
            try
            {
                Api.AddFirstPersonEye(null);
            }
            catch (ArgumentNullException)
            {
                threw = true;
            }
            C("1 null provider throws", threw, "ArgumentNullException " + threw);
            Func<Pawn, Vector3?> throwing = FeThrowingEye, nan = FeNaNEye, figure = FeFigureEye;
            Api.AddFirstPersonEye(throwing);
            Api.AddFirstPersonEye(nan);
            Api.AddFirstPersonEye(figure);
            threw = false;
            try
            {
                Api.AddFirstPersonEye(figure);
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            C("1 provider added twice throws", threw, "ArgumentException " + threw);
            int warnThrowBefore = FeWarnings("first-person eye provider failed"), warnNaNBefore = FeWarnings("first-person eye provider answered");

            Vector3[] provEye = new Vector3[2];
            for (int i = 0; i < 2; i++)
            {
                FwEnterFirstPerson(map, who[i].Position);
                yield return FeSettle;
                Vector3 cam = FeCameraEye();
                Vector3 want = feLast.TryGetValue(who[i], out Vector3 v) ? v + Floors.ThingOffset(who[i]) : new Vector3(float.NaN, 0f, 0f);
                provEye[i] = cam;
                C("1 " + names[i] + ": the camera eye is the provider's", (cam - want).magnitude <= FeTol, "camera " + FeV(cam) + ", provider " + FeV(want) + ", IsoRim's (step 0) " + FeV(isoEye[i]));
                CvShot(dir, "fp_" + names[i] + "_prov");
                yield return 2;
            }
            C("1 adult's eye higher than IsoRim's, child's lower (the fake mod's heads)", provEye[0].y > isoEye[0].y && provEye[1].y < isoEye[1].y,
                "adult " + provEye[0].y.ToString("0.000") + " vs " + isoEye[0].y.ToString("0.000") + ", child " + provEye[1].y.ToString("0.000") + " vs " + isoEye[1].y.ToString("0.000"));
            C("1 the throwing provider was asked once and warned once", feThrowCalls == 1 && FeWarnings("first-person eye provider failed") - warnThrowBefore == 1,
                "asked " + feThrowCalls + ", warnings " + (FeWarnings("first-person eye provider failed") - warnThrowBefore));
            C("1 the NaN provider was asked once and warned once", feNaNCalls == 1 && FeWarnings("first-person eye provider answered") - warnNaNBefore == 1,
                "asked " + feNaNCalls + ", warnings " + (FeWarnings("first-person eye provider answered") - warnNaNBefore));

            // The second adult: the provider answers null, IsoRim's eye stays.
            FwEnterFirstPerson(map, other.Position);
            yield return FeSettle;
            Vector3 otherCam = FeCameraEye();
            C("1 other (provider null): IsoRim's eye", (otherCam - isoEye[2]).magnitude <= FeTol && !feLast.ContainsKey(other), "camera " + FeV(otherCam) + ", step 0 " + FeV(isoEye[2]));

            // The photo (Api 76): its pose and its picture follow the provided eye.
            FwEnterFirstPerson(map, adult.Position);
            yield return FeSettle;
            Texture2D refProv = null;
            {
                bool got = false;
                Api.RequestSceneImage(t => { refProv = t; got = true; }, new SceneImageOptions { IncludeOverlays = false });
                for (int w = 0; !got && w < FeMaxPicture; w++)
                {
                    yield return 1;
                }
                CvSave(refProv, dir, "ref_adult_prov");
            }
            float fov = Mathf.Clamp(IsoRimMod.Settings.fpFov, 15f, 100f);
            ViewPose photoPose = ViewPose.FirstPersonOf(adult, FirstPerson.StartPitch, fov);
            Vector3 photoWant = feLast[adult] + Floors.ThingOffset(adult);
            C("1 FirstPersonOf: the provider's eye", photoPose.Eye.HasValue && (photoPose.Eye.Value - photoWant).magnitude <= FeTol, "pose eye " + (photoPose.Eye.HasValue ? FeV(photoPose.Eye.Value) : "null") + ", provider " + FeV(photoWant));
            ViewPose childPose = ViewPose.FirstPersonOf(child, FirstPerson.StartPitch, fov);
            Vector3 childWant = feLast[child] + Floors.ThingOffset(child);
            C("1 FirstPersonOf child: the provider's eye", childPose.Eye.HasValue && (childPose.Eye.Value - childWant).magnitude <= FeTol, "pose eye " + (childPose.Eye.HasValue ? FeV(childPose.Eye.Value) : "null") + ", provider " + FeV(childWant));
            Texture2D photo = null;
            bool asked = Api.CaptureView(map, photoPose, new CaptureViewOptions { Width = Screen.width, Height = Screen.height, FirstPersonPawn = adult }, t => photo = t);
            for (int w = 0; asked && photo == null && w < FeMaxPicture; w++)
            {
                yield return 1;
            }
            CvSave(photo, dir, "photo_adult");
            float toProv = FeGridMae(photo, refProv), toIso = FeGridMae(photo, refIso), provIso = FeGridMae(refProv, refIso);
            C("1 photo delivered and closer to the provided first person than to IsoRim's", photo != null && toProv < toIso,
                "grid MAE photo-provided " + toProv.ToString("0.0000") + ", photo-IsoRim " + toIso.ToString("0.0000") + ", provided-IsoRim first person " + provIso.ToString("0.0000"));

            // Removed: IsoRim's eye again; added back.
            Api.RemoveFirstPersonEye(figure);
            Api.RemoveFirstPersonEye(figure);   // (not added: nothing happens)
            yield return FeSettle;
            Vector3 back = FeCameraEye();
            C("1 provider removed: IsoRim's eye again", (back - isoEye[0]).magnitude <= FeTol, "camera " + FeV(back) + ", step 0 " + FeV(isoEye[0]));

            // A provider built on IsoRim's own eye (FirstPersonOf inside it): that eye plus FeBaseLift, no recursion.
            Func<Pawn, Vector3?> baseEye = FeBaseEye;
            Api.AddFirstPersonEye(baseEye);
            feBaseCalls = 0;
            yield return FeSettle;
            Vector3 lifted = FeCameraEye();
            Vector3 liftWant = isoEye[0] + new Vector3(0f, FeBaseLift, 0f);
            C("1 provider on IsoRim's eye (FirstPersonOf inside): IsoRim's eye + " + FeBaseLift + ", no recursion", (lifted - liftWant).magnitude <= FeTol && feBaseMaxDepth == 1 && feBaseCalls > 0,
                "camera " + FeV(lifted) + ", want " + FeV(liftWant) + ", deepest " + feBaseMaxDepth + ", asked " + feBaseCalls + " times in " + FeSettle + " frames");
            Api.RemoveFirstPersonEye(baseEye);

            // The same on a bridge deck (a non-zero floor offset, Bridges.Deck): the offset is added once, not twice.
            IntVec3 deck = other.Position;
            map.terrainGrid.SetFoundation(deck, TerrainDefOf.Bridge);
            FwEnterFirstPerson(map, deck);
            yield return FeSettle;
            Vector3 deckOffset = Floors.ThingOffset(other);
            Vector3 isoDeck = FeCameraEye();
            Api.AddFirstPersonEye(baseEye);
            feBaseMaxDepth = 0;
            yield return FeSettle;
            Vector3 deckCam = FeCameraEye();
            Vector3 deckWant = isoDeck + new Vector3(0f, FeBaseLift, 0f);
            C("1 provider on IsoRim's eye on a bridge deck: IsoRim's eye + " + FeBaseLift + ", offset once", deckOffset.y > FeTol && (deckCam - deckWant).magnitude <= FeTol && feBaseMaxDepth == 1,
                "camera " + FeV(deckCam) + ", want " + FeV(deckWant) + " (IsoRim's on the deck " + FeV(isoDeck) + ", floor offset " + FeV(deckOffset) + "), deepest " + feBaseMaxDepth);
            Api.RemoveFirstPersonEye(baseEye);
            map.terrainGrid.RemoveFoundation(deck);   // (the adult walks over this cell in step 2)
            FwEnterFirstPerson(map, adult.Position);
            Api.AddFirstPersonEye(figure);
            yield return FeSettle;

            // 2: the adult walks west; the eye follows the provider's every frame.
            Vector3 start = FeCameraEye();
            IntVec3 to = adult.Position + new IntVec3(-FeWalk, 0, 0);
            adult.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, to), JobCondition.InterruptForced);
            Job stay = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
            stay.expiryInterval = FeWaitTicks;
            adult.jobs.jobQueue.EnqueueFirst(stay);   // (then it stays there)
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            int frames = 0, bad = 0, samples = 0;
            float worst = 0f, heightOff = 0f;
            while (frames < FeMaxWalk && (adult.Position != to || adult.pather.Moving))
            {
                yield return 1;
                frames++;
                if (Api.FirstPersonPawn != adult)
                {
                    continue;
                }
                Vector3 cam = FeCameraEye();
                Vector3 want = feLast[adult] + Floors.ThingOffset(adult);
                float d = (cam - want).magnitude;
                worst = Mathf.Max(worst, d);
                heightOff = Mathf.Max(heightOff, Mathf.Abs(cam.y - FeHead * adult.ageTracker.CurLifeStage.bodySizeFactor));
                samples++;
                if (d > FeTol)
                {
                    bad++;
                }
            }
            yield return FeSettle;   // (still running: the drawn position catches up with the arrival; the wait job holds them there)
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            yield return FeSettle;
            Vector3 end = FeCameraEye();
            Vector3 endWant = feLast[adult] + Floors.ThingOffset(adult);
            float moved = new Vector2(end.x - start.x, end.z - start.z).magnitude;
            C("2 walking: the eye is the provider's every frame", samples > 30 && bad == 0, samples + " frames, " + bad + " off, worst " + worst.ToString("0.000000") + " cells, walk " + frames + " frames");
            C("2 walking: the eye moved with them at the provider's height", moved >= FeWalk - 1 && heightOff <= FeTol && (end - endWant).magnitude <= FeTol,
                "moved " + moved.ToString("0.00") + " cells from " + FeV(start) + " to " + FeV(end) + " (provider " + FeV(endWant) + ", drawn at " + FeV(adult.DrawPos) + ", cell " + adult.Position
                + "), height off by at most " + heightOff.ToString("0.000000"));

            // 3: the adult and the child walk to their chairs and sit (a real job each), then first person on each.
            IntVec3 chairA = adult.Position + new IntVec3(0, 0, FeChairRows), chairC = child.Position + new IntVec3(0, 0, FeChairRows);
            foreach (IntVec3 c in new[] { chairA, chairC })
            {
                Thing chair = ThingMaker.MakeThing(ThingDef.Named("DiningChair"), ThingDefOf.WoodLog);
                chair.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(chair, c, map, Rot4.South);
            }
            Api.LeaveFirstPerson();
            Find.Selector.ClearSelection();
            foreach (Pawn p in new[] { adult, child })
            {
                IntVec3 c = p == adult ? chairA : chairC;
                p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, c), JobCondition.InterruptForced);
                Job wait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
                wait.expiryInterval = FeWaitTicks;
                p.jobs.jobQueue.EnqueueFirst(wait);
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            frames = 0;
            while (frames < FeMaxWalk && !(adult.Position == chairA && child.Position == chairC && !adult.pather.MovingNow && !child.pather.MovingNow))
            {
                yield return 1;
                frames++;
            }
            yield return FeSettle;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            yield return FeSettle;
            Pawn[] sitters = { adult, child };
            for (int i = 0; i < 2; i++)
            {
                Pawn p = sitters[i];
                Poses.Seat seat;
                bool isoSeated = Poses.Seated(p, out seat);
                FwEnterFirstPerson(map, p.Position);
                yield return FeSettle;
                Vector3 cam = FeCameraEye();
                Vector3 want = feLast[p] + Floors.ThingOffset(p);
                float wantY = FeHead * p.ageTracker.CurLifeStage.bodySizeFactor * FeSeatedShare;
                C("3 " + names[i] + " seated: the camera eye is the provider's seated eye", (cam - want).magnitude <= FeTol && Mathf.Abs(cam.y - wantY) <= FeTol,
                    "camera " + FeV(cam) + ", provider " + FeV(want) + ", want height " + wantY.ToString("0.0000") + ", at " + p.Position + " job " + (p.CurJobDef != null ? p.CurJobDef.defName : "none")
                    + ", IsoRim counts them seated " + isoSeated + ", walked " + frames + " frames");
                CvShot(dir, "fp_" + names[i] + "_seat");
                yield return 2;
            }

            Api.LeaveFirstPerson();
            Api.RemoveFirstPersonEye(figure);
            Api.RemoveFirstPersonEye(throwing);   // (already removed by IsoRim)
            Api.RemoveFirstPersonEye(nan);
            if (refIso != null)
            {
                UnityEngine.Object.Destroy(refIso);
            }
            if (refProv != null)
            {
                UnityEngine.Object.Destroy(refProv);
            }
            if (photo != null)
            {
                UnityEngine.Object.Destroy(photo);
            }
            Log.Message("[IsoRim probe] " + L.TrimEnd() + ": total " + pass + " PASS, " + fail + " FAIL");
#endif
        }
    }
}
