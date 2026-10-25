using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Beams in the 3D view (Api 33): light, not things. Drawn unlit with the additive IsoRimAdd material (IsoRimSolid.frag.glsl
    // in unlit mode; blend SrcAlpha One, no depth write, no culling, queue 3151 like the game's glowing motes, so after the
    // night and indoor light overlay): they only brighten what is behind them, have no outline, cast no shadow and are not
    // sorted as glass.
    //   - Line motes (MoteDualAttached) that FloorSprays draws as a 3D strip and that a ModelDef lists in beamMotes (opt-in;
    //     the game's own beams, MoteBeam grasers and lasers, keep the game's drawing, user 2026-10-25). One camera-facing strip:
    //     a white-ish core, the beam's colour, black (nothing, when added) at the edges; its colour is the mote's
    //     colour times the mean colour of its texture, its width the part of the drawn width the texture lights (both read
    //     once per def), faded in and out with the mote (and the game's beam flicker for MoteBeam).
    //   - Model parts: faces whose material name starts with "beam_" are taken out of the model when it is loaded
    //     (ObjLoader.Load: no hull or outline, no shadow, no glass, not the barrel end) into its beam part (Model.beam), drawn
    //     with the model in their MTL colour (Kd): baked with a static thing (ModelBaker.Write) or with a moving model
    //     (DynamicModels). ModelDef.beamFlicker (0-1) and beamPulse (Hz) make them flicker or pulse (one material per
    //     setting, its brightness set every frame). Switched off with the model's glowing parts (ObjLoader.GlowOff).
    // 3D off: nothing here runs; the game draws its motes itself.
    internal static class Beams
    {
        public const string AddDef = "IsoRimAdd";
        public const string Prefix = "beam_";

        // probes (Api.Dev): how often each kind was drawn or baked
        public static int MoteDraws, PartDraws, PartBaked;

        private const float Intensity = 1.1f;    // the strip's colours times this at full strength (added to what is behind)

        public static bool IsBeamMaterial(string name) => name != null && name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

        // ---- materials

        private static Material moteMat;
        private static readonly Dictionary<int, Material> partMats = new Dictionary<int, Material>();
        private static readonly List<(Material mat, float flicker, float pulse, int seed)> animated = new List<(Material, float, float, int)>();
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        public static Material MoteMaterial
        {
            get
            {
                if (moteMat == null)
                {
                    moteMat = R3DMaterials.Make(AddDef, "IsoRim_BeamMote");
                }
                return moteMat;
            }
        }

        // The material of a model's beam part: one per flicker / pulse setting (the steady one when both are 0).
        public static Material PartMaterial(Model beam)
        {
            float flicker = beam != null ? Mathf.Clamp01(beam.beamFlicker) : 0f;
            float pulse = beam != null ? Mathf.Max(0f, beam.beamPulse) : 0f;
            int key = Mathf.RoundToInt(flicker * 100f) * 100000 + Mathf.RoundToInt(pulse * 100f);
            if (!partMats.TryGetValue(key, out Material m) || m == null)
            {
                m = R3DMaterials.Make(AddDef, "IsoRim_BeamPart_" + key);
                partMats[key] = m;
                if (m != null && (flicker > 0f || pulse > 0f))
                {
                    animated.Add((m, flicker, pulse, partMats.Count * 7919));
                }
            }
            return m;
        }

        public static bool IsPartMaterial(Material m)
        {
            if (m == null)
            {
                return false;
            }
            foreach (Material p in partMats.Values)
            {
                if (ReferenceEquals(p, m))
                {
                    return true;
                }
            }
            return false;
        }

        // Called every frame while the 3D view is on (View3D): the flickering and pulsing beam parts' brightness now.
        public static void Update()
        {
            if (animated.Count == 0)
            {
                return;
            }
            float t = Find.TickManager != null ? Find.TickManager.TicksGame / 60f : Time.realtimeSinceStartup;
            for (int i = 0; i < animated.Count; i++)
            {
                var a = animated[i];
                if (a.mat == null)
                {
                    continue;
                }
                float k = 1f;
                if (a.pulse > 0f)
                {
                    k *= 0.6f + 0.4f * (0.5f + 0.5f * Mathf.Sin(t * a.pulse * 2f * Mathf.PI));
                }
                if (a.flicker > 0f)
                {
                    k *= 1f - a.flicker * Mathf.PerlinNoise(t * 9f, a.seed * 0.001f);
                }
                a.mat.color = new Color(k, k, k, 1f);
            }
        }

        // ---- line motes

        private sealed class MoteLook
        {
            public bool beam;
            public Color colour = Color.white;   // the texture's mean colour (brightest channel 1)
            public float widthShare = 0.35f;     // the part of the drawn width the texture lights
            public bool gameFlicker;             // the game's beam shader (MoteBeam) flickers its beams
            public bool read;
        }

        private static readonly Dictionary<ThingDef, MoteLook> looks = new Dictionary<ThingDef, MoteLook>();
        private static HashSet<string> listed;

        // Line motes listed by ModelDef.beamMotes (every ModelDef's list, case-insensitive).
        private static HashSet<string> Listed
        {
            get
            {
                if (listed == null)
                {
                    listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ModelDef md in DefDatabase<ModelDef>.AllDefsListForReading)
                    {
                        if (md.beamMotes != null)
                        {
                            foreach (string s in md.beamMotes)
                            {
                                if (!s.NullOrEmpty())
                                {
                                    listed.Add(s.Trim());
                                }
                            }
                        }
                    }
                }
                return listed;
            }
        }

        private static MoteLook LookOf(ThingDef def)
        {
            if (!looks.TryGetValue(def, out MoteLook l))
            {
                l = new MoteLook();
                ShaderTypeDef shader = def.graphicData?.shaderType;
                l.gameFlicker = shader != null && shader.defName == "MoteBeam";
                l.beam = Listed.Contains(def.defName);   // opt-in only: the game's own MoteBeam beams keep its drawing
                looks[def] = l;
            }
            return l;
        }

        // A line mote drawn as a beam in 3D: listed in a ModelDef's beamMotes.
        public static bool IsBeamMote(ThingDef def) => def != null && LookOf(def).beam;

        // The texture's colour (the mean of its coloured pixels, weighted by how much each adds: brightness times alpha times
        // saturation; the plain mean for a white beam), brightest channel 1, and the share of its width (across u, the strip's
        // width) whose columns light at least LitShare of the brightest column.
        private const float LitShare = 0.35f;

        private static void Read(MoteLook l, Mote mote)
        {
            l.read = true;
            string how = "default (no texture)";
            try
            {
                Texture tex = mote.Graphic?.MatSingle?.mainTexture;
                const int n = 64;
                Color32[] px = tex != null ? SpritePixels.Sample(tex, Vector2.one, Vector2.zero, n) : null;
                if (px == null || px.Length < n * n)
                {
                    how = tex != null ? "default (pixels unreadable)" : how;
                    return;
                }
                float[] column = new float[n];
                double r = 0, g = 0, b = 0, w = 0, sr = 0, sg = 0, sb = 0, sw = 0;
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        Color32 c = px[y * n + x];
                        int hi = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                        int lo = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                        float v = hi / 255f * (c.a / 255f);
                        column[x] += v;
                        r += c.r * v;
                        g += c.g * v;
                        b += c.b * v;
                        w += v;
                        float s = hi > 0 ? v * (hi - lo) / hi : 0f;
                        sr += c.r * s;
                        sg += c.g * s;
                        sb += c.b * s;
                        sw += s;
                    }
                }
                if (w > 0.0001)
                {
                    bool coloured = sw > 0.05 * w;
                    Color mean = coloured ? new Color((float)(sr / sw), (float)(sg / sw), (float)(sb / sw)) : new Color((float)(r / w), (float)(g / w), (float)(b / w));
                    float top = Mathf.Max(mean.r, Mathf.Max(mean.g, mean.b));
                    if (top > 0.01f)
                    {
                        l.colour = new Color(mean.r / top, mean.g / top, mean.b / top);
                    }
                }
                float peak = 0f;
                for (int x = 0; x < n; x++)
                {
                    peak = Mathf.Max(peak, column[x]);
                }
                if (peak > 0f)
                {
                    int lit = 0;
                    for (int x = 0; x < n; x++)
                    {
                        if (column[x] >= peak * LitShare)
                        {
                            lit++;
                        }
                    }
                    l.widthShare = Mathf.Clamp((float)lit / n, 0.04f, 1f);
                }
                how = "from its texture";
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading a beam's texture failed: " + e.Message, 93612201);
            }
            finally
            {
                ModLog.Note($"[IsoRim] beam look {mote.def.defName}: {how}, colour {l.colour}, width {l.widthShare:F3} of the drawn width");
            }
        }

        private static readonly Dictionary<int, Mesh> strips = new Dictionary<int, Mesh>();

        // The cross-section of a beam strip: at each distance from the middle (share of the half width), how bright the
        // colour is there and how much white is mixed in. A thin white-ish core, the colour round it, a soft glow out to
        // black (nothing, when added) at the edges.
        private static readonly float[] ProfileAt = { 0f, 0.14f, 0.40f, 1f };
        private static readonly float[] ProfileLevel = { 1f, 0.85f, 0.32f, 0f };
        private static readonly float[] ProfileWhite = { 0.7f, 0.15f, 0f, 0f };

        // The strip in one colour (rounded; one mesh each): x across (-0.5..0.5, as the game's plane), z along (-0.5..0.5), a
        // line of corners along it at each step of the profile on both sides.
        private static Mesh StripFor(Color colour)
        {
            Color32 k = colour;
            int key = (k.r & 0xF8) << 16 | (k.g & 0xF8) << 8 | (k.b & 0xF8);
            if (strips.TryGetValue(key, out Mesh m) && m != null)
            {
                return m;
            }
            int steps = ProfileAt.Length;
            int cols = steps * 2 - 1;
            float[] xs = new float[cols];
            Color32[] cs = new Color32[cols];
            for (int i = 0; i < cols; i++)
            {
                int p = Mathf.Abs(i - (steps - 1));
                xs[i] = 0.5f * ProfileAt[p] * (i < steps - 1 ? -1f : 1f);
                Color c = Color.Lerp(colour, Color.white, ProfileWhite[p]) * ProfileLevel[p];
                cs[i] = new Color(c.r, c.g, c.b, 1f);
            }
            Vector3[] v = new Vector3[cols * 2];
            Color32[] all = new Color32[cols * 2];
            for (int i = 0; i < cols; i++)
            {
                v[i] = new Vector3(xs[i], 0f, -0.5f);
                v[i + cols] = new Vector3(xs[i], 0f, 0.5f);
                all[i] = cs[i];
                all[i + cols] = cs[i];
            }
            int[] tris = new int[(cols - 1) * 6];
            for (int i = 0; i < cols - 1; i++)
            {
                tris[i * 6] = i;
                tris[i * 6 + 1] = i + cols;
                tris[i * 6 + 2] = i + cols + 1;
                tris[i * 6 + 3] = i;
                tris[i * 6 + 4] = i + cols + 1;
                tris[i * 6 + 5] = i + 1;
            }
            m = new Mesh();
            m.vertices = v;
            m.colors32 = all;
            m.triangles = tris;
            m.RecalculateBounds();
            MeshGuard.Check(m, "beam strip");
            strips[key] = m;
            return m;
        }

        private sealed class Last
        {
            public Material mat;
            public Vector3 from, to;
        }

        private static readonly ConditionalWeakTable<Thing, Last> lastDrawn = new ConditionalWeakTable<Thing, Last>();

        // Draws a line mote as a beam: m places the game's plane along the strip FloorSprays works out (column 0 its whole
        // drawn width, toward the camera); from / to are its ends (true space) for the probe. Queued where the game's own
        // mote would have been (Graphics.DrawMesh: the floor mapping and faded floors apply as to it).
        public static void DrawMote(Mote mote, Matrix4x4 m, Vector3 from, Vector3 to)
        {
            Material mat = MoteMaterial;
            if (mat == null)
            {
                return;
            }
            MoteLook l = LookOf(mote.def);
            if (!l.read)
            {
                Read(l, mote);
            }
            float alpha = mote.Alpha;
            if (alpha <= 0f)
            {
                return;
            }
            Color tint = mote.def.graphicData != null ? mote.def.graphicData.color : Color.white;
            tint *= mote.instanceColor;
            Color colour = new Color(l.colour.r * tint.r, l.colour.g * tint.g, l.colour.b * tint.b);
            float k = Intensity;
            if (l.gameFlicker)
            {
                k *= 1f + Mathf.Sin(mote.AgeSecs * 45f) * 0.15f;   // as MoteBeam.frag.glsl
            }
            m.SetColumn(0, m.GetColumn(0) * l.widthShare);
            block.Clear();
            block.SetColor(ShaderPropertyIDs.Color, new Color(k, k, k, Mathf.Clamp01(alpha * tint.a)));
            Graphics.DrawMesh(StripFor(colour), m, mat, 0, null, 0, block);
            MoteDraws++;
            Last last = lastDrawn.GetValue(mote, _ => new Last());
            last.mat = mat;
            last.from = from;
            last.to = to;
        }

        // (probe) The beam last drawn for this mote: its material and ends (true space, its first end first).
        public static bool LastOf(Thing mote, out Material mat, out Vector3 from, out Vector3 to)
        {
            mat = null;
            from = to = Vector3.zero;
            if (mote == null || !lastDrawn.TryGetValue(mote, out Last l))
            {
                return false;
            }
            mat = l.mat;
            from = l.from;
            to = l.to;
            return true;
        }

        // ---- model parts

        // Splits the faces of beam_* materials off a model being loaded (its lists, as ObjLoader.Load builds them) into a beam
        // part: positions, normals, colours and materials of those faces; the lists keep the rest. Null if there are none, or
        // if every face is a beam (then the model keeps them as faces).
        internal static Model Split(List<Vector3> pos, List<Vector3> normal, List<Color> colour, List<bool> tint, List<Vector2> uv, List<int> material,
            List<ModelMaterial> materials)
        {
            bool any = false;
            for (int i = 0; i < materials.Count; i++)
            {
                materials[i].beam = IsBeamMaterial(materials[i].name);
                any |= materials[i].beam;
            }
            if (!any)
            {
                return null;
            }
            int beams = 0;
            for (int i = 0; i + 2 < pos.Count; i += 3)
            {
                if (materials[material[i]].beam)
                {
                    beams += 3;
                }
            }
            if (beams == 0 || beams >= pos.Count)
            {
                return null;
            }
            List<Vector3> bp = new List<Vector3>(beams), bn = new List<Vector3>(beams);
            List<Color> bc = new List<Color>(beams);
            List<int> bm = new List<int>(beams);
            int keep = 0;
            for (int i = 0; i + 2 < pos.Count; i += 3)
            {
                bool beam = materials[material[i]].beam;
                for (int k = i; k < i + 3; k++)
                {
                    if (beam)
                    {
                        bp.Add(pos[k]);
                        bn.Add(normal[k]);
                        bc.Add(colour[k]);
                        bm.Add(material[k]);
                    }
                    else
                    {
                        pos[keep] = pos[k];
                        normal[keep] = normal[k];
                        colour[keep] = colour[k];
                        tint[keep] = tint[k];
                        uv[keep] = uv[k];
                        material[keep] = material[k];
                        keep++;
                    }
                }
            }
            int cut = pos.Count - keep;
            pos.RemoveRange(keep, cut);
            normal.RemoveRange(keep, cut);
            colour.RemoveRange(keep, cut);
            tint.RemoveRange(keep, cut);
            uv.RemoveRange(keep, cut);
            material.RemoveRange(keep, cut);
            Model b = new Model();
            b.positions = bp.ToArray();
            b.normals = bn.ToArray();
            b.colors = bc.ToArray();
            b.tinted = new bool[bp.Count];
            b.uvs = new Vector2[bp.Count];
            b.material = bm.ToArray();
            b.materials = materials.ToArray();
            b.id = ObjLoader.NewId();
            return b;
        }

        // The beam part moved and turned with its model (ObjLoader.Transformed, Mirrored): a new part of the moved corners.
        internal static Model Moved(Model beam, Func<Vector3, Vector3> point, Func<Vector3, Vector3> normal)
        {
            if (beam == null)
            {
                return null;
            }
            Model b = new Model();
            int n = beam.positions.Length;
            b.positions = new Vector3[n];
            b.normals = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                b.positions[i] = point(beam.positions[i]);
                b.normals[i] = normal(beam.normals[i]);
            }
            b.colors = beam.colors;
            b.tinted = beam.tinted;
            b.uvs = beam.uvs;
            b.material = beam.material;
            b.materials = beam.materials;
            b.beamFlicker = beam.beamFlicker;
            b.beamPulse = beam.beamPulse;
            b.id = ObjLoader.NewId();
            return b;
        }

        // The beam parts of several models merged into one (ObjLoader.Merge), or null.
        internal static Model Merged(List<Model> models)
        {
            List<Model> parts = null;
            for (int i = 0; i < models.Count; i++)
            {
                if (models[i]?.beam != null)
                {
                    (parts ??= new List<Model>()).Add(models[i].beam);
                }
            }
            if (parts == null)
            {
                return null;
            }
            if (parts.Count == 1)
            {
                return parts[0];
            }
            List<Vector3> p = new List<Vector3>(), nn = new List<Vector3>();
            List<Color> c = new List<Color>();
            List<int> mi = new List<int>();
            List<ModelMaterial> mats = new List<ModelMaterial>();
            foreach (Model b in parts)
            {
                int offset = mats.Count;
                mats.AddRange(b.materials);
                p.AddRange(b.positions);
                nn.AddRange(b.normals);
                c.AddRange(b.colors);
                foreach (int k in b.material)
                {
                    mi.Add(k + offset);
                }
            }
            Model m = new Model();
            m.positions = p.ToArray();
            m.normals = nn.ToArray();
            m.colors = c.ToArray();
            m.tinted = new bool[p.Count];
            m.uvs = new Vector2[p.Count];
            m.material = mi.ToArray();
            m.materials = mats.ToArray();
            m.beamFlicker = parts[0].beamFlicker;
            m.beamPulse = parts[0].beamPulse;
            m.id = ObjLoader.NewId();
            return m;
        }

        // ModelDef.beamFlicker / beamPulse onto the beam parts of every model of an entry (its Model fields, arrays and maps).
        private static FieldInfo[] entryFields;

        internal static void Tag(ModelEntry entry)
        {
            ModelDef md = entry?.def;
            if (md == null || (md.beamFlicker <= 0f && md.beamPulse <= 0f))
            {
                return;
            }
            if (entryFields == null)
            {
                entryFields = typeof(ModelEntry).GetFields(BindingFlags.Instance | BindingFlags.Public);
            }
            foreach (FieldInfo f in entryFields)
            {
                object v = f.GetValue(entry);
                if (v is Model one)
                {
                    TagOne(one, md);
                }
                else if (v is Model[] many)
                {
                    foreach (Model m in many)
                    {
                        TagOne(m, md);
                    }
                }
                else if (v is Dictionary<string, Model> map)
                {
                    foreach (Model m in map.Values)
                    {
                        TagOne(m, md);
                    }
                }
                else if (v is Dictionary<string, Model[]> maps)
                {
                    foreach (Model[] ms in maps.Values)
                    {
                        foreach (Model m in ms)
                        {
                            TagOne(m, md);
                        }
                    }
                }
            }
        }

        private static void TagOne(Model m, ModelDef md)
        {
            if (m?.beam != null)
            {
                m.beam.beamFlicker = Mathf.Clamp01(md.beamFlicker);
                m.beam.beamPulse = Mathf.Max(0f, md.beamPulse);
            }
        }

        // Writes a model's beam part into a section layer, placed as ModelBaker.Write places the model (the same turn, lay,
        // scale, offset and floor), in its MTL colours with the beam material.
        internal static void Bake(SectionLayer layer, Model beam, Vector3 center, Quaternion turn, Quaternion lay, Vector3 layLift, Vector3 scale,
            Vector3 offset, float baseY, float floor, float squash)
        {
            Material mat = PartMaterial(beam);
            LayerSubMesh sm = mat != null ? MeshWriter.Sub(layer, mat) : null;
            int n = beam.positions.Length;
            if (sm == null || sm.verts.Count + n > MeshWriter.MaxVerts)
            {
                return;
            }
            for (int k = 0; k < n; k++)
            {
                Vector3 p = turn * (Vector3.Scale(lay * beam.positions[k] + layLift, scale) + offset);
                sm.tris.Add(sm.verts.Count);
                sm.verts.Add(new Vector3(center.x + p.x, baseY + Mathf.Max(floor, p.y) / squash, center.z + p.z));
                sm.colors.Add((Color32)beam.colors[k]);
                sm.uvs.Add(default);
            }
            PartBaked += n / 3;
        }
    }

    public static partial class Api
    {
        public static partial class Dev
        {
            // (Api 33) Beam probes: line motes drawn as beams, moving beam parts drawn, beam faces baked into the map (counts
            // since start), and the beam parts' shader.
            public static int BeamMoteDraws => Beams.MoteDraws;

            public static int BeamPartDraws => Beams.PartDraws;

            public static int BeamPartBaked => Beams.PartBaked;

            public static UnityEngine.Shader BeamPartShader => Beams.PartMaterial(null)?.shader;

            // The beam IsoRim last drew for a line mote: its material and its two ends (true space: x, z map cells as drawn,
            // y the height; from = the mote's first end). False if it was never drawn as a beam.
            public static bool BeamDrawn(Verse.Thing mote, out UnityEngine.Material material, out UnityEngine.Vector3 from, out UnityEngine.Vector3 to)
            {
                return Beams.LastOf(mote, out material, out from, out to);
            }
        }
    }
}
