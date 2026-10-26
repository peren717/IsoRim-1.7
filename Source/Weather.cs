using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Rain, snow, fog and the other weather overlays. The game draws them as a sheet over the whole
    // map (SkyOverlay.DrawWorldOverlay) or over the screen (DrawScreenOverlay), both lying flat, which
    // in the 3D view put the rain on the ground. While the 3D view is on they are drawn on a curtain
    // stood up just in front of the camera instead, so the streaks fall down the screen.
    public static class Weather
    {
        private static Mesh worldCurtain, screenCurtain;
        private static Vector2 worldSpan;

        // The world sheet (MeshPool.wholeMapPlane) repeats its texture every 10 cells, so its drops grow
        // as the camera zooms in. On the curtain they keep one size on screen: this many repeats up the
        // screen at weatherSize 1 (about the game's middle zoom).
        private const float TilesUpScreen = 15f;

        // The option (weather3D, off at first): off, every weather overlay is the game's own, as without the mod.
        public static bool On
        {
            get { return View3D.Ready && IsoRimMod.Settings.weather3D; }
        }

        public static bool DrawWorldOverlay_Prefix(Map __0, Material __1, int __3)
        {
            if (!On || __0 == null || __1 == null)
            {
                return true;
            }
            Falling.Kind kind = Falling.KindOf(__1);
            if (kind != Falling.Kind.None)
            {
                Falling.Draw(__0, __1, kind);
                return false;
            }
            float tiles = TilesUpScreen / Mathf.Clamp(IsoRimMod.Settings.weatherSize, 0.25f, 3f);
            Vector2 span = new Vector2(tiles * View3D.Aspect, tiles);
            if (worldCurtain == null || (span - worldSpan).sqrMagnitude > 1e-8f)
            {
                worldSpan = span;
                worldCurtain = Quad(worldCurtain, span);
            }
            Graphics.DrawMesh(worldCurtain, View3D.CurtainMatrix(), __1, __3);
            return false;
        }

        public static bool DrawScreenOverlay_Prefix(Material __0, int __2)
        {
            if (!On || __0 == null)
            {
                return true;
            }
            if (Falling.KindOf(__0) != Falling.Kind.None)
            {
                return false;   // the falling drops or flakes stand in for it
            }
            if (screenCurtain == null)
            {
                screenCurtain = Quad(null, Vector2.one);
            }
            Graphics.DrawMesh(screenCurtain, View3D.CurtainMatrix(), __0, __2);
            return false;
        }

        // A 1 by 1 quad on the XZ plane facing up (as MeshPool.plane10), seen from both sides,
        // its texture running 0 to span.
        private static Mesh Quad(Mesh mesh, Vector2 span)
        {
            bool fresh = mesh == null;
            if (fresh)
            {
                mesh = new Mesh();
                mesh.vertices = new[]
                {
                    new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f),
                    new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f)
                };
            }
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(0f, span.y), new Vector2(span.x, span.y), new Vector2(span.x, 0f)
            };
            if (fresh)
            {
                mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 }, 0);
                mesh.RecalculateBounds();
            }
            return mesh;
        }
    }

    // Rain and snow as real drops and flakes falling in 3D round what the camera looks at, instead of
    // the game's textured sheet (which, seen from the side, showed as wide blurred columns). Drops sit
    // at fixed places in the world (repeating every box width), so they do not slide as the camera
    // moves, and none fall under a roof.
    [StaticConstructorOnStartup]
    public static class Falling
    {
        public enum Kind { None, Rain, Snow }

        private const int MaxDrops = 4000;

        private static float[] seedX, seedZ, seedY, seedSpeed;
        private static Mesh mesh;
        private static Vector3[] verts;
        private static Material mat;
        private static int drawnFrame = -1;
        private static readonly int colorId = Shader.PropertyToID("_Color");

        // From the overlay's class. The game's own by name and base class (WeatherOverlay_Rain, _TorrentialRain,
        // _ToxRain, _BloodRain; the snow, fallout and ashes as flakes); its other overlays (fog, haze, sand, Odyssey's
        // volcanic ash) stay the game's sheets on the curtain. Other mods' overlays by their class name.
        private static readonly Dictionary<Type, Kind> kinds = new Dictionary<Type, Kind>();

        public static Kind KindOf(SkyOverlay o)
        {
            if (o == null)
            {
                return Kind.None;
            }
            Type t = o.GetType();
            Kind k;
            if (!kinds.TryGetValue(t, out k))
            {
                k = Classify(t);
                kinds[t] = k;
            }
            return k;
        }

        private static Kind Classify(Type t)
        {
            for (Type b = t; b != null && b != typeof(SkyOverlay); b = b.BaseType)
            {
                switch (b.FullName)
                {
                    case "RimWorld.WeatherOverlay_Rain":
                    case "RimWorld.WeatherOverlay_TorrentialRain":
                    case "RimWorld.WeatherOverlay_ToxRain":
                    case "RimWorld.WeatherOverlay_BloodRain":
                        return Kind.Rain;
                    case "RimWorld.WeatherOverlay_SnowGentle":
                    case "RimWorld.WeatherOverlay_SnowHard":
                    case "RimWorld.WeatherOverlay_SnowBlizzard":
                    case "RimWorld.WeatherOverlay_Fallout":
                    case "RimWorld.WeatherOverlay_DeathpallAshes":
                    case "RimWorld.WeatherOverlay_GrayPallAsh":
                        return Kind.Snow;
                }
            }
            if (t.Assembly == typeof(SkyOverlay).Assembly)
            {
                return Kind.None;
            }
            return KindOf(t.Name);
        }

        public static Kind KindOf(Material m)
        {
            return m == null ? Kind.None : KindOf(m.name);
        }

        private static Kind KindOf(string n)
        {
            if (string.IsNullOrEmpty(n) || n.IndexOf("Volcanic", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Kind.None;
            }
            if (n.IndexOf("Rain", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Kind.Rain;
            }
            if (n.IndexOf("Snow", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Ash", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Fallout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Kind.Snow;
            }
            return Kind.None;
        }

        // In place of a weather overlay's own drawing (Patches.SkyOverlayDraw_Prefix).
        public static bool DrawOverlay_Prefix(SkyOverlay o, Map map)
        {
            if (!Weather.On || map == null)
            {
                return true;
            }
            Kind kind = KindOf(o);
            if (kind == Kind.None)
            {
                return true;
            }
            float amount = Amount(o, map);
            if (amount > 0.01f)
            {
                Draw(map, o.GetType().Name, kind, amount, TintOf(MaterialOf(o)));
            }
            return false;
        }

        // The overlay's sheet, whose colour the drops take (blood rain red, tox rain green, ashes dark).
        private static readonly Dictionary<Type, FieldInfo[]> matFields = new Dictionary<Type, FieldInfo[]>();

        internal static Material MaterialOf(SkyOverlay o)
        {
            WeatherOverlayDualPanner dual = o as WeatherOverlayDualPanner;
            if (dual != null && dual.worldOverlayMat != null)
            {
                return dual.worldOverlayMat;
            }
            Type t = o.GetType();
            FieldInfo[] fields;
            if (!matFields.TryGetValue(t, out fields))
            {
                // Overlays with their own layers (torrential rain, blizzard): the particle layer first.
                List<FieldInfo> found = new List<FieldInfo>();
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType == typeof(Material))
                    {
                        if (f.Name.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found.Insert(0, f);
                        }
                        else
                        {
                            found.Add(f);
                        }
                    }
                }
                fields = found.ToArray();
                matFields[t] = fields;
            }
            for (int i = 0; i < fields.Length; i++)
            {
                Material m = fields[i].GetValue(fields[i].IsStatic ? null : o) as Material;
                if (m != null)
                {
                    return m;
                }
            }
            return null;
        }

        // The hue of a sheet: its colour (the sky's, or the weather's own, as the game sets it) times its texture's
        // average, brightest channel 1. Grey sheets (plain rain, snow) give white: the drops keep their own colour.
        private static readonly Dictionary<Texture, Color> texAverage = new Dictionary<Texture, Color>();
        private static readonly int[] tintIds = { Shader.PropertyToID("_TuningColor") };

        internal static Color TintOf(Material m)
        {
            if (m == null)
            {
                return Color.white;
            }
            Color c = m.color;
            // The game's RainOverlay shader also tints by these (tox rain is green by its _TuningColor).
            for (int i = 0; i < tintIds.Length; i++)
            {
                if (m.HasProperty(tintIds[i]))
                {
                    Color t = m.GetColor(tintIds[i]);
                    c = new Color(c.r * t.r, c.g * t.g, c.b * t.b);
                }
            }
            Texture tex = m.mainTexture;
            if (tex != null)
            {
                Color avg;
                if (!texAverage.TryGetValue(tex, out avg))
                {
                    avg = Color.white;
                    Color32[] px = SpritePixels.Sample(tex, Vector2.one, Vector2.zero, 16);
                    if (px != null)
                    {
                        float r = 0f, g = 0f, b = 0f, w = 0f;
                        for (int i = 0; i < px.Length; i++)
                        {
                            float k = px[i].a / 255f * Mathf.Max(px[i].r, Mathf.Max(px[i].g, px[i].b)) / 255f;
                            r += px[i].r * k;
                            g += px[i].g * k;
                            b += px[i].b * k;
                            w += k;
                        }
                        if (w > 0.001f)
                        {
                            avg = new Color(r / w / 255f, g / w / 255f, b / w / 255f);
                        }
                    }
                    texAverage[tex] = avg;
                }
                c = new Color(c.r * avg.r, c.g * avg.g, c.b * avg.b);
            }
            float top = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (top < 0.02f)
            {
                return Color.white;
            }
            c = new Color(c.r / top, c.g / top, c.b / top);
            return Mathf.Min(c.r, Mathf.Min(c.g, c.b)) > 0.85f ? Color.white : c;
        }

        // How much of a weather is showing: the game keeps drawing the last weather after it has gone
        // (its sheets fade out with the transition), so the drops follow the same blend.
        private static float Amount(SkyOverlay o, Map map)
        {
            WeatherManager wm = map.weatherManager;
            if (wm == null || wm.curWeather == null || wm.lastWeather == null)
            {
                return 1f;
            }
            bool inCur = wm.curWeather.Worker.overlays.Contains(o);
            bool inLast = wm.lastWeather.Worker.overlays.Contains(o);
            float t = Mathf.Clamp01(wm.TransitionLerpFactor);
            if (inCur && inLast)
            {
                return 1f;
            }
            if (inCur)
            {
                return t;
            }
            if (inLast)
            {
                return 1f - t;
            }
            return 1f;   // a game condition's overlay, not the weather's
        }

        public static void Draw(Map map, Material overlay, Kind kind)
        {
            Draw(map, overlay.name, kind, 1f, TintOf(overlay));
        }

        public static void Draw(Map map, string n, Kind kind, float amount, Color tint)
        {
            if (Time.frameCount == drawnFrame)
            {
                return;   // a weather with two sheets (or two weathers blending) falls once
            }
            drawnFrame = Time.frameCount;
            if (mat == null)
            {
                mat = R3DMaterials.Make(R3DMaterials.BlendDef, "IsoRim_Falling", 3200);
                if (mat == null)
                {
                    return;
                }
            }
            Setup();

            bool rain = kind == Kind.Rain;
            float size = Mathf.Clamp(IsoRimMod.Settings.weatherSize, 0.25f, 3f);

            // The box the drops fall in: round the point between the eye and the focus.
            Vector3 eye = View3D.Eye;
            Vector3 fwd = View3D.Forward;
            float r, top;
            Vector3 centre;
            if (View3D.Perspective)
            {
                float d = FirstPerson.On ? 6f : View3D.CamDistance;
                centre = eye + fwd * (d * 0.45f);
                r = Mathf.Clamp(d * 0.6f, 15f, 45f);
                top = Mathf.Clamp(eye.y + 2f, 6f, 30f);
            }
            else
            {
                Vector3 focus = eye + fwd * (eye.y / Mathf.Max(0.05f, -fwd.y));
                centre = focus;
                r = Mathf.Clamp(View3D.OrthoSize * View3D.Aspect, 6f, 70f);
                top = 10f;
            }
            float span = 2f * r;

            // As many drops as the box holds at a steady density, so zooming in (a smaller box) does not
            // pack them together.
            float density = rain ? (n.IndexOf("Torrential", System.StringComparison.OrdinalIgnoreCase) >= 0 ? 0.03f : 0.02f) : 0.016f;
            int count = Mathf.Min(MaxDrops, Mathf.CeilToInt(span * span * top * density * amount));
            float x0 = centre.x - r, z0 = centre.z - r;

            float t = Clock();
            float fall = rain ? 16f : 1.3f;
            float len = (rain ? 0.55f : 0.07f) * size;
            Vector3 slant = rain ? new Vector3(-0.12f, 1f, 0f) : Vector3.up;
            RoofGrid roofs = map.roofGrid;
            IntVec3 sizeMap = new IntVec3(map.Size.x, 0, Floors.DrawnRows(map));   // stacked floors: the ground rows only

            for (int i = 0; i < MaxDrops; i++)
            {
                int v = i * 4;
                if (i >= count)
                {
                    verts[v] = verts[v + 1] = verts[v + 2] = verts[v + 3] = Vector3.zero;
                    continue;
                }
                float x = x0 + Mathf.Repeat(seedX[i] * span - x0, span);
                float z = z0 + Mathf.Repeat(seedZ[i] * span - z0, span);
                float y = top - Mathf.Repeat(seedY[i] * top + t * fall * seedSpeed[i], top);
                if (!rain)
                {
                    // Flakes drift as they fall.
                    x += Mathf.Sin(t * 0.9f + seedY[i] * 40f) * 0.35f;
                    z += Mathf.Cos(t * 0.7f + seedX[i] * 40f) * 0.35f;
                }
                int cx = (int)x, cz = (int)z;
                if (x < 0f || z < 0f || cx >= sizeMap.x || cz >= sizeMap.z || roofs.Roofed(cx, cz))
                {
                    verts[v] = verts[v + 1] = verts[v + 2] = verts[v + 3] = Vector3.zero;
                    continue;
                }
                Vector3 b = new Vector3(x, y, z);
                Vector3 toEye = View3D.Perspective ? eye - b : -fwd;
                float dist = toEye.magnitude;
                Vector3 side, along;
                if (rain)
                {
                    along = slant * len;
                    side = Vector3.Cross(slant, toEye).normalized * (Mathf.Max(0.018f, dist * 0.0016f) * size * 0.5f);
                }
                else
                {
                    // A small square turned to the camera, never thinner than about a pixel.
                    float w = Mathf.Max(len, dist * 0.0022f) * 0.5f;
                    along = View3D.Up * (2f * w);
                    side = View3D.Right * w;
                }
                verts[v] = Drawn(b - side);
                verts[v + 1] = Drawn(b - side + along);
                verts[v + 2] = Drawn(b + side + along);
                verts[v + 3] = Drawn(b + side);
            }
            mesh.vertices = verts;
            mesh.RecalculateBounds();

            // Lit by the daylight: pale grey-blue by day, dim at night, in the hue of the weather's own sheet.
            float glow = map.skyManager != null ? map.skyManager.CurSkyGlow : 1f;
            float l = Mathf.Lerp(0.22f, 0.85f, glow);
            mat.SetColor(colorId, new Color(l * 0.92f * tint.r, l * 0.96f * tint.g, l * tint.b, rain ? 0.35f : 0.8f));
            Graphics.DrawMesh(mesh, Matrix4x4.identity, mat, 0);
        }

        // Drops stop in the air while the game is paused, and fall faster with the game's speed.
        private static float clock, lastReal = -1f;

        private static float Clock()
        {
            float now = Time.realtimeSinceStartup;
            float dt = lastReal < 0f ? 0f : Mathf.Min(now - lastReal, 0.1f);
            lastReal = now;
            TickManager tm = Find.TickManager;
            float rate = tm == null || tm.Paused ? 0f : Mathf.Min(tm.TickRateMultiplier, 3f);
            clock += dt * rate;
            return clock;
        }

        // True space (what the camera sees) into drawn space (the view matrix squashes Y).
        private static Vector3 Drawn(Vector3 p)
        {
            return new Vector3(p.x, p.y / View3D.Squash, p.z);
        }

        private static void Setup()
        {
            if (mesh != null)
            {
                return;
            }
            System.Random rnd = new System.Random(4471);
            seedX = new float[MaxDrops];
            seedZ = new float[MaxDrops];
            seedY = new float[MaxDrops];
            seedSpeed = new float[MaxDrops];
            for (int i = 0; i < MaxDrops; i++)
            {
                seedX[i] = (float)rnd.NextDouble();
                seedZ[i] = (float)rnd.NextDouble();
                seedY[i] = (float)rnd.NextDouble();
                seedSpeed[i] = 0.8f + 0.4f * (float)rnd.NextDouble();
            }
            verts = new Vector3[MaxDrops * 4];
            int[] tris = new int[MaxDrops * 6];
            Color32[] cols = new Color32[MaxDrops * 4];
            for (int i = 0; i < MaxDrops; i++)
            {
                int v = i * 4, k = i * 6;
                tris[k] = v; tris[k + 1] = v + 1; tris[k + 2] = v + 2;
                tris[k + 3] = v; tris[k + 4] = v + 2; tris[k + 5] = v + 3;
                // Rain streaks fade toward their top end.
                cols[v] = cols[v + 3] = new Color32(255, 255, 255, 255);
                cols[v + 1] = cols[v + 2] = new Color32(255, 255, 255, 90);
            }
            mesh = new Mesh();
            mesh.vertices = verts;
            mesh.colors32 = cols;
            mesh.SetTriangles(tris, 0);
        }
    }
}
