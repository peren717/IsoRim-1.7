using System;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Tilt-shift (miniature look) for the 3D view, Graphics tab. Two grabbed full-screen passes (ShaderDefs
    // IsoRimTiltH 4880, IsoRimTiltV 4881): a separable Gaussian whose sigma depends on the screen row only, zero in
    // a horizontal band and growing smoothly above and below it. The second pass can also lift saturation and
    // contrast ("pop"). After night lighting and the fog lid, before FXAA (4890) and the grade (4900); the interface
    // is drawn later and is not blurred. Fades out as the camera gets shallow (a screen-row blur stops reading as
    // depth there) and is off in first person. Off, or faded out: nothing is drawn, so nothing is grabbed (no cost).
    [StaticConstructorOnStartup]
    public static class TiltShift
    {
        public const float MaxSigma = 8f;       // px at full strength, for 1080 lines (scaled by the screen height)
        public const float Ramp = 0.3f;         // band edge to full blur, fraction of the screen height
        public const float PitchNone = 12f;     // camera pitch (deg) at and below which the effect is gone
        public const float PitchFull = 30f;     // ... and at and above which it is full
        public const float PopAmount = 0.5f;    // "pop" strength (shader: up to +30 % saturation, +12 % contrast)

        public static int ExtraDraws;           // harness only (probe key tiltgpu=N): the passes drawn N more times
        public static bool Debug;               // harness only (probe key tiltdebug=1): tint by blur amount

        private static bool tried;
        private static Material h, v;
        private static Mesh quad;
        private static readonly int sigmaId = Shader.PropertyToID("_TiltSigma");
        private static readonly int centreId = Shader.PropertyToID("_TiltCentre");
        private static readonly int halfId = Shader.PropertyToID("_TiltHalf");
        private static readonly int rampId = Shader.PropertyToID("_TiltRamp");
        private static readonly int popId = Shader.PropertyToID("_TiltPop");
        private static readonly int debugId = Shader.PropertyToID("_TiltDebug");

        // 1 at a normal pitch, fading to 0 as the camera gets shallow; 0 in first person.
        public static float PitchFactor()
        {
            if (FirstPerson.On)
            {
                return 0f;
            }
            float t = Mathf.InverseLerp(PitchNone, PitchFull, View3D.Pitch);
            return t * t * (3f - 2f * t);
        }

        public static void Draw(Camera cam)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (!st.tiltShift || cam == null || !WorldRendererUtility.DrawingMap)
            {
                return;
            }
            float fade = PitchFactor();
            float sigma = Mathf.Clamp01(st.tiltStrength) * MaxSigma * fade;
            float pop = st.tiltPop ? PopAmount * fade : 0f;
            if (sigma < 0.05f && pop < 0.01f && !Debug)
            {
                return;
            }
            if (!tried)
            {
                Init();
            }
            if (h == null || v == null)
            {
                return;
            }
            try
            {
                float centre = Mathf.Clamp(st.tiltCentre, 0.3f, 0.7f);
                float half = 0.5f * Mathf.Clamp(st.tiltWidth, 0.1f, 0.6f);
                h.SetFloat(sigmaId, sigma);
                h.SetFloat(centreId, centre);
                h.SetFloat(halfId, half);
                h.SetFloat(rampId, Ramp);
                v.SetFloat(sigmaId, sigma);
                v.SetFloat(centreId, centre);
                v.SetFloat(halfId, half);
                v.SetFloat(rampId, Ramp);
                v.SetFloat(popId, pop);
                v.SetFloat(debugId, Debug ? 1f : 0f);
                for (int i = 0; i <= ExtraDraws; i++)
                {
                    Graphics.DrawMesh(quad, Matrix4x4.identity, h, 0, cam);
                    Graphics.DrawMesh(quad, Matrix4x4.identity, v, 0, cam);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the tilt-shift passes failed: " + e, 93611981);
            }
        }

        private static Material Make(string def)
        {
            Shader s = R3DMaterials.ShaderOf(def);
            if (s == null)
            {
                return null;
            }
            Material m = new Material(s);
            m.name = "IsoRim_" + def;
            return m;
        }

        private static void Init()
        {
            tried = true;
            h = Make("IsoRimTiltH");
            v = Make("IsoRimTiltV");
            quad = new Mesh();
            quad.name = "IsoRim_TiltShift";
            quad.vertices = new Vector3[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) };
            quad.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            quad.RecalculateBounds();
            MeshGuard.Check(quad, "tilt-shift quad");
        }
    }
}
