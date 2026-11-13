using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only check for Api 76 (Api.CaptureView, Photos.cs), real flow on the quick-test map with the 3D view on:
    //   capview=label,mode,x,z   mode cap (photos taken), nocap (the same frames, no photo asked: the reference run for the
    //                            player's screen) or slow (as nocap, each of those frames made CvSlowMs slower: what a slow
    //                            frame alone does to the screen afterwards); x,z the scene's middle cell. A colonist (fixed seed CvPawnSeed) is spawned
    //                            on the first free standable cell round it, facing south.
    //   capup=x,z                (with ProjectZ) the thing to photograph on floor 1 for the "upper" pose (the floor shown).
    // Poses: iso (orthographic, from the other side of the player's view, yaw 225), far (perspective, zoomed out, yaw 135),
    // fp (ViewPose.FirstPersonOf the colonist, left out of the photo), selfie (ViewPose.LookAt the colonist's face from in
    // front), upper (capup set: LookAt the floor-1 thing). Steps (cap mode; nocap takes the same screenshots only):
    //   0  before any photo: nothing hooked (no Harmony patch on Graphics.DrawMesh from Photos), frame times measured.
    //   1  references: each pose pushed (Api 66) and pictured with RequestSceneImage (no overlays), then given back.
    //   2  paused, per pose: the screen shot on the frame before the photo, the photo's frame and the one after (and one
    //      more pair before, for the noise floor); the photo saved. Checks: the screen of the photo's frame and the frame
    //      after equal the frame before (or differ no more than two frames without a photo do); the photo is not empty;
    //      it matches its own reference far better than the player's view or the other poses' references.
    //   3  running (normal speed): every CvRunEvery frames the screen is shot (run_k.png) and, in cap mode, a photo is
    //      asked in that frame (poses in turn). Frame times with and without a photo logged. The runner compares run_k.png
    //      of a cap run with a nocap run (and two nocap runs for the noise floor).
    //   CHECK lines, "[IsoRim probe] capcheck <label>: total n PASS, m FAIL"; Capture17 takes its (identical, paused) shots on and
    //   quits (the layout has enough of them to outlast the script).
    // Output: renders/<tag>/capview/ (ref_<pose>.png, photo_<pose>.png, screen_<pose>_<k>.png, run_k.png, run_k_photo.png).
    public static partial class Probe
    {
        private const int CvPawnSeed = 7601;          // the probe colonist's generation seed
        private const float CvPawnRadius = 6f;        // cells round the scene's middle searched for its free cell
        private const int CvSettle = 10;              // frames after a change before the next step
        private const int CvStartWait = 150;          // frames before the script starts (Capture17 moves the camera at its first shot)
        private const int CvRefWait = 3;              // frames a pushed pose holds before its reference is asked for
        private const int CvAfterRefs = 120;          // frames after the references before the paused photos (the camera settled)
        private const int CvMaxWait = 240;            // frames a picture may take at most
        private const int CvRunShots = 8;             // running phase: screenshots (and photos)
        private const int CvRunEvery = 30;            // frames between them
        private const int CvSlowMs = 20;              // mode slow: that frame made this much slower instead of a photo (a photo costs about that)
        private const int CvTimeFrames = 60;          // frames measured for a frame-time figure
        private const int CvGrid = 48;                // the pose check compares 48 x 27 cell luminance grids
        private const float CvOwnMax = 0.08f;         // a photo's mean grid difference to its own reference at most (0-1 luminance)
        private const float CvOwnShare = 0.6f;        // and at most this share of its difference to any other reference
        private const float CvMinSpread = 0.02f;      // a photo's luminance spread (std) at least: not empty

        private static Vector2Int cvUpper = new Vector2Int(-1, -1);
        private static Vector2Int cvFar = new Vector2Int(-1, -1);
        private static bool cvIconHooked;
        private static readonly List<Thing> cvIcons = new List<Thing>();
        private static readonly List<string> cvUiTargets = new List<string>();   // render targets active while the icons were drawn
        private static int cvIconFrames, cvUiShots;
        private const float CvIconX = 300f, CvIconY = 300f, CvIconStep = 140f, CvIconSize = 128f;

        // 2u: icons drawn with a material (Widgets.ThingIcon of stuffed walls, a colonist: GenUI.DrawTextureWithMaterial,
        // Graphics.DrawTexture, which draws into RenderTexture.active when one is set) on the interface every frame.
        public static void CvIconOnGUI()
        {
            if (cvIcons.Count == 0)
            {
                return;
            }
            if (Event.current.type == EventType.Repaint)
            {
                cvIconFrames++;
                if (RenderTexture.active != null)
                {
                    cvUiTargets.Add(Time.frameCount + ":" + RenderTexture.active.name);
                }
            }
            for (int i = 0; i < cvIcons.Count; i++)
            {
                if (cvIcons[i] != null && cvIcons[i].Spawned)
                {
                    Widgets.ThingIcon(new Rect(CvIconX + i * CvIconStep, CvIconY, CvIconSize, CvIconSize), cvIcons[i]);
                }
            }
        }
       // capfar: middle of the floor-1 area outside the shown view
        private static readonly List<IntVec3> cvFarWalls = new List<IntVec3>();
        private static Vector3 cvFarAt;
        private const int CvFarSpread = 8;   // its walls stand this far either way of the middle (other map blocks: 17 cells)

        private static bool CapViewKey(string key, string[] a, Map map)
        {
            if (key == "capup")
            {
                cvUpper = new Vector2Int(I(a[0]), I(a[1]));
                return true;
            }
            if (key == "capfar")
            {
                cvFar = new Vector2Int(I(a[0]), I(a[1]));
                return true;
            }
            if (key != "capview")
            {
                return false;
            }
            Api17Patch();
            if (!cvIconHooked)
            {
                cvIconHooked = true;
                new Harmony("peren.isorim.probe.capview").Patch(AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)),
                    postfix: new HarmonyMethod(typeof(Probe), nameof(CvIconOnGUI)));
            }
            FwStart(CapViewScript(a[0], a[1], new IntVec3(I(a[2]), 0, I(a[3])), map));
            return true;
        }

        private sealed class CvPose
        {
            public string name;
            public Func<ViewPose> pose;
            public Pawn eyesOf;
            public Texture2D reference, photo;
        }

        // Whether the photo's per-frame step is hooked (Map.MapUpdate postfix: made with the first request; the draw entry
        // points are hooked at load and only test a flag until then).
        private static bool CvHooked()
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(AccessTools.Method(typeof(Map), nameof(Map.MapUpdate)));
            return info != null && info.Owners.Contains(Photos.HarmonyId);
        }

        private static IEnumerator<int> CapViewScript(string label, string mode, IntVec3 centre, Map map)
        {
            string L = "capcheck " + label + " ";
            bool cap = mode == "cap";
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
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag(), "capview");
            Directory.CreateDirectory(dir);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            yield return CvStartWait;   // Capture17 puts the camera over the scene (its first shot) first

            IntVec3 cell = GenRadial.RadialCellsAround(centre, CvPawnRadius, true).First(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null);
            Pawn p = FwTempPawn(map, cell, CvPawnSeed);
            Find.Selector.ClearSelection();
            yield return CvSettle;
            Log.Message("[IsoRim probe] " + L + "mode " + mode + ", Api.Version " + Api.Version + ", colonist " + p.LabelShort + " at " + p.Position + " facing " + p.Rotation
                + ", screen " + Screen.width + "x" + Screen.height + ", seed " + Api.HarnessSeed);

            List<CvPose> poses = new List<CvPose>();
            Vector3 mid = centre.ToVector3Shifted();
            poses.Add(new CvPose { name = "iso", pose = () => ViewPose.Orbit(mid, 225f, 35.3f, false, 9f) });
            poses.Add(new CvPose { name = "far", pose = () => ViewPose.Orbit(mid, 135f, 55f, true, 28f) });
            poses.Add(new CvPose { name = "fp", pose = () => ViewPose.FirstPersonOf(p, FirstPerson.StartPitch, Mathf.Clamp(IsoRimMod.Settings.fpFov, 15f, 100f)), eyesOf = p });   // as IsoRim's own first person
            poses.Add(new CvPose { name = "selfie", pose = () => CvSelfie(p) });
            if (cvFar.x >= 0 && cvFarWalls.Count == 0)
            {
                // Granite walls on floor 1 in four map blocks round the middle (built now, rebuilt by the game while unseen).
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        IntVec3 c = Floors.Stacked(map, new IntVec3(cvFar.x + sx * CvFarSpread, 0, cvFar.y + sz * CvFarSpread), 1);
                        for (int dx = -1; dx <= 1 && c.IsValid; dx++)
                        {
                            Thing w = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                            w.SetFaction(Faction.OfPlayer);
                            GenSpawn.Spawn(w, c + new IntVec3(dx, 0, 0), map);
                            cvFarWalls.Add(c + new IntVec3(dx, 0, 0));
                        }
                    }
                }
                IntVec3 farMid = Floors.Stacked(map, new IntVec3(cvFar.x, 0, cvFar.y), 1);
                Vector3 mo = farMid.IsValid ? Floors.ThingOffset(cvFarWalls.Count > 0 ? cvFarWalls[0].GetFirstBuilding(map) : null) : Vector3.zero;
                // (where floor 1 shows: its stacked cell moved by the floor's offset, half a wall up)
                cvFarAt = farMid.IsValid ? farMid.ToVector3Shifted() + mo + new Vector3(0f, 0.75f, 0f) : Vector3.zero;
                C("far floor-1 walls", cvFarWalls.Count == 12, cvFarWalls.Count + " walls, looked at " + cvFarAt);
            }
            Vector4 cast;
            Shader.TryGetGlobalVector(ShaderPropertyIDs.MapSunLightDirection, out cast);
            Log.Message("[IsoRim probe] " + L + "hour " + GenLocalDate.HourOfDay(map) + ", the map's cast vector " + cast + ", its length " + new Vector3(cast.x, cast.y, cast.z).magnitude.ToString("0.000"));
            Thing upper = null;
            if (cvUpper.x >= 0)
            {
                // Three granite walls in a row on floor 1 (a landmark that only that floor has), the middle one photographed.
                IntVec3 up = Floors.Stacked(map, new IntVec3(cvUpper.x, 0, cvUpper.y), 1);
                for (int dx = -1; dx <= 1 && up.IsValid; dx++)
                {
                    Thing put = (up + new IntVec3(dx, 0, 0)).GetFirstBuilding(map);   // (a later pass: already built)
                    if (put == null)
                    {
                        Thing w = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                        w.SetFaction(Faction.OfPlayer);
                        put = GenSpawn.Spawn(w, up + new IntVec3(dx, 0, 0), map);
                    }
                    upper = dx == 0 ? put : upper;
                }
                if (upper != null)
                {
                    Thing u = upper;
                    Vector3 uo = Floors.ThingOffset(u);   // true space: the floor's rows and height
                    Vector3 uAt = new Vector3(u.Position.x + 0.5f + uo.x, uo.y + 0.75f, u.Position.z + 0.5f + uo.z);
                    poses.Add(new CvPose { name = "upper", pose = () => CvLookDown(uAt, 225f) });
                }
                C("upper floor thing", upper != null, "floor-1 cell " + up + ", floor offset (true space) " + (upper != null ? Floors.ThingOffset(upper).ToString() : "-"));
            }

            // 0: nothing hooked before the first photo; frame times without photos.
            if (Photos.Taken == 0)   // (a later pass of the same game comes after photos)
            {
                C("0 unused: no per-frame hook", !CvHooked(), "Map.MapUpdate patched by Photos " + CvHooked());
            }
            List<float> dts = new List<float>();
            for (int i = 0; i < CvTimeFrames; i++)
            {
                yield return 1;
                dts.Add(Time.unscaledDeltaTime * 1000f);
            }
            float msUnused = Median(dts);

            // 1: references from Api 66 (the shown view takes each pose for a few frames).
            Texture2D mainRef = null;
            if (cap)
            {
                Texture2D got = null;
                bool done = false;
                Api.RequestSceneImage(t => { got = t; done = true; }, new SceneImageOptions { IncludeOverlays = false });
                for (int w = 0; !done && w < CvMaxWait; w++)
                {
                    yield return 1;
                }
                mainRef = got;
                CvSave(mainRef, dir, "ref_main");
                foreach (CvPose cp in poses)
                {
                    // The reference of a first-person photo is IsoRim's own first person (the colonist left out as in the
                    // photo); the others are the pose pushed on the shown view (Api 66).
                    IDisposable held = null;
                    if (cp.eyesOf != null)
                    {
                        FwEnterFirstPerson(map, cp.eyesOf.Position);
                    }
                    else
                    {
                        held = Api.PushViewPose(typeof(Probe), () => cp.pose());
                    }
                    yield return CvRefWait;
                    got = null;
                    done = false;
                    Api.RequestSceneImage(t => { got = t; done = true; }, new SceneImageOptions { IncludeOverlays = false });
                    for (int w = 0; !done && w < CvMaxWait; w++)
                    {
                        yield return 1;
                    }
                    cp.reference = got;
                    if (held != null)
                    {
                        held.Dispose();
                    }
                    else
                    {
                        Api.LeaveFirstPerson();
                        Find.Selector.ClearSelection();
                    }
                    CvSave(cp.reference, dir, "ref_" + cp.name);
                    yield return CvSettle;
                }
            }

            yield return CvAfterRefs;   // the player's camera swung back from the references (first person turns it)

            // 2: paused photos, the screen round each.
            int k = 0;
            foreach (CvPose cp in poses)
            {
                string s = "screen_" + cp.name + "_";
                CvShot(dir, s + "0");
                yield return 1;
                CvShot(dir, s + "1");
                yield return 1;
                CvShot(dir, s + "2");   // the frame of the photo
                bool asked = false;
                if (cap)
                {
                    asked = Api.CaptureView(map, cp.pose(), new CaptureViewOptions { Width = Screen.width, Height = Screen.height, FirstPersonPawn = cp.eyesOf },
                        t => cp.photo = t);
                    C("2 " + cp.name + " asked", asked, "CaptureView returned " + asked);
                }
                yield return 1;
                CvShot(dir, s + "3");   // the frame after (the photo read back and handed over)
                yield return 1;
                CvShot(dir, s + "4");
                int waited = 0;
                while (asked && cp.photo == null && waited < CvMaxWait)
                {
                    waited++;
                    yield return 1;
                }
                if (cap)
                {
                    C("2 " + cp.name + " delivered", cp.photo != null, "after " + waited + " more frames, pass " + Photos.LastPassMs.ToString("0.00") + " ms ("
                        + Photos.LastDraws + " draws, " + Photos.LastClones + " blocks copied), render " + Photos.LastBakeMs.ToString("0.00") + " ms, read " + Photos.LastReadMs.ToString("0.00") + " ms");
                    CvSave(cp.photo, dir, "photo_" + cp.name);
                }
                k++;
                yield return CvSettle;
            }
            // 2u: the same with the interface on (screenshot mode off, the colonist selected: inspect pane, gizmos, colonist bar
            // with its portraits): the frame the photo is read back in must not move any interface drawing into the photo.
            {
                bool shotMode = Find.UIRoot.screenshotMode.Active;
                Find.UIRoot.screenshotMode.Active = false;
                Find.Selector.ClearSelection();
                Find.Selector.Select(p, false, false);
                Messages.Clear();   // (fading messages change every frame)
                cvIcons.Clear();
                cvIcons.Add(p);
                cvIcons.AddRange(map.listerBuildings.allBuildingsColonist.Where(b => b.def == ThingDefOf.Wall).Take(3));
                Log.Message("[IsoRim probe] " + L + "2u: " + cvIcons.Count + " icons drawn on the interface, selected " + Find.Selector.NumSelected);
                yield return CvSettle;
                // A screenshot every frame from before the photo is asked for to two frames after it is handed over (it is read
                // back at the start of a frame, before that frame's interface).
                string s = "screen_ui_";
                cvUiTargets.Clear();
                cvIconFrames = 0;
                CvShot(dir, s + "0");
                yield return 1;
                CvShot(dir, s + "1");
                Texture2D uiPhoto = null;
                int askedAt = Time.frameCount, gotAt = -1;
                if (cap)
                {
                    Api.CaptureView(map, poses[0].pose(), new CaptureViewOptions { Width = 640, Height = 360 }, t => { uiPhoto = t; gotAt = Time.frameCount; });
                }
                int uiShots = 2;
                for (int w = 0; w < CvMaxWait && (cap ? gotAt < 0 || Time.frameCount < gotAt + 2 : w < 4); w++)
                {
                    yield return 1;
                    CvShot(dir, s + uiShots);
                    uiShots++;
                }
                cvUiShots = uiShots;
                if (cap)
                {
                    C("2u photo with the interface on", uiPhoto != null, uiPhoto != null ? "delivered " + (gotAt - askedAt) + " frames after asking" : "null");
                    CvSave(uiPhoto, dir, "photo_ui");
                }
                yield return CvSettle;   // (the interface of the last shot's frame is drawn after this script's step)
                C("2u no render target active while the interface drew", cvIconFrames > 0 && cvUiTargets.Count == 0,
                    cvIconFrames + " interface frames with the icons, " + (cvUiTargets.Count == 0 ? "none with a target" : "with a target: " + string.Join(", ", cvUiTargets.Take(5))));
                cvIcons.Clear();
                Find.Selector.ClearSelection();
                Find.UIRoot.screenshotMode.Active = shotMode;
                yield return CvSettle;
            }
            if (cap && cvFar.x >= 0)
            {
                // 2f (ProjectZ): blocks of floor 1 outside the shown view, waiting for their rebuild, photographed in that
                // frame: drawn as last built and on their floor (the game's DrawSection with the floor hooks), as the reference
                // shows them once rebuilt.
                foreach (IntVec3 c in cvFarWalls)
                {
                    map.mapDrawer.MapMeshDirty(c, (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.Things);
                }
                // (the game rebuilds blocks in its shown area, Floors-widened, before anything is drawn: the far blocks must lie outside it)
                CellRect shownRect = Find.CameraDriver.CurrentViewRect;
                int inShown = cvFarWalls.Count(c => shownRect.Overlaps(new CellRect(c.x / 17 * 17, c.z / 17 * 17, 17, 17)));
                yield return 1;   // (the game marks them waiting in its next update)
                Section[,] secs = Traverse.Create(map.mapDrawer).Field("sections").GetValue<Section[,]>();
                int waiting = cvFarWalls.Select(c => secs[c.x / 17, c.z / 17]).Distinct().Count(sc => Cull.anyDirtyOf(sc));
                int dirtyBefore = Photos.DirtyDrawn;
                Texture2D farPhoto = null, farRef = null;
                Func<ViewPose> farPose = () => CvLookDown(cvFarAt, 225f);
                Api.CaptureView(map, farPose(), new CaptureViewOptions { Width = Screen.width, Height = Screen.height }, t => farPhoto = t);
                for (int w = 0; farPhoto == null && w < CvMaxWait; w++)
                {
                    yield return 1;
                }
                int dirtyDrawn = Photos.DirtyDrawn - dirtyBefore;
                CvSave(farPhoto, dir, "photo_upfar");
                IDisposable held = Api.PushViewPose(typeof(Probe), () => farPose());
                yield return 30;   // shown: rebuilt
                bool refDone = false;
                Api.RequestSceneImage(t => { farRef = t; refDone = true; }, new SceneImageOptions { IncludeOverlays = false });
                for (int w = 0; !refDone && w < CvMaxWait; w++)
                {
                    yield return 1;
                }
                held.Dispose();
                CvSave(farRef, dir, "ref_upfar");
                C("2f floor-1 blocks outside the view drawn while waiting for their rebuild", dirtyDrawn > 0 && inShown == 0,
                    dirtyDrawn + " waiting blocks drawn as built (waiting before the photo " + waiting + ", far walls in blocks of the shown area " + inShown + ", shown area " + shownRect + ", map " + map.Size + ")");
                if (farPhoto != null && farRef != null && mainRef != null)
                {
                    float[] g = CvGridOf(farPhoto);
                    float own = CvMae(g, CvGridOf(farRef)), other = CvMae(g, CvGridOf(mainRef)), spread = CvSpread(g);
                    C("2f floor-1 blocks outside the view on their floor", spread >= CvMinSpread && own <= CvOwnMax && own <= CvOwnShare * other,
                        "luminance spread " + spread.ToString("0.000") + ", mean grid difference to its reference " + own.ToString("0.0000") + ", to the main view " + other.ToString("0.0000"));
                }
                else
                {
                    C("2f floor-1 blocks outside the view on their floor", false, "photo " + (farPhoto != null) + ", reference " + (farRef != null));
                }
                yield return CvSettle;
            }
            bool hookedAfter = CvHooked();
            if (cap)
            {
                C("2 hooked once asked", hookedAfter, "Map.MapUpdate patched by Photos " + hookedAfter);
            }

            // 3: running, photos in turn on the screenshots' frames.
            dts.Clear();
            for (int i = 0; i < CvTimeFrames; i++)
            {
                yield return 1;
                dts.Add(Time.unscaledDeltaTime * 1000f);
            }
            float msNoPhoto = Median(dts);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            List<float> photoFrames = new List<float>(), plainFrames = new List<float>();
            for (int r = 0; r < CvRunShots; r++)
            {
                CvShot(dir, "run_" + r);
                CvPose cp = poses[r % poses.Count];
                Texture2D got = null;
                if (cap)
                {
                    Api.CaptureView(map, cp.pose(), new CaptureViewOptions { Width = 640, Height = 360, FirstPersonPawn = cp.eyesOf }, t => got = t);
                }
                else if (mode == "slow")
                {
                    System.Threading.Thread.Sleep(CvSlowMs);   // a frame as slow as a photo's, no photo: what frame time alone does
                }
                for (int f = 0; f < CvRunEvery; f++)
                {
                    yield return 1;
                    (f <= 1 ? photoFrames : plainFrames).Add(Time.unscaledDeltaTime * 1000f);
                }
                if (cap)
                {
                    C("3 run " + r + " " + cp.name + " delivered", got != null, (got != null ? got.width + "x" + got.height : "null") + ", pass " + Photos.LastPassMs.ToString("0.00") + " ms, render " + Photos.LastBakeMs.ToString("0.00") + " ms, read " + Photos.LastReadMs.ToString("0.00") + " ms");
                    CvSave(got, dir, "run_" + r + "_photo_" + cp.name);
                    if (got != null)
                    {
                        UnityEngine.Object.Destroy(got);
                    }
                }
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            yield return 30;   // the screenshots are written in the background

            // Checks on the paused pictures.
            {
                // Within the three wall icons (opaque; the alerts elsewhere slide by real time): every frame as before the photo.
                string s = Path.Combine(dir, "screen_ui_");
                CvDiff noise = CvCompare(s + "0.png", s + "1.png", true);
                bool same = noise.ok && cvUiShots > 2;
                List<string> diffs = new List<string>();
                for (int u = 2; u < cvUiShots; u++)
                {
                    CvDiff d = CvCompare(s + "1.png", s + u + ".png", true);
                    same &= d.ok && d.pixels <= noise.pixels;
                    diffs.Add(d.ToString());
                }
                C("2u icons on the screen unchanged", same, "differing icon pixels: two plain frames " + noise + ", each frame after asking " + string.Join(", ", diffs));
            }
            foreach (CvPose cp in poses)
            {
                string s = Path.Combine(dir, "screen_" + cp.name + "_");
                CvDiff noise = CvCompare(s + "0.png", s + "1.png");
                CvDiff atPhoto = CvCompare(s + "1.png", s + "2.png");
                CvDiff after = CvCompare(s + "1.png", s + "3.png");
                CvDiff after2 = CvCompare(s + "1.png", s + "4.png");
                bool same = atPhoto.ok && after.ok && after2.ok && atPhoto.pixels <= noise.pixels && after.pixels <= noise.pixels && after2.pixels <= noise.pixels;
                C("2 " + cp.name + " screen unchanged", same, "differing pixels: two plain frames " + noise + ", the photo's frame " + atPhoto + ", the next " + after + ", the one after " + after2);
                if (!cap)
                {
                    continue;
                }
                if (cp.photo == null || cp.reference == null)
                {
                    C("2 " + cp.name + " pose", false, "photo " + (cp.photo != null) + ", reference " + (cp.reference != null));
                    continue;
                }
                float[] g = CvGridOf(cp.photo);
                float spread = CvSpread(g);
                C("2 " + cp.name + " not empty", spread >= CvMinSpread, "luminance spread " + spread.ToString("0.000"));
                float own = CvMae(g, CvGridOf(cp.reference));
                float other = mainRef != null ? CvMae(g, CvGridOf(mainRef)) : 1f;
                string otherName = "main view";
                foreach (CvPose o in poses)
                {
                    if (o != cp && o.reference != null)
                    {
                        float d = CvMae(g, CvGridOf(o.reference));
                        if (d < other)
                        {
                            other = d;
                            otherName = o.name;
                        }
                    }
                }
                C("2 " + cp.name + " from its pose", own <= CvOwnMax && own <= CvOwnShare * other,
                    "mean grid difference to its reference " + own.ToString("0.0000") + ", to the nearest other (" + otherName + ") " + other.ToString("0.0000"));
            }

            // What the draw hook costs a frame without a photo: its prefix (a flag test) timed on its own, times the draws of
            // the shown view's last frame (counted the same way on a photo's frame of the same scene, LastDraws, as an order).
            const int benchCalls = 1000000;
            int benchLayer = 0;
            MaterialPropertyBlock benchProps = null;
            Stopwatch bench = Stopwatch.StartNew();
            for (int i = 0; i < benchCalls; i++)
            {
                benchLayer = 0;
                Photos.DrawProps_Prefix(ref benchLayer, ref benchProps);
            }
            double nsPerDraw = bench.Elapsed.TotalMilliseconds * 1e6 / benchCalls;
            Log.Message("[IsoRim probe] " + L + "draw hook without a photo: " + nsPerDraw.ToString("0.0") + " ns a draw (" + benchCalls + " calls), about "
                + (nsPerDraw * Photos.LastDraws / 1e6).ToString("0.000") + " ms a frame at " + Photos.LastDraws + " draws");
            float photoMs = photoFrames.Count > 0 ? photoFrames.Average() : 0f;
            float plainMs = plainFrames.Count > 0 ? Median(plainFrames) : 0f;
            Log.Message("[IsoRim probe] " + L + "frame ms: before any photo (nothing hooked) median " + msUnused.ToString("0.00") + ", hooked without a photo median "
                + msNoPhoto.ToString("0.00") + "; running: photo frame and the next (avg) " + photoMs.ToString("0.00") + ", other frames median " + plainMs.ToString("0.00")
                + "; last photo pass " + Photos.LastPassMs.ToString("0.00") + " ms, read back " + Photos.LastReadMs.ToString("0.00") + " ms; photos taken " + Photos.Taken
                + ", delivered " + Photos.Delivered + ", failed " + Photos.Failed);
            if (!p.Destroyed)
            {
                p.Destroy(DestroyMode.Vanish);
            }
            Log.Message("[IsoRim probe] " + L.TrimEnd() + ": total " + pass + " PASS, " + fail + " FAIL");
        }

        // In front of the colonist's face, a little above, looking back at it.
        private static ViewPose CvSelfie(Pawn p)
        {
            ViewPose fp = ViewPose.FirstPersonOf(p);
            Vector3 face = fp.Eye.Value;
            Vector3 ahead = Quaternion.Euler(0f, p.Rotation.AsAngle, 0f) * Vector3.forward;
            return ViewPose.LookAt(face + ahead * 1.6f + Vector3.up * 0.35f, face - Vector3.up * 0.15f, 55f);
        }

        private static ViewPose CvLookDown(Vector3 at, float yaw)
        {
            Vector3 back = Quaternion.Euler(0f, yaw, 0f) * Vector3.back;
            return ViewPose.LookAt(at + back * 9f + Vector3.up * 7f, at, 50f);
        }

        private static void CvShot(string dir, string name)
        {
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png"));
        }

        private static void CvSave(Texture2D t, string dir, string name)
        {
            if (t != null)
            {
                File.WriteAllBytes(Path.Combine(dir, name + ".png"), t.EncodeToPNG());
            }
        }

        private struct CvDiff
        {
            public bool ok;
            public int pixels, max;

            public override string ToString()
            {
                return ok ? pixels + " (max " + max + ")" : "unreadable";
            }
        }

        // How many pixels of two screenshots differ, and by how much at most (a channel, 0-255).
        private static CvDiff CvCompare(string a, string b, bool iconsOnly = false)
        {
            CvDiff d = new CvDiff();
            if (!File.Exists(a) || !File.Exists(b))
            {
                return d;
            }
            Texture2D ta = new Texture2D(2, 2), tb = new Texture2D(2, 2);
            try
            {
                if (!ImageConversion.LoadImage(ta, File.ReadAllBytes(a), false) || !ImageConversion.LoadImage(tb, File.ReadAllBytes(b), false)
                    || ta.width != tb.width || ta.height != tb.height)
                {
                    return d;
                }
                Color32[] pa = ta.GetPixels32(), pb = tb.GetPixels32();
                float ui = Prefs.UIScale;
                for (int i = 0; i < pa.Length; i++)
                {
                    if (iconsOnly)
                    {
                        // (the screenshot's rows run bottom-up, the interface's top-down, in UI units; 4 units in from each edge)
                        float x = (i % ta.width) / ui, y = (ta.height - 1 - i / ta.width) / ui;
                        float inX = x - CvIconX - CvIconStep;
                        if (y < CvIconY + 4f || y > CvIconY + CvIconSize - 4f || inX < 0f || inX > 3f * CvIconStep
                            || inX % CvIconStep < 4f || inX % CvIconStep > CvIconSize - 4f)
                        {
                            continue;
                        }
                    }
                    int m = Math.Max(Math.Abs(pa[i].r - pb[i].r), Math.Max(Math.Abs(pa[i].g - pb[i].g), Math.Abs(pa[i].b - pb[i].b)));
                    if (m > 0)
                    {
                        d.pixels++;
                        d.max = Math.Max(d.max, m);
                    }
                }
                d.ok = true;
                return d;
            }
            finally
            {
                UnityEngine.Object.Destroy(ta);
                UnityEngine.Object.Destroy(tb);
            }
        }

        // Mean luminance (0-1) of each cell of a CvGrid x (CvGrid * 9 / 16) grid over the picture.
        private static float[] CvGridOf(Texture2D t)
        {
            int gw = CvGrid, gh = CvGrid * 9 / 16;
            float[] g = new float[gw * gh];
            int[] n = new int[gw * gh];
            Color32[] px = t.GetPixels32();
            for (int y = 0; y < t.height; y++)
            {
                int gy = y * gh / t.height;
                for (int x = 0; x < t.width; x++)
                {
                    Color32 c = px[y * t.width + x];
                    int i = gy * gw + x * gw / t.width;
                    g[i] += (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                    n[i]++;
                }
            }
            for (int i = 0; i < g.Length; i++)
            {
                g[i] /= Math.Max(1, n[i]);
            }
            return g;
        }

        private static float CvMae(float[] a, float[] b)
        {
            float s = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                s += Math.Abs(a[i] - b[i]);
            }
            return s / a.Length;
        }

        private static float CvSpread(float[] g)
        {
            float mean = g.Average();
            return Mathf.Sqrt(g.Select(v => (v - mean) * (v - mean)).Average());
        }
    }
}
