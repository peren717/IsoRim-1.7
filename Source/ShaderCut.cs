using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Step 7: the cut-away done in the vertex stage, so it follows the pointer the same frame and rebuilds nothing.
    //
    // Walls, rock, hull corner pieces, trees and roofs are baked once, whole (full height, all leaves, every roof
    // cell). Each vertex of IsoRim's walls, damage, trees and roofs layers carries in texture coordinate 1 the cell it
    // belongs to (x + 0.5, z + 0.5) and what may happen to it (z = the flags below). Every frame the cut-away
    // (Cutaway) works out which cells are cut, thinned or opened, exactly as before but without waiting for the
    // pointer to settle, and writes them into the map's cell texture (IsoCellMap: b = how far the cell's wall is cut
    // down, a = tree thinned / roof open); IsoRim.vert.glsl and IsoRimTex.vert.glsl read it: a lowered wall's upper
    // part is pressed down to the cut height, leaves of a thinned tree and an opened roof collapse to nothing, and
    // the sides between neighbouring walls (always baked now) show only where the neighbour is lower.
    //
    // On while the walls are drawn from IsoRim's textures (texturedWalls, the default); with the game's own wall
    // sprites the old rebuilding cut-away is used.
    public static class ShaderCut
    {
        public const int Lower = 1;          // the vertex goes down with its cell's cut
        public const int CondShift = 1;      // bits 1-4: shown only while the neighbour this way (1-8) stands lower than the cell
        public const int TexSide = 32;       // a textured side: v follows the height (the pattern is not squeezed)
        public const int Leaf = 64;          // hidden while the cell's tree is thinned
        public const int Twig = 128;         // shown only while the cell's tree is thinned
        public const int Roof = 256;         // hidden while the cell's roof is open
        public const int SecondShift = 9;    // bits 9-12: also lowered with the neighbour this way (1-8), whichever is cut more
                                             // bits 13-14: roof clipping (RoofClip.Own, RoofClip.Moving)
        public const int See = 32768;        // bit 15: goes see-through near the pointer while building (BuildSee)
        public const int Crown = 65536;      // bit 16 (Api 11): a wall top's crown (WallTops.cs), hidden while its cell is cut down

        // How long a cell takes to go down or come back up, in seconds. 0 = at once (the user's choice; the old
        // cut-away popped too, but only after the pointer had rested 0.08 s, and 0.25 s before walls came back).
        // Try 0.12 for a soft fade.
        public const float FadeSeconds = 0f;

        public static float Fade
        {
            get { return Probe.CutFadeOverride >= 0f ? Probe.CutFadeOverride : FadeSeconds; }
        }

        public static bool On
        {
            get
            {
                IsoRimSettings st = IsoRimMod.Settings;
                return View3D.Active && st.texturedWalls;
            }
        }

        // The eight neighbours, codes 1..8 (0 = none).
        private static readonly int[] DX = { 0, 0, 0, -1, 1, -1, 1, -1, 1 };
        private static readonly int[] DZ = { 0, -1, 1, 0, 0, -1, -1, 1, 1 };

        public static int DirCode(IntVec3 d)
        {
            for (int i = 1; i < 9; i++)
            {
                if (DX[i] == d.x && DZ[i] == d.z)
                {
                    return i;
                }
            }
            return 0;
        }

        public static int Cond(IntVec3 d)
        {
            return DirCode(d) << CondShift;
        }

        public static int Second(IntVec3 d)
        {
            return DirCode(d) << SecondShift;
        }
    }

    // Writes the per-vertex cut-away data (texture coordinate 1) while a tagged layer is built.
    public static class CutTags
    {
        public static bool Enabled;     // set while one of the tagged layers is built with the shader cut-away on
        private static readonly List<int[]> stack = new List<int[]>();
        private static int depth;

        // Starts a run of writes that will all get the same tag: returns the mark to hand to Apply. Marks nest (a
        // tag given inside a run wins over the run's own).
        public static int Mark(SectionLayer layer)
        {
            if (!Enabled)
            {
                return -1;
            }
            List<LayerSubMesh> subs = layer.subMeshes;
            if (stack.Count <= depth)
            {
                stack.Add(new int[64]);
            }
            int[] m = stack[depth];
            if (m.Length < subs.Count + 1)
            {
                m = stack[depth] = new int[subs.Count * 2 + 1];
            }
            m[0] = subs.Count;
            for (int i = 0; i < subs.Count; i++)
            {
                m[i + 1] = subs[i].verts.Count;
            }
            return depth++;
        }

        // Tags every vertex written to the layer since the mark that has no tag yet.
        public static void Apply(SectionLayer layer, int mark, IntVec3 owner, int flags)
        {
            if (!Enabled || mark < 0)
            {
                return;
            }
            depth = mark;
            flags |= ShaderCut.See;   // all of the tagged layers (walls, rock, roofs, damage marks) go see-through (BuildSee)
            int[] m = stack[mark];
            Vector3 tag = new Vector3(owner.x + 0.5f, owner.z + 0.5f, flags);
            List<LayerSubMesh> subs = layer.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sm = subs[i];
                int from = i < m[0] ? m[i + 1] : 0;
                int to = sm.verts.Count;
                if (to <= from)
                {
                    continue;
                }
                List<Vector3> t = sm.uvsChannelTwo;
                while (t.Count < to)
                {
                    t.Add(Vector3.zero);
                }
                for (int k = from; k < to; k++)
                {
                    // Untagged, or tagged by an inner run with no flags of its own (See only): this run's tag. (Otherwise
                    // an inner run without flags would keep an outer run's Lower off its vertices.)
                    if (t[k].z == 0f || t[k].z == ShaderCut.See)
                    {
                        t[k] = tag;
                    }
                }
            }
        }

        // Tags the last vertex written to one piece of mesh (trees: per triangle, as leaves and twigs are mixed).
        public static void Stamp(LayerSubMesh sm, Vector3 tag)
        {
            List<Vector3> t = sm.uvsChannelTwo;
            while (t.Count < sm.verts.Count - 1)
            {
                t.Add(Vector3.zero);
            }
            t.Add(tag);
        }

        public static Vector3 Tag(IntVec3 owner, int flags)
        {
            return new Vector3(owner.x + 0.5f, owner.z + 0.5f, flags);
        }

        // Before FinalizeMesh: every piece with any tag gets one per vertex (untagged ones zero).
        public static void Finish(SectionLayer layer)
        {
            depth = 0;
            List<LayerSubMesh> subs = layer.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                List<Vector3> t = subs[i].uvsChannelTwo;
                if (t.Count == 0)
                {
                    continue;
                }
                while (t.Count < subs[i].verts.Count)
                {
                    t.Add(Vector3.zero);
                }
            }
        }
    }
}
