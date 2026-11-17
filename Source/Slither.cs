using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 81, ModelDef.slither) A legless animal's body bent from side to side while it moves: a wave along the model's
    // forward axis that runs from the head back to the tail, its phase the walk phase (Animals.Advance), its reach growing from
    // nothing at the head to slither at the tail (rule and units: ModelDef.slither). The one place of the bend.
    //
    // How it is drawn: a moving model is a few cached meshes (solid, outline shell, textured faces: DynamicModels.PartsFor)
    // shared by every animal of that model and batched by mesh (PartBatches). A slithering animal gets its own copies of
    // those meshes, kept while it is drawn, whose corners are moved by the bend (and their smooth normals turned with it)
    // each frame its wave has moved on; the copies keep everything else (colours, tags, materials), so outline, smooth light,
    // textures, hit flash, roof clip and the screen-space sun shadows (from the depth they write) follow the bent body. The
    // game's wound marks on it are bent the same way (WoundMarks.DrawAnimal). Standing still (gait 0) it is drawn from the
    // shared meshes as any animal; without slither nothing here is used. The copies are new meshes built from the shared ones
    // (Make: on RimWorld 1.7 / Waylith, Object.Instantiate of a mesh made at run time gives back the same mesh), so the shared
    // meshes and the game's wound-mark meshes are never changed or destroyed here. Cost: one pass over the copies' corners per
    // frame the wave moves on by RebendPhase or more, plus the engine's own copy of the arrays handed to SetVertices /
    // SetNormals (Waylith rebuilds its CPU mesh data there: about one mesh's worth of garbage per bent frame and copy).
    internal static class Slither
    {
        // A wave as it is now, in the space of the mesh it bends.
        internal struct Wave
        {
            public Vector3 forward;     // the body's forward axis (unit, level)
            public Vector3 side;        // its side axis (unit, level, square to forward)
            public float head;          // the foremost point along forward
            public float length;        // head to tail along forward (> 0)
            public float wavelength;    // along the body (> 0)
            public float reach;         // sideways at the tail now: slither x gait (model units, > 0)
            public float phase;         // the walk phase, radians

            // The same wave for a mesh in the space after the body model's rotation (ModelDef.rotation): forward +z, side +x
            // (the game's wound marks are laid on the turned body, WoundMarks.BuildAnimal).
            public Wave Turned()
            {
                Wave w = this;
                w.forward = Vector3.forward;
                w.side = Vector3.right;
                return w;
            }
        }

        // Below this reach (model units) the body counts as straight.
        private const float MinReach = 1e-4f;
        // Copies of an animal not drawn bent for Animals.ForgetAfterFrames frames are let go (as Animals forgets its step).
        private const int SweepEveryFrames = 300;
        // A copy is bent again only once the wave has moved on by RebendPhase (radians; at a walk it moves 0.3..0.55 a frame) or
        // its reach changed by RebendReach (model units): smaller changes do not show.
        private const float RebendPhase = 0.02f;
        private const float RebendReach = 0.001f;

        // The bend's wave for this animal now (false: drawn straight). phase and gait as Animals.Draw has them (Advance);
        // the caller decides that it moves on its own (not lying, attached, carried or in the air).
        internal static bool Of(ModelDef def, Model body, float phase, float gait, out Wave w)
        {
            w = default(Wave);
            if (def == null || body == null || def.slither <= 0f || gait <= Animals.StillGait)
            {
                return false;
            }
            float reach = def.slither * Mathf.Clamp01(gait);
            if (reach < MinReach)
            {
                return false;
            }
            Extent e = ExtentOf(def, body);
            if (e.length <= MinReach)
            {
                return false;
            }
            w.forward = e.forward;
            w.side = e.side;
            w.head = e.head;
            w.length = e.length;
            w.wavelength = def.slitherWave > 0f ? def.slitherWave : e.length;
            w.reach = reach;
            w.phase = phase;
            return true;
        }

        // How far a point is moved sideways (along w.side), and the slope of that along the body (per unit behind the head).
        internal static float Offset(in Wave w, Vector3 p, out float slope)
        {
            float u = w.head - Vector3.Dot(p, w.forward);           // behind the head
            float t = u / w.length;
            float k = 2f * Mathf.PI / w.wavelength;
            float a = k * u - w.phase;
            float sin = Mathf.Sin(a);
            float grow = t > 0f && t < 1f ? 1f / w.length : 0f;   // d(t)/du inside the body
            t = Mathf.Clamp01(t);
            slope = w.reach * (grow * sin + t * k * Mathf.Cos(a));
            return w.reach * t * sin;
        }

        // ---- the model's extent along its forward axis

        private struct Extent
        {
            public float rotation;
            public Vector3 forward, side;
            public float head, length;
        }

        private static readonly Dictionary<Model, Extent> extents = new Dictionary<Model, Extent>();

        private static Extent ExtentOf(ModelDef def, Model body)
        {
            Extent e;
            if (extents.TryGetValue(body, out e) && e.rotation == def.rotation)
            {
                return e;
            }
            // Animals.Draw turns the body model by def.rotation (bodyTurn) before facing it north: in the model's own space its
            // forward axis is +z turned back by that.
            Quaternion back = Quaternion.AngleAxis(-def.rotation, Vector3.up);
            e.rotation = def.rotation;
            e.forward = back * Vector3.forward;
            e.side = back * Vector3.right;
            float hi = float.MinValue, lo = float.MaxValue;
            Vector3[] v = body.positions;
            for (int i = 0; v != null && i < v.Length; i++)
            {
                float s = Vector3.Dot(v[i], e.forward);
                hi = Mathf.Max(hi, s);
                lo = Mathf.Min(lo, s);
            }
            e.head = hi > lo ? hi : 0f;
            e.length = hi > lo ? hi - lo : 0f;
            extents[body] = e;
            return e;
        }

        // ---- one animal's bent copies

        private sealed class Copy
        {
            public Mesh source;
            public Mesh mesh;
            public Vector3[] from, fromNormals, to, toNormals;
            public float phase = float.NaN, reach = float.NaN;   // the wave its corners are bent to now
            public int used;
        }

        private sealed class Owner
        {
            public int used;
            public int made;   // the frame its copies were first made (tests)
            public List<DynamicModels.Part> source;                 // the shared parts copied
            public readonly List<DynamicModels.Part> parts = new List<DynamicModels.Part>();
            public readonly List<Copy> partCopies = new List<Copy>();
            public readonly List<Copy> loose = new List<Copy>();    // other meshes bent for it (its wound marks)
        }

        private static readonly Dictionary<int, Owner> owners = new Dictionary<int, Owner>();
        private static readonly List<int> stale = new List<int>();
        private static int sweptAt;

        // The animal's own bent copies of a model's shared parts (DynamicModels.DrawLocalSlither), bent to w.
        internal static List<DynamicModels.Part> Parts(Pawn pawn, List<DynamicModels.Part> source, in Wave w)
        {
            Owner o = OwnerOf(pawn);
            if (!ReferenceEquals(o.source, source))
            {
                // first bent draw, or the shared parts were remade (another tint or model, the cache cleared): copy them again
                for (int i = 0; i < o.partCopies.Count; i++)
                {
                    Retire(o.partCopies[i]);
                }
                o.partCopies.Clear();
                o.parts.Clear();
                o.source = source;
                for (int i = 0; i < source.Count; i++)
                {
                    DynamicModels.Part s = source[i];
                    Copy c = Make(s.mesh);
                    o.partCopies.Add(c);
                    o.parts.Add(new DynamicModels.Part { mesh = c != null ? c.mesh : s.mesh, material = s.material, outline = s.outline, beam = s.beam, alone = s.alone });
                }
            }
            for (int i = 0; i < o.partCopies.Count; i++)
            {
                Bend(o.partCopies[i], w);
            }
            return o.parts;
        }

        // The animal's own copy of one more mesh (its wound marks), bent to w; the mesh itself if it cannot be copied.
        internal static Mesh Bent(Pawn pawn, Mesh source, in Wave w)
        {
            if (source == null)
            {
                return null;
            }
            Owner o = OwnerOf(pawn);
            Copy c = null;
            for (int i = 0; i < o.loose.Count; i++)
            {
                if (ReferenceEquals(o.loose[i].source, source))
                {
                    c = o.loose[i];
                    break;
                }
            }
            if (c == null)
            {
                c = Make(source);
                if (c == null)
                {
                    return source;
                }
                o.loose.Add(c);
            }
            Bend(c, w);
            return c.mesh;
        }

        // Tests (slithercheck): whether the animal was last drawn bent, how many frames ago, and the reach and phase it had.
        internal static bool LastBent(Pawn pawn, out int age, out float reach, out float phase)
        {
            age = -1;
            reach = phase = 0f;
            Owner o;
            if (pawn == null || !owners.TryGetValue(pawn.thingIDNumber, out o) || o.partCopies.Count == 0 || o.partCopies[0] == null)
            {
                return false;
            }
            age = Time.frameCount - o.used;
            reach = o.partCopies[0].reach;
            phase = o.partCopies[0].phase;
            return true;
        }

        // Tests (slithercheck): the animal's copies (parts, loose), whether every one is a mesh of its own (never the shared or
        // the game's mesh), and a hash of the shared parts' corners (they must never change).
        internal static bool Copies(Pawn pawn, out int parts, out int loose, out bool own, out int sharedHash)
        {
            parts = loose = 0;
            own = true;
            sharedHash = 17;
            Owner o;
            if (pawn == null || !owners.TryGetValue(pawn.thingIDNumber, out o))
            {
                return false;
            }
            for (int i = 0; i < o.partCopies.Count; i++)
            {
                Copy c = o.partCopies[i];
                parts++;
                own &= c != null && c.mesh != null && !ReferenceEquals(c.mesh, c.source);
            }
            for (int i = 0; i < o.loose.Count; i++)
            {
                loose++;
                own &= o.loose[i].mesh != null && !ReferenceEquals(o.loose[i].mesh, o.loose[i].source);
            }
            for (int i = 0; o.source != null && i < o.source.Count; i++)
            {
                Mesh m = o.source[i].mesh;
                if (m == null)
                {
                    continue;
                }
                foreach (Vector3 v in m.vertices)
                {
                    unchecked { sharedHash = sharedHash * 31 + v.GetHashCode(); }
                }
            }
            return true;
        }

        // Tests (slithercheck): the frame the animal's current copies were made; -1 for none.
        internal static int CopiesMadeAt(Pawn pawn)
        {
            Owner o;
            return pawn != null && owners.TryGetValue(pawn.thingIDNumber, out o) ? o.made : -1;
        }

        private static Owner OwnerOf(Pawn pawn)
        {
            int frame = Time.frameCount;
            SweepIfDue(frame);
            Owner o;
            if (!owners.TryGetValue(pawn.thingIDNumber, out o))
            {
                o = new Owner { made = frame };
                owners[pawn.thingIDNumber] = o;
            }
            o.used = frame;
            return o;
        }

        private static Copy Make(Mesh source)
        {
            if (source == null || !source.isReadable)
            {
                return null;
            }
            Mesh mesh = CopyMesh(source);
            if (mesh == null)
            {
                return null;
            }
            Copy c = new Copy();
            c.source = source;
            c.mesh = mesh;
            c.from = source.vertices;
            c.to = new Vector3[c.from.Length];
            Vector3[] n = source.normals;
            if (n != null && n.Length == c.from.Length)
            {
                c.fromNormals = n;
                c.toNormals = new Vector3[n.Length];
            }
            return c;
        }

        // A new mesh with the source's corners, colours, both texture channels (uv1 carries the roof-clip and smooth-light tags in
        // x, y, z: DynamicModels.Lists.ToMesh), normals and triangles (Object.Instantiate would give back the source itself).
        private static Mesh CopyMesh(Mesh source)
        {
            Vector3[] v = source.vertices;
            if (v == null || v.Length == 0)
            {
                return null;
            }
            Mesh m = new Mesh();
            MeshGuard.FitIndices(m, v.Length);
            m.SetVertices(new List<Vector3>(v));
            Color32[] colours = source.colors32;
            if (colours != null && colours.Length == v.Length)
            {
                m.SetColors(new List<Color32>(colours));
            }
            Vector2[] uv = source.uv;
            if (uv != null && uv.Length == v.Length)
            {
                m.SetUVs(0, new List<Vector2>(uv));
            }
            List<Vector4> tags4 = new List<Vector4>();
            source.GetUVs(1, tags4);
            if (tags4.Count == v.Length)
            {
                List<Vector3> tags = new List<Vector3>(tags4.Count);
                for (int i = 0; i < tags4.Count; i++)
                {
                    tags.Add(new Vector3(tags4[i].x, tags4[i].y, tags4[i].z));
                }
                m.SetUVs(1, tags);
            }
            Vector3[] n = source.normals;
            if (n != null && n.Length == v.Length)
            {
                m.SetNormals(new List<Vector3>(n));
            }
            int subs = Mathf.Max(1, source.subMeshCount);
            m.subMeshCount = subs;
            for (int s = 0; s < subs; s++)
            {
                List<int> tris = new List<int>();
                source.GetTriangles(tris, s);
                m.SetTriangles(tris, s);
            }
            m.RecalculateBounds();
            m.MarkDynamic();
            if (!MeshGuard.Check(m, "slither copy"))
            {
                UnityEngine.Object.Destroy(m);
                return null;
            }
            return m;
        }

        // Moves the copy's corners (and turns its normals) to the wave, unless they are there already (the game paused, or
        // drawn twice in a frame) or nearly (RebendPhase, RebendReach).
        private static void Bend(Copy c, in Wave w)
        {
            if (c == null)
            {
                return;
            }
            c.used = Time.frameCount;
            if (Mathf.Abs(c.phase - w.phase) < RebendPhase && Mathf.Abs(c.reach - w.reach) < RebendReach)
            {
                return;   // (NaN at first: always bent then)
            }
            c.phase = w.phase;
            c.reach = w.reach;
            Vector3[] from = c.from, to = c.to, fn = c.fromNormals, tn = c.toNormals;
            for (int i = 0; i < from.Length; i++)
            {
                float slope;
                float d = Offset(w, from[i], out slope);
                to[i] = from[i] + w.side * d;
                if (fn != null)
                {
                    // p' = p + side g(s), s = p . forward: normals by the inverse transpose, n - forward g'(s) (side . n), where
                    // g'(s) = -slope (slope is per unit behind the head)
                    Vector3 n = fn[i] + w.forward * (slope * Vector3.Dot(w.side, fn[i]));
                    tn[i] = n.normalized;
                }
            }
            c.mesh.SetVertices(to);   // (bounds recalculated: the culling box follows the bend)
            if (tn != null)
            {
                c.mesh.SetNormals(tn);
            }
        }

        private static void Retire(Copy c)
        {
            if (c != null && c.mesh != null && !ReferenceEquals(c.mesh, c.source))   // (never a shared or the game's own mesh)
            {
                DynamicModels.Retire(c.mesh);
                c.mesh = null;
            }
        }

        // Also from Animals.Prune (as it forgets animals not drawn for a while): Sweep at most every SweepEveryFrames.
        internal static void SweepIfDue(int frame)
        {
            if (frame - sweptAt >= SweepEveryFrames)
            {
                sweptAt = frame;
                Sweep(frame);
            }
        }

        // A new or loaded game (View3D.NewGame): every copy let go (the last game's animals are gone).
        internal static void Reset()
        {
            foreach (KeyValuePair<int, Owner> pair in owners)
            {
                RetireAll(pair.Value);
            }
            owners.Clear();
            sweptAt = 0;
        }

        private static void RetireAll(Owner o)
        {
            for (int i = 0; i < o.partCopies.Count; i++)
            {
                Retire(o.partCopies[i]);
            }
            for (int i = 0; i < o.loose.Count; i++)
            {
                Retire(o.loose[i]);
            }
        }

        // Lets go of the copies of animals not drawn bent for a while (standing, gone, on another map), and of loose copies
        // no longer drawn (wound marks remade).
        private static void Sweep(int frame)
        {
            if (owners.Count == 0)
            {
                return;
            }
            stale.Clear();
            foreach (KeyValuePair<int, Owner> pair in owners)
            {
                Owner o = pair.Value;
                if (frame - o.used > Animals.ForgetAfterFrames)
                {
                    stale.Add(pair.Key);
                    continue;
                }
                for (int i = o.loose.Count - 1; i >= 0; i--)
                {
                    if (frame - o.loose[i].used > Animals.ForgetAfterFrames)
                    {
                        Retire(o.loose[i]);
                        o.loose.RemoveAt(i);
                    }
                }
            }
            for (int k = 0; k < stale.Count; k++)
            {
                RetireAll(owners[stale[k]]);
                owners.Remove(stale[k]);
            }
        }
    }
}
