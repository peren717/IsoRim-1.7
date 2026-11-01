using System;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Screen-space ambient occlusion for the 3D view (Graphics options, default on). Two full-screen passes:
    // IsoRimAO (scene grab at queue 2450) reads the scene depth copied by DepthGrab at the same grab and works out the
    // occlusion of each pixel (creases: wall feet, inside corners, rock crevices, things against walls), kept in the
    // alpha channel; IsoRimAOBlur (grab at 2451) averages it depth-aware, applies strength and steps and darkens the colour.
    // Queue 2450: after the opaque 3D geometry (IsoRim walls, rock, models, pawns, trees, outlines: 2000/2001), the
    // terrain and IsoRim's ground shadows (2420); before every cut-out card and sprite (2900: plant cards, the game's
    // sprites, which would otherwise darken each other, grass most), see-through things (3000+), the night/indoor light
    // pass (3099/3100), the fog lid (3175), roofs (3600), tilt-shift, FXAA and the grade (4880+). So cards and sprites
    // are neither darkened nor darken, and lamps light the darkened colour as they light everything else.
    //
    // Ground: the game's terrain and floors write no depth. The shader puts the ground plane (height 0) under every
    // pixel analytically (ray against the plane, camera space): wherever the scene depth is behind the plane or empty,
    // the pixel is ground. The ground occludes wall feet and things standing on it, but by default receives no AO itself:
    // GroundShadows already darkens it along walls and under things (no double darkening, no seam).
    //
    // Off, unavailable (DepthGrab.Broken) or not drawing the map: nothing is drawn, so nothing is grabbed or copied.
    [StaticConstructorOnStartup]
    public static class AmbientOcclusion
    {
        public const float Radius = 0.55f;         // cells (camera space), the same at every zoom
        public const float GroundReceive = 0.0f;   // how much AO the ground itself takes (GroundShadows does it)
        public const float Gain = 4.0f;            // occlusion scale (horizon AO in 8 directions)
        public const float Bias = 0.1f;            // sine of the smallest elevation that counts (flat or slightly bent surfaces)

        public static int Debug;                   // harness only (probe key ao=...): 1 AO only, 2 ground/scene classes, 3 normals
        public static int ExtraDraws;              // harness only: the pass drawn N more times (GPU cost)
        public static float RadiusOverride = -1f;  // harness only
        public static float GroundOverride = -1f;  // harness only
        public static float GainOverride = -1f;    // harness only

        // Drawn this frame (SunShadowsSS then takes the darker of AO and its shadow, reading AO's factor from the alpha).
        public static bool DrawnThisFrame;

        private static bool tried;
        private static Material material, blur;
        private static Mesh quad;
        private static readonly int projId = Shader.PropertyToID("_AOProj");
        private static readonly int projBId = Shader.PropertyToID("_AOProjB");
        private static readonly int planeId = Shader.PropertyToID("_AOPlane");
        private static readonly int paramsId = Shader.PropertyToID("_AOParams");
        private static readonly int tuneId = Shader.PropertyToID("_AOTune");

        public static bool Available
        {
            get { return !DepthGrab.Broken; }
        }

        public static void Draw(Camera cam)
        {
            DrawnThisFrame = false;
            IsoRimSettings st = IsoRimMod.Settings;
            if (!st.ambientOcclusion || cam == null || !WorldRendererUtility.DrawingMap || DepthGrab.Broken)
            {
                return;
            }
            if (!tried)
            {
                Init();
            }
            if (material == null || blur == null || !DepthGrab.Request())
            {
                return;
            }
            try
            {
                Matrix4x4 p = cam.projectionMatrix;
                bool persp = View3D.Perspective;
                Vector4 proj = new Vector4(p.m00, p.m11, p.m22, p.m23);
                Vector4 projB = new Vector4(p.m03, p.m13, persp ? 1f : 0f, GroundOverride >= 0f ? GroundOverride : GroundReceive);

                // The ground plane (drawn space y = 0) in camera space: n.p + c = 0, n pointing up (away from the ground).
                Matrix4x4 v = cam.worldToCameraMatrix;
                Vector3 p0 = v.MultiplyPoint(Vector3.zero);
                Vector3 p1 = v.MultiplyPoint(new Vector3(0f, 0f, 1f));
                Vector3 p2 = v.MultiplyPoint(new Vector3(1f, 0f, 0f));
                Vector3 n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                Vector3 upC = v.MultiplyVector(Vector3.up);
                if (Vector3.Dot(n, upC) < 0f)
                {
                    n = -n;
                }
                Vector4 plane = new Vector4(n.x, n.y, n.z, -Vector3.Dot(n, p0));

                int steps = st.aoSteps == 2 || st.aoSteps == 3 ? st.aoSteps : 0;
                float radius = RadiusOverride > 0f ? RadiusOverride : Radius;
                Vector4 prm = new Vector4(Mathf.Clamp01(st.aoStrength), radius, steps, Debug);
                Vector4 tune = new Vector4(GainOverride > 0f ? GainOverride : Gain, Bias, 96f, SunShadowsSS.WillDraw ? 1f : 0f);
                for (int k = 0; k < 2; k++)
                {
                    Material m = k == 0 ? material : blur;
                    m.SetVector(projId, proj);
                    m.SetVector(projBId, projB);
                    m.SetVector(planeId, plane);
                    m.SetVector(paramsId, prm);
                    m.SetVector(tuneId, tune);
                }
                for (int i = 0; i <= ExtraDraws; i++)
                {
                    Graphics.DrawMesh(quad, Matrix4x4.identity, material, 0, cam);
                }
                Graphics.DrawMesh(quad, Matrix4x4.identity, blur, 0, cam);
                DrawnThisFrame = true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the ambient occlusion pass failed: " + e, 93611977);
            }
        }

        private static void Init()
        {
            tried = true;
            Shader shader = R3DMaterials.ShaderOf("IsoRimAO");
            if (shader == null)
            {
                return;
            }
            material = new Material(shader);
            material.name = "IsoRim_AO";
            Shader blurShader = R3DMaterials.ShaderOf("IsoRimAOBlur");
            if (blurShader == null)
            {
                material = null;
                return;
            }
            blur = new Material(blurShader);
            blur.name = "IsoRim_AOBlur";
            quad = new Mesh();
            quad.name = "IsoRim_AO";
            quad.vertices = new Vector3[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) };
            quad.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            quad.RecalculateBounds();
            MeshGuard.Check(quad, "ambient occlusion quad");
        }
    }
}
