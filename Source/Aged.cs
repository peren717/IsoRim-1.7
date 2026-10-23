using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The aged look (ModelDef.aged, 0..1): a model drawn old and rusty, as the game's ancient ruins are drawn (the ancient
    // workbenches, rechargers and gestators are the player's buildings repainted duller, darker and flecked with rust,
    // their lights dead). Everything is done once, when the model is loaded (ModelLibrary.MakeEntry -> Aged.Copy), so an
    // aged model costs no more to draw than a plain one:
    //   - its colours (faces not tinted by the stuff) are dulled: toward their own grey, a touch olive, and darker;
    //   - the colour its "stuff" parts take is dulled the same way (R3DTextures.ModelTint), so stuff still reads;
    //     a thing made of no stuff and with none in its cost list takes old grey metal (OldMetal) on the Metal swatch;
    //   - its textures are swapped for rusty copies made at run time (Aged.Texture: the same texture, Tile x Tile cells
    //     of it in one, with rust flakes and grime laid over it; the model's texture coordinates are scaled to match), so
    //     the flakes are crisp at any zoom and do not repeat every cell;
    //   - its glowing faces are switched off, as a powered-down panel (ObjLoader.GlowOff).
    // The outline, the stuff swatch and the shader are untouched. Faces with no texture are only dulled.
    public static class Aged
    {
        public const int Tile = 4;             // cells of texture in one rusty copy (the noise does not repeat inside it)
        private const string Mark = "#aged";   // texture path suffix: <path>#aged<strength in hundredths>

        // The stuff colour of an aged thing made of no stuff whose sprite is drawn white (before Dull).
        public static readonly Color OldMetal = new Color(0.64f, 0.64f, 0.62f);

        // Harness only (probe "aged=x"): every aged def drawn at this strength instead of its own; < 0 = off.
        internal static float Override = -1f;

        public static float StrengthOf(ModelDef def)
        {
            if (def == null || def.aged <= 0f)
            {
                return 0f;
            }
            return Mathf.Clamp01(Override >= 0f ? Override : def.aged);
        }

        // A colour dulled for strength s: most of the way to its own grey (a touch olive, as the ancient sprites), darker.
        public static Color Dull(Color c, float s)
        {
            if (s <= 0f)
            {
                return c;
            }
            float lum = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
            Color grey = new Color(lum * 0.97f, lum, lum * 0.92f, c.a);
            Color o = Color.Lerp(c, grey, Mathf.Clamp01(1.2f * s));
            float k = 1f - 0.25f * s;
            return new Color(o.r * k, o.g * k, o.b * k, c.a);
        }

        // The aged copy of a model (its own id; the source is left as it is).
        public static Model Copy(Model src, float s)
        {
            if (src == null || s <= 0f || src.materials == null || src.material == null)
            {
                return src;
            }
            Model lit = ObjLoader.GlowOff(src);   // dead lights; the source itself if it has none
            int level = Mathf.Clamp(Mathf.RoundToInt(s * 100f), 1, 100);
            Model m = new Model();
            m.positions = lit.positions;
            m.normals = lit.normals;
            m.hull = lit.hull;
            m.noOutline = lit.noOutline;
            m.maxY = lit.maxY;
            m.halfDepth = lit.halfDepth;
            m.minX = lit.minX;
            m.maxX = lit.maxX;
            m.minZ = lit.minZ;
            m.maxZ = lit.maxZ;
            m.material = lit.material;
            m.tinted = lit.tinted;

            bool anyTexture = false;
            m.materials = new ModelMaterial[lit.materials.Length];
            for (int i = 0; i < lit.materials.Length; i++)
            {
                ModelMaterial o = lit.materials[i];
                bool aged = !o.glow;
                m.materials[i] = new ModelMaterial
                {
                    name = o.name, tinted = o.tinted, sheet = o.sheet, leaf = o.leaf, twig = o.twig, glow = o.glow, fire = o.fire,
                    color = aged && !o.tinted ? Dull(o.color, s) : o.color,   // stuff parts: their colour is dulled (ModelTint)
                    texture = o.texture != null && aged ? o.texture + Mark + level : o.texture
                };
                anyTexture |= o.texture != null;
            }
            m.colors = (Color[])lit.colors.Clone();
            for (int k = 0; k < m.colors.Length; k++)
            {
                if (!m.tinted[k] && !m.materials[m.material[k]].glow)
                {
                    m.colors[k] = Dull(m.colors[k], s);
                }
            }
            if (lit.uvs != null && anyTexture)
            {
                // One rusty copy holds Tile x Tile cells of the texture: the coordinates (in cells) shrink to match. The
                // glow swatch, not aged, is a flat colour, so its faces look the same.
                m.uvs = new Vector2[lit.uvs.Length];
                for (int k = 0; k < lit.uvs.Length; k++)
                {
                    m.uvs[k] = lit.uvs[k] / Tile;
                }
            }
            else
            {
                m.uvs = lit.uvs;
            }
            m.id = ObjLoader.NewId();
            return m;
        }

        // Every body model of an entry (variants, per-stuff, per-style, per-state, stack, empty, hacked, open) aged.
        // Glass, turret guns, spinning, bobbing and glowing parts keep their own look.
        public static void Apply(ModelEntry e, float s)
        {
            if (e == null || s <= 0f)
            {
                return;
            }
            e.variants = CopyAll(e.variants, s);
            e.stack = CopyAll(e.stack, s);
            e.empty = Copy(e.empty, s);
            e.hacked = Copy(e.hacked, s);
            e.open = Copy(e.open, s);
            if (e.byAppearance != null)
            {
                foreach (string k in new List<string>(e.byAppearance.Keys))
                {
                    e.byAppearance[k] = Copy(e.byAppearance[k], s);
                }
            }
            if (e.byState != null)
            {
                foreach (string k in new List<string>(e.byState.Keys))
                {
                    e.byState[k] = Copy(e.byState[k], s);
                }
            }
            if (e.byStyle != null)
            {
                foreach (string k in new List<string>(e.byStyle.Keys))
                {
                    e.byStyle[k] = CopyAll(e.byStyle[k], s);
                }
            }
        }

        private static Model[] CopyAll(Model[] ms, float s)
        {
            if (ms == null)
            {
                return null;
            }
            Model[] o = new Model[ms.Length];
            for (int i = 0; i < ms.Length; i++)
            {
                o[i] = Copy(ms[i], s);
            }
            return o;
        }

        // The colour and swatch the stuff parts of an aged thing take (ModelTint / ModelSwatch); s = 0: as given.
        public static float StrengthOf(Thing t)
        {
            ModelEntry entry;
            return t != null && t.def != null && ModelLibrary.TryGet(t.def, out entry) ? StrengthOf(entry.def) : 0f;
        }

        // ---- the rusty textures

        public static bool IsAgedPath(string path)
        {
            return path != null && path.IndexOf(Mark, StringComparison.Ordinal) > 0;
        }

        // The rusty copy for a path "<texture>#aged<level>" (R3DTextures.Get); null if the texture cannot be read.
        public static Texture2D Texture(string path)
        {
            int at = path.IndexOf(Mark, StringComparison.Ordinal);
            string basePath = path.Substring(0, at);
            int level;
            if (!int.TryParse(path.Substring(at + Mark.Length), out level))
            {
                level = 60;
            }
            Texture2D src = ContentFinder<Texture2D>.Get(basePath, false);
            int w = 0, h = 0;
            Color32[] from = src != null ? SpritePixels.Full(src, out w, out h) : null;
            if (from == null || w <= 0 || h <= 0)
            {
                ModLog.Warn("[IsoRim] Aged look: cannot read the texture " + basePath + "; drawn without rust.");
                return src;
            }
            return Build(from, w, h, level / 100f);
        }

        private static readonly Color RustLight = new Color(0.86f, 0.48f, 0.36f);
        private static readonly Color RustDark = new Color(0.62f, 0.33f, 0.25f);

        private static Texture2D Build(Color32[] from, int w, int h, float s)
        {
            int cell = Math.Min(Math.Min(w, h), 256);         // texture pixels per cell
            int n = cell * Tile;
            float[] f = new float[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = (float)x / n, v = (float)y / n;
                    // Flakes about a sixth of a cell across with ragged edges (periodic over the copy, so it tiles).
                    f[y * n + x] = 0.55f * Noise(u, v, 6 * Tile, 11) + 0.3f * Noise(u, v, 12 * Tile, 23) + 0.15f * Noise(u, v, 24 * Tile, 37);
                }
            }
            // The threshold that rusts this share of the surface.
            float cover = 0.03f + 0.2f * s;
            float[] sample = new float[(f.Length + 6) / 7];
            for (int i = 0, j = 0; i < f.Length && j < sample.Length; i += 7, j++)
            {
                sample[j] = f[i];
            }
            Array.Sort(sample);
            float thr = sample[Mathf.Clamp((int)((1f - cover) * sample.Length), 0, sample.Length - 1)];

            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    Color32 c0 = from[(y % h) * w + (x % w)];
                    Color c = c0;
                    float u = (float)x / n, v = (float)y / n;
                    float lum = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
                    // Grime: broad soft darker patches.
                    float grime = 1f - 0.22f * s * Smooth(0.42f, 0.75f, Noise(u, v, 2 * Tile, 51) * 0.7f + Noise(u, v, 6 * Tile, 53) * 0.3f);
                    c = new Color(c.r * grime, c.g * grime, c.b * grime, 1f);
                    float rust = Smooth(thr, thr + 0.012f, f[y * n + x]);
                    if (rust > 0f)
                    {
                        Color r = Color.Lerp(RustLight, RustDark, Noise(u, v, 12 * Tile, 71)) * (0.75f + 0.35f * lum);
                        c = Color.Lerp(c, r, rust);
                    }
                    c.a = 1f;
                    px[y * n + x] = c;
                }
            }
            Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, true, false);
            tex.name = "IsoRim_Aged";
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            Aniso.Own(tex);   // level 4, or the anisotropic filtering option's if higher
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        private static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        // Smooth value noise over (u, v) in 0..1 with period cells per side (tiles), 0..1.
        private static float Noise(float u, float v, int period, int seed)
        {
            float x = u * period, y = v * period;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float tx = x - x0, ty = y - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float a = Hash(x0, y0, period, seed), b = Hash(x0 + 1, y0, period, seed);
            float c = Hash(x0, y0 + 1, period, seed), d = Hash(x0 + 1, y0 + 1, period, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        private static float Hash(int x, int y, int period, int seed)
        {
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;
            unchecked
            {
                uint hsh = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
                hsh = (hsh ^ (hsh >> 13)) * 1274126177u;
                hsh ^= hsh >> 16;
                return (hsh & 0xFFFFFF) / 16777215f;
            }
        }
    }
}
