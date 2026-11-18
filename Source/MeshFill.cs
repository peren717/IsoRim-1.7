using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;
using W = Waylith.Graphics.Meshes;

namespace IsoRim
{
    // A map sub-mesh finalized in the 3D view is written straight into the engine's mesh storage, in one pass from the
    // sub-mesh's lists, in place of the game's LayerSubMesh.FinalizeMesh body.
    //
    // Why: the game's body hands each list to UnityEngine.Mesh (SetVertices, SetTriangles, SetColors, SetUVs,
    // SetNormals), and the 1.7 engine's Mesh keeps a managed copy of every one (each uv channel widened to four floats),
    // then copies those arrays corner by corner into its native storage the first time the engine mesh is asked for
    // (Mesh.RebuildEngineStorage). A forest block is about 100,000 corners: 6.4 MB of short-lived arrays and 5 of its
    // 12 ms; a zoom out to the limit rebuilt 180 blocks = half a gigabyte and six to eight collections.
    //
    // What the game does, line by line (Verse.LayerSubMesh.FinalizeMesh(MeshParts), game 1.7.5011), and what is done
    // here: the table is BodyCalls below (it also names the engine's Mesh members the game's body calls, the one list of
    // them). A list set by the game whose length is not the corner count is kept by the engine's Mesh but left out of
    // its storage: that case is not taken here either. disabled is not touched by the game, nor here.
    //
    // What the skipped body changes for other mods' Harmony patches, and when this turns itself off because of one
    // (Alone, asked as ForeignPatches.Schedule says; one log line, the game's body runs for the session):
    // - a transpiler or a Harmony 3 inner patch on FinalizeMesh lives in the body: off;
    // - a prefix sorted after ours would not run (unless it returns nothing and takes nothing by reference): off;
    // - a postfix or finalizer on FinalizeMesh that asks whether the body ran (__runOriginal) would be told "no": off;
    // - a patch on one of the Mesh members the body calls (BodyCalls) would not be reached for map sub-meshes: off.
    // Other postfixes and finalizers on FinalizeMesh run as before, after the fill, and see the finished mesh.
    //
    // Not taken, by our own choice: a sub-mesh whose normals and tangents IsoRim sets again right after the finalize
    // (static surfaces, StaticModelExtensions.Uploads). A set after a direct fill makes the engine read every list back
    // from the storage and build the storage a second time: slower than the game's body, which is what runs for those.
    //
    // What the engine then builds (UnityEngine.Mesh.RebuildEngineStorage in UnityStubs, Waylith 1.7.5011), copied here:
    // one vertex stream holding, in this order, position (3 x Float32), normal (3 x Float32) if set, colour (4 x UNorm8)
    // if set, texture coordinate 0 (4 x Float32: x, y, z, 0) if set, texture coordinate 1 the same if set; the index
    // buffer in the mesh's index format (16-bit unless the mesh was set to 32-bit, or has more than 65,535 corners, or
    // an index above 65,535: that last, broken, case is not taken here); one sub-mesh of triangles over all the indices;
    // the box of the positions (MeshBounds.Encapsulate of each) as the sub-mesh's and the mesh's bounds.
    //
    // How it is put in place: a Waylith.Graphics.Meshes.WritableMeshData (public: it wraps a new CpuMeshData, the class
    // RebuildEngineStorage fills) is filled and handed to the private UnityEngine.Mesh.ApplyWritableMeshData(
    // WritableMeshData), which is the whole body of the public Mesh.ApplyAndDisposeWritableMeshData. (The public one
    // only takes the Unity-style MeshData, whose SetSubMesh turns the bounds into centre and extents and back, not bit
    // for bit, and which has no way to set the mesh's bounds: the storage would need a touch-up after the upload was
    // asked for.) ApplyWritableMeshData and RebuildEngineStorage both end in the engine mesh's
    // ReplaceCpuStorage(storage): one version step, one upload request. The uploader (MeshUploader.RecordMeshUpload,
    // on the render thread) reads only the mesh's storage, residency and version: nothing tells it how the storage was
    // filled. So the GPU gets the same bytes through the same calls. ApplyWritableMeshData also forgets the Mesh's
    // managed copies; mesh.vertices and the like read them back from the storage when asked (the engine's own lazy load).
    // A game update that renames ApplyWritableMeshData or WritableMeshData, or changes that signature: this turns
    // itself off with one log line (Look).
    //
    // Watching the copy. Api.WatchCopy is NOT used for these two, on purpose: on the 1.7 host the IL reflection gives
    // for a method changes with the Harmony patches on it (measured 2026-11-17: FinalizeMesh hashed bb9a84fedfce8099
    // with no patch on it, 10da70301895a693 with IsoRim's three patches and d9bc6c5e0b4dbbc4 with another owner's
    // added; RebuildEngineStorage, unpatched, stayed the same), and WatchCopy checks after loading, when FinalizeMesh
    // carries our own patches at least. So both
    // are checked by CopyWatch.AddNow (the same hash, the same warning) in Patches.ApplyAll before IsoRim's first patch
    // on FinalizeMesh, each only while no one has patched it yet; if another mod got there first the watch is skipped.
    //
    // The proof at first use covers both sources whether watched or not (Proved): 36 small sub-meshes, each finalized
    // by the game's own body (every hook on it running) and its lists then written by this code through the same
    // ApplyWritableMeshData, the two engine storages compared field by field and byte by byte.
    // - The engine's layout rules (RebuildEngineStorage): meshes 1 to 32 = every combination of normals, colours and the
    //   two texture coordinates (16 layouts; the seven a map really uses are among them: position alone; + colour;
    //   + colour + coordinate 0; + colour + both; + colour + coordinate 1; + normal + colour + both), each once on a
    //   16-bit and once on a 32-bit mesh (the index buffer's two formats).
    // - The game's order of sets (FinalizeMesh): the same 32 with every part asked for (each list set or left unset as
    //   it is empty or not), and meshes 33 to 36 with all lists filled but only some parts asked for (corners and
    //   triangles alone; those with colours and coordinate 1), on both index formats: a part not asked for is not set.
    // Any difference or any exception here turns this off for the session, like the patches listed above: the game's
    // body runs, as before.
    // Only while View3D.Ready (the 3D view is on: sub-meshes of other maps and hidden floors finalized meanwhile are
    // filled too, the same bytes); never while the 2D view is shown.
    internal static unsafe class MeshFill
    {
        internal const string GameMethod = "Verse.LayerSubMesh.FinalizeMesh";               // copied: game 1.7.5011
        internal const string EngineMethod = "UnityEngine.Mesh.RebuildEngineStorage";       // copied: UnityStubs, Waylith 1.7.5011
        // Api.CopyHash of the two when they were copied, taken with no Harmony patch on them (CopyWatch.AddNow says once
        // if either has changed since; see "Watching the copy" above).
        internal const string GameHash = "bb9a84fedfce8099";
        internal const string EngineHash = "96e478f046948203";

        // Harness: the game's body for every sub-mesh (the reference of the runner's ffcheck), and what was done.
        public static bool Off;
        public static int Filled, Passed;

        private static bool failed;        // an exception or a difference: off for the session
        private static bool proved;        // the first-use proof has passed
        private static bool proving;       // the proof is running (the game's body is wanted)
        private static bool looked;
        private static Action<Mesh, W.WritableMeshData> apply;   // UnityEngine.Mesh.ApplyWritableMeshData
        private static MethodBase target;                        // LayerSubMesh.FinalizeMesh
        private static MethodInfo self;                          // our prefix on it
        private static readonly ForeignPatches.Schedule looks = new ForeignPatches.Schedule();
        private static bool alone;                               // the last look found nothing of another mod's in the way
        private static MethodBase[] bodyCalls;                   // every overload of the Mesh members in BodyCalls
        private static W.VertexAttribute[][] layouts;            // by Flags

        // The largest index (and corner count) the engine's 16-bit index format holds: the engine's own limit.
        private const int MaxIndex16 = ushort.MaxValue;

        // What the game's body does, line by line (Verse.LayerSubMesh.FinalizeMesh(MeshParts), game 1.7.5011), and what
        // is done here in its place. The entries are the engine's Mesh members the body calls, in its order: the one
        // list of them (Alone looks at each for another mod's patches, which a skipped body would not reach).
        private static readonly string[] BodyCalls =
        {
            //  1. finalized already: a warning, then goes on                   -> not taken here (the game's body runs)
            //  2. parts has Verts or Tris: mesh.Clear()                        -> the storage is replaced whole (see 9)
            nameof(Mesh.Clear),
            //  3. parts has Verts: verts.Count > 0 ? mesh.SetVertices(verts) : an error
            //                                                                  -> taken only with Verts, verts.Count > 0
            nameof(Mesh.SetVertices),
            //  4. parts has Tris: tris.Count > 0 ? mesh.SetTriangles(tris, 0) : an error
            //                                                                  -> taken only with Tris, tris.Count > 0
            nameof(Mesh.SetTriangles),
            //  5. parts has Colors and colors.Count > 0: mesh.SetColors(colors) -> the colours, if one per corner
            nameof(Mesh.SetColors),
            //  6. parts has UVs1 and uvs.Count > 0: mesh.SetUVs(0, uvs)        -> texture coordinate 0, if one per corner
            //  7. parts has UVs2 and uvsChannelTwo.Count > 0: mesh.SetUVs(1, ..) -> texture coordinate 1, if one per corner
            nameof(Mesh.SetUVs),
            //  8. parts has Normals and normals.Count > 0: mesh.SetNormals(..) -> the normals, if one per corner
            nameof(Mesh.SetNormals),
            //  9. finalized = true                                             -> finalized = true
        };

        [Flags]
        private enum Has { None = 0, Normals = 1, Colours = 2, Uv0 = 4, Uv1 = 8 }

        // (Patches) the prefix this class runs as, for the order check.
        internal static void Hooked(MethodBase original, MethodInfo prefix)
        {
            target = original;
            self = prefix;
        }

        // LayerSubMesh.FinalizeMesh prefix (last of the prefixes). True: finalized here, the game's body is skipped.
        public static bool Finalize(LayerSubMesh sm, MeshParts parts)
        {
            if (failed || Off || proving || target == null || !View3D.Ready || !UnityData.IsInMainThread)
            {
                return false;
            }
            // The plain case only (see the list above); anything else is the game's.
            if ((parts & MeshParts.Verts) == MeshParts.None || (parts & MeshParts.Tris) == MeshParts.None || sm.finalized || sm.mesh == null)
            {
                Passed++;
                return false;
            }
            int n = sm.verts.Count;
            if (n == 0 || sm.tris.Count == 0 || StaticModelExtensions.Uploads(sm))
            {
                Passed++;
                return false;   // (the last: set again right after the finalize, see the header)
            }
            Has has = Has.None;
            if (!Wanted(parts, MeshParts.Colors, sm.colors.Count, n, Has.Colours, ref has)
                || !Wanted(parts, MeshParts.UVs1, sm.uvs.Count, n, Has.Uv0, ref has)
                || !Wanted(parts, MeshParts.UVs2, sm.uvsChannelTwo.Count, n, Has.Uv1, ref has)
                || !Wanted(parts, MeshParts.Normals, sm.normals.Count, n, Has.Normals, ref has))
            {
                Passed++;
                return false;
            }
            try
            {
                if (!Alone() || !Usable() || !Proved())
                {
                    Passed++;
                    return false;
                }
                if (!Fill(sm.mesh, sm, has))
                {
                    Passed++;
                    return false;
                }
                sm.finalized = true;
                Filled++;
                return true;
            }
            catch (Exception e)
            {
                Fail("an error: " + e);
                return false;   // (nothing was applied, or the game's body sets the whole mesh again)
            }
        }

        // A list the game would set (its part asked for, not empty) must hold one entry per corner: then it is in the
        // engine's storage. Not asked for or empty: not set, not in the storage. Any other length: not the plain case.
        private static bool Wanted(MeshParts parts, MeshParts part, int count, int corners, Has flag, ref Has has)
        {
            if ((parts & part) == MeshParts.None || count == 0)
            {
                return true;
            }
            if (count != corners)
            {
                return false;
            }
            has |= flag;
            return true;
        }

        // The storage the game's body and the engine would end with, built aside and put in place whole. False (nothing
        // touched): an index the 16-bit format cannot hold.
        private static bool Fill(Mesh mesh, LayerSubMesh sm, Has has)
        {
            Look();
            Span<Vector3> verts = CollectionsMarshal.AsSpan(sm.verts);
            Span<int> tris = CollectionsMarshal.AsSpan(sm.tris);
            int n = verts.Length, m = tris.Length;
            // The index format as the engine's Mesh ends with: the mesh's own (Mesh.Clear keeps it), widened by
            // SetVertices for more than 65,535 corners (and by SetTriangles for an index above that: not taken, below).
            bool wide = mesh.indexFormat == IndexFormat.UInt32 || n > MaxIndex16;
            W.WritableMeshData data = new W.WritableMeshData();
            try
            {
                data.SetVertexBufferParams(n, layouts[(int)has]);
                data.SetIndexBufferParams(m, wide ? W.MeshIndexFormat.UInt32 : W.MeshIndexFormat.UInt16);
                data.SetSubMeshCount(1);
                W.VertexLayout layout = data.Layout;
                int stride = data.GetVertexBufferStride(0);
                byte* p = (byte*)data.GetVertexDataPointer(0);
                if (stride != Size(has) || data.GetVertexDataByteLength(0) != (long)n * stride
                    || data.GetIndexDataByteLength() != (long)m * (wide ? 4 : 2))
                {
                    throw new InvalidOperationException("the engine's vertex layout is not the expected one (stride " + stride + ")");
                }

                W.MeshBounds box = W.MeshBounds.Empty;
                int at = Offset(layout, W.VertexAttributeSemantic.Position);
                for (int i = 0; i < n; i++)
                {
                    Vector3 v = verts[i];
                    float* f = (float*)(p + (long)i * stride + at);
                    f[0] = v.x;
                    f[1] = v.y;
                    f[2] = v.z;
                    box = box.Encapsulate(v.x, v.y, v.z);
                }
                if ((has & Has.Normals) != 0)
                {
                    Span<Vector3> normals = CollectionsMarshal.AsSpan(sm.normals);
                    at = Offset(layout, W.VertexAttributeSemantic.Normal);
                    for (int i = 0; i < n; i++)
                    {
                        Vector3 v = normals[i];
                        float* f = (float*)(p + (long)i * stride + at);
                        f[0] = v.x;
                        f[1] = v.y;
                        f[2] = v.z;
                    }
                }
                if ((has & Has.Colours) != 0)
                {
                    Span<Color32> colours = CollectionsMarshal.AsSpan(sm.colors);
                    at = Offset(layout, W.VertexAttributeSemantic.Color);
                    for (int i = 0; i < n; i++)
                    {
                        Color32 c = colours[i];
                        byte* b = p + (long)i * stride + at;
                        b[0] = c.r;
                        b[1] = c.g;
                        b[2] = c.b;
                        b[3] = c.a;
                    }
                }
                if ((has & Has.Uv0) != 0)
                {
                    Wide(CollectionsMarshal.AsSpan(sm.uvs), p, stride, Offset(layout, W.VertexAttributeSemantic.TexCoord0));
                }
                if ((has & Has.Uv1) != 0)
                {
                    Wide(CollectionsMarshal.AsSpan(sm.uvsChannelTwo), p, stride, Offset(layout, W.VertexAttributeSemantic.TexCoord1));
                }

                if (wide)
                {
                    tris.CopyTo(new Span<int>((void*)data.GetIndexDataPointer(), m));
                }
                else
                {
                    ushort* ix = (ushort*)data.GetIndexDataPointer();
                    for (int i = 0; i < m; i++)
                    {
                        int t = tris[i];
                        if ((uint)t > (uint)MaxIndex16)
                        {
                            return false;   // the engine's Mesh would go to 32-bit (or it is a broken index): the game's body
                        }
                        ix[i] = (ushort)t;
                    }
                }
                data.SetSubMesh(0, new W.SubMesh(0, m, W.MeshTopology.Triangles, 0, box));
                data.SetBounds(box);
                apply(mesh, data);   // (takes the storage out of data)
                return true;
            }
            finally
            {
                data.Dispose();   // (frees the storage if it was not applied)
            }
        }

        // A texture coordinate list as the engine keeps it: x, y, z, 0.
        private static void Wide(Span<Vector3> uvs, byte* p, int stride, int at)
        {
            for (int i = 0; i < uvs.Length; i++)
            {
                Vector3 v = uvs[i];
                float* f = (float*)(p + (long)i * stride + at);
                f[0] = v.x;
                f[1] = v.y;
                f[2] = v.z;
                f[3] = 0f;
            }
        }

        private static int Offset(W.VertexLayout layout, W.VertexAttributeSemantic semantic)
        {
            W.VertexAttribute a;
            if (!layout.TryGetAttribute(semantic, out a) || a.Stream != 0 || a.OffsetBytes < 0)
            {
                throw new InvalidOperationException("the engine's vertex layout has no " + semantic);
            }
            return a.OffsetBytes;
        }

        // The bytes of one corner: what is written above, nothing in between.
        private static int Size(Has has)
        {
            return 12 + ((has & Has.Normals) != 0 ? 12 : 0) + ((has & Has.Colours) != 0 ? 4 : 0)
                + ((has & Has.Uv0) != 0 ? 16 : 0) + ((has & Has.Uv1) != 0 ? 16 : 0);
        }

        private static void Look()
        {
            if (looked)
            {
                return;
            }
            looked = true;
            // void UnityEngine.Mesh.ApplyWritableMeshData(Waylith.Graphics.Meshes.WritableMeshData), an instance method:
            // exactly that, or not at all.
            MethodInfo m = AccessTools.DeclaredMethod(typeof(Mesh), "ApplyWritableMeshData", new[] { typeof(W.WritableMeshData) });
            if (m == null || m.IsStatic || m.ReturnType != typeof(void) || m.GetParameters().Length != 1
                || m.GetParameters()[0].ParameterType != typeof(W.WritableMeshData))
            {
                apply = null;
                return;
            }
            apply = AccessTools.MethodDelegate<Action<Mesh, W.WritableMeshData>>(m);
            // The attribute lists RebuildEngineStorage makes, by what is set: position, normal, (tangent: never set by a
            // map sub-mesh), colour, texture coordinates 0 and 1.
            layouts = new W.VertexAttribute[16][];
            for (int k = 0; k < 16; k++)
            {
                Has has = (Has)k;
                List<W.VertexAttribute> list = new List<W.VertexAttribute>(5);
                list.Add(new W.VertexAttribute(W.VertexAttributeSemantic.Position, W.VertexAttributeFormat.Float32, 3));
                if ((has & Has.Normals) != 0)
                {
                    list.Add(new W.VertexAttribute(W.VertexAttributeSemantic.Normal, W.VertexAttributeFormat.Float32, 3));
                }
                if ((has & Has.Colours) != 0)
                {
                    list.Add(new W.VertexAttribute(W.VertexAttributeSemantic.Color, W.VertexAttributeFormat.UNorm8, 4));
                }
                if ((has & Has.Uv0) != 0)
                {
                    list.Add(new W.VertexAttribute(W.VertexAttributeSemantic.TexCoord0, W.VertexAttributeFormat.Float32, 4));
                }
                if ((has & Has.Uv1) != 0)
                {
                    list.Add(new W.VertexAttribute(W.VertexAttributeSemantic.TexCoord1, W.VertexAttributeFormat.Float32, 4));
                }
                layouts[k] = list.ToArray();
            }
        }

        // The engine's own way to put a filled storage in place was found, as expected (Look).
        private static bool Usable()
        {
            Look();
            if (apply == null)
            {
                Stop("the engine's UnityEngine.Mesh.ApplyWritableMeshData(WritableMeshData) was not found in the expected form");
                return false;
            }
            return true;
        }

        // ---- who else is on FinalizeMesh, and on what its body calls

        // Skipping the body is only right while nothing of another mod's depends on it running (the cases are listed in
        // the header). Looked at when ForeignPatches.Schedule says (first use, every few seconds, after a map was
        // built), not every frame: a look reads Harmony's patch records.
        private static bool Alone()
        {
            if (!looks.Due())
            {
                return alone;
            }
            alone = false;
            if (!LookNow(out string why))
            {
                if (why != null)
                {
                    Stop(why);
                }
                return false;   // (no reason given: our own prefix is not on it, nothing to skip with)
            }
            alone = true;
            return true;
        }

        // One look: what of another mod's a skipped body would bypass or mislead, in words; null if nothing.
        private static string InTheWay(HarmonyLib.Patches info)
        {
            string who = ForeignPatches.First(info, ForeignPatches.Kind.InBody);
            if (who != null)
            {
                return "another mod rewrites " + GameMethod + " (" + who + ")";
            }
            // Harmony does not run the prefixes after one that skips the body, unless they return nothing and take
            // nothing by reference: ours must be the last.
            Patch[] prefixes = new Patch[info.Prefixes.Count];
            info.Prefixes.CopyTo(prefixes, 0);
            List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(target, prefixes);
            if (sorted.Count == 0 || sorted[sorted.Count - 1] != self)
            {
                return "a prefix on " + GameMethod + " runs after IsoRim's (" + (sorted.Count > 0 ? sorted[sorted.Count - 1].DeclaringType + "." + sorted[sorted.Count - 1].Name : "none") + ")";
            }
            who = ForeignPatches.First(info, ForeignPatches.Kind.Postfix | ForeignPatches.Kind.Finalizer, ForeignPatches.AsksRunOriginal);
            if (who != null)
            {
                return "a patch on " + GameMethod + " asks whether the game's code ran (" + who + ")";
            }
            if (bodyCalls == null)
            {
                List<MethodBase> list = new List<MethodBase>();
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(Mesh)))
                {
                    if (Array.IndexOf(BodyCalls, m.Name) >= 0 && !m.IsGenericMethodDefinition)
                    {
                        list.Add(m);
                    }
                }
                bodyCalls = list.ToArray();
            }
            for (int i = 0; i < bodyCalls.Length; i++)
            {
                who = ForeignPatches.First(bodyCalls[i], ForeignPatches.Kind.All);
                if (who != null)
                {
                    return "another mod patches UnityEngine.Mesh." + bodyCalls[i].Name + ", which " + GameMethod + " calls (" + who + ")";
                }
            }
            return null;
        }

        // One look (also the harness's: the perf runner measures what a look costs). False with no reason: not hooked.
        public static bool LookNow(out string why)
        {
            why = null;
            HarmonyLib.Patches info = target != null && self != null ? Harmony.GetPatchInfo(target) : null;   // (read once)
            if (info == null)
            {
                return false;
            }
            why = InTheWay(info);
            return why == null;
        }

        // ---- the proof at first use

        // One small sub-mesh per layout and index format, finalized by the game's body on one mesh, then its lists written
        // by Fill into another: the two engine storages must be the same in every field and byte.
        private static bool Proved()
        {
            if (proved)
            {
                return true;
            }
            Look();
            // 1 to 32: every layout on both index formats, every part asked for
            for (int k = 0; k < 16; k++)
            {
                for (int w = 0; w < 2; w++)
                {
                    string why = Differs((Has)k, w == 1, MeshParts.All);
                    if (why != null)
                    {
                        Fail("its test mesh (" + (Has)k + (w == 1 ? ", 32-bit" : ", 16-bit") + ") came out different from the game's: " + why);
                        return false;
                    }
                }
            }
            // 33 to 36: every list filled, only some parts asked for
            for (int p = 0; p < SomeParts.Length; p++)
            {
                for (int w = 0; w < 2; w++)
                {
                    string why = Differs(Has.Normals | Has.Colours | Has.Uv0 | Has.Uv1, w == 1, SomeParts[p]);
                    if (why != null)
                    {
                        Fail("its test mesh (parts " + SomeParts[p] + (w == 1 ? ", 32-bit" : ", 16-bit") + ") came out different from the game's: " + why);
                        return false;
                    }
                }
            }
            proved = true;
            return true;
        }

        private static readonly MeshParts[] SomeParts =
        {
            MeshParts.Verts | MeshParts.Tris,
            MeshParts.Verts | MeshParts.Tris | MeshParts.Colors | MeshParts.UVs2
        };

        private static string Differs(Has has, bool wide, MeshParts parts)
        {
            Mesh a = new Mesh(), b = new Mesh();
            try
            {
                if (wide)
                {
                    a.indexFormat = IndexFormat.UInt32;
                    b.indexFormat = IndexFormat.UInt32;
                }
                // (a real material: a hook of another mod's on FinalizeMesh may read it)
                LayerSubMesh game = new LayerSubMesh(a, BaseContent.ClearMat);
                for (int i = 0; i < 7; i++)
                {
                    // (values no rounding could hide: thirds, negatives, an infinity-free spread)
                    Vector3 v = new Vector3(i / 3f - 1f, 0.1f * i * i, -7.25f + i * 1.7f);
                    game.verts.Add(v);
                    if ((has & Has.Normals) != 0)
                    {
                        Vector3 nrm = new Vector3(0.6f, -0.8f + i * 0.01f, i * 0.123f);
                        game.normals.Add(nrm);
                    }
                    if ((has & Has.Colours) != 0)
                    {
                        Color32 c = new Color32((byte)(i * 37), (byte)(255 - i * 11), (byte)(i * i), (byte)(90 + i));
                        game.colors.Add(c);
                    }
                    if ((has & Has.Uv0) != 0)
                    {
                        Vector3 uv = new Vector3(i / 7f, 1f - i / 9f, i * 0.5f);
                        game.uvs.Add(uv);
                    }
                    if ((has & Has.Uv1) != 0)
                    {
                        Vector3 uv = new Vector3(-i, i * 3.3f, 1f / (i + 1));
                        game.uvsChannelTwo.Add(uv);
                    }
                }
                int[] order = { 0, 1, 2, 2, 3, 4, 4, 5, 6, 6, 0, 3 };
                game.tris.AddRange(order);
                proving = true;
                try
                {
                    game.FinalizeMesh(parts);   // the game's body, with every hook on it
                }
                finally
                {
                    proving = false;
                }
                // (a hook may have changed the lists, RoofClip pads texture coordinate 1: ours is filled from the same)
                Has now = Has.None;
                int n = game.verts.Count;
                if (!Wanted(parts, MeshParts.Colors, game.colors.Count, n, Has.Colours, ref now)
                    || !Wanted(parts, MeshParts.UVs1, game.uvs.Count, n, Has.Uv0, ref now)
                    || !Wanted(parts, MeshParts.UVs2, game.uvsChannelTwo.Count, n, Has.Uv1, ref now)
                    || !Wanted(parts, MeshParts.Normals, game.normals.Count, n, Has.Normals, ref now))
                {
                    return null;   // (not a plain case after the hooks: never filled here either)
                }
                if (!Fill(b, game, now))
                {
                    return "not filled";
                }
                return Compare(a.EngineMesh.CpuStorage, b.EngineMesh.CpuStorage);
            }
            finally
            {
                UnityEngine.Object.Destroy(a);
                UnityEngine.Object.Destroy(b);
            }
        }

        // Null if two engine storages are the same: counts, index format, layout, sub-meshes, bounds, every byte.
        internal static string Compare(W.CpuMeshData a, W.CpuMeshData b)
        {
            if (a == null || b == null)
            {
                return "no storage";
            }
            if (a.VertexCount != b.VertexCount || a.IndexCount != b.IndexCount || a.IndexFormat != b.IndexFormat)
            {
                return "counts or index format (" + a.VertexCount + "/" + a.IndexCount + "/" + a.IndexFormat + " and " + b.VertexCount + "/" + b.IndexCount + "/" + b.IndexFormat + ")";
            }
            ReadOnlySpan<W.VertexAttribute> la = a.Layout.Attributes, lb = b.Layout.Attributes;
            if (la.Length != lb.Length || a.Layout.StreamCount != b.Layout.StreamCount)
            {
                return "layout size";
            }
            for (int i = 0; i < la.Length; i++)
            {
                if (la[i].Semantic != lb[i].Semantic || la[i].Format != lb[i].Format || la[i].Dimension != lb[i].Dimension
                    || la[i].Stream != lb[i].Stream || la[i].OffsetBytes != lb[i].OffsetBytes)
                {
                    return "layout attribute " + i;
                }
            }
            for (int s = 0; s < a.Layout.StreamCount; s++)
            {
                if (a.Layout.GetStreamStride(s) != b.Layout.GetStreamStride(s))
                {
                    return "stride of stream " + s;
                }
                W.NativeByteBuffer va = a.GetVertexStream(s), vb = b.GetVertexStream(s);
                if (va.LengthBytes != vb.LengthBytes
                    || !new ReadOnlySpan<byte>((void*)va.Pointer, va.LengthBytes).SequenceEqual(new ReadOnlySpan<byte>((void*)vb.Pointer, vb.LengthBytes)))
                {
                    return "vertex bytes of stream " + s;
                }
            }
            if (a.IndexCount > 0)
            {
                W.NativeByteBuffer ia = a.GetIndexBuffer(), ib = b.GetIndexBuffer();
                if (ia.LengthBytes != ib.LengthBytes
                    || !new ReadOnlySpan<byte>((void*)ia.Pointer, ia.LengthBytes).SequenceEqual(new ReadOnlySpan<byte>((void*)ib.Pointer, ib.LengthBytes)))
                {
                    return "index bytes";
                }
            }
            W.SubMesh[] sa = a.SubMeshes, sb = b.SubMeshes;
            if (sa.Length != sb.Length)
            {
                return "sub-mesh count";
            }
            for (int i = 0; i < sa.Length; i++)
            {
                if (sa[i].IndexStart != sb[i].IndexStart || sa[i].IndexCount != sb[i].IndexCount || sa[i].BaseVertex != sb[i].BaseVertex
                    || sa[i].Topology != sb[i].Topology || !Same(sa[i].Bounds, sb[i].Bounds))
                {
                    return "sub-mesh " + i;
                }
            }
            return Same(a.Bounds, b.Bounds) ? null : "bounds";
        }

        private static bool Same(W.MeshBounds a, W.MeshBounds b)
        {
            return Bits(a.MinX, b.MinX) && Bits(a.MinY, b.MinY) && Bits(a.MinZ, b.MinZ)
                && Bits(a.MaxX, b.MaxX) && Bits(a.MaxY, b.MaxY) && Bits(a.MaxZ, b.MaxZ);
        }

        private static bool Bits(float a, float b)
        {
            return BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        }

        // ---- off

        // Not an error: something of another mod's on FinalizeMesh that skipping the body would bypass.
        private static void Stop(string why)
        {
            failed = true;
            Log.Message("[IsoRim] Map meshes are finalized by the game's own code this session: " + why + ".");
        }

        private static void Fail(string why)
        {
            failed = true;
            proving = false;
            Log.ErrorOnce("[IsoRim] Writing map meshes straight into the engine's storage is off until the game restarts (the game's own code does it, as before): " + why, 93611304);
        }
    }
}
