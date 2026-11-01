using System;
using System.Reflection;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using Verse;
using W = Waylith.Rendering;

namespace IsoRim
{
    // Readable scene depth for full-screen passes (internal, used by AmbientOcclusion and SunShadowsSS).
    //
    // Waylith has no depth grab: a ShaderDef <sceneGrab> copies colour only. This adds the depth half for one grab point
    // (GrabName). A postfix on Waylith.Rendering.SceneGrabTargets.RecordGrab (render thread, between two slices of the
    // map pass, so outside a render pass) finds the frame's scene target (RimWorldWaylith.Bootstrap._sceneTargets ->
    // SceneColorTargets.targets[], matched by its colour image) and copies the depth aspect of its depth image
    // (D32SFloatS8UInt, DepthStencilAttachmentOptimal between slices) into our own image of the same format:
    //   scene depth  attachment -> transfer src -> attachment,   our image  undefined -> transfer dst -> shader read.
    // Our image is bound through a depth-only view as the global texture _IsoSceneDepth (reverse-Z: 1 near, 0 far or
    // nothing drawn). It is recreated when the scene target changes size (window resize); the old one is retired by
    // the engine's deferred disposal once no frame in flight uses it.
    //
    // Cost: the patch is made at load (Hook, from Patches.ApplyAll, so never while the render thread runs RecordGrab);
    // its postfix only compares the grab point's name. The depth texture is made when something first asks (Request),
    // and the grab itself only happens on frames where the pass using it is drawn.
    // Fail soft: a missing engine member or any exception logs once and sets Broken; callers then draw nothing and the
    // game renders as without it. Nothing is recorded into the command buffer before every lookup has succeeded.
    //
    // Engine members touched (Waylith 1.7.4995): SceneGrabTargets.RecordGrab(CommandBuffer, SceneGrabPoint, VkImage),
    // SceneGrabPoint.Name, RimWorldWaylith.Bootstrap._sceneTargets (private static), SceneColorTargets.targets (private),
    // RenderTexture.device (private static), RenderTexture.Color/Depth/Width/Height/DepthFormat/IsCreated,
    // GpuTextureService.AllocateTextureImage, GpuTextureAllocation(..), Texture2D.GpuResource, GlobalShaderState.SetTexture.
    public static class DepthGrab
    {
        public const string GrabName = "IsoRimAO";

        // Set once anything failed: the feature is off for the rest of the session.
        public static volatile bool Broken;

        // Frames that got a fresh depth copy (harness checks).
        public static int Copies;
        public static int Recreated;
        public static int Reused;
        private static bool aoCopied;   // render thread: AO's grab copied the depth, the sun shadows' grab next reuses it

        // Harness only (probe key aofail=1): the next grab throws before recording anything (fail-soft test).
        public static volatile bool SimulateFailure;

        private static bool patched;
        private static W.Texture2D depthTex;
        private static W.GpuTextureAllocation copy;
        private static int copyW, copyH;
        private static FieldInfo sceneTargetsField;
        private static FieldInfo targetsField;
        private static FieldInfo deviceField;

        private static bool hooked;

        // At load (Patches.ApplyAll), before anything is drawn: finds the engine members and patches RecordGrab, so the
        // patch is never made while the render thread may be running it. The postfix does nothing until a grab named
        // GrabName comes (the AO pass, drawn only after Request made the depth texture).
        public static void Hook()
        {
            if (hooked)
            {
                return;
            }
            hooked = true;
            try
            {
                Type boot = AccessTools.TypeByName("RimWorldWaylith.Bootstrap");
                Type sct = AccessTools.TypeByName("RimWorldWaylith.Rendering.SceneColorTargets");
                sceneTargetsField = boot != null ? AccessTools.Field(boot, "_sceneTargets") : null;
                targetsField = sct != null ? AccessTools.Field(sct, "targets") : null;
                deviceField = AccessTools.Field(typeof(W.RenderTexture), "device");
                MethodInfo m = AccessTools.Method(typeof(W.SceneGrabTargets), "RecordGrab",
                    new[] { typeof(CommandBuffer), typeof(W.SceneGrabPoint), typeof(VkImage) });
                if (sceneTargetsField == null || targetsField == null || deviceField == null || m == null)
                {
                    Fail("an engine member is missing (boot " + (sceneTargetsField != null) + ", targets " + (targetsField != null)
                        + ", device " + (deviceField != null) + ", RecordGrab " + (m != null) + ")", null);
                    return;
                }
                new Harmony("peren.isorim.depthgrab").Patch(m, postfix: new HarmonyMethod(typeof(DepthGrab), nameof(RecordGrabPostfix)));
            }
            catch (Exception e)
            {
                Fail("setting up failed", e);
            }
        }

        // Main thread, every frame the depth is wanted: makes the depth texture on first use. False when unavailable.
        public static bool Request()
        {
            Hook();   // normally done at load
            if (Broken)
            {
                return false;
            }
            if (patched)
            {
                return true;
            }
            patched = true;
            try
            {
                depthTex = new W.Texture2D(1, 1) { Name = "IsoRim_SceneDepth", filterMode = W.FilterMode.Point, wrapMode = W.TextureWrapMode.Clamp };
                W.GlobalShaderState.SetTexture(W.ShaderPropertyId.GetOrCreate("_IsoSceneDepth"), depthTex);
                return true;
            }
            catch (Exception e)
            {
                Fail("setting up failed", e);
                return false;
            }
        }

        private static void Fail(string what, Exception e)
        {
            Broken = true;
            Log.Warning("[IsoRim] Ambient occlusion and sun shadows on walls are off for this session: reading the scene depth " + what + (e != null ? ": " + e : "."));
        }

        // Render thread.
        public static void RecordGrabPostfix(CommandBuffer cmd, W.SceneGrabPoint point, VkImage sceneImage)
        {
            if (Broken || depthTex == null || point == null || (point.Name != GrabName && point.Name != SunShadowsSS.GrabName))
            {
                return;
            }
            try
            {
                // AO's grab (2450) and the sun shadows' (2452) see the same depth (only full-screen passes without depth
                // writes run between them): when AO copied this frame, the sun shadows' grab reuses that copy.
                if (point.Name == GrabName)
                {
                    aoCopied = true;
                }
                else if (aoCopied)
                {
                    aoCopied = false;
                    Reused++;
                    return;
                }
                if (SimulateFailure)
                {
                    throw new InvalidOperationException("simulated failure (harness)");
                }
                W.RenderTexture scene = FindScene(sceneImage);
                if (scene == null || scene.Depth == null)
                {
                    Fail("found no scene depth for the grab", null);
                    return;
                }
                if (!EnsureCopy(scene.Width, scene.Height, scene.DepthFormat))
                {
                    return;
                }
                RecordCopy(cmd, scene.Depth.Image, copy.Image, scene.Width, scene.Height);
                copy.CurrentLayout = VkImageLayout.ShaderReadOnlyOptimal;
                copy.MarkWritten();
                if (depthTex.GpuResource != copy)
                {
                    depthTex.width = copyW;
                    depthTex.height = copyH;
                    depthTex.GpuResource = copy;
                }
                Copies++;
            }
            catch (Exception e)
            {
                Fail("copying failed", e);
            }
        }

        private static W.RenderTexture FindScene(VkImage sceneImage)
        {
            object st = sceneTargetsField.GetValue(null);
            if (st == null)
            {
                return null;
            }
            W.RenderTexture[] targets = targetsField.GetValue(st) as W.RenderTexture[];
            if (targets == null)
            {
                return null;
            }
            foreach (W.RenderTexture t in targets)
            {
                if (t != null && t.IsCreated && t.Color != null && t.Color.Image.Equals(sceneImage))
                {
                    return t;
                }
            }
            return null;
        }

        private static bool EnsureCopy(int w, int h, VkFormat format)
        {
            if (copy != null && copyW == w && copyH == h && copy.Format == format)
            {
                return true;
            }
            if (w <= 0 || h <= 0)
            {
                return false;
            }
            Waylith.Graphics.GraphicsDevice dev = deviceField.GetValue(null) as Waylith.Graphics.GraphicsDevice;
            if (dev == null)
            {
                Fail("found no graphics device", null);
                return false;
            }
            VkImageCreateInfo ci = new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D(w, h, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined
            };
            var alloc = W.GpuTextureService.AllocateTextureImage(dev.Allocator, "IsoRim_SceneDepthCopy", Waylith.Graphics.GpuMemoryCategory.RenderTarget, in ci);
            VkImageViewCreateInfo vi = new VkImageViewCreateInfo
            {
                Image = alloc.Image,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.DepthBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 }
            };
            VkImageView view = dev.Device.CreateImageView(in vi, dev.Device.VkAllocator);
            W.GpuTextureAllocation made = new W.GpuTextureAllocation(dev.Device, dev.Allocator, alloc, view, new IntPtr(0x150A0000 + (Recreated & 0xFFFF)), 1, format,
                "IsoRim_SceneDepthCopy", Waylith.Graphics.GpuMemoryCategory.RenderTarget);
            if (copy != null)
            {
                copy.Dispose();   // deferred by the engine until no frame in flight can use it
            }
            copy = made;
            copyW = w;
            copyH = h;
            Recreated++;
            return true;
        }

        private static void RecordCopy(CommandBuffer cmd, VkImage sceneDepth, VkImage dst, int w, int h)
        {
            VkImageSubresourceRange both = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.DepthBit | VkImageAspectFlags.StencilBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 };
            VkImageMemoryBarrier[] pre = new VkImageMemoryBarrier[2]
            {
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.DepthStencilAttachmentWriteBit,
                    DstAccessMask = VkAccessFlags.TransferReadBit,
                    OldLayout = VkImageLayout.DepthStencilAttachmentOptimal,
                    NewLayout = VkImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = -1,
                    DstQueueFamilyIndex = -1,
                    Image = sceneDepth,
                    SubresourceRange = both
                },
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.ShaderReadBit,
                    DstAccessMask = VkAccessFlags.TransferWriteBit,
                    OldLayout = VkImageLayout.Undefined,
                    NewLayout = VkImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = -1,
                    DstQueueFamilyIndex = -1,
                    Image = dst,
                    SubresourceRange = both
                }
            };
            cmd.PipelineBarrier(VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit | VkPipelineStageFlags.FragmentShaderBit,
                VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>), default(ReadOnlySpan<VkBufferMemoryBarrier>), pre);
            VkImageSubresourceLayers depthOnly = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.DepthBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 };
            cmd.CopyImage(sceneDepth, VkImageLayout.TransferSrcOptimal, dst, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageCopy>(new VkImageCopy
            {
                SrcSubresource = depthOnly,
                SrcOffset = new VkOffset3D(0, 0, 0),
                DstSubresource = depthOnly,
                DstOffset = new VkOffset3D(0, 0, 0),
                Extent = new VkExtent3D(w, h, 1)
            }));
            VkImageMemoryBarrier[] post = new VkImageMemoryBarrier[2]
            {
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.None,
                    DstAccessMask = VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
                    OldLayout = VkImageLayout.TransferSrcOptimal,
                    NewLayout = VkImageLayout.DepthStencilAttachmentOptimal,
                    SrcQueueFamilyIndex = -1,
                    DstQueueFamilyIndex = -1,
                    Image = sceneDepth,
                    SubresourceRange = both
                },
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.TransferWriteBit,
                    DstAccessMask = VkAccessFlags.ShaderReadBit,
                    OldLayout = VkImageLayout.TransferDstOptimal,
                    NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = -1,
                    DstQueueFamilyIndex = -1,
                    Image = dst,
                    SubresourceRange = both
                }
            };
            cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit,
                VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit | VkPipelineStageFlags.FragmentShaderBit,
                VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>), default(ReadOnlySpan<VkBufferMemoryBarrier>), post);
        }
    }
}
