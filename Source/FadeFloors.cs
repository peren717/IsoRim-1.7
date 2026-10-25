using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // While an addon asks for it (Api.PeekUpperFloors, Peek), the floors above the viewed one are drawn faded instead
    // of hidden, by screen-door transparency. They stay opaque, depth-written geometry; each of their fragments is dropped
    // where a 4 x 4 ordered dither pattern fixed on the screen is at or above the floor's kept share (Keep), so the viewed
    // floor shows through the gaps. No sorting, no blending.
    //
    // Which floors (Floors.Refresh): the viewed floor k + 1 keeps Keep[0] of its pixels, k + 2 Keep[1], ...; floors further
    // up stay hidden as before. The pointer never works on them (Floors.PointerFloorAt starts at the viewed floor).
    //
    // How a draw is faded: everything drawn for a faded floor passes Graphics with Floors.FadeSlot set (a block of that
    // floor drawn: DrawSection_Prefix; a thing on it drawn every frame: ThingPhase_Prefix) or with a world matrix in that
    // floor's rows (Floors.Fade_Prefix). There its material is swapped for a copy (For) whose _IsoFade is the kept share:
    // IsoRim's own fragment stages (IsoRimSolid, cards, fog lid) read _IsoFade; the game's Cutout, CutoutComplex and
    // terrain shaders get copies with the same drop (Shaders\IsoRimFade*.frag.glsl, defs IsoRimFade*). Materials of any
    // other shader are drawn unfaded. Copies follow their source (material version) and are kept per source and share.
    public static class FadeFloors
    {
        // Kept share of pixels for the floors 1, 2, ... above the viewed one (more entries: more floors drawn faded).
        public static float[] Keep = { 0.25f, 0.125f };

        // Where in the dither pattern (0-1, wrapping) each of those kept ranges starts. Where two faded floors overlap on the
        // screen, the upper covers the lower only in the pixels their ranges share: 0 and 0.1875 overlap by 1 of 16 (near alpha
        // blending: k + 1 shows 18.75%, k + 2 12.5%, the viewed floor 68.75%); 0 and 0 nest them (k + 2 steals all its pixels
        // from k + 1); 0 and 0.5 keep them apart (the viewed floor gets only what both leave).
        public static float[] From = { 0f, 0.1875f };

        // Set by an addon (Api.PeekUpperFloors, e.g. from its own hold key); IsoRim itself never sets it.
        public static bool Peek;

        // Held by the probe (peek=1), as if Peek were set.
        public static bool ProbeHeld;

        // Probe peeklog=1: the frame times around each press and release are logged.
        public static bool LogFlips;

        // Whether the upper floors are drawn faded this frame. Read once a frame (Floors.Refresh).
        public static bool Enabled
        {
            get
            {
                bool held = ProbeHeld || Peek;
                if (LogFlips)
                {
                    NoteFrame(held);
                }
                return held;
            }
        }

        // ---- probe timing: the frame time of the press (or release) frame and the two after it, against the median of
        // the 30 frames before.
        private static readonly float[] recent = new float[30];
        private static int recentN;
        private static bool lastHeld;
        private static int flipFrame = -1;
        private static float flipBase;
        private static readonly List<float> after = new List<float>();
        private static int lastFrame = -1;

        private static void NoteFrame(bool held)
        {
            if (Time.frameCount == lastFrame)
            {
                return;
            }
            lastFrame = Time.frameCount;
            float dt = Time.unscaledDeltaTime * 1000f;   // the time of the frame before this one
            if (flipFrame >= 0)
            {
                after.Add(dt);
                if (after.Count == 3)
                {
                    Log.Message("[IsoRim probe] peek " + (lastHeld ? "press" : "release") + " at frame " + flipFrame + ": frame ms "
                        + string.Join(", ", after.ConvertAll(x => x.ToString("0.00")).ToArray()) + " (before: median " + flipBase.ToString("0.00") + ")");
                    flipFrame = -1;
                    after.Clear();
                }
            }
            else
            {
                recent[recentN++ % recent.Length] = dt;
            }
            if (held != lastHeld && flipFrame < 0)
            {
                lastHeld = held;
                flipFrame = Time.frameCount;
                int n = Mathf.Min(recentN, recent.Length);
                List<float> s = new List<float>(n);
                for (int i = 0; i < n; i++)
                {
                    s.Add(recent[i]);
                }
                s.Sort();
                flipBase = n > 0 ? s[n / 2] : 0f;
                after.Clear();
            }
        }

        private static readonly int fadeId = Shader.PropertyToID("_IsoFade");
        private static readonly int fromId = Shader.PropertyToID("_IsoFadeFrom");

        private sealed class Copy
        {
            public Material mat;
            public int version = int.MinValue;
            public float keep = -1f;
            public float from = -1f;
        }

        private sealed class Entry
        {
            public Copy[] copies;
        }

        private static readonly ConditionalWeakTable<Material, Entry> copies = new ConditionalWeakTable<Material, Entry>();
        private static Dictionary<Shader, Shader> shaderMap;

        // Vanilla ShaderDefs with a faded twin, and IsoRim's own defs whose fragment stage reads _IsoFade itself.
        private static readonly string[][] Twins =
        {
            new[] { "Cutout", "IsoRimFadeCutout" },
            new[] { "CutoutComplex", "IsoRimFadeCutoutComplex" },
            new[] { "TerrainHard", "IsoRimFadeTerrainHard" },
            new[] { "TerrainFade", "IsoRimFadeTerrainFade" },
            new[] { "TerrainFadeRough", "IsoRimFadeTerrainFadeRough" },
        };

        private static readonly string[] Own =
        {
            "IsoRimSolid", "IsoRimSolidTex", "IsoRimOutline", "IsoRimGhost", "IsoRimBlend", "IsoRimLitMark", "IsoRimLitMarkTex",
            "IsoRimLitMarkCard", "IsoRimCardPlant", "IsoRimCardSnow", "IsoRimFog", "IsoRimFadeCutout", "IsoRimFadeCutoutComplex",
            "IsoRimFadeTerrainHard", "IsoRimFadeTerrainFade", "IsoRimFadeTerrainFadeRough", "IsoRimAdd",
        };

        private static void BuildMap()
        {
            shaderMap = new Dictionary<Shader, Shader>();
            foreach (string name in Own)
            {
                ShaderDef d = DefDatabase<ShaderDef>.GetNamedSilentFail(name);
                if (d != null && d.Shader != null)
                {
                    shaderMap[d.Shader] = d.Shader;
                }
            }
            foreach (string[] t in Twins)
            {
                ShaderDef from = DefDatabase<ShaderDef>.GetNamedSilentFail(t[0]);
                ShaderDef to = DefDatabase<ShaderDef>.GetNamedSilentFail(t[1]);
                if (from != null && to != null && from.Shader != null && to.Shader != null)
                {
                    shaderMap[from.Shader] = to.Shader;
                }
            }
        }

        private static float FromOf(int slot)
        {
            return slot - 1 < From.Length ? From[slot - 1] : 0f;
        }

        // The faded copy of a material for slot (1 = the floor right above the viewed one), or the material itself when its
        // shader has no faded form.
        public static Material For(Material src, int slot)
        {
            if (src == null || slot <= 0 || slot > Keep.Length || src.IsDeferredMaterial)
            {
                return src;
            }
            if (shaderMap == null)
            {
                BuildMap();
            }
            Shader s = src.shader;
            Shader to;
            if (s == null || !shaderMap.TryGetValue(s, out to))
            {
                return src;
            }
            Entry e = copies.GetValue(src, _ => new Entry { copies = new Copy[Keep.Length] });
            if (e.copies.Length < Keep.Length)
            {
                System.Array.Resize(ref e.copies, Keep.Length);
            }
            Copy c = e.copies[slot - 1];
            if (c == null)
            {
                c = e.copies[slot - 1] = new Copy { mat = new Material(to) { name = src.name + "_Fade" + slot } };
            }
            int v = src.engineMaterial.Version;
            if (c.version != v || c.keep != Keep[slot - 1] || c.from != FromOf(slot))
            {
                c.mat.CopyPropertiesFromMaterial(src);
                c.mat.renderQueue = src.renderQueue;
                c.mat.SetFloat(fadeId, Keep[slot - 1]);
                c.mat.SetFloat(fromId, FromOf(slot));
                c.version = v;
                c.keep = Keep[slot - 1];
                c.from = FromOf(slot);
            }
            return c.mat;
        }
    }
}
