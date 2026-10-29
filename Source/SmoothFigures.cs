using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace IsoRim
{
    // Smooth shading on people (setting smoothPeople, on by default): the parts of a person's figure (head, body, hair, beard,
    // clothes, headgear, gene parts, attachments, shield; an outfit stand's clothes and head, Stands) are lit by per-corner normals averaged over the faces that meet
    // at that corner, instead of each face by its own normal. Faces meeting at more than the crease angle (smoothCrease,
    // degrees) keep a hard edge between them.
    //
    // Worked out once per model and crease (cached here, weakly: tucked hair and other models made in code go with their
    // model), and baked into the person's merged meshes when they are built (DynamicModels.BuildMerged / Build with
    // smooth): the mesh normal carries it and the vertex flag Bit tells IsoRim.vert.glsl to hand it to the fragment
    // stage (IsoRimSolid.frag.glsl), which lights the pixel by it, unsnapped, instead of the face normal from screen
    // derivatives. Nothing is done per frame. The outline shells (Model.hull) are not touched.
    // Animals (setting smoothAnimals, on by default, the same crease): every model part an animal is drawn with (body, legs,
    // wings; Animals.Draw: standing, walking, asleep, downed, dead, flying, carried) while DynamicModels.AnimalPart is set.
    // Insects too; mechanoids (and entities, Mechs.Draw) not.
    // Everything else (weapons, furniture, mechs, buildings) is lit as before.
    public static class SmoothFigures
    {
        // Bit 17 of the vertex flags (texture coordinate 1 z, next to RoofClip.Moving and ShaderCut.See): the mesh normal
        // is a smooth normal (IsoRim.vert.glsl).
        public const int Bit = 131072;

        // Welding: corners closer than 1/Weld (model units) count as one position.
        private const float Weld = 10000f;

        public static bool On => IsoRimMod.Settings != null && IsoRimMod.Settings.smoothPeople;

        public static float Crease => IsoRimMod.Settings != null ? Mathf.Clamp(IsoRimMod.Settings.smoothCrease, 0f, 180f) : 70f;

        // Changes whenever the look of smooth people would (People.LookOf throws away the made meshes then).
        public static int Stamp => On ? 1 + Mathf.RoundToInt(Crease * 10f) : 0;

        // Smooth shading on animals (not mechanoids).
        public static bool AnimalsOn => IsoRimMod.Settings != null && IsoRimMod.Settings.smoothAnimals;

        // Changes whenever the made meshes of smooth model parts would (DynamicModels.PartsFor throws them away then).
        public static int PartStamp => (On || AnimalsOn ? 1 + Mathf.RoundToInt(Crease * 10f) : 0) * 4 + (On ? 1 : 0) + (AnimalsOn ? 2 : 0);

        // Harness: time spent working out normals (Stopwatch ticks) and how many models were worked out.
        public static long Ticks;
        public static int Worked;

        private class Entry
        {
            public float crease;
            public Vector3[] normals;
        }

        private static readonly ConditionalWeakTable<Model, Entry> cache = new ConditionalWeakTable<Model, Entry>();

        // One smooth normal per corner of m (model space, unit length, on the side of the face's own normal), or null if
        // m has no faces.
        public static Vector3[] NormalsOf(Model m)
        {
            if (m == null || m.positions == null || m.positions.Length < 3)
            {
                return null;
            }
            float crease = Crease;
            Entry e;
            if (cache.TryGetValue(m, out e) && e.crease == crease)
            {
                return e.normals;
            }
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Vector3[] normals = Work(m, crease);
            Ticks += sw.ElapsedTicks;
            Worked++;
            if (e != null)
            {
                cache.Remove(m);
            }
            cache.Add(m, new Entry { crease = crease, normals = normals });
            return normals;
        }

        private static Vector3[] Work(Model m, float crease)
        {
            Vector3[] pos = m.positions;
            int n = pos.Length - pos.Length % 3;
            int tris = n / 3;
            Vector3[] face = new Vector3[tris];
            for (int t = 0; t < tris; t++)
            {
                int i = t * 3;
                Vector3 f = Vector3.Cross(pos[i + 1] - pos[i], pos[i + 2] - pos[i]);
                Vector3 own = m.normals != null && i < m.normals.Length ? m.normals[i] : Vector3.zero;
                if (own.sqrMagnitude > 1e-12f && Vector3.Dot(f, own) < 0f)
                {
                    f = -f;   // the model's own face normal says which side is out
                }
                float len = f.magnitude;
                face[t] = len > 1e-12f ? f / len : (own.sqrMagnitude > 1e-12f ? own.normalized : Vector3.zero);
            }

            // The corners at each welded position.
            Dictionary<long, List<int>> at = new Dictionary<long, List<int>>();
            long[] keys = new long[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 p = pos[i];
                long key = (((long)Mathf.RoundToInt(p.x * Weld) & 0x1FFFFF) << 42) | (((long)Mathf.RoundToInt(p.y * Weld) & 0x1FFFFF) << 21)
                    | ((long)Mathf.RoundToInt(p.z * Weld) & 0x1FFFFF);
                keys[i] = key;
                List<int> list;
                if (!at.TryGetValue(key, out list))
                {
                    list = new List<int>(6);
                    at[key] = list;
                }
                list.Add(i);
            }

            // Each corner's weight: the angle of its face there (angle-weighted normals).
            float[] weight = new float[n];
            for (int t = 0; t < tris; t++)
            {
                int i = t * 3;
                for (int c = 0; c < 3; c++)
                {
                    Vector3 a = pos[i + (c + 1) % 3] - pos[i + c];
                    Vector3 b = pos[i + (c + 2) % 3] - pos[i + c];
                    float la = a.magnitude, lb = b.magnitude;
                    weight[i + c] = la > 1e-9f && lb > 1e-9f ? Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b) / (la * lb), -1f, 1f)) : 0f;
                }
            }

            float limit = crease >= 180f ? -2f : Mathf.Cos(crease * Mathf.Deg2Rad);
            Vector3[] result = new Vector3[pos.Length];
            for (int i = 0; i < n; i++)
            {
                Vector3 mine = face[i / 3];
                Vector3 sum = Vector3.zero;
                List<int> list = at[keys[i]];
                for (int k = 0; k < list.Count; k++)
                {
                    int j = list[k];
                    Vector3 other = face[j / 3];
                    if (Vector3.Dot(mine, other) >= limit - 1e-6f)
                    {
                        sum += other * weight[j];
                    }
                }
                float len = sum.magnitude;
                // Faces that cancel out (both sides of a thin sheet, fully smooth): the face's own normal.
                result[i] = len > 1e-4f && Vector3.Dot(sum, mine) > 0f ? sum / len : mine;
            }
            for (int i = n; i < pos.Length; i++)
            {
                result[i] = Vector3.up;
            }
            return result;
        }

        // For the harness: everything worked out again with the next build.
        internal static void Clear()
        {
            Ticks = 0;
            Worked = 0;
            cache.Clear();
        }
    }
}
