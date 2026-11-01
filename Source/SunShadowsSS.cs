using System;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Sun shadows on walls and models (Graphics options, default off): screen-space sun shadows on everything above the
    // ground (wall faces, furniture, models, pawns, ProjectZ slabs and what stands on them).
    // Two full-screen passes after the AO passes: IsoRimSSS (scene grab at 2452) marches toward the sun through the scene
    // depth (DepthGrab) at half resolution and keeps the shadow in the alpha channel of one pixel per 2 x 2 block;
    // IsoRimSSSBlur (2453) fills every pixel from those samples, depth-aware, and multiplies the colour toward the game's
    // own sun shadow colour (MatBases.SunShadow.color, which already fades with the game's shadow strength: dusk and night
    // follow the game, no moon shadows of our own) times the strength setting (100% = the game's ground shadows).
    // The sun direction is the one the game's ground shadows use in 3D: a point one wall up throws its shadow one cast
    // vector away (Floors.ShadowSlide), so the shadow on a wall meets the ground shadow at the wall's foot.
    // The ground receives nothing (the game's shadows, tree shadows, ground shadows and Dynamic Pawn Shadows are there).
    // With AO on the same frame, AO keeps its factor in the alpha channel and the darker of the two is used where both
    // apply (no double darkening in shaded corners); the depth copy made for AO is reused (DepthGrab).
    // Only what is drawn into the depth before queue 2452 casts or receives: cut-out cards and sprites (2900) do neither,
    // and ProjectZ floors hidden above the viewed one cast nothing on the floors below (they are not drawn).
    [StaticConstructorOnStartup]
    public static class SunShadowsSS
    {
        public const string GrabName = "IsoRimSSS";

        public const float MaxDist = 40f;          // cells, the longest march (low sun)
        public const float Thickness = 2.0f;       // cells: a surface counts as this thick behind what the depth shows (1 let rays
                                                   // from the ground pass behind walls seen at an angle; harness sweep 2026-11-01)
        public const float TallestWalls = 3.5f;    // the tallest caster without floors, in wall heights (walls, trees)
        public const int Steps = 24;
        public const float EdgeFade = 0.05f;       // fraction of the screen

        // Harness only (probe key sss=...).
        public static float StrengthScale = 1f;
        public static float MaxDistOverride = -1f;
        public static float ThicknessOverride = -1f;
        public static int StepsOverride = -1;
        public static float TallestOverride = -1f;
        public static int Debug;                   // 1 shadow factor, 2 classes, 3 N.L, 4 raw samples, 5 normals
        public static int ExtraDraws;
        public static string LastInfo = "";

        // Set by Prepare before AO draws: the passes will be drawn this frame (AO then keeps its factor for us).
        public static bool WillDraw;

        private static bool tried;
        private static Material material, blur;
        private static Mesh quad;
        private static readonly int projId = Shader.PropertyToID("_SSProj");
        private static readonly int projBId = Shader.PropertyToID("_SSProjB");
        private static readonly int planeId = Shader.PropertyToID("_SSPlane");
        private static readonly int lightId = Shader.PropertyToID("_SSLight");
        private static readonly int paramsId = Shader.PropertyToID("_SSParams");
        private static readonly int tuneId = Shader.PropertyToID("_SSTune");
        private static readonly int colorId = Shader.PropertyToID("_SSColor");
        private static readonly int world0Id = Shader.PropertyToID("_SSWorld0");
        private static readonly int worldXId = Shader.PropertyToID("_SSWorldX");
        private static readonly int worldZId = Shader.PropertyToID("_SSWorldZ");
        private static readonly int heightsId = Shader.PropertyToID("_IsoShadowHeights");

        public static bool HeightMap = true;     // harness A/B (probe key ssshm=0/1)

        // The option is on in the 3D view and can draw: the ground takes its sun shadows from this pass, and the game's own
        // sun shadows and IsoRim's tree shadows are left out (Patches: SunShadowsRegenerate_Prefix, GraphicShadowDraw_Prefix: models only, cards and sprites keep theirs,
        // TreeShadows.Print; part of View3D.StateSignature, so switching rebuilds the baked ones). Off: all as before.
        public static bool ReplacesGround
        {
            get { return View3D.Active && IsoRimMod.Settings != null && IsoRimMod.Settings.screenSunShadows && !DepthGrab.Broken; }
        }

        public static bool Available
        {
            get { return !DepthGrab.Broken; }
        }

        // Main thread, before AmbientOcclusion.Draw: whether the passes will be drawn this frame.
        public static void Prepare(Camera cam)
        {
            WillDraw = false;
            IsoRimSettings st = IsoRimMod.Settings;
            if (!st.screenSunShadows || cam == null || !WorldRendererUtility.DrawingMap || DepthGrab.Broken)
            {
                return;
            }
            Vector4 cast;
            if (!Shader.TryGetGlobalVector(ShaderPropertyIDs.MapSunLightDirection, out cast))
            {
                return;
            }
            Color sc = MatBases.SunShadow.color;
            if (sc.r > 0.995f && sc.g > 0.995f && sc.b > 0.995f)
            {
                return;   // no shadows right now (the game's shadow strength is 0)
            }
            if (!tried)
            {
                Init();
            }
            WillDraw = material != null && blur != null;
        }

        public static void Draw(Camera cam)
        {
            if (!WillDraw || !DepthGrab.Request())
            {
                return;
            }
            try
            {
                IsoRimSettings st = IsoRimMod.Settings;
                Vector4 cast = Shader.GetGlobalVector(ShaderPropertyIDs.MapSunLightDirection);
                Color sc = MatBases.SunShadow.color;
                Matrix4x4 p = cam.projectionMatrix;
                bool persp = View3D.Perspective;
                Vector4 proj = new Vector4(p.m00, p.m11, p.m22, p.m23);
                Vector4 projB = new Vector4(p.m03, p.m13, persp ? 1f : 0f, AmbientOcclusion.DrawnThisFrame ? 1f : 0f);

                // The ground plane (drawn space y = 0) in camera space: n.p + c = 0, n pointing up.
                Matrix4x4 v = cam.worldToCameraMatrix;
                Vector3 p0 = v.MultiplyPoint(Vector3.zero);
                Vector3 p1 = v.MultiplyPoint(new Vector3(0f, 0f, 1f));
                Vector3 p2 = v.MultiplyPoint(new Vector3(1f, 0f, 0f));
                Vector3 n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                if (Vector3.Dot(n, v.MultiplyVector(Vector3.up)) < 0f)
                {
                    n = -n;
                }
                Vector4 plane = new Vector4(n.x, n.y, n.z, -Vector3.Dot(n, p0));

                float wall = View3D.WallHeight;
                Vector3 sunW = new Vector3(-cast.x, wall, -cast.z).normalized;   // drawn space, toward the sun
                // The view matrix squashes world y (IsoRim keeps the game's altitudes in y): the camera-space direction is
                // built from the ground axes and the plane normal (camera space is unsquashed: one cell up = one unit along n).
                Vector3 sunC = (v.MultiplyVector(Vector3.right) * sunW.x + n * sunW.y + v.MultiplyVector(Vector3.forward) * sunW.z).normalized;
                Vector4 light = new Vector4(sunC.x, sunC.y, sunC.z, 0f);

                float tallest = TallestOverride > 0f ? TallestOverride * wall : Mathf.Max(TallestWalls * wall, Floors.TopShownHeight + 2f * wall);
                float strength = Mathf.Clamp(st.sssStrength, 0.5f, 2f) * StrengthScale;
                Vector4 prm = new Vector4(strength, MaxDistOverride > 0f ? MaxDistOverride : MaxDist, ThicknessOverride > 0f ? ThicknessOverride : Thickness, Debug);
                Vector4 tune = new Vector4(tallest, sunW.y, Mathf.Clamp(StepsOverride > 0 ? StepsOverride : Steps, 4, 64), EdgeFade);
                Vector4 col = new Vector4(sc.r, sc.g, sc.b, 1f);

                // The world shadow height map (ShadowHeights): world axes in camera space to find a ray point's cell.
                Map map = Find.CurrentMap;
                Texture2D hm = null;
                ShadowHeights sh = null;
                if (HeightMap && map != null)
                {
                    sh = ShadowHeights.For(map);
                    hm = sh.Update();
                }
                Vector3 rx = v.MultiplyVector(Vector3.right).normalized, rz = v.MultiplyVector(Vector3.forward).normalized;
                Vector4 w0 = new Vector4(p0.x, p0.y, p0.z, hm != null ? 1f : 0f);
                Vector4 wx = new Vector4(rx.x, rx.y, rx.z, sh != null ? sh.Width : 0f);
                Vector4 wz = new Vector4(rz.x, rz.y, rz.z, sh != null ? sh.Rows : 0f);
                if (hm != null)
                {
                    Shader.SetGlobalTexture(heightsId, hm);
                }
                material.SetVector(world0Id, w0);
                material.SetVector(worldXId, wx);
                material.SetVector(worldZId, wz);
                LastInfo = "sunW " + sunW.ToString("F3") + " tallest " + tallest.ToString("F2") + " strength " + strength.ToString("F2") + " ao " + AmbientOcclusion.DrawnThisFrame;
                for (int k = 0; k < 2; k++)
                {
                    Material m = k == 0 ? material : blur;
                    m.SetVector(projId, proj);
                    m.SetVector(projBId, projB);
                    m.SetVector(planeId, plane);
                    m.SetVector(lightId, light);
                    m.SetVector(paramsId, prm);
                    m.SetVector(tuneId, tune);
                    m.SetVector(colorId, col);
                }
                for (int i = 0; i <= ExtraDraws; i++)
                {
                    Graphics.DrawMesh(quad, Matrix4x4.identity, material, 0, cam);
                }
                Graphics.DrawMesh(quad, Matrix4x4.identity, blur, 0, cam);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the sun shadows on walls and models failed: " + e, 93611978);
            }
        }

        private static void Init()
        {
            tried = true;
            Shader shader = R3DMaterials.ShaderOf("IsoRimSSS");
            Shader blurShader = R3DMaterials.ShaderOf("IsoRimSSSBlur");
            if (shader == null || blurShader == null)
            {
                return;
            }
            material = new Material(shader) { name = "IsoRim_SSS" };
            blur = new Material(blurShader) { name = "IsoRim_SSSBlur" };
            quad = new Mesh();
            quad.name = "IsoRim_SSS";
            quad.vertices = new Vector3[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) };
            quad.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            quad.RecalculateBounds();
            MeshGuard.Check(quad, "sun shadows quad");
        }
    }
}
