using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using Verse;
using W = Waylith.Graphics.Meshes;

namespace IsoRim
{
    // The last check before a mesh reaches the GPU. The 1.7 engine does not validate index buffers: an index past
    // the vertex count, a sub-mesh range past the index buffer or a NaN position draws garbage on NVIDIA but can
    // reset AMD and Intel drivers ("the graphics device was lost"). Every mesh IsoRim builds, and every map
    // sub-mesh finalized while IsoRim is loaded, is checked once per build, on the engine's own copy (the buffers
    // the GPU gets, read in place: nothing is allocated). A bad one is emptied (an empty mesh is not drawn),
    // noted in the crash trail and reported once per name.
    public static class MeshGuard
    {
        public static int Checked, Dropped;
        public static long Ticks;   // time spent checking (Stopwatch ticks), read by the harness
        private static readonly HashSet<string> reported = new HashSet<string>();
        private static bool failed;

        // Meshes with more vertices than this get 32-bit indices (16-bit ones stop at 65,535).
        public const int WideAbove = 65000;

        // Sets the index size a mesh of this many vertices needs; called by every builder before the triangles are set.
        // Small meshes keep the default 16-bit indices (a pooled mesh that was large is put back to them). Waylith's
        // Mesh also switches to 32-bit on its own when SetVertices/SetTriangles see more than 65,535; setting it here
        // keeps the format from depending on that.
        public static void FitIndices(Mesh mesh, int vertexCount)
        {
            UnityEngine.Rendering.IndexFormat want = vertexCount > WideAbove ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            if (mesh != null && mesh.indexFormat != want)
            {
                mesh.indexFormat = want;
            }
        }

        // True if the mesh is safe to draw; otherwise it is cleared and false is returned.
        public static bool Check(Mesh mesh, string what)
        {
            string why = Run(mesh);
            if (why == null)
            {
                return true;
            }
            Drop(mesh, what, why);
            return false;
        }

        // A map sub-mesh just finalized (named by its material only when it is bad).
        public static void CheckLayer(LayerSubMesh sm)
        {
            if (sm == null)
            {
                return;
            }
            string why = Run(sm.mesh);
            if (why != null)
            {
                Drop(sm.mesh, "map layer " + (sm.material != null ? sm.material.name : "?"), why);
            }
        }

        private static string Run(Mesh mesh)
        {
            if (mesh == null || failed)
            {
                return null;
            }
            long t0 = Stopwatch.GetTimestamp();
            string why = null;
            try
            {
                why = Problem(mesh);
            }
            catch (Exception e)
            {
                failed = true;   // an engine change: stop checking rather than break mesh building
                Log.Warning("[IsoRim] Mesh check turned off: " + e.Message);
            }
            Interlocked.Add(ref Ticks, Stopwatch.GetTimestamp() - t0);
            Interlocked.Increment(ref Checked);
            return why;
        }

        private static void Drop(Mesh mesh, string what, string why)
        {
            Interlocked.Increment(ref Dropped);
            mesh.Clear();
            CrashTrail.Note("mesh '" + what + "' left out: " + why, false);
            bool first;
            lock (reported)
            {
                first = reported.Count < 20 && reported.Add(what);
            }
            if (first)
            {
                Log.Warning("[IsoRim] Mesh '" + what + "' (" + mesh.name + ") left out, it would break the GPU: " + why);
            }
        }

        private static string Problem(Mesh mesh)
        {
            W.Mesh m = mesh.EngineMesh;
            if (m == null || !m.IsReadable)
            {
                return null;   // no CPU copy left to check
            }
            W.CpuMeshData d = m.CpuStorage;
            W.SubMesh[] subs = d?.SubMeshes;
            if (subs == null || subs.Length == 0 || d.IndexCount == 0)
            {
                return null;
            }
            int vc = d.VertexCount;
            bool wide = d.IndexFormat == W.MeshIndexFormat.UInt32;
            Span<byte> raw = d.GetIndexBuffer().AsSpan();
            ReadOnlySpan<int> i32 = wide ? MemoryMarshal.Cast<byte, int>(raw) : default;
            ReadOnlySpan<ushort> i16 = wide ? default : MemoryMarshal.Cast<byte, ushort>(raw);
            int avail = wide ? i32.Length : i16.Length;
            for (int s = 0; s < subs.Length; s++)
            {
                W.SubMesh sub = subs[s];
                if (sub.IndexCount == 0)
                {
                    continue;
                }
                if (sub.Topology == W.MeshTopology.Triangles && sub.IndexCount % 3 != 0)
                {
                    return "sub-mesh " + s + " has " + sub.IndexCount + " indices, not whole triangles";
                }
                if (sub.IndexStart < 0 || sub.IndexCount < 0 || (long)sub.IndexStart + sub.IndexCount > avail)
                {
                    return "sub-mesh " + s + " indices " + sub.IndexStart + "+" + sub.IndexCount + " past the " + avail + " in the buffer";
                }
                int lo = int.MaxValue, hi = int.MinValue;
                int end = sub.IndexStart + sub.IndexCount;
                if (wide)
                {
                    for (int k = sub.IndexStart; k < end; k++)
                    {
                        int v = i32[k];
                        lo = v < lo ? v : lo;
                        hi = v > hi ? v : hi;
                    }
                }
                else
                {
                    for (int k = sub.IndexStart; k < end; k++)
                    {
                        int v = i16[k];
                        lo = v < lo ? v : lo;
                        hi = v > hi ? v : hi;
                    }
                }
                long first = (long)lo + sub.BaseVertex, last = (long)hi + sub.BaseVertex;
                if (first < 0 || last >= vc)
                {
                    return "sub-mesh " + s + " uses vertex " + (first < 0 ? first : last) + " of " + vc;
                }
            }
            if (d.Layout.TryGetAttribute(W.VertexAttributeSemantic.Position, out W.VertexAttribute pos) && pos.Format == W.VertexAttributeFormat.Float32 && pos.Dimension >= 3)
            {
                int stride = d.Layout.GetStreamStride(pos.Stream);
                Span<byte> vs = d.GetVertexStream(pos.Stream).AsSpan();
                if (vc > 0 && (long)(vc - 1) * stride + pos.OffsetBytes + 12 > vs.Length)
                {
                    return vc + " vertices but a vertex stream of " + vs.Length + " bytes";
                }
                for (int k = 0; k < vc; k++)
                {
                    ReadOnlySpan<float> p = MemoryMarshal.Cast<byte, float>(vs.Slice(k * stride + pos.OffsetBytes, 12));
                    if (!float.IsFinite(p[0]) || !float.IsFinite(p[1]) || !float.IsFinite(p[2]))
                    {
                        return "vertex " + k + " is (" + p[0] + ", " + p[1] + ", " + p[2] + ")";
                    }
                }
            }
            return null;
        }
    }
}
