using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Moving model parts drawn as one instanced draw per part (step 8b of the 1.7 port).
    //
    // The engine's cost is per draw command (Vulkan recording on the render thread plus one draw item
    // on the main thread each), not per triangle. DynamicModels keeps one set of ready-made meshes per
    // model and colour, so the same part (a door leaf of one stuff, a cow's leg, a husky's head) is drawn
    // over and over with only the matrix changing: on a large base, 265 doors made 24 commands each.
    // While the game draws the map's things (DrawDynamicThings), DynamicModels.Submit hands its parts
    // here instead; at the end each (mesh, material) is drawn once for all its copies with
    // DrawMeshInstanced, and so is its light-pass mark. A part seen only once is drawn as before.
    //
    // Why the picture is the same: the instanced vertex stages (IsoRim.vert.glsl, IsoRimTex.vert.glsl
    // with INSTANCED) do exactly what the plain ones do with each copy's matrix; the solid, outline and
    // textured parts are opaque and depth-tested (queues 2000/2001), so the order they are drawn in
    // decides nothing but exact depth ties; their marks are drawn with the same instanced stage as the
    // face they mark, so they meet it at the same depth as before. Copies are kept in the order they
    // were submitted. The harness switch (probe batch=0|1, abswitch=batch) compares both ways.
    public static class PartBatches
    {
        public static bool Enabled = true;

        private sealed class Group
        {
            public Mesh mesh;
            public Material material;
            public int floor;   // the floor the copies stand on (RoofClip.FloorBlock)
            public Matrix4x4[] matrices = new Matrix4x4[8];
            public int count;
        }

        private struct Key
        {
            public Mesh mesh;
            public Material material;
            public int floor;
        }

        private sealed class KeyComparer : IEqualityComparer<Key>
        {
            public bool Equals(Key a, Key b)
            {
                return ReferenceEquals(a.mesh, b.mesh) && ReferenceEquals(a.material, b.material) && a.floor == b.floor;
            }

            public int GetHashCode(Key k)
            {
                return (RuntimeHelpers.GetHashCode(k.mesh) * 397 ^ RuntimeHelpers.GetHashCode(k.material)) * 31 + k.floor;
            }
        }

        private static readonly Dictionary<Key, Group> groups = new Dictionary<Key, Group>(new KeyComparer());
        private static readonly List<Group> order = new List<Group>();
        private static readonly Stack<Group> pool = new Stack<Group>();
        private static bool collecting;

        // DrawDynamicThings starts (Patches.CullThings_Prefix). Not during the gravship cutscene or
        // capture, which draw parts of the map elsewhere.
        public static void Begin()
        {
            if (collecting)
            {
                Flush();
            }
            collecting = Enabled && View3D.Ready && UnityData.IsInMainThread
                && !WorldComponent_GravshipController.CutsceneInProgress && !WorldComponent_GravshipController.GravshipRenderInProgess;
        }

        // Takes a part to draw at the end of the things, or returns false to have it drawn now. floor: the floor it stands on
        // (copies on different floors are drawn apart, each with its floor: RoofClip.FloorBlock).
        public static bool Add(Mesh mesh, Matrix4x4 matrix, Material material, int floor = 0)
        {
            if (!collecting || mesh == null || material == null || !UnityData.IsInMainThread || Floors.FadeSlot > 0)
            {
                return false;
            }
            Key key = new Key { mesh = mesh, material = material, floor = floor };
            Group g;
            if (!groups.TryGetValue(key, out g))
            {
                g = pool.Count > 0 ? pool.Pop() : new Group();
                g.mesh = mesh;
                g.material = material;
                g.floor = floor;
                g.count = 0;
                groups[key] = g;
                order.Add(g);
            }
            if (g.count == g.matrices.Length)
            {
                System.Array.Resize(ref g.matrices, g.count * 2);
            }
            g.matrices[g.count++] = matrix;
            return true;
        }

        // DrawDynamicThings ends (Patches.CullThings_Finalizer): every collected part drawn, with its mark. The matrices are in
        // drawn space already (DynamicModels.Submit moved each onto its floor before Add): drawn where they are
        // (Floors.InDrawnSpace), not moved again by their rows (a part past its band's north edge would be).
        public static void Flush()
        {
            collecting = false;
            if (order.Count == 0)
            {
                return;
            }
            bool ready = View3D.Ready;
            bool wasDrawn = Floors.InDrawnSpace;
            Floors.InDrawnSpace = true;
            try
            {
                for (int i = 0; i < order.Count; i++)
                {
                    Group g = order[i];
                    if (g.mesh != null && g.material != null)
                    {
                        MaterialPropertyBlock props = RoofClip.FloorBlock(g.floor);
                        if (g.count == 1)
                        {
                            IsoGlow.DrawLit(g.mesh, g.matrices[0], g.material, 0, props);
                        }
                        else
                        {
                            Graphics.DrawMeshInstanced(g.mesh, 0, g.material, g.matrices, g.count, props, UnityEngine.Rendering.ShadowCastingMode.Off, false, 0);
                            Material mark = ready ? IsoGlow.MarkFor(g.material) : null;
                            if (mark != null)
                            {
                                Graphics.DrawMeshInstanced(g.mesh, 0, mark, g.matrices, g.count, props, UnityEngine.Rendering.ShadowCastingMode.Off, false, 0);
                            }
                        }
                    }
                    g.mesh = null;
                    g.material = null;
                    g.count = 0;
                    pool.Push(g);
                }
            }
            finally
            {
                Floors.InDrawnSpace = wasDrawn;
            }
            order.Clear();
            groups.Clear();
        }
    }
}
