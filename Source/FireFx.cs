using System;
using System.Collections.Generic;
using UnityEngine;
using RimWorld;
using Verse;

namespace IsoRim
{
    // The colours of a burning fire's particles (Api 46: other mods' fires recoloured, e.g. a purple fire). flame runs from
    // the hottest (where a piece is born) to the coldest (where it dies out at the top); spark and smoke are one colour each.
    internal sealed class FireLook
    {
        public Color[] flame;
        public Color spark;
        public Color smoke;
        public List<DynamicModels.Part>[] pieces;   // the flame pieces drawn: one per colour step (FireFx.Steps), additive (or opaque if that failed)
        public List<DynamicModels.Part>[] solid;    // the same, opaque glowing
        public List<DynamicModels.Part>[] added;    // the same, additive (light added to what is behind; its tip fades out)
        public List<DynamicModels.Part>[][] levels; // the additive pieces at each FireFx.Levels strength (added = levels[0])
        public List<DynamicModels.Part>[] sparks;   // two: hot and the spark's own colour
        public List<DynamicModels.Part>[] smokes;   // FireFx.SmokeSteps: dark and thick to light and thin
    }

    // Burning fires in 3D as small low-poly particles (Effects.BurningFire draws one with Draw, every frame it is drawn):
    // slender flame tongues born at the base of the fire that rise, cool from yellow-white through orange to red and die out
    // by shrinking to nothing (never darkening), a hot heart at the foot, sparks that fly up with the wind and twinkle out,
    // and see-through smoke puffs that rise over the flame, grow and thin out. The tongues are additive light (IsoRimAdd,
    // the beams' def): overlapping they merge into one glowing mass, white-hot where most overlap; no outline, no depth.
    // Nothing is kept per fire: every particle's place, size and colour is worked out from the game time and the fire's ID
    // (stands still while paused, the same every run). Pieces of one colour are one mesh, drawn instanced (PartBatches).
    // Cost: a budget of pieces per frame (the fewer per fire the more fires are drawn), fewer for small fires and when
    // zoomed out; the game draws only the fires in view.
    internal static class FireFx
    {
        public const int Steps = 10;
        private const int Budget = 6000;           // flame pieces and sparks per frame, over all fires
        private const float FlameTall = 1.7f;     // how high the pieces rise for the sprite size 1 (cells): the mass about as tall as the game's fire sprite
        private const float BaseRadius = 0.27f;    // where the pieces are born, around the foot
        private const float PieceWide = 0.30f;     // a full-size piece, across

        private static readonly Vector3 WindDir = new Vector3(0.97f, 0f, 0.24f);   // as the game's smoke drifts

        private static FireLook standard;
        private static readonly Dictionary<ThingDef, FireLook> looks = new Dictionary<ThingDef, FireLook>();
        private static bool failed;

        // Per-frame budget: what the fires wanted last frame decides how many each gets this frame.
        private static int frameSeen = -1;
        private static int wantedThis, wantedLast;
        private static int firesThis, firesLast;
        private static float share = 1f;

        // Harness / Api.Dev: pieces, sparks and puffs drawn in the last whole frame.
        internal static int DrawnPieces, DrawnLast;
        internal static int FiresLast => firesLast;

        private static readonly Color[] StandardRamp =
        {
            new Color(1.00f, 0.95f, 0.72f),
            new Color(1.00f, 0.76f, 0.30f),
            new Color(1.00f, 0.58f, 0.13f),
            new Color(1.00f, 0.45f, 0.08f),
            new Color(1.00f, 0.35f, 0.06f),
            new Color(0.98f, 0.27f, 0.05f),
        };

        // (Api 46) A fire def's own colours.
        internal static void SetLook(ThingDef def, Color[] flame, Color spark, Color smoke)
        {
            if (def == null || flame == null || flame.Length == 0)
            {
                return;
            }
            FireLook was;
            if (looks.TryGetValue(def, out was))
            {
                Destroy(was);
            }
            looks[def] = new FireLook { flame = (Color[])flame.Clone(), spark = spark, smoke = smoke };
        }

        internal static bool HasOwnLook(ThingDef def) => def != null && looks.ContainsKey(def);

        internal static FireLook LookFor(ThingDef def)
        {
            FireLook l;
            if (def != null && looks.TryGetValue(def, out l))
            {
                return Ready(l) ? l : null;
            }
            if (standard == null)
            {
                standard = new FireLook { flame = StandardRamp, spark = new Color(1f, 0.78f, 0.30f), smoke = new Color(0.44f, 0.43f, 0.42f) };
            }
            return Ready(standard) ? standard : null;
        }

        // Its meshes made on first use (main thread).
        private static bool Ready(FireLook l)
        {
            if (l.pieces != null)
            {
                return true;
            }
            if (failed)
            {
                return false;
            }
            Material mat = R3DMaterials.Solid;
            if (mat == null)
            {
                return false;
            }
            try
            {
                Material add = AddMat;
                l.solid = new List<DynamicModels.Part>[Steps];
                l.levels = add != null ? new List<DynamicModels.Part>[Levels.Length][] : null;
                for (int lv = 0; l.levels != null && lv < Levels.Length; lv++)
                {
                    l.levels[lv] = new List<DynamicModels.Part>[Steps];
                }
                for (int i = 0; i < Steps; i++)
                {
                    Color c = Ramp(l.flame, i / (float)(Steps - 1));
                    Color hotter = Ramp(l.flame, Mathf.Max(0f, (i - 2) / (float)(Steps - 1)));
                    l.solid[i] = One(PieceMesh(Color.Lerp(c, hotter, 0.6f), c, c, c), mat);
                    if (add != null)
                    {
                        // a little less green and blue than the opaque colour: added over the ground they come out as warm as it
                        // (blue too for IsoRim's own colours; an Api 46 look keeps its blue)
                        bool warm = l == standard;
                        Color ca = new Color(c.r, c.g * 0.8f, warm ? c.b * 0.6f : c.b), ha = new Color(hotter.r, hotter.g * 0.88f, warm ? hotter.b * 0.7f : hotter.b);
                        for (int lv = 0; lv < Levels.Length; lv++)
                        {
                            float k = AddStrength * Levels[lv];
                            l.levels[lv][i] = One(PieceMesh(ha * k, ca * k, ca * (k * 0.7f), ca * (k * 0.15f)), add);
                        }
                    }
                }
                l.added = l.levels != null ? l.levels[0] : null;
                l.pieces = l.added ?? l.solid;
                Material blend = SmokeMat;
                if (blend != null)
                {
                    l.smokes = new List<DynamicModels.Part>[SmokeSteps];
                    for (int i = 0; i < SmokeSteps; i++)
                    {
                        float u = i / (float)(SmokeSteps - 1);
                        Color c = Color.Lerp(Color.Lerp(l.smoke, Color.white, 0.25f), Color.Lerp(l.smoke, Color.white, 0.6f), u);
                        l.smokes[i] = One(SmokeMesh(c, Mathf.Lerp(0.38f, 0.05f, u)), blend);
                        l.smokes[i][0].alone = true;
                    }
                }
                l.sparks = new[] { One(SparkMesh(Color.Lerp(l.spark, Color.white, 0.55f)), mat), One(SparkMesh(l.spark), mat) };
                return true;
            }
            catch (Exception e)
            {
                failed = true;
                l.pieces = null;
                Log.ErrorOnce("[IsoRim] Making the 3D fire particles failed (burning fires stay as before): " + e, 93611082);
                return false;
            }
        }

        private const float AddStrength = 0.18f;   // an additive piece's light (overlaps add up to the yellow-white middle)

        // The additive light in three strengths: over dark ground, over bright ground (light floors, some snow), and over
        // very bright ground (deep snow, white floors), so the fire stays fire-coloured (a fire on a body: the middle one).
        internal static readonly float[] Levels = { 1f, 0.7f, 0.48f };

        // Which strength a fire gets over this cell: by how bright the ground is (snow on it, or a floor's colour; natural
        // ground counts as dark). Cached per terrain.
        private static readonly Dictionary<TerrainDef, float> floorBright = new Dictionary<TerrainDef, float>();

        internal static int LevelFor(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map))
            {
                return 0;
            }
            float b = 0.3f;
            TerrainDef t = map.terrainGrid.TerrainAt(c);
            if (t != null && (t.IsFloor || t.layerable) && !t.natural)
            {
                float fb;
                if (!floorBright.TryGetValue(t, out fb))
                {
                    Color col = t.color;
                    Material mat = t.DrawMatSingle;
                    if (mat != null && mat.HasProperty("_Color"))
                    {
                        col *= mat.color;
                    }
                    fb = 0.85f * (0.3f * col.r + 0.59f * col.g + 0.11f * col.b);
                    floorBright[t] = fb;
                }
                b = fb;
                ColorDef paint = map.terrainGrid.ColorAt(c);
                if (paint != null)
                {
                    b = 0.85f * (0.3f * paint.color.r + 0.59f * paint.color.g + 0.11f * paint.color.b);
                }
            }
            float snow = map.snowGrid != null ? map.snowGrid.GetDepth(c) : 0f;
            if (snow > 0.1f)
            {
                b = Mathf.Max(b, 0.55f + 0.4f * Mathf.Clamp01(snow / 0.6f));
            }
            return b > 0.75f ? 2 : b > 0.5f ? 1 : 0;
        }

        private static Material addMat;

        // Light added to what is behind it (the beams' def, IsoRimAdd: instanced like the solid parts, after the light
        // overlay, so it glows at night too); no depth written.
        private static Material AddMat
        {
            get
            {
                if (addMat == null)
                {
                    addMat = R3DMaterials.Make(Beams.AddDef, "IsoRim_FireAdd");
                }
                return addMat;
            }
        }

        public const int SmokeSteps = 8;
        private static Material smokeMat;

        // See-through smoke, unshaded (no facets), darkened by its own cell's light like the glass (after the light overlay).
        private static Material SmokeMat
        {
            get
            {
                if (smokeMat == null)
                {
                    smokeMat = R3DMaterials.Make(R3DMaterials.BlendDef, "IsoRim_FireSmoke", 3102, R3DMaterials.LightUnlit);   // no facet shading
                    if (smokeMat != null)
                    {
                        IsoGlow.Use(smokeMat, IsoGlow.OwnCellUnmarked);
                    }
                }
                return smokeMat;
            }
        }

        // A soft smoke puff one unit across, its foot at 0: three nested round shells (an icosahedron split once, 80 faces
        // each), the outer ones fainter, so it thickens toward its middle and its edge melts away. alpha: the middle's.
        private static Mesh SmokeMesh(Color c, float alpha)
        {
            float g = (1f + Mathf.Sqrt(5f)) / 2f;
            Vector3[] p =
            {
                new Vector3(-1, g, 0), new Vector3(1, g, 0), new Vector3(-1, -g, 0), new Vector3(1, -g, 0),
                new Vector3(0, -1, g), new Vector3(0, 1, g), new Vector3(0, -1, -g), new Vector3(0, 1, -g),
                new Vector3(g, 0, -1), new Vector3(g, 0, 1), new Vector3(-g, 0, -1), new Vector3(-g, 0, 1),
            };
            int[] f = { 0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                        3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1 };
            float[] radius = { 0.5f, 0.38f, 0.24f };
            float[] share = { 0.12f, 0.22f, 0.36f };   // each shell is crossed twice through the middle
            Vector3 up = new Vector3(0f, 0.5f, 0f);
            List<Vector3> v = new List<Vector3>();
            List<Color32> col = new List<Color32>();
            for (int sh = 0; sh < radius.Length; sh++)
            {
                Color32 k = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), (byte)Mathf.Clamp(alpha * share[sh] * 255f * 2.2f, 1f, 255f));
                for (int i = 0; i < f.Length; i += 3)
                {
                    Vector3 a = p[f[i]].normalized, b = p[f[i + 1]].normalized, d = p[f[i + 2]].normalized;
                    Vector3 ab = (a + b).normalized, bd = (b + d).normalized, da = (d + a).normalized;
                    float r = radius[sh];
                    Tri(v, col, a * r + up, ab * r + up, da * r + up, k, k, k);
                    Tri(v, col, ab * r + up, b * r + up, bd * r + up, k, k, k);
                    Tri(v, col, da * r + up, bd * r + up, d * r + up, k, k, k);
                    Tri(v, col, ab * r + up, bd * r + up, da * r + up, k, k, k);
                }
            }
            return Finish(v, col, "IsoRim_FireSmoke");
        }



        private static List<DynamicModels.Part> One(Mesh mesh, Material mat)
        {
            return new List<DynamicModels.Part> { new DynamicModels.Part { mesh = mesh, material = mat } };
        }

        private static void Destroy(FireLook l)
        {
            List<List<DynamicModels.Part>[]> sets = new List<List<DynamicModels.Part>[]> { l.solid };
            if (l.levels != null)
            {
                sets.AddRange(l.levels);
            }
            foreach (List<DynamicModels.Part>[] set in sets)
            {
                for (int i = 0; set != null && i < set.Length; i++)
                {
                    DynamicModels.Destroy(set[i]);
                }
            }
            l.solid = l.added = null;
            l.levels = null;
            if (l.sparks != null)
            {
                for (int i = 0; i < l.sparks.Length; i++)
                {
                    DynamicModels.Destroy(l.sparks[i]);
                }
            }
            if (l.smokes != null)
            {
                for (int i = 0; i < l.smokes.Length; i++)
                {
                    DynamicModels.Destroy(l.smokes[i]);
                }
            }
            l.pieces = null;
            l.sparks = null;
            l.smokes = null;
        }

        // The colour at u (0 hottest .. 1 coldest) of a ramp of evenly spaced keys.
        private static Color Ramp(Color[] keys, float u)
        {
            if (keys.Length == 1)
            {
                return keys[0];
            }
            float x = Mathf.Clamp01(u) * (keys.Length - 1);
            int i = Mathf.Min(keys.Length - 2, (int)x);
            return Color.Lerp(keys[i], keys[i + 1], x - i);
        }

        // A flame piece one unit tall and across: a slender six-sided tongue, round at the foot, widest a quarter of the way
        // up and drawn out to a point; coloured foot, body, upper body and tip (an additive piece fades out toward its tip).
        private static Mesh PieceMesh(Color foot, Color body, Color upper, Color tip)
        {
            const int sides = 6;
            float[] ys = { 0.09f, 0.25f, 0.52f, 0.78f };
            float[] rs = { 0.30f, 0.50f, 0.33f, 0.13f };
            Color32[] rc = { R3DMaterials.Shaded(foot, 1f), R3DMaterials.Shaded(body, 1f), R3DMaterials.Shaded(upper, 1f), R3DMaterials.Shaded(Color.Lerp(upper, tip, 0.6f), 1f) };
            Color32 cBottom = R3DMaterials.Shaded(foot, 1f), cTop = R3DMaterials.Shaded(tip, 1f);
            Vector3[,] ring = new Vector3[ys.Length, sides];
            for (int r = 0; r < ys.Length; r++)
            {
                for (int i = 0; i < sides; i++)
                {
                    float an = (i + 0.5f * r) * Mathf.PI * 2f / sides;
                    ring[r, i] = new Vector3(Mathf.Cos(an) * rs[r], ys[r], Mathf.Sin(an) * rs[r]);
                }
            }
            Vector3 bottom = Vector3.zero, top = new Vector3(0f, 1f, 0f);
            List<Vector3> v = new List<Vector3>();
            List<Color32> col = new List<Color32>();
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                Tri(v, col, bottom, ring[0, j], ring[0, i], cBottom, rc[0], rc[0]);
                for (int r = 0; r + 1 < ys.Length; r++)
                {
                    Tri(v, col, ring[r, i], ring[r, j], ring[r + 1, i], rc[r], rc[r], rc[r + 1]);
                    Tri(v, col, ring[r, j], ring[r + 1, j], ring[r + 1, i], rc[r], rc[r + 1], rc[r + 1]);
                }
                Tri(v, col, top, ring[ys.Length - 1, i], ring[ys.Length - 1, j], cTop, rc[ys.Length - 1], rc[ys.Length - 1]);
            }
            return Finish(v, col, "IsoRim_FirePiece");
        }

        // A spark one unit across: an octahedron of one colour.
        private static Mesh SparkMesh(Color c)
        {
            Color32 k = R3DMaterials.Shaded(c, 1f);
            Vector3[] p = { new Vector3(0.5f, 0.5f, 0f), new Vector3(0f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0f, 0.5f, -0.5f) };
            Vector3 lo = Vector3.zero, hi = new Vector3(0f, 1f, 0f);
            List<Vector3> v = new List<Vector3>();
            List<Color32> col = new List<Color32>();
            for (int i = 0; i < 4; i++)
            {
                Tri(v, col, lo, p[(i + 1) % 4], p[i], k, k, k);
                Tri(v, col, hi, p[i], p[(i + 1) % 4], k, k, k);
            }
            return Finish(v, col, "IsoRim_FireSpark");
        }

        private static void Tri(List<Vector3> v, List<Color32> col, Vector3 a, Vector3 b, Vector3 c, Color32 ca, Color32 cb, Color32 cc)
        {
            v.Add(a);
            v.Add(b);
            v.Add(c);
            col.Add(ca);
            col.Add(cb);
            col.Add(cc);
        }

        // The mesh as DynamicModels makes its moving parts: clipped under the roof over where it is drawn (RoofClip tag).
        private static Mesh Finish(List<Vector3> v, List<Color32> col, string name)
        {
            Mesh m = new Mesh();
            m.name = name;
            MeshGuard.FitIndices(m, v.Count);
            m.SetVertices(v);
            m.SetColors(col);
            List<Vector2> uv = new List<Vector2>(v.Count);
            List<Vector3> tags = new List<Vector3>(v.Count);
            List<int> tris = new List<int>(v.Count);
            for (int i = 0; i < v.Count; i++)
            {
                uv.Add(Vector2.zero);
                tags.Add(RoofClip.MovingTag);
                tris.Add(i);
            }
            m.SetUVs(0, uv);
            m.SetUVs(1, tags);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            MeshGuard.Check(m, "fire particle");
            return m;
        }

        private static void Frame()
        {
            int f = Time.frameCount;
            if (f == frameSeen)
            {
                return;
            }
            frameSeen = f;
            wantedLast = wantedThis;
            firesLast = firesThis;
            DrawnLast = DrawnPieces;
            wantedThis = 0;
            firesThis = 0;
            DrawnPieces = 0;
            share = wantedLast > Budget ? Budget / (float)wantedLast : 1f;
        }

        // Fewer particles when zoomed out (they are small on screen then).
        private static float Detail()
        {
            float size = View3D.OrthoSize;
            return size <= 0f ? 1f : Mathf.Clamp(15f / size, 0.45f, 1f);
        }

        private static uint Mix(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        private static float U(uint seed, int k, int cycle, int salt)
        {
            uint h = Mix(seed ^ ((uint)k * 0x9E3779B1u) ^ ((uint)cycle * 0x85EBCA77u) ^ ((uint)salt * 0xC2B2AE3Du));
            return (h & 0xFFFFFFu) / 16777216f;
        }

        // One burning fire: foot the middle of its base (x, z in cells, y the height above the ground), s the game's sprite
        // size of it (Graphic_Flicker: fireSize / 1.2, at most 1.2), id its thing ID, t the game time in seconds, wind the
        // map's wind speed, puff the smoke model used if the see-through smoke cannot be made (none: no smoke).
        // level: the additive strength (Levels; LevelFor the ground, 2 on a body).
        internal static void Draw(Vector3 foot, float s, int id, float t, FireLook look, float wind, Model puff, int level = 0)
        {
            Frame();
            firesThis++;
            uint seed = Mix((uint)id * 2654435761u + 0x3C6EF372u);
            float big = Mathf.Clamp01(s / 1.2f);
            float detail = Detail();
            int want = Mathf.Max(4, Mathf.RoundToInt(Mathf.Lerp(10f, 32f, big) * detail));
            int sparkWant = s > 0.35f ? Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(1f, 4f, big) * detail)) : 0;
            wantedThis += want + sparkWant;
            int n = Mathf.Max(4, Mathf.RoundToInt(want * share));
            int ns = Mathf.RoundToInt(sparkWant * share);

            float H = s * FlameTall * (0.9f + 0.2f * U(seed, 0, 0, 1));
            float R = s * BaseRadius;
            float W = s * PieceWide;
            t *= 0.92f + 0.16f * U(seed, 0, 0, 2);   // its own pace
            float sq = View3D.Squash;
            float lean = (0.10f + 0.18f * wind) * H;

            List<DynamicModels.Part>[] set = look.levels != null ? look.levels[Mathf.Clamp(level, 0, Levels.Length - 1)] : look.solid;   // opaque: the additive def is missing
            // Flame pieces; the first three are the heart: short-lived, low, wide and hot.
            for (int k = 0; k < n; k++)
            {
                bool heart = k < 3;
                float life = heart ? 0.30f + 0.12f * U(seed, k, 0, 3) : (0.50f + 0.30f * U(seed, k, 0, 3)) * (0.85f + 0.3f * big);
                float x = t / life + U(seed, k, 0, 4);
                int cyc = Mathf.FloorToInt(x);
                float u = x - cyc;
                float r1 = U(seed, k, cyc, 5), r2 = U(seed, k, cyc, 6), r3 = U(seed, k, cyc, 7), r4 = U(seed, k, cyc, 8), r5 = U(seed, k, cyc, 9);
                float ang = r1 * 6.2832f;
                float out01 = Mathf.Sqrt(r2);
                float rad = R * (heart ? 0.4f : 1f) * out01;
                float pull = 1f - 0.6f * u;   // drawn in toward the middle as it rises
                float rise = heart ? 0.18f * H * u : H * (0.2f * u + 0.8f * u * u) * (0.75f + 0.5f * r3);
                float jig = 0.05f * s * (0.5f + u);
                float jx = jig * Mathf.Sin(t * 9.1f + k * 1.7f + r1 * 6f);
                float jz = jig * Mathf.Cos(t * 7.3f + k * 2.3f + r2 * 6f);
                float drift = lean * u * u;
                Vector3 p = new Vector3(
                    foot.x + Mathf.Cos(ang) * rad * pull + jx + WindDir.x * drift,
                    foot.y + rise,
                    foot.z + Mathf.Sin(ang) * rad * pull + jz + WindDir.z * drift);
                float grow = Mathf.Clamp01(u / 0.12f);
                float fade = Mathf.Pow(1f - u, 1.5f);   // dies out by shrinking to nothing, never darkening
                float w = W * (heart ? 1.5f : 0.7f + 0.6f * r4) * grow * fade * (1f + 0.3f * (1f - u));
                if (w < 0.012f)
                {
                    continue;
                }
                float tall = w * (heart ? 1.7f : 2.3f + 0.9f * (1f - u));
                float c = heart ? 0f : Mathf.Clamp01(0.12f + u * 0.85f + out01 * 0.28f + (r5 - 0.5f) * 0.15f);   // orange soon; the hot core is the heart
                int step = Mathf.Min(Steps - 1, (int)(c * (Steps - 1) + 0.5f));
                // upright, turned about its own axis only (a tilted piece breaks in the instanced squashed draw)
                Quaternion turn = Quaternion.AngleAxis(r4 * 360f, Vector3.up);
                Matrix4x4 m = Matrix4x4.TRS(new Vector3(p.x, p.y / sq, p.z), turn, new Vector3(w, tall / sq, w));
                DynamicModels.SubmitParts(set[step], m);
                DrawnPieces++;
            }

            // Sparks: now and then one flies up with the wind, twinkling, and goes out.
            for (int k = 0; k < ns; k++)
            {
                float life = 1.1f + 0.7f * U(seed, k, 0, 11);
                float x = t / life + U(seed, k, 0, 12);
                int cyc = Mathf.FloorToInt(x);
                float u = x - cyc;
                if (U(seed, k, cyc, 13) > 0.6f)
                {
                    continue;   // this round it has none
                }
                float r1 = U(seed, k, cyc, 14), r2 = U(seed, k, cyc, 15), r3 = U(seed, k, cyc, 16);
                float ang = r1 * 6.2832f;
                float spread = (0.25f + 0.5f * r2) * Mathf.Max(0.5f, s);
                float drift = (0.3f + 0.7f * wind) * u * (0.6f + 0.6f * r3);
                Vector3 p = new Vector3(
                    foot.x + Mathf.Cos(ang) * spread * u + WindDir.x * drift + 0.04f * Mathf.Sin(t * 13f + k * 3f),
                    foot.y + H * (0.5f + 1.9f * u * (0.7f + 0.6f * r3)),
                    foot.z + Mathf.Sin(ang) * spread * u + WindDir.z * drift);
                float twinkle = 0.6f + 0.4f * Mathf.Sin(t * 31f + k * 5.1f + r1 * 9f);
                float size = 0.075f * (1f - u * u) * twinkle;
                if (size < 0.015f)
                {
                    continue;
                }
                Matrix4x4 m = Matrix4x4.TRS(new Vector3(p.x, p.y / sq, p.z), Quaternion.AngleAxis(r2 * 360f, Vector3.up), new Vector3(size, size / sq, size));
                DynamicModels.SubmitParts(look.sparks[u < 0.5f ? 0 : 1], m);
                DrawnPieces++;
            }

            // Smoke: a few see-through puffs over the flame that rise, grow and thin out (fewer fires have it when many burn).
            if (s < 0.25f || look.smokes == null && puff == null)
            {
                return;
            }
            float smokeShare = firesLast > 30 ? Mathf.Max(0.12f, 30f / firesLast) : 1f;
            if (U(seed, 0, 0, 20) > smokeShare)
            {
                return;
            }
            int puffs = s > 0.8f ? 4 : 3;
            for (int k = 0; k < puffs; k++)
            {
                float life = 2.6f + 0.8f * U(seed, k, 0, 21);
                float x = t / life + (k + U(seed, k, 0, 22) * 0.5f) / puffs;
                int cyc = Mathf.FloorToInt(x);
                float u = x - cyc;
                float r1 = U(seed, k, cyc, 23), r2 = U(seed, k, cyc, 24);
                float size = Mathf.Lerp(0.16f, 0.62f, u) * (0.55f + 0.5f * big) * (0.8f + 0.4f * r2) * Mathf.Clamp01(u / 0.1f);
                if (size < 0.03f)
                {
                    continue;
                }
                float drift = (0.35f + 0.5f * wind) * u * u * 1.6f;
                float wob = t * 4.4f + k * 1.9f + r1 * 6f;
                Vector3 p = new Vector3(
                    foot.x + WindDir.x * drift + 0.08f * Mathf.Sin(wob) * (0.3f + u) + (r2 - 0.5f) * 0.2f * s,
                    foot.y + H * 0.7f + 2.2f * u,
                    foot.z + WindDir.z * drift + 0.08f * Mathf.Cos(wob * 1.3f) * (0.3f + u));
                if (look.smokes != null)
                {
                    int step = Mathf.Min(SmokeSteps - 1, (int)(u * SmokeSteps));
                    Matrix4x4 m = Matrix4x4.TRS(new Vector3(p.x, p.y / sq, p.z), Quaternion.AngleAxis(r1 * 360f + t * 15f, Vector3.up), new Vector3(size, size * 0.85f / sq, size));
                    DynamicModels.SubmitParts(look.smokes[step], m);
                }
                else
                {
                    float g = Mathf.Lerp(1.6f, 2.6f, u);
                    Color col = new Color(Mathf.Min(1f, look.smoke.r * g), Mathf.Min(1f, look.smoke.g * g), Mathf.Min(1f, look.smoke.b * g));
                    DynamicModels.DrawLocal(puff, p, r1 * 360f + t * 20f, Matrix4x4.Scale(new Vector3(size, size * 0.9f, size)), col, null);
                }
                DrawnPieces++;
            }
        }
    }
}
