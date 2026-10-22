using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Hair (and beards) shown together with headgear are tucked inside it instead of poking through
    // (isorim-hair-under-hat-research.md). Done once on the CPU when a figure's head is put together
    // (People.Merged), from the drawn headgear's own triangles, so it works for every hat, helmet and mask
    // model, other packs' bound models too. Nothing per frame, no shader.
    //
    // In head-model space, centre C: for each hair corner p a ray runs from the head's surface (an ellipsoid
    // round C) out toward p. If the headgear's surface lies between the head and p, p moves back along the
    // ray to just inside that surface. The hair is first split into smaller faces (all of them alike, so no
    // cracks), and faces whose three corners all moved are left out. Cached by (hair model, headgear model).
    internal static class HairTuck
    {
        private static readonly Vector3 C = new Vector3(0f, 1.0f, 0f);
        private static readonly Vector3 HeadHalf = new Vector3(0.236f, 0.25f, 0.217f);   // person_head round C
        private const float Depth = 0.045f;      // how far inside the headgear a tucked corner goes
        private const float MaxEdge = 0.06f;     // faces are split until no edge is longer (at most MaxLevels)
        private const int MaxLevels = 2;
        private const float Eps = 1e-6f;

        private static readonly Dictionary<long, Model> cache = new Dictionary<long, Model>();

        // For the harness: tucks worked out so far and the time they took.
        internal static int Built;
        internal static double BuiltMs;
        internal static bool Off;                                  // harness: hair drawn whole, as before
        internal static Action<Model, Model, Model, double> OnBuilt;   // harness: (hair, covering, result, ms)

        // The hair as drawn under all the given coverings (null ones skipped); the hair itself where nothing covers it.
        public static Model Under(Model hair, Model a, Model b, Model c)
        {
            if (hair == null || Off)
            {
                return hair;
            }
            hair = Tucked(hair, a);
            hair = Tucked(hair, b);
            return Tucked(hair, c);
        }

        public static Model Tucked(Model hair, Model cover)
        {
            if (hair == null || cover == null || hair.positions == null || cover.positions == null || hair == cover)
            {
                return hair;
            }
            long key = ((long)hair.id << 32) | (uint)cover.id;
            Model m;
            if (cache.TryGetValue(key, out m))
            {
                return m;
            }
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                m = Build(hair, cover) ?? hair;
            }
            catch (Exception e)
            {
                m = hair;
                Log.ErrorOnce("[IsoRim] Tucking hair under headgear failed: " + e, 93611071);
            }
            sw.Stop();
            Built++;
            BuiltMs += sw.Elapsed.TotalMilliseconds;
            cache[key] = m;
            if (OnBuilt != null)
            {
                OnBuilt(hair, cover, m, sw.Elapsed.TotalMilliseconds);
            }
            return m;
        }

        // Null when the covering does not reach the hair (the hair is drawn as it is).
        private static Model Build(Model hair, Model cover)
        {
            Bounds hb = Box(hair.positions);
            Bounds cb = Box(cover.positions);
            if (!hb.Intersects(cb))
            {
                return null;
            }
            // Only the covering's faces that can be met: those whose box meets the hair's box.
            List<Vector3> tris = new List<Vector3>();
            Vector3[] cp = cover.positions;
            for (int i = 0; i + 2 < cp.Length; i += 3)
            {
                Vector3 a = cp[i], b = cp[i + 1], c = cp[i + 2];
                Vector3 lo = Vector3.Min(a, Vector3.Min(b, c)), hi = Vector3.Max(a, Vector3.Max(b, c));
                if (hi.x < hb.min.x || lo.x > hb.max.x || hi.y < hb.min.y || lo.y > hb.max.y || hi.z < hb.min.z || lo.z > hb.max.z)
                {
                    continue;
                }
                tris.Add(a);
                tris.Add(b);
                tris.Add(c);
            }
            if (tris.Count == 0)
            {
                return null;
            }
            Vector3[] ct = tris.ToArray();
            Bounds tb = Box(ct);

            int level;
            Model s = Split(hair, out level);
            int n = s.positions.Length;
            Vector3[] moved = new Vector3[n];
            bool[] did = new bool[n];
            Dictionary<Vector3, Vector3> done = new Dictionary<Vector3, Vector3>();   // the same corner in many faces: one ray
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = s.positions[i];
                Vector3 q;
                if (!done.TryGetValue(p, out q))
                {
                    q = Tuck(p, ct, tb);
                    done[p] = q;
                }
                moved[i] = q;
                did[i] = q != p;
                any |= did[i];
            }
            if (!any)
            {
                return null;
            }
            // A source face none of whose split corners moved is kept whole (its split corners all lie on its own
            // plane, so its neighbours' split edges meet it without gaps): only faces near the covering grow.
            int block = 3 << (2 * level);   // corners of the split faces made from one source face
            List<int> keep = new List<int>(n);
            for (int b = 0; b + block <= n; b += block)
            {
                bool moves = false;
                for (int i = b; i < b + block && !moves; i++)
                {
                    moves = did[i];
                }
                if (!moves)
                {
                    int src = b / block * 3;
                    keep.Add(~src);
                    keep.Add(~(src + 1));
                    keep.Add(~(src + 2));
                    continue;
                }
                for (int i = b; i + 2 < b + block; i += 3)
                {
                    if (!(did[i] && did[i + 1] && did[i + 2]))
                    {
                        keep.Add(i);
                        keep.Add(i + 1);
                        keep.Add(i + 2);
                    }
                }
            }
            return Pick(hair, s, moved, keep);
        }

        // p moved to just inside the first covering surface between the head and p; p itself if none is.
        private static Vector3 Tuck(Vector3 p, Vector3[] ct, Bounds tb)
        {
            Vector3 v = p - C;
            float len = v.magnitude;
            if (len < Eps)
            {
                return p;
            }
            Vector3 d = v / len;
            // Where the ray leaves the head's ellipsoid.
            float ex = d.x / HeadHalf.x, ey = d.y / HeadHalf.y, ez = d.z / HeadHalf.z;
            float t0 = 1f / Mathf.Sqrt(ex * ex + ey * ey + ez * ez);
            if (len <= t0)
            {
                return p;   // inside the head
            }
            Vector3 s = C + d * t0;
            Vector3 lo = Vector3.Min(s, p), hi = Vector3.Max(s, p);
            if (hi.x < tb.min.x || lo.x > tb.max.x || hi.y < tb.min.y || lo.y > tb.max.y || hi.z < tb.min.z || lo.z > tb.max.z)
            {
                return p;
            }
            float best = len;
            for (int i = 0; i + 2 < ct.Length; i += 3)
            {
                float t;
                if (Hit(C, d, ct[i], ct[i + 1], ct[i + 2], out t) && t > t0 && t < best)
                {
                    best = t;
                }
            }
            if (best >= len)
            {
                return p;
            }
            return C + d * Mathf.Max(best - Depth, 0.5f * t0);
        }

        // Moller-Trumbore, both sides.
        private static bool Hit(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 h = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, h);
            if (det > -1e-9f && det < 1e-9f)
            {
                return false;
            }
            float f = 1f / det;
            Vector3 s = o - a;
            float u = f * Vector3.Dot(s, h);
            if (u < 0f || u > 1f)
            {
                return false;
            }
            Vector3 q = Vector3.Cross(s, e1);
            float w = f * Vector3.Dot(d, q);
            if (w < 0f || u + w > 1f)
            {
                return false;
            }
            t = f * Vector3.Dot(e2, q);
            return true;
        }

        // The hair with every face split 1-to-4 as often as needed (at most MaxLevels) for edges of MaxEdge or less.
        private static Model Split(Model m, out int level)
        {
            for (level = 0; level < MaxLevels && LongestEdge(m.positions) > MaxEdge; level++)
            {
                m = SplitOnce(m);
            }
            return m;
        }

        private static float LongestEdge(Vector3[] p)
        {
            float best = 0f;
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                best = Mathf.Max(best, Mathf.Max((p[i] - p[i + 1]).sqrMagnitude, Mathf.Max((p[i + 1] - p[i + 2]).sqrMagnitude, (p[i + 2] - p[i]).sqrMagnitude)));
            }
            return Mathf.Sqrt(best);
        }

        // Corners a, b, c (0, 1, 2) and the edge middles ab, bc, ca (3, 4, 5) as four faces, each wound as the source.
        private static readonly int[] Sub = { 0, 3, 5, 3, 1, 4, 5, 4, 2, 3, 4, 5 };

        private static Model SplitOnce(Model m)
        {
            int n = m.positions.Length / 3 * 3;
            int outN = n * 4;
            Model o = Blank(m, outN);
            Vector3[] pos = new Vector3[6];
            Vector3[] nor = new Vector3[6];
            Vector3[] hul = new Vector3[6];
            Vector2[] uv = new Vector2[6];
            Color[] col = new Color[6];
            int w = 0;
            for (int i = 0; i < n; i += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    pos[k] = m.positions[i + k];
                    col[k] = m.colors[i + k];
                    if (m.normals != null) nor[k] = m.normals[i + k];
                    if (m.hull != null) hul[k] = m.hull[i + k];
                    if (m.uvs != null) uv[k] = m.uvs[i + k];
                }
                for (int k = 0; k < 3; k++)
                {
                    int a = k, b = (k + 1) % 3;
                    pos[3 + k] = (pos[a] + pos[b]) * 0.5f;
                    col[3 + k] = Color.Lerp(col[a], col[b], 0.5f);
                    if (m.normals != null) nor[3 + k] = (nor[a] + nor[b]).normalized;
                    if (m.hull != null) hul[3 + k] = HullMid(hul[a], hul[b]);
                    if (m.uvs != null) uv[3 + k] = (uv[a] + uv[b]) * 0.5f;
                }
                for (int j = 0; j < Sub.Length; j++, w++)
                {
                    int c = Sub[j];
                    o.positions[w] = pos[c];
                    o.colors[w] = col[c];
                    if (o.normals != null) o.normals[w] = nor[c];
                    if (o.hull != null) o.hull[w] = hul[c];
                    if (o.uvs != null) o.uvs[w] = uv[c];
                    // Flags are the face's: its first corner's, as BuildMerged reads them.
                    o.tinted[w] = m.tinted[i + (c < 3 ? c : 0)];
                    if (o.material != null) o.material[w] = m.material[i];
                    if (o.noOutline != null) o.noOutline[w] = m.noOutline[i + (c < 3 ? c : 0)];
                }
            }
            Finish(o);
            return o;
        }

        // The middle of two outline offsets: the mean direction, at their mean length.
        private static Vector3 HullMid(Vector3 a, Vector3 b)
        {
            Vector3 s = a + b;
            float len = (a.magnitude + b.magnitude) * 0.5f;
            return s.sqrMagnitude > 1e-12f ? s.normalized * len : a;
        }

        // The faces listed in keep (corner indices, three at a time): i a corner of the split model at its moved
        // position, ~i a corner of the source model as it is.
        private static Model Pick(Model src, Model split, Vector3[] moved, List<int> keep)
        {
            Model o = Blank(split, keep.Count);
            for (int w = 0; w < keep.Count; w++)
            {
                int i = keep[w];
                Model m = split;
                if (i < 0)
                {
                    i = ~i;
                    m = src;
                }
                o.positions[w] = m == split ? moved[i] : m.positions[i];
                o.colors[w] = m.colors[i];
                o.tinted[w] = m.tinted[i];
                if (o.normals != null) o.normals[w] = m.normals[i];
                if (o.hull != null) o.hull[w] = m.hull[i];
                if (o.uvs != null) o.uvs[w] = m.uvs[i];
                if (o.material != null) o.material[w] = m.material[i];
                if (o.noOutline != null) o.noOutline[w] = m.noOutline[i];
            }
            Finish(o);
            return o;
        }

        private static Model Blank(Model m, int n)
        {
            Model o = new Model();
            o.positions = new Vector3[n];
            o.colors = new Color[n];
            o.tinted = new bool[n];
            o.normals = m.normals != null ? new Vector3[n] : null;
            o.hull = m.hull != null ? new Vector3[n] : null;
            o.uvs = m.uvs != null ? new Vector2[n] : null;
            o.material = m.material != null ? new int[n] : null;
            o.noOutline = m.noOutline != null ? new bool[n] : null;
            o.materials = m.materials;
            o.id = ObjLoader.NewId();
            return o;
        }

        private static void Finish(Model o)
        {
            if (o.positions.Length == 0)
            {
                return;
            }
            Bounds b = Box(o.positions);
            o.maxY = b.max.y;
            o.minX = b.min.x;
            o.maxX = b.max.x;
            o.minZ = b.min.z;
            o.maxZ = b.max.z;
            o.halfDepth = Mathf.Max(Mathf.Abs(b.min.z), Mathf.Abs(b.max.z));
        }

        private static Bounds Box(Vector3[] p)
        {
            if (p.Length == 0)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }
            Vector3 lo = p[0], hi = p[0];
            for (int i = 1; i < p.Length; i++)
            {
                lo = Vector3.Min(lo, p[i]);
                hi = Vector3.Max(hi, p[i]);
            }
            Bounds b = new Bounds();
            b.SetMinMax(lo, hi);
            return b;
        }
    }
}
