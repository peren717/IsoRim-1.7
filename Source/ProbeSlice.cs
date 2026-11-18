using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only key (see Probe): the sliced rebuild of blocks coming into view (SectionSlicer) against an immediate
    // rebuild, in one seeded run. Needs -isorimslice=N on the command line (the slicer is off in harness runs without it).
    //   slicecheck=<tag>     with the game paused (the harness holds it): the camera near the map's centre and every
    //                        block built for the map as it is; one plant destroyed in every block outside the view (a
    //                        world change off screen, through the game's own change flags); the mouse wheel turned out
    //                        notch by notch through the game's input path until the zoom limit; then waits until the
    //                        slicer holds nothing back (SectionSlicer.Held) and takes two screenshots a little apart
    //                        (slice_<tag>_sliced0, slice_<tag>_sliced); every visible layer of every block in view is
    //                        hashed as it is, the block rebuilt at once by the game (Section.RegenerateAllLayers) and
    //                        hashed again: no layer may differ (the hard check). A third screenshot (slice_<tag>_atonce)
    //                        is compared with the second, pixel by pixel; the first two give the noise of what moves
    //                        on a paused map (flames, breathing animals), which the difference may not exceed by much.
    // Lines: "[IsoRim probe] CHECK PASS|FAIL slicecheck <tag>: ..." (layers compared and different, blocks changed, held
    // at most, rebuilt by the slicer, frames) and "[IsoRim probe] CHECK PASS|FAIL slicecheck <tag> images: ...".
    public static partial class Probe
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> sliceSections = AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");
        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> sliceLayers = AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");
        private static readonly FieldInfo sliceDesired = AccessTools.Field(typeof(CameraDriver), "desiredSize");

        private const float SliceNear = 12f;        // root size of the near view
        private const int SliceSettle = 20;         // frames the slicer must hold nothing before the check
        private const int SliceLimit = 4000;        // frames the catch-up may take before the check gives up
        private const int SliceShotWait = 600;      // frames a screenshot file may take to appear
        private const float SliceNoiseGap = 1.2f;   // real seconds between the two screenshots of the sliced view (about
                                                    // what the immediate rebuild of every block takes before the third)
        private const int SliceChannelNoise = 8;    // a channel difference up to this is not counted (the sun's glow drifts
                                                    // by a level or two when the harness moves the clock a day on)
        // The sliced view and the view rebuilt at once may differ in this many times the pixels two screenshots of the
        // same sliced view differ in, and in SliceNoiseFloor pixels in any case (of 1600 x 900: a twentieth of a percent).
        private const int SliceNoiseTimes = 4;
        private const int SliceNoiseFloor = 700;

        private static string sliceTag;
        private static int sliceStage, sliceWait, sliceFrames, sliceChanged, sliceHeldMax, sliceRebuilt0, sliceQuiet, sliceNotches;
        private static float slicePending;
        private static bool sliceHooked;
        private static string sliceShot0, sliceShotA, sliceShotB;
        private static float sliceShot0At;

        private static void SliceKey(string[] a)
        {
            sliceTag = a.Length > 0 && a[0].Length > 0 ? a[0] : "main";
            sliceStage = 1;
            sliceWait = 0;
            if (!sliceHooked)
            {
                sliceHooked = true;
                // Wheel notches are queued where the host's own callback queues them: right before its GUI input pass.
                MethodInfo dispatch = AccessTools.Method(AccessTools.TypeByName("RimWorldWaylith.Imgui.ImguiDispatch"), "DispatchInput");
                if (dispatch != null)
                {
                    new Harmony("peren.isorim.probe.slice").Patch(dispatch, prefix: new HarmonyMethod(typeof(Probe), nameof(SliceScrollFeed)));
                }
                else
                {
                    Log.Message("[IsoRim probe] CHECK FAIL slicecheck " + sliceTag + ": the host's input pass was not found, no wheel");
                }
            }
        }

        public static void SliceScrollFeed()
        {
            if (slicePending != 0f)
            {
                Vector2 centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                UnityEngine.InputSystem.InputSystem.QueueMouseMove(centre);
                UnityEngine.InputSystem.InputSystem.QueueMouseScroll(new Vector2(0f, slicePending), centre);
                slicePending = 0f;
            }
        }

        // Every frame (from MotionTick).
        private static void SliceTick()
        {
            if (sliceStage == 0)
            {
                return;
            }
            Map map = Find.CurrentMap;
            CameraDriver cam = Find.CameraDriver;
            if (map == null || cam == null)
            {
                return;
            }
            try
            {
                switch (sliceStage)
                {
                    case 1:   // near the centre; every block built for the map as it is
                        cam.SetRootPosAndSize(map.Center.ToVector3Shifted(), SliceNear);
                        if (sliceWait++ == 3)
                        {
                            map.mapDrawer.RegenerateEverythingNow();
                        }
                        if (sliceWait > 12)
                        {
                            sliceStage = 2;
                        }
                        break;
                    case 2:   // a world change in every block outside the view, through the game's own flags
                        sliceChanged = SliceChangeOffScreen(map, cam.CurrentViewRect.ExpandedBy(2));
                        sliceHeldMax = 0;
                        sliceRebuilt0 = SectionSlicer.Rebuilt;
                        sliceFrames = 0;
                        sliceNotches = 0;
                        sliceWait = 0;
                        sliceStage = 3;
                        break;
                    case 3:   // the wheel, a notch a frame, until the size asked for is the zoom limit
                        sliceFrames++;
                        sliceHeldMax = Math.Max(sliceHeldMax, SectionSlicer.Held);
                        if (sliceWait++ < 3)
                        {
                            break;   // (the flags reach the blocks first: Section.TryUpdate of the next frame)
                        }
                        if ((float)sliceDesired.GetValue(cam) >= cam.config.sizeRange.max - 0.001f)
                        {
                            sliceQuiet = 0;
                            sliceStage = 4;
                        }
                        else if (sliceNotches++ > 400)
                        {
                            SliceFail("the wheel did not zoom the camera out (size asked " + ((float)sliceDesired.GetValue(cam)).ToString("0.0") + ")");
                        }
                        else
                        {
                            slicePending = -1f;
                        }
                        break;
                    case 4:   // the camera has arrived and the slicer holds nothing, for a while
                        sliceFrames++;
                        sliceHeldMax = Math.Max(sliceHeldMax, SectionSlicer.Held);
                        bool arrived = cam.RootSize >= cam.config.sizeRange.max - 0.05f;
                        sliceQuiet = arrived && SectionSlicer.Held == 0 ? sliceQuiet + 1 : 0;
                        if (sliceQuiet >= SliceSettle)
                        {
                            sliceShot0 = SliceShot("sliced0");
                            sliceShot0At = Time.realtimeSinceStartup;
                            sliceShotA = null;
                            sliceWait = 0;
                            sliceStage = 5;
                        }
                        else if (sliceFrames > SliceLimit)
                        {
                            SliceFail("the slicer still held " + SectionSlicer.Held + " blocks after " + sliceFrames + " frames (camera size " + cam.RootSize.ToString("0.0") + ")");
                        }
                        break;
                    case 5:   // two screenshots of the sliced view on disk, a little apart; then the hard check
                        if (!File.Exists(sliceShot0) || (sliceShotA != null && !File.Exists(sliceShotA)))
                        {
                            if (sliceWait++ > SliceShotWait)
                            {
                                SliceFail("no screenshot " + (File.Exists(sliceShot0) ? sliceShotA : sliceShot0));
                            }
                            break;
                        }
                        if (sliceShotA == null)
                        {
                            if (Time.realtimeSinceStartup - sliceShot0At >= SliceNoiseGap)
                            {
                                sliceShotA = SliceShot("sliced");
                                sliceWait = 0;
                            }
                            break;
                        }
                        if (SectionSlicer.Held != 0)
                        {
                            SliceFail("the slicer held " + SectionSlicer.Held + " blocks again before the check");
                            break;
                        }
                        SliceCompare(map);
                        sliceWait = 0;
                        sliceStage = 6;
                        break;
                    case 6:
                        if (sliceWait++ == 5)
                        {
                            sliceShotB = SliceShot("atonce");
                        }
                        else if (sliceWait > 5 && File.Exists(sliceShotB))
                        {
                            sliceWait = 0;
                            sliceStage = 7;
                        }
                        else if (sliceWait > SliceShotWait)
                        {
                            SliceFail("no screenshot " + sliceShotB);
                        }
                        break;
                    case 7:
                        if (sliceWait++ < 3)
                        {
                            break;   // (the file is complete)
                        }
                        CvDiff noise = CvCompare(sliceShot0, sliceShotA, false, SliceChannelNoise);
                        CvDiff d = CvCompare(sliceShotA, sliceShotB, false, SliceChannelNoise);
                        bool same = noise.ok && d.ok && d.pixels <= Math.Max(SliceNoiseFloor, SliceNoiseTimes * noise.pixels);
                        Log.Message("[IsoRim probe] " + (same ? "CHECK PASS" : "CHECK FAIL") + " slicecheck " + sliceTag + " images: " + (noise.ok && d.ok
                            ? d.pixels + " pixels differ between the sliced view and the view rebuilt at once (largest channel difference " + d.max
                                + "); two screenshots of the sliced view " + SliceNoiseGap.ToString("0.0") + " s apart differ in " + noise.pixels + " (largest " + noise.max
                                + "): what moves on a paused map; differences up to " + SliceChannelNoise + " in a channel not counted"
                            : "the screenshots could not be compared"));
                        sliceStage = 0;
                        break;
                }
            }
            catch (Exception e)
            {
                SliceFail("an error: " + e);
            }
        }

        private static void SliceFail(string why)
        {
            sliceStage = 0;
            Log.Message("[IsoRim probe] CHECK FAIL slicecheck " + sliceTag + ": " + why);
        }

        private static string SliceShot(string what)
        {
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "slice_" + sliceTag + "_" + what + ".png");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            ScreenCapture.CaptureScreenshot(path);
            return path;
        }

        // One plant destroyed in every block that does not touch the view: the game's own path (Thing.Destroy, its despawn
        // sets the block's change flags). Returns the number of blocks changed.
        private static int SliceChangeOffScreen(Map map, CellRect view)
        {
            int n = 0;
            foreach (Section s in sliceSections(map.mapDrawer))
            {
                if (s == null || view.Overlaps(s.CellRect))
                {
                    continue;
                }
                Thing plant = null;
                foreach (IntVec3 c in s.CellRect)
                {
                    List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                    for (int i = 0; i < things.Count && plant == null; i++)
                    {
                        if (things[i] is Plant && things[i].Position == c)
                        {
                            plant = things[i];
                        }
                    }
                    if (plant != null)
                    {
                        break;
                    }
                }
                if (plant != null)
                {
                    plant.Destroy();
                    n++;
                }
            }
            return n;
        }

        // Every visible, non-dynamic layer of every block the game draws now: as it is, and after the game rebuilt the
        // block at once.
        private static void SliceCompare(Map map)
        {
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1).ClipInsideMap(map);
            int blocks = 0, layers = 0, different = 0, waiting = 0;
            List<string> which = new List<string>();
            List<ulong> before = new List<ulong>();
            foreach (Section s in sliceSections(map.mapDrawer))
            {
                Vector3 shift;
                if (s == null || !Cull.Drawn(ref view, s, out shift))
                {
                    continue;
                }
                blocks++;
                if (Cull.anyDirtyOf(s))
                {
                    waiting++;
                }
                List<SectionLayer> ls = sliceLayers(s);
                before.Clear();
                for (int i = 0; i < ls.Count; i++)
                {
                    before.Add(ls[i].Visible && !(ls[i] is SectionLayer_Dynamic) ? SliceHash(ls[i]) : 0UL);
                }
                s.RegenerateAllLayers();
                for (int i = 0; i < ls.Count; i++)
                {
                    if (!ls[i].Visible || ls[i] is SectionLayer_Dynamic)
                    {
                        continue;
                    }
                    layers++;
                    if (SliceHash(ls[i]) != before[i])
                    {
                        different++;
                        if (which.Count < 6)
                        {
                            which.Add(ls[i].GetType().Name + " at " + s.botLeft);
                        }
                    }
                }
            }
            int rebuilt = SectionSlicer.Rebuilt - sliceRebuilt0;
            bool ok = different == 0 && waiting == 0 && layers > 0 && sliceChanged > 0 && sliceHeldMax > 0 && rebuilt > 0;
            Log.Message("[IsoRim probe] " + (ok ? "CHECK PASS" : "CHECK FAIL") + " slicecheck " + sliceTag + ": " + layers + " layers of " + blocks
                + " blocks in view compared with an immediate rebuild, " + different + " different" + (which.Count > 0 ? " (" + string.Join(", ", which) + ")" : "")
                + "; blocks still waiting " + waiting + "; blocks changed off screen " + sliceChanged + "; held back at most " + sliceHeldMax
                + " a frame, rebuilt by the slicer " + rebuilt + "; " + sliceFrames + " frames from the change to the check, wheel notches " + sliceNotches
                + "; blocks a frame asked for " + (SliceTestArg.Blocks != null ? SliceTestArg.Blocks.Value.ToString() : "none (the slicer is off in a harness run)"));
        }

        // What a layer shows: every sub-mesh's material, state and lists.
        private static ulong SliceHash(SectionLayer layer)
        {
            ulong h = 1469598103934665603UL;
            List<LayerSubMesh> sms = layer.subMeshes;
            for (int i = 0; i < sms.Count; i++)
            {
                LayerSubMesh sm = sms[i];
                if (sm == null || sm.verts.Count == 0)
                {
                    continue;
                }
                h = Mix(h, (ulong)(sm.material != null ? sm.material.GetHashCode() : 0));
                h = Mix(h, (ulong)((sm.disabled ? 1 : 0) | (sm.finalized ? 2 : 0)));
                h = Mix(h, (ulong)sm.verts.Count);
                for (int k = 0; k < sm.verts.Count; k++)
                {
                    h = Mix3(h, sm.verts[k]);
                }
                h = Mix(h, (ulong)sm.tris.Count);
                for (int k = 0; k < sm.tris.Count; k++)
                {
                    h = Mix(h, (ulong)(uint)sm.tris[k]);
                }
                h = Mix(h, (ulong)sm.colors.Count);
                for (int k = 0; k < sm.colors.Count; k++)
                {
                    Color32 c = sm.colors[k];
                    h = Mix(h, (ulong)(c.r | c.g << 8 | c.b << 16 | c.a << 24));
                }
                h = Mix(h, (ulong)sm.uvs.Count);
                for (int k = 0; k < sm.uvs.Count; k++)
                {
                    h = Mix3(h, sm.uvs[k]);
                }
                h = Mix(h, (ulong)sm.uvsChannelTwo.Count);
                for (int k = 0; k < sm.uvsChannelTwo.Count; k++)
                {
                    h = Mix3(h, sm.uvsChannelTwo[k]);
                }
                h = Mix(h, (ulong)sm.normals.Count);
                for (int k = 0; k < sm.normals.Count; k++)
                {
                    h = Mix3(h, sm.normals[k]);
                }
            }
            return h;
        }

        private static ulong Mix(ulong h, ulong v)
        {
            return (h ^ v) * 1099511628211UL;
        }

        private static ulong Mix3(ulong h, Vector3 v)
        {
            h = Mix(h, (ulong)(uint)BitConverter.SingleToInt32Bits(v.x));
            h = Mix(h, (ulong)(uint)BitConverter.SingleToInt32Bits(v.y));
            return Mix(h, (ulong)(uint)BitConverter.SingleToInt32Bits(v.z));
        }
    }
}
