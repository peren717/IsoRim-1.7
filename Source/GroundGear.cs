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
    //   - a weapon: its own sprite made solid (Extruded), as a 3D colonist holds it (FigureWeapons, SpriteSolids); outfit
    //     stands and mechs hold the same (HeldSprite);
    //   - apparel, while colonists are 3D figures: the same 3D clothes a figure wears (People.Dress, the list the outfit
    //     stands use: People.ClothesParts), on the adult male build (child-only apparel: the child's build and size), at
    //     the figure's size, laid down: what goes on the body lies on its back, front up, collar to the north (as the
    //     game's sprite), squashed to Flatten of its depth; what goes only on the head (hats, helmets, masks) stands on its
    //     rim, front to the south. Turned as the game turns the sprite (onGroundRandomRotateAngle; none on a shelf), shrunk
    //     only if longer than its cell, 0.8 where the game draws several items in a cell. Baked into the map mesh: nothing
    //     is drawn per frame;
    //   - apparel that puts no shape of its own on a figure (a plain shirt or trousers only colour the bare body; glasses,
    //     most belts put on nothing) and anything when figures are off: the game's sprite made solid.
    // A model another mod binds to the very thing (ModelDef thingDef) still wins over all of this.
    //
    // The laid clothes are a simplified copy of the worn parts, made once per shape (def, build, parts) whatever the colours
    // and reused by every item that dresses the same: the parts welded, merged where flattest (Decimate), without the faces
    // no camera over the map can see, faces of one colour sharing their corners (an indexed mesh). Each item only adds its
    // colours as it is written: a map block full of laid clothes rebuilds about as fast as with their sprites.
    public static class GroundGear
    {
        public const float Height = 0.12f;      // cells: what lies on it rests this high (Forms.HeightOf)
        private const float MaxFoot = 0.92f;    // cells: the longest side of what lies in a cell
        private const float Flatten = 0.5f;     // a laid garment's depth (front to back), as a share of the worn depth
        private const float Weld = 0.002f;      // cells: corners closer than this are one corner
        private const float Keep = 0.5f;        // share of the welded faces kept (Decimate) ...
        private const float MaxError = 0.008f;  // ... unless that moves the surface further than this (cells, mean)
        private const float Hidden = 0.87f;     // |face direction.y| past which a face is never seen from the camera's
                                                // lowest pitch (30°): the model's faces turned down, the outline's turned up
        private const int MaxShapes = 1024;     // shapes kept before the cache starts over

        // Whether the look of this item lying on the map is GroundGear's: apparel and weapons (IsGear), unless another mod
        // binds a model of its own to this very thing.
        public static bool Replaces(ThingDef def, ModelEntry entry)
        {
            return def != null && def.category == ThingCategory.Item && !IsoRimMod.Settings.itemsFromArt && Gear(def, entry);
        }

        // Whether a held weapon of this def (outfit stands, mechs: Patches.HeldWeapon3D) is its own sprite made solid, as a 3D
        // colonist holds it: with the game's art, or without it unless another mod binds a model of its own to this very weapon.
        public static bool HeldSprite(ThingDef def)
        {
            if (IsoRimMod.Settings.itemsFromArt)
            {
                return true;
            }
            ModelEntry entry;
            return def != null && Gear(def, ModelLibrary.TryGet(def, out entry) ? entry : null);
        }

        private static bool Gear(ThingDef def, ModelEntry entry)
        {
            if (!IsGear(def))
            {
                return false;
            }
            return entry == null || !entry.HasModel || ModelLibrary.HowBound(def) != "thingDef" || OwnDef(entry.def);
        }

        // Real gear: apparel, and weapons as the game files them (equippable and under the Weapons category, the rule of its
        // weapon filters). Not what can merely be wielded: wood, tusks and horns (ResourceVerbBase), a beer bottle; those keep
        // their own look, as before GroundGear.
        public static bool IsGear(ThingDef def)
        {
            if (def.IsApparel)
            {
                return true;
            }
            return def.equipmentType == EquipmentType.Primary && ThingCategoryDefOf.Weapons != null && def.IsWithinCategory(ThingCategoryDefOf.Weapons);
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

        // Apparel only children wear: dressed on the child's build, at a child's size.
        internal static bool ChildOnly(ThingDef def)
        {
            return def.apparel != null && (def.apparel.developmentalStageFilter & DevelopmentalStage.Adult) == 0
                && (def.apparel.developmentalStageFilter & DevelopmentalStage.Child) != 0;
        }

        private static string BuildFor(ThingDef def)
        {
            return ChildOnly(def) ? People.BuildOf(BodyTypeDefOf.Child) : "male";
        }

        private static float SizeFor(ThingDef def)
        {
            LifeStageDef child = LifeStageDefOf.HumanlikeChild;
            return ChildOnly(def) && child != null ? People.SizeFor(child.bodySizeFactor) : 1f;
        }

        // ------------------------------------------------------------------ dressing

        // What Dress put on the figure, body parts first, then head parts (the order of a shape's parts and of tints).
        private static readonly List<Model> models = new List<Model>();
        private static readonly List<Matrix4x4> locals = new List<Matrix4x4>();
        private static readonly List<Color> tints = new List<Color>();
        private static int bodyCount;
        private static readonly List<Model> headModels = new List<Model>();
        private static readonly List<Matrix4x4> headLocals = new List<Matrix4x4>();
        private static readonly List<Color> headTints = new List<Color>();
        private static readonly List<Apparel> one = new List<Apparel>(1);
        private static readonly Action<Model, Matrix4x4, Color> addBody = (m, l, c) => Add(models, locals, tints, m, l, c);
        private static readonly Action<Model, Matrix4x4, Color> addHead = (m, l, c) => Add(headModels, headLocals, headTints, m, l, c);

        private static void Add(List<Model> ms, List<Matrix4x4> ls, List<Color> ts, Model m, Matrix4x4 l, Color c)
        {
            if (m == null || m.positions == null || m.positions.Length == 0)
            {
                return;
            }
            ms.Add(m);
            ls.Add(l);
            ts.Add(c);
        }

        // Dresses a bare figure of this build in this one item (as a colonist wearing only it) and keeps what it puts on, in
        // models / locals / tints (the colours as on a colonist): bodyCount body parts, then the head's.
        private static void Dress(Apparel a, string build)
        {
            models.Clear();
            locals.Clear();
            tints.Clear();
            headModels.Clear();
            headLocals.Clear();
            headTints.Clear();
            one.Clear();
            one.Add(a);
            People.Look look = new People.Look();
            People.Dress(look, build, one, Color.white, null);
            People.ClothesParts(look, build, addBody, addHead);
            one.Clear();
            bodyCount = models.Count;
            models.AddRange(headModels);
            locals.AddRange(headLocals);
            tints.AddRange(headTints);
        }

        // Whether the clothes gathered by Dress are only the figure's own body parts in the item's colour (a plain shirt,
        // trousers): no shape of its own, so the item keeps the game's sprite.
        private static bool OnlyBody(string build)
        {
            if (models.Count == 0 || bodyCount < models.Count)
            {
                return false;
            }
            Model upper = People.Part("person_" + build + "_upper"), lower = People.Part("person_" + build + "_lower");
            for (int i = 0; i < models.Count; i++)
            {
                if (models[i] != upper && models[i] != lower)
                {
                    return false;
                }
            }
            return true;
        }

        // What an apparel def lies as, for the model sweep: "worn" (its clothes laid down), "plain" (only colours the body:
        // its sprite), "none" (puts nothing on a figure: its sprite); with the triangles of its laid shape (lit + outline)
        // against the worn parts' triangles. Dresses an unspawned copy in its default stuff.
        public static string LookOf(ThingDef def, out int laidTris, out int wornTris)
        {
            laidTris = wornTris = 0;
            Apparel a = def.IsApparel ? ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null) as Apparel : null;
            if (a == null)
            {
                return "";
            }
            string build = BuildFor(def);
            Dress(a, build);
            if (models.Count == 0)
            {
                return "none";
            }
            if (OnlyBody(build))
            {
                return "plain";
            }
            for (int i = 0; i < models.Count; i++)
            {
                wornTris += models[i].positions.Length / 3;
            }
            Shape shape = ShapeFor(def, build, SizeFor(def));
            laidTris = (shape.lit.index.Length + shape.glow.index.Length + shape.line.index.Length) / 3;
            for (int i = 0; i < shape.slowParts.Length; i++)
            {
                laidTris += shape.slowParts[i].positions.Length * 2 / 3;
            }
            return "worn";
        }

        // ------------------------------------------------------------------ the simplified shape

        // One indexed triangle list of a shape: its corners in model space, each with its entry in the shape's palette (corners
        // shared by faces of one colour), and the corners of each triangle.
        private sealed class Tris
        {
            public Vector3[] pos;
            public int[] pal;
            public int[] index;
        }

        // Gathers a Tris: one corner per weld and palette entry.
        private sealed class TrisBuilder
        {
            private readonly List<Vector3> pos = new List<Vector3>();
            private readonly List<int> pal = new List<int>();
            private readonly List<int> index = new List<int>();
            private readonly Dictionary<long, int> corner = new Dictionary<long, int>();

            public void Add(int weld, Vector3 p, int palEntry)
            {
                long key = ((long)weld << 24) | (uint)palEntry;
                int i;
                if (!corner.TryGetValue(key, out i))
                {
                    i = pos.Count;
                    corner[key] = i;
                    pos.Add(p);
                    pal.Add(palEntry);
                }
                index.Add(i);
            }

            public Tris Done()
            {
                return new Tris { pos = pos.ToArray(), pal = pal.ToArray(), index = index.ToArray() };
            }
        }

        // An item's clothes posed to lie on the ground and simplified (model space = cells, origin at the middle of its
        // footprint on the ground, before the item's scale and turn): the lit faces, the faces that glow, the outline shell.
        // Parts with a texture keep their full model (slowParts, tinted as part slowPart).
        private sealed class Shape
        {
            public Tris lit, glow, line;
            public Color[] palColor;   // the palette: a colour before the item's tint ...
            public int[] palPart;      // ... and the part whose tint it takes (-1: none)
            public Model[] slowParts;
            public int[] slowPart;
            public float fit;   // scale that fits it in its cell (times the figure size)
        }

        private static readonly Dictionary<string, Shape> shapes = new Dictionary<string, Shape>();
        private static int shapesStamp = -1;
        private static readonly StringBuilder keyText = new StringBuilder();

        private static Shape ShapeFor(ThingDef def, string build, float size)
        {
            if (shapesStamp != View3D.Stamp || shapes.Count > MaxShapes)
            {
                shapes.Clear();
                shapesStamp = View3D.Stamp;
            }
            StringBuilder sb = keyText;
            sb.Length = 0;
            sb.Append(def.defName).Append('|').Append(build).Append('|').Append(size).Append('|').Append(bodyCount);
            for (int i = 0; i < models.Count; i++)
            {
                sb.Append('|').Append(models[i].id).Append(':').Append(locals[i].GetHashCode());
            }
            string key = sb.ToString();
            Shape shape;
            if (!shapes.TryGetValue(key, out shape))
            {
                shape = Build(size);
                shapes[key] = shape;
            }
            return shape;
        }

        // One corner of the merged, posed clothes, before welding.
        private struct Corner
        {
            public Vector3 pos, hull;
            public Color color;
            public int part;    // whose tint it takes; -1 none
            public bool line;   // its triangle gets an outline
        }

        private static readonly List<Corner> corners = new List<Corner>();
        private static readonly List<bool> cornerGlow = new List<bool>();   // per triangle

        // The parts gathered by Dress, posed, merged and simplified. Anything on the body lies on its back (the figure faces
        // -z, its head up +y: front turned up, collar to the north, depth squashed); a head-only item stands as worn, its
        // front to the south.
        private static Shape Build(float size)
        {
            bool laidDown = bodyCount > 0;
            Matrix4x4 pose = laidDown
                ? Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.up)) * Matrix4x4.Rotate(Quaternion.AngleAxis(-90f, Vector3.right))
                : Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.up));

            // The box all of it covers, posed; then moved onto the ground, centred, and a laid garment squashed.
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < models.Count; i++)
            {
                Matrix4x4 placed = pose * locals[i];
                Vector3[] pos = models[i].positions;
                for (int v = 0; v < pos.Length; v++)
                {
                    Vector3 p = placed.MultiplyPoint3x4(pos[v]);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            float squash = laidDown ? Flatten : 1f;
            Matrix4x4 settle = Matrix4x4.Scale(new Vector3(1f, squash, 1f))
                * Matrix4x4.Translate(new Vector3(-(min.x + max.x) * 0.5f, -min.y, -(min.z + max.z) * 0.5f));

            Shape shape = new Shape();
            float foot = Mathf.Max(max.x - min.x, max.z - min.z) * size;
            shape.fit = size * (foot > MaxFoot ? MaxFoot / foot : 1f);

            bool textures = IsoRimMod.Settings.texturedModels;
            List<Model> slow = new List<Model>();
            List<int> slowPart = new List<int>();
            corners.Clear();
            cornerGlow.Clear();
            for (int i = 0; i < models.Count; i++)
            {
                Matrix4x4 t = settle * pose * locals[i];
                if (textures && Textured(models[i]))
                {
                    slow.Add(PosedCopy(models[i], t));
                    slowPart.Add(i);
                    continue;
                }
                AddCorners(models[i], t, i);
            }
            // Welded and merged in the size it is drawn at (a cell's worth at most): the same detail on every item.
            Simplify(shape, 1f / Mathf.Max(0.01f, shape.fit));
            shape.slowParts = slow.ToArray();
            shape.slowPart = slowPart.ToArray();
            corners.Clear();
            cornerGlow.Clear();
            return shape;
        }

        private static bool Textured(Model m)
        {
            if (m.materials == null || m.uvs == null || m.material == null)
            {
                return false;
            }
            for (int i = 0; i + 2 < m.material.Length; i += 3)
            {
                ModelMaterial mm = m.materials[m.material[i]];
                if (mm != null && mm.texture != null)
                {
                    return true;   // a face drawn with a texture (the material library may hold others it never uses)
                }
            }
            return false;
        }

        // The corners of model m moved by t, with their colours as ModelBaker.Write takes them (tinted by part's tint where the
        // model says so). A mirroring t has its triangles' corners swapped back to the model's winding.
        private static void AddCorners(Model m, Matrix4x4 t, int part)
        {
            int n = m.positions.Length;
            bool mirrored = Vector3.Dot(Vector3.Cross(t.GetColumn(0), t.GetColumn(1)), t.GetColumn(2)) < 0f;
            for (int i = 0; i + 2 < n; i += 3)
            {
                ModelMaterial mm = m.materials != null && m.material != null ? m.materials[m.material[i]] : null;
                if (mm != null && (mm.twig || mm.fire))
                {
                    continue;   // as Write: no hidden twigs, no flame (nothing a garment has)
                }
                cornerGlow.Add(mm != null && mm.glow);
                for (int j = 0; j < 3; j++)
                {
                    int k = i + (mirrored && j > 0 ? 3 - j : j);
                    Corner cn = new Corner();
                    cn.pos = t.MultiplyPoint3x4(m.positions[k]);
                    cn.hull = m.hull != null ? t.MultiplyVector(m.hull[k]) : Vector3.zero;
                    cn.color = m.colors[k];
                    cn.part = m.tinted[k] ? part : -1;
                    cn.line = m.hull != null && (m.noOutline == null || !m.noOutline[k]);
                    corners.Add(cn);
                }
            }
        }

        // Welds the gathered corners (each weld at the mean of its corners and of their outline pushes), merges the faces where
        // the surface is flattest (Decimate), drops the triangles that collapse or repeat, and sorts the rest into the lit,
        // glowing and outline lists, leaving out the model faces turned so far down, and the outline faces so far up, that no
        // camera over the map sees them (the GPU would cull them anyway: no holes). perCell: model units per drawn cell.
        private static void Simplify(Shape shape, float perCell)
        {
            int n = corners.Count;
            Dictionary<long, int> welds = new Dictionary<long, int>(n / 2 + 1);
            int[] weldOf = new int[n];
            List<Vector3> sumPos = new List<Vector3>();
            List<Vector3> sumHull = new List<Vector3>();
            List<int> count = new List<int>();
            float inv = 1f / (Weld * perCell);
            for (int k = 0; k < n; k++)
            {
                Vector3 p = corners[k].pos;
                long key = ((long)(Mathf.FloorToInt(p.x * inv) + 1048576) << 42) | ((long)(Mathf.FloorToInt(p.y * inv) + 1048576) << 21)
                    | (long)(Mathf.FloorToInt(p.z * inv) + 1048576);
                int w;
                if (!welds.TryGetValue(key, out w))
                {
                    w = sumPos.Count;
                    welds[key] = w;
                    sumPos.Add(Vector3.zero);
                    sumHull.Add(Vector3.zero);
                    count.Add(0);
                }
                weldOf[k] = w;
                sumPos[w] += p;
                sumHull[w] += corners[k].hull;
                count[w]++;
            }
            Vector3[] wpos = new Vector3[sumPos.Count];
            Vector3[] whull = new Vector3[sumPos.Count];
            for (int w = 0; w < wpos.Length; w++)
            {
                wpos[w] = sumPos[w] / count[w];
                whull[w] = sumHull[w] / count[w];
            }

            // Faces of different colours (or tints) keep the edge between them.
            int[] colorOf = new int[n / 3];
            for (int t = 0; t < n / 3; t++)
            {
                Color32 c = corners[t * 3].color;
                colorOf[t] = c.r | (c.g << 8) | (c.b << 16) | ((corners[t * 3].part + 1) << 24) | (cornerGlow[t] ? 1 << 30 : 0);
            }
            // (One merged shape for the faces and the outline shell: a shell merged on its own pokes through the faces.)
            Decimate.Run(wpos, whull, weldOf, colorOf, Keep, MaxError * perCell);

            TrisBuilder lit = new TrisBuilder(), glow = new TrisBuilder(), line = new TrisBuilder();
            palette.Clear();
            palColors.Clear();
            palParts.Clear();
            Emit(wpos, whull, weldOf, false, lit, glow);
            Emit(wpos, whull, weldOf, true, line, null);
            shape.lit = lit.Done();
            shape.glow = glow.Done();
            shape.line = line.Done();
            shape.palColor = palColors.ToArray();
            shape.palPart = palParts.ToArray();
        }

        // The triangles left after Decimate (pos, hull, weldOf): the model's faces (outline false: lit or glowing, not those
        // turned too far down) or the outline shell's (pushed out by hull, not those turned too far up).
        private static void Emit(Vector3[] wpos, Vector3[] whull, int[] weldOf, bool outline, TrisBuilder into, TrisBuilder glowInto)
        {
            int n = corners.Count;
            HashSet<long> seen = new HashSet<long>();
            for (int i = 0, tri = 0; i + 2 < n; i += 3, tri++)
            {
                int a = weldOf[i], b = weldOf[i + 1], c = weldOf[i + 2];
                if (a == b || b == c || a == c)
                {
                    continue;   // collapsed
                }
                Vector3 after = Vector3.Cross(wpos[b] - wpos[a], wpos[c] - wpos[a]);
                if (after.sqrMagnitude < 1e-14f)
                {
                    continue;   // no area left
                }
                // The same triangle twice (two parts welded together): once is enough.
                int lo = Mathf.Min(a, Mathf.Min(b, c));
                int r0 = lo == a ? a : lo == b ? b : c;
                int r1 = lo == a ? b : lo == b ? c : a;
                int r2 = lo == a ? c : lo == b ? a : b;
                if (!seen.Add(((long)r0 << 42) | ((long)r1 << 21) | (long)r2))
                {
                    continue;
                }
                // Which way it faces (clockwise seen from outside, Unity's front face: the cross points outward).
                float upward = after.normalized.y;
                if (!outline && upward >= -Hidden)
                {
                    TrisBuilder to = cornerGlow[tri] ? glowInto : into;
                    to.Add(a, wpos[a], PaletteOf(i));
                    to.Add(b, wpos[b], PaletteOf(i + 1));
                    to.Add(c, wpos[c], PaletteOf(i + 2));
                }
                else if (outline && upward <= Hidden && corners[i].line && corners[i + 1].line && corners[i + 2].line)
                {
                    into.Add(a, wpos[a] + whull[a], PaletteOf(i));
                    into.Add(b, wpos[b] + whull[b], PaletteOf(i + 1));
                    into.Add(c, wpos[c] + whull[c], PaletteOf(i + 2));
                }
            }
        }

        private static readonly Dictionary<long, int> palette = new Dictionary<long, int>();
        private static readonly List<Color> palColors = new List<Color>();
        private static readonly List<int> palParts = new List<int>();

        // The palette entry of corner k's colour and tinting part.
        private static int PaletteOf(int k)
        {
            Color c = corners[k].color;
            long key = ((long)(corners[k].part + 1) << 48) ^ ((long)Mathf.RoundToInt(c.r * 4095f) << 32)
                ^ ((long)Mathf.RoundToInt(c.g * 4095f) << 16) ^ Mathf.RoundToInt(c.b * 4095f);
            int i;
            if (!palette.TryGetValue(key, out i))
            {
                i = palColors.Count;
                palette[key] = i;
                palColors.Add(c);
                palParts.Add(corners[k].part);
            }
            return i;
        }

        // A full copy of model m moved by matrix t (a textured part, written by ModelBaker as it is).
        private static Model PosedCopy(Model m, Matrix4x4 t)
        {
            int n = m.positions.Length;
            Matrix4x4 normalT = t.inverse.transpose;
            Model c = new Model();
            c.positions = new Vector3[n];
            c.normals = m.normals != null ? new Vector3[m.normals.Length] : null;
            c.hull = m.hull != null ? new Vector3[m.hull.Length] : null;
            float maxY = 0f;
            for (int v = 0; v < n; v++)
            {
                c.positions[v] = t.MultiplyPoint3x4(m.positions[v]);
                maxY = Mathf.Max(maxY, c.positions[v].y);
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
            c.id = ObjLoader.NewId();
            return c;
        }

        // ------------------------------------------------------------------ printing

        private static Color[] itemTints = new Color[8];
        private static Color32[] litPal = new Color32[32], linePal = new Color32[32];   // the item's colours of its shape's palette

        // Writes the apparel lying here into the block's mesh in place of its sprite (Patches.TakePrintFrom_Prefix). False
        // (nothing written) if it has no shape of its own on a figure: the caller prints its sprite.
        public static bool Bake(SectionLayer layer, Thing thing, float baseHeight)
        {
            Apparel a = thing as Apparel;
            if (a == null || !WornLook(thing.def) || ModelBaker.DamageMarks > 0 || ModelBaker.Ghost.a > 0f)
            {
                return false;
            }
            string build = BuildFor(thing.def);
            Dress(a, build);
            if (models.Count == 0 || OnlyBody(build))
            {
                return false;   // nothing of its own on a figure (or only the body in its colour): the sprite made solid
            }
            Shape shape = ShapeFor(thing.def, build, SizeFor(thing.def));
            if (itemTints.Length < tints.Count)
            {
                itemTints = new Color[tints.Count * 2];
            }
            tints.CopyTo(itemTints);
            int np = shape.palColor.Length;
            if (litPal.Length < np)
            {
                litPal = new Color32[np * 2];
                linePal = new Color32[np * 2];
            }
            for (int i = 0; i < np; i++)
            {
                // As ModelBaker.Write colours: the colour times the part's tint where tinted; the outline darker.
                Color c = shape.palPart[i] >= 0 ? shape.palColor[i] * itemTints[shape.palPart[i]] : shape.palColor[i];
                litPal[i] = R3DMaterials.Shaded(c, 1f);
                linePal[i] = R3DMaterials.Shaded(c, ModelBaker.OutlineShade);
            }

            float scale = shape.fit * (thing.MultipleItemsPerCellDrawn() ? 0.8f : 1f);
            Quaternion turn = Quaternion.AngleAxis(Turn(thing), Vector3.up);
            Vector3 center = thing.DrawPos;
            int mark = RoofClip.Mark(layer);
            bool any = false;
            try
            {
                Material litMat = R3DMaterials.Lit(0), solid = R3DMaterials.Solid;
                any |= Put(litMat != null ? MeshWriter.Sub(layer, litMat) : null, shape.lit, litPal, center, turn, scale, baseHeight);
                any |= Put(solid != null ? MeshWriter.Sub(layer, solid) : null, shape.glow, litPal, center, turn, scale, baseHeight);
                if (any && IsoRimMod.Settings.outlines && R3DMaterials.Outline != null)
                {
                    Put(MeshWriter.Sub(layer, R3DMaterials.Outline), shape.line, linePal, center, turn, scale, baseHeight);
                }
                for (int i = 0; i < shape.slowParts.Length; i++)
                {
                    any |= ModelBaker.WritePosed(layer, thing, shape.slowParts[i], center, turn, scale, baseHeight, itemTints[shape.slowPart[i]], 1f);
                }
            }
            finally
            {
                RoofClip.Tag(layer, mark, thing.Position);
            }
            return any;
        }

        // Appends a triangle list to a piece of the block's mesh: scaled, turned about up, placed at center, baseHeight up,
        // each corner the colour of its palette entry.
        private static bool Put(LayerSubMesh sm, Tris tris, Color32[] pal, Vector3 center, Quaternion turn, float scale, float baseHeight)
        {
            Vector3[] pos = tris.pos;
            int n = pos.Length;
            if (n == 0 || sm == null || sm.verts.Count + n > MeshWriter.MaxVerts)
            {
                return false;
            }
            float squash = View3D.Squash;
            float baseY = baseHeight / squash;
            float yScale = scale / squash;
            Vector3 right = turn * Vector3.right * scale, forward = turn * Vector3.forward * scale;
            int[] palOf = tris.pal;
            int first = sm.verts.Count;
            for (int k = 0; k < n; k++)
            {
                Vector3 p = pos[k];
                sm.verts.Add(new Vector3(center.x + right.x * p.x + forward.x * p.z, baseY + Mathf.Max(0f, p.y) * yScale, center.z + right.z * p.x + forward.z * p.z));
                sm.colors.Add(pal[palOf[k]]);
                sm.uvs.Add(default);
            }
            int[] index = tris.index;
            for (int k = 0; k < index.Length; k++)
            {
                sm.tris.Add(first + index[k]);
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
