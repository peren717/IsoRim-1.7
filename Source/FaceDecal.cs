using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Facial Animation's face on a 3D head (option faceDecal). With Facial Animation active, a person drawn
    // as a model gets that mod's animated face (eyes, lids, brows, mouth, blush...) instead of IsoRim's eyes: each
    // face layer's south texture is projected straight from the front onto the front of the head model, layer over
    // layer, lit like the head. Facial Animation is read by reflection only (no reference to its assembly). Its own
    // head cover and skin layers are left out: they carry the sprite's dark head outline, which would wrap round the
    // sides of the 3D head. The game does not run a person's render tree while the model stands in for it, so the
    // face's animation is stepped here, once a tick.
    public static class FaceDecal
    {
        // Where the game's head sprite lands on the 3D head. The head sprite (128 px, Facial Animation's 512 px alike) is
        // 49 px tall, the head model 0.505 cells (0.0103 a pixel); IsoRim's own eyes were traced bigger (0.0131). Between
        // the two, so the eyes keep about their size and the mouth (pixel row ~80) still lands on the face rather than
        // under the chin: 0.0115 cells a pixel, the eyes' middle (pixel row 70.6 of 128) at height 0.945.
        private const float SpritePixelCells = 0.0115f;
        internal const float SpriteCells = SpritePixelCells * 128f;             // the whole texture, in cells
        internal const float CentreHeight = 0.945f + (70.6f - 64f) * SpritePixelCells;   // the texture's middle
        private const float HeadMiddle = 0.98f;                                // People.HeadHeight

        private static bool tried, available;
        private static Type partType, masterType;
        private static FieldInfo controllerField, layerTypeField, overrideRotField;
        private static MethodInfo updateMethod;

        private class Face
        {
            public Pawn pawn;
            public PawnRenderNode root;
            public PawnRenderNode master;
            public readonly List<PawnRenderNode> parts = new List<PawnRenderNode>();
            public readonly List<object> controllers = new List<object>();
            public readonly List<object> layers = new List<object>();
            public int tick = -1;
        }

        private static readonly Dictionary<int, Face> faces = new Dictionary<int, Face>();
        private static readonly object south = Rot4.South;   // boxed once (overrideHeadRot is set every frame)

        // Faces of people no longer on a map (gone, dead) are let go now and then; one drawn again is read anew.
        private const int PruneEvery = 2500;
        private static int prunedAt = -PruneEvery;
        private static readonly List<int> gone = new List<int>();

        private static void Prune()
        {
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (now >= prunedAt && now - prunedAt < PruneEvery)
            {
                return;
            }
            prunedAt = now;
            foreach (KeyValuePair<int, Face> e in faces)
            {
                Pawn p = e.Value.pawn;
                if (p == null || p.Destroyed || p.Dead || !p.Spawned)
                {
                    gone.Add(e.Key);
                }
            }
            for (int i = 0; i < gone.Count; i++)
            {
                faces.Remove(gone[i]);
            }
            gone.Clear();
        }

        private static bool Available
        {
            get
            {
                if (!tried)
                {
                    tried = true;
                    Init();
                }
                return available;
            }
        }

        private static void Init()
        {
            try
            {
                if (!ModsConfig.IsActive("Nals.FacialAnimation"))
                {
                    return;
                }
                partType = GenTypes.GetTypeInAnyAssembly("FacialAnimation.NLFacialAnimationPartNode");
                masterType = GenTypes.GetTypeInAnyAssembly("FacialAnimation.NLFacialAnimationMasterNode");
                if (partType == null || masterType == null)
                {
                    return;
                }
                controllerField = partType.GetField("controller");
                layerTypeField = partType.GetField("layerType");
                overrideRotField = partType.GetField("overrideHeadRot");
                updateMethod = masterType.GetMethod("UpdateAnimation", Type.EmptyTypes);
                available = controllerField != null && layerTypeField != null && overrideRotField != null;
                Log.Message("[IsoRim] Facial Animation face on 3D heads: " + (available ? "ready" : "not found"));
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Facial Animation face on 3D heads unavailable: " + e.Message);
                available = false;
            }
        }

        // True if this person's 3D head should carry Facial Animation's face (and leave out IsoRim's eyes).
        public static bool Wanted(Pawn pawn)
        {
            if (!IsoRimMod.Settings.faceDecal || !Available || pawn == null)
            {
                return false;
            }
            Face f = FaceOf(pawn);
            return f != null && f.parts.Count > 0;
        }

        private static Face FaceOf(Pawn pawn)
        {
            try
            {
                PawnRenderTree tree = pawn.Drawer != null && pawn.Drawer.renderer != null ? pawn.Drawer.renderer.renderTree : null;
                if (tree == null)
                {
                    return null;
                }
                if (!tree.Resolved)
                {
                    tree.EnsureInitialized(PawnRenderFlags.None);
                }
                if (tree.rootNode == null)
                {
                    return null;
                }
                Prune();
                Face f;
                if (faces.TryGetValue(pawn.thingIDNumber, out f) && f.root == tree.rootNode)
                {
                    return f;
                }
                f = new Face { pawn = pawn, root = tree.rootNode };
                List<KeyValuePair<float, int>> order = new List<KeyValuePair<float, int>>();
                Stack<PawnRenderNode> open = new Stack<PawnRenderNode>();
                open.Push(tree.rootNode);
                while (open.Count > 0)
                {
                    PawnRenderNode n = open.Pop();
                    if (n == null)
                    {
                        continue;
                    }
                    if (masterType.IsInstanceOfType(n))
                    {
                        f.master = n;
                    }
                    else if (partType.IsInstanceOfType(n))
                    {
                        object controller = controllerField.GetValue(n);
                        string kind = controller != null ? controller.GetType().Name : "";
                        // The head cover and the skin carry the sprite head's outline: left to IsoRim's head.
                        if (controller != null && kind != "HeadControllerComp" && kind != "SkinControllerComp")
                        {
                            order.Add(new KeyValuePair<float, int>(n.Props != null ? n.Props.baseLayer : 0f, f.parts.Count));
                            f.parts.Add(n);
                            f.controllers.Add(controller);
                            f.layers.Add(layerTypeField.GetValue(n));
                        }
                    }
                    if (n.children != null)
                    {
                        for (int i = 0; i < n.children.Length; i++)
                        {
                            open.Push(n.children[i]);
                        }
                    }
                }
                // Drawn bottom layer first, each a little further out.
                order.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value));
                Face sorted = new Face { pawn = pawn, root = f.root, master = f.master };
                foreach (KeyValuePair<float, int> o in order)
                {
                    sorted.parts.Add(f.parts[o.Value]);
                    sorted.controllers.Add(f.controllers[o.Value]);
                    sorted.layers.Add(f.layers[o.Value]);
                }
                faces[pawn.thingIDNumber] = sorted;
                return sorted;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading Facial Animation's face failed: " + e, 93611071);
                return null;
            }
        }

        // Draws the face on the head placed like People's head meshes (position, yaw, and headLocal: the figure's pose
        // and size times the head's widening). head is IsoRim's person_head model.
        public static void Draw(Pawn pawn, Model head, Vector3 position, float yaw, Matrix4x4 headLocal)
        {
            Face f = FaceOf(pawn);
            if (f == null || f.parts.Count == 0 || head == null || FigureScope.Measuring)
            {
                return;
            }
            try
            {
                int tick = Find.TickManager.TicksGame;
                if (f.tick != tick && f.master != null && updateMethod != null)
                {
                    f.tick = tick;
                    updateMethod.Invoke(f.master, null);
                }
                FaceAdjust a = AdjustFor(pawn);
                float size = Mathf.Clamp(a.size, 0.5f, 1.5f);
                Mesh m = MeshFor(head, size, a.height, 1f);
                Mesh mouth = MeshFor(head, size, a.height + a.mouthHeight, Mathf.Clamp(a.mouthSize, 0.5f, 3f));
                if (m == null)
                {
                    return;
                }
                Matrix4x4 placed = Placed(position, yaw, headLocal);
                for (int i = 0; i < f.parts.Count; i++)
                {
                    PawnRenderNode node = f.parts[i];
                    overrideRotField.SetValue(node, south);
                    Graphic g = node.GraphicFor(pawn);
                    Material gm = g != null ? g.MatSouth : null;
                    if (gm == null || gm.mainTexture == null || IsDummy(g))
                    {
                        continue;   // an empty layer
                    }
                    // Facial Animation's own material (its colours, iris mask and soft edges), on the front of the head,
                    // each layer a little further out than the one under it. It writes no depth, so the head's light
                    // pass (queue 3099) still darkens it at night with the head's own cell.
                    bool isMouth = f.controllers[i].GetType().Name == "MouthControllerComp";
                    Graphics.DrawMesh(isMouth && mouth != null ? mouth : m, Drawn(placed, i), gm, 0);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing Facial Animation's face on a 3D head failed: " + e, 93611072);
            }
        }

        // The head's placing as People draws the head (position, yaw, headLocal), drawn space; shared with the face
        // drawers (Api 21, FaceFrame).
        internal static Matrix4x4 Placed(Vector3 position, float yaw, Matrix4x4 headLocal)
        {
            float s = View3D.Squash;
            return Matrix4x4.Translate(new Vector3(position.x, position.y / s, position.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up))
                * headLocal;
        }

        // The matrix face layer i is drawn with (each a little further out from the head's middle than the one under
        // it; -1: the head itself), moved as the figure's parts are (FigureScope) and onto its stacked floor.
        internal static Matrix4x4 Drawn(Matrix4x4 placed, int i)
        {
            if (i < 0)
            {
                return Floors.DrawnMatrix(FigureScope.Apply(placed));
            }
            float grow = 1.008f + 0.003f * i;
            Matrix4x4 local = Matrix4x4.Translate(new Vector3(0f, HeadMiddle, 0f))
                * Matrix4x4.Scale(new Vector3(grow, grow, grow))
                * Matrix4x4.Translate(new Vector3(0f, -HeadMiddle, 0f));
            return Floors.DrawnMatrix(FigureScope.Apply(placed * local));
        }

        // The face's placing for this person: the global sliders, then the first worn apparel (outermost first) that
        // has its own: the player's override from the settings, else the apparel def's FaceDecalAdjust (XML, fields
        // left out keep the global value).
        private static readonly FaceAdjust adjust = new FaceAdjust();

        internal static FaceAdjust AdjustFor(Pawn pawn)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            adjust.size = st.faceDecalSize;
            adjust.height = st.faceDecalHeight;
            adjust.mouthSize = st.faceDecalMouth;
            adjust.mouthHeight = st.faceDecalMouthHeight;
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            if (worn == null || worn.Count == 0)
            {
                return adjust;
            }
            for (int i = worn.Count - 1; i >= 0; i--)
            {
                ThingDef def = worn[i].def;
                FaceAdjust own = st.FaceAdjustOf(def.defName);
                if (own != null)
                {
                    adjust.size = own.size;
                    adjust.height = own.height;
                    adjust.mouthSize = own.mouthSize;
                    adjust.mouthHeight = own.mouthHeight;
                    return adjust;
                }
                FaceDecalAdjust ext = def.GetModExtension<FaceDecalAdjust>();
                if (ext != null)
                {
                    ext.Apply(adjust);
                    return adjust;
                }
            }
            return adjust;
        }

        // Facial Animation's empty layer (GraphicPool.dummyGraphic, Things/Common/dummy): see-through in the game's
        // shaders, but opaque grey under the head's cut-out material.
        private static bool IsDummy(Graphic g)
        {
            return g.path != null && g.path.EndsWith("/dummy", StringComparison.OrdinalIgnoreCase);
        }

        // Where Facial Animation's mouth is in its texture (u, v from the bottom): the mouth's own size is set around it.
        private static readonly Vector2 MouthCentre = new Vector2(0.5f, 1f - 319.5f / 512f);

        // The projected meshes made so far, by size, height and mouth zoom (a few: the global sliders and any apparel's).
        private static readonly Dictionary<long, Mesh> meshes = new Dictionary<long, Mesh>();
        private static Model meshesOf;

        // The front of the head model, its texture coordinates projected straight from the front. The .obj files face
        // -Z, but loading flips Z (right- to left-handed), so a loaded figure faces +Z. zoom enlarges the texture round
        // the mouth (1 for the rest of the face).
        internal static Mesh MeshFor(Model head, float size, float shift, float zoom)
        {
            if (meshesOf != head || meshes.Count > 64)
            {
                foreach (Mesh old in meshes.Values)
                {
                    if (old != null)
                    {
                        DynamicModels.Retire(old);
                    }
                }
                meshes.Clear();
                meshesOf = head;
            }
            long key = ((long)Mathf.RoundToInt(size * 1000f) << 42) ^ ((long)(Mathf.RoundToInt(shift * 1000f) + 100000) << 21) ^ (Mathf.RoundToInt(zoom * 1000f) + 100000);
            Mesh m;
            if (!meshes.TryGetValue(key, out m))
            {
                m = Build(head, size, shift, zoom);
                meshes[key] = m;
            }
            return m;
        }

        private static Mesh Build(Model head, float size, float shift, float zoom)
        {
            float cells = SpriteCells * size;
            List<Vector3> verts = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<Color32> colors = new List<Color32>();
            List<int> tris = new List<int>();
            Vector3 middle = new Vector3(0f, HeadMiddle, 0f);
            Vector3[] p = head.positions;
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                Vector3 a = p[i], b = p[i + 1], c = p[i + 2];
                Vector3 n = Vector3.Cross(b - a, c - a);
                Vector3 centroid = (a + b + c) / 3f;
                if (Vector3.Dot(n, centroid - middle) < 0f)
                {
                    n = -n;   // outward
                }
                if (n.sqrMagnitude < 1e-12f || n.normalized.z < 0.05f)
                {
                    continue;   // only what faces the front
                }
                foreach (Vector3 v in new[] { a, b, c })
                {
                    tris.Add(verts.Count);
                    verts.Add(v);
                    // Seen from the front (+Z, looking back along -Z) the viewer's right is -X.
                    Vector2 uv = new Vector2(0.5f - v.x / cells, 0.5f + (v.y - CentreHeight - shift) / cells);
                    uvs.Add(MouthCentre + (uv - MouthCentre) / zoom);
                    colors.Add(new Color32(255, 255, 255, 255));
                }
            }
            if (verts.Count == 0)
            {
                return null;
            }
            Mesh m = new Mesh();
            m.SetVertices(verts);
            m.SetColors(colors);
            m.SetUVs(0, uvs);
            List<Vector3> tags = new List<Vector3>(verts.Count);
            for (int i = 0; i < verts.Count; i++)
            {
                tags.Add(RoofClip.MovingTag);
            }
            m.SetUVs(1, tags);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }
    }
}

namespace IsoRim
{
    // A placing of Facial Animation's face on 3D heads: the global sliders' values, or one apparel's override (settings).
    public class FaceAdjust : IExposable
    {
        public string apparel;   // the apparel def's defName (overrides only)
        public float size = 1f;
        public float height;
        public float mouthSize = 1.6f;
        public float mouthHeight;

        public void ExposeData()
        {
            Scribe_Values.Look(ref apparel, "apparel");
            Scribe_Values.Look(ref size, "size", 1f);
            Scribe_Values.Look(ref height, "height", 0f);
            Scribe_Values.Look(ref mouthSize, "mouthSize", 1.6f);
            Scribe_Values.Look(ref mouthHeight, "mouthHeight", 0f);
        }
    }

    // For apparel modders: how Facial Animation's face sits on a 3D head while this is worn (a mask, a helmet, a tall
    // collar). Any field left out keeps the global slider; the player's own override in the settings wins over this.
    //   <modExtensions><li Class="IsoRim.FaceDecalAdjust"><height>0.03</height><mouthHeight>0.02</mouthHeight></li></modExtensions>
    public class FaceDecalAdjust : DefModExtension
    {
        public float size = float.NaN;
        public float height = float.NaN;
        public float mouthSize = float.NaN;
        public float mouthHeight = float.NaN;

        public void Apply(FaceAdjust a)
        {
            if (!float.IsNaN(size)) a.size = size;
            if (!float.IsNaN(height)) a.height = height;
            if (!float.IsNaN(mouthSize)) a.mouthSize = mouthSize;
            if (!float.IsNaN(mouthHeight)) a.mouthHeight = mouthHeight;
        }
    }
}
