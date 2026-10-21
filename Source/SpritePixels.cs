using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Small samples of a sprite's pixels, read on the CPU (Extruder outlines, SpriteColors).
    //
    // The 1.5 mod drew the sprite into a tiny off-screen image (Graphics.Blit with a scale and
    // offset for the sprite's part of a texture sheet) and read that back. In 1.7 Blit ignores the
    // scale and offset, and game textures keep (or can reload) their pixels on the CPU, so the
    // same sample is taken here directly: n x n points at the pixel centres of the part shown,
    // bilinear, from the mip level a Blit would pick (nearest to the size ratio), rows bottom
    // first as ReadPixels gave them. Colours are the texture's stored values (as 1.5 in gamma
    // space read them).
    //
    // The game's texture sheets (static atlases) are built on the GPU and have no CPU pixels, so
    // a part of a sheet is mapped back to the sprite texture it was packed from; that holds the
    // same pixels. Textures that must reload their pixels for this are let go again afterwards.
    public static class SpritePixels
    {
        private class Level
        {
            public Color32[] px;
            public int w, h;
            public float lastUse;
            public string info;
        }

        private struct Tile
        {
            public Rect uv;
            public Texture2D source;
        }

        private const long CacheCap = 32L * 1024 * 1024;   // bytes of decoded levels kept at most
        private const float IdleRelease = 10f;              // seconds without use before the levels are dropped

        private static readonly Dictionary<long, Level> levels = new Dictionary<long, Level>();
        private static long cachedBytes;
        private static float lastUse;

        private static readonly Dictionary<Texture, List<Tile>> sheets = new Dictionary<Texture, List<Tile>>();
        private static int sheetCount = -1;

        private static readonly AccessTools.FieldRef<List<StaticTextureAtlas>> atlasesRef =
            AccessTools.StaticFieldRefAccess<List<StaticTextureAtlas>>(AccessTools.Field(typeof(GlobalTextureAtlasManager), "staticTextureAtlases"));
        private static readonly AccessTools.FieldRef<StaticTextureAtlas, Dictionary<Texture, StaticTextureAtlasTile>> tilesRef =
            AccessTools.FieldRefAccess<StaticTextureAtlas, Dictionary<Texture, StaticTextureAtlasTile>>("tiles");

        // Counters (shown by the harness probe).
        public static int Samples, LevelReads, Reloaded, Unreadable, NoTile, Halved;
        public static string LastInfo = "";   // harness: format, CPU levels and mip of the last sample
        public static double SampleMs, ReadMs;
        public static long PeakBytes;

        // n x n colours of the part of tex shown at scale/offset (texture coordinates, as
        // Graphics.Blit took them), or null if the pixels cannot be read.
        public static Color32[] Sample(Texture tex, Vector2 scale, Vector2 offset, int n)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                Texture2D src = Resolve(tex, ref scale, ref offset);
                if (src == null)
                {
                    return null;
                }
                float ratio = Mathf.Max(Mathf.Abs(scale.x) * src.width, Mathf.Abs(scale.y) * src.height) / n;
                int mip = ratio <= 1f ? 0 : Mathf.Clamp((int)MathF.Round(MathF.Log2(ratio)), 0, Mathf.Max(0, src.mipmapCount - 1));
                Level lv = LevelOf(src, mip);
                LastInfo = lv != null ? lv.info : "";
                if (lv == null)
                {
                    return null;
                }
                Samples++;
                return Bilinear(lv, src.wrapMode == TextureWrapMode.Repeat, scale, offset, n);
            }
            finally
            {
                SampleMs += watch.Elapsed.TotalMilliseconds;
            }
        }

        // The sprite texture behind a part of a texture sheet, with scale/offset made relative to
        // it. Any other texture is returned as it is.
        public static Texture2D Resolve(Texture tex, ref Vector2 scale, ref Vector2 offset)
        {
            List<Tile> tiles = TilesOf(tex);
            if (tiles == null)
            {
                return tex as Texture2D;
            }
            Vector2 mid = offset + scale * 0.5f;
            for (int i = 0; i < tiles.Count; i++)
            {
                Rect r = tiles[i].uv;
                if (mid.x >= r.xMin && mid.x <= r.xMax && mid.y >= r.yMin && mid.y <= r.yMax && r.width > 0f && r.height > 0f)
                {
                    offset = new Vector2((offset.x - r.xMin) / r.width, (offset.y - r.yMin) / r.height);
                    scale = new Vector2(scale.x / r.width, scale.y / r.height);
                    return tiles[i].source;
                }
            }
            NoTile++;
            return null;
        }

        // All the pixels of a texture at full size (mip 0, rows bottom first, the texture's stored values), or null if they
        // cannot be read on the CPU (WallTops: a side texture cut from another mod's atlas). The array is shared: copy it.
        public static Color32[] Full(Texture tex, out int w, out int h)
        {
            w = 0;
            h = 0;
            Vector2 scale = Vector2.one;
            Vector2 offset = Vector2.zero;
            Texture2D src = Resolve(tex, ref scale, ref offset);
            if (src == null || scale != Vector2.one || offset != Vector2.zero)
            {
                return null;
            }
            Level lv = LevelOf(src, 0);
            if (lv == null)
            {
                return null;
            }
            w = lv.w;
            h = lv.h;
            return lv.px;
        }

        public static bool IsSheet(Texture tex)
        {
            return TilesOf(tex) != null;
        }

        private static List<Tile> TilesOf(Texture tex)
        {
            if (tex == null)
            {
                return null;
            }
            List<StaticTextureAtlas> atlases = atlasesRef();
            int count = atlases != null ? atlases.Count : 0;
            if (count != sheetCount)
            {
                sheets.Clear();
                sheetCount = count;
                for (int a = 0; a < count; a++)
                {
                    StaticTextureAtlas atlas = atlases[a];
                    Dictionary<Texture, StaticTextureAtlasTile> map = atlas != null ? tilesRef(atlas) : null;
                    if (map == null || atlas.ColorTexture == null)
                    {
                        continue;
                    }
                    List<Tile> list = new List<Tile>(map.Count);
                    foreach (KeyValuePair<Texture, StaticTextureAtlasTile> kv in map)
                    {
                        Texture2D source = kv.Key as Texture2D;
                        if (source != null && kv.Value != null)
                        {
                            list.Add(new Tile { uv = kv.Value.uvRect, source = source });
                        }
                    }
                    sheets[atlas.ColorTexture] = list;
                }
            }
            List<Tile> tiles;
            return sheets.TryGetValue(tex, out tiles) ? tiles : null;
        }

        // One mip level of a texture as colours, rows bottom first. Kept for a while: a sheet
        // part is often sampled several times (variants, mirrored), and so are stacks.
        private static Level LevelOf(Texture2D t, int mip)
        {
            long key = ((long)t.GetInstanceID() << 8) | (uint)mip;
            Level lv;
            float now = Time.realtimeSinceStartup;
            lastUse = now;
            if (levels.TryGetValue(key, out lv))
            {
                lv.lastUse = now;
                return lv;
            }

            Stopwatch watch = Stopwatch.StartNew();
            Waylith.Rendering.Texture2D core = t.CoreTexture;
            bool had = core != null && core.IsReadable;
            try
            {
                if (core == null || core.CpuData == null || (!had && core.SourcePath == null))
                {
                    Unreadable++;
                    return null;
                }
                Color32[] px = ReadLevel(t, core, mip);
                int w = Math.Max(1, t.width >> mip);
                int h = Math.Max(1, t.height >> mip);
                if (px == null || px.Length == 0)
                {
                    Unreadable++;
                    return null;
                }
                if (px.Length != w * h)
                {
                    // Fewer levels on the CPU than on the GPU: halve the largest one there is.
                    int lw = t.width;
                    int lh = t.height;
                    while (lw * lh != px.Length && (lw > 1 || lh > 1))
                    {
                        lw = Math.Max(1, lw >> 1);
                        lh = Math.Max(1, lh >> 1);
                    }
                    if (lw * lh != px.Length)
                    {
                        Unreadable++;
                        return null;
                    }
                    Halved++;
                    while (lw > w || lh > h)
                    {
                        px = Halve(px, lw, lh, out lw, out lh);
                    }
                }
                lv = new Level { px = px, w = w, h = h, lastUse = now, info = core.CpuData.Format + " levels " + core.CpuData.Levels.Length + "/" + t.mipmapCount + " mip " + mip };
            }
            catch (Exception)
            {
                Unreadable++;
                return null;
            }
            finally
            {
                if (!had && core != null && core.IsReadable)
                {
                    // Reloaded only for this: let the pixels go again, as the game had them.
                    core.CpuData.ReleaseTracked();
                    core.CpuData.Release();
                    Reloaded++;
                }
                ReadMs += watch.Elapsed.TotalMilliseconds;
            }

            LevelReads++;
            long bytes = (long)lv.px.Length * 4;
            if (cachedBytes + bytes > CacheCap)
            {
                Clear();
            }
            levels[key] = lv;
            cachedBytes += bytes;
            PeakBytes = Math.Max(PeakBytes, cachedBytes);
            return lv;
        }

        // A mip level as colours, rows bottom first. Compressed levels are decoded with the engine's
        // own block decoder and channel mapping (as the GPU samples them); the generic GetPixels32
        // decodes one-channel BC4 differently and leaves out the mapping.
        private static Color32[] ReadLevel(Texture2D t, Waylith.Rendering.Texture2D core, int mip)
        {
            Waylith.Rendering.TexturePixelData data = core.EnsureCpuData();
            if (data == null || !data.HasData)
            {
                return null;
            }
            Waylith.Rendering.LoadedTextureFormat f = data.Format;
            bool own = f == Waylith.Rendering.LoadedTextureFormat.Rgba32 || f == Waylith.Rendering.LoadedTextureFormat.Dxt1
                || f == Waylith.Rendering.LoadedTextureFormat.Bc7 || f == Waylith.Rendering.LoadedTextureFormat.Bc4 || f == Waylith.Rendering.LoadedTextureFormat.Bc5;
            if (!own || mip >= data.Levels.Length)
            {
                Color32[] g = t.GetPixels32(mip);
                if (g != null && !data.Swizzle.IsIdentity)
                {
                    data.Swizzle.ApplyInPlace(System.Runtime.InteropServices.MemoryMarshal.AsBytes(g.AsSpan()));
                }
                return g;
            }
            int w = Math.Max(1, data.Width >> mip);
            int h = Math.Max(1, data.Height >> mip);
            Waylith.Rendering.TexturePixelData one = new Waylith.Rendering.TexturePixelData(w, h, f, data.Linear, 1, false);
            one.Replace(w, h, f, new byte[][] { data.Levels[mip] }, 1, data.Linear);
            one.Swizzle = data.Swizzle;
            Span<Color32> top = System.Runtime.InteropServices.MemoryMarshal.Cast<Waylith.Math.Color32, Color32>(one.GetPixels32().AsSpan());   // rows top first, as the engine stores them
            Color32[] px = new Color32[top.Length];
            for (int y = 0; y < h; y++)
            {
                top.Slice((h - 1 - y) * w, w).CopyTo(px.AsSpan(y * w, w));
            }
            return px;
        }

        private static Color32[] Halve(Color32[] px, int w, int h, out int nw, out int nh)
        {
            nw = Math.Max(1, w >> 1);
            nh = Math.Max(1, h >> 1);
            Color32[] r = new Color32[nw * nh];
            for (int y = 0; y < nh; y++)
            {
                int y0 = Math.Min(h - 1, y * 2);
                int y1 = Math.Min(h - 1, y * 2 + 1);
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(w - 1, x * 2);
                    int x1 = Math.Min(w - 1, x * 2 + 1);
                    Color32 a = px[y0 * w + x0], b = px[y0 * w + x1], c = px[y1 * w + x0], d = px[y1 * w + x1];
                    r[y * nw + x] = new Color32((byte)((a.r + b.r + c.r + d.r + 2) >> 2), (byte)((a.g + b.g + c.g + d.g + 2) >> 2),
                        (byte)((a.b + b.b + c.b + d.b + 2) >> 2), (byte)((a.a + b.a + c.a + d.a + 2) >> 2));
                }
            }
            return r;
        }

        private static Color32[] Bilinear(Level lv, bool repeat, Vector2 scale, Vector2 offset, int n)
        {
            Color32[] r = new Color32[n * n];
            int w = lv.w;
            int h = lv.h;
            Color32[] px = lv.px;
            for (int j = 0; j < n; j++)
            {
                float v = offset.y + scale.y * ((j + 0.5f) / n);
                float ty = v * h - 0.5f;
                int y0 = Mathf.FloorToInt(ty);
                float fy = Mathf.Round((ty - y0) * 256f) / 256f;   // GPUs blend with 8-bit weights
                int ya = Wrap(y0, h, repeat);
                int yb = Wrap(y0 + 1, h, repeat);
                for (int i = 0; i < n; i++)
                {
                    float u = offset.x + scale.x * ((i + 0.5f) / n);
                    float tx = u * w - 0.5f;
                    int x0 = Mathf.FloorToInt(tx);
                    float fx = Mathf.Round((tx - x0) * 256f) / 256f;
                    int xa = Wrap(x0, w, repeat);
                    int xb = Wrap(x0 + 1, w, repeat);
                    Color32 c00 = px[ya * w + xa], c10 = px[ya * w + xb], c01 = px[yb * w + xa], c11 = px[yb * w + xb];
                    float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy), w01 = (1f - fx) * fy, w11 = fx * fy;
                    r[j * n + i] = new Color32(
                        Unorm(c00.r * w00 + c10.r * w10 + c01.r * w01 + c11.r * w11),
                        Unorm(c00.g * w00 + c10.g * w10 + c01.g * w01 + c11.g * w11),
                        Unorm(c00.b * w00 + c10.b * w10 + c01.b * w01 + c11.b * w11),
                        Unorm(c00.a * w00 + c10.a * w10 + c01.a * w01 + c11.a * w11));
                }
            }
            return r;
        }

        // A blended channel back to 8 bits. Exact halves round down, as the GPU's Blit does.
        private static byte Unorm(float x)
        {
            return (byte)Mathf.Clamp(Mathf.CeilToInt(x - 0.5f - 1e-4f), 0, 255);
        }

        private static int Wrap(int v, int size, bool repeat)
        {
            if (repeat)
            {
                v %= size;
                return v < 0 ? v + size : v;
            }
            return v < 0 ? 0 : (v >= size ? size - 1 : v);
        }

        // Called every frame: decoded levels are dropped once nothing has been sampled for a while
        // (after a map is built, or a block rebuilt).
        public static void Tick()
        {
            if (cachedBytes > 0 && Time.realtimeSinceStartup - lastUse > IdleRelease)
            {
                Clear();
            }
        }

        // The 1.5 way: the whole texture drawn into an n x n off-screen image and read back. Still
        // right in 1.7 for a whole texture (no scale or offset); used when the pixels cannot be read
        // on the CPU, and by the harness to check Sample.
        public static Color32[] GpuSample(Texture tex, int n)
        {
            RenderTexture target = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            Texture2D read = null;
            try
            {
                Graphics.Blit(tex, target);
                RenderTexture.active = target;
                read = new Texture2D(n, n, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0f, 0f, n, n), 0, 0);
                read.Apply();
                Color32[] px = read.GetPixels32();
                return px != null && px.Length >= n * n ? px : null;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (read != null)
                {
                    UnityEngine.Object.Destroy(read);
                }
            }
        }

        public static void Clear()
        {
            levels.Clear();
            cachedBytes = 0;
        }

        public static long CachedBytes
        {
            get { return cachedBytes; }
        }
    }
}
