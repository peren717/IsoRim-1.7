using System;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Anti-aliasing for the 3D view. Waylith creates its window without MSAA (the sample count is fixed
    // when the engine starts, before any mod loads), so edges of walls and models show stair steps,
    // worst with the perspective camera. This draws one full-screen FXAA pass (Shaders\IsoRimAA.frag.glsl)
    // over the map view, before the colour grade; the interface is drawn later and is not touched.
    // The same pass sharpens when that option is on (Graphics tab), also with FXAA off (then it only sharpens).
    [StaticConstructorOnStartup]
    public static class AntiAlias
    {
        private static bool tried;
        private static Material material;
        private static Mesh quad;
        private static readonly int strengthId = Shader.PropertyToID("_AAStrength");
        private static readonly int sharpenId = Shader.PropertyToID("_Sharpen");

        public static void Draw(Camera cam)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if ((!st.antiAlias && !st.sharpen) || cam == null || !WorldRendererUtility.DrawingMap)
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
            try
            {
                material.SetFloat(strengthId, st.antiAlias ? Mathf.Clamp01(st.antiAliasStrength) : 0f);
                material.SetFloat(sharpenId, st.sharpen ? Mathf.Clamp01(st.sharpenStrength) : 0f);
                Graphics.DrawMesh(quad, Matrix4x4.identity, material, 0, cam);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the anti-aliasing pass failed: " + e, 93611961);
            }
        }

        private static void Init()
        {
            tried = true;
            Shader shader = R3DMaterials.ShaderOf("IsoRimAA");
            if (shader == null)
            {
                return;
            }
            material = new Material(shader);
            material.name = "IsoRim_AA";
            quad = new Mesh();
            quad.name = "IsoRim_AA";
            quad.vertices = new Vector3[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) };
            quad.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            quad.RecalculateBounds();
            MeshGuard.Check(quad, "anti-aliasing quad");
        }
    }
}
