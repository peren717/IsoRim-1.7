using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 65) An animal's model, tint and texture by the body graphic the game shows it with (ModelDef.byBodyGraphic). The
    // game picks an animal's body sprite from its kind's life stage (bodyGraphicData, femaleGraphicData for a female) or one
    // of the kind's alternateGraphics (by its ID): a horse in one of six coats, a young tom drawn as a hen, a fawn as a spotted
    // baby whatever its sex. The first entry whose path matches stands in for model / femaleModel, the tint and the texture.
    //   - Paths: matched case-insensitively ('\' = '/'), on whole trailing segments: "Horse3" and "Animal/Horse/Horse3" both
    //     match Things/Pawn/Animal/Horse/Horse3, "Horse" does not match Horse3. A full path is an exact match.
    //   - Two paths are tried, in this order: the graphic the game draws the body with now (PawnRenderer.BodyGraphic: any
    //     other mod's change to it counts), then the living body graphic by the game's own rule (alternate, female, body:
    //     PawnRenderNode_AnimalPart.GraphicFor without rot), so a rotting or corpse-graphic body keeps its match.
    //   - Every animal draw goes through ModelEntry.Pick, LegFor, WingFor and Tint, so standing, asleep, downed, dead,
    //     carried, on a shelf, held by a host and Api.ReadAnimalModel all agree.
    //   - Cache: per pawn, the matching entry for its drawn graphic, life stage and sex (a life stage change, an alternate
    //     graphic, another mod's new body graphic make a new graphic object, so the entry is looked up again); looked up
    //     at most once a frame. Entries without byBodyGraphic never get here (ModelEntry.byBodyGraphic null).
    public static class BodyGraphicModels
    {
        public sealed class Entry
        {
            public string path;      // normalised: lower case, '/' separators, no leading or trailing '/'
            public Model model;      // the body (the def's parts merged, small outlines dropped as model's); null: model / femaleModel
            public Model leg;        // null: legModel / femaleLegModel
            public Model wing, wingMirror;   // null: wingModel / femaleWingModel
            public string texture;   // null: the models' own map_Kd
            public bool hasTint;
            public Color tint;
        }

        private sealed class Rec
        {
            public Graphic drawn;
            public PawnKindLifeStage stage;
            public Gender gender;
            public Entry[] entries;
            public int frame = -1;
            public Entry entry;
        }

        private static readonly Dictionary<int, Rec> recs = new Dictionary<int, Rec>();
        private static readonly Dictionary<Model, Dictionary<string, Model>> retextured = new Dictionary<Model, Dictionary<string, Model>>();

        internal static Entry[] Load(ModelDef md, Func<string, Model> body)
        {
            if (md.byBodyGraphic == null || md.byBodyGraphic.Count == 0 || md.modContentPack == null)
            {
                return null;
            }
            string root = md.modContentPack.RootDir;
            List<Entry> list = new List<Entry>();
            for (int i = 0; i < md.byBodyGraphic.Count; i++)
            {
                BodyGraphicModel b = md.byBodyGraphic[i];
                string path = b != null ? Normalise(b.path) : "";
                if (path.Length == 0)
                {
                    Log.Warning("[IsoRim] " + md.defName + ": a byBodyGraphic entry needs a path; it is left out.");
                    continue;
                }
                Entry e = new Entry { path = path, texture = b.texture.NullOrEmpty() ? null : b.texture.Replace('\\', '/').Trim('/') };
                if (!b.model.NullOrEmpty())
                {
                    e.model = body(b.model);
                    if (e.model == null)
                    {
                        continue;   // LoadFile has reported it
                    }
                }
                if (!b.legModel.NullOrEmpty())
                {
                    e.leg = ModelLibrary.LoadFile(Path.Combine(root, b.legModel));
                }
                if (!b.wingModel.NullOrEmpty())
                {
                    e.wing = ModelLibrary.LoadFile(Path.Combine(root, b.wingModel));
                    e.wingMirror = e.wing != null ? ObjLoader.Mirrored(e.wing) : null;
                }
                if (b.tint.a > 0f)
                {
                    e.hasTint = true;
                    e.tint = new Color(b.tint.r, b.tint.g, b.tint.b, 1f);
                }
                list.Add(e);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        public static string Normalise(string p)
        {
            if (p == null)
            {
                return "";
            }
            return p.Replace('\\', '/').ToLowerInvariant().Trim().Trim('/');
        }

        // True if pattern (normalised) is path (normalised) or its last whole segments.
        internal static bool Matches(string path, string pattern)
        {
            if (path == null || pattern.Length == 0 || path.Length < pattern.Length || !path.EndsWith(pattern, StringComparison.Ordinal))
            {
                return false;
            }
            return path.Length == pattern.Length || path[path.Length - pattern.Length - 1] == '/';
        }

        // The entry this pawn is drawn with now, or null (no byBodyGraphic, not a pawn, no match).
        public static Entry For(ModelEntry e, Thing t)
        {
            Pawn pawn = t as Pawn;
            if (e == null || e.byBodyGraphic == null || pawn == null)
            {
                return null;
            }
            try
            {
                Rec r;
                if (!recs.TryGetValue(pawn.thingIDNumber, out r))
                {
                    if (recs.Count > 4096)
                    {
                        recs.Clear();   // pawns long gone (another game): looked up again when drawn
                    }
                    r = new Rec();
                    recs[pawn.thingIDNumber] = r;
                }
                int frame = Time.frameCount;
                if (r.frame == frame && r.stage != null && r.entries == e.byBodyGraphic)
                {
                    return r.entry;
                }
                Graphic drawn = Drawn(pawn);
                PawnKindLifeStage stage = pawn.ageTracker != null ? pawn.ageTracker.CurKindLifeStage : null;
                if (r.frame < 0 || drawn != r.drawn || stage != r.stage || pawn.gender != r.gender || r.entries != e.byBodyGraphic)
                {
                    r.entries = e.byBodyGraphic;
                    r.drawn = drawn;
                    r.stage = stage;
                    r.gender = pawn.gender;
                    r.entry = Find(e.byBodyGraphic, drawn != null ? Normalise(drawn.path) : null, pawn, stage);
                }
                r.frame = frame;
                return r.entry;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[IsoRim] Matching an animal's body graphic (byBodyGraphic) failed: " + ex, 93621965);
                return null;
            }
        }

        private static Entry Find(Entry[] entries, string drawn, Pawn pawn, PawnKindLifeStage stage)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                string path = pass == 0 ? drawn : Normalise(LivingPath(pawn, stage));
                if (pass == 1 && path == drawn)
                {
                    break;   // the same path: no second look
                }
                if (path.NullOrEmpty())
                {
                    continue;
                }
                for (int i = 0; i < entries.Length; i++)
                {
                    if (Matches(path, entries[i].path))
                    {
                        return entries[i];
                    }
                }
            }
            return null;
        }

        // The graphic the game draws the pawn's body with now, or null.
        private static Graphic Drawn(Pawn pawn)
        {
            return pawn.Drawer != null && pawn.Drawer.renderer != null ? pawn.Drawer.renderer.BodyGraphic : null;
        }

        // The texture path of the body graphic the game gives this pawn alive and fresh (PawnRenderNode_AnimalPart.GraphicFor
        // before the rot and corpse graphics): its kind's alternate graphic, else her femaleGraphicData, else bodyGraphicData.
        public static string LivingPath(Pawn pawn, PawnKindLifeStage stage)
        {
            if (pawn == null || stage == null || stage.bodyGraphicData == null)
            {
                return null;
            }
            AlternateGraphic ag;
            int index;
            if (pawn.kindDef != null && pawn.kindDef.alternateGraphics != null && pawn.kindDef.alternateGraphics.Count > 0
                && pawn.TryGetAlternate(out ag, out index))
            {
                Graphic g = ag.GetGraphic(stage.bodyGraphicData.Graphic);
                return g != null ? g.path : null;
            }
            if (pawn.gender == Gender.Female && stage.femaleGraphicData != null)
            {
                return stage.femaleGraphicData.texPath;
            }
            return stage.bodyGraphicData.texPath;
        }

        // The body model with the entry's texture on it (a copy per model and texture), or m itself.
        internal static Model Textured(Entry b, Model m)
        {
            if (b == null || b.texture == null || m == null)
            {
                return m;
            }
            Dictionary<string, Model> byTex;
            if (!retextured.TryGetValue(m, out byTex))
            {
                byTex = new Dictionary<string, Model>();
                retextured[m] = byTex;
            }
            Model r;
            if (!byTex.TryGetValue(b.texture, out r))
            {
                r = Retexture(m, b.texture);
                byTex[b.texture] = r;
            }
            return r;
        }

        // A copy of src (its own id: meshes are cached by id) whose own textures (map_Kd, not stuff swatches, glowing parts or
        // run-time textures) are the given one.
        private static Model Retexture(Model src, string texture)
        {
            try
            {
                if (src.materials == null || src.material == null)
                {
                    return src;
                }
                Model m = ObjLoader.Merge(new List<Model> { src });
                ModelMaterial[] mats = new ModelMaterial[m.materials.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    ModelMaterial o = m.materials[i];
                    bool own = o.texture != null && o.runtimeTexture == null && !o.glow && o.texture.IndexOf("{stuff}", StringComparison.Ordinal) < 0;
                    mats[i] = new ModelMaterial
                    {
                        name = o.name, texture = own ? texture : o.texture, color = o.color, tinted = o.tinted, sheet = o.sheet, leaf = o.leaf,
                        twig = o.twig, glow = o.glow, fire = o.fire, beam = o.beam, round = o.round, runtimeTexture = o.runtimeTexture,
                        runtimeName = o.runtimeName, runtimeModelLight = o.runtimeModelLight
                    };
                }
                m.materials = mats;
                return m;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Retexturing an animal's model (byBodyGraphic texture) failed: " + e, 93621966);
                return src;
            }
        }

        // (harness) The model a texture copy was made from, or m itself.
        internal static Model SourceOf(Model m)
        {
            foreach (KeyValuePair<Model, Dictionary<string, Model>> kv in retextured)
            {
                foreach (Model copy in kv.Value.Values)
                {
                    if (copy == m)
                    {
                        return kv.Key;
                    }
                }
            }
            return m;
        }

        // The colour Animals.Draw draws the animal's model in and whether its textured materials take it (DynamicModels.TintTextured):
        // the matching entry's tint, else the vanilla graphic colour with tintFromGraphic (Api 49), else white.
        public static Color Tint(ModelEntry e, Pawn pawn, out bool tintTextured)
        {
            Entry b = For(e, pawn);
            if (b != null && b.hasTint)
            {
                tintTextured = true;
                return b.tint;
            }
            tintTextured = e.def.tintFromGraphic;
            return e.def.tintFromGraphic ? Animals.GraphicColour(pawn) : Color.white;
        }
    }
}
