using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // One colour look for the 3D view.
    public class LookPreset
    {
        public string name;
        public string label;
        public string description;
        public float contrast;              // 0 to 1: deeper shadows and mid-tones
        public float desat;                 // 0 to 1: how much colour is taken out
        public float bright = 1f;           // scales the whole picture
        public Color lift = Color.black;    // a colour added to the shadows
        public Color gain = Color.white;    // a colour the highlights are multiplied by
        public float exposure;              // 0 to 0.5: brightens the whole picture
        public string lutFile;              // a LUT file (LUTs folder) used instead of the values above

        public LookPreset(string name, string label, string description)
        {
            this.name = name;
            this.label = label;
            this.description = description;
        }
    }

    // The looks. What a look does to a colour is in LookMath; Grade draws it.
    [StaticConstructorOnStartup]
    public static class Looks
    {
        public const string CustomName = "Custom";

        public static readonly List<LookPreset> Presets = new List<LookPreset>();

        static Looks()
        {
            Add("Gentle", "Gentle", "A little less colour and brightness. The mod's usual look.", desat: 0.15f, bright: 0.95f);
            Add("WarmAfternoon", "Warm afternoon", "Golden highlights and warm shadows.", desat: 0.06f, contrast: 0.2f,
                gain: new Color(1f, 0.95f, 0.85f), lift: new Color(0.03f, 0.015f, 0f));
            Add("CoolMorning", "Cool morning", "Fresh blue-green light, slightly muted.", desat: 0.12f,
                gain: new Color(0.90f, 0.96f, 1f), lift: new Color(0f, 0.012f, 0.035f));
            Add("Filmic", "Filmic", "Deeper shadows, softer colour and a faint warm cast, like film stock.", desat: 0.18f, contrast: 0.5f,
                gain: new Color(1f, 0.97f, 0.93f), lift: new Color(0.025f, 0.02f, 0.03f));
            Add("Bleak", "Bleak rim", "Washed-out, cold and hard: life on the edge.", desat: 0.45f, contrast: 0.25f, bright: 0.95f,
                gain: new Color(0.92f, 0.95f, 0.98f), lift: new Color(0.02f, 0.025f, 0.03f));
            Add("Vivid", "Vivid", "Punchier contrast and a touch brighter.", contrast: 0.45f, exposure: 0.06f);
            Add("Autumn", "Autumn", "Amber light and rich, earthy tones.", desat: 0.10f, contrast: 0.25f,
                gain: new Color(1f, 0.90f, 0.78f), lift: new Color(0.03f, 0.01f, 0f));
            Add("Moonlight", "Moonlight", "Cold blue night light over everything.", desat: 0.5f, bright: 0.9f,
                gain: new Color(0.72f, 0.82f, 1f), lift: new Color(0f, 0.01f, 0.04f));
            Add("Toxic", "Toxic", "A sickly green haze.", desat: 0.2f,
                gain: new Color(0.88f, 1f, 0.80f), lift: new Color(0.01f, 0.03f, 0f));
            Add("Sepia", "Sepia", "Old photograph browns.", desat: 0.85f,
                gain: new Color(1f, 0.88f, 0.70f), lift: new Color(0.04f, 0.025f, 0.01f), lutFile: "Sepia");
            Add("Noir", "Noir", "Black and white with hard contrast.", desat: 1f, contrast: 0.6f, lutFile: "Noir");
            Presets.Add(new LookPreset(CustomName, "Custom", "Set colour and brightness yourself with the two sliders below."));
        }

        private static void Add(string name, string label, string description, float desat = 0f, float contrast = 0f, float bright = 1f,
            float exposure = 0f, Color? gain = null, Color? lift = null, string lutFile = null)
        {
            LookPreset p = new LookPreset(name, label, description);
            p.desat = desat;
            p.contrast = contrast;
            p.bright = bright;
            p.exposure = exposure;
            p.gain = gain ?? Color.white;
            p.lift = lift ?? Color.black;
            p.lutFile = lutFile;
            Presets.Add(p);
        }

        public static LookPreset Find(string name)
        {
            for (int i = 0; i < Presets.Count; i++)
            {
                if (Presets[i].name == name)
                {
                    return Presets[i];
                }
            }
            return Presets[0];
        }

        // The look in force, with the strength slider and the custom sliders worked in.
        public static LookPreset Current()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            LookPreset p = Find(st.lookName);
            LookPreset r = new LookPreset(p.name, p.label, p.description);
            r.lutFile = p.lutFile;
            if (p.name == CustomName)
            {
                r.desat = 1f - Mathf.Clamp(st.saturation, 0.3f, 1f);
                r.bright = Mathf.Clamp(st.brightness, 0.3f, 1f);
                return r;
            }
            float k = Mathf.Clamp01(st.lookStrength);
            r.contrast = p.contrast * k;
            r.desat = p.desat * k;
            r.bright = Mathf.Lerp(1f, p.bright, k);
            r.exposure = p.exposure * k;
            r.gain = Color.Lerp(Color.white, p.gain, k);
            r.lift = Color.Lerp(Color.black, p.lift, k);
            return r;
        }

        // What a colour becomes under a look. Used for the previews in the options.
        public static Color Apply(LookPreset p, Color c)
        {
            return LookMath.Apply(p, c);
        }
    }

    // The colour grade over the 3D view: one full-screen pass after everything else in the map view
    // (ShaderDef IsoRimGrade, queue 4900). The engine copies the picture drawn so far, and the pass
    // looks every pixel up in a lookup table (LUT): one built here from the chosen look, or a LUT
    // file from the mod's LUTs folder picked by name, mixed in by the strength slider. The game's
    // interface is drawn later and is not affected.
    [StaticConstructorOnStartup]
    public static class Grade
    {
        public const string DefName = "IsoRimGrade";
        // Slices of the built tables. 64 keeps them within 1/255 of the looks' curves; 32 (as in the LUT
        // files) is up to 4/255 off where a curve bends sharply (the exposure clip of Vivid at 50%).
        public const int Size = 64;

        // Harness only (probe key ramp=1): a band of known colours is graded instead of the picture
        // at the top left of the screen. Drawn with the grade off too (then ungraded).
        public static bool TestPattern;
        public static int ExtraDraws;   // harness only (probe key gradegpu=N): the pass drawn N more times, to time it

        private static readonly int strengthId = Shader.PropertyToID("_Strength");
        private static readonly int lutId = Shader.PropertyToID("_LutTex");
        private static readonly int testId = Shader.PropertyToID("_TestPattern");

        // A material on a shader with the grade's uniforms (IsoRimGrade.glsl) set to the grade asked for now (a photo's
        // grade pass, Api 76). False: the grade changes nothing now.
        internal static bool SetUp(Material m)
        {
            Texture2D lut;
            float strength;
            if (!Current(out lut, out strength))
            {
                return false;
            }
            m.SetTexture(lutId, lut);
            m.SetFloat(strengthId, strength);
            m.SetFloat(testId, 0f);
            return true;
        }

        private static bool tried;
        private static Material material;
        private static Mesh quad;

        private static LiveTexture built;
        private static Color32[] builtPixels;
        private static string builtKey;
        private static int rebuilds;

        private static readonly Dictionary<string, Table> tables = new Dictionary<string, Table>();
        private static List<string> files;

        // A LUT file, read once.
        public class Table
        {
            public Texture2D texture;
            public Color32[] pixels;   // SetPixels32 order, for the preview
            public int size;
        }

        // Called every frame while the 3D view is on.
        public static void Draw(Camera cam)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if ((!st.grade && !TestPattern) || cam == null || !WorldRendererUtility.DrawingMap)
            {
                return;
            }
            if (!tried)
            {
                Init();
            }
            if (material == null)
            {
                return;
            }

            Texture2D lut;
            float strength;
            if (!Current(out lut, out strength))
            {
                if (!TestPattern)
                {
                    return;
                }
                lut = Built(Looks.Find("Gentle"));
                strength = 0f;
            }
            material.SetTexture(lutId, lut);
            material.SetFloat(strengthId, strength);
            material.SetFloat(testId, TestPattern ? 1f : 0f);
            Graphics.DrawMesh(quad, Matrix4x4.identity, material, 0, cam);
            for (int i = 0; i < ExtraDraws; i++)
            {
                Graphics.DrawMesh(quad, Matrix4x4.identity, material, 0, cam);
            }
        }

        // The grade the player's settings ask for now: its LUT strip and strength; false when the grade changes nothing (off,
        // no look, or no strength). The shown view's pass (Draw) and a photo's (Photos, Api 76) take it from here.
        internal static bool Current(out Texture2D lut, out float strength)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            lut = null;
            strength = 0f;
            if (st.grade)
            {
                Table file = FileFor(st);
                if (file != null)
                {
                    lut = file.texture;
                    strength = Mathf.Clamp01(st.lookStrength);
                }
                else
                {
                    LookPreset p = Looks.Current();
                    if (!LookMath.Identity(p))
                    {
                        lut = Built(p);
                        strength = 1f;   // the strength slider is already worked into the look
                    }
                }
            }
            return lut != null && strength > 0.001f;
        }

        private static void Init()
        {
            tried = true;
            Shader shader = R3DMaterials.ShaderOf(DefName);
            if (shader == null)
            {
                return;
            }
            material = new Material(shader);
            material.name = "IsoRim_Grade";
            // The whole screen, straight in clip space (the vertex shader ignores the camera).
            quad = new Mesh();
            quad.name = "IsoRim_Grade";
            quad.vertices = new Vector3[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) };
            quad.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            quad.RecalculateBounds();
            MeshGuard.Check(quad, "screen quad");
        }

        // The table of a look, rebuilt only when the look (with its sliders) changes.
        private static Texture2D Built(LookPreset p)
        {
            string key = Key(p);
            if (built != null && key == builtKey)
            {
                return built.Use();
            }
            Stopwatch watch = Stopwatch.StartNew();
            builtPixels = LookMath.Strip(p, Size, builtPixels);
            if (built == null)
            {
                built = new LiveTexture(Size * Size, Size, "IsoRim_GradeLut", FilterMode.Bilinear);
            }
            built.Upload(builtPixels);
            builtKey = key;
            rebuilds++;
            if (rebuilds <= 5)
            {
                ModLog.Note("[IsoRim] Grade table for " + p.name + " built in " + watch.Elapsed.TotalMilliseconds.ToString("0.000") + " ms");
            }
            return built.Use();
        }

        private static string Key(LookPreset p)
        {
            return string.Join(",", p.contrast, p.desat, p.bright, p.exposure, p.gain.r, p.gain.g, p.gain.b, p.lift.r, p.lift.g, p.lift.b);
        }

        // ------------------------------------------------------------ LUT files

        public static string Folder
        {
            get { return IsoRimMod.RootDir != null ? Path.Combine(IsoRimMod.RootDir, "LUTs") : null; }
        }

        // The names (without .png) of the LUT files in the LUTs folder.
        public static List<string> Files
        {
            get
            {
                if (files == null)
                {
                    files = new List<string>();
                    try
                    {
                        string dir = Folder;
                        if (dir != null && Directory.Exists(dir))
                        {
                            foreach (string f in Directory.GetFiles(dir, "*.png"))
                            {
                                files.Add(Path.GetFileNameWithoutExtension(f));
                            }
                            files.Sort();
                        }
                    }
                    catch (Exception e)
                    {
                        ModLog.Warn("[IsoRim] Could not list the LUT files: " + e.Message);
                    }
                }
                return files;
            }
        }

        public static void Rescan()
        {
            files = null;
        }

        // A LUT file by name, or null if it is missing or not a LUT strip.
        public static Table TableFor(string name)
        {
            Table t;
            if (tables.TryGetValue(name, out t))
            {
                return t;
            }
            t = null;
            try
            {
                string path = Folder != null ? Path.Combine(Folder, name + ".png") : null;
                if (path != null && File.Exists(path))
                {
                    Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                    if (ImageConversion.LoadImage(tex, File.ReadAllBytes(path), false) && tex.height >= 2 && tex.width == tex.height * tex.height)
                    {
                        tex.name = "IsoRim_Lut_" + name;
                        tex.filterMode = FilterMode.Bilinear;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.anisoLevel = 0;
                        t = new Table { texture = tex, pixels = tex.GetPixels32(), size = tex.height };
                    }
                    else
                    {
                        ModLog.Warn("[IsoRim] " + path + " is not a LUT strip (it must be N*N wide and N high, for example 1024 x 32).");
                        UnityEngine.Object.Destroy(tex);
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Could not read LUT " + name + ": " + e.Message);
                t = null;
            }
            tables[name] = t;
            return t;
        }

        // The LUT file in force: one picked by name, or the file of a look that uses one (Noir, Sepia).
        // Null when the look's own values apply (also when its file is missing).
        public static Table FileFor(IsoRimSettings st)
        {
            if (!st.lutFile.NullOrEmpty())
            {
                return TableFor(st.lutFile);
            }
            string own = Looks.Find(st.lookName).lutFile;
            return own.NullOrEmpty() ? null : TableFor(own);
        }

        // True for a LUT file that a look already uses (not listed again in the menu).
        public static bool UsedByLook(string file)
        {
            foreach (LookPreset p in Looks.Presets)
            {
                if (p.lutFile == file)
                {
                    return true;
                }
            }
            return false;
        }

        // What a colour becomes under a LUT file at a strength. Used for the previews.
        public static Color ApplyFile(Table t, float strength, Color c)
        {
            Color r = Color.Lerp(c, LookMath.Lookup(t.pixels, t.size, c), Mathf.Clamp01(strength));
            r.a = 1f;
            return r;
        }
    }
}
