using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Repeatable "random" numbers from map coordinates or a thing's id, so that a given rock
    // corner or tree looks the same every time the map is redrawn.
    public static class Hashing
    {
        // A value from 0 up to (not including) 1, so that (int)(Unit * n) is always below n.
        public static float Unit(int a, int b, int salt)
        {
            unchecked
            {
                int h = (a * 73856093) ^ (b * 19349663) ^ (salt * 83492791);
                h = (h ^ (int)((uint)h >> 13)) * 1274126177;
                h ^= (int)((uint)h >> 16);
                return (h & 0xFFFF) / 65536f;
            }
        }

        // A whole number from 0 to n - 1 (n > 0): for picking one of n things.
        public static int Index(int a, int b, int salt, int n)
        {
            return n <= 0 ? 0 : Math.Min(n - 1, (int)(Unit(a, b, salt) * n));
        }
    }

    // Draws a model every frame, for things that move: a weapon in a colonist's hands, the two
    // leaves of a door. (Everything that stays put is written into the map mesh by ModelBaker
    // instead, which costs nothing per frame.) The model is turned into a few ready-made meshes
    // the first time it is needed in a given colour and kept.
    public static class DynamicModels
    {
        // Set while doors are drawn: the meshes made then also go see-through near the pointer while building (BuildSee).
        public static bool SeeParts;

        // (Api 31) Set while a turret's gun with ModelDef.topThroughRoof is drawn (Patches.DrawTurret_Prefix): its meshes,
        // kept apart in the cache, are tagged to rise through roofs (RoofClip.ThroughRoof).
        public static bool ThroughRoof;

        public class Part
        {
            public Mesh mesh;
            public Material material;
            public bool outline;
            public bool beam;      // (Api 33) a model's beam part: drawn at once, as light (Beams)
        }

        private class Lists
        {
            public List<Vector3> verts = new List<Vector3>();
            public List<Color32> colors = new List<Color32>();
            public List<Vector2> uvs = new List<Vector2>();
            public List<int> tris = new List<int>();
            public Material material;

            public void Add(Vector3 v, Color32 c, Vector2 uv)
            {
                tris.Add(verts.Count);
                verts.Add(v);
                colors.Add(c);
                uvs.Add(uv);
            }

            public Mesh ToMesh()
            {
                Mesh mesh = new Mesh();
                mesh.SetVertices(verts);
                mesh.SetColors(colors);
                mesh.SetUVs(0, uvs);
                List<Vector3> tags = new List<Vector3>(verts.Count);
                for (int i = 0; i < verts.Count; i++)
                {
                    tags.Add(ThroughRoof ? (SeeParts ? RoofClip.MovingSeeThroughRoofTag : RoofClip.MovingThroughRoofTag)
                        : SeeParts ? RoofClip.MovingSeeTag : RoofClip.MovingTag);   // clipped under the roof over where it is drawn (RoofClip)
                }
                mesh.SetUVs(1, tags);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();
                MeshGuard.Check(mesh, "moving model");
                return mesh;
            }
        }

        private static readonly Dictionary<long, List<Part>> cache = new Dictionary<long, List<Part>>();
        private static readonly Color32 White = new Color32(255, 255, 255, 255);

        // Meshes no longer wanted are destroyed a couple of frames later rather than at once:
        // one may already be queued to draw this frame, and destroying it would make whatever
        // it shows blink out for that frame.
        private static readonly List<Mesh> retired = new List<Mesh>();
        private static readonly List<int> retiredAt = new List<int>();

        public static void Retire(Mesh mesh)
        {
            if (mesh == null)
            {
                return;
            }
            retired.Add(mesh);
            retiredAt.Add(Time.frameCount);
        }

        private static void Sweep()
        {
            int now = Time.frameCount;
            int done = 0;
            while (done < retired.Count && now - retiredAt[done] > 2)
            {
                if (retired[done] != null)
                {
                    UnityEngine.Object.Destroy(retired[done]);
                }
                done++;
            }
            if (done > 0)
            {
                retired.RemoveRange(0, done);
                retiredAt.RemoveRange(0, done);
            }
        }

        // Throws away every ready-made mesh; they are made again as they are needed.
        public static void Forget()
        {
            foreach (KeyValuePair<long, List<Part>> pair in cache)
            {
                List<Part> parts = pair.Value;
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i].mesh != null)
                    {
                        Retire(parts[i].mesh);
                    }
                }
            }
            cache.Clear();
        }

        // position: x and z in map cells, y the height above the ground in cells.
        // yaw: turn about the vertical axis in degrees. swatch: Wood, Stone, Metal or Fabric.
        public static void Draw(Model m, Vector3 position, float yaw, Vector3 scale, Color tint, string swatch)
        {
            if (m == null)
            {
                return;
            }
            List<Part> parts = PartsFor(m, tint, swatch);
            if (parts.Count == 0)
            {
                return;
            }

            // The whole map is drawn squashed flat (see View3D); dividing height by the same
            // factor here makes the model come out its true shape.
            float s = View3D.Squash;
            Matrix4x4 matrix = Matrix4x4.Translate(new Vector3(position.x, position.y / s, position.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.TRS(Vector3.zero, Quaternion.AngleAxis(yaw, Vector3.up), scale);
            Submit(parts, matrix);
        }

        // Draws ready-made parts (from BuildMerged) the same way DrawLocal places a model.
        public static void DrawParts(List<Part> parts, Vector3 position, float yaw, Matrix4x4 local)
        {
            if (parts == null || parts.Count == 0)
            {
                return;
            }
            float s = View3D.Squash;
            Matrix4x4 matrix = Matrix4x4.Translate(new Vector3(position.x, position.y / s, position.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up))
                * local;
            Submit(parts, matrix);
        }

        public static void Destroy(List<Part> parts)
        {
            if (parts == null)
            {
                return;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].mesh != null)
                {
                    Retire(parts[i].mesh);
                }
            }
            parts.Clear();
        }

        // Several plain-coloured models, each with its own placing and colour, put together into
        // one solid mesh and one outline mesh: one draw each instead of one per model. The solid
        // mesh is lit per pixel by the shader (glowing parts go into a separate unlit mesh), so it
        // does not depend on the light or on which way the whole is turned (turned is unused).
        // Textures are left off.
        // mono (a statue's subject): every colour carved in that one colour (Statues.Carved), nothing glowing.
        public static List<Part> BuildMerged(List<Model> models, List<Matrix4x4> locals, List<Color> tints, int turned, Color? mono = null)
        {
            return BuildMerged(models, locals, tints, turned, mono, float.NegativeInfinity);
        }

        // clipBelow: faces whose middle is lower than this (after their placing) are left out (a figure without legs).
        public static List<Part> BuildMerged(List<Model> models, List<Matrix4x4> locals, List<Color> tints, int turned, Color? mono, float clipBelow)
        {
            List<Part> parts = new List<Part>();
            Material solidMat = R3DMaterials.Solid;
            Material litMat = R3DMaterials.LitModel;
            if (solidMat == null || litMat == null)
            {
                return parts;
            }
            Lists solid = new Lists();
            Lists glowing = new Lists();
            Lists outline = new Lists();
            try
            {
                for (int k = 0; k < models.Count; k++)
                {
                    Model m = models[k];
                    Matrix4x4 local = locals[k];
                    Color tint = tints[k];
                    int n = m.positions.Length;
                    for (int i = 0; i + 2 < n; i += 3)
                    {
                        if (clipBelow > float.NegativeInfinity
                            && local.MultiplyPoint3x4(m.positions[i]).y + local.MultiplyPoint3x4(m.positions[i + 1]).y + local.MultiplyPoint3x4(m.positions[i + 2]).y < 3f * clipBelow)
                        {
                            continue;
                        }
                        int mi = m.material != null ? m.material[i] : 0;
                        ModelMaterial mm = m.materials != null ? m.materials[mi] : null;
                        Lists into = mm != null && mm.glow && !mono.HasValue ? glowing : solid;
                        for (int v = i; v < i + 3; v++)
                        {
                            Color c = m.colors[v];
                            if (m.tinted[v])
                            {
                                c *= tint;
                            }
                            if (mono.HasValue)
                            {
                                c = Statues.Carved(c, mono.Value);
                            }
                            into.Add(local.MultiplyPoint3x4(m.positions[v]), R3DMaterials.Shaded(c, 1f), Vector2.zero);
                            if (m.hull != null && (m.noOutline == null || !m.noOutline[v]))
                            {
                                outline.Add(local.MultiplyPoint3x4(m.positions[v] + m.hull[v]), R3DMaterials.Shaded(c, ModelBaker.OutlineShade), Vector2.zero);
                            }
                        }
                    }
                }
                AddPart(parts, solid, litMat, false);
                AddPart(parts, glowing, solidMat, false);
                AddPart(parts, outline, R3DMaterials.OutlineMoving, true);
                AddBeams(parts, models, locals, mono.HasValue);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Putting a figure together failed: " + e, 93611031);
            }
            return parts;
        }

        // (Api 33) The models' beam parts (placed by locals, or as they are) as one part drawn as light (Beams): their MTL colours,
        // no outline. None for a carved statue (mono).
        private static void AddBeams(List<Part> parts, List<Model> models, List<Matrix4x4> locals, bool mono)
        {
            if (mono)
            {
                return;
            }
            Lists beams = null;
            Model first = null;
            for (int k = 0; k < models.Count; k++)
            {
                Model b = models[k]?.beam;
                if (b == null)
                {
                    continue;
                }
                beams ??= new Lists();
                first ??= b;
                for (int i = 0; i < b.positions.Length; i++)
                {
                    Vector3 p = locals != null ? locals[k].MultiplyPoint3x4(b.positions[i]) : b.positions[i];
                    beams.Add(p, b.colors[i], Vector2.zero);
                }
            }
            if (beams != null)
            {
                AddPart(parts, beams, Beams.PartMaterial(first), false);
                if (parts.Count > 0 && parts[parts.Count - 1].material == Beams.PartMaterial(first))
                {
                    parts[parts.Count - 1].beam = true;
                }
            }
        }

        private static void AddPart(List<Part> parts, Lists lists, Material material, bool isOutline)
        {
            if (lists == null || lists.verts.Count == 0 || material == null)
            {
                return;
            }
            Part p = new Part();
            p.mesh = lists.ToMesh();
            p.material = material;
            p.outline = isOutline;
            parts.Add(p);
        }

        // While set, DrawLocal draws each model as a see-through placement ghost in GhostColour instead (Doors.Ghost).
        internal static bool Ghosting;
        internal static Color GhostColour;

        // The same for one part of something that moves as a whole, such as an animal's body
        // or one of its legs. local places the part within the whole, in cells, before the whole
        // is turned by yaw and put at position. The light stays where it is as the whole turns:
        // the shader lights every face per pixel for the direction it faces now.
        public static void DrawLocal(Model m, Vector3 position, float yaw, Matrix4x4 local, Color tint, string swatch)
        {
            if (m == null)
            {
                return;
            }
            if (Ghosting)
            {
                // a placement preview (Doors.Ghost): the part as a see-through ghost, as Ghosts draws models
                float gs = View3D.Squash;
                Ghosts.DrawModel(m, Matrix4x4.Translate(new Vector3(position.x, position.y / gs, position.z))
                    * Matrix4x4.Scale(new Vector3(1f, 1f / gs, 1f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up)) * local, GhostColour);
                return;
            }
            List<Part> parts = PartsFor(m, tint, swatch);
            if (parts.Count == 0)
            {
                return;
            }
            float s = View3D.Squash;
            Matrix4x4 matrix = Matrix4x4.Translate(new Vector3(position.x, position.y / s, position.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up))
                * local;
            Submit(parts, matrix);
        }

        // A model drawn with a whole matrix of its own (drawn space, before the floor shift): figure attachments (Api 8).
        internal static void DrawMatrix(Model m, Matrix4x4 matrix, Color tint, string swatch)
        {
            if (m == null)
            {
                return;
            }
            List<Part> parts = PartsFor(m, tint, swatch);
            if (parts.Count > 0)
            {
                Submit(parts, matrix);
            }
        }

        private static void Submit(List<Part> parts, Matrix4x4 matrix)
        {
            if (FigureScope.Active && FigureScope.Submit(parts, ref matrix))
            {
                return;   // a figure measured for another mod (Api.FigureBounds): nothing drawn
            }
            if (retired.Count > 0)
            {
                Sweep();
            }
            int floor;
            matrix = Floors.DrawnMatrix(matrix, out floor);   // a thing on an upper floor: before culling, tested where it shows
            // Api 10: a model drawn outside a thing's draw (Api.DrawModel from an addon's own update) on a floor faded while
            // peeking takes that floor's fade, as the floor's own things do (they set FadeSlot in Floors.ThingPhase_Prefix).
            // Its rows tell the floor; it is in drawn space by now, so FadeSlot carries the fade to Graphics
            // (Floors.Fade_Prefix), and PartBatches leaves the draw to be made at once (it only batches solid draws).
            int fadeWas = Floors.FadeSlot;
            if (fadeWas == 0 && floor > 0)
            {
                Floors.FadeSlot = Floors.FadeOfFloor(floor);
            }
            try
            {
                bool outlines = IsoRimMod.Settings.outlines;
                for (int i = 0; i < parts.Count; i++)
                {
                    Part p = parts[i];
                    if (p.outline && !outlines)
                    {
                        continue;
                    }
                    if (!Cull.MeshVisible(p.mesh, matrix))
                    {
                        continue;
                    }
                    if (p.beam)
                    {
                        IsoGlow.DrawLit(p.mesh, matrix, p.material, 0, RoofClip.FloorBlock(floor));   // (Api 33) light: not batched (it has no light pass)
                        Beams.PartDraws++;
                        continue;
                    }
                    if (!PartBatches.Add(p.mesh, matrix, p.material, floor))
                    {
                        IsoGlow.DrawLit(p.mesh, matrix, p.material, 0, RoofClip.FloorBlock(floor));   // clipped on its own floor (RoofClip)
                    }
                }
            }
            finally
            {
                Floors.FadeSlot = fadeWas;
            }
        }

        // The ready-made meshes of a model in one colour. They carry no shading (the shader lights
        // them), so they serve every light and every way the model is turned.
        private static List<Part> PartsFor(Model m, Color tint, string swatch)
        {
            bool textured = IsoRimMod.Settings.texturedModels && m.materials != null && m.uvs != null && m.material != null;

            // Colours are rounded a little, so that people's clothes and drifting smoke, which
            // come in endless shades, share ready-made meshes instead of each needing its own.
            Color32 t = tint;
            t.r = (byte)(t.r & 0xF8);
            t.g = (byte)(t.g & 0xF8);
            t.b = (byte)(t.b & 0xF8);
            t.a = 255;
            tint = t;
            if (cache.Count > 6000)
            {
                Forget();
            }
            long key = ((long)m.id << 34) | ((long)(ThroughRoof ? 1 : 0) << 28) | ((long)(textured ? 1 : 0) << 27) | ((long)R3DTextures.SwatchIndex(swatch) << 24)
                | (uint)((t.r << 16) | (t.g << 8) | t.b);

            List<Part> parts;
            if (cache.TryGetValue(key, out parts))
            {
                return parts;
            }
            parts = new List<Part>();
            cache[key] = parts;
            try
            {
                Build(m, tint, swatch, textured, parts);
            }
            catch (Exception e)
            {
                parts.Clear();
                Log.ErrorOnce("[IsoRim] Preparing a model for drawing failed: " + e, 93611030);
            }
            return parts;
        }

        // The same three layers ModelBaker writes: a plain solid copy (lit; glowing faces unlit),
        // an enlarged inside-out copy for the outline, and the textured faces (lit like ModelBaker's,
        // texture colour * TextureLift; glowing ones unlit).
        private static void Build(Model m, Color tint, string swatch, bool textured, List<Part> parts)
        {
            Material solidMat = R3DMaterials.Solid;
            Material litMat = R3DMaterials.LitModel;
            if (solidMat == null || litMat == null)
            {
                return;
            }
            Lists solid = new Lists();
            Lists glowing = new Lists();
            Lists outline = new Lists();
            Lists[] texturedLists = textured ? new Lists[m.materials.Length] : null;
            bool[] looked = textured ? new bool[m.materials.Length] : null;

            int n = m.positions.Length;
            for (int i = 0; i + 2 < n; i += 3)
            {
                int mi = m.material != null ? m.material[i] : 0;
                ModelMaterial mm = m.materials != null ? m.materials[mi] : null;
                bool glow = mm != null && mm.glow;   // a flame is as bright on every side

                Lists tl = null;
                if (textured && mm != null && mm.texture != null)
                {
                    if (!looked[mi])
                    {
                        looked[mi] = true;
                        Color c = mm.color * ModelBaker.TextureLift;
                        if (mm.tinted)
                        {
                            c *= tint;
                        }
                        string path = mm.texture.Replace("{stuff}", swatch);
                        Material tmat = glow ? R3DTextures.MaterialFor(path, c) : R3DMaterials.LitTexture(path, c);
                        if (tmat != null)
                        {
                            texturedLists[mi] = new Lists();
                            texturedLists[mi].material = tmat;
                        }
                    }
                    tl = texturedLists[mi];
                }

                Lists into = glow ? glowing : solid;
                for (int k = i; k < i + 3; k++)
                {
                    Vector3 hull = m.hull != null ? m.hull[k] : Vector3.zero;
                    Color c = m.colors[k];
                    if (m.tinted[k])
                    {
                        c *= tint;
                    }
                    into.Add(tl != null ? m.positions[k] - hull * ModelBaker.Inset : m.positions[k], R3DMaterials.Shaded(c, 1f), Vector2.zero);
                    if (m.hull != null && (m.noOutline == null || !m.noOutline[k]))
                    {
                        outline.Add(m.positions[k] + hull, R3DMaterials.Shaded(c, ModelBaker.OutlineShade), Vector2.zero);
                    }
                    if (tl != null)
                    {
                        tl.Add(m.positions[k] + m.normals[i] * ModelBaker.FaceLift, White, m.uvs[k]);
                    }
                }
            }

            AddPart(parts, solid, litMat, false);
            AddPart(parts, glowing, solidMat, false);
            AddPart(parts, outline, R3DMaterials.OutlineMoving, true);
            AddBeams(parts, new List<Model> { m }, null, false);
            if (texturedLists != null)
            {
                for (int i = 0; i < texturedLists.Length; i++)
                {
                    Lists l = texturedLists[i];
                    if (l != null)
                    {
                        AddPart(parts, l, l.material, false);
                    }
                }
            }
        }
    }

    // Swaps the game's ground textures for the mod's own while the 3D view is on, and puts the
    // originals back when it is off. Only the picture changes: the game goes on drawing the
    // ground, blending edges and tinting stone as before.
    //   - natural ground (soil, sand, gravel, stone ...), with soil optionally drawn as grass;
    //   - built floors (wood, stone tile, flagstone, carpet, concrete, metal ...);
    // (Water is drawn by the mod's own layer; see SectionLayer_IsoRimWater.)
    public static class Ground
    {
        private struct Saved
        {
            public Texture texture;
            public Color color;
        }

        private static int applied;   // which of the options below are in force; 0 = the game's own textures
        private static readonly Dictionary<Material, Saved> originals = new Dictionary<Material, Saved>();

        // Called every frame; does nothing unless the wanted state has changed.
        public static void Sync()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            int want = 0;
            if (View3D.Active)
            {
                want = (st.groundStyle ? 1 : 0) | (st.groundStyle && st.grassSoil ? 2 : 0) | (st.floorStyle ? 4 : 0);
            }
            if (want == applied)
            {
                return;
            }
            applied = want;
            try
            {
                Restore();
                if (want != 0)
                {
                    Apply(st);
                }
                Bridges.Rebuilt();   // the deck sides take the deck's tint
                if (Find.CurrentMap != null)
                {
                    MapSections.QueueAll(Find.CurrentMap, typeof(SectionLayer_IsoRimBridges), true);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Changing the ground textures failed: " + e, 93611050);
            }
        }

        private static void Apply(IsoRimSettings st)
        {
            int changed = 0;
            foreach (TerrainDef def in DefDatabase<TerrainDef>.AllDefsListForReading)
            {
                Material mat = def.DrawMatSingle;
                if (mat == null || !mat.HasProperty("_MainTex"))
                {
                    continue;
                }

                if (IsWater(def))
                {
                    continue;
                }

                Color tint = Color.white;
                bool ownTint = false;
                string kind = st.floorStyle ? FloorKind(def, ref tint, ref ownTint) : null;
                if (kind == null && st.groundStyle)
                {
                    kind = KindOf(def);
                    if (st.grassSoil)
                    {
                        // Bare earth is drawn as turf. Rich soil gets a darker, lusher turf so that
                        // the good farmland can still be told apart at a glance.
                        if (kind == "Soil")
                        {
                            kind = "Grass";
                        }
                        else if (kind == "SoilRich")
                        {
                            kind = "GrassRich";
                        }
                    }
                }
                if (kind == null)
                {
                    continue;
                }
                Texture2D tex = R3DTextures.Get("IsoRim/Ground/" + kind);
                if (tex == null)
                {
                    continue;
                }
                Remember(mat);
                mat.mainTexture = tex;

                // The floor pictures are plain grey. Where the game tints the floor itself (stone,
                // carpet) that is all that is needed; where its own picture carried the colour,
                // the colour is supplied here.
                if (ownTint && mat.HasProperty("_Color"))
                {
                    Color c = mat.color;
                    if (c.r > 0.93f && c.g > 0.93f && c.b > 0.93f)
                    {
                        mat.color = new Color(tint.r, tint.g, tint.b, c.a);
                    }
                }
                changed++;
            }
            if (changed == 0)
            {
                ModLog.Warn("[IsoRim] No ground textures could be replaced.");
            }
        }

        private static void Remember(Material mat)
        {
            if (originals.ContainsKey(mat))
            {
                return;
            }
            Saved saved = new Saved();
            saved.texture = Aniso.RealTexture(mat);   // not the filtering option's one-frame stand-in
            saved.color = mat.HasProperty("_Color") ? mat.color : Color.white;
            originals[mat] = saved;
        }

        private static void Restore()
        {
            foreach (KeyValuePair<Material, Saved> pair in originals)
            {
                Material mat = pair.Key;
                if (mat == null)
                {
                    continue;
                }
                mat.mainTexture = pair.Value.texture;
                if (mat.HasProperty("_Color"))
                {
                    mat.color = pair.Value.color;
                }
            }
        }

        // Open water. Marshy ground is not: it keeps the mod's own marsh picture.
        public static bool IsWater(TerrainDef def)
        {
            if (def == null)
            {
                return false;
            }
            string name = def.defName ?? "";
            if (Has(name, "Marsh"))
            {
                return false;
            }
            return (def.tags != null && def.tags.Contains("Water")) || Has(name, "Water");
        }

        private static readonly Color WoodTint = new Color(0.60f, 0.44f, 0.30f);
        private static readonly Color ConcreteTint = new Color(0.62f, 0.62f, 0.60f);
        private static readonly Color PavedTint = new Color(0.56f, 0.53f, 0.50f);
        private static readonly Color SteelTint = new Color(0.47f, 0.49f, 0.53f);
        private static readonly Color SilverTint = new Color(0.80f, 0.82f, 0.84f);
        private static readonly Color GoldTint = new Color(0.88f, 0.72f, 0.30f);
        private static readonly Color SterileTint = new Color(0.90f, 0.92f, 0.92f);

        // Which of the mod's floor textures suits a built floor, or null to leave it alone.
        // tint is the colour to give it if the game does not tint that floor itself.
        private static string FloorKind(TerrainDef def, ref Color tint, ref bool ownTint)
        {
            string name = def.defName ?? "";
            string both = name + " " + (def.texturePath ?? "");
            if (Has(both, "Burned") || Has(both, "Water"))
            {
                return null;
            }
            if (Has(both, "WoodPlank") || Has(both, "WoodFloor") || Has(name, "Bridge"))
            {
                tint = WoodTint;
                ownTint = true;
                return "FloorWood";
            }
            if (Has(both, "Flagstone"))
            {
                return "FloorFlagstone";
            }
            if (Has(both, "Carpet"))
            {
                return "FloorCarpet";
            }
            if (Has(both, "Sterile"))
            {
                tint = SterileTint;
                ownTint = true;
                return "FloorSterile";
            }
            if (Has(name, "SilverTile") || Has(name, "GoldTile") || Has(name, "MetalTile"))
            {
                tint = Has(name, "Silver") ? SilverTint : (Has(name, "Gold") ? GoldTint : SteelTint);
                ownTint = true;
                return "FloorMetal";
            }
            if (Has(both, "PavedTile"))
            {
                tint = PavedTint;
                ownTint = true;
                return "FloorPaved";
            }
            if (Has(both, "Concrete"))
            {
                tint = ConcreteTint;
                ownTint = true;
                return "FloorConcrete";
            }
            if (Has(both, "TilesStone") || Has(both, "StoneTile") || (name.StartsWith("Tile") && def.layerable))
            {
                return "FloorStoneTile";
            }
            return null;
        }

        // Which of the mod's ground textures suits a natural terrain, or null to leave it alone.
        private static string KindOf(TerrainDef def)
        {
            string name = def.defName ?? "";
            string both = name + " " + (def.texturePath ?? "");
            if (def.layerable)
            {
                return null;   // a built floor the mod has no texture for
            }
            if (Has(both, "Water") || Has(both, "Bridge") || Has(both, "Floor") || Has(both, "Tile") || Has(both, "Flagstone")
                || Has(both, "Carpet") || Has(both, "Concrete") || Has(both, "Paved") || Has(both, "Burned") || Has(both, "Asphalt"))
            {
                return null;
            }

            // Stone ground is generated once per kind of rock, named like Granite_Rough.
            if (name.EndsWith("_RoughHewn"))
            {
                return "RoughHewn";
            }
            if (name.EndsWith("_Rough"))
            {
                return "RoughStone";
            }
            if (name.EndsWith("_Smooth"))
            {
                return "SmoothStone";
            }

            if (Has(both, "SoilRich") || Has(both, "RichSoil"))
            {
                return "SoilRich";
            }
            if (Has(both, "Marsh"))
            {
                return "Marsh";
            }
            if (Has(both, "Moss"))
            {
                return "Moss";
            }
            if (Has(both, "Mud"))
            {
                return "Mud";
            }
            if (Has(both, "PackedDirt"))
            {
                return "PackedDirt";
            }
            if (Has(both, "Gravel"))
            {
                return "Gravel";
            }
            if (Has(both, "Sand"))
            {
                return "Sand";
            }
            if (name == "Ice")
            {
                return "Ice";
            }
            if (Has(both, "Soil"))
            {
                return "Soil";
            }
            return null;
        }

        private static bool Has(string text, string word)
        {
            return text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // Decides which walls are cut down to show what is behind them (the stretch under the
    // pointer, and optionally the walls in front of roofed rooms) and redraws the parts of the
    // map they are in when that changes, because the game only redraws walls when the walls
    // themselves change.
    public static class Cutaway
    {
        private struct Pending
        {
            public Map map;
            public int sx;
            public int sz;
            public float due;
        }

        private const int SectionSize = 17;   // the game's map mesh is built in 17 x 17 blocks

        private static readonly List<Pending> queue = new List<Pending>();
        private static bool looked;
        private static MethodInfo sectionAt;
        private static MethodInfo regenerate;
        private static MethodInfo getLayer;
        private static MethodInfo regenerateLayer;
        private static Type thingsLayer;

        // ---- what is dissolved round the pointer and the colonists ------------------------------
        // Walls within reach of the pointer (and, as an option, of each colonist) are lowered,
        // trees there lose their leaves so what is under them shows, and the roof of the room
        // under the pointer (or of rooms with colonists in them) is lifted off. All of these are
        // part of the map's ready-made mesh, so the blocks they are in are redrawn when the sets
        // change, after the pointer has settled for a moment.

        private static HashSet<IntVec3> cutWalls = new HashSet<IntVec3>();
        private static HashSet<IntVec3> thinTrees = new HashSet<IntVec3>();
        private static HashSet<IntVec3> openRoofs = new HashSet<IntVec3>();
        private static HashSet<IntVec3> wantWalls = new HashSet<IntVec3>();
        private static HashSet<IntVec3> wantTrees = new HashSet<IntVec3>();
        private static HashSet<IntVec3> wantRoofs = new HashSet<IntVec3>();
        private static readonly List<IntVec3> redraw = new List<IntVec3>();
        private static readonly List<IntVec3> redrawRoofs = new List<IntVec3>();
        private static readonly List<IntVec3> redrawTrees = new List<IntVec3>();
        private static readonly List<Vector3> pointerFoci = new List<Vector3>();
        private static readonly List<Vector3> colonistFoci = new List<Vector3>();
        private static readonly HashSet<Room> rooms = new HashSet<Room>();
        private static readonly HashSet<Room> lastRooms = new HashSet<Room>();
        private static int lastRoomCells = -1;
        private static bool roomsSame;
        private static Map hoverMap;
        private static int appliedSig;
        private static int wantedSig;
        private static float wantedSince;
        private static int lastScan = -1000;

        public static bool Hovered(Map map, IntVec3 c)
        {
            return !FullWalls.Required(map) && map == hoverMap && cutWalls.Count > 0 && cutWalls.Contains(c);
        }

        // A camera policy changed: discard cached pointer cuts, and refresh room cuts as well. Shader walls keep their
        // geometry; the older untextured path must rebuild it. Trees and roofs keep their own visibility rules.
        internal static void WallModeChanged(Map map)
        {
            if (map == null)
            {
                return;
            }
            if (map == hoverMap)
            {
                cutWalls.Clear();
                wantWalls.Clear();
                lastScan = -1000;
                appliedSig = 0;
                wantedSig = 0;
            }
            IsoCellMap.Rebuilt(map);
            if (!ShaderCut.On)
            {
                MapSections.QueueWallCutaway(map);
            }
        }

        public static bool TreeThinned(Map map, IntVec3 c)
        {
            return map == hoverMap && thinTrees.Count > 0 && thinTrees.Contains(c);
        }

        public static bool RoofOpen(Map map, IntVec3 c)
        {
            return map == hoverMap && openRoofs.Count > 0 && openRoofs.Contains(c);
        }

        // Called every frame while the 3D view is on.
        public static void TrackPointer()
        {
            Map map = Find.CurrentMap;
            if (map != hoverMap)
            {
                cutWalls.Clear();
                thinTrees.Clear();
                openRoofs.Clear();
                lastRooms.Clear();
                lastRoomCells = -1;
                hoverMap = map;
                appliedSig = 0;
                wantedSig = 0;
            }
            BuildSee.Update(map);
            if (map == null)
            {
                return;
            }
            if (ShaderCut.On)
            {
                TrackShader(map);
                return;
            }
            int frame = Time.frameCount;
            if (frame - lastScan >= 4)
            {
                lastScan = frame;
                try
                {
                    Gather(map);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Working out what to see through failed: " + e, 93611072);
                    return;
                }
            }
            int sig = Signature(wantWalls, 1) ^ Signature(wantTrees, 2) ^ Signature(wantRoofs, 3);
            if (sig == appliedSig)
            {
                wantedSig = sig;
                return;
            }
            if (sig != wantedSig)
            {
                wantedSig = sig;
                wantedSince = Time.realtimeSinceStartup;
                return;
            }
            // Wait for the pointer to settle, a little longer before putting things back.
            bool growing = wantWalls.Count + wantTrees.Count + wantRoofs.Count >= cutWalls.Count + thinTrees.Count + openRoofs.Count;
            if (Time.realtimeSinceStartup - wantedSince < (growing ? 0.08f : 0.25f))
            {
                return;
            }

            redraw.Clear();
            redrawTrees.Clear();
            redrawRoofs.Clear();
            NoteChanges(cutWalls, wantWalls, redraw, 1);
            NoteChanges(thinTrees, wantTrees, redrawTrees, 0);
            NoteChanges(openRoofs, wantRoofs, redrawRoofs, SectionLayer_IsoRimRoofs.Reach);
            Swap(ref cutWalls, ref wantWalls);
            Swap(ref thinTrees, ref wantTrees);
            Swap(ref openRoofs, ref wantRoofs);
            appliedSig = sig;
            for (int i = 0; i < redraw.Count; i++)
            {
                RedrawWalls(map, redraw[i]);
            }
            for (int i = 0; i < redrawTrees.Count; i++)
            {
                RedrawLayer(map, redrawTrees[i], typeof(SectionLayer_IsoRimTrees));
            }
            for (int i = 0; i < redrawRoofs.Count; i++)
            {
                RedrawLayer(map, redrawRoofs[i], typeof(SectionLayer_IsoRimRoofs));
            }
            // The swapped-out sets now hold the old state; they are refilled on the next look.
        }

        // (shader cut-away, ShaderCut) Every frame: works out what is cut from where the pointer and the colonists are
        // now, and hands every cell that changed to the cell texture (IsoCellMap), which the vertex stage reads. No
        // waiting for the pointer to settle and nothing rebuilt.
        private static void TrackShader(Map map)
        {
            try
            {
                Gather(map);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Working out what to see through failed: " + e, 93611072);
                return;
            }
            Changed(map, cutWalls, wantWalls);
            Changed(map, thinTrees, wantTrees);
            Swap(ref cutWalls, ref wantWalls);
            Swap(ref thinTrees, ref wantTrees);
            if (!roomsSame)
            {
                Changed(map, openRoofs, wantRoofs);
                Swap(ref openRoofs, ref wantRoofs);
            }
        }

        private static void Changed(Map map, HashSet<IntVec3> now, HashSet<IntVec3> next)
        {
            foreach (IntVec3 c in now)
            {
                if (!next.Contains(c))
                {
                    IsoCellMap.Dirty(map, CellRect.SingleCell(c));
                }
            }
            foreach (IntVec3 c in next)
            {
                if (!now.Contains(c))
                {
                    IsoCellMap.Dirty(map, CellRect.SingleCell(c));
                }
            }
        }

        private static void Swap(ref HashSet<IntVec3> a, ref HashSet<IntVec3> b)
        {
            HashSet<IntVec3> t = a;
            a = b;
            b = t;
        }

        private static int Signature(HashSet<IntVec3> cells, int salt)
        {
            unchecked
            {
                int h = cells.Count * 7919 * salt;
                foreach (IntVec3 c in cells)
                {
                    h += (c.x * 73856093) ^ (c.z * 19349663) ^ (salt * 83492791);
                }
                return h;
            }
        }

        // Notes the blocks holding every cell that changes; with neighbours, the blocks of the
        // cells beside it too (a wall next to a lowered one shows a side it did not show before).
        private static void NoteChanges(HashSet<IntVec3> now, HashSet<IntVec3> next, List<IntVec3> into, int neighbours)
        {
            foreach (IntVec3 c in now)
            {
                if (!next.Contains(c))
                {
                    NoteBlock(c, into, neighbours);
                }
            }
            foreach (IntVec3 c in next)
            {
                if (!now.Contains(c))
                {
                    NoteBlock(c, into, neighbours);
                }
            }
        }

        // reach: how many cells round the changed one can look different because of it. A block
        // is 17 cells across, so checking the corners and edge middles of that square finds them all.
        private static void NoteBlock(IntVec3 c, List<IntVec3> into, int reach)
        {
            NoteOne(c.x, c.z, into);
            if (reach <= 0)
            {
                return;
            }
            for (int dx = -reach; dx <= reach; dx += reach)
            {
                for (int dz = -reach; dz <= reach; dz += reach)
                {
                    NoteOne(c.x + dx, c.z + dz, into);
                }
            }
        }

        private static void NoteOne(int x, int z, List<IntVec3> into)
        {
            if (x < 0 || z < 0)
            {
                return;
            }
            IntVec3 corner = new IntVec3(x / SectionSize * SectionSize + 1, 0, z / SectionSize * SectionSize + 1);
            if (!into.Contains(corner))
            {
                into.Add(corner);
            }
        }

        private static bool PointerOnInterface()
        {
            return Find.WindowStack != null && Find.WindowStack.GetWindowAt(Verse.UI.MousePositionOnUIInverted) != null;
        }

        // Where the pointer is on the map (stacked floors: on the floor it hits, in that floor's rows); false while it is
        // over the interface. Also read by the see-through while building (BuildSee).
        public static bool Pointer(out Vector3 ground)
        {
            if (Probe.On && Probe.HasCutPointer)
            {
                ground = Probe.CutPointer;   // harness: a virtual pointer (ProbeCut.cs)
                return true;
            }
            Vector3 mouse = Input.mousePosition;
            ground = Floors.ScreenToMap(new Vector2(mouse.x, mouse.y));
            return !PointerOnInterface();
        }

        // Fills the wanted sets from where the pointer and the colonists are now.
        private static void Gather(Map map)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            wantWalls.Clear();
            wantTrees.Clear();
            wantRoofs.Clear();
            pointerFoci.Clear();
            colonistFoci.Clear();

            Vector3 ground;
            bool pointer = Pointer(out ground);
            if (pointer)
            {
                pointerFoci.Add(ground);
            }
            if (st.cutNearColonists || st.treesNearColonists || st.roofOverColonists)
            {
                var pawns = map.mapPawns.FreeColonistsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    colonistFoci.Add(pawns[i].DrawPos);
                }
            }

            if (st.walls3D && !FullWalls.Required(map))
            {
                if (st.cutHover)
                {
                    AddWalls(map, pointerFoci, st.dissolveRadius, st.cutFrontOnly);
                }
                if (st.cutNearColonists)
                {
                    AddWalls(map, colonistFoci, st.colonistRadius, true);
                }
                if (st.cutWholeWall)
                {
                    AddColumns(map);
                }
            }
            if (st.trees3D)
            {
                if (st.treesNearPointer)
                {
                    AddTrees(map, pointerFoci, st.treeRadius);
                }
                if (st.treesNearColonists)
                {
                    AddTrees(map, colonistFoci, st.treeRadius);
                }
            }
            roomsSame = false;
            if (!st.showRoofs)
            {
                lastRooms.Clear();
                lastRoomCells = -1;
            }
            if (st.showRoofs)
            {
                rooms.Clear();
                if (st.roofHover && pointer)
                {
                    AddRoom(map, ground.ToIntVec3());
                }
                if (st.roofOverColonists)
                {
                    for (int i = 0; i < colonistFoci.Count; i++)
                    {
                        AddRoom(map, colonistFoci[i].ToIntVec3());
                    }
                }
                // (shader cut-away, run every frame) the same rooms as last time, each as big: nothing to list again.
                int cells = 0;
                foreach (Room room in rooms)
                {
                    cells += room.CellCount;
                }
                if (ShaderCut.On && rooms.SetEquals(lastRooms) && cells == lastRoomCells)
                {
                    roomsSame = true;
                    return;
                }
                lastRooms.Clear();
                lastRooms.UnionWith(rooms);
                lastRoomCells = cells;
                foreach (Room room in rooms)
                {
                    foreach (IntVec3 c in room.Cells)
                    {
                        wantRoofs.Add(c);
                    }
                    foreach (IntVec3 c in room.BorderCells)
                    {
                        wantRoofs.Add(c);
                    }
                }
            }
        }

        private static void AddRoom(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return;
            }
            Room room = c.GetRoom(map);
            if (room == null || room.PsychologicallyOutdoors || room.CellCount > 2500)
            {
                return;
            }
            rooms.Add(room);
        }

        // Cuttable walls within reach of each point; with frontOnly, only those between the point and the camera.
        private static void AddWalls(Map map, List<Vector3> foci, float radius, bool frontOnly)
        {
            Vector3 forward = View3D.GroundForward;
            for (int k = 0; k < foci.Count; k++)
            {
                Vector3 f = foci[k];
                for (int x = Mathf.FloorToInt(f.x - radius); x <= Mathf.CeilToInt(f.x + radius); x++)
                {
                    for (int z = Mathf.FloorToInt(f.z - radius); z <= Mathf.CeilToInt(f.z + radius); z++)
                    {
                        float dx = x + 0.5f - f.x;
                        float dz = z + 0.5f - f.z;
                        if (dx * dx + dz * dz > radius * radius)
                        {
                            continue;
                        }
                        if (frontOnly && dx * forward.x + dz * forward.z > 0.6f)
                        {
                            continue;   // behind the point, as the camera sees it
                        }
                        IntVec3 c = new IntVec3(x, 0, z);
                        if (Cuttable(map, c) > 0f)
                        {
                            wantWalls.Add(c);
                        }
                    }
                }
            }
        }

        // (option cutWholeWall) Stacked floors: every lowered wall takes the walls of its ground column on all shown floors
        // with it (the ground and every floor drawn now), not just its own floor's. Without floors nothing changes.
        private static readonly List<IntVec3> columnCells = new List<IntVec3>();

        private static void AddColumns(Map map)
        {
            if (!Floors.On || wantWalls.Count == 0)
            {
                return;
            }
            columnCells.Clear();
            columnCells.AddRange(wantWalls);
            for (int i = 0; i < columnCells.Count; i++)
            {
                IntVec3 c = columnCells[i];
                float h;
                int z;
                int k = Floors.ColumnFloor(map, c, out h, out z);
                if (k < 0)
                {
                    continue;
                }
                for (int j = 0; j <= Floors.MaxFloors; j++)
                {
                    if (j == k || !Floors.Shown(j))
                    {
                        continue;
                    }
                    IntVec3 s = Floors.Stacked(map, c, j - k);
                    if (s.IsValid && Cuttable(map, s) > 0f)
                    {
                        wantWalls.Add(s);
                    }
                }
            }
        }

        // Trees within reach of each point, a little further toward the camera, where they hide it.
        private static void AddTrees(Map map, List<Vector3> foci, float radius)
        {
            Vector3 forward = View3D.GroundForward;
            for (int k = 0; k < foci.Count; k++)
            {
                Vector3 f = foci[k] - forward * (radius * 0.4f);
                float r = radius * 1.2f;
                for (int x = Mathf.FloorToInt(f.x - r); x <= Mathf.CeilToInt(f.x + r); x++)
                {
                    for (int z = Mathf.FloorToInt(f.z - r); z <= Mathf.CeilToInt(f.z + r); z++)
                    {
                        float dx = x + 0.5f - f.x;
                        float dz = z + 0.5f - f.z;
                        if (dx * dx + dz * dz > r * r)
                        {
                            continue;
                        }
                        IntVec3 c = new IntVec3(x, 0, z);
                        if (!c.InBounds(map))
                        {
                            continue;
                        }
                        Plant plant = c.GetPlant(map);
                        if (plant != null && plant.def.plant != null && plant.def.plant.IsTree)
                        {
                            wantTrees.Add(c);
                        }
                    }
                }
            }
        }

        // The full height of a wall or door in this cell that can be cut down, or 0.
        private static float Cuttable(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return 0f;
            }
            Building b = c.GetEdifice(map);
            if (b == null)
            {
                return 0f;
            }
            if (b is Building_Door)
            {
                string name = b.def.defName ?? "";
                return name.Contains("Gate") || name.Contains("Fence") ? 0f : View3D.WallHeight;
            }
            ThingForm form = Forms.For(b.def);
            return WallBoxes.IsWallForm(form) && WallBoxes.CanBeCut(b.def) ? WallBoxes.WallHeightOf(form) : 0f;
        }

        // The walls of the block holding the cell: just the wall layer, or (if walls are drawn
        // from the game's own sprites) everything in the block.
        private static void RedrawWalls(Map map, IntVec3 cell)
        {
            if (IsoRimMod.Settings.texturedWalls)
            {
                RedrawLayer(map, cell, typeof(SectionLayer_IsoRimWalls));
            }
            else
            {
                Redraw(map, cell, true);
            }
            if (IsoRimMod.Settings.damage3D)
            {
                RedrawLayer(map, cell, typeof(SectionLayer_IsoRimDamage));   // marks move with a cut wall's top
            }
        }

        // Redraws one layer of the block that holds the given cell.
        public static void RedrawLayer(Map map, IntVec3 cell, Type layerType)
        {
            Look();
            if (sectionAt == null || getLayer == null || map == null || map.mapDrawer == null || !cell.InBounds(map))
            {
                return;
            }
            try
            {
                object section = sectionAt.Invoke(map.mapDrawer, new object[] { cell });
                SectionLayer layer = section != null ? getLayer.Invoke(section, new object[] { layerType }) as SectionLayer : null;
                if (layer != null)
                {
                    layer.Regenerate();
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Redrawing part of the map failed: " + e, 93611073);
            }
        }

        public static void RoofChanged(Map map, IntVec3 c)
        {
            if (map == null)
            {
                return;
            }
            // The walls that depend on this cell are the ones just in front of it.
            Vector3 f = View3D.GroundForward;
            int dx = f.x > 0.3f ? 1 : (f.x < -0.3f ? -1 : 0);
            int dz = f.z > 0.3f ? 1 : (f.z < -0.3f ? -1 : 0);
            Note(map, c.x - dx, c.z);
            Note(map, c.x, c.z - dz);
            Note(map, c.x - dx, c.z - dz);
        }

        private static void Note(Map map, int x, int z)
        {
            if (x < 0 || z < 0 || x >= map.Size.x || z >= map.Size.z)
            {
                return;
            }
            int sx = x / SectionSize;
            int sz = z / SectionSize;
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i].map == map && queue[i].sx == sx && queue[i].sz == sz)
                {
                    return;
                }
            }
            Pending p = new Pending();
            p.map = map;
            p.sx = sx;
            p.sz = sz;
            p.due = Time.realtimeSinceStartup + 0.6f;   // roofs go up a cell at a time; wait for the rest
            queue.Add(p);
        }

        // Called every frame: redraws at most two waiting blocks.
        public static void Process()
        {
            if (queue.Count == 0)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            int done = 0;
            for (int i = queue.Count - 1; i >= 0 && done < 2; i--)
            {
                Pending p = queue[i];
                if (p.due > now)
                {
                    continue;
                }
                queue.RemoveAt(i);
                done++;
                RedrawWalls(p.map, new IntVec3(p.sx * SectionSize + 1, 0, p.sz * SectionSize + 1));
            }
        }

        // Redraws the block of the map that holds the given cell: just its things (walls,
        // furniture, plants) if thingsOnly and the game allows that, otherwise all of it.
        private static void Look()
        {
            if (looked)
            {
                return;
            }
            looked = true;
            sectionAt = AccessTools.Method(typeof(MapDrawer), "SectionAt", new[] { typeof(IntVec3) });
            Type section = AccessTools.TypeByName("Verse.Section");
            regenerate = section != null ? AccessTools.Method(section, "RegenerateAllLayers") : null;
            getLayer = section != null ? AccessTools.Method(section, "GetLayer", new[] { typeof(Type) }) : null;
            thingsLayer = AccessTools.TypeByName("Verse.SectionLayer_ThingsGeneral");
            regenerateLayer = AccessTools.Method(typeof(SectionLayer), "Regenerate", new Type[0]);
            if (sectionAt == null || regenerate == null)
            {
                ModLog.Warn("[IsoRim] The game's way of redrawing one block of the map was not found. Lowered walls will only catch up when something else nearby changes.");
            }
        }

        private static void Redraw(Map map, IntVec3 cell, bool thingsOnly)
        {
            Look();
            if (sectionAt == null || regenerate == null)
            {
                return;
            }
            try
            {
                if (map == null || map.mapDrawer == null || !cell.InBounds(map))
                {
                    return;
                }
                object section = sectionAt.Invoke(map.mapDrawer, new object[] { new IntVec3(cell.x, 0, cell.z) });
                if (section == null)
                {
                    return;
                }
                if (thingsOnly && getLayer != null && thingsLayer != null && regenerateLayer != null)
                {
                    object layer = getLayer.Invoke(section, new object[] { thingsLayer });
                    if (layer != null)
                    {
                        regenerateLayer.Invoke(layer, null);
                        return;
                    }
                }
                regenerate.Invoke(section, null);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Redrawing part of the map failed: " + e, 93611071);
            }
        }
    }

    // The blades of a wind turbine: a separate model spun round its hub at a speed that follows
    // the power the turbine is making, in place of the flat blades the game draws.
    public static class Turbines
    {
        private const float HubHeight = 2.75f;    // where the hub is on the turbine model
        private const float HubForward = -0.62f;

        private static bool tried;
        private static Model rotor;
        private static readonly Dictionary<int, float> spin = new Dictionary<int, float>();

        // comp is the turbine's power component. Returns false if nothing was drawn.
        public static bool Draw(ThingComp comp)
        {
            Thing parent = comp != null ? comp.parent : null;
            if (parent == null || parent.def == null || !IsoRimMod.Settings.models3D)
            {
                return false;
            }
            ModelEntry entry;
            if (!ModelLibrary.TryGet(parent.def, out entry) || !entry.HasModel)
            {
                return false;
            }
            if (!tried)
            {
                tried = true;
                if (IsoRimMod.RootDir != null)
                {
                    rotor = ModelLibrary.LoadFile(Path.Combine(IsoRimMod.RootDir, "Models", "wind_rotor.obj"));
                }
            }
            if (rotor == null)
            {
                return false;
            }

            float angle;
            spin.TryGetValue(parent.thingIDNumber, out angle);
            CompPowerTrader power = comp as CompPowerTrader;
            float making = power != null ? Mathf.Clamp(Mathf.Abs(power.PowerOutput) / 2300f, 0f, 1.6f) : 0.5f;
            float gameSpeed = Find.TickManager != null ? Find.TickManager.TickRateMultiplier : 1f;
            angle = Mathf.Repeat(angle + Time.deltaTime * gameSpeed * making * 220f, 360f);
            spin[parent.thingIDNumber] = angle;

            float size = entry.def.scale;
            Vector3 at = parent.DrawPos;
            Matrix4x4 local = Matrix4x4.Translate(new Vector3(0f, 0f, HubForward * size))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(angle, Vector3.forward))
                * Matrix4x4.Scale(new Vector3(size, size, size));
            DynamicModels.DrawLocal(rotor, new Vector3(at.x, HubHeight * size, at.z), parent.Rotation.AsAngle + entry.def.rotation, local, Color.white, null);
            return true;
        }
    }

    // A door drawn as two 3D leaves that slide apart into the walls either side, with a beam
    // across the top that carries the wall line over the doorway. It is as tall as the walls
    // and takes the texture and colour of what the door is built from.
    // A door frame's crown under a floor (ModelDef.crownTop): the frame and its leaves cut off at the wall top, kept
    // once made. The heights are in model space, where the wall top of a crowned door is the same at any wall height.
    public static class Crowns
    {
        private const float Below = 0.005f;   // the cut sits this far under the wall top, so the walls' own caps win
        private const float Step = 0.01f;     // cut heights are kept at steps this far apart (a rising portcullis)

        private static readonly Dictionary<long, Model> cut = new Dictionary<long, Model>();

        // The part of m under height top (model space), with a flat lintel over its ground box if lid. Null if nothing
        // of it is left (a portcullis risen into the floor above).
        public static Model Under(Model m, float top, bool lid)
        {
            if (m == null)
            {
                return null;
            }
            int step = Mathf.RoundToInt(top / Step);
            if (step <= 0)
            {
                return null;
            }
            if (step * Step >= m.maxY + Below)
            {
                return m;
            }
            long key = ((long)m.id << 20) | ((long)step << 1) | (lid ? 1L : 0L);
            Model c;
            if (!cut.TryGetValue(key, out c))
            {
                c = ObjLoader.ClipBelow(m, step * Step - Below, lid);
                cut[key] = c;
            }
            return c;
        }
    }

    public static class Doors
    {
        private const float BuiltForWallHeight = 1.2f;

        private static bool tried;
        private static Model leaf;
        private static Model lintel;
        private static Model gateLeaf;
        private static Model gatePosts;

        // Where and how a door is drawn: a built door's, or (Ghost) one still being placed.
        private struct Site
        {
            public ThingDef def;
            public Map map;
            public IntVec3 cell;
            public Rot4 rot;
            public Vector3 at;          // the drawn middle (wide doors)
            public CellRect rect;
            public ModelEntry styled;   // the entry of its style, if the style has its own models
            public bool supported;      // a Building_SupportedDoor
            public Color tint;
            public string swatch;
            public float openPct;       // the game's OpenPct (gathering leaves)
            public bool hackShown;      // its hacking terminal still stands
            public Thing thing;         // the built door (null for a placement ghost)
        }

        private static Site SiteOf(Building_Door door)
        {
            CompHackable hack = door.GetComp<CompHackable>();
            return new Site
            {
                def = door.def, map = door.Map, cell = door.Position, rot = door.Rotation, at = door.DrawPos, rect = door.OccupiedRect(),
                styled = ModelLibrary.StyledEntry(door), supported = door is Building_SupportedDoor, tint = door.DrawColor,
                swatch = R3DTextures.StuffSwatch(door), openPct = Patches.DoorOpenPct(door), hackShown = hack != null && !hack.IsHacked,
                thing = door
            };
        }

        // openDistance is how far each leaf has slid, in cells (0 = shut). Returns false if the
        // door could not be drawn, so the game draws its own.
        public static bool Draw(Building_Door door, float openDistance)
        {
            DynamicModels.SeeParts = true;   // door leaves go see-through while building (BuildSee)
            try
            {
                return DrawOne(SiteOf(door), openDistance);
            }
            finally
            {
                DynamicModels.SeeParts = false;
            }
        }

        public static bool DrawWide(Building_Door door, ModelEntry entry, float openPct, bool stuckOpen)
        {
            DynamicModels.SeeParts = true;
            try
            {
                return DrawWideOne(SiteOf(door), entry, openPct, stuckOpen);
            }
            finally
            {
                DynamicModels.SeeParts = false;
            }
        }

        public static bool DrawSupported(Building_Door door, ModelEntry entry, float openPct)
        {
            DynamicModels.SeeParts = true;
            try
            {
                return DrawSupportedOne(SiteOf(door), entry, openPct);
            }
            finally
            {
                DynamicModels.SeeParts = false;
            }
        }

        // The placement preview of a door (Ghosts.DrawPreview): the shut door as it will be drawn once built (the same
        // choice of drawing as the game's DrawAt prefixes make), each model part a see-through ghost in col. False if
        // IsoRim would not draw this door in 3D (the game's flat ghost then stands).
        internal static bool Ghost(ThingDef def, IntVec3 cell, Rot4 rot, Map map, ThingStyleDef style, Color col)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (map == null || def == null || !typeof(Building_Door).IsAssignableFrom(def.thingClass) || !st.doors3D || !cell.InBounds(map))
            {
                return false;
            }
            bool supported = typeof(Building_SupportedDoor).IsAssignableFrom(def.thingClass);
            Site d = new Site
            {
                def = def, map = map, cell = cell, rot = rot, at = GenThing.TrueCenter(cell, rot, def.size, 0f), rect = GenAdj.OccupiedRect(cell, rot, def.size),
                styled = st.models3D ? ModelLibrary.StyledEntry(def, style) : null, supported = supported, tint = Color.white, swatch = R3DTextures.SwatchOf(null)
            };
            ModelEntry entry = null;
            bool hasEntry = st.models3D && ModelLibrary.TryGet(def, out entry) && entry.HasModel;
            if (!hasEntry)
            {
                entry = null;
            }
            DynamicModels.GhostColour = col;
            DynamicModels.Ghosting = true;
            try
            {
                if (def.size.x == 1 && def.size.z == 1)
                {
                    if (supported && entry != null)
                    {
                        d.rot = DoorUtility.DoorRotationAt(cell, map, def.building != null && def.building.preferConnectingToFences);   // as DoorPreDraw
                        return DrawSupportedOne(d, entry, 0f);
                    }
                    return DrawOne(d, 0f);
                }
                if (supported && entry != null && !typeof(Building_MultiTileDoor).IsAssignableFrom(def.thingClass))
                {
                    return DrawSupportedOne(d, entry, 0f);   // (Api 29) a multi-cell supported door, as placed
                }
                return typeof(Building_MultiTileDoor).IsAssignableFrom(def.thingClass) && entry != null && entry.doorLeaf != null && DrawWideOne(d, entry, 0f, false);
            }
            finally
            {
                DynamicModels.Ghosting = false;
            }
        }

        private static bool DrawOne(Site d, float openDistance)
        {
            if (!tried)
            {
                tried = true;
                string root = IsoRimMod.RootDir;
                if (root != null)
                {
                    leaf = ModelLibrary.LoadFile(Path.Combine(root, "Models", "door_leaf.obj"));
                    lintel = ModelLibrary.LoadFile(Path.Combine(root, "Models", "door_lintel.obj"));
                    gateLeaf = ModelLibrary.LoadFile(Path.Combine(root, "Models", "gate_leaf.obj"));
                    gatePosts = ModelLibrary.LoadFile(Path.Combine(root, "Models", "gate_posts.obj"));
                }
            }
            Map map = d.map;
            if (leaf == null || map == null)
            {
                return false;
            }

            // The door lies along whichever line has more wall next to it.
            IntVec3 c = d.cell;
            int eastWest = Block(map, c + IntVec3.East) + Block(map, c + IntVec3.West);
            int northSouth = Block(map, c + IntVec3.North) + Block(map, c + IntVec3.South);
            float yaw = eastWest >= northSouth ? 0f : 90f;

            Vector3 along = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.right;
            Vector3 centre = new Vector3(c.x + 0.5f, 0f, c.z + 0.5f);

            // A door of a style with its own models (ModelDef.style: a pack's Viking door): its frame (model) and two
            // leaves (doorLeafModel, the other the same turned half round) in place of the plain ones, slid as those;
            // as tall as the walls, cut down with them.
            // (Api 18) With no style entry, the door's own thingDef entry if it has a frame and a leaf (the animal flap).
            ModelEntry own = IsoRimMod.Settings.models3D ? d.styled : null;
            ModelEntry e;
            if (own == null && IsoRimMod.Settings.models3D && !d.supported && ModelLibrary.TryGet(d.def, out e)
                && e.HasModel && e.doorLeaf != null)
            {
                own = e;
            }
            if (own != null && own.HasModel)
            {
                float ownHeight = View3D.WallHeight * (WallBoxes.CellCut(map, c) ? WallBoxes.CutFraction : 1f);
                float s = own.def.scale;
                Matrix4x4 ownSized = Matrix4x4.Scale(new Vector3(s, s * ownHeight / BuiltForWallHeight, s));
                float ownYaw = yaw + own.def.rotation;
                Vector3 ownAlong = Quaternion.AngleAxis(ownYaw, Vector3.up) * Vector3.right;
                Color ownTint = d.tint;
                string ownSwatch = d.swatch;
                DynamicModels.DrawLocal(own.variants[0], centre, ownYaw, ownSized, ownTint, ownSwatch);
                if (own.doorLeaf != null && own.def.doorLeafGather > 0f)
                {
                    // (Api 18) Leaves gathered toward their jambs (x 0.5 of the leaf), like curtains drawn aside.
                    float g = 1f - Mathf.Clamp01(own.def.doorLeafGather) * Mathf.Clamp01(d.openPct);
                    Matrix4x4 gathered = ownSized * Matrix4x4.Translate(new Vector3(0.5f, 0f, 0f))
                        * Matrix4x4.Scale(new Vector3(g, 1f, 1f)) * Matrix4x4.Translate(new Vector3(-0.5f, 0f, 0f));
                    DynamicModels.DrawLocal(own.doorLeaf, centre, ownYaw, gathered, ownTint, ownSwatch);
                    DynamicModels.DrawLocal(own.doorLeaf, centre, ownYaw + 180f, gathered, ownTint, ownSwatch);
                }
                else if (own.doorLeaf != null)
                {
                    float slide = Mathf.Clamp(openDistance, 0f, 0.48f) * s;
                    DynamicModels.DrawLocal(own.doorLeaf, centre + ownAlong * slide, ownYaw, ownSized, ownTint, ownSwatch);
                    DynamicModels.DrawLocal(own.doorLeaf, centre - ownAlong * slide, ownYaw + 180f, ownSized, ownTint, ownSwatch);
                }
                return true;
            }

            // A gate in a fence: two low leaves between a pair of posts, with no beam over them.
            string defName = d.def.defName ?? "";
            if (gateLeaf != null && (defName.IndexOf("Gate", StringComparison.OrdinalIgnoreCase) >= 0 || defName.Contains("Fence")))
            {
                Color gateTint = d.tint;
                string gateSwatch = d.swatch;
                float swing = Mathf.Clamp(openDistance, 0f, 0.44f);
                DynamicModels.DrawLocal(gateLeaf, centre + along * swing, yaw, Matrix4x4.identity, gateTint, gateSwatch);
                DynamicModels.DrawLocal(gateLeaf, centre - along * swing, yaw + 180f, Matrix4x4.identity, gateTint, gateSwatch);
                if (gatePosts != null)
                {
                    DynamicModels.DrawLocal(gatePosts, centre, yaw, Matrix4x4.identity, gateTint, gateSwatch);
                }
                return true;
            }

            // A door in a wall that is cut down is cut down with it.
            float height = View3D.WallHeight;
            if (WallBoxes.CellCut(map, c))
            {
                height *= WallBoxes.CutFraction;
            }
            Matrix4x4 sized = Matrix4x4.Scale(new Vector3(1f, height / BuiltForWallHeight, 1f));
            Color tint = d.tint;
            string swatch = d.swatch;
            float open = Mathf.Clamp(openDistance, 0f, 0.48f);

            // The two leaves are the same model, one turned half way round. Drawn this way their
            // shading follows the light, not the model, so both come out the same.
            DynamicModels.DrawLocal(leaf, centre + along * open, yaw, sized, tint, swatch);
            DynamicModels.DrawLocal(leaf, centre - along * open, yaw + 180f, sized, tint, swatch);
            if (lintel != null)
            {
                DynamicModels.DrawLocal(lintel, centre, yaw, sized, tint, swatch);
            }
            return true;
        }

        // A door wider than one cell with its own models (ornate door, ModelDef doorLeafModel): the
        // fixed frame (and arch, doorTopModel) over the doorway and two leaves sliding apart into the
        // walls either side, as far as the game slides its own (0.7 of a leaf's width for the
        // ornate door). Lies along the door's rotation; as tall as the walls, cut down with them
        // (without the arch, which would float over the cut wall). openPct is the game's OpenPct;
        // a door stuck open shows no leaves, as in the game. doorUpperLeafModel (security door plates) is a second pair
        // sliding as the game's upper mover, 0.35 * clamp01(OpenPct * 2.5) door widths. Returns false if it drew nothing.
        private static bool DrawWideOne(Site d, ModelEntry entry, float openPct, bool stuckOpen)
        {
            Map map = d.map;
            if (map == null || entry == null || !entry.HasModel || entry.doorLeaf == null)
            {
                return false;
            }
            ModelDef def = entry.def;
            float yaw = d.rot.AsAngle + def.rotation;
            Vector3 along = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.right;
            Vector3 at = d.at;
            Vector3 centre = new Vector3(at.x, 0f, at.z);

            float height = View3D.WallHeight;
            bool cut = false;
            bool slab = false;
            foreach (IntVec3 c in d.rect)
            {
                cut |= WallBoxes.CellCut(map, c);
                slab |= Floors.SlabOver(map, c);   // a stacked floor right above: no room for the arch
            }
            if (cut)
            {
                height *= WallBoxes.CutFraction;
            }
            float s = def.scale;
            Model frame = entry.variants[0];
            // How tall one model cell is drawn: as the walls the models are built for, or (crownTop) so that the
            // frame's top stands crownTop wall heights high.
            float up = s * height / BuiltForWallHeight;
            bool crowned = def.crownTop > 0f && frame.maxY > 0.01f;
            float wallTop = 0f;   // with a crown under a slab: the wall top in model space, where everything is cut off
            if (crowned)
            {
                up = def.crownTop * height / frame.maxY;
                if (slab)
                {
                    wallTop = frame.maxY / def.crownTop;   // the same at any wall height and cut-down
                }
            }
            Matrix4x4 sized = Matrix4x4.Scale(new Vector3(s, up, s));
            Color tint = d.tint;
            string swatch = d.swatch;

            DynamicModels.DrawLocal(wallTop > 0f ? Crowns.Under(frame, wallTop, true) : frame, centre, yaw, sized, tint, swatch);
            if (entry.doorTop != null && !cut && !slab)
            {
                DynamicModels.DrawLocal(entry.doorTop, centre, yaw, sized, tint, swatch);
            }
            if (!stuckOpen && def.doorLeafRise > 0f)
            {
                // A portcullis: one leaf across the doorway, raised as the door opens (doorLeafRise).
                float lift = def.doorLeafRise * Mathf.Clamp01(openPct);   // model cells
                float rise = lift * up;
                Model portcullis = entry.doorLeaf;
                if (wallTop > 0f)
                {
                    // Under a slab it rises into the floor above: only what is still under the wall top shows.
                    portcullis = Crowns.Under(entry.doorLeaf, wallTop - lift, false);
                }
                if (portcullis != null)
                {
                    DynamicModels.DrawLocal(portcullis, centre + Vector3.up * rise, yaw, sized, tint, swatch);
                }
            }
            else if (!stuckOpen)
            {
                // The game: each leaf's middle sits 0.25 + 0.35 * OpenPct door widths from the centre.
                float slide = 0.35f * Mathf.Clamp01(openPct) * d.def.size.x * s;
                Model leaf = wallTop > 0f ? Crowns.Under(entry.doorLeaf, wallTop, false) : entry.doorLeaf;
                DynamicModels.DrawLocal(leaf, centre + along * slide, yaw, sized, tint, swatch);
                DynamicModels.DrawLocal(leaf, centre - along * slide, yaw + 180f, sized, tint, swatch);
                if (entry.doorUpperLeaf != null)
                {
                    float slide2 = 0.35f * Mathf.Clamp01(openPct * 2.5f) * d.def.size.x * s;
                    Model upper = wallTop > 0f ? Crowns.Under(entry.doorUpperLeaf, wallTop, false) : entry.doorUpperLeaf;
                    DynamicModels.DrawLocal(upper, centre + along * slide2, yaw, sized, tint, swatch);
                    DynamicModels.DrawLocal(upper, centre - along * slide2, yaw + 180f, sized, tint, swatch);
                }
            }
            return true;
        }

        // A supported door with its own models (Building_SupportedDoor: vac barrier, ancient blast door; Api 29: also
        // multi-cell ones, not the game's Building_MultiTileDoor, which has its own path): the fixed frame (model) and
        // lintel (doorTopModel) across the doorway, along the door's rotation as the game sets it (North: walls east and
        // west), and, if it has doorLeafModel, two leaves sliding apart as far as the game slides its own movers (0.45 *
        // OpenPct, times the door's width for a multi-cell door). A multi-cell door's models are built for its builtFor
        // footprint around its drawn middle. hackTerminalModel is drawn while the door's CompHackable is not hacked. As
        // tall as the walls, cut down with them (no lintel over a cut wall). Returns false if it drew nothing.
        // Where a supported door's parts go: its middle on the ground, its yaw, the parts' scale (wall height) and whether
        // the cut-away lowers it.
        private static void SupportedFrame(Site d, ModelEntry entry, out Vector3 centre, out float yaw, out Matrix4x4 sized, out bool cut)
        {
            Map map = d.map;
            ModelDef def = entry.def;
            yaw = d.rot.AsAngle + def.rotation;
            IntVec3 c = d.cell;
            bool wide = d.def.size.x > 1 || d.def.size.z > 1;
            centre = wide ? new Vector3(d.at.x, 0f, d.at.z) : new Vector3(c.x + 0.5f, 0f, c.z + 0.5f);
            float height = View3D.WallHeight;
            cut = WallBoxes.CellCut(map, c);
            if (wide && !cut)
            {
                foreach (IntVec3 x in d.rect)
                {
                    if (x.InBounds(map) && WallBoxes.CellCut(map, x))
                    {
                        cut = true;
                        break;
                    }
                }
            }
            if (cut)
            {
                height *= WallBoxes.CutFraction;
            }
            float s = def.scale;
            sized = Matrix4x4.Scale(new Vector3(s, s * height / BuiltForWallHeight, s));
        }

        // (Api 30) A supported door's glass part (glassModel) where its other parts stand: a vac barrier's field (drawn only
        // where the game draws its field sprite, in its place: Patches.GraphicDraw_Prefix) or a window. fieldColour: the
        // colour of the game's own sprite, the default tint of a field.
        internal static void DrawSupportedGlass(Building_Door door, ModelEntry entry, Color? fieldColour)
        {
            if (door == null || entry == null || entry.glass == null || !door.Spawned)
            {
                return;
            }
            Site d = SiteOf(door);
            Patches.PreDrawDoor(door);   // (the game's rotation of a one-cell door, from the walls beside it)
            d.rot = door.Rotation;
            SupportedFrame(d, entry, out Vector3 centre, out float yaw, out Matrix4x4 sized, out bool cut);
            Glass.DrawPart(door, entry, centre, yaw, sized, fieldColour);
        }

        private static bool DrawSupportedOne(Site d, ModelEntry entry, float openPct)
        {
            Map map = d.map;
            if (map == null || entry == null || !entry.HasModel)
            {
                return false;
            }
            ModelDef def = entry.def;
            SupportedFrame(d, entry, out Vector3 centre, out float yaw, out Matrix4x4 sized, out bool cut);
            Vector3 along = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.right;
            float s = def.scale;
            IntVec3 c = d.cell;
            Color tint = d.tint;
            string swatch = d.swatch;

            DynamicModels.DrawLocal(entry.variants[0], centre, yaw, sized, tint, swatch);
            if (entry.doorTop != null && !cut)
            {
                DynamicModels.DrawLocal(entry.doorTop, centre, yaw, sized, tint, swatch);
            }
            if (entry.doorLeaf != null)
            {
                float slide = 0.45f * Mathf.Clamp01(openPct) * s * Mathf.Max(1, d.def.size.x);
                DynamicModels.DrawLocal(entry.doorLeaf, centre + along * slide, yaw, sized, tint, swatch);
                DynamicModels.DrawLocal(entry.doorLeaf, centre - along * slide, yaw + 180f, sized, tint, swatch);
            }
            if (entry.hackTerminal != null && d.hackShown)
            {
                DynamicModels.DrawLocal(entry.hackTerminal, centre, yaw, sized, tint, swatch);
            }
            // (Api 30) a window of glass in the door, every frame; a vac barrier's field instead goes where the game draws
            // its field sprite (Patches.GraphicDraw_Prefix), so it shows exactly while the game shows the field
            if (entry.glass != null && d.thing != null && !DynamicModels.Ghosting && !(d.thing is Building_VacBarrier))
            {
                Glass.DrawPart(d.thing, entry, centre, yaw, sized, null);
            }
            return true;
        }

        private static int Block(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map))
            {
                return 0;
            }
            Building b = cell.GetEdifice(map);
            if (b == null)
            {
                return 0;
            }
            return WallBoxes.IsWallForm(Forms.For(b.def)) || b is Building_Door || (b.def.defName != null && b.def.defName.Contains("Fence")) ? 1 : 0;
        }
    }

    // Flames that flicker and smoke and steam that rise, built from small 3D shapes in the same
    // style as everything else and moved a little every frame: over campfires (while they have
    // fuel) and over steam geysers (unless a generator sits on them).
    public static class Effects
    {
        private static bool tried;
        private static Model flame;
        private static Model flameCore;
        private static Model puff;
        private static ThingDef campfire;
        private static ThingDef geyser;

        // Called every frame while the 3D view is on.
        public static void DrawAll()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            Map map = Find.CurrentMap;
            if (!st.effects3D || !st.models3D || map == null || Find.TickManager == null)
            {
                return;
            }
            Load();
            try
            {
                // Time in seconds of game time, so everything stands still while the game is paused.
                float t = Find.TickManager.TicksGame / 60f;
                float wind = map.windManager != null ? Mathf.Clamp(map.windManager.WindSpeed, 0f, 2f) : 0.5f;
                CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(4);

                List<ThingDef> defs = FlameDefs();
                for (int d = 0; d < defs.Count; d++)
                {
                    List<Thing> fires = map.listerThings.ThingsOfDef(defs[d]);
                    for (int i = 0; i < fires.Count; i++)
                    {
                        if (view.Contains(fires[i].Position) && !HiddenByFog(fires[i]))
                        {
                            if (defs[d] == campfire)
                            {
                                Campfire(fires[i], t, wind);
                            }
                            else
                            {
                                LiveFlame(fires[i], t);
                            }
                        }
                    }
                }
                if (st.waterStyle)
                {
                    Sparkles(map, view, t);
                }
                if (geyser != null && puff != null)
                {
                    List<Thing> vents = map.listerThings.ThingsOfDef(geyser);
                    for (int i = 0; i < vents.Count; i++)
                    {
                        if (view.Contains(vents[i].Position) && !HiddenByFog(vents[i]))
                        {
                            Geyser(vents[i], map, t, wind);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing flames and smoke failed: " + e, 93611080);
            }
        }

        private static void Load()
        {
            if (tried)
            {
                return;
            }
            tried = true;
            string root = IsoRimMod.RootDir;
            if (root != null)
            {
                flame = ModelLibrary.LoadFile(Path.Combine(root, "Models", "fx_flame.obj"));
                flameCore = ModelLibrary.LoadFile(Path.Combine(root, "Models", "fx_flame_core.obj"));
                flameDark = ModelLibrary.LoadFile(Path.Combine(root, "Models", "fx_flame_dark.obj"));
                flameDarkCore = ModelLibrary.LoadFile(Path.Combine(root, "Models", "fx_flame_dark_core.obj"));
                puff = ModelLibrary.LoadFile(Path.Combine(root, "Models", "fx_puff.obj"));
            }
            campfire = DefDatabase<ThingDef>.GetNamedSilentFail("Campfire");
            geyser = DefDatabase<ThingDef>.GetNamedSilentFail("SteamGeyser");
        }

        // A thing the game's fog hides is not drawn, as the game leaves fogged things out (lid on or off; these
        // live flames and steam columns are drawn here every frame, outside the fog clip of RoofClip).
        private static bool HiddenByFog(Thing t)
        {
            return RoofClip.FogOn && !t.def.seeThroughFog && t.Fogged();
        }

        private static Mesh glint;

        // Little stars of light that wink on and off across open water, in three groups that take
        // turns so the surface always has a few. Which cells sparkle is picked afresh each time.
        // The open water cells of the current map, looked up again now and then, so the sparkles
        // never have to search the whole view.
        private static bool[] waterGrid;
        private static int waterCount;
        private static Map waterMap;
        private static int waterFrame = -100000;

        private static void Sparkles(Map map, CellRect view, float t)
        {
            if (glint == null)
            {
                glint = MakeGlint();
            }
            Material mat = R3DMaterials.Solid;
            if (mat == null)
            {
                return;
            }
            int frame = Time.frameCount;
            if (map != waterMap || waterGrid == null || frame - waterFrame > 2500)
            {
                waterMap = map;
                waterFrame = frame;
                int n = map.cellIndices.NumGridCells;
                if (waterGrid == null || waterGrid.Length != n)
                {
                    waterGrid = new bool[n];
                }
                waterCount = 0;
                TerrainGrid terrain = map.terrainGrid;
                for (int i = 0; i < n; i++)
                {
                    bool w = Ground.IsWater(terrain.TerrainAt(i));
                    waterGrid[i] = w;
                    if (w)
                    {
                        waterCount++;
                    }
                }
            }
            if (waterCount == 0)
            {
                return;
            }
            view = view.ClipInsideMap(map);
            CellIndices indices = map.cellIndices;
            float y = AltitudeLayer.Terrain.AltitudeFor() + 0.02f;
            for (int g = 0; g < 3; g++)
            {
                float clockT = t * 0.8f + g / 3f;
                int slot = Mathf.FloorToInt(clockT);
                float size = Mathf.Sin((clockT - slot) * Mathf.PI) * 0.17f;
                if (size < 0.025f)
                {
                    continue;
                }
                int salt = (slot * 3 + g) & 0xFFFF;
                for (int z = view.minZ; z <= view.maxZ; z++)
                {
                    for (int x = view.minX; x <= view.maxX; x++)
                    {
                        if (!waterGrid[indices.CellToIndex(x, z)] || Hashing.Unit(x, z, salt) < 0.985f)
                        {
                            continue;
                        }
                        Vector3 at = new Vector3(x + 0.15f + 0.7f * Hashing.Unit(x, z, salt + 1), y, z + 0.15f + 0.7f * Hashing.Unit(x, z, salt + 2));
                        float k = size * (0.7f + 0.6f * Hashing.Unit(x, z, salt + 3));
                        IsoGlow.DrawLit(glint, Matrix4x4.TRS(at, Quaternion.identity, new Vector3(k, 1f, k)), mat, 0);
                    }
                }
            }
        }

        // A flat four-pointed star one unit across.
        private static Mesh MakeGlint()
        {
            List<Vector3> v = new List<Vector3>();
            List<Color32> c = new List<Color32>();
            List<int> tris = new List<int>();
            Color32 white = new Color32(246, 252, 255, 255);
            v.Add(Vector3.zero);
            c.Add(white);
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f;
                float r = i % 2 == 0 ? 0.5f : 0.12f;
                v.Add(new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
                c.Add(white);
            }
            for (int i = 0; i < 8; i++)
            {
                tris.Add(0);
                tris.Add(1 + (i + 1) % 8);
                tris.Add(1 + i);
            }
            Mesh m = new Mesh();
            m.name = "IsoRim_Glint";
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            MeshGuard.Check(m, "glint");
            return m;
        }

        // Whether a live 3D flame is drawn over this thing now (its flat fire sprite is then left out).
        public static bool DrawsFlamesFor(Thing fire)
        {
            if (fire == null || fire.Map != Find.CurrentMap || flame == null && tried)
            {
                return false;
            }
            return Animates(fire.def);
        }

        // ---- live flames -----------------------------------------------------------------------
        // Every building the game draws with a fire overlay (CompFireOverlay, CompDarklightOverlay, CompRitualFireOverlay)
        // whose model shows where its flame burns (its own flame* parts, or ModelDef.flameAt and flameSize) gets the
        // campfire's flickering flame there, every frame: in place of the still flame in its baked model
        // (ModelBaker.NoFlame) and of the game's flat fire sprite (SectionLayer_IsoRimBodies.FlameStandsIn). Placed with
        // the bake's own placement (ModelBaker.PointOnMap: a wall torch's flame is on its wall), sized by the model.

        private struct FlameBox
        {
            public bool has;
            public Vector3 foot;     // bottom middle of the flame, model space
            public float height;
            public float width;
            public bool dark;        // darklight: the blue flame
        }

        // The campfire's flame, which Flame scales: tongues from FlameBase over the foot, about FlameHeight tall
        // and FlameWidth across.
        private const float FlameBase = 0.07f;
        private const float FlameHeight = 0.70f;
        private const float FlameWidth = 0.44f;

        private static Model flameDark;
        private static Model flameDarkCore;
        private static List<ThingDef> flameDefs;
        private static HashSet<ThingDef> flameSet;
        private static readonly Dictionary<int, FlameBox> boxes = new Dictionary<int, FlameBox>();   // by Model.id

        // A flame part of a model (flame, flame_core, flame_dark, ...), not embers.
        public static bool IsFlame(ModelMaterial mm)
        {
            return mm != null && mm.fire && mm.name != null && mm.name.StartsWith("flame", StringComparison.OrdinalIgnoreCase);
        }

        // Whether things of this def get the live flame (and so their model is baked without its own).
        public static bool Animates(ThingDef def)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (!st.effects3D || !st.models3D || def == null)
            {
                return false;
            }
            FlameDefs();
            return flameSet.Contains(def);
        }

        // The defs with a live flame, worked out once: the campfire, and every other one drawn every frame with a fire
        // overlay and a model that says where the flame is.
        private static List<ThingDef> FlameDefs()
        {
            if (flameDefs != null)
            {
                return flameDefs;
            }
            flameDefs = new List<ThingDef>();
            flameSet = new HashSet<ThingDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.drawerType != DrawerType.RealtimeOnly || def.GetCompProperties<CompProperties_FireOverlay>() == null)
                {
                    continue;
                }
                ModelEntry entry;
                if (!ModelLibrary.TryGet(def, out entry) || !entry.HasModel)
                {
                    continue;
                }
                if (def.defName == "Campfire" || BoxFor(entry, entry.variants[0]).has)
                {
                    flameDefs.Add(def);
                    flameSet.Add(def);
                }
            }
            return flameDefs;
        }

        private static FlameBox BoxFor(ModelEntry entry, Model m)
        {
            FlameBox b = new FlameBox();
            if (entry.def.flameAt.y >= 0f)
            {
                b.has = entry.def.flameSize.x > 0f;
                b.foot = entry.def.flameAt;
                b.height = entry.def.flameSize.x;
                b.width = entry.def.flameSize.y;
                return b;
            }
            if (m == null || m.positions == null || m.materials == null || m.material == null)
            {
                return b;
            }
            if (boxes.TryGetValue(m.id, out b))
            {
                return b;
            }
            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < m.positions.Length; i++)
            {
                ModelMaterial mm = m.materials[m.material[i]];
                if (!IsFlame(mm))
                {
                    continue;
                }
                b.has = true;
                b.dark |= mm.name.IndexOf("_dark", StringComparison.OrdinalIgnoreCase) >= 0;
                lo = Vector3.Min(lo, m.positions[i]);
                hi = Vector3.Max(hi, m.positions[i]);
            }
            if (b.has)
            {
                b.foot = new Vector3((lo.x + hi.x) * 0.5f, lo.y, (lo.z + hi.z) * 0.5f);
                b.height = hi.y - lo.y;
                b.width = Mathf.Max(hi.x - lo.x, hi.z - lo.z);
            }
            boxes[m.id] = b;
            return b;
        }

        // Whether the thing's fire burns now, as the game's own overlay decides: while there is fuel, or (a ritual
        // fire) once its ritual has gone far enough.
        public static bool Burning(Thing t)
        {
            CompRitualFireOverlay ritual = t.TryGetComp<CompRitualFireOverlay>();
            if (ritual != null)
            {
                if (Probe.On && Probe.RitualTarget(t))
                {
                    return true;   // harness: ritual=x,z
                }
                LordJob_Ritual job = t.TargetOfRitual();
                return job != null && job.Progress >= ritual.Props.minRitualProgress;
            }
            return SectionLayer_IsoRimBodies.Lit(t);
        }

        private static void LiveFlame(Thing t, float time)
        {
            if (!Burning(t) || !SectionLayer_IsoRimBodies.StandsIn(t))
            {
                return;   // out, or its model is not standing in (the game's own sprite and fire are drawn)
            }
            ModelEntry entry = Forms.For(t.def).model;
            if (entry == null || !entry.HasModel)
            {
                return;
            }
            Model m = entry.Pick(t);
            FlameBox b = BoxFor(entry, m);
            if (!b.has)
            {
                return;
            }
            Vector3 fit;
            float yaw;
            Vector3 foot = ModelBaker.PointOnMap(t, entry, m, b.foot, out fit, out yaw);
            float sy = b.height * fit.y / FlameHeight;
            float sxz = b.width * Mathf.Max(fit.x, fit.z) / FlameWidth;
            foot.y -= FlameBase * sy;
            // Blue as the game's darklight overlay, also for a model that only gives the flame's place (flameAt).
            bool dark = b.dark || t.TryGetComp<CompDarklightOverlay>() != null;
            Flame(foot, sy, sxz, dark, t.thingIDNumber * 0.37f, time);
        }

        // A live flame on something a figure wears (ApparelModels: a bound model's flame* faces): foot the bottom middle of
        // the flame* part (true space), height and width its size there, own its flicker's offset.
        internal static void FigureFlame(Vector3 foot, float height, float width, bool dark, float own)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (!st.effects3D || !st.models3D || Find.TickManager == null)
            {
                return;
            }
            Load();
            float sy = height / FlameHeight;
            float sxz = width / FlameWidth;
            foot.y -= FlameBase * sy;
            Flame(foot, sy, sxz, dark, own, Find.TickManager.TicksGame / 60f);
        }

        // Three tongues of flame round a brighter heart, each flickering to its own beat; the campfire's at sizes 1, 1.
        private static void Flame(Vector3 foot, float sy, float sxz, bool dark, float own, float t)
        {
            Model tongue = dark ? flameDark : flame;
            Model heart = dark ? flameDarkCore : flameCore;
            if (tongue != null)
            {
                for (int i = 0; i < 3; i++)
                {
                    float beat = t * (7.0f + i * 1.7f) + own + i * 2.1f;
                    float tall = 0.62f + 0.13f * Mathf.Sin(beat) + 0.05f * Mathf.Sin(beat * 2.3f);
                    float wide = 0.30f - 0.035f * Mathf.Sin(beat * 1.3f);
                    float a = i * 2.094f + 0.3f * Mathf.Sin(beat * 0.5f);
                    Matrix4x4 local = Matrix4x4.Translate(new Vector3(0.07f * Mathf.Cos(a) * sxz, FlameBase * sy, 0.07f * Mathf.Sin(a) * sxz))
                        * Matrix4x4.Scale(new Vector3(wide * sxz, tall * sy, wide * sxz));
                    DynamicModels.DrawLocal(tongue, foot, t * 50f * (i % 2 == 0 ? 1f : -1f) + i * 40f, local, Color.white, null);
                }
            }
            if (heart != null)
            {
                float beat = t * 11f + own;
                Matrix4x4 local = Matrix4x4.Translate(new Vector3(0f, 0.08f * sy, 0f))
                    * Matrix4x4.Scale(new Vector3(0.18f * sxz, (0.40f + 0.07f * Mathf.Sin(beat)) * sy, 0.18f * sxz));
                DynamicModels.DrawLocal(heart, foot, t * 90f, local, Color.white, null);
            }
        }

        // ---- burning fires --------------------------------------------------------------------
        // The game draws every fire that burns (the Fire thing: in a cell, burning its grass, plants, building, items or
        // corpse, or attached to a pawn on fire) one way only: its flat flickering sprite, Graphic_Flicker.DrawWorker,
        // sized by fireSize. In 3D that draw is the campfire's flame instead (Patches.FlickerDraw_Prefix), sized as the
        // game sizes the sprite: on the ground, on top of what burns in the cell (a wall, a bed, an item; a plant burns from the ground up), or on the body of
        // the one it burns on, on the side facing the camera. The game still decides when it is drawn (in view, not
        // fogged, not on a hidden floor); its light, fire glow, sparks and smoke stay the game's. False: draw the sprite.
        public static bool BurningFire(Fire fire)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (!st.effects3D || !st.models3D || !BurningOn || Find.TickManager == null)
            {
                return false;
            }
            Load();
            if (flame == null)
            {
                return false;
            }
            if (HiddenByFog(fire))
            {
                return true;
            }
            try
            {
                float s = Mathf.Min(fire.fireSize / 1.2f, 1.2f);   // the sprite's size (Graphic_Flicker)
                float sy = s * BurnTall;
                float sxz = s * BurnWide;
                Vector3 at = fire.DrawPos;
                Vector3 foot = new Vector3(at.x, 0f, at.z);
                Thing on = fire.parent;
                bool lifted = false;
                Matrix4x4 wasFloor = DrawHook.floorMatrix;
                bool wasOnFloor = DrawHook.floor;
                if (on == null)
                {
                    float top = BurningBase(fire);
                    if (top > 0f)
                    {
                        foot.y = Mathf.Max(0f, top - 0.2f * FlameHeight * sy);   // the top of it burns
                    }
                }
                else
                {
                    // Only pawns carry a fire (FireUtility.CanEverAttachFire). A small one still shows on the body.
                    Pawn p = on as Pawn;
                    bool lying = p == null || p.Dead || p.Downed;
                    float size = p != null ? Mathf.Clamp(p.BodySize, 0.3f, 2f) : 1f;
                    sy = Mathf.Max(s, MinOnBody) * BurnTall;
                    sxz = Mathf.Max(s, MinOnBody) * BurnWide;
                    float mid = lying ? 0.12f * Mathf.Sqrt(size) : Interaction.PickHeight(p) * 0.45f;
                    foot.y = Mathf.Max(0f, mid - 0.4f * FlameHeight * sy);
                    foot -= View3D.GroundForward * (lying ? 0.14f : 0.22f) * Mathf.Sqrt(size);   // in front of the body
                    if (on.Spawned)
                    {
                        lifted = true;   // a pawn on a stair step or a bridge deck: its rise (the fire's own draw has none)
                        DrawHook.floor = true;
                        DrawHook.floorMatrix = Floors.ExtraMatrix(on);
                    }
                }
                try
                {
                    BurnFlame(foot, sy, sxz, fire.thingIDNumber, Find.TickManager.TicksGame / 60f);
                }
                finally
                {
                    if (lifted)
                    {
                        DrawHook.floor = wasOnFloor;
                        DrawHook.floorMatrix = wasFloor;
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a fire failed: " + e, 93611081);
            }
            return true;
        }

        public static bool BurningOn = true;   // harness: burn3d=0 draws the flat sprite (the look before)
        public static readonly Dictionary<int, int> ForcedShape = new Dictionary<int, int>();   // harness: fire=x,z,size,shape

        // A burning fire is one of four shapes, so a field of them doesn't repeat: 0 one tall tongue, 1 a fork of two
        // leaning apart, 2 a low wide lick of four, 3 a cluster leaning one way. Its ID picks the shape (it never changes)
        // and its turn, size (+-15% across and up) and beat, so neighbours don't pulse together. Each has the yellow
        // heart a little wider than the campfire's and a little toward the camera, as the game's fire has a yellow centre.
        private static void BurnFlame(Vector3 foot, float sy, float sxz, int id, float t)
        {
            uint h = Mix((uint)id * 2654435761u + 0x3C6EF372u);
            uint g = Mix(h ^ 0x9E3779B9u);
            int shape = (int)(h & 3u);
            int forced;
            if (ForcedShape.Count > 0 && ForcedShape.TryGetValue(id, out forced))
            {
                shape = forced & 3;
            }
            float yaw = ((h >> 2) & 1023u) * (360f / 1024f);
            if (shape == 1 || shape == 3)
            {
                // The fork and the lean read only across the view: turned to the screen's sideways, give or take 35 degrees.
                Vector3 f = View3D.GroundForward;
                yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg + (yaw / 360f - 0.5f) * 70f + ((g >> 20) & 1u) * 180f;
            }
            sxz *= 0.85f + 0.30f * (((h >> 12) & 255u) / 255f);
            sy *= 0.85f + 0.30f * (((h >> 20) & 255u) / 255f);
            float own = (g & 1023u) * (6.2832f / 1024f);
            t *= 0.9f + 0.2f * (((g >> 10) & 255u) / 255f);   // its own pace
            Model m = flame;
            float b = t * 7f + own;
            float coreTall = 0.44f, coreWide = 0.22f;
            switch (shape)
            {
                case 0:   // one tall tongue with a small one at its side
                    Tongue(m, foot, yaw, sy, sxz, 0f, 0f, 0.34f, 1.02f + 0.15f * Mathf.Sin(b) + 0.06f * Mathf.Sin(b * 2.3f),
                        4f * Mathf.Sin(b * 0.7f), 0f, t * 50f);
                    Tongue(m, foot, yaw, sy, sxz, 0.14f, 0.02f, 0.24f, 0.48f + 0.09f * Mathf.Sin(b * 1.4f + 2f),
                        16f + 5f * Mathf.Sin(b * 0.9f), 0.15f, -t * 60f);
                    coreTall = 0.52f;
                    break;
                case 1:   // a fork: two tongues leaning apart
                    Tongue(m, foot, yaw, sy, sxz, 0.09f, 0f, 0.28f, 0.80f + 0.13f * Mathf.Sin(b) + 0.05f * Mathf.Sin(b * 2.1f),
                        17f + 5f * Mathf.Sin(b * 0.8f), 0f, t * 50f);
                    Tongue(m, foot, yaw, sy, sxz, -0.09f, 0f, 0.26f, 0.62f + 0.12f * Mathf.Sin(b * 1.2f + 2.4f),
                        17f + 5f * Mathf.Sin(b * 0.9f + 1.3f), Mathf.PI, -t * 55f);
                    break;
                case 2:   // a low wide lick of four
                    for (int i = 0; i < 4; i++)
                    {
                        float a = i * 1.5708f + 0.4f;
                        float bi = b * (1f + 0.13f * i) + i * 1.9f;
                        Tongue(m, foot, yaw, sy, sxz, 0.17f * Mathf.Cos(a), 0.17f * Mathf.Sin(a), 0.25f,
                            (i % 2 == 0 ? 0.40f : 0.30f) + 0.07f * Mathf.Sin(bi), 12f + 4f * Mathf.Sin(bi * 0.6f), a,
                            t * 45f * (i % 2 == 0 ? 1f : -1f));
                    }
                    coreTall = 0.32f;
                    coreWide = 0.28f;
                    break;
                default:   // a cluster leaning one way, its tongues of three heights
                {
                    float lean = 18f + 7f * Mathf.Sin(b * 0.8f);
                    Tongue(m, foot, yaw, sy, sxz, -0.07f, 0f, 0.29f, 0.74f + 0.12f * Mathf.Sin(b * 1.1f), lean, 0f, t * 50f);
                    Tongue(m, foot, yaw, sy, sxz, 0.04f, 0.09f, 0.23f, 0.50f + 0.10f * Mathf.Sin(b * 1.3f + 1.7f), lean * 0.8f, 0.3f, -t * 60f);
                    Tongue(m, foot, yaw, sy, sxz, 0.05f, -0.09f, 0.21f, 0.36f + 0.08f * Mathf.Sin(b * 1.6f + 3.1f), lean * 1.2f, -0.3f, t * 70f);
                    break;
                }
            }
            if (flameCore != null)
            {
                Vector3 at = foot - View3D.GroundForward * (0.05f * sxz);
                Matrix4x4 local = Matrix4x4.Translate(new Vector3(0f, 0.06f * sy, 0f))
                    * Matrix4x4.Scale(new Vector3(coreWide * sxz, (coreTall + 0.07f * Mathf.Sin(t * 11f + own)) * sy, coreWide * sxz));
                DynamicModels.DrawLocal(flameCore, at, yaw + t * 90f, local, Color.white, null);
            }
        }

        // One tongue of a burning fire: at (x, z) over the foot, wide and tall as the flame model's share, tipped lean
        // degrees toward the angle dir (radians) and spun about its own axis.
        private static void Tongue(Model m, Vector3 foot, float yaw, float sy, float sxz, float x, float z, float wide, float tall,
            float lean, float dir, float spin)
        {
            Matrix4x4 local = Matrix4x4.Translate(new Vector3(x * sxz, FlameBase * sy, z * sxz))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(lean, new Vector3(Mathf.Sin(dir), 0f, -Mathf.Cos(dir))))
                * Matrix4x4.Scale(new Vector3(wide * sxz, tall * sy, wide * sxz))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(spin, Vector3.up));
            DynamicModels.DrawLocal(m, foot, yaw, local, Color.white, null);
        }

        private static uint Mix(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        // The burning fire's flame against the campfire's at the same sprite size.
        private const float BurnTall = 1.25f;
        private const float BurnWide = 1.8f;
        private const float MinOnBody = 0.3f;   // the least sprite size of a fire on someone (smaller hides in the body)

        // The top of what burns in a fire's cell (not plants, which burn from the ground; not people or other fires).
        private static float BurningBase(Fire fire)
        {
            Map map = fire.Map;
            if (map == null || !fire.Position.InBounds(map))
            {
                return 0f;
            }
            float h = 0f;
            List<Thing> things = fire.Position.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing o = things[i];
                if (o != fire && !(o is Pawn) && !(o is Fire) && !(o is Plant) && !(o is Corpse) && !(o is Filth) && !(o is Mote))
                {
                    h = Mathf.Max(h, Interaction.PickHeight(o));
                }
            }
            return h;
        }

        private static void Campfire(Thing fire, float t, float wind)
        {
            ModelEntry entry;
            if (!ModelLibrary.TryGet(fire.def, out entry) || !entry.HasModel)
            {
                return;
            }
            CompRefuelable fuel = fire.TryGetComp<CompRefuelable>();
            if (fuel != null && !fuel.HasFuel)
            {
                return;   // burnt out
            }
            Vector3 at = fire.DrawPos;
            Vector3 foot = new Vector3(at.x, 0f, at.z);
            float own = fire.thingIDNumber * 0.37f;

            Flame(foot, 1f, 1f, false, own, t);

            Rise(foot, t * 0.30f + own, 5, 0.75f, 2.3f, 0.15f, 0.44f, 0.35f + 0.5f * wind, 0.30f, 0.60f);
        }

        private static void Geyser(Thing vent, Map map, float t, float wind)
        {
            ModelEntry entry;
            if (!ModelLibrary.TryGet(vent.def, out entry) || !entry.HasModel)
            {
                return;
            }
            // A geothermal generator built over the vent caps it.
            Building over = vent.Position.GetEdifice(map);
            if (over != null && over != vent && over.def.defName != null && over.def.defName.Contains("Geothermal"))
            {
                return;
            }
            Vector3 at = vent.DrawPos;
            Vector3 foot = new Vector3(at.x, 0f, at.z);
            float own = vent.thingIDNumber * 0.53f;
            Rise(foot, t * 0.42f + own, 7, 0.15f, 3.4f, 0.30f, 0.95f, 0.25f + 0.6f * wind, 0.86f, 0.97f);
        }

        // A column of puffs, each rising from 'from' to 'from + climb' above the ground, growing
        // from small to big, leaning with the wind, and shrinking away at the top. grey0 and
        // grey1 are its shade at the bottom and at the top.
        private static void Rise(Vector3 foot, float clock, int count, float from, float climb, float small, float big, float lean, float grey0, float grey1)
        {
            for (int i = 0; i < count; i++)
            {
                float u = Mathf.Repeat(clock + (float)i / count, 1f);
                float size = Mathf.Lerp(small, big, u) * Mathf.Clamp01(u / 0.12f) * Mathf.Clamp01((1f - u) / 0.28f);
                if (size < 0.02f)
                {
                    continue;
                }
                float wobble = clock * 6.28f * 0.7f + i * 1.9f;
                Vector3 p = new Vector3(
                    foot.x + lean * u * u + 0.07f * Mathf.Sin(wobble) * (0.3f + u),
                    from + climb * u,
                    foot.z + 0.25f * lean * u * u + 0.07f * Mathf.Cos(wobble * 1.3f) * (0.3f + u));
                float g = Mathf.Lerp(grey0, grey1, u);
                DynamicModels.DrawLocal(puff, p, i * 67f + clock * 40f, Matrix4x4.Scale(new Vector3(size, size * 0.9f, size)), new Color(g, g, g * 1.02f), null);
            }
        }
    }
}
