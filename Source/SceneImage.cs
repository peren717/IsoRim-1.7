using System;
using System.Collections.Generic;
using System.Reflection;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using W = Waylith.Rendering;

namespace IsoRim
{
    // A clean picture of the map view (Api 66, Api.RequestSceneImage): what the map pass drew, after all of IsoRim's screen
    // passes (AO, sun shadows, tilt-shift, anti-aliasing, the colour grade) and before any interface (IMGUI: the game's and
    // every mod's windows, labels, bars). Flat 2D maps too.
    //
    // How: on a frame a picture is wanted a full-screen pass that changes nothing (blend zero/one) is drawn on the ShaderDef
    // IsoRimShot, whose <sceneGrab> IsoRimShot sits at queue 4990, after the grade (4900) and everything else the map pass
    // draws (the game's highest map queue is 4000). A postfix on Waylith.Rendering.SceneGrabTargets.RecordGrab (render
    // thread, between two slices of the map pass) then blits the scene colour (B8G8R8A8; the blit swizzles the channels) into
    // our own R8G8B8A8 RenderTexture and leaves it shader-readable. The main thread reads it back (Texture2D.ReadPixels
    // through the engine's own staging copy) only once that frame has been submitted: a few frames later, never in the frame
    // it is drawn, then given the engine's final composite saturation (the sky's) so it matches the screen. One picture at a
    // time; requests made meanwhile wait for the next.
    //
    // Map overlays (selection brackets and rings, designations, status icons, the designator in hand with its drag box and
    // ghosts, targeting highlights, room stats, group move markers, IsoRim's drag boxes and mining x-ray) can be left out:
    // their draws are skipped for the frames round the one that is grabbed (two frames, so on screen they blink once).
    //
    // Cost when nobody asks: the RecordGrab postfix compares one name per grab; Frame returns on its first line; no grab is
    // drawn. Fail soft: a missing engine member or an exception logs once, sets Broken, and every request answers null.
    //
    // Engine members touched (Waylith 1.7.5000): SceneGrabTargets.RecordGrab(CommandBuffer, SceneGrabPoint, VkImage),
    // SceneGrabPoint.Name/Texture, Texture2D.GpuResource, GpuTextureAllocation.Image/Format/CurrentLayout/MarkWritten,
    // UnityEngine.RenderTexture.GpuTexture, Texture2D.ReadPixels.
    internal static class SceneImage
    {
        public const string GrabName = "IsoRimShot";
        public const string DefName = "IsoRimShot";

        public static volatile bool Broken;

        // Harness/diagnostics.
        public static int Copies;
        public static int Delivered;
        public static int Failed;
        public static int GrabDraws;
        public static string LastSceneFormat;

        private sealed class Asked
        {
            public Action<Texture2D> done;
            public bool overlays;
        }

        private enum Phase
        {
            Idle,
            Armed,
            Drawn
        }

        private static readonly List<Asked> waiting = new List<Asked>();
        private static readonly List<Asked> batch = new List<Asked>();
        private static Phase phase;
        private static int phaseFrame;
        private static int tries;

        // Main thread sets the target and asks (armed); the render thread answers (copied) and notes a size it could not fit.
        private static volatile int armed;
        private static volatile int copied;
        private static volatile W.GpuTextureAllocation target;
        private static volatile int targetW, targetH;
        private static volatile int wrongW, wrongH;

        private static RenderTexture rt;
        private static float drawnSaturation = 1f;   // the composite's saturation on the frame the grab was drawn
        private static Material material;
        private static Mesh quad;
        private static bool triedInit;

        // Read by the overlay prefixes (and View3D's own overlay draws): skip this frame.
        public static bool SkipOverlays;
        private static bool overlayPatched;

        public static RenderTexture Texture
        {
            get { return rt != null && copied > 0 ? rt : null; }
        }

        public static bool Busy
        {
            get { return phase != Phase.Idle || waiting.Count > 0; }
        }

        private static bool hooked;

        // At load (Patches.ApplyAll), before anything is drawn, so the render thread is never patched under its feet.
        public static void Hook()
        {
            if (hooked)
            {
                return;
            }
            hooked = true;
            try
            {
                MethodInfo m = AccessTools.Method(typeof(W.SceneGrabTargets), "RecordGrab",
                    new[] { typeof(CommandBuffer), typeof(W.SceneGrabPoint), typeof(VkImage) });
                if (m == null)
                {
                    Fail("an engine member is missing (RecordGrab)", null);
                    return;
                }
                new Harmony("peren.isorim.sceneimage").Patch(m, postfix: new HarmonyMethod(typeof(SceneImage), nameof(RecordGrabPostfix)));
            }
            catch (Exception e)
            {
                Fail("setting up failed", e);
            }
        }

        private static void Fail(string what, Exception e)
        {
            Broken = true;
            Log.Warning("[IsoRim] Scene images (Api.RequestSceneImage) are off for this session: " + what + (e != null ? ": " + e : "."));
        }

        public static bool Request(Action<Texture2D> done, bool overlays)
        {
            if (done == null)
            {
                throw new ArgumentNullException(nameof(done));
            }
            Hook();
            if (Broken || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
            {
                return false;
            }
            waiting.Add(new Asked { done = done, overlays = overlays });
            return true;
        }

        // A new game: open requests are answered with null.
        public static void Reset()
        {
            SkipOverlays = false;
            if (phase == Phase.Idle && waiting.Count == 0)
            {
                return;
            }
            phase = Phase.Idle;
            List<Asked> all = new List<Asked>(batch);
            all.AddRange(waiting);
            batch.Clear();
            waiting.Clear();
            Answer(all, null);
        }

        // Once a frame from View3D.TickInner, after IsoRim's screen passes are queued.
        public static void Frame()
        {
            if (phase == Phase.Idle && waiting.Count == 0)
            {
                return;
            }
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] A scene image failed: " + e, 93611660);
                SkipOverlays = false;
                phase = Phase.Idle;
                List<Asked> all = new List<Asked>(batch);
                batch.Clear();
                Answer(all, null);
            }
        }

        private static void Step()
        {
            int now = Time.frameCount;
            if (Broken)
            {
                SkipOverlays = false;
                phase = Phase.Idle;
                List<Asked> all = new List<Asked>(batch);
                all.AddRange(waiting);
                batch.Clear();
                waiting.Clear();
                Answer(all, null);
                return;
            }
            switch (phase)
            {
                case Phase.Idle:
                {
                    // The next batch: the oldest request and every waiting one with the same overlay choice.
                    bool overlays = waiting[0].overlays;
                    for (int i = 0; i < waiting.Count; i++)
                    {
                        if (waiting[i].overlays == overlays)
                        {
                            batch.Add(waiting[i]);
                            waiting.RemoveAt(i);
                            i--;
                        }
                    }
                    if (!Init() || !EnsureTarget(Screen.width, Screen.height))
                    {
                        phase = Phase.Idle;
                        List<Asked> b = new List<Asked>(batch);
                        batch.Clear();
                        Answer(b, null);
                        return;
                    }
                    if (!overlays)
                    {
                        PatchOverlays();
                        SkipOverlays = true;
                    }
                    tries = 0;
                    phase = Phase.Armed;
                    phaseFrame = now;
                    return;
                }
                case Phase.Armed:
                    // A frame after arming (the target made, the overlays already skipped whichever comes first this frame,
                    // the map update or the camera update), the grab is drawn.
                    if (now <= phaseFrame)
                    {
                        return;
                    }
                    if (!WorldRendererUtility.DrawingMap || Find.CurrentMap == null)
                    {
                        if (now - phaseFrame > 120)
                        {
                            Finish(null);
                        }
                        return;
                    }
                    armed = armed + 1;
                    Graphics.DrawMesh(quad, Matrix4x4.identity, material, 0);
                    GrabDraws++;
                    drawnSaturation = CompositeSaturation;
                    phase = Phase.Drawn;
                    phaseFrame = now;
                    return;
                case Phase.Drawn:
                    if (now == phaseFrame + 1 && batch.Count > 0 && !batch[0].overlays)
                    {
                        SkipOverlays = false;   // the grabbed frame's overlays are past
                    }
                    if (wrongW > 0)
                    {
                        // The scene is not the screen's size (being resized): a target of its size, and again.
                        int w = wrongW, h = wrongH;
                        wrongW = 0;
                        wrongH = 0;
                        if (++tries > 3 || !EnsureTarget(w, h))
                        {
                            Finish(null);
                            return;
                        }
                        SkipOverlays = batch.Count > 0 && !batch[0].overlays;
                        phase = Phase.Armed;
                        phaseFrame = now;
                        return;
                    }
                    if (copied == armed)
                    {
                        // Submitted: the frame drawn at phaseFrame was handed to the render thread at its end; two more
                        // frames' throttles (backlog at most one) mean it was recorded and submitted before this one.
                        if (now >= phaseFrame + 4)
                        {
                            Finish(Read());
                        }
                        return;
                    }
                    if (now - phaseFrame > 60)
                    {
                        // The grab never came (the map pass did not run that frame): try once more, then give up.
                        if (++tries > 2)
                        {
                            Finish(null);
                            return;
                        }
                        SkipOverlays = batch.Count > 0 && !batch[0].overlays;
                        phase = Phase.Armed;
                        phaseFrame = now;
                    }
                    return;
            }
        }

        private static void Finish(Texture2D image)
        {
            SkipOverlays = false;
            phase = Phase.Idle;
            List<Asked> b = new List<Asked>(batch);
            batch.Clear();
            Answer(b, image);
        }

        // Each caller gets its own texture (theirs to keep or destroy).
        private static void Answer(List<Asked> rs, Texture2D image)
        {
            Color32[] px = null;
            for (int i = 0; i < rs.Count; i++)
            {
                Texture2D mine = image;
                if (image != null && i > 0)
                {
                    if (px == null)
                    {
                        px = image.GetPixels32();
                    }
                    mine = new Texture2D(image.width, image.height, TextureFormat.RGBA32, false);
                    mine.SetPixels32(px);
                    mine.Apply(false);
                }
                if (mine != null)
                {
                    Delivered++;
                }
                else
                {
                    Failed++;
                }
                try
                {
                    rs[i].done(mine);
                }
                catch (Exception e)
                {
                    Log.Warning("[IsoRim] A scene image callback failed: " + e);
                }
            }
        }

        private static Texture2D Read()
        {
            return ReadOut(rt, "IsoRim_SceneImage", drawnSaturation);
        }

        // The saturation the engine's final composite gives the map view now (the sky's, from the game's ColorCorrectionCurves).
        internal static float CompositeSaturation
        {
            get { return Current.ColorCorrectionCurves != null ? Current.ColorCorrectionCurves.saturation : 1f; }
        }

        // A picture of the map view on the GPU (R8G8B8A8) read back into a new opaque RGBA32 texture as the screen gets it: the
        // engine's final saturation applied (sat: CompositeSaturation on the frame it was drawn). Scene images and photos (Api 76).
        internal static Texture2D ReadOut(RenderTexture from, string name, float sat)
        {
            int w = from.width, h = from.height;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.name = name;
            RenderTexture prev = RenderTexture.active;
            try
            {
                RenderTexture.active = from;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            }
            catch
            {
                UnityEngine.Object.Destroy(tex);   // not handed out: not left behind either
                throw;
            }
            finally
            {
                RenderTexture.active = prev;
            }
            // As the engine's final composite puts the map view on screen (PostFxComposite.frag.glsl: the sky's saturation, from
            // the game's ColorCorrectionCurves, round Unity's gamma-space luminance), so the picture looks as it did on screen.
            // The scene's alpha is working data of the screen passes (AO keeps its occlusion there): the picture is opaque.
            Color32[] px = tex.GetPixels32();
            bool tint = Mathf.Abs(sat - 1f) > 1e-4f;
            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                if (tint)
                {
                    float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                    float lum = r * 0.22f + g * 0.707f + b * 0.071f;
                    c.r = (byte)Mathf.RoundToInt(Mathf.Clamp01(lum + (r - lum) * sat) * 255f);
                    c.g = (byte)Mathf.RoundToInt(Mathf.Clamp01(lum + (g - lum) * sat) * 255f);
                    c.b = (byte)Mathf.RoundToInt(Mathf.Clamp01(lum + (b - lum) * sat) * 255f);
                }
                c.a = 255;
                px[i] = c;
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            return tex;
        }

        private static bool Init()
        {
            if (!triedInit)
            {
                triedInit = true;
                Shader shader = R3DMaterials.ShaderOf(DefName);
                if (shader == null)
                {
                    Fail("its shader " + DefName + " is missing", null);
                    return false;
                }
                material = new Material(shader);
                material.name = "IsoRim_SceneImage";
                quad = new Mesh();
                quad.name = "IsoRim_SceneImage";
                quad.vertices = new Vector3[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) };
                quad.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
                quad.RecalculateBounds();
                MeshGuard.Check(quad, "scene image quad");
            }
            return material != null && quad != null;
        }

        // Our RGBA8 target at this size, made on the main thread a frame before the grab is drawn.
        private static bool EnsureTarget(int w, int h)
        {
            if (w <= 0 || h <= 0)
            {
                return false;
            }
            if (rt == null || rt.width != w || rt.height != h)
            {
                target = null;
                if (rt != null)
                {
                    rt.Release();   // the engine retires the image once no frame in flight uses it
                }
                rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
                rt.name = "IsoRim_SceneImage";
                rt.Create();
            }
            W.GpuTextureAllocation g = rt.GpuTexture;
            if (g == null)
            {
                Fail("its texture could not be made", null);
                return false;
            }
            targetW = w;
            targetH = h;
            target = g;
            return true;
        }

        // ---------------------------------------------------------------- render thread

        public static void RecordGrabPostfix(CommandBuffer cmd, W.SceneGrabPoint point, VkImage sceneImage)
        {
            if (point == null || point.Name != GrabName || Broken)
            {
                return;
            }
            int want = armed;
            if (want == copied)
            {
                return;
            }
            try
            {
                W.GpuTextureAllocation dst = target;
                W.Texture2D grab = point.Texture;
                if (dst == null || grab == null)
                {
                    return;
                }
                int w = grab.width, h = grab.height;
                if (w != targetW || h != targetH)
                {
                    wrongW = w;
                    wrongH = h;
                    return;
                }
                VkFormat sceneFormat = grab.GpuResource != null ? grab.GpuResource.Format : VkFormat.Undefined;
                LastSceneFormat = sceneFormat.ToString();
                RecordBlit(cmd, sceneImage, dst, w, h);
                dst.CurrentLayout = VkImageLayout.ShaderReadOnlyOptimal;
                dst.MarkWritten();
                Copies++;
                copied = want;
            }
            catch (Exception e)
            {
                Fail("copying failed", e);
            }
        }

        // Scene colour (shader-readable between slices, as RecordGrab leaves it) -> our image, converted by the blit.
        private static void RecordBlit(CommandBuffer cmd, VkImage scene, W.GpuTextureAllocation dst, int w, int h)
        {
            VkImageSubresourceRange colour = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 };
            VkImageLayout dstOld = dst.CurrentLayout;
            VkImageMemoryBarrier[] pre = new VkImageMemoryBarrier[2]
            {
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
                    DstAccessMask = VkAccessFlags.TransferReadBit,
                    OldLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    NewLayout = VkImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = -1,
                    DstQueueFamilyIndex = -1,
                    Image = scene,
                    SubresourceRange = colour
                },
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = dstOld != VkImageLayout.Undefined ? (VkAccessFlags.ShaderReadBit | VkAccessFlags.TransferReadBit) : VkAccessFlags.None,
                    DstAccessMask = VkAccessFlags.TransferWriteBit,
                    OldLayout = dstOld,
                    NewLayout = VkImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = -1,
                    DstQueueFamilyIndex = -1,
                    Image = dst.Image,
                    SubresourceRange = colour
                }
            };
            cmd.PipelineBarrier(VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.TransferBit,
                VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>), default(ReadOnlySpan<VkBufferMemoryBarrier>), pre);
            VkImageSubresourceLayers layers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 };
            VkImageBlit blit = new VkImageBlit();
            blit.SrcSubresource = layers;
            blit.DstSubresource = layers;
            blit.SrcOffsets[0] = new VkOffset3D(0, 0, 0);
            blit.SrcOffsets[1] = new VkOffset3D(w, h, 1);
            blit.DstOffsets[0] = new VkOffset3D(0, 0, 0);
            blit.DstOffsets[1] = new VkOffset3D(w, h, 1);
            cmd.BlitImage(scene, VkImageLayout.TransferSrcOptimal, dst.Image, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageBlit>(new[] { blit }), VkFilter.Nearest);
            VkImageMemoryBarrier[] post = new VkImageMemoryBarrier[2]
            {
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.TransferReadBit,
                    DstAccessMask = VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit,
                    OldLayout = VkImageLayout.TransferSrcOptimal,
                    NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = -1,
                    DstQueueFamilyIndex = -1,
                    Image = scene,
                    SubresourceRange = colour
                },
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.TransferWriteBit,
                    DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.TransferReadBit,
                    OldLayout = VkImageLayout.TransferDstOptimal,
                    NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = -1,
                    DstQueueFamilyIndex = -1,
                    Image = dst.Image,
                    SubresourceRange = colour
                }
            };
            cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit,
                VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.TransferBit,
                VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>), default(ReadOnlySpan<VkBufferMemoryBarrier>), post);
        }

        // ---------------------------------------------------------------- map overlays

        // Patched the first time a picture without overlays is asked for (main thread; none of these run on another thread).
        private static void PatchOverlays()
        {
            if (overlayPatched)
            {
                return;
            }
            overlayPatched = true;
            Harmony h = new Harmony("peren.isorim.sceneimage.overlays");
            HarmonyMethod skip = new HarmonyMethod(typeof(SceneImage), nameof(SkipPrefix)) { priority = Priority.First };
            Try(h, AccessTools.Method(typeof(SelectionDrawer), nameof(SelectionDrawer.DrawSelectionOverlays)), skip);
            Try(h, AccessTools.Method(typeof(DesignationManager), nameof(DesignationManager.DrawDesignations)), skip);
            Try(h, AccessTools.Method(typeof(OverlayDrawer), nameof(OverlayDrawer.DrawAllOverlays)), skip);
            Try(h, AccessTools.Method(typeof(EnvironmentStatsDrawer), nameof(EnvironmentStatsDrawer.DrawRoomOverlays)), skip);
            Try(h, AccessTools.Method(typeof(DesignatorManager), nameof(DesignatorManager.DesignatorManagerUpdate)), skip);
            Try(h, AccessTools.Method(typeof(Targeter), nameof(Targeter.TargeterUpdate)), skip);
            Try(h, AccessTools.Method(typeof(MultiPawnGotoController), nameof(MultiPawnGotoController.Draw)), skip);
        }

        private static void Try(Harmony h, MethodInfo m, HarmonyMethod prefix)
        {
            if (m == null)
            {
                Log.Warning("[IsoRim] Scene images: an overlay draw was not found; it stays in pictures without overlays.");
                return;
            }
            try
            {
                h.Patch(m, prefix: prefix);
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Scene images: leaving out " + m.DeclaringType.Name + "." + m.Name + " failed: " + e.Message);
            }
        }

        public static bool SkipPrefix()
        {
            return !SkipOverlays;
        }
    }
}
