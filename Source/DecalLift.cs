using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace IsoRim
{
    // Keeps plain faces off the lifted textured layer. Every textured face (map_Kd) is drawn ModelBaker.FaceLift out along its
    // normal, with another shader, in a later queue; plain faces stay where they are. So a plain decal modelled a hair over a
    // textured face lands on that lifted layer and the two fight in the depth buffer: fine hatching that shimmers as the camera
    // moves. Measured in engine (2026-11-05, ladder of decals at known gaps, tools\zfight_scan.py): the fight window is a true
    // gap of 0.0025 to 0.0046. Under it the textured face simply wins, steadily (the plain face is buried, no shimmer, left
    // alone). Faces of the same kind fight only when exactly coplanar, which no lift can settle: also left alone.
    //
    // The one mechanism for this: every model file (ObjLoader.Load) and every model whose size is baked into its corners
    // (ObjLoader.Transformed, where FaceLift stays 0.004 but the gaps scale) goes through Apply, before its outline is worked
    // out. All other models are made from these, or have no textured faces over plain ones of their own; every writer of faces
    // (ModelBaker.Write, DynamicModels.Build) reads the moved corners, so nothing downstream knows of this.
    //
    // A plain face in the window moves Lift out along its normal, to a gap of at least 0.0085: 0.0045 clear of the lifted layer
    // where the window reaches only 0.0006 past it. The lift and the gap are both scaled when a model is drawn larger or smaller
    // (Baking.Place, a mesh's matrix), so the 0.0045 only shrinks with the model, and still clears the window at a fifth of its
    // size; a decal moves at most 1.3 pixels at the closest zoom.
    //
    // The corners of a model are held per triangle, so a face can move without its neighbours. What has to go with it:
    //   - plain faces of the same plane joined to it at a corner, or the panel would step;
    //   - faces lying over it, within the distance it moves, or it would come up through them (a textured one is drawn
    //     FaceLift further out, which counts);
    //   - the corners of side faces it shares a corner with: they stretch, so a raised plate stays closed.
    // Faces lying the same way as the moved one stay put when they only touch it. Corners where moved faces of different
    // directions meet take the mitre, as the outline does, so each face still moves by Lift.
    public static class DecalLift
    {
        public const float Lift = 0.006f;
        internal const double FightLow = 0.0025, FightHigh = 0.0046;   // the fight window, a true gap (tools\zfight_scan.py PT_LO, PT_HI)
        private const double WindowLow = FightLow - 1e-5;   // the margin is for corners written with four decimals
        private const double WindowHigh = FightHigh + 1e-5;
        // The true gap a face drawn over a surface that may carry a textured layer keeps from that surface: the smallest one a
        // lifted plain face gets (FightLow + Lift = 0.0085), 0.0045 clear of the textured layer (ModelBaker.FaceLift) and past
        // its fight window. (Api 75 review) Api 10 apparel prints start there (ApparelDecals), so they never fight a garment's
        // textured faces.
        internal const float Clear = (float)(FightLow + Lift);
        private const double SamePlane = 1e-4;
        private const double MinOverlap = 1e-6;            // cells², as the scan
        private const float MitreLimit = 1.5f;

        // Totals since the game started, for the load time check (read by the test harness).
        public static int ModelsLooked, ModelsChanged, FacesMoved, SidesStretched;
        public static long Ticks;
        public static double Milliseconds
        {
            get { return Ticks * 1000.0 / Stopwatch.Frequency; }
        }

        private class Plane
        {
            public Vector3 sum;
            public Vector3 u, w;
            public readonly List<int> faces = new List<int>();
            public bool textured, plain;
            public int[] byOffset;   // faces, nearest the back of the normal first
        }

        // Moves the plain faces of a model in the window (positions, normals: three per triangle, as a Model's). Changes
        // nothing if anything goes wrong.
        public static void Apply(Vector3[] positions, Vector3[] normals, int[] material, ModelMaterial[] materials)
        {
            if (positions == null || normals == null || material == null || materials == null || positions.Length < 6)
            {
                return;
            }
            long start = Stopwatch.GetTimestamp();
            try
            {
                Run(positions, normals, material, materials);
            }
            catch (Exception e)
            {
                Verse.Log.ErrorOnce("[IsoRim] Lifting a model's decals failed: " + e, 93611071);
            }
            Interlocked.Add(ref Ticks, Stopwatch.GetTimestamp() - start);
        }

        private static void Run(Vector3[] pos, Vector3[] nor, int[] material, ModelMaterial[] materials)
        {
            int faces = pos.Length / 3;
            bool[] tex = new bool[faces];
            bool anyTex = false, anyPlain = false;
            for (int f = 0; f < faces; f++)
            {
                tex[f] = materials[material[f * 3]].texture != null;
                anyTex |= tex[f];
                anyPlain |= !tex[f];
            }
            if (!anyTex || !anyPlain)
            {
                return;
            }
            Interlocked.Increment(ref ModelsLooked);

            // Faces by direction: only directions holding both kinds matter.
            Dictionary<long, Plane> byNormal = new Dictionary<long, Plane>();
            Plane[] planeOf = new Plane[faces];
            for (int f = 0; f < faces; f++)
            {
                Vector3 n = nor[f * 3];
                if (n.sqrMagnitude < 0.5f)
                {
                    continue;
                }
                long key = ((long)(Math.Round(n.x * 200.0) + 512) << 40) | ((long)(Math.Round(n.y * 200.0) + 512) << 20) | (long)(Math.Round(n.z * 200.0) + 512);
                Plane p;
                if (!byNormal.TryGetValue(key, out p))
                {
                    p = new Plane();
                    byNormal[key] = p;
                }
                p.faces.Add(f);
                p.sum += n;
                p.textured |= tex[f];
                p.plain |= !tex[f];
                planeOf[f] = p;
            }

            double[] off = null;
            Vector2[] flat = null;
            List<int> work = null;
            bool[] moved = null;
            foreach (Plane p in byNormal.Values)
            {
                if (!p.textured || !p.plain)
                {
                    continue;
                }
                if (off == null)
                {
                    off = new double[faces];
                    flat = new Vector2[faces * 3];
                    work = new List<int>();
                    moved = new bool[faces];
                }
                Prepare(p, pos, off, flat);
                int[] order = p.byOffset;
                for (int a = 0; a < order.Length; a++)
                {
                    int f = order[a];
                    if (tex[f])
                    {
                        continue;
                    }
                    for (int b = a - 1; b >= 0 && off[f] - off[order[b]] <= WindowHigh; b--)
                    {
                        int t = order[b];
                        if (tex[t] && off[f] - off[t] >= WindowLow && Overlap(flat, f, t))
                        {
                            moved[f] = true;
                            work.Add(f);
                            break;
                        }
                    }
                }
            }
            if (work == null || work.Count == 0)
            {
                return;
            }

            // Corners by place, as the outline welds them.
            Dictionary<Vector3Int, List<int>> at = new Dictionary<Vector3Int, List<int>>();
            for (int i = 0; i < pos.Length; i++)
            {
                Vector3Int key = ObjLoader.HullKey(pos[i]);
                List<int> l;
                if (!at.TryGetValue(key, out l))
                {
                    l = new List<int>(6);
                    at[key] = l;
                }
                l.Add(i);
            }

            // What goes with them.
            while (work.Count > 0)
            {
                int f = work[work.Count - 1];
                work.RemoveAt(work.Count - 1);
                Plane p = planeOf[f];
                for (int c = 0; c < 3; c++)
                {
                    foreach (int corner in at[ObjLoader.HullKey(pos[f * 3 + c])])
                    {
                        int q = corner / 3;
                        if (!moved[q] && !tex[q] && planeOf[q] == p && Math.Abs(off[q] - off[f]) < SamePlane)
                        {
                            moved[q] = true;
                            work.Add(q);
                        }
                    }
                }
                int[] order = p.byOffset;
                for (int b = FirstAbove(order, off, off[f] + SamePlane); b < order.Length; b++)
                {
                    int q = order[b];
                    double gap = off[q] - off[f];
                    if (gap > Lift + SamePlane)
                    {
                        break;
                    }
                    if (!moved[q] && gap <= Lift + SamePlane - (tex[q] ? ModelBaker.FaceLift : 0f) && Overlap(flat, f, q))
                    {
                        moved[q] = true;
                        work.Add(q);
                    }
                }
            }

            // Every corner place of a moved face moves by the mitre of the moved faces there; the moved list first, so that a
            // failure above leaves the model as it was.
            int faceCount = 0;
            List<Vector3> directions = new List<Vector3>(4);
            HashSet<Vector3Int> done = new HashSet<Vector3Int>();
            List<KeyValuePair<int, Vector3>> shifts = new List<KeyValuePair<int, Vector3>>();
            HashSet<int> stretched = new HashSet<int>();
            for (int f = 0; f < faces; f++)
            {
                if (!moved[f])
                {
                    continue;
                }
                faceCount++;
                for (int c = 0; c < 3; c++)
                {
                    Vector3Int key = ObjLoader.HullKey(pos[f * 3 + c]);
                    if (!done.Add(key))
                    {
                        continue;
                    }
                    List<int> corners = at[key];
                    directions.Clear();
                    foreach (int corner in corners)
                    {
                        if (moved[corner / 3])
                        {
                            AddDirection(directions, nor[corner]);
                        }
                    }
                    Vector3 d;
                    if (!ObjLoader.ShortestMitre(directions, out d))
                    {
                        d = nor[f * 3];
                    }
                    if (d.magnitude > MitreLimit)
                    {
                        d = d.normalized * MitreLimit;
                    }
                    Vector3 along = d.normalized;
                    foreach (int corner in corners)
                    {
                        int q = corner / 3;
                        if (moved[q])
                        {
                            shifts.Add(new KeyValuePair<int, Vector3>(corner, d * Lift));
                        }
                        else if (Mathf.Abs(Vector3.Dot(nor[corner], along)) < 0.9f)
                        {
                            shifts.Add(new KeyValuePair<int, Vector3>(corner, d * Lift));
                            stretched.Add(q);
                        }
                    }
                }
            }
            foreach (KeyValuePair<int, Vector3> s in shifts)
            {
                pos[s.Key] += s.Value;
            }
            Interlocked.Increment(ref ModelsChanged);
            Interlocked.Add(ref FacesMoved, faceCount);
            Interlocked.Add(ref SidesStretched, stretched.Count);
        }

        private static void AddDirection(List<Vector3> list, Vector3 n)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (Vector3.Dot(list[i], n) > 0.999f)
                {
                    return;
                }
            }
            list.Add(n);
        }

        // Each face's distance along the direction and its corners laid flat, its faces sorted by that distance.
        private static void Prepare(Plane p, Vector3[] pos, double[] off, Vector2[] flat)
        {
            Vector3 n = p.sum.normalized;
            Vector3 u = Vector3.Cross(n, Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 w = Vector3.Cross(n, u);
            foreach (int f in p.faces)
            {
                Vector3 a = pos[f * 3], b = pos[f * 3 + 1], c = pos[f * 3 + 2];
                off[f] = ((double)a.x + b.x + c.x) / 3.0 * n.x + ((double)a.y + b.y + c.y) / 3.0 * n.y + ((double)a.z + b.z + c.z) / 3.0 * n.z;
                flat[f * 3] = new Vector2(Vector3.Dot(a, u), Vector3.Dot(a, w));
                flat[f * 3 + 1] = new Vector2(Vector3.Dot(b, u), Vector3.Dot(b, w));
                flat[f * 3 + 2] = new Vector2(Vector3.Dot(c, u), Vector3.Dot(c, w));
            }
            int[] order = p.faces.ToArray();
            Array.Sort(order, (x, y) => off[x].CompareTo(off[y]));
            p.byOffset = order;
        }

        private static int FirstAbove(int[] order, double[] off, double value)
        {
            int lo = 0, hi = order.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (off[order[mid]] > value)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }
            return lo;
        }

        // Whether two faces of a direction cover the same ground by more than a speck.
        private static bool Overlap(Vector2[] flat, int a, int b)
        {
            float aMinX = Mathf.Min(flat[a * 3].x, Mathf.Min(flat[a * 3 + 1].x, flat[a * 3 + 2].x));
            float aMaxX = Mathf.Max(flat[a * 3].x, Mathf.Max(flat[a * 3 + 1].x, flat[a * 3 + 2].x));
            float bMinX = Mathf.Min(flat[b * 3].x, Mathf.Min(flat[b * 3 + 1].x, flat[b * 3 + 2].x));
            float bMaxX = Mathf.Max(flat[b * 3].x, Mathf.Max(flat[b * 3 + 1].x, flat[b * 3 + 2].x));
            if (aMaxX <= bMinX || bMaxX <= aMinX)
            {
                return false;
            }
            float aMinY = Mathf.Min(flat[a * 3].y, Mathf.Min(flat[a * 3 + 1].y, flat[a * 3 + 2].y));
            float aMaxY = Mathf.Max(flat[a * 3].y, Mathf.Max(flat[a * 3 + 1].y, flat[a * 3 + 2].y));
            float bMinY = Mathf.Min(flat[b * 3].y, Mathf.Min(flat[b * 3 + 1].y, flat[b * 3 + 2].y));
            float bMaxY = Mathf.Max(flat[b * 3].y, Mathf.Max(flat[b * 3 + 1].y, flat[b * 3 + 2].y));
            if (aMaxY <= bMinY || bMaxY <= aMinY)
            {
                return false;
            }
            List<Vector2> one = Anticlockwise(flat, a);
            List<Vector2> two = Anticlockwise(flat, b);
            for (int i = 0; i < 3; i++)
            {
                one = ClipSide(one, two[i], two[(i + 1) % 3]);
                if (one.Count < 3)
                {
                    return false;
                }
            }
            return Area(one) >= MinOverlap;
        }

        private static List<Vector2> Anticlockwise(Vector2[] flat, int f)
        {
            List<Vector2> l = new List<Vector2>(3) { flat[f * 3], flat[f * 3 + 1], flat[f * 3 + 2] };
            if (Area(l) < 0)
            {
                l.Reverse();
            }
            return l;
        }

        private static List<Vector2> ClipSide(List<Vector2> poly, Vector2 p, Vector2 q)
        {
            List<Vector2> o = new List<Vector2>(poly.Count + 1);
            float ex = q.x - p.x, ey = q.y - p.y;
            for (int j = 0; j < poly.Count; j++)
            {
                Vector2 c = poly[j], e = poly[(j + 1) % poly.Count];
                float sc = ex * (c.y - p.y) - ey * (c.x - p.x);
                float se = ex * (e.y - p.y) - ey * (e.x - p.x);
                if (sc >= 0f)
                {
                    o.Add(c);
                }
                if ((sc > 0f && se < 0f) || (sc < 0f && se > 0f))
                {
                    o.Add(c + (e - c) * (sc / (sc - se)));
                }
            }
            return o;
        }

        private static double Area(List<Vector2> q)
        {
            double s = 0;
            for (int i = 0; i < q.Count; i++)
            {
                Vector2 a = q[i], b = q[(i + 1) % q.Count];
                s += (double)a.x * b.y - (double)b.x * a.y;
            }
            return s / 2;
        }
    }
}
