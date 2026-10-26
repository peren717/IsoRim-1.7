using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The planet below a space map in the perspective view: the game's own world globe, seen from just above it.
    //
    // On a space map the game renders the world behind the map (WorldRenderMode.Background): its world camera looks
    // straight down at the planet from far above the map's tile (WorldCameraDriver.ApplyMapPositionToGameObject), which
    // in 2D gives a flat picture under the map. In the perspective view that picture filled everything below the
    // horizon with a straight edge. Instead, while the perspective sky is drawn on a space map with the planet below
    // (Sky.PlanetBelow), the world camera is put close above the planet's surface, over the map's tile (straight below
    // the map, the map's north toward the world's north), and turned exactly as IsoRim's camera, with the same field of
    // view. The globe then shows as a huge curved horizon below the map that turns with the view, lit by the game's own
    // sun (day under the map at noon, night at midnight), with the game's own terrain and clouds.
    //
    // Seen from the height H over a planet of radius R, the planet's rim (limb) stands at Dip degrees below the
    // horizontal all round (cos Dip = R / (R + H)), so the sky shaders only need that one number (LimbSin, passed in
    // _SkySpace.w): rays more than Dip below the horizontal hit the planet (the sky leaves those pixels to the world
    // camera's picture), the glow of its air hugs the limb just above.
    //
    // Nothing else of the world camera is changed; when the view leaves it (2D, a surface map, the world view) its own
    // field of view and near plane are put back and the game places it again as before.
    [StaticConstructorOnStartup]
    public static class OrbitPlanet
    {
        // Harness only (probe orbitplanet=0|1): the game's flat planet backdrop (before/after shots).
        public static bool Enabled = true;

        // How far below the horizontal the planet's limb stands, degrees (probe orbitdip=x). Smaller: a bigger planet
        // and a flatter arc, seen from closer (its terrain coarser); larger: a rounder planet further down.
        public static float Dip = 16f;

        // Which way the planet's terrain is turned under the map, degrees about the vertical (probe orbitturn=x).
        public static float Turn = 0f;

        private static int frame = -10;
        private static Quaternion look = Quaternion.identity;
        private static float fov = 40f;
        private static Map frameMap;
        private static Color spaceColor = Color.black;

        private static bool overridden;
        private static float savedFov = 20f;
        private static float savedNear = 2f;
        private static Color savedSkybox = Color.black;

        static OrbitPlanet()
        {
            if (!ModsConfig.OdysseyActive)
            {
                return;
            }
            try
            {
                Harmony h = new Harmony("isorim.orbitplanet");
                var place = AccessTools.Method(typeof(WorldCameraDriver), nameof(WorldCameraDriver.ApplyMapPositionToGameObject));
                var update = AccessTools.Method(typeof(WorldCameraDriver), nameof(WorldCameraDriver.Update));
                if (place == null || update == null)
                {
                    ModLog.Warn("[IsoRim] WorldCameraDriver methods not found: the planet below space maps stays flat.");
                    return;
                }
                h.Patch(place, postfix: new HarmonyMethod(typeof(OrbitPlanet), nameof(Place_Postfix)));
                h.Patch(update, postfix: new HarmonyMethod(typeof(OrbitPlanet), nameof(Update_Postfix)));
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Could not hook the world camera (planet below space maps): " + e.Message);
            }
        }

        // The sine of the limb's angle below the horizontal, for the sky shaders (_SkySpace.w).
        public static float LimbSin
        {
            get { return Mathf.Sin(Mathf.Clamp(Dip, 2f, 60f) * Mathf.Deg2Rad); }
        }

        // Whether the globe is drawn below this space map in the perspective view (the sky's own conditions aside).
        public static bool For(Map map)
        {
            return Enabled && map != null && Sky.PlanetBelow && WorldRendererUtility.WorldBackgroundNow && SpaceEdges.IsSpaceMap(map);
        }

        // From Sky.Draw, every frame the perspective sky is drawn on such a map: IsoRim's camera basis (true space) and
        // field of view. The world camera is placed now (if the game has already placed it this frame) and again
        // whenever the game places it (Place_Postfix), so it never lags a turn by a frame.
        public static void Frame(Map map, Vector3 fwd, Vector3 up, float fieldOfView, Color space)
        {
            spaceColor = space;
            look = Quaternion.LookRotation(fwd, up);
            fov = fieldOfView;
            frameMap = map;
            frame = Time.frameCount;
            Apply();
        }

        private static bool On
        {
            get { return Time.frameCount - frame <= 1 && frameMap != null && frameMap == Find.CurrentMap && For(frameMap); }
        }

        public static void Place_Postfix()
        {
            if (On)
            {
                Apply();
            }
        }

        public static void Update_Postfix()
        {
            if (overridden && !On)
            {
                Restore();
            }
        }

        private static void Apply()
        {
            Camera cam = WorldCameraManager.WorldCamera;
            if (cam == null || frameMap == null)
            {
                return;
            }
            try
            {
                SurfaceLayer surface = Find.WorldGrid.Surface;
                float radius = surface != null ? surface.Radius : WorldGrid.PlanetRadius;
                Vector3 origin = surface != null ? surface.Origin : Vector3.zero;

                // Straight below the map's tile (any layer: the orbit's tiles stand over the surface).
                Vector3 n = Vector3.up;
                PlanetTile tile = frameMap.Tile;
                if (tile.Valid && tile.Layer != null)
                {
                    Vector3 c = tile.Layer.GetTileCenter(tile) - tile.Layer.Origin;
                    if (c.sqrMagnitude > 1e-4f)
                    {
                        n = c.normalized;
                    }
                }
                // The map's north toward the world's north pole (east if the tile is on a pole).
                Vector3 north = Vector3.up - n * n.y;
                if (north.sqrMagnitude < 1e-4f)
                {
                    north = Vector3.Cross(Vector3.right, n);
                }
                Quaternion frameRot = Quaternion.LookRotation(north.normalized, n) * Quaternion.Euler(0f, Turn, 0f);

                float dip = Mathf.Clamp(Dip, 2f, 60f) * Mathf.Deg2Rad;
                float dist = radius / Mathf.Cos(dip);
                float height = dist - radius;
                if (!overridden)
                {
                    savedFov = cam.fieldOfView;
                    savedNear = cam.nearClipPlane;
                    Camera box = WorldCameraManager.WorldSkyboxCamera;
                    savedSkybox = box != null ? box.backgroundColor : Color.black;
                    overridden = true;
                }
                Transform t = cam.transform;
                t.position = origin + n * dist;
                t.rotation = frameRot * look;
                cam.fieldOfView = fov;
                cam.nearClipPlane = Mathf.Min(savedNear, height * 0.25f);
                // Where something over the void hides the sky (it writes depth, e.g. a flat shadow), the world's own
                // backdrop shows through: cleared to the sky's space colour, not the game's teal.
                Camera skybox = WorldCameraManager.WorldSkyboxCamera;
                if (skybox != null)
                {
                    skybox.backgroundColor = spaceColor;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Placing the world camera over the planet failed: " + e, 93611961);
            }
        }

        private static void Restore()
        {
            overridden = false;
            Camera cam = WorldCameraManager.WorldCamera;
            if (cam != null)
            {
                cam.fieldOfView = savedFov;
                cam.nearClipPlane = savedNear;
            }
            Camera skybox = WorldCameraManager.WorldSkyboxCamera;
            if (skybox != null)
            {
                skybox.backgroundColor = savedSkybox;
            }
        }
    }
}
