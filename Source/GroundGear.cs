using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Apparel and weapons lying on the map look like what a colonist wears or holds, not like a generic model for their
    // category (with "items and weapons from the game's art" off; with it on they are the game's own sprite made solid,
    // as before, which is also what a held weapon is):
    //   - a weapon: its own sprite made solid (Extruded), as a 3D colonist holds it (FigureWeapons, SpriteSolids);
    //   - apparel, while colonists are 3D figures: the same 3D clothes a figure wears (People.Dress, the list the outfit
    //     stands use: People.ClothesParts), on the adult male build, at the figure's size, laid down: what goes on the
    //     body lies on its back, front up, collar to the north (as the game's sprite), squashed to Flatten of its depth;
    //     what goes only on the head (hats, helmets, masks) stands on its rim, front to the south. Turned as the game turns
    //     the sprite (onGroundRandomRotateAngle; none on a shelf), shrunk to fit its cell, 0.8 where the game draws several
    //     items in a cell. Baked into the map mesh like any model: nothing is drawn per frame.
    //   - apparel with no 3D part on a figure (glasses, most belts) and anything when figures are off: the sprite made solid.
    // A model another mod binds to the very thing (ModelDef thingDef) still wins over all of this.
    public static class GroundGear
    {
        public const float Height = 0.12f;      // cells: what lies on it rests this high (Forms.HeightOf)
        private const float MaxFoot = 0.92f;    // cells: the longest side of what lies in a cell
        private const string Build = "male";    // the figure build the clothes are taken from
        internal static float Flatten = 0.5f;   // a laid garment's depth (front to back), as a share of the worn depth (harness: groundflat=)
        internal static bool BareAsSprite = false;   // (harness A/B groundbare=1) a garment that only colours the bare body (shirt, trousers): its sprite instead
        internal static bool CullDown = false;   // (harness A/B groundcull=1) faces turned to the ground left out: -28% vertices, ~-8% block rebuild time, small holes
        private const float DownLimit = -0.5f;   // a face whose direction points further down than this is left out
        internal static int Bakes;               // harness counters (sectiontime): items baked, time dressing and writing, model vertices
        internal static long DressTicks, WriteTicks, Verts;

        // Whether the look of this item lying on the map is GroundGear's: apparel and equippable weapons, unless another mod
        // binds a model of its own to this very thing.
        public static bool Replaces(ThingDef def, ModelEntry entry)
        {
            if (def == null || def.category != ThingCategory.Item || IsoRimMod.Settings.itemsFromArt)
            {
                return false;
            }
            if (!def.IsApparel && def.equipmentType != EquipmentType.Primary)
            {
                return false;
            }
            return entry == null || !entry.HasModel || ModelLibrary.HowBound(def) != "thingDef" || OwnDef(entry.def);
        }

        // Apparel drawn as the clothes a figure wears (else: from its sprite).
        public static bool WornLook(ThingDef def)
        {
            return def.IsApparel && IsoRimMod.Settings.pawns3D;
        }

        // A binding of IsoRim's own (the original author's per-weapon models), which GroundGear replaces.
        private static bool OwnDef(ModelDef md)
        {
            return md.modContentPack != null && IsoRimMod.RootDir != null
                && string.Equals(md.modContentPack.RootDir.TrimEnd('/', '\\'), IsoRimMod.RootDir.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ the shape

        // An item's clothes, posed to lie on the ground (model space = cells, origin at the middle of its footprint on the
        // ground), shared by every item that dresses into the same parts.
        private sealed class Shape
        {
            public Model[] parts;
            public float fit;   // scale that fits it in its cell
        }

        private static readonly Dictionary<string, Shape> shapes = new Dictionary<string, Shape>();
        private static float shapesFlatten = -1f;
        private static bool shapesCull;

        private static readonly List<Model> bodyModels = new List<Model>();
        private static readonly List<Matrix4x4> bodyLocals = new List<Matrix4x4>();
        private static readonly List<Color> bodyTints = new List<Color>();
        private static readonly List<Model> headModels = new List<Model>();
        private static readonly List<Matrix4x4> headLocals = new List<Matrix4x4>();
        private static readonly List<Color> headTints = new List<Color>();
        private static readonly List<Apparel> one = new List<Apparel>(1);
        private static readonly Action<Model, Matrix4x4, Color> addBody = (m, l, c) => Add(bodyModels, bodyLocals, bodyTints, m, l, c);
        private static readonly Action<Model, Matrix4x4, Color> addHead = (m, l, c) => Add(headModels, headLocals, headTints, m, l, c);

        private static void Add(List<Model> models, List<Matrix4x4> locals, List<Color> tints, Model m, Matrix4x4 l, Color c)
        {
            if (m == null || m.positions == null || m.positions.Length == 0)
            {
                return;
            }
            models.Add(m);
            locals.Add(l);
            tints.Add(c);
        }

        // Dresses a bare figure in this one item (as a colonist wearing only it) and keeps what it puts on: the parts in
        // bodyModels / headModels, their colours as on a colonist.
        private static void Dress(Apparel a)
        {
            bodyModels.Clear();
            bodyLocals.Clear();
            bodyTints.Clear();
            headModels.Clear();
            headLocals.Clear();
            headTints.Clear();
            one.Clear();
            one.Add(a);
            People.Look look = new People.Look();
            People.Dress(look, Build, one, Color.white, null);
            People.ClothesParts(look, Build, addBody, addHead);
            one.Clear();
        }

        private static Shape ShapeFor(ThingDef def)
        {
            if (shapesFlatten != Flatten || shapesCull != CullDown)
            {
                shapes.Clear();
                shapesFlatten = Flatten;
                shapesCull = CullDown;
            }
            StringBuilder sb = new StringBuilder(def.defName);
            for (int i = 0; i < bodyModels.Count; i++)
            {
                sb.Append("|b").Append(bodyModels[i].id).Append(':').Append(bodyLocals[i].GetHashCode());
            }
            for (int i = 0; i < headModels.Count; i++)
            {
                sb.Append("|h").Append(headModels[i].id).Append(':').Append(headLocals[i].GetHashCode());
            }
            string key = sb.ToString();
            Shape shape;
            if (!shapes.TryGetValue(key, out shape))
            {
                shape = Pose();
                shapes[key] = shape;
            }
            return shape;
        }

        // The parts gathered by Dress, posed. Anything on the body lies on its back (the figure faces +z, its head up +y:
        // front turned up, collar to the north, depth squashed); a head-only item stands as worn, its front to the south.
        private static Shape Pose()
        {
            bool laid = bodyModels.Count > 0;
            Matrix4x4 pose = laid
                ? Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.up)) * Matrix4x4.Rotate(Quaternion.AngleAxis(-90f, Vector3.right))
                : Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.up));
            List<Matrix4x4> placed = new List<Matrix4x4>();
            List<Model> sources = new List<Model>();
            for (int i = 0; i < bodyModels.Count; i++)
            {
                sources.Add(bodyModels[i]);
                placed.Add(pose * bodyLocals[i]);
            }
            for (int i = 0; i < headModels.Count; i++)
            {
                sources.Add(headModels[i]);
                placed.Add(pose * headLocals[i]);
            }

            // The box all of it covers, posed; then moved onto the ground, centred, and a laid garment squashed.
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < sources.Count; i++)
            {
                Vector3[] pos = sources[i].positions;
                for (int v = 0; v < pos.Length; v++)
                {
                    Vector3 p = placed[i].MultiplyPoint3x4(pos[v]);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            float squash = laid ? Mathf.Clamp(Flatten, 0.05f, 1f) : 1f;
            Matrix4x4 settle = Matrix4x4.Scale(new Vector3(1f, squash, 1f))
                * Matrix4x4.Translate(new Vector3(-(min.x + max.x) * 0.5f, -min.y, -(min.z + max.z) * 0.5f));

            Shape shape = new Shape();
            shape.parts = new Model[sources.Count];
            for (int i = 0; i < sources.Count; i++)
            {
                shape.parts[i] = Posed(sources[i], settle * placed[i]);
            }
            float foot = Mathf.Max(max.x - min.x, max.z - min.z);
            shape.fit = foot > MaxFoot ? MaxFoot / foot : 1f;
            return shape;
        }

        // A copy of model m moved by matrix t (points, face directions and outline pushes with it), without the faces turned to
        // the ground (no camera over the map sees them; half the mesh of a garment lying down).
        private static Model Posed(Model m, Matrix4x4 t)
        {
            Model c = PosedAll(m, t);
            return CullDown ? WithoutDown(c) : c;
        }

        private static Model WithoutDown(Model c)
        {
            int n = c.positions.Length;
            List<int> keep = new List<int>(n);
            for (int i = 0; i + 2 < n; i += 3)
            {
                Vector3 face = c.normals != null ? c.normals[i] : Vector3.Cross(c.positions[i + 1] - c.positions[i], c.positions[i + 2] - c.positions[i]).normalized;
                if (face.y >= DownLimit)
                {
                    keep.Add(i);
                    keep.Add(i + 1);
                    keep.Add(i + 2);
                }
            }
            if (keep.Count == n)
            {
                return c;
            }
            c.positions = Pick(c.positions, keep);
            c.normals = Pick(c.normals, keep);
            c.hull = Pick(c.hull, keep);
            c.colors = Pick(c.colors, keep);
            c.tinted = Pick(c.tinted, keep);
            c.uvs = Pick(c.uvs, keep);
            c.material = Pick(c.material, keep);
            c.noOutline = Pick(c.noOutline, keep);
            return c;
        }

        private static T[] Pick<T>(T[] from, List<int> keep)
        {
            if (from == null)
            {
                return null;
            }
            T[] to = new T[keep.Count];
            for (int k = 0; k < keep.Count; k++)
            {
                to[k] = from[keep[k]];
            }
            return to;
        }

        private static Model PosedAll(Model m, Matrix4x4 t)
        {
            int n = m.positions.Length;
            Matrix4x4 normalT = t.inverse.transpose;
            Model c = new Model();
            c.positions = new Vector3[n];
            c.normals = m.normals != null ? new Vector3[m.normals.Length] : null;
            c.hull = m.hull != null ? new Vector3[m.hull.Length] : null;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, maxY = 0f;
            for (int v = 0; v < n; v++)
            {
                Vector3 p = t.MultiplyPoint3x4(m.positions[v]);
                c.positions[v] = p;
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z);
                maxZ = Mathf.Max(maxZ, p.z);
                maxY = Mathf.Max(maxY, p.y);
            }
            for (int v = 0; c.normals != null && v < c.normals.Length; v++)
            {
                c.normals[v] = normalT.MultiplyVector(m.normals[v]).normalized;
            }
            for (int v = 0; c.hull != null && v < c.hull.Length; v++)
            {
                c.hull[v] = t.MultiplyVector(m.hull[v]);
            }
            c.colors = m.colors;
            c.tinted = m.tinted;
            c.uvs = m.uvs;
            c.material = m.material;
            c.materials = m.materials;
            c.noOutline = m.noOutline;
            c.maxY = maxY;
            c.minX = minX;
            c.maxX = maxX;
            c.minZ = minZ;
            c.maxZ = maxZ;
            c.halfDepth = Mathf.Max(maxZ, -minZ);
            c.id = ObjLoader.NewId();
            return c;
        }

        // ------------------------------------------------------------------ printing

        // Writes the apparel lying here into the block's mesh in place of its sprite (Patches.TakePrintFrom_Prefix). False
        // (nothing written) if it puts nothing on a figure: the caller prints its sprite.
        public static bool Bake(SectionLayer layer, Thing thing, float baseHeight)
        {
            Apparel a = thing as Apparel;
            if (a == null || !WornLook(thing.def))
            {
                return false;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Dress(a);
            if (BareAsSprite && OnlyBody())
            {
                return false;   // no shape of its own on a figure, only the body in its colour: the sprite made solid
            }
            if (bodyModels.Count + headModels.Count == 0)
            {
                return false;
            }
            Shape shape = ShapeFor(thing.def);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            List<Color> tints = new List<Color>(bodyTints);
            tints.AddRange(headTints);

            float scale = shape.fit * (thing.MultipleItemsPerCellDrawn() ? 0.8f : 1f);
            Quaternion turn = Quaternion.AngleAxis(Turn(thing), Vector3.up);
            Vector3 center = thing.DrawPos;
            int mark = RoofClip.Mark(layer);
            bool any = false;
            try
            {
                for (int i = 0; i < shape.parts.Length; i++)
                {
                    any |= ModelBaker.WritePosed(layer, thing, shape.parts[i], center, turn, scale, baseHeight, tints[i], 1f);
                }
            }
            finally
            {
                RoofClip.Tag(layer, mark, thing.Position);
            }
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            Bakes++;
            DressTicks += t1 - t0;
            WriteTicks += t2 - t1;
            for (int i = 0; i < shape.parts.Length; i++)
            {
                Verts += shape.parts[i].positions.Length;
            }
            return any;
        }

        // Whether the clothes gathered by Dress are only the figure's own body parts in the item's colour (a plain shirt, trousers).
        private static bool OnlyBody()
        {
            if (headModels.Count > 0 || bodyModels.Count == 0)
            {
                return false;
            }
            Model upper = People.Part("person_" + Build + "_upper"), lower = People.Part("person_" + Build + "_lower");
            for (int i = 0; i < bodyModels.Count; i++)
            {
                if (bodyModels[i] != upper && bodyModels[i] != lower)
                {
                    return false;
                }
            }
            return true;
        }

        // The game's turn for an apparel sprite on the ground (Graphic_RandomRotated; its rack rule is for weapons only): a set angle per item.
        private static float Turn(Thing thing)
        {
            GraphicData data = thing.def.graphicData;
            float max = data != null ? data.onGroundRandomRotateAngle : 0f;
            if (max <= 0.01f)
            {
                return 0f;
            }
            return -max + (float)(thing.thingIDNumber * 542) % (max * 2f);
        }
    }
}
