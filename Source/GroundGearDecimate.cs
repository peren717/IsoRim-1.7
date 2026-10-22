using System;
using System.Collections.Generic;
using UnityEngine;

namespace IsoRim
{
    // Quadric edge collapse (Garland and Heckbert) for GroundGear's laid clothes: merges the welded shape's faces where the
    // surface is flattest until keep of them are left or the next merge would move the surface further than maxError (cells,
    // mean distance). Open edges and edges between colours are held in place. Moves the kept corners (pos, hull) and points
    // each triangle's corners at the corners they were merged into (a merged-away triangle repeats one corner).
    internal static class Decimate
    {
        private const double BorderWeight = 4.0;   // how firmly open and colour edges stay (times their squared length)

        private struct Edge
        {
            public double cost;
            public int a, b, sa, sb;
            public Vector3 to;
        }

        private static double[] q;
        private static double[] area;
        private static Vector3[] pos;
        private static int[] tris;
        private static List<int>[] around;
        private static bool[] deadTri;
        private static int[] stamp;
        private static readonly List<Edge> heap = new List<Edge>();
        private static readonly HashSet<int> near = new HashSet<int>();

        public static void Run(Vector3[] positions, Vector3[] hull, int[] triangles, int[] colorOf, float keep, float maxError)
        {
            pos = positions;
            tris = triangles;
            int nv = pos.Length, nt = tris.Length / 3;
            q = new double[nv * 10];
            area = new double[nv];
            deadTri = new bool[nt];
            stamp = new int[nv];
            bool[] dead = new bool[nv];
            around = new List<int>[nv];
            for (int v = 0; v < nv; v++)
            {
                around[v] = new List<int>(6);
            }
            int alive = 0;
            Dictionary<long, int> edgeFace = new Dictionary<long, int>();   // first face on an edge; -1 once a second face shares it
            for (int t = 0; t < nt; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                if (a == b || b == c || a == c)
                {
                    deadTri[t] = true;
                    continue;
                }
                alive++;
                around[a].Add(t);
                around[b].Add(t);
                around[c].Add(t);
                Vector3 n = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                float len = n.magnitude;
                if (len > 1e-9f)
                {
                    n /= len;
                    double d = -Vector3.Dot(n, pos[a]);
                    double w = len * 0.5;
                    AddPlane(a, n, d, w);
                    AddPlane(b, n, d, w);
                    AddPlane(c, n, d, w);
                    area[a] += w;
                    area[b] += w;
                    area[c] += w;
                }
                for (int k = 0; k < 3; k++)
                {
                    int i = tris[t * 3 + k], j = tris[t * 3 + (k + 1) % 3];
                    long key = EdgeKey(i, j);
                    int other;
                    if (!edgeFace.TryGetValue(key, out other))
                    {
                        edgeFace[key] = t;
                    }
                    else if (other >= 0)
                    {
                        if (colorOf[other] != colorOf[t])
                        {
                            Border(t, i, j);
                            Border(other, i, j);
                        }
                        edgeFace[key] = -1;
                    }
                }
            }
            foreach (KeyValuePair<long, int> e in edgeFace)
            {
                if (e.Value >= 0)
                {
                    Border(e.Value, (int)(e.Key >> 32), (int)(e.Key & 0xffffffff));   // an open edge
                }
            }

            heap.Clear();
            foreach (KeyValuePair<long, int> e in edgeFace)
            {
                Push((int)(e.Key >> 32), (int)(e.Key & 0xffffffff));
            }
            int target = Mathf.Max(4, Mathf.RoundToInt(alive * keep));
            double maxCost = (double)maxError * maxError;
            while (alive > target && heap.Count > 0)
            {
                Edge e = Pop();
                if (dead[e.a] || dead[e.b] || stamp[e.a] != e.sa || stamp[e.b] != e.sb)
                {
                    continue;
                }
                if (e.cost > maxCost)
                {
                    break;
                }
                if (Flips(e.a, e.b, e.to) || Flips(e.b, e.a, e.to))
                {
                    continue;
                }
                // b goes into a.
                List<int> ab = around[e.b];
                for (int k = 0; k < ab.Count; k++)
                {
                    int t = ab[k];
                    if (deadTri[t])
                    {
                        continue;
                    }
                    int at = t * 3;
                    if (tris[at] == e.a || tris[at + 1] == e.a || tris[at + 2] == e.a)
                    {
                        deadTri[t] = true;
                        alive--;
                        continue;
                    }
                    for (int c = 0; c < 3; c++)
                    {
                        if (tris[at + c] == e.b)
                        {
                            tris[at + c] = e.a;
                        }
                    }
                    around[e.a].Add(t);
                }
                ab.Clear();
                for (int k = 0; k < 10; k++)
                {
                    q[e.a * 10 + k] += q[e.b * 10 + k];
                }
                area[e.a] += area[e.b];
                pos[e.a] = e.to;
                hull[e.a] = (hull[e.a] + hull[e.b]) * 0.5f;
                dead[e.b] = true;
                stamp[e.a]++;

                List<int> aa = around[e.a];
                aa.RemoveAll(t => deadTri[t]);
                near.Clear();
                for (int k = 0; k < aa.Count; k++)
                {
                    int at = aa[k] * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        if (tris[at + c] != e.a)
                        {
                            near.Add(tris[at + c]);
                        }
                    }
                }
                foreach (int v in near)
                {
                    Push(e.a, v);
                }
            }
            for (int t = 0; t < nt; t++)
            {
                if (deadTri[t])
                {
                    tris[t * 3 + 1] = tris[t * 3 + 2] = tris[t * 3];
                }
            }
            heap.Clear();
            near.Clear();
            q = null;
            area = null;
            pos = null;
            tris = null;
            around = null;
            deadTri = null;
            stamp = null;
        }

        private static long EdgeKey(int i, int j)
        {
            return i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
        }

        private static void AddPlane(int v, Vector3 n, double d, double w)
        {
            int o = v * 10;
            q[o] += w * n.x * n.x;
            q[o + 1] += w * n.x * n.y;
            q[o + 2] += w * n.x * n.z;
            q[o + 3] += w * n.x * d;
            q[o + 4] += w * n.y * n.y;
            q[o + 5] += w * n.y * n.z;
            q[o + 6] += w * n.y * d;
            q[o + 7] += w * n.z * n.z;
            q[o + 8] += w * n.z * d;
            q[o + 9] += w * d * d;
        }

        // A plane through edge i-j standing up from face t: moving i or j off the edge's line costs.
        private static void Border(int t, int i, int j)
        {
            int at = t * 3;
            Vector3 face = Vector3.Cross(pos[tris[at + 1]] - pos[tris[at]], pos[tris[at + 2]] - pos[tris[at]]);
            Vector3 e = pos[j] - pos[i];
            Vector3 n = Vector3.Cross(e, face);
            float len = n.magnitude;
            if (len < 1e-12f)
            {
                return;
            }
            n /= len;
            double d = -Vector3.Dot(n, pos[i]);
            double w = BorderWeight * e.sqrMagnitude;
            AddPlane(i, n, d, w);
            AddPlane(j, n, d, w);
        }

        // The squared distance, area-weighted mean, of point p from the planes of a's and b's faces (and their held edges).
        private static double Error(int a, int b, Vector3 p)
        {
            int oa = a * 10, ob = b * 10;
            double x = p.x, y = p.y, z = p.z;
            double e = (q[oa] + q[ob]) * x * x + 2 * (q[oa + 1] + q[ob + 1]) * x * y + 2 * (q[oa + 2] + q[ob + 2]) * x * z + 2 * (q[oa + 3] + q[ob + 3]) * x
                + (q[oa + 4] + q[ob + 4]) * y * y + 2 * (q[oa + 5] + q[ob + 5]) * y * z + 2 * (q[oa + 6] + q[ob + 6]) * y
                + (q[oa + 7] + q[ob + 7]) * z * z + 2 * (q[oa + 8] + q[ob + 8]) * z + (q[oa + 9] + q[ob + 9]);
            return Math.Max(0.0, e) / Math.Max(1e-9, area[a] + area[b]);
        }

        private static void Push(int a, int b)
        {
            Vector3 mid = (pos[a] + pos[b]) * 0.5f;
            double ea = Error(a, b, pos[a]), eb = Error(a, b, pos[b]), em = Error(a, b, mid);
            Edge e = new Edge { a = a, b = b, sa = stamp[a], sb = stamp[b] };
            if (em <= ea && em <= eb)
            {
                e.cost = em;
                e.to = mid;
            }
            else if (ea <= eb)
            {
                e.cost = ea;
                e.to = pos[a];
            }
            else
            {
                e.cost = eb;
                e.to = pos[b];
            }
            heap.Add(e);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (heap[parent].cost <= heap[i].cost)
                {
                    break;
                }
                Edge tmp = heap[parent];
                heap[parent] = heap[i];
                heap[i] = tmp;
                i = parent;
            }
        }

        private static Edge Pop()
        {
            Edge top = heap[0];
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            int i = 0, n = heap.Count;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, s = i;
                if (l < n && heap[l].cost < heap[s].cost)
                {
                    s = l;
                }
                if (r < n && heap[r].cost < heap[s].cost)
                {
                    s = r;
                }
                if (s == i)
                {
                    break;
                }
                Edge tmp = heap[s];
                heap[s] = heap[i];
                heap[i] = tmp;
                i = s;
            }
            return top;
        }

        // Whether moving v to p turns over (or flattens) one of v's faces that does not also hold other.
        private static bool Flips(int v, int other, Vector3 p)
        {
            List<int> av = around[v];
            for (int k = 0; k < av.Count; k++)
            {
                int t = av[k];
                if (deadTri[t])
                {
                    continue;
                }
                int at = t * 3;
                int a = tris[at], b = tris[at + 1], c = tris[at + 2];
                if (a == other || b == other || c == other)
                {
                    continue;
                }
                Vector3 pa = a == v ? p : pos[a], pb = b == v ? p : pos[b], pc = c == v ? p : pos[c];
                Vector3 before = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                Vector3 after = Vector3.Cross(pb - pa, pc - pa);
                float bm = before.magnitude, am = after.magnitude;
                if (am < bm * 0.02f || Vector3.Dot(before, after) < 0.3f * bm * am)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
