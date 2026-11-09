using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The outline colour of a textured model face. The one rule for every writer of model outlines (ModelBaker.Write,
    // DynamicModels.Build): the outline is the colour the face is drawn in, times ModelBaker.OutlineShade.
    // - A plain face is drawn in its corner colour (times the tint where tinted): unchanged.
    // - A textured face is drawn as its texture times its material's colour (Kd * TextureLift, times the tint where the
    //   material is tinted or DynamicModels.TintTextured applies, times the sheet colour) times its corner colour (white, or
    //   the corner's own colour on Model.OwnBase: Model.OwnColour). Its outline takes the texture's mean colour in place of
    //   the texture, so a near-white Kd over a coloured texture (the blob animals) gives a dark, tinted outline like a plain
    //   face's, instead of a mid grey one.
    // The mean is alpha-weighted, over the part of the texture the material's faces use (their UV triangles, weighted by
    // their size on the model; an atlas often leaves most of its area unused or holds other materials' parts), read once
    // per model, material and texture from a small mip level (SpritePixels.Small). A texture whose pixels cannot be read
    // keeps the plain colour (as before).
    public static class TextureOutline
    {
        private const int MaxSide = 128;     // the mip level read: no larger than this
        private const int MaxSteps = 12;     // samples along a face's UV triangle side, at most

        private static readonly ConditionalWeakTable<Model, Dictionary<long, Color>> means = new ConditionalWeakTable<Model, Dictionary<long, Color>>();
        private static readonly Color None = new Color(0f, 0f, 0f, -1f);

        // Counters (harness probe).
        public static int Reads, Unreadable;

        // The base colour (before OutlineShade) of the outline of a corner of a textured face of material mi of model m,
        // drawn with tex: its corner colour (White, or the own-colour corner's colour) times the material's colour times the
        // texture's mean. plain: the corner's plain colour, kept if the texture cannot be read.
        public static Color Line(Model m, int mi, Texture tex, Color corner, Color material, Color plain)
        {
            Color mean = MeanOf(m, mi, tex);
            if (mean.a < 0f)
            {
                return plain;
            }
            Color c = corner * material * mean;
            return new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
        }

        // The mean colour of tex over the faces of material mi; alpha -1 if it cannot be read.
        public static Color MeanOf(Model m, int mi, Texture tex)
        {
            if (m == null || tex == null)
            {
                return None;
            }
            Dictionary<long, Color> byKey = means.GetOrCreateValue(m);
            long key = ((long)mi << 32) | (uint)tex.GetInstanceID();
            Color mean;
            if (byKey.TryGetValue(key, out mean))
            {
                return mean;
            }
            try
            {
                mean = Read(m, mi, tex);
            }
            catch (Exception e)
            {
                mean = None;
                Log.ErrorOnce("[IsoRim] Reading a model texture's colour failed: " + e, 93611620);
            }
            if (mean.a < 0f)
            {
                Unreadable++;
            }
            Reads++;
            byKey[key] = mean;
            return mean;
        }

        private static Color Read(Model m, int mi, Texture tex)
        {
            int w, h;
            Color32[] px = SpritePixels.Small(tex, MaxSide, out w, out h);
            if (px == null || w <= 0 || h <= 0 || px.Length < w * h)
            {
                return None;
            }

            // Samples spread over each face's UV triangle (wrapped: the textures tile), weighted by the face's area
            // on the model and the pixel's alpha.
            double r = 0, g = 0, b = 0, a = 0;
            int n = m.positions.Length;
            bool any = false;
            for (int i = 0; i + 2 < n; i += 3)
            {
                if ((m.material != null ? m.material[i] : 0) != mi || m.uvs == null || i + 2 >= m.uvs.Length)
                {
                    continue;
                }
                float area = Vector3.Cross(m.positions[i + 1] - m.positions[i], m.positions[i + 2] - m.positions[i]).magnitude;
                if (area <= 0f)
                {
                    continue;
                }
                Vector2 u0 = m.uvs[i], u1 = m.uvs[i + 1], u2 = m.uvs[i + 2];
                float span = Math.Max(Math.Max(Math.Abs(u1.x - u0.x), Math.Max(Math.Abs(u2.x - u0.x), Math.Abs(u2.x - u1.x))) * w,
                    Math.Max(Math.Abs(u1.y - u0.y), Math.Max(Math.Abs(u2.y - u0.y), Math.Abs(u2.y - u1.y))) * h);
                int steps = Mathf.Clamp(Mathf.CeilToInt(span), 1, MaxSteps);
                int count = 0;
                double fr = 0, fg = 0, fb = 0, fa = 0;
                for (int s = 0; s < steps; s++)
                {
                    for (int t = 0; s + t < steps; t++)
                    {
                        // the centres of a steps x steps split of the triangle (the upward cells)
                        float bs = (s + 1f / 3f) / steps;
                        float bt = (t + 1f / 3f) / steps;
                        Vector2 uv = u0 + (u1 - u0) * bs + (u2 - u0) * bt;
                        Color32 p = px[Wrap(uv.y, h) * w + Wrap(uv.x, w)];
                        double al = p.a / 255.0;
                        fr += p.r * al;
                        fg += p.g * al;
                        fb += p.b * al;
                        fa += al;
                        count++;
                    }
                }
                double weight = area / count;
                r += fr * weight;
                g += fg * weight;
                b += fb * weight;
                a += fa * weight;
                any = true;
            }
            if (!any || a <= 0.0)
            {
                return None;
            }
            return new Color((float)(r / a / 255.0), (float)(g / a / 255.0), (float)(b / a / 255.0), 1f);
        }

        private static int Wrap(float u, int size)
        {
            int i = (int)Math.Floor(u * size);
            i %= size;
            return i < 0 ? i + size : i;
        }
    }
}
