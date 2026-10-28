using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness only (probe key coloursweep=1): for every vanilla and DLC def drawn as a hand-made
    // model, the colour its faces resolve to (material colour x build-material tint x texture
    // mean, weighted by how much of each face the isometric camera sees) next to the mean colour
    // of the game's own sprite. Writes IsoRimColourSweep.tsv / .txt to the save data folder. Flags:
    //   STUFFLESS_WHITE  "stuff" parts on a def with no build material and a white sprite colour:
    //                    they take no colour at all (the model's tint is white)
    //   TEX_MISSING      a material names a texture that is not found
    //   BRIGHT / DARK    the model is much brighter / darker than the sprite
    //   HUE              the model's hue is clearly off the sprite's
    // Never runs in normal play (Probe.On needs dev mode, -isorim3d and a probe file).
    public static class ColourSweep
    {
        private static readonly Dictionary<string, Color> texMeans = new Dictionary<string, Color>();

        public static void Run()
        {
            StringBuilder tsv = new StringBuilder();
            tsv.Append("defName\tpack\tstuffable\tstuff\ttint\tswatch\tstuffArea\tmodel\tsprite\tlumRatio\tchromaDist\tmaterials\tflags\n");
            Dictionary<string, int> counts = new Dictionary<string, int>();
            List<string> flagged = new List<string>();
            int rows = 0;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs.OrderBy(d => d.defName))
            {
                if (def.modContentPack == null || !def.modContentPack.IsOfficialMod || def.IsBlueprint || def.IsFrame)
                {
                    continue;
                }
                ModelEntry entry;
                if (Forms.For(def).form != Form3D.Model || !ModelLibrary.TryGet(def, out entry) || !entry.HasModel)
                {
                    continue;
                }
                try
                {
                    string line = Row(def, entry, out List<string> flags);
                    rows++;
                    tsv.Append(line);
                    foreach (string f in flags)
                    {
                        counts[f] = (counts.TryGetValue(f, out int c) ? c : 0) + 1;
                    }
                    if (flags.Count > 0)
                    {
                        flagged.Add(line);
                    }
                }
                catch (Exception e)
                {
                    tsv.Append(def.defName).Append("\tERROR ").Append(e.GetType().Name).Append(": ").Append(e.Message.Replace('\n', ' ')).Append('\n');
                }
            }
            StringBuilder txt = new StringBuilder();
            txt.Append("IsoRim colour sweep, vanilla + DLC defs drawn as models: ").Append(rows).Append(" defs\n");
            txt.Append("flags: ").Append(string.Join(", ", counts.OrderBy(p => p.Key).Select(p => p.Key + " " + p.Value))).Append('\n');
            txt.Append("\nflagged rows:\n");
            foreach (string l in flagged)
            {
                txt.Append(l);
            }
            string dir = GenFilePaths.SaveDataFolderPath;
            File.WriteAllText(Path.Combine(dir, "IsoRimColourSweep.tsv"), tsv.ToString());
            File.WriteAllText(Path.Combine(dir, "IsoRimColourSweep.txt"), txt.ToString());
            Log.Message("[IsoRim probe] colour sweep: " + rows + " defs; flags " + string.Join(", ", counts.Select(p => p.Key + " " + p.Value)));
        }

        private static string Row(ThingDef def, ModelEntry entry, out List<string> flags)
        {
            flags = new List<string>();
            Model m = entry.variants[0];
            ThingDef stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
            Color tint = R3DTextures.TintFor(def, stuff);
            string swatch = R3DTextures.SwatchFor(def, stuff);
            if (entry.def.spriteTint)
            {
                tint = SpriteMean(def, stuff, false);
            }

            // The model: per material, area seen from the camera (tops fully, south faces most,
            // east/west faces a little) and resolved colour.
            Dictionary<int, float> area = new Dictionary<int, float>();
            Color sum = Color.black;
            float total = 0f;
            float stuffArea = 0f;
            bool missing = false;
            List<string> missingNames = new List<string>();
            bool textured = IsoRimMod.Settings.texturedModels && m.materials != null && m.uvs != null && m.material != null;
            for (int i = 0; i + 2 < m.positions.Length; i += 3)
            {
                Vector3 a = m.positions[i], b = m.positions[i + 1], c = m.positions[i + 2];
                Vector3 cr = Vector3.Cross(b - a, c - a);
                float ar = cr.magnitude * 0.5f;
                if (ar <= 0f)
                {
                    continue;
                }
                Vector3 n = m.normals != null ? m.normals[i].normalized : cr / (2f * ar);
                float w = ar * (Mathf.Abs(n.y) > 0.5f ? Mathf.Max(0f, n.y) : Mathf.Max(0f, -n.z) * 0.7f + Mathf.Abs(n.x) * 0.3f);
                if (w <= 0f)
                {
                    continue;
                }
                int mi = m.material != null ? m.material[i] : 0;
                ModelMaterial mm = m.materials != null ? m.materials[mi] : null;
                if (mm != null && mm.twig)
                {
                    continue;
                }
                Color col = m.colors[i];
                if (m.tinted[i])
                {
                    col *= tint;
                    stuffArea += w;
                }
                if (textured && mm != null && mm.texture != null)
                {
                    string path = mm.texture.Replace("{stuff}", swatch);
                    if (TexMean(path, out Color tm))
                    {
                        col = (m.OwnColour(i) ? m.colors[i] : mm.color) * (m.tinted[i] ? tint : Color.white) * tm * ModelBaker.TextureLift;   // as ModelBaker.Write
                    }
                    else
                    {
                        missing = true;
                        if (!missingNames.Contains(path))
                        {
                            missingNames.Add(path);
                        }
                    }
                }
                sum += col * w;
                total += w;
                area[mi] = (area.TryGetValue(mi, out float f) ? f : 0f) + w;
            }
            Color model = total > 0f ? sum / total : Color.black;
            Color sprite = SpriteMean(def, stuff, true);
            float lm = Lum(model), ls = Lum(sprite);
            float ratio = ls > 0.01f ? lm / ls : 0f;
            float chroma = ChromaDist(model, sprite);
            float sat = Sat(sprite);

            float stuffFrac = total > 0f ? stuffArea / total : 0f;
            bool whiteTint = tint.r > 0.97f && tint.g > 0.97f && tint.b > 0.97f;
            if (stuff == null && stuffFrac > 0.05f && whiteTint && !entry.def.spriteTint && !entry.def.paintedHull)
            {
                flags.Add("STUFFLESS_WHITE");
            }
            if (missing)
            {
                flags.Add("TEX_MISSING");
            }
            if (ratio > 1.7f)
            {
                flags.Add("BRIGHT");
            }
            else if (ratio > 0f && ratio < 0.5f)
            {
                flags.Add("DARK");
            }
            if (chroma > 0.07f && (sat > 0.15f || Sat(model) > 0.15f))
            {
                flags.Add("HUE");
            }

            StringBuilder mats = new StringBuilder();
            foreach (KeyValuePair<int, float> p in area.OrderByDescending(p => p.Value))
            {
                ModelMaterial mm = m.materials != null ? m.materials[p.Key] : null;
                if (mats.Length > 0)
                {
                    mats.Append(' ');
                }
                mats.Append(mm != null && mm.name.Length > 0 ? mm.name : "(none)").Append(':').Append((p.Value / total * 100f).ToString("0"));
            }
            if (missingNames.Count > 0)
            {
                mats.Append(" MISSING=").Append(string.Join(",", missingNames));
            }
            return def.defName + "\t" + def.modContentPack.PackageId + "\t" + (def.MadeFromStuff ? "1" : "0") + "\t" + (stuff != null ? stuff.defName : "")
                + "\t" + Hex(tint) + "\t" + swatch + "\t" + (stuffFrac * 100f).ToString("0") + "\t" + Hex(model) + "\t" + Hex(sprite)
                + "\t" + ratio.ToString("0.00") + "\t" + chroma.ToString("0.000") + "\t" + mats + "\t" + string.Join(",", flags) + "\n";
        }

        private static bool TexMean(string path, out Color mean)
        {
            if (texMeans.TryGetValue(path, out mean))
            {
                return mean.a > 0f;
            }
            Texture2D tex = R3DTextures.Get(path);
            mean = new Color(0f, 0f, 0f, 0f);
            if (tex != null)
            {
                Color32[] px = SpritePixels.Sample(tex, Vector2.one, Vector2.zero, 16) ?? SpritePixels.GpuSample(tex, 16);
                mean = Raw(px);
                mean.a = 1f;
            }
            texMeans[path] = mean;
            return mean.a > 0f;
        }

        // Mean colour of the solid pixels of the def's south-facing sprite, coloured as the game
        // draws it (by its build material if it has one); raw = without SpriteColors' lift.
        private static Color SpriteMean(ThingDef def, ThingDef stuff, bool raw)
        {
            Graphic g = def.graphic;
            if (g == null)
            {
                return Color.black;
            }
            Material mat = g.MatSingle;
            if (g is Graphic_Multi)
            {
                mat = g.MatSouth;
            }
            if (mat == null || mat.mainTexture == null)
            {
                return Color.black;
            }
            Color32[] px = SpritePixels.Sample(mat.mainTexture, Vector2.one, Vector2.zero, 16);
            if (px == null && !SpritePixels.IsSheet(mat.mainTexture))
            {
                px = SpritePixels.GpuSample(mat.mainTexture, 16);
            }
            Color c = raw ? Raw(px) : SpriteColors.MeanOf(px);
            Color tint = stuff != null ? def.GetColorForStuff(stuff) : (mat.HasProperty("_Color") ? mat.color : Color.white);
            c *= tint;
            c.a = 1f;
            return c;
        }

        private static Color Raw(Color32[] px)
        {
            float r = 0f, g = 0f, b = 0f;
            int n = 0;
            if (px != null)
            {
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].a >= 128)
                    {
                        r += px[i].r;
                        g += px[i].g;
                        b += px[i].b;
                        n++;
                    }
                }
            }
            return n > 0 ? new Color(r / n / 255f, g / n / 255f, b / n / 255f, 1f) : Color.black;
        }

        private static float Lum(Color c)
        {
            return 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
        }

        private static float Sat(Color c)
        {
            float hi = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), lo = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            return hi > 0.01f ? (hi - lo) / hi : 0f;
        }

        private static float ChromaDist(Color a, Color b)
        {
            float sa = a.r + a.g + a.b, sb = b.r + b.g + b.b;
            if (sa < 0.03f || sb < 0.03f)
            {
                return 0f;
            }
            float dr = a.r / sa - b.r / sb, dg = a.g / sa - b.g / sb, db = a.b / sa - b.b / sb;
            return Mathf.Sqrt(dr * dr + dg * dg + db * db);
        }

        private static string Hex(Color c)
        {
            Color32 k = c;
            return k.r.ToString("X2") + k.g.ToString("X2") + k.b.ToString("X2");
        }
    }
}
