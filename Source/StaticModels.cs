using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 61, Api61.cs) The plumbing behind the static model calls: model selectors and registered surfaces (a normal and
    // tangent per corner, and the consumer's own materials) for static building prints. Baking.Write asks Select, HasSurface,
    // MaterialFor and Append while it writes a model; the FinalizeMesh bridge (Patches.CullFinalizeMesh_Postfix) uploads the
    // basis. No asset format, texture loading, detail policy or update loop here: those belong to the consumer. With nothing
    // registered every hook leaves at its first check.
    internal static class StaticModelExtensions
    {
        private sealed class Surface
        {
            internal Vector3[] normals;
            internal Vector4[] tangents;
            internal Func<StaticMaterialContext, Material> material;
            internal bool disabled;
        }
        private sealed class Buffer
        {
            internal readonly List<Vector4> values = new List<Vector4>();
            internal Material original;
        }
        private static readonly ConditionalWeakTable<Model, Surface> surfaces = new ConditionalWeakTable<Model, Surface>();
        private static readonly ConditionalWeakTable<LayerSubMesh, Buffer> buffers = new ConditionalWeakTable<LayerSubMesh, Buffer>();
        private static Func<StaticModelContext, ModelHandle>[] selectors = Array.Empty<Func<StaticModelContext, ModelHandle>>();
        private static readonly Dictionary<Material, Material> originals = new Dictionary<Material, Material>();
        private static int surfaceCount;
        private static bool bufferWarning;

        // Reads the floor state the renderer last prepared (Floors.BeginFrame in View3D.Tick) and never prepares it itself: an
        // early call from a MapComponentUpdate would fix the frame's layout before the floor mod's own update in that frame,
        // and could run while the renderer skips its frame (a long event). Before the renderer's first frame on a map the
        // row reads as not visible; a caller asks again later.
        internal static StaticModelView ReadView(Map map, int row)
        {
            if (map == null || map != Find.CurrentMap || row < 0 || row >= map.Size.z || !Api.ModelsOn)
                return new StaticModelView { Floor = -1 };
            if (Api.Floors != null && Floors.FrameMap != map)
                return new StaticModelView { Floor = -1 };
            if (!Floors.On) return new StaticModelView { Visible = true };
            int k = Floors.FloorOfRow(row);
            return new StaticModelView { Floor = k, ViewedFloor = Floors.PointerTop,
                Visible = k >= 0 && Floors.Shown(k), Offset = Floors.RowOffset(row) };
        }

        private static bool Ordinary(Model m)
        {
            if (m == null || m.beam != null || m.ownColor != null || m.materials == null || m.uvs == null || m.material == null) return false;
            foreach (ModelMaterial slot in m.materials)
                if (slot.leaf || slot.twig || slot.glow || slot.fire || slot.beam || slot.runtimeTexture != null) return false;
            return true;
        }
        internal static StaticModelGeometry Read(ModelHandle handle)
        {
            Model m = handle?.model;
            if (!Ordinary(m)) return null;
            var names = new string[m.materials.Length];
            var paths = new string[names.Length];
            for (int i = 0; i < names.Length; i++) { names[i] = m.materials[i].name; paths[i] = m.materials[i].texture; }
            return new StaticModelGeometry { Positions = (Vector3[])m.positions.Clone(), UVs = (Vector2[])m.uvs.Clone(),
                MaterialIndices = (int[])m.material.Clone(), MaterialNames = names, TexturePaths = paths };
        }
        internal static void Register(ModelHandle handle, Vector3[] normals, Vector4[] tangents, Func<StaticMaterialContext, Material> material)
        {
            Model m = handle?.model;
            if (!Ordinary(m) || material == null || normals == null || tangents == null
                || normals.Length != m.positions.Length || tangents.Length != normals.Length)
                throw new ArgumentException("Static surface requires an ordinary model and matching corner arrays");
            for (int i = 0; i < normals.Length; i++)
            {
                Vector3 n = normals[i]; Vector4 v = tangents[i]; Vector3 t = new Vector3(v.x, v.y, v.z);
                if (!Finite(n) || !Finite(t) || !float.IsFinite(v.w) || Math.Abs(n.magnitude - 1f) > .01f
                    || Math.Abs(t.magnitude - 1f) > .01f || Math.Abs(Vector3.Dot(n, t)) > .01f || Math.Abs(Math.Abs(v.w) - 1f) > .001f)
                    throw new ArgumentException("Invalid static surface basis at corner " + i);
            }
            if (surfaces.TryGetValue(m, out _)) throw new InvalidOperationException("Static surface already registered for this model");
            surfaces.Add(m, new Surface { normals = (Vector3[])normals.Clone(), tangents = (Vector4[])tangents.Clone(), material = material });
            surfaceCount++;
        }
        private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
        internal static void Add(Func<StaticModelContext, ModelHandle> selector)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            if (Array.IndexOf(selectors, selector) >= 0) throw new ArgumentException("Selector already registered");
            var next = new Func<StaticModelContext, ModelHandle>[selectors.Length + 1];
            Array.Copy(selectors, next, selectors.Length); next[next.Length - 1] = selector; selectors = next;
        }
        internal static void Remove(Func<StaticModelContext, ModelHandle> selector)
        {
            int index = Array.IndexOf(selectors, selector);
            if (index < 0) return;
            var next = new Func<StaticModelContext, ModelHandle>[selectors.Length - 1];
            Array.Copy(selectors, 0, next, 0, index);
            Array.Copy(selectors, index + 1, next, index, next.Length - index); selectors = next;
        }

        internal static Model Select(Thing thing, Model model, Vector3 center, Quaternion turn, Quaternion lay,
            Vector3 lift, Vector3 scale, Vector3 offset, float baseHeight, float floor)
        {
            if (selectors.Length == 0 || !(thing is Building) || !thing.Spawned || !Ordinary(model)) return model;
            var context = new StaticModelContext { Thing = thing, Model = Api.StaticHandle(model), center = center, turn = turn,
                lay = lay, lift = lift, scale = scale, offset = offset, baseHeight = baseHeight, floor = floor };
            // A callback may add/remove registrations; do not let that invalidate iteration.
            foreach (var selector in selectors)
            {
                try
                {
                    ModelHandle chosen = selector(context);
                    if (chosen == null) continue;
                    if (!Ordinary(chosen.model)) throw new ArgumentException("Unsupported static replacement model");
                    return chosen.model;
                }
                catch (Exception e) { Remove(selector); Log.Warning("[IsoRim] Static model selector disabled; keeping original. " + e); return model; }
            }
            return model;
        }
        internal static bool HasSurface(Thing thing, Model model, Vector3 scale) => surfaceCount != 0 && thing is Building && thing.Spawned
            && scale.x > 0 && scale.y > 0 && scale.z > 0 && surfaces.TryGetValue(model, out Surface surface) && !surface.disabled;

        internal static Material MaterialFor(Model model, int slot, Material original)
        {
            if (original == null || !surfaces.TryGetValue(model, out Surface surface)) return original;
            if (surface.disabled) return original;
            try
            {
                Material result = surface.material(new StaticMaterialContext { Model = Api.StaticHandle(model), Slot = slot, Original = original });
                if (result == null || result == original) return original;
                if (originals.TryGetValue(result, out Material prior) && prior != original)
                    throw new ArgumentException("Replacement material shared across different native materials");
                originals[result] = original;
                return result;
            }
            catch (Exception e) { surface.disabled = true; Log.Warning("[IsoRim] Static material factory disabled; keeping original. " + e); return original; }
        }
        internal static void Append(Model model, int corner, LayerSubMesh mesh, Quaternion turn, Quaternion lay, Vector3 scale)
        {
            if (!originals.TryGetValue(mesh.material, out Material original) || !surfaces.TryGetValue(model, out Surface surface)) return;
            Buffer buffer = buffers.GetOrCreateValue(mesh);
            int start = mesh.verts.Count - 1;
            if (start == 0) { buffer.values.Clear(); buffer.original = original; }
            // Do not mix shared native materials into a custom basis stream.
            if (buffer.values.Count != start || mesh.normals.Count != start) { Fallback(mesh, buffer); return; }
            Vector3 n = lay * surface.normals[corner];
            n = (turn * new Vector3(n.x / scale.x, n.y / scale.y, n.z / scale.z)).normalized;
            Vector4 v = surface.tangents[corner];
            Vector3 t = turn * Vector3.Scale(lay * new Vector3(v.x, v.y, v.z), scale);
            t = (t - n * Vector3.Dot(n, t)).normalized;
            mesh.normals.Add(n);
            buffer.values.Add(new Vector4(t.x, t.y, t.z, v.w));
        }
        // Called by IsoRim's existing FinalizeMesh bridge. No addon patch or new Harmony target.
        internal static void Upload(LayerSubMesh mesh)
        {
            if (surfaceCount == 0 || !buffers.TryGetValue(mesh, out Buffer buffer) || mesh.verts.Count == 0) return;
            if (buffer.values.Count != mesh.verts.Count || mesh.normals.Count != mesh.verts.Count) { Fallback(mesh, buffer); return; }
            mesh.mesh.SetNormals(mesh.normals);
            mesh.mesh.SetTangents(buffer.values);
        }
        private static void Fallback(LayerSubMesh mesh, Buffer buffer)
        {
            if (buffer.original != null) mesh.material = buffer.original;
            buffers.Remove(mesh);
            if (!bufferWarning)
            {
                bufferWarning = true;
                Log.Warning("[IsoRim] Static surface buffer misaligned; original material retained.");
            }
        }
    }
}
