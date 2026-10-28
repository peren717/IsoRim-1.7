using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Weapons as their 3D models (setting "Weapons as 3D models", 2026-10-27): a weapon with a model (ModelLibrary, bound by
    // IsoRim or another mod) shows it everywhere: lying on the ground or a shelf (Forms), on an outfit stand, held by a mech and
    // carried or aimed by a 3D figure (FigureWeapons). A weapon without one keeps its sprite made solid (SpriteSolids).
    //
    // The figure's weapon is posed as the game draws its sprite (FigureWeapons: the slab of the sprite, placed by the game's
    // matrix). The model goes where that slab goes, through a hold frame (Hold): the model laid into the slab's space so that
    // its outline covers the sprite's, as long as the sprite's drawn length. Worked out once per model and sprite:
    //   - the sprite's long axis: the turn of its opaque pixels (at the drawn size) with the narrowest width across;
    //   - each of the 24 square turns of the model, its longest side along that axis and scaled to the sprite's length, its
    //     outline drawn over the sprite's: the turn whose outline covers the sprite best (overlap over union) wins; a small
    //     bonus for the model's front (+x, how IsoRim's weapon models are built) toward the art's front (the way the game
    //     points the sprite: +u turned back by equippedAngleOffset). A model built lying (a crossbow with its limbs out
    //     sideways) is turned up the way its sprite shows it.
    // The same frame gives the model's barrel end (GunEnd: where its shots start) and its butt (what rests at the shoulder).
    public static class WeaponModels
    {
        private const int N = 64;            // the sprite's mask, N x N
        private const float FrontBonus = 0.08f;   // the model's front (+x) toward the art's front
        private const float UpBonus = 0.2f;       // the model's top (+y) toward the art's top
        private const float Sparse = 1.6f;        // the model laid along the sprite covers this many times its pixels: sized by area
        private const float LongAxis = 0.9f;      // a model side this share of its longest counts as long (lies along the sprite)

        // The new behaviour is on: models for weapons that have one (and the shoulder hold, the stand's carry pose).
        public static bool On
        {
            get
            {
                IsoRimSettings st = IsoRimMod.Settings;
                return st.weaponModels3D && st.models3D;
            }
        }

        // Whether this weapon def shows its model (On, a real weapon as GroundGear files them, a model bound to it).
        public static bool Uses(ThingDef def)
        {
            ModelEntry entry;
            return Uses(def, out entry);
        }

        public static bool Uses(ThingDef def, out ModelEntry entry)
        {
            entry = null;
            if (!On || def == null || def.IsApparel || !GroundGear.IsGear(def))
            {
                return false;
            }
            if (!ModelLibrary.TryGet(def, out entry) || !entry.HasModel || entry.def.flat)
            {
                return false;
            }
            // Its own model: bound to this very weapon (or its art, Api 16), not a stand-in for its category or name.
            string how = ModelLibrary.HowBound(def);
            return how == "thingDef" || (how != null && how.StartsWith("graphicPath:"));
        }

        // The model this weapon shows (its style or appearance), or null.
        public static Model ModelOf(Thing weapon)
        {
            ModelEntry entry;
            return weapon != null && Uses(weapon.def, out entry) ? entry.Pick(weapon) : null;
        }

        // The model laid into the sprite slab's space (SpriteSolids: x along the sprite, y up it, z its thickness; the unit square
        // is the drawn size: slab = (u - 0.5, v - 0.5)).
        public sealed class Hold
        {
            public Matrix4x4 slab;     // model space to slab space
            public Vector3 tip;        // the model's barrel end (GunEnd), slab space
            public Vector3 butt;       // its back end along the art's front, slab space
            public float iou;          // how well its outline covers the sprite's
            public float spriteLength; // the sprite's drawn length (cells, at the drawn size)
            public float modelLength;  // the model's longest side (model units)
            public float axis;         // the sprite's long axis, degrees from +u toward +v
            public string turn;        // which model axes went where (harness)
        }

        private static readonly Dictionary<long, Hold> holds = new Dictionary<long, Hold>();

        // The sprite's butt (no model): its back end along the art's front, slab space; per material.
        private static readonly Dictionary<int, Vector3> butts = new Dictionary<int, Vector3>();

        public static Hold HoldOf(Model m, Material mat, Vector2 drawSize, float angleOffset)
        {
            if (m == null || mat == null || mat.mainTexture == null || m.positions == null || m.positions.Length < 3)
            {
                return null;
            }
            long key = ((long)m.id << 32) ^ (uint)mat.GetInstanceID();
            Hold h;
            if (holds.TryGetValue(key, out h))
            {
                return h;
            }
            try
            {
                h = Fit(m, mat, drawSize, angleOffset);
            }
            catch (Exception e)
            {
                h = null;
                Log.ErrorOnce("[IsoRim] Fitting a weapon model to its sprite failed: " + e, 93611530);
            }
            holds[key] = h;
            return h;
        }

        // The sprite's back end along the art's front (slab space): what rests at the shoulder when there is no model.
        public static Vector3 SpriteButt(Material mat, Vector2 drawSize, float angleOffset)
        {
            Vector3 b;
            if (mat == null || mat.mainTexture == null)
            {
                return new Vector3(-0.5f, 0f, 0f);
            }
            int key = mat.GetInstanceID();
            if (butts.TryGetValue(key, out b))
            {
                return b;
            }
            b = new Vector3(-0.5f, 0f, 0f);
            bool[] mask = MaskOf(mat);
            if (mask != null)
            {
                Vector2 fwd = ArtFront(drawSize, angleOffset);
                float best = float.MaxValue;
                for (int j = 0; j < N; j++)
                {
                    for (int i = 0; i < N; i++)
                    {
                        if (mask[j * N + i])
                        {
                            best = Mathf.Min(best, Vector2.Dot(Drawn(i, j, drawSize), fwd));
                        }
                    }
                }
                if (best < float.MaxValue)
                {
                    float near = best + 1.5f / N * Mathf.Max(drawSize.x, drawSize.y);
                    Vector2 sum = Vector2.zero;
                    int count = 0;
                    for (int j = 0; j < N; j++)
                    {
                        for (int i = 0; i < N; i++)
                        {
                            if (mask[j * N + i] && Vector2.Dot(Drawn(i, j, drawSize), fwd) <= near)
                            {
                                sum += Drawn(i, j, drawSize);
                                count++;
                            }
                        }
                    }
                    Vector2 d = sum / Mathf.Max(1, count);
                    b = new Vector3(d.x / drawSize.x, d.y / drawSize.y, 0f);
                }
            }
            butts[key] = b;
            return b;
        }

        // Draws the model placed by slab (SpriteSolids' slab matrix, drawn space before the floor shift) through its hold frame.
        public static void Draw(Thing weapon, Model m, Hold h, Matrix4x4 slab)
        {
            Matrix4x4 world = slab * h.slab;
            if (Vector3.Dot(Vector3.Cross(world.GetColumn(0), world.GetColumn(1)), world.GetColumn(2)) < 0f)
            {
                world = world * Matrix4x4.Scale(new Vector3(1f, 1f, -1f));   // (a mirrored placing: its far side out, still solid)
            }
            DynamicModels.DrawMatrix(m, world, R3DTextures.ModelTint(weapon), R3DTextures.ModelSwatch(weapon));
        }

        // A weapon held level in a mech's mount or a stand of old (Patches.HeldWeapon3D): its model where SpriteSolids.DrawHeld
        // puts the sprite. False without a model or a sprite to fit it to.
        public static bool DrawHeld(Thing weapon, Vector3 hand, float aim)
        {
            Model m = ModelOf(weapon);
            Graphic g = weapon.Graphic;
            Material mat = g != null ? g.MatSingleFor(weapon) : null;
            Hold h = m != null && mat != null ? HoldOf(m, mat, g.drawSize, weapon.def.equippedAngleOffset) : null;
            if (h == null)
            {
                return false;
            }
            Draw(weapon, m, h, SpriteSolids.HeldMatrix(weapon, hand, aim, g.drawSize));
            return true;
        }

        // ------------------------------------------------------------------ fitting

        private static bool[] MaskOf(Material mat)
        {
            Color32[] px = SpritePixels.Sample(mat.mainTexture, Vector2.one, Vector2.zero, N);
            if (px == null && !SpritePixels.IsSheet(mat.mainTexture))
            {
                px = SpritePixels.GpuSample(mat.mainTexture, N);
            }
            if (px == null || px.Length < N * N)
            {
                return null;
            }
            bool[] mask = new bool[N * N];
            bool any = false;
            for (int k = 0; k < N * N; k++)
            {
                mask[k] = px[k].a > 128;
                any |= mask[k];
            }
            return any ? mask : null;
        }

        // A sprite that shows several of the thing apart (the game's grenades: a few in a heap of separate pieces): its largest
        // piece only, which one model stands for. A sprite in one piece (or with only crumbs beside it) as it is.
        private static bool[] OnePiece(bool[] mask)
        {
            int[] label = new int[N * N];
            List<int> sizes = new List<int> { 0 };
            Stack<int> todo = new Stack<int>();
            int total = 0;
            for (int k = 0; k < N * N; k++)
            {
                if (!mask[k] || label[k] != 0)
                {
                    continue;
                }
                int id = sizes.Count, count = 0;
                sizes.Add(0);
                label[k] = id;
                todo.Push(k);
                while (todo.Count > 0)
                {
                    int q = todo.Pop();
                    count++;
                    int qi = q % N, qj = q / N;
                    for (int d = 0; d < 4; d++)
                    {
                        int ni = qi + (d == 0 ? 1 : d == 1 ? -1 : 0), nj = qj + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (ni < 0 || nj < 0 || ni >= N || nj >= N)
                        {
                            continue;
                        }
                        int n = nj * N + ni;
                        if (mask[n] && label[n] == 0)
                        {
                            label[n] = id;
                            todo.Push(n);
                        }
                    }
                }
                sizes[id] = count;
                total += count;
            }
            int best = 0, big = 0;
            for (int id = 1; id < sizes.Count; id++)
            {
                if (sizes[id] > sizes[best])
                {
                    best = id;
                }
                if (sizes[id] >= total * 0.15f)
                {
                    big++;
                }
            }
            if (big < 2 || sizes[best] >= total * 0.85f)
            {
                return mask;
            }
            bool[] one = new bool[N * N];
            for (int k = 0; k < N * N; k++)
            {
                one[k] = label[k] == best;
            }
            return one;
        }

        // Pixel (i, j) (row 0 the bottom) in slab space at the drawn size, cells.
        private static Vector2 Drawn(int i, int j, Vector2 drawSize)
        {
            return new Vector2(((i + 0.5f) / N - 0.5f) * drawSize.x, ((j + 0.5f) / N - 0.5f) * drawSize.y);
        }

        // The way the game points the art (Projectiles.BarrelEnd's rule), slab space at the drawn size, unit length.
        internal static Vector2 ArtFront(Vector2 drawSize, float angleOffset)
        {
            Vector3 d3 = Quaternion.AngleAxis(-angleOffset, Vector3.up) * Vector3.right;
            Vector2 d = new Vector2(d3.x * drawSize.x, d3.z * drawSize.y);
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector2.right;
        }

        private static Hold Fit(Model m, Material mat, Vector2 drawSize, float angleOffset)
        {
            bool[] sprite = MaskOf(mat);
            if (sprite == null)
            {
                return null;
            }
            sprite = OnePiece(sprite);
            // The sprite's long axis: the narrowest width across.
            List<Vector2> pts = new List<Vector2>();
            for (int j = 0; j < N; j++)
            {
                for (int i = 0; i < N; i++)
                {
                    if (sprite[j * N + i])
                    {
                        pts.Add(Drawn(i, j, drawSize));
                    }
                }
            }
            float bestWidth = float.MaxValue, theta = 0f;
            for (int a = 0; a < 180; a++)
            {
                float r = a * Mathf.Deg2Rad;
                Vector2 perp = new Vector2(-Mathf.Sin(r), Mathf.Cos(r));
                float lo = float.MaxValue, hi = float.MinValue;
                for (int k = 0; k < pts.Count; k++)
                {
                    float d = Vector2.Dot(pts[k], perp);
                    lo = Mathf.Min(lo, d);
                    hi = Mathf.Max(hi, d);
                }
                if (hi - lo < bestWidth - 1e-5f)
                {
                    bestWidth = hi - lo;
                    theta = a;
                }
            }
            float tr = theta * Mathf.Deg2Rad;
            Vector2 along = new Vector2(Mathf.Cos(tr), Mathf.Sin(tr)), across = new Vector2(-Mathf.Sin(tr), Mathf.Cos(tr));
            float aLo = float.MaxValue, aHi = float.MinValue, pLo = float.MaxValue, pHi = float.MinValue;
            for (int k = 0; k < pts.Count; k++)
            {
                float da = Vector2.Dot(pts[k], along), dp = Vector2.Dot(pts[k], across);
                aLo = Mathf.Min(aLo, da);
                aHi = Mathf.Max(aHi, da);
                pLo = Mathf.Min(pLo, dp);
                pHi = Mathf.Max(pHi, dp);
            }
            float spriteLength = aHi - aLo;
            Vector2 centre = along * ((aLo + aHi) * 0.5f) + across * ((pLo + pHi) * 0.5f);
            Vector2 front = ArtFront(drawSize, angleOffset);
            int spriteCount = pts.Count;

            Vector3 lo3 = m.positions[0], hi3 = m.positions[0];
            for (int i = 1; i < m.positions.Length; i++)
            {
                lo3 = Vector3.Min(lo3, m.positions[i]);
                hi3 = Vector3.Max(hi3, m.positions[i]);
            }
            Vector3 ext = hi3 - lo3, mid = (lo3 + hi3) * 0.5f;
            float modelLength = Mathf.Max(ext.x, Mathf.Max(ext.y, ext.z));

            Matrix4x4 rotSprite = Matrix4x4.identity;   // the slab's x, y turned onto the sprite's axis
            rotSprite.SetColumn(0, new Vector4(along.x, along.y, 0f, 0f));
            rotSprite.SetColumn(1, new Vector4(across.x, across.y, 0f, 0f));

            bool[] drawn = new bool[N * N];
            float bestScore = float.MinValue;
            Matrix4x4 best = Matrix4x4.identity;
            float bestIou = 0f;
            int bestDrawn = 0;
            string bestTurn = "";
            int[][] perms = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
            int[] permSign = { 1, -1, -1, 1, 1, -1 };
            string[] names = { "x", "y", "z" };
            for (int pi = 0; pi < perms.Length; pi++)
            {
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        int sz = permSign[pi] * sx * sy;   // a proper turn (no mirror)
                        int[] p = perms[pi];
                        // Slab axis r takes the model's axis p[r] (times its sign).
                        Matrix4x4 R = Matrix4x4.zero;
                        R[0, p[0]] = sx;
                        R[1, p[1]] = sy;
                        R[2, p[2]] = sz;
                        R[3, 3] = 1f;
                        float lx = ext[p[0]];
                        if (lx < 1e-4f || lx < modelLength * LongAxis)
                        {
                            continue;
                        }
                        float s = spriteLength / lx;
                        Matrix4x4 place = Matrix4x4.Translate(new Vector3(centre.x, centre.y, 0f)) * rotSprite * Matrix4x4.Scale(new Vector3(s, s, s)) * R
                            * Matrix4x4.Translate(-mid);
                        Array.Clear(drawn, 0, drawn.Length);
                        Raster(m, place, drawSize, drawn);
                        int both = 0, either = 0, filled = 0;
                        for (int k = 0; k < N * N; k++)
                        {
                            if (drawn[k])
                            {
                                filled++;
                            }
                            if (drawn[k] && sprite[k])
                            {
                                both++;
                            }
                            if (drawn[k] || sprite[k])
                            {
                                either++;
                            }
                        }
                        float iou = either > 0 ? (float)both / either : 0f;
                        Vector3 mf = place.MultiplyVector(Vector3.right);
                        Vector3 mu = place.MultiplyVector(Vector3.up);
                        float score = iou + (Vector2.Dot(new Vector2(mf.x, mf.y).normalized, front) > 0.3f ? FrontBonus : 0f)
                            + (Vector2.Dot(new Vector2(mu.x, mu.y).normalized, new Vector2(-front.y, front.x)) > 0.3f ? UpBonus : 0f);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = place;
                            bestIou = iou;
                            bestDrawn = filled;
                            bestTurn = (sx > 0 ? "+" : "-") + names[p[0]] + " along, " + (sy > 0 ? "+" : "-") + names[p[1]] + " up";
                        }
                    }
                }
            }
            // A sprite much sparser than the model laid along it (a heap of a few small grenades, one model): the model sized by
            // the area it covers instead, about the sprite's middle.
            float shrink = 1f;
            if (bestDrawn > spriteCount * Sparse)
            {
                shrink = Mathf.Sqrt((float)spriteCount / bestDrawn);
                Vector3 c3 = new Vector3(centre.x, centre.y, 0f);
                best = Matrix4x4.Translate(c3) * Matrix4x4.Scale(new Vector3(shrink, shrink, shrink)) * Matrix4x4.Translate(-c3) * best;
                bestTurn += " sized by area x" + shrink.ToString("0.00");
            }
            // Into the slab's unit square (the drawn size is put back by the slab's own matrix).
            Matrix4x4 unit = Matrix4x4.Scale(new Vector3(1f / drawSize.x, 1f / drawSize.y, 1f));
            Hold h = new Hold();
            h.slab = unit * best;
            h.iou = bestIou;
            h.spriteLength = spriteLength * shrink;
            h.modelLength = modelLength;
            h.axis = theta;
            h.turn = bestTurn + " (sprite " + spriteCount + " px)";
            // The barrel end: GunEnd (the model's +x front) when the model's front came out toward the art's front, else the
            // middle of its corners furthest along the art's front; the butt the same at the back.
            Vector3 fwd3 = best.MultiplyVector(Vector3.right);
            Vector3 tip = Vector3.Dot(new Vector2(fwd3.x, fwd3.y).normalized, front) > 0.3f ? best.MultiplyPoint3x4(Projectiles.GunEnd(m)) : Extreme(m, best, front, true);
            h.tip = unit.MultiplyPoint3x4(tip);
            h.butt = unit.MultiplyPoint3x4(Extreme(m, best, front, false));
            return h;
        }

        // The middle of the model's corners (placed in slab space at the drawn size) furthest along dir (or against it).
        private static Vector3 Extreme(Model m, Matrix4x4 place, Vector2 dir, bool forward)
        {
            float sign = forward ? 1f : -1f;
            float best = float.MinValue;
            for (int i = 0; i < m.positions.Length; i++)
            {
                Vector3 q = place.MultiplyPoint3x4(m.positions[i]);
                best = Mathf.Max(best, sign * (q.x * dir.x + q.y * dir.y));
            }
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < m.positions.Length; i++)
            {
                Vector3 q = place.MultiplyPoint3x4(m.positions[i]);
                if (sign * (q.x * dir.x + q.y * dir.y) >= best - 0.02f)
                {
                    sum += q;
                    n++;
                }
            }
            return sum / Mathf.Max(1, n);
        }

        // The model's triangles (placed in slab space at the drawn size) filled into the N x N grid of the sprite's pixels.
        private static void Raster(Model m, Matrix4x4 place, Vector2 drawSize, bool[] grid)
        {
            Vector3[] p = m.positions;
            for (int t = 0; t + 2 < p.Length; t += 3)
            {
                Vector2 a = Grid(place.MultiplyPoint3x4(p[t]), drawSize);
                Vector2 b = Grid(place.MultiplyPoint3x4(p[t + 1]), drawSize);
                Vector2 c = Grid(place.MultiplyPoint3x4(p[t + 2]), drawSize);
                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
                int x1 = Mathf.Min(N - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                int y1 = Mathf.Min(N - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
                float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                if (Mathf.Abs(area) < 1e-6f)
                {
                    continue;
                }
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        Vector2 q = new Vector2(x + 0.5f, y + 0.5f);
                        float w0 = ((b.x - q.x) * (c.y - q.y) - (b.y - q.y) * (c.x - q.x)) / area;
                        float w1 = ((c.x - q.x) * (a.y - q.y) - (c.y - q.y) * (a.x - q.x)) / area;
                        float w2 = 1f - w0 - w1;
                        if (w0 >= -1e-4f && w1 >= -1e-4f && w2 >= -1e-4f)
                        {
                            grid[y * N + x] = true;
                        }
                    }
                }
            }
        }

        // A point in slab space (drawn size) in grid units (pixel centres at i + 0.5).
        private static Vector2 Grid(Vector3 q, Vector2 drawSize)
        {
            return new Vector2((q.x / drawSize.x + 0.5f) * N, (q.y / drawSize.y + 0.5f) * N);
        }

        // ------------------------------------------------------------------ harness

        // For the harness (weaponsizes): every weapon with a model, its fit to its sprite and its size held against lying down.
        public static string SizeReport(float picture)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("def\tmodel\tturn\tiou\tspriteLen\theldLen\tgroundLen\tratio\n");
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                ModelEntry entry;
                if (!Uses(def, out entry) || def.graphicData == null)
                {
                    continue;
                }
                Model m = entry.Pick(null);
                Graphic g = def.graphicData.Graphic;
                Material mat = g != null ? g.MatSingle : null;
                Hold h = HoldOf(m, mat, g != null ? g.drawSize : Vector2.one, def.equippedAngleOffset);
                if (h == null)
                {
                    sb.Append(def.defName).Append("\t?\tno fit\n");
                    continue;
                }
                float held = h.spriteLength * picture;
                float ground = h.modelLength * entry.def.scale;
                sb.Append(def.defName).Append('\t').Append(entry.def.model ?? "?").Append('\t').Append(h.turn).Append('\t').Append(h.iou.ToString("0.00"))
                    .Append('\t').Append(h.spriteLength.ToString("0.000")).Append('\t').Append(held.ToString("0.000")).Append('\t').Append(ground.ToString("0.000"))
                    .Append('\t').Append((held / Mathf.Max(1e-4f, ground)).ToString("0.00")).Append('\n');
            }
            return sb.ToString();
        }
    }
}
