using System;
using UnityEngine;
using Verse;
using GpuMemoryCategory = Waylith.Graphics.GpuMemoryCategory;
using GpuTextureAllocation = Waylith.Rendering.GpuTextureAllocation;
using GpuTextureService = Waylith.Rendering.GpuTextureService;
using LoadedTextureFormat = Waylith.Rendering.LoadedTextureFormat;
using TextureLevelUpload = Waylith.Rendering.TextureLevelUpload;
using TexturePixelData = Waylith.Graphics.Textures.TexturePixelData;

namespace IsoRim
{
    // A data texture the CPU rewrites while the game runs (the cell texture, the glow corners, the grade table).
    //
    // On 1.7 every Texture2D.Apply of changed pixels throws the GPU image away and makes a new one (Waylith
    // Texture2D.UploadLevels: dispose, CreateTexture2D, new image view). The cell texture did that 10-15 times a
    // second while the pointer moved. A LiveTexture is one Texture2D (the one the shaders are given, so no material
    // has to be bound again) with a ring of GPU images behind it, made once and pinned: an update copies the pixels
    // into the next image of the ring in place (GpuTextureService.UploadRgba32, same size and format) and points the
    // texture at it. After the ring is filled no image is made or freed.
    //
    // An image is only rewritten once the texture last showed it at least MinAge frames ago. Waylith keeps at most
    // FramesInFlight (2) frames queued for its render thread plus FramesInFlight on the GPU, and frees replaced images
    // 4 frames after the last submitted one, so no frame still reading it can be running. If that cannot be met (more
    // than one update a frame) a fresh image is made for that slot (the old one freed by the engine, as Apply does).
    // If anything in this path fails, the texture falls back to plain Apply.
    public sealed class LiveTexture
    {
        public const int Ring = 8;
        public const int MinAge = 6;
        public static int Created, InPlace, Fallbacks;   // counts over all live textures (crash report, harness)
        private static bool ringFailed;

        private readonly int width, height;
        private readonly string name;
        private readonly FilterMode filter;
        private Texture2D tex;
        private readonly GpuTextureAllocation[] ring = new GpuTextureAllocation[Ring];
        private readonly int[] lastUsed = new int[Ring];
        private int current = -1;

        public LiveTexture(int width, int height, string name, FilterMode filter)
        {
            this.width = width;
            this.height = height;
            this.name = name;
            this.filter = filter;
        }

        public bool Ready
        {
            get { return tex != null; }
        }

        // The texture to bind (null before the first Upload). Call it every frame the texture is bound.
        public Texture2D Use()
        {
            if (current >= 0)
            {
                lastUsed[current] = Time.frameCount;
            }
            return tex;
        }

        // Sends new pixels (SetPixels32 order, width x height).
        public void Upload(Color32[] pixels)
        {
            if (tex == null)
            {
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                tex.name = name;
                tex.filterMode = filter;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.SetPixels32(pixels);
                tex.Apply(false);
                Created++;
                if (!ringFailed)
                {
                    try
                    {
                        // The texture's own first image becomes the first of the ring.
                        GpuTextureAllocation first = tex.GpuTexture;
                        if (first != null)
                        {
                            first.PinInPlace();
                            ring[0] = first;
                            current = 0;
                            lastUsed[0] = Time.frameCount;
                        }
                    }
                    catch (Exception e)
                    {
                        Fail(e);
                    }
                }
                return;
            }
            tex.SetPixels32(pixels);   // the CPU copy, kept right (the engine reloads a texture from it if it has to)
            if (ringFailed || current < 0 || !Next())
            {
                tex.Apply(false);
                Fallbacks++;
            }
        }

        private bool Next()
        {
            try
            {
                TexturePixelData cpu = tex.CoreTexture?.CpuData;
                byte[] bytes = cpu != null && cpu.HasData ? cpu.Levels[0] : null;
                if (bytes == null || bytes.Length != width * height * 4 || cpu.Format != LoadedTextureFormat.Rgba32 || tex.GpuTexture != ring[current])
                {
                    current = -1;   // the engine replaced the texture's image: plain Apply from now on
                    return false;
                }
                int frame = Time.frameCount;
                int next = (current + 1) % Ring;
                GpuTextureAllocation g = ring[next];
                if (g != null && !g.IsDisposed && frame - lastUsed[next] >= MinAge)
                {
                    GpuTextureService.UploadRgba32(g, width, height, 0, bytes, batch: true);
                    InPlace++;
                }
                else
                {
                    if (g != null && !g.IsDisposed)
                    {
                        g.Dispose();   // maybe still read: the engine frees it once no frame can use it
                        Fallbacks++;
                    }
                    else
                    {
                        Created++;
                    }
                    TextureLevelUpload[] level = { TexturePixelData.BuildLevelUpload(0, width, height, 1, LoadedTextureFormat.Rgba32, bytes, 0) };
                    g = GpuTextureService.CreateTexture2D(name + "#" + next, width, height, 1, TexturePixelData.ToVkFormat(LoadedTextureFormat.Rgba32), level,
                        GpuMemoryCategory.GameTexture, null, batchUpload: true);
                    ring[next] = g;
                    if (g == null)
                    {
                        return false;
                    }
                    g.PinInPlace();
                }
                tex.CoreTexture.GpuResource = g;
                current = next;
                lastUsed[next] = frame;
                return true;
            }
            catch (Exception e)
            {
                Fail(e);
                return false;
            }
        }

        private void Fail(Exception e)
        {
            ringFailed = true;
            current = -1;
            Log.Warning("[IsoRim] Texture update in place turned off (remade instead): " + e.Message);
        }

        public void Destroy()
        {
            if (tex != null)
            {
                UnityEngine.Object.Destroy(tex);   // frees the image it shows
                tex = null;
            }
            for (int i = 0; i < Ring; i++)
            {
                if (ring[i] != null && !ring[i].IsDisposed)
                {
                    ring[i].Dispose();
                }
                ring[i] = null;
            }
            current = -1;
        }
    }
}
