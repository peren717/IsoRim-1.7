using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Sky and distance fog for the perspective camera (C1 and C2).
    //
    // The sky (Shaders\IsoRimSky.*) is a full-screen quad at the far end of the depth range, so it
    // only covers pixels nothing was drawn on: above the horizon, and the void past the map. The fog
    // (Shaders\IsoRimDistFog.*) is a stack of full-screen quads at set distances, each depth-tested:
    // the engine has no depth texture to read, but a layer only shows where the scene is further away
    // than it, so the stack gives fog by real distance. It ends solid in the horizon colour of the sky
    // just before the view distance, so the edge of what is drawn never shows.
    //
    // Colours follow the game: the sun's glow picks night, dusk or day, rain, snow and fog grey it,
    // and the sun and moon sit where IsoRim's light comes from.
    //
    // In space (SpaceEdges.IsSpaceMap) the sky is black with the night sky at full strength whatever the
    // hour, the sun is white, and there is no distance fog: the game's planet backdrop (drawn behind the
    // map, as in 2D) shows through the space cells and, with PlanetBelow, below the horizon, darkened by
    // the map's sky light as the game's light overlay darkens it on the map. The game's own dark ring
    // round space maps (MapDrawLayer_ExteriorLightingOverlay, for the flat view) is hidden meanwhile: in
    // perspective it stops short of the view and would leave a lit band.
    [StaticConstructorOnStartup]
    public static class Sky
    {
        private const int FogLayers = 24;

        private static bool tried;
        private static Material skyMat;
        private static Material dimMat;
        private static Material fogMat;
        private static Material planeMat;
        private static Mesh plane;
        private static IntVec3 planeSize;
        private static Mesh spacePlane;
        private static CellRect spacePlaneRect;
        private static Mesh quad;
        private static Mesh fogMesh;

        private static readonly int fwdId = Shader.PropertyToID("_SkyFwd");
        private static readonly int rightId = Shader.PropertyToID("_SkyRight");
        private static readonly int upId = Shader.PropertyToID("_SkyUp");
        private static readonly int zenithId = Shader.PropertyToID("_SkyZenith");
        private static readonly int horizonId = Shader.PropertyToID("_SkyHorizon");
        private static readonly int sunId = Shader.PropertyToID("_SkySun");
        private static readonly int sunColorId = Shader.PropertyToID("_SkySunColor");
        private static readonly int moonId = Shader.PropertyToID("_SkyMoon");
        private static readonly int celAId = Shader.PropertyToID("_SkyCelA");
        private static readonly int celBId = Shader.PropertyToID("_SkyCelB");
        private static readonly int celPId = Shader.PropertyToID("_SkyCelP");
        private static readonly int nightId = Shader.PropertyToID("_SkyNight");
        private static readonly int miscId = Shader.PropertyToID("_SkyMisc");
        private static readonly int trueSunId = Shader.PropertyToID("_SkyTrueSun");
        private static readonly int fogColorId = Shader.PropertyToID("_FogColor");
        private static readonly int fogDepthId = Shader.PropertyToID("_FogDepth");
        private static readonly int fogShapeId = Shader.PropertyToID("_FogShape");
        private static readonly int spaceId = Shader.PropertyToID("_SkySpace");
        private static readonly int dimId = Shader.PropertyToID("_SkyDim");

        // Space maps (SpaceEdges.IsSpaceMap): the planet the game draws behind the map shows below the horizon
        // (true), or black space with stars all round (false). Harness: probe key spaceplanet=0|1.
        public static bool PlanetBelow = true;

        // The last frame the sky darkened the planet backdrop itself (the game's dark ring is hidden then).
        private static int dimFrame = -10;

        static Sky()
        {
            if (!ModsConfig.OdysseyActive)
            {
                return;
            }
            try
            {
                var target = AccessTools.PropertyGetter(typeof(MapDrawLayer_ExteriorLightingOverlay), "Visible");
                if (target == null)
                {
                    ModLog.Warn("[IsoRim] MapDrawLayer_ExteriorLightingOverlay.Visible not found: the space sky may show a lit band past the map at night.");
                    return;
                }
                new Harmony("isorim.sky").Patch(target, postfix: new HarmonyMethod(typeof(Sky), nameof(ExteriorVisible_Postfix)));
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Could not hook the game's dark ring round space maps: " + e.Message);
            }
        }

        public static void ExteriorVisible_Postfix(ref bool __result)
        {
            if (__result && Time.frameCount - dimFrame <= 1)
            {
                __result = false;
            }
        }

        // False for a map the game makes underground (MapGeneratorDef.isUnderground: the undercave and the metal hell of
        // Anomaly, Odyssey's ancient stockpile and insect lair): no sky, sun, moon, night sky or distance fog there, as in
        // the game, which draws nothing past the cave walls. The one check for everything the perspective sky draws (Draw).
        public static bool OpenSky(Map map)
        {
            return map == null || map.generatorDef == null || !map.generatorDef.isUnderground;
        }

        // The colour the fog fades into this frame (the sky's horizon).
        public static Color Horizon { get; private set; }

        private static readonly Color NightZenith = new Color(0.015f, 0.025f, 0.06f);
        private static readonly Color NightHorizon = new Color(0.06f, 0.08f, 0.14f);
        private static readonly Color DuskZenith = new Color(0.26f, 0.30f, 0.55f);
        private static readonly Color DuskHorizon = new Color(0.93f, 0.60f, 0.40f);
        private static readonly Color DayZenith = new Color(0.30f, 0.52f, 0.86f);
        private static readonly Color DayHorizon = new Color(0.72f, 0.82f, 0.92f);
        private static readonly Color SpaceBlack = new Color(0.004f, 0.005f, 0.01f);
        private static readonly Color SpaceSun = new Color(1f, 0.97f, 0.92f);

        // Called from View3D.ApplyCamera's frame, with the perspective camera's basis.
        // tanX and tanY are the tangents of half the field of view across and up the screen.
        public static void Draw(Camera cam, Vector3 fwd, Vector3 right, Vector3 up, float tanX, float tanY,
            float near, float far, float fogStart, float fogEnd)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if ((!st.sky && !st.distanceFog) || cam == null || !WorldRendererUtility.DrawingMap)
            {
                return;
            }
            if (!tried)
            {
                Init();
            }
            Map map = Find.CurrentMap;
            if (map == null || !OpenSky(map))
            {
                return;
            }
            try
            {
                Color zenith, horizon;
                float sunShow, moonShow, grey;
                bool space = SpaceEdges.IsSpaceMap(map);
                Colours(map, space, out zenith, out horizon, out sunShow, out moonShow, out grey);
                Horizon = horizon;

                // The game's terrain writes no depth: give the ground its depth first, or the fog and the
                // sky would cover it as if it were empty.
                if (planeMat != null)
                {
                    Graphics.DrawMesh(space ? SpacePlane(map) : PlaneFor(map), Matrix4x4.identity, planeMat, 0, cam);
                }

                if (st.distanceFog && fogMat != null && !space)
                {
                    fogMat.SetVector(fogColorId, new Vector4(horizon.r, horizon.g, horizon.b, 1f));
                    fogMat.SetVector(fogDepthId, new Vector4(near, far, fogStart, Mathf.Max(fogEnd, fogStart + 1f)));
                    fogMat.SetVector(fogShapeId, new Vector4(1.6f, FogLayers, Mathf.Clamp01(st.fogStrength), 0f));
                    Graphics.DrawMesh(fogMesh, Matrix4x4.identity, fogMat, 0, cam);
                }

                if (st.sky && skyMat != null)
                {
                    float hour = GenLocalDate.HourFloat(map);
                    Vector3 sun = R3DMaterials.SunDirAt(Mathf.Clamp(hour, 4.5f, 19.5f));
                    Vector3 moon = R3DMaterials.MoonDir(map);
                    if (st.lowSky)
                    {
                        sun = Lowered(sun, LowSky);
                        moon = Lowered(moon, LowSky);
                    }
                    // The sun only shows while it is up.
                    sunShow *= Mathf.Clamp01(sun.y * 6f + 0.3f);
                    Color sunColor = space ? SpaceSun : Color.Lerp(new Color(1f, 0.55f, 0.3f), new Color(1f, 0.95f, 0.82f), Mathf.Clamp01(sun.y * 2.5f));

                    skyMat.SetVector(fwdId, fwd);
                    skyMat.SetVector(rightId, right * tanX);
                    skyMat.SetVector(upId, up * tanY);
                    skyMat.SetVector(zenithId, (Vector4)zenith);
                    skyMat.SetVector(horizonId, (Vector4)horizon);
                    skyMat.SetVector(sunId, new Vector4(sun.x, sun.y, sun.z, sunShow));
                    skyMat.SetVector(sunColorId, (Vector4)sunColor);
                    skyMat.SetVector(moonId, new Vector4(moon.x, moon.y, moon.z, moonShow));
                    NightSky(map, hour, grey, space);
                    // x: space, y: the planet backdrop below the horizon, z: its thin blue rim of air (lit by day).
                    float rim = space && PlanetBelow ? Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.1f, 0.6f, GenCelestial.CurCelestialSunGlow(map))) : 0f;
                    skyMat.SetVector(spaceId, new Vector4(space ? 1f : 0f, space && PlanetBelow ? 1f : 0f, rim, 0f));
                    skyMat.SetVector(dimId, new Vector4(1f, 1f, 1f, 0f));
                    Graphics.DrawMesh(quad, Matrix4x4.identity, skyMat, 0, cam);
                    if (space && PlanetBelow && dimMat != null)
                    {
                        // The planet backdrop past the drawn map, as dark as the game's light overlay makes it on the map.
                        dimMat.CopyPropertiesFromMaterial(skyMat);
                        Color sky = IsoGlow.SkyFor(map);
                        dimMat.SetVector(dimId, new Vector4(Mathf.Min(sky.r, 1.5f), Mathf.Min(sky.g, 1.5f), Mathf.Min(sky.b, 1.5f), 1f));
                        Graphics.DrawMesh(quad, Matrix4x4.identity, dimMat, 0, cam);
                        dimFrame = Time.frameCount;
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the sky failed: " + e, 93611951);
            }
        }

        // C3: the celestial sphere (stars, Milky Way, planets, gas giant) and the moon's phase.
        private static void NightSky(Map map, float hour, float grey, bool space)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            float days = Find.TickManager.TicksAbs / 60000f;

            // The pole stands as high as the map's latitude, to the north (+z) or the south.
            float lat = 40f;
            try
            {
                lat = Find.WorldGrid.LongLatOf(map.Tile).y;
            }
            catch
            {
            }
            float el = Mathf.Max(Mathf.Abs(lat), 3f) * Mathf.Deg2Rad;
            Vector3 pole = new Vector3(0f, Mathf.Sin(el), Mathf.Cos(el) * (lat < 0f ? -1f : 1f));
            Vector3 e1 = Vector3.right;
            Vector3 e2 = Vector3.Cross(pole, e1);
            // One turn a day, plus one a year (60 days) so the night sky changes with the seasons.
            float turn = (hour / 24f + days / 60f) * 2f * Mathf.PI;
            Vector3 a = Mathf.Cos(turn) * e1 + Mathf.Sin(turn) * e2;
            Vector3 b = -Mathf.Sin(turn) * e1 + Mathf.Cos(turn) * e2;

            float glow = GenCelestial.CurCelestialSunGlow(map);
            float clear = 1f - grey;
            // In space: no air, so the night sky at full strength at any hour.
            float dark = space ? 1f : Smooth(Mathf.InverseLerp(0.45f, 0.12f, glow));
            float bright = st.starBrightness;
            float stars = st.stars ? dark * clear * bright : 0f;
            float milky = st.milkyWay ? dark * dark * clear * bright : 0f;
            float planets = st.planets ? (space ? 1f : Smooth(Mathf.InverseLerp(0.55f, 0.25f, glow))) * clear * bright : 0f;
            float giant = st.gasGiant ? 1f - grey * 0.85f : 0f;
            float daylight = space ? 0f : Mathf.InverseLerp(0.3f, 0.75f, glow);
            float phase = st.moonPhases ? Mathf.Repeat(days / 15f + 0.5f, 1f) : 0.5f;
            Vector3 sun = TrueSun(hour);

            skyMat.SetVector(celAId, a);
            skyMat.SetVector(celBId, b);
            skyMat.SetVector(celPId, new Vector4(pole.x, pole.y, pole.z, st.lowSky ? LowSky : 1f));
            skyMat.SetVector(nightId, new Vector4(stars, milky, planets, giant));
            skyMat.SetVector(miscId, new Vector4(days, st.gasGiantSize, Time.realtimeSinceStartup % 600f, daylight));
            skyMat.SetVector(trueSunId, new Vector4(sun.x, sun.y, sun.z, phase));
        }

        // The sun where it really is, below the horizon at night (IsoRim's own sun path only covers
        // the day): opposite to where it stands twelve hours on, blended across dusk and dawn.
        private static Vector3 TrueSun(float hour)
        {
            Vector3 day = R3DMaterials.SunDirAt(Mathf.Clamp(hour, 6f, 18f));
            float other = hour < 12f ? hour + 12f : hour - 12f;
            Vector3 night = -R3DMaterials.SunDirAt(Mathf.Clamp(other, 6f, 18f));
            float t = hour < 12f ? Mathf.InverseLerp(6.5f, 5.5f, hour) : Mathf.InverseLerp(17.5f, 18.5f, hour);
            return Vector3.Slerp(day, night, Smooth(t)).normalized;
        }

        // How much lower the sun, moon and gas giant are drawn with "low in the sky" on.
        private const float LowSky = 0.35f;

        // The same direction with its height above the horizon scaled by k.
        private static Vector3 Lowered(Vector3 d, float k)
        {
            Vector2 flat = new Vector2(d.x, d.z);
            if (flat.sqrMagnitude < 1e-6f)
            {
                flat = new Vector2(0f, 1f);
            }
            flat.Normalize();
            float el = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * k;
            return new Vector3(flat.x * Mathf.Cos(el), Mathf.Sin(el), flat.y * Mathf.Cos(el));
        }

        private static float Smooth(float t)
        {
            return t * t * (3f - 2f * t);
        }

        // Night, dusk or day by the sun's glow; greyed by rain, snow and fog. In space: black, no weather.
        private static void Colours(Map map, bool space, out Color zenith, out Color horizon, out float sunShow, out float moonShow, out float grey)
        {
            float glow = GenCelestial.CurCelestialSunGlow(map);
            if (space)
            {
                zenith = SpaceBlack;
                horizon = SpaceBlack;
                float dim = R3DMaterials.NightWeight(glow);
                sunShow = 1f - dim;
                moonShow = dim;
                grey = 0f;
                return;
            }
            if (glow < 0.45f)
            {
                float t = Mathf.InverseLerp(0.1f, 0.45f, glow);
                zenith = Color.Lerp(NightZenith, DuskZenith, t);
                horizon = Color.Lerp(NightHorizon, DuskHorizon, t);
            }
            else
            {
                float t = Mathf.InverseLerp(0.45f, 0.8f, glow);
                zenith = Color.Lerp(DuskZenith, DayZenith, t);
                horizon = Color.Lerp(DuskHorizon, DayHorizon, t);
            }
            float night = R3DMaterials.NightWeight(glow);
            sunShow = 1f - night;
            moonShow = night;

            grey = 0f;
            WeatherManager w = map.weatherManager;
            if (w != null && w.curWeather != null)
            {
                float lerp = w.TransitionLerpFactor;
                float fogNow = w.curWeather.defName.Contains("Fog") ? 0.8f : 0f;
                float fogLast = w.lastWeather != null && w.lastWeather.defName.Contains("Fog") ? 0.8f : 0f;
                grey = Mathf.Clamp01(w.RainRate * 0.9f + w.SnowRate * 0.9f + Mathf.Lerp(fogLast, fogNow, lerp));
            }
            if (grey > 0f)
            {
                zenith = Greyed(zenith, grey);
                horizon = Greyed(horizon, grey);
                sunShow *= 1f - grey;
                moonShow *= 1f - grey;
            }
        }

        private static Color Greyed(Color c, float amount)
        {
            float l = c.r * 0.3f + c.g * 0.55f + c.b * 0.15f;
            Color g = new Color(l * 0.88f, l * 0.9f, l * 0.95f);
            return Color.Lerp(c, g, amount);
        }

        // The map's ground, one quad at height 0 (rebuilt when the map size changes).
        private static Mesh PlaneFor(Map map)
        {
            IntVec3 size = map.Size;
            if (plane != null && size == planeSize)
            {
                return plane;
            }
            planeSize = size;
            if (plane == null)
            {
                plane = new Mesh();
                plane.name = "IsoRim_DepthPlane";
            }
            plane.vertices = new Vector3[] { new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, size.z), new Vector3(size.x, 0f, size.z), new Vector3(size.x, 0f, 0f) };
            plane.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            plane.RecalculateBounds();
            MeshGuard.Check(plane, "sky depth plane");
            return plane;
        }

        // Space maps: the ground's depth only under the sections the game draws (MapDrawer.VisibleSections:
        // the view rect widened by a cell, in whole sections), so past them the sky's dim pass reaches the
        // planet backdrop as on the rest of the void (the game draws no light overlay there).
        private static Mesh SpacePlane(Map map)
        {
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1).ClipInsideMap(map);
            int s = Section.Size;
            int x0 = view.minX / s * s;
            int z0 = view.minZ / s * s;
            int x1 = Mathf.Min(map.Size.x, (view.maxX / s + 1) * s);
            int z1 = Mathf.Min(map.Size.z, (view.maxZ / s + 1) * s);
            CellRect r = new CellRect(x0, z0, Mathf.Max(0, x1 - x0), Mathf.Max(0, z1 - z0));
            if (spacePlane != null && r == spacePlaneRect)
            {
                return spacePlane;
            }
            spacePlaneRect = r;
            if (spacePlane == null)
            {
                spacePlane = new Mesh();
                spacePlane.name = "IsoRim_DepthPlaneSpace";
            }
            spacePlane.vertices = new Vector3[] { new Vector3(x0, 0f, z0), new Vector3(x0, 0f, z1), new Vector3(x1, 0f, z1), new Vector3(x1, 0f, z0) };
            spacePlane.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            spacePlane.RecalculateBounds();
            MeshGuard.Check(spacePlane, "sky depth plane (space)");
            return spacePlane;
        }

        private static void Init()
        {
            tried = true;
            Shader depth = R3DMaterials.ShaderOf("IsoRimDepthPlane");
            if (depth != null)
            {
                planeMat = new Material(depth);
                planeMat.name = "IsoRim_DepthPlane";
            }
            Shader sky = R3DMaterials.ShaderOf("IsoRimSky");
            Shader fog = R3DMaterials.ShaderOf("IsoRimDistFog");
            Shader dim = R3DMaterials.ShaderOf("IsoRimSkyDim");
            if (sky != null)
            {
                skyMat = new Material(sky);
                skyMat.name = "IsoRim_Sky";
            }
            if (dim != null)
            {
                dimMat = new Material(dim);
                dimMat.name = "IsoRim_SkyDim";
            }
            if (fog != null)
            {
                fogMat = new Material(fog);
                fogMat.name = "IsoRim_DistFog";
            }

            quad = new Mesh();
            quad.name = "IsoRim_Sky";
            quad.vertices = new Vector3[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) };
            quad.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            quad.RecalculateBounds();
            MeshGuard.Check(quad, "sky quad");

            // One quad per layer; uv x tells the vertex stage which layer (and so how far and how see-through).
            Vector3[] verts = new Vector3[FogLayers * 4];
            Vector2[] uvs = new Vector2[FogLayers * 4];
            int[] tris = new int[FogLayers * 6];
            for (int i = 0; i < FogLayers; i++)
            {
                float t = (i + 1) / (float)FogLayers;
                int v = i * 4;
                verts[v] = new Vector3(-1f, -1f, 0f);
                verts[v + 1] = new Vector3(-1f, 1f, 0f);
                verts[v + 2] = new Vector3(1f, 1f, 0f);
                verts[v + 3] = new Vector3(1f, -1f, 0f);
                for (int k = 0; k < 4; k++)
                {
                    uvs[v + k] = new Vector2(t, 0f);
                }
                int q = i * 6;
                tris[q] = v;
                tris[q + 1] = v + 1;
                tris[q + 2] = v + 2;
                tris[q + 3] = v;
                tris[q + 4] = v + 2;
                tris[q + 5] = v + 3;
            }
            fogMesh = new Mesh();
            fogMesh.name = "IsoRim_DistFog";
            fogMesh.vertices = verts;
            fogMesh.uv = uvs;
            fogMesh.triangles = tris;
            fogMesh.RecalculateBounds();
            MeshGuard.Check(fogMesh, "distance fog layers");
        }
    }
}
