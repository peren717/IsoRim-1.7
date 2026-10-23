using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 25: humanlike races other than the game's own people (Humanoid Alien Races and other frameworks; design
    // isorim-har-design.md). Every race but Human and CreepJoiner stays a flat card unless an alien pack (AlienModelDef) makes it
    // a 3D figure: with IsoRim's human figure placed for the race (Human) or with the race's own body and head models (Own).
    // The player's "Keep as paper" list wins over the packs. IsoRim never reads HAR: only the vanilla calls HAR patches
    // (the body and head mesh sets, which carry its draw sizes, BaseHeadOffsetAt, the render tree with its addon nodes).
    //
    // One chokepoint: People.FigureRace asks AlienFigures.Drawn3D (all five callers: People.Draw, MeasureWeapon, Report,
    // Patches.DrawnAsModel, Corpses.ShownAsModel). People.Work asks AlienFigures.Prepare / Finish for the race's placing
    // (scale, head, build, its own models, addon models and cards) into Look.alien; People.Draw puts the race's head
    // transform into the head matrix and its hands shift into the hands matrix, so everything placed on the head or held
    // (hats, hair, faces, figure hooks, face drawers, weapons, carried things) follows. Addon cards: AlienCards.

    /// <summary>How IsoRim draws a humanlike pawn (Api version 25; <see cref="Api.FigureModeOf"/>).</summary>
    public enum FigureMode
    {
        /// <summary>A flat card standing up (the game's own sprite): every non-human race without an alien pack.</summary>
        Card = 0,
        /// <summary>IsoRim's human figure: people and creepjoiners, and races whose pack says <c>Human</c>.</summary>
        Human = 1,
        /// <summary>The race's own body and head models from its alien pack (<c>Own</c>).</summary>
        Own = 2,
    }

    /// <summary>
    /// An alien pack entry (Api version 25): how one humanlike race is drawn in 3D. Without one, a race stays a card. See
    /// Docs/ModelPacks.md (alien packs) and Docs/Api.md part 16. Add it through a patch gated on Api 25 (the def type is new).
    /// </summary>
    public class AlienModelDef : Def
    {
        /// <summary>The race's ThingDef defName (or several in <see cref="aliens"/>).</summary>
        public string alien;
        public List<string> aliens;

        /// <summary>Own (the pack's own models), Human (IsoRim's human figure placed for the race) or Card.</summary>
        public FigureMode figure = FigureMode.Card;

        /// <summary>The packageId of the mod whose folder the model paths are relative to (a def added by a patch has no
        /// mod of its own); without it, the first active mod that has the file.</summary>
        public string modPackageId;

        /// <summary>Size of the whole figure (1 = a human). Default: Human, measured from the game's body mesh (HAR's draw
        /// size); Own 1.</summary>
        public float scale = float.NaN;

        /// <summary>Human: size of the head next to the body (1 = a human's). Default: measured (head mesh / body mesh).</summary>
        public float headScale = float.NaN;

        /// <summary>Human: the head moved (y up, z forward; cells of a figure of size 1). Default: measured from the game's
        /// head offset (BaseHeadOffsetAt) against a human's.</summary>
        public Vector3 headOffset = new Vector3(float.NaN, float.NaN, float.NaN);

        /// <summary>The race's body types onto IsoRim's builds (male, female, thin, fat, hulk): &lt;build&gt;&lt;MyBody&gt;thin&lt;/MyBody&gt;&lt;/build&gt;.
        /// Without an entry: a vanilla body type keeps its own, others by name (Female, Thin, Hulk, Fat), else male.</summary>
        public AlienBuildMap build;

        /// <summary>IsoRim: IsoRim's face (its eyes, Facial Animation's face, other mods' face drawers) on the head; on an Own race's
        /// head at the face anchor, sized by anchors.faceFit. None: no face (the head model has its own).</summary>
        public AlienFace face = AlienFace.IsoRim;

        /// <summary>The hair and beard: IsoRim (its hair shapes, by the hairstyle), Card (the game's own hair and beard sprites as cards,
        /// like the addons: for races whose hair art IsoRim's shapes do not match) or None (no hair; the head model or addons have it).</summary>
        public AlienHair hair = AlienHair.IsoRim;

        /// <summary>The race's own render nodes (HAR body addons: ears, tails, horns) without a model of their own drawn as
        /// cards from the game's sprites.</summary>
        public bool addonCards = true;

        /// <summary>Show: worn apparel as on people (fitted by bodyFit / headFit); Hide: only apparel with an apparel model
        /// binding for this race (ApparelModelDef.aliens) shows.</summary>
        public string clothes = "Show";

        /// <summary>Own: body models by body type and life stage (first match; an entry with neither matches all).</summary>
        public List<AlienBodyModel> bodies;

        /// <summary>Own: head models by head type or the head's texture path (first match).</summary>
        public List<AlienHeadModel> heads;

        /// <summary>Models (or hiding) for the race's render nodes, by the node's texture path.</summary>
        public List<AlienAddonModel> addons;

        /// <summary>Named points on the race's figure and the fits of IsoRim's human parts onto it.</summary>
        public AlienAnchors anchors;

        internal IEnumerable<string> Aliens()
        {
            if (!alien.NullOrEmpty())
            {
                yield return alien;
            }
            if (aliens != null)
            {
                foreach (string r in aliens)
                {
                    if (!r.NullOrEmpty())
                    {
                        yield return r;
                    }
                }
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (alien.NullOrEmpty() && aliens.NullOrEmpty())
            {
                yield return "no alien";
            }
            if (clothes != "Show" && clothes != "Hide")
            {
                yield return "unknown clothes " + clothes + " (Show, Hide)";
            }
            if (build != null)
            {
                foreach (KeyValuePair<string, string> p in build.map)
                {
                    if (p.Value != "male" && p.Value != "female" && p.Value != "thin" && p.Value != "fat" && p.Value != "hulk")
                    {
                        yield return "unknown build " + p.Value + " for " + p.Key + " (male, female, thin, fat, hulk)";
                    }
                }
            }
            if (bodies != null)
            {
                foreach (AlienBodyModel b in bodies)
                {
                    if (b.upper.NullOrEmpty() || b.lower.NullOrEmpty())
                    {
                        yield return "a body without upper and lower";
                    }
                }
            }
        }
    }

    /// <summary>AlienModelDef.face (Api 25): who draws the race's face.</summary>
    /// <summary>AlienModelDef.hair (Api 25): how the race's hair and beard are drawn.</summary>
    public enum AlienHair
    {
        IsoRim = 0,
        Card = 1,
        None = 2,
    }

    public enum AlienFace
    {
        /// <summary>IsoRim's face, as on people (and other mods' face drawers).</summary>
        IsoRim = 0,
        /// <summary>No face: the head model has its own.</summary>
        None = 1,
    }

    /// <summary>AlienModelDef.build: body type defName to IsoRim build, written as &lt;MyBody&gt;thin&lt;/MyBody&gt;.</summary>
    public class AlienBuildMap
    {
        internal readonly Dictionary<string, string> map = new Dictionary<string, string>();

        public void LoadDataFromXmlCustom(XmlNode root)
        {
            foreach (XmlNode n in root.ChildNodes)
            {
                if (n is XmlElement)
                {
                    map[n.Name] = n.InnerText.Trim().ToLowerInvariant();
                }
            }
        }
    }

    /// <summary>One own body of an alien pack (two models as IsoRim's person_&lt;build&gt;_upper / _lower: the upper part takes
    /// the shirt colour or the skin, the lower the trousers; faces under a material named stuff... take it).</summary>
    public class AlienBodyModel
    {
        public string bodyType;
        public string lifeStage;
        public string upper;
        public string lower;
        /// <summary>The IsoRim build whose clothes, packs and belts are fitted onto this body (with anchors.bodyFit).</summary>
        public string fitAs = "male";
    }

    /// <summary>One own head of an alien pack (modelled in the race figure's space, as the bodies).</summary>
    public class AlienHeadModel
    {
        public string headType;
        /// <summary>The head type's texture path; a trailing / matches every path under it.</summary>
        public string graphicPath;
        public string model;
    }

    /// <summary>A model for a race's render node (a HAR body addon), or hiding it, by the node's texture path.</summary>
    public class AlienAddonModel
    {
        /// <summary>The node's texture path (case does not matter, \ = /); a trailing / matches every path under it.</summary>
        public string graphicPath;
        public string model;
        /// <summary>The named anchor the model's origin goes to (head, face, neck, back, waist, handLeft, handRight,
        /// tailRoot); default head for nodes on the head, tailRoot for the others.</summary>
        public string anchor;
        public Vector3 offset = Vector3.zero;
        public Vector3 rotation = Vector3.zero;
        public float scale = 1f;
        /// <summary>Not drawn at all (no card, no model): for parts the race's own models already have.</summary>
        public bool hide;
        /// <summary>What the model's stuff faces take: Material (the node's colour, HAR's colour channel), Skin, Hair, or White.</summary>
        public string colour = "Material";
        /// <summary>On the head, while headgear is drawn: the addon (card or model) is lifted by this share of how far the hat reaches
        /// above the head (0: stays; 1: by all of it), so ears that show over hats in 2D stick out of the 3D hat.</summary>
        public float hatLift;
    }

    /// <summary>Named points of a race's figure (cells, a figure of size 1, feet at the origin, y up, facing +z), and how
    /// IsoRim's human parts fit onto it. Points not given are IsoRim's person figure's.</summary>
    public class AlienAnchors
    {
        private static readonly Vector3 Unset = new Vector3(float.NaN, float.NaN, float.NaN);
        /// <summary>The head's pivot: where IsoRim's human neck (0, 0.74, 0) goes; the head frame (hats, hair, eyes, face
        /// drawers, head hooks) turns with it.</summary>
        public Vector3 head = Unset;
        /// <summary>IsoRim's head parts (hats, hair, eyes) scaled about the pivot onto the race head.</summary>
        public Vector3 headFit = Vector3.one;
        public Vector3 face = Unset;
        public Vector3 neck = Unset;
        public Vector3 back = Unset;
        public Vector3 waist = Unset;
        public Vector3 handLeft = Unset;
        public Vector3 handRight = Unset;
        public Vector3 tailRoot = Unset;
        /// <summary>IsoRim's clothes, packs and belts (made for the body's fitAs build) scaled onto the race body.</summary>
        public Vector3 bodyFit = Vector3.one;
        /// <summary>IsoRim's face (made on its person head, the eyes' middle at (0, 0.95, 0.22)) scaled about that point onto the
        /// face anchor of an Own race's head model. Default: headFit.</summary>
        public Vector3 faceFit = Unset;

        internal static bool Set(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z);
        }
    }

    // A race's placing for one person, worked out with the look (People.Work) and kept in Look.alien (null for people).
    internal sealed class AlienLook
    {
        public ThingDef race;
        public AlienModelDef pack;
        public FigureMode mode;
        public string build;
        public float scale = 1f;          // in Look.size
        public float headScale = 1f;      // about the neck
        public Vector3 headShift;         // y up, z forward (figure of size 1)
        public float bodyDraw = 1f, headDraw = 1f;   // the game's measured mesh sizes against a human's (cards)
        public Matrix4x4 head = Matrix4x4.identity;  // onto People.Draw's head matrix
        public Matrix4x4 headInv = Matrix4x4.identity;
        public float handShift;           // the hands moved up (Own: handLeft / handRight against IsoRim's 0.46)
        public bool clothesHidden;
        public Model ownHead, ownUpper, ownLower;
        public bool faceShown = true;
        public Matrix4x4 face = Matrix4x4.identity;   // IsoRim's head space to where the face goes in the head frame (eyes, face drawers, decals)
        public Vector3 headFit = Vector3.one, bodyFit = Vector3.one;
        public readonly Dictionary<string, Vector3> anchors = new Dictionary<string, Vector3>();   // body frame (head ones: head frame)
        public readonly List<Model> extraModels = new List<Model>();
        public readonly List<Matrix4x4> extraLocals = new List<Matrix4x4>();
        public readonly List<Color> extraTints = new List<Color>();
        public readonly List<bool> extraHead = new List<bool>();
        public readonly List<bool> extraFace = new List<bool>();
        public AlienCards.Set cards;
        public string headType, bodyType;
        public float hatTop = float.NaN;  // the top of the drawn hat in the head frame (NaN: none), for addons' hatLift

        public bool SameAs(AlienLook o)
        {
            if (o == null || o.pack != pack || o.mode != mode || o.build != build || o.scale != scale || o.headScale != headScale || o.headShift != headShift
                || o.ownHead != ownHead || o.ownUpper != ownUpper || o.ownLower != ownLower || o.faceShown != faceShown || o.face != face || o.extraModels.Count != extraModels.Count
                || o.handShift != handShift)
            {
                return false;
            }
            for (int i = 0; i < extraModels.Count; i++)
            {
                if (o.extraModels[i] != extraModels[i] || o.extraLocals[i] != extraLocals[i] || o.extraTints[i] != extraTints[i] || o.extraHead[i] != extraHead[i])
                {
                    return false;
                }
            }
            return true;
        }
    }

    internal static class AlienFigures
    {
        // Threads: ModeOf (through Patches.DrawnAsModel, Corpses.ShownAsModel) also runs on the game's parallel pre-render
        // workers. The mode cache is read without a lock; everything behind it (the Keep as paper snapshot, rules, packs,
        // live copies) is guarded by gate. The model caches (loaded, fitted, clipped) have their own lock, modelGate.
        private static readonly object gate = new object();
        private static readonly object modelGate = new object();
        private static readonly ConcurrentDictionary<ThingDef, FigureMode> modes = new ConcurrentDictionary<ThingDef, FigureMode>();
        private static Dictionary<string, AlienModelDef> packs;
        private static readonly List<Func<ThingDef, FigureMode?>> rules = new List<Func<ThingDef, FigureMode?>>();
        private static readonly Dictionary<string, Model> fitted = new Dictionary<string, Model>();
        private static readonly Dictionary<string, Model> loaded = new Dictionary<string, Model>();
        private static volatile List<string> seenList;
        private static volatile int seenCount;
        private static HashSet<string> paper = new HashSet<string>();   // the Keep as paper list as last seen (a copy)
        private static volatile bool refreshPending;                   // looks to work out again, done on the main thread

        // IsoRim's person figure (People / Poses / FigureWeapons constants), the defaults of the anchors.
        internal const float Neck = 0.74f;          // Poses.BodyTop
        internal const float HandY = 0.46f;         // People.HandY
        private const float HeadMiddle = 0.98f;     // People.HeadHeight
        private const float SpriteHeadHalf = 0.287f; // FigureBoxes: the head sprite's opaque half height
        private const float VanillaHeadY = 0.34f;   // a vanilla body type's headOffset.y

        // The race the look being worked out is dressed for (ApparelModels.For: race bindings), set around People.Dress
        // (per thread: only the thread working out that look sees it).
        [ThreadStatic] internal static ThingDef dressingAlien;
        [ThreadStatic] internal static BodyTypeDef dressingBody;

        // ---- the chokepoint

        internal static bool Drawn3D(Pawn pawn)
        {
            return pawn != null && pawn.def != null && ModeOf(pawn.def) != FigureMode.Card;
        }

        internal static FigureMode ModeOf(Pawn pawn)
        {
            if (pawn == null || pawn.def == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike || pawn.story == null)
            {
                return FigureMode.Card;
            }
            return ModeOf(pawn.def);
        }

        internal static FigureMode ModeOf(ThingDef def)
        {
            if (def.defName == "Human" || def.defName == "CreepJoiner")
            {
                return FigureMode.Human;   // people: unchanged, whatever packs or settings say
            }
            IsoRimSettings st = IsoRimMod.Settings;
            if (st != null && (st.keepPaper != seenList || (st.keepPaper != null && st.keepPaper.Count != seenCount)))
            {
                // The player changed the "Keep as paper" list (or reset the settings): every race asked again.
                lock (gate)
                {
                    if (st.keepPaper != seenList || (st.keepPaper != null && st.keepPaper.Count != seenCount))
                    {
                        ClearLocked();
                    }
                }
            }
            if (refreshPending && UnityData.IsInMainThread)
            {
                refreshPending = false;
                People.RefreshAll();
            }
            FigureMode m;
            if (modes.TryGetValue(def, out m))
            {
                return m;
            }
            lock (gate)
            {
                if (!modes.TryGetValue(def, out m))
                {
                    m = Work(def);
                    modes[def] = m;
                }
            }
            return m;
        }

        // Under gate: the Keep as paper list copied again, every race's mode dropped, every look to be worked out again
        // (People.RefreshAll, on the main thread: now if this is it, else at the next ModeOf there).
        private static void ClearLocked()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            List<string> list = st != null ? st.keepPaper : null;
            HashSet<string> copy = new HashSet<string>();
            int count = 0;
            if (list != null)
            {
                try
                {
                    count = list.Count;
                    copy.UnionWith(list.ToArray());
                }
                catch (Exception)
                {
                    count = -1;   // changed while copied (on the main thread): copied again at the next ModeOf
                }
            }
            paper = copy;
            seenList = list;
            seenCount = count;
            modes.Clear();
            refreshPending = true;
        }

        // Under gate.
        private static FigureMode Work(ThingDef def)
        {
            if (def.race == null || !def.race.Humanlike)
            {
                return FigureMode.Card;
            }
            if (paper.Contains(def.defName))
            {
                return FigureMode.Card;   // the player's "Keep as paper" list wins
            }
            for (int i = 0; i < rules.Count; i++)
            {
                try
                {
                    FigureMode? r = rules[i](def);
                    if (r.HasValue)
                    {
                        return r.Value;
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] A race figure rule failed: " + e, 93625000 ^ i);
                }
            }
            AlienModelDef pack = PackFor(def);
            return pack != null ? pack.figure : FigureMode.Card;
        }

        // The alien tuning window's live copies (dev mode, AlienTune.cs), by race defName; they stand in for the packs this game.
        private static readonly Dictionary<string, AlienModelDef> live = new Dictionary<string, AlienModelDef>();

        private static Game liveGame;

        internal static void SetLive(ThingDef race, AlienModelDef def)
        {
            lock (gate)
            {
                if (liveGame != Current.Game)
                {
                    live.Clear();
                    liveGame = Current.Game;
                }
                if (def == null)
                {
                    live.Remove(race.defName);
                }
                else
                {
                    live[race.defName] = def;
                }
            }
            Clear();
        }

        internal static AlienModelDef PackFor(ThingDef def)
        {
            lock (gate)
            {
                AlienModelDef tuned;
                if (live.Count > 0 && def != null && liveGame == Current.Game && live.TryGetValue(def.defName, out tuned))
                {
                    return tuned;
                }
                if (packs == null)
                {
                    packs = new Dictionary<string, AlienModelDef>();
                    foreach (AlienModelDef d in DefDatabase<AlienModelDef>.AllDefsListForReading)
                    {
                        foreach (string r in d.Aliens())
                        {
                            packs[r] = d;   // a later def for the same race wins (a pack overriding another)
                        }
                    }
                }
                AlienModelDef p;
                return def != null && packs.TryGetValue(def.defName, out p) ? p : null;
            }
        }

        // The settings list or the rules changed: every race asked again, every look worked out again.
        internal static void Clear()
        {
            lock (gate)
            {
                ClearLocked();
            }
            if (UnityData.IsInMainThread)
            {
                refreshPending = false;
                People.RefreshAll();
            }
        }

        internal static void AddRule(Func<ThingDef, FigureMode?> rule)
        {
            bool added = false;
            lock (gate)
            {
                if (rule != null && !rules.Contains(rule))
                {
                    rules.Add(rule);
                    added = true;
                }
            }
            if (added)
            {
                Clear();
            }
        }

        internal static void RemoveRule(Func<ThingDef, FigureMode?> rule)
        {
            bool removed;
            lock (gate)
            {
                removed = rules.Remove(rule);
            }
            if (removed)
            {
                Clear();
            }
        }

        // The humanlike races the player can keep as paper (settings): every loaded humanlike race but people.
        internal static List<ThingDef> OtherAliens()
        {
            List<ThingDef> list = new List<ThingDef>();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (d.race != null && d.race.Humanlike && d.defName != "Human" && d.defName != "CreepJoiner" && !d.IsCorpse)
                {
                    list.Add(d);
                }
            }
            return list;
        }

        // ---- size and bones (callers outside People)

        // A person figure's size as drawn (life stage, and the race's scale): Attach / Poses, where no look is at hand.
        internal static float SizeOf(Pawn pawn)
        {
            float size = pawn.ageTracker != null && pawn.ageTracker.CurLifeStage != null ? People.SizeFor(pawn.ageTracker.CurLifeStage.bodySizeFactor) : 1f;
            if (pawn.def == null || pawn.def.defName == "Human" || pawn.def.defName == "CreepJoiner")
            {
                return size;
            }
            People.Look look = People.LastLook(pawn);
            return look != null && look.alien != null ? look.size : size;
        }

        // Dried to bones: a 3D skeleton for people and Human-figure races; an Own race keeps its card (IsoRim has no
        // skeleton of its shape).
        internal static bool BonesAsModel(Pawn pawn)
        {
            if (pawn.def == null)
            {
                return false;
            }
            FigureMode m = ModeOf(pawn.def);
            return m == FigureMode.Human;
        }

        // ---- the look

        // Before People.Work dresses the figure: the race's build and placing; null for people (nothing changes).
        internal static AlienLook Prepare(Pawn pawn, ref string build)
        {
            dressingAlien = null;
            dressingBody = null;
            if (pawn.def == null || pawn.def.defName == "Human" || pawn.def.defName == "CreepJoiner")
            {
                return null;
            }
            FigureMode mode = ModeOf(pawn.def);
            if (mode == FigureMode.Card)
            {
                return null;
            }
            AlienModelDef pack = PackFor(pawn.def);
            AlienLook r = new AlienLook { race = pawn.def, pack = pack, mode = mode };
            Pawn_StoryTracker story = pawn.story;
            r.bodyType = story.bodyType != null ? story.bodyType.defName : null;
            r.headType = story.headType != null ? story.headType.defName : null;
            r.build = BuildFor(pack, story.bodyType);
            build = r.build;
            Measure(pawn, r);
            AlienBodyModel body = mode == FigureMode.Own ? BodyFor(pack, pawn) : null;
            if (body != null && !body.fitAs.NullOrEmpty())
            {
                r.build = body.fitAs.ToLowerInvariant();
                build = r.build;
            }
            AlienAnchors an = pack != null ? pack.anchors : null;
            if (mode == FigureMode.Own)
            {
                r.scale = pack != null && !float.IsNaN(pack.scale) ? pack.scale : 1f;
                r.headScale = 1f;
                r.headShift = Vector3.zero;
                r.headFit = an != null ? an.headFit : Vector3.one;
                r.bodyFit = an != null ? an.bodyFit : Vector3.one;
                Vector3 pivot = an != null && AlienAnchors.Set(an.head) ? an.head : new Vector3(0f, Neck, 0f);
                r.head = Matrix4x4.Translate(pivot) * Matrix4x4.Scale(r.headFit) * Matrix4x4.Translate(new Vector3(0f, -Neck, 0f));
                Vector3 hl = an != null && AlienAnchors.Set(an.handLeft) ? an.handLeft : new Vector3(0f, HandY, 0f);
                Vector3 hr = an != null && AlienAnchors.Set(an.handRight) ? an.handRight : new Vector3(0f, HandY, 0f);
                r.handShift = (hl.y + hr.y) * 0.5f - HandY;
            }
            else
            {
                r.scale = pack != null && !float.IsNaN(pack.scale) ? pack.scale : r.bodyDraw;
                r.headScale = pack != null && !float.IsNaN(pack.headScale) ? pack.headScale : r.headDraw / Mathf.Max(0.05f, r.bodyDraw);
                if (pack != null && !float.IsNaN(pack.headOffset.y))
                {
                    r.headShift = new Vector3(0f, pack.headOffset.y, float.IsNaN(pack.headOffset.z) ? 0f : pack.headOffset.z);
                }
                r.head = Matrix4x4.Translate(new Vector3(0f, Neck + r.headShift.y, r.headShift.z)) * Matrix4x4.Scale(Vector3.one * r.headScale)
                    * Matrix4x4.Translate(new Vector3(0f, -Neck, 0f));
            }
            r.headInv = r.head.inverse;
            r.clothesHidden = pack != null && pack.clothes == "Hide";
            r.faceShown = pack == null || pack.face == AlienFace.IsoRim;
            dressingAlien = pawn.def;
            dressingBody = story.bodyType;
            return r;
        }

        // The body scale of the figure (Look.bodyScale): the build's for a Human figure, bodyFit for an Own one.
        internal static Vector3 BodyScale(AlienLook r)
        {
            if (r.mode == FigureMode.Own)
            {
                return r.bodyFit;
            }
            switch (r.build)
            {
                case "female": return BodyScales.For(BodyTypeDefOf.Female);
                case "thin": return BodyScales.For(BodyTypeDefOf.Thin);
                case "fat": return BodyScales.For(BodyTypeDefOf.Fat);
                case "hulk": return BodyScales.For(BodyTypeDefOf.Hulk);
            }
            return Vector3.one;
        }

        // What People.Dress gets: all worn apparel, or with clothes Hide only what has a binding for this race.
        internal static List<Apparel> Worn(Pawn pawn, AlienLook r)
        {
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            if (r == null || !r.clothesHidden || worn == null)
            {
                return worn;
            }
            List<Apparel> kept = new List<Apparel>();
            for (int i = 0; i < worn.Count; i++)
            {
                if (ApparelModels.ForAlien(worn[i].def, pawn.def) != null)
                {
                    kept.Add(worn[i]);
                }
            }
            return kept;
        }

        // After People.Work: the race's own models, eyes, addon models and cards.
        internal static void Finish(Pawn pawn, People.Look look, AlienLook r)
        {
            dressingAlien = null;
            dressingBody = null;
            Pawn_StoryTracker story = pawn.story;
            if (story.headType != null && !Has(story.headType.defName, "Female") && !Has(story.headType.defName, "Male"))
            {
                // A race's own head type: the eyes by the head type's gender, else the person's.
                look.femaleEyes = story.headType.gender == Gender.Female || (story.headType.gender == Gender.None && pawn.gender == Gender.Female);
                if (!Has(story.headType.defName, "Narrow") && !Has(story.headType.defName, "Wide") && story.headType.narrow)
                {
                    look.headWide = 0.9f;
                }
            }
            AlienModelDef pack = r.pack;
            bool bones = look.skull != null;
            if (r.mode == FigureMode.Own && !bones)
            {
                AlienBodyModel body = BodyFor(pack, pawn);
                if (body != null)
                {
                    Matrix4x4 unfit = Matrix4x4.Scale(Inverse(r.bodyFit));
                    Model up = Fitted(pack, body.upper, unfit);
                    Model low = Fitted(pack, body.lower, unfit);
                    if (up != null && low != null)
                    {
                        r.ownUpper = up;
                        r.ownLower = low;
                        look.upper = up;
                        look.lower = low;
                    }
                }
                AlienHeadModel headModel = HeadFor(pack, story.headType);
                Model h = headModel != null ? Fitted(pack, headModel.model, r.headInv) : null;
                if (h != null && !look.headHidden)
                {
                    r.ownHead = h;
                    look.ownHead = h;   // replaces the person head and its face (drawn in the head frame)
                    r.face = FaceOnOwnHead(r) * Matrix4x4.Scale(new Vector3(look.headWide, 1f, 1f));
                    if (r.faceShown && !look.faceHidden && !look.faceDecal)
                    {
                        Model eyes = People.Part(look.femaleEyes && People.Part("person_face_female") != null ? "person_face_female" : "person_face");
                        AddExtra(r, eyes, r.face, Color.white, true, true);
                    }
                }
            }
            if (r.ownHead == null)
            {
                r.face = Matrix4x4.Scale(new Vector3(look.headWide, 1f, 1f));   // the person head's own place (fitted by headFit on an Own race)
            }
            if (pack != null && pack.hair != AlienHair.IsoRim)
            {
                look.hair = null;   // (Card: the game's hair and beard as cards, AlienCards)
                look.hairTop = null;
                look.beard = null;
            }
            if (!r.faceShown)
            {
                look.faceHidden = true;   // face None: no IsoRim face, no Facial Animation face
                look.faceDecal = false;
            }
            Model personHead = People.Part("person_head");
            r.hatTop = look.hat != null ? look.hat.maxY : float.NaN;
            Anchors(r, look);
            // Addon models (by the render node's texture path) and cards for the rest.
            r.cards = AlienCards.Collect(pawn, look, r);
            if (r.cards != null && pack != null && pack.addons != null)
            {
                foreach (AlienCards.Node n in r.cards.nodes)
                {
                    AlienAddonModel a = AddonFor(pack, n.path);
                    if (a == null)
                    {
                        continue;
                    }
                    n.hatLift = a.hatLift;
                    n.bound = a.hide || !a.model.NullOrEmpty();   // (an entry with neither only sets options: the card stays)
                    if (a.hide || a.model.NullOrEmpty() || !n.shown)
                    {
                        continue;
                    }
                    Model m = Load(pack, a.model);
                    if (m == null)
                    {
                        continue;
                    }
                    string an = a.anchor.NullOrEmpty() ? (n.onHead ? "head" : "tailroot") : Norm(a.anchor);
                    bool onHead = an == "head" || an == "face";
                    Vector3 at;
                    if (!r.anchors.TryGetValue(an, out at))
                    {
                        at = Vector3.zero;
                    }
                    // On the head the model keeps its own size (the head frame carries the fit of IsoRim's head parts).
                    Matrix4x4 local = onHead
                        ? r.headInv * Matrix4x4.Translate(r.head.MultiplyPoint3x4(at)) * Matrix4x4.TRS(a.offset, Quaternion.Euler(a.rotation), Vector3.one * a.scale)
                        : Matrix4x4.Translate(at) * Matrix4x4.TRS(a.offset, Quaternion.Euler(a.rotation), Vector3.one * a.scale);
                    if (onHead && a.hatLift > 0f && !float.IsNaN(r.hatTop))
                    {
                        // Out of the hat (hatLift): its lowest point lifted that share of the way up to the hat's top.
                        float low = float.MaxValue;
                        for (int i = 0; i < m.positions.Length; i++)
                        {
                            low = Mathf.Min(low, local.MultiplyPoint3x4(m.positions[i]).y);
                        }
                        local = Matrix4x4.Translate(new Vector3(0f, Mathf.Max(0f, r.hatTop - low) * a.hatLift, 0f)) * local;
                    }
                    Color tint = a.colour == "Skin" ? look.skin : (a.colour == "Hair" ? look.hairColor : (a.colour == "White" ? Color.white : n.colour));
                    AddExtra(r, m, local, tint, onHead);
                }
            }
        }

        private static void AddExtra(AlienLook r, Model m, Matrix4x4 local, Color tint, bool head, bool face = false)
        {
            if (m == null)
            {
                return;
            }
            r.extraModels.Add(m);
            r.extraLocals.Add(local);
            r.extraTints.Add(tint);
            r.extraHead.Add(head);
            r.extraFace.Add(face);
        }

        // IsoRim's face on an Own race's own head, in the head frame: the eyes' middle (FacePoint, IsoRim's person head space)
        // onto the face anchor (the race's figure space), scaled about it by faceFit (default headFit). Without either
        // field: fitted by headFit like the rest of the head parts (identity in the head frame).
        internal static readonly Vector3 FacePoint = new Vector3(0f, 0.95f, 0.22f);

        private static Matrix4x4 FaceOnOwnHead(AlienLook r)
        {
            AlienAnchors an = r.pack != null ? r.pack.anchors : null;
            bool anchor = an != null && AlienAnchors.Set(an.face);
            bool fit = an != null && AlienAnchors.Set(an.faceFit);
            if (!anchor && !fit)
            {
                return Matrix4x4.identity;
            }
            Vector3 at = anchor ? an.face : r.head.MultiplyPoint3x4(FacePoint);
            Vector3 size = fit ? an.faceFit : r.headFit;
            return r.headInv * Matrix4x4.Translate(at) * Matrix4x4.Scale(size) * Matrix4x4.Translate(-FacePoint);
        }

        // People.Merged, a race's head in a bed that hides the body (headOnly): faces lower than the chin are left out (hair hanging
        // down the back would otherwise lie on the blanket, apart from the head). The chin: the lowest point of the head drawn
        // (an Own race's head model, else IsoRim's person head), in the head frame.
        internal static float ChinClip(People.Look look)
        {
            Model h = look.ownHead ?? People.Part("person_head");
            if (h == null)
            {
                return float.NegativeInfinity;
            }
            float low = float.MaxValue;
            for (int i = 0; i < h.positions.Length; i++)
            {
                low = Mathf.Min(low, h.positions[i].y);
            }
            return low - 0.005f;
        }

        // People.Merged: the race's extra parts of the head or the body.
        // clip: in a bed that hides the body (People.Merged, ChinClip), the head's addon models are cut cleanly at that height (an
        // open plane cut in the head frame, not face by face: no ragged strips of hair beside the head); NegativeInfinity: whole.
        internal static void AddParts(People.Look look, bool headPart, int hide, List<Model> models, List<Matrix4x4> locals, List<Color> tints,
            float clip = float.NegativeInfinity)
        {
            AlienLook r = look.alien;
            if (r == null)
            {
                return;
            }
            for (int i = 0; i < r.extraModels.Count; i++)
            {
                if (r.extraHead[i] != headPart)
                {
                    continue;
                }
                if (headPart ? (hide & (int)(FigureParts.Head | FigureParts.Face)) != 0 && r.extraFace[i] : (hide & (int)FigureParts.Body) != 0)
                {
                    continue;
                }
                if (!float.IsNegativeInfinity(clip))
                {
                    Model cut = ClippedBelow(r.extraModels[i], r.extraLocals[i], clip);
                    if (cut != null)
                    {
                        models.Add(cut);
                        locals.Add(Matrix4x4.identity);
                        tints.Add(r.extraTints[i]);
                    }
                    continue;
                }
                models.Add(r.extraModels[i]);
                locals.Add(r.extraLocals[i]);
                tints.Add(r.extraTints[i]);
            }
        }

        private static readonly Dictionary<string, Model> clipped = new Dictionary<string, Model>();

        // A model placed by local, cut at height clip (both in the head frame), the part above kept; null when nothing is left. Made once.
        private static Model ClippedBelow(Model m, Matrix4x4 local, float clip)
        {
            lock (modelGate)
            {
                return ClippedBelowLocked(m, local, clip);
            }
        }

        private static Model ClippedBelowLocked(Model m, Matrix4x4 local, float clip)
        {
            string key = m.id + "|" + local.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) + "|"
                + clip.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            Model c;
            if (!clipped.TryGetValue(key, out c))
            {
                Matrix4x4 normals = local.inverse.transpose;
                Model placed = ObjLoader.Transformed(m, p => local.MultiplyPoint3x4(p), nn => normals.MultiplyVector(nn).normalized);
                // ObjLoader.ClipBelow keeps what is below a height: mirrored upside down and back, it keeps what is above. Open cut
                // (its lid would cover the model's whole box).
                Func<Vector3, Vector3> flip = p => new Vector3(p.x, -p.y, p.z);
                Model down = ObjLoader.ClipBelow(ObjLoader.Transformed(placed, flip, flip), -clip, false);
                c = down != null ? ObjLoader.Transformed(down, flip, flip) : null;
                clipped[key] = c;
            }
            return c;
        }

        // The named anchors (body frame; head and face in the head frame as IsoRim's own head is modelled).
        private static void Anchors(AlienLook r, People.Look look)
        {
            AlienAnchors an = r.pack != null ? r.pack.anchors : null;
            Vector3 bs = look.bodyScale;
            float half = look.upper != null ? look.upper.halfDepth * bs.z : 0.12f;
            float side = look.upper != null ? Mathf.Max(-look.upper.minX, look.upper.maxX) * bs.x : 0.2f;
            FigureBoxes.Box hb = FigureBoxes.Head(look);
            Put(r, "head", an != null ? an.head : Unset, new Vector3(0f, Neck, 0f), r.mode == FigureMode.Own);
            Put(r, "face", an != null ? an.face : Unset, new Vector3(0f, HeadMiddle, hb.centre.z + hb.size.z * 0.5f), r.mode == FigureMode.Own);
            Put(r, "neck", an != null ? an.neck : Unset, new Vector3(0f, Neck, 0f), false);
            Put(r, "back", an != null ? an.back : Unset, new Vector3(0f, Neck - 0.1f, -half), false);
            Put(r, "waist", an != null ? an.waist : Unset, new Vector3(0f, Attachments.Hips + 0.05f, 0f), false);
            Put(r, "handleft", an != null ? an.handLeft : Unset, new Vector3(-side, HandY, 0f), false);
            Put(r, "handright", an != null ? an.handRight : Unset, new Vector3(side, HandY, 0f), false);
            Put(r, "tailroot", an != null ? an.tailRoot : Unset, new Vector3(0f, Attachments.Hips + 0.1f, -half), false);
        }

        private static readonly Vector3 Unset = new Vector3(float.NaN, float.NaN, float.NaN);

        // A pack anchor is in the race figure's space; head and face of an Own race go into the head frame (headInv).
        private static void Put(AlienLook r, string name, Vector3 given, Vector3 fallback, bool headFrameFromAlien)
        {
            if (!AlienAnchors.Set(given))
            {
                r.anchors[name] = fallback;
                return;
            }
            r.anchors[name] = headFrameFromAlien ? r.headInv.MultiplyPoint3x4(given) : given;
        }

        // FigureFrame.Anchor: a named point (a head one in the head frame) for a person figure; false for unknown names.
        internal static bool Anchor(People.Look look, string name, out Vector3 point, out bool head)
        {
            string n = Norm(name);
            head = n == "head" || n == "face";
            if (look.alien != null)
            {
                return look.alien.anchors.TryGetValue(n, out point);
            }
            Vector3 bs = look.bodyScale;
            float half = look.upper != null ? look.upper.halfDepth * bs.z : 0.12f;
            float side = look.upper != null ? Mathf.Max(-look.upper.minX, look.upper.maxX) * bs.x : 0.2f;
            switch (n)
            {
                case "head": point = new Vector3(0f, Neck, 0f); return true;
                case "face":
                    FigureBoxes.Box hb = FigureBoxes.Head(look);
                    point = new Vector3(0f, HeadMiddle, hb.centre.z + hb.size.z * 0.5f);
                    return true;
                case "neck": point = new Vector3(0f, Neck, 0f); return true;
                case "back": point = new Vector3(0f, Neck - 0.1f, -half); return true;
                case "waist": point = new Vector3(0f, Attachments.Hips + 0.05f, 0f); return true;
                case "handleft": point = new Vector3(-side, HandY, 0f); return true;
                case "handright": point = new Vector3(side, HandY, 0f); return true;
                case "tailroot": point = new Vector3(0f, Attachments.Hips + 0.1f, -half); return true;
            }
            point = Vector3.zero;
            return false;
        }

        internal static string Norm(string name)
        {
            string n = (name ?? "").Replace("_", "").ToLowerInvariant();
            if (n == "handl")
            {
                return "handleft";
            }
            if (n == "handr")
            {
                return "handright";
            }
            return n;
        }

        // ---- measuring (Human figures)

        // The game's body and head mesh sizes against a human's of the same life stage (HAR's draw sizes, any mod that
        // resizes them), and its head offset against the body type's own: the head's shift in figure cells.
        private static void Measure(Pawn pawn, AlienLook r)
        {
            try
            {
                // The game sets a race's draw sizes up with its render tree (HAR copies them from the life stage then): set it up
                // first, as the game would before its first draw.
                PawnRenderTree tree = pawn.Drawer != null && pawn.Drawer.renderer != null ? pawn.Drawer.renderer.renderTree : null;
                if (tree != null && !tree.Resolved)
                {
                    tree.EnsureInitialized(PawnRenderFlags.Clothes | PawnRenderFlags.Headgear);
                }
                float bodyW = HumanlikeMeshPoolUtility.HumanlikeBodyWidthForPawn(pawn);
                float headW = HumanlikeMeshPoolUtility.HumanlikeHeadWidthForPawn(pawn);
                r.bodyDraw = MeshHeight(HumanlikeMeshPoolUtility.GetHumanlikeBodySetForPawn(pawn), bodyW);
                r.headDraw = MeshHeight(HumanlikeMeshPoolUtility.GetHumanlikeHeadSetForPawn(pawn), headW);
                PawnRenderer renderer = pawn.Drawer != null ? pawn.Drawer.renderer : null;
                BodyTypeDef type = pawn.story.bodyType;
                if (renderer == null || type == null || pawn.ageTracker == null || pawn.ageTracker.CurLifeStage == null)
                {
                    return;
                }
                Vector2 van = type.headOffset * Mathf.Sqrt(pawn.ageTracker.CurLifeStage.bodySizeFactor);
                Vector3 south = renderer.BaseHeadOffsetAt(Rot4.South);
                Vector3 east = renderer.BaseHeadOffsetAt(Rot4.East);
                float b = Mathf.Max(0.05f, r.bodyDraw);
                // In the game the body mesh shrinks about its middle while the head stays at its offset; the figure scales
                // about its feet with the neck on the body: the head's shift is what the game's head is off a scaled human's.
                float up = south.z - van.y * b - SpriteHeadHalf * (r.headDraw - b);
                float forward = east.x - van.x * b;
                float spriteToFigure = SpriteToFigure(r.build);
                r.headShift = new Vector3(0f, up / b * spriteToFigure, forward / b * spriteToFigure);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Measuring a race's figure failed: " + e, 93625001);
            }
        }

        private static float MeshHeight(GraphicMeshSet set, float human)
        {
            Mesh m = set != null ? set.MeshAt(Rot4.South) : null;
            if (m == null || human <= 0f)
            {
                return 1f;
            }
            Vector3[] v = m.vertices;
            if (v == null || v.Length == 0)
            {
                return Mathf.Max(0.05f, m.bounds.size.z / human);
            }
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < v.Length; i++)
            {
                lo = Mathf.Min(lo, v[i].z);
                hi = Mathf.Max(hi, v[i].z);
            }
            return Mathf.Max(0.05f, (hi - lo) / human);
        }

        // Sprite cells (the game's south body sprite) to figure cells, by IsoRim's build (FigureBoxes.BodySprite's "up").
        internal static float SpriteToFigure(string build)
        {
            float feet = build == "female" || build == "fat" ? -0.568f : (build == "hulk" ? -0.721f : -0.510f);
            float chin = VanillaHeadY - SpriteHeadHalf;
            return Neck / Mathf.Max(0.1f, chin - feet);
        }

        // ---- packs

        private static string BuildFor(AlienModelDef pack, BodyTypeDef type)
        {
            string name = type != null ? type.defName ?? "" : "";
            string b;
            if (pack != null && pack.build != null && pack.build.map.TryGetValue(name, out b))
            {
                return b;
            }
            if (type == BodyTypeDefOf.Male || type == BodyTypeDefOf.Female || type == BodyTypeDefOf.Thin || type == BodyTypeDefOf.Fat
                || type == BodyTypeDefOf.Hulk || name == "Child" || name == "Baby")
            {
                return People.BuildOf(type);
            }
            if (Has(name, "Female") || Has(name, "Thin") || Has(name, "Hulk") || Has(name, "Fat") || Has(name, "Child") || Has(name, "Baby"))
            {
                return People.BuildOf(type);
            }
            // The body's own art names its build (a custom body type drawn with a vanilla body texture).
            string path = type != null ? (type.bodyNakedGraphicPath ?? "") + " " + (type.bodyDessicatedGraphicPath ?? "") : "";
            if (Has(path, "Female"))
            {
                return "female";
            }
            if (Has(path, "Thin"))
            {
                return "thin";
            }
            if (Has(path, "Hulk"))
            {
                return "hulk";
            }
            if (Has(path, "Fat"))
            {
                return "fat";
            }
            return "male";
        }

        private static AlienBodyModel BodyFor(AlienModelDef pack, Pawn pawn)
        {
            if (pack == null || pack.bodies == null)
            {
                return null;
            }
            string type = pawn.story.bodyType != null ? pawn.story.bodyType.defName : "";
            string stage = pawn.ageTracker != null && pawn.ageTracker.CurLifeStage != null ? pawn.ageTracker.CurLifeStage.defName : "";
            foreach (AlienBodyModel b in pack.bodies)
            {
                if ((b.bodyType.NullOrEmpty() || b.bodyType == type) && (b.lifeStage.NullOrEmpty() || b.lifeStage == stage))
                {
                    return b;
                }
            }
            return null;
        }

        private static AlienHeadModel HeadFor(AlienModelDef pack, HeadTypeDef type)
        {
            if (pack == null || pack.heads == null)
            {
                return null;
            }
            string name = type != null ? type.defName : "";
            string path = type != null ? ModelLibrary.NormalPath(type.graphicPath) : null;
            foreach (AlienHeadModel h in pack.heads)
            {
                if (!h.headType.NullOrEmpty() && h.headType == name)
                {
                    return h;
                }
                if (!h.graphicPath.NullOrEmpty() && PathMatches(h.graphicPath, path))
                {
                    return h;
                }
                if (h.headType.NullOrEmpty() && h.graphicPath.NullOrEmpty())
                {
                    return h;
                }
            }
            return null;
        }

        internal static AlienAddonModel AddonFor(AlienModelDef pack, string path)
        {
            if (pack == null || pack.addons == null || path == null)
            {
                return null;
            }
            foreach (AlienAddonModel a in pack.addons)
            {
                if (PathMatches(a.graphicPath, path))
                {
                    return a;
                }
            }
            return null;
        }

        // An entry's path against a node's (already normal): the same, or under it when the entry ends in /.
        private static bool PathMatches(string entry, string normalPath)
        {
            if (entry.NullOrEmpty() || normalPath == null)
            {
                return false;
            }
            string e = entry.Replace('\\', '/').ToLowerInvariant();
            bool prefix = e.EndsWith("/");
            e = e.TrimEnd('/');
            return prefix ? normalPath.StartsWith(e + "/") || normalPath == e : normalPath == e;
        }

        // A pack's model file (relative to its mod: modPackageId, its own mod, or the first active mod with the file).
        internal static Model Load(AlienModelDef pack, string path)
        {
            lock (modelGate)
            {
                return LoadLocked(pack, path);
            }
        }

        private static Model LoadLocked(AlienModelDef pack, string path)
        {
            if (pack == null || path.NullOrEmpty())
            {
                return null;
            }
            string key = pack.defName + "|" + path;
            Model m;
            if (loaded.TryGetValue(key, out m))
            {
                return m;
            }
            string root = RootFor(pack, path);
            m = root != null ? ModelLibrary.LoadFile(Path.GetFullPath(Path.Combine(root, path))) : null;
            if (m == null)
            {
                Log.ErrorOnce("[IsoRim] Alien pack " + pack.defName + ": " + path + " not found.", pack.shortHash ^ 93625002 ^ path.GetHashCode());
            }
            loaded[key] = m;
            return m;
        }

        // A pack model moved into the frame IsoRim draws it in (bodyFit / head fit taken back out), made once.
        private static Model Fitted(AlienModelDef pack, string path, Matrix4x4 local)
        {
            lock (modelGate)
            {
                return FittedLocked(pack, path, local);
            }
        }

        private static Model FittedLocked(AlienModelDef pack, string path, Matrix4x4 local)
        {
            Model raw = Load(pack, path);
            if (raw == null)
            {
                return null;
            }
            if (local == Matrix4x4.identity)
            {
                return raw;
            }
            string key = raw.id + "|" + local.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
            Model m;
            if (!fitted.TryGetValue(key, out m))
            {
                Matrix4x4 normals = local.inverse.transpose;
                m = ObjLoader.Transformed(raw, p => local.MultiplyPoint3x4(p), n => normals.MultiplyVector(n).normalized);
                fitted[key] = m;
            }
            return m;
        }

        private static string RootFor(AlienModelDef pack, string path)
        {
            if (!pack.modPackageId.NullOrEmpty())
            {
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                {
                    if (string.Equals(mod.PackageIdPlayerFacing, pack.modPackageId, StringComparison.OrdinalIgnoreCase) || string.Equals(mod.PackageId, pack.modPackageId, StringComparison.OrdinalIgnoreCase))
                    {
                        return mod.RootDir;
                    }
                }
            }
            if (pack.modContentPack != null)
            {
                return pack.modContentPack.RootDir;
            }
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                if (File.Exists(Path.Combine(mod.RootDir, path)))
                {
                    return mod.RootDir;
                }
            }
            return null;
        }

        private static Vector3 Inverse(Vector3 v)
        {
            return new Vector3(1f / Mathf.Max(0.01f, v.x), 1f / Mathf.Max(0.01f, v.y), 1f / Mathf.Max(0.01f, v.z));
        }

        private static bool Has(string text, string word)
        {
            return text != null && text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // For People.Report (harness): the race's mode and placing.
        internal static string Report(People.Look look)
        {
            AlienLook r = look != null ? look.alien : null;
            if (r == null)
            {
                return "";
            }
            int cards = 0, bound = 0;
            if (r.cards != null)
            {
                foreach (AlienCards.Node n in r.cards.nodes)
                {
                    if (n.bound)
                    {
                        bound++;
                    }
                    else if (n.shown)
                    {
                        cards++;
                    }
                }
            }
            return ", alienMode " + r.mode + ", pack " + (r.pack != null ? r.pack.defName : "-") + ", build " + r.build + ", bodyDraw " + r.bodyDraw.ToString("0.000")
                + ", headDraw " + r.headDraw.ToString("0.000") + ", scale " + r.scale.ToString("0.000") + ", headScale " + r.headScale.ToString("0.000")
                + ", headShift " + r.headShift.ToString("F3") + ", handShift " + r.handShift.ToString("0.000") + ", size " + look.size.ToString("0.000")
                + ", ownHead " + (r.ownHead != null) + ", ownBody " + (r.ownUpper != null) + ", extras " + r.extraModels.Count + ", addon nodes "
                + (r.cards != null ? r.cards.nodes.Count : 0) + " (cards " + cards + ", bound " + bound + "), clothesHidden " + r.clothesHidden + ", face " + (r.faceShown ? "IsoRim" : "None") + " at " + r.face.MultiplyPoint3x4(AlienFigures.FacePoint).ToString("F3") + ", hatTop " + r.hatTop.ToString("0.000") + AlienCards.Report(r.cards);
        }
    }

    public static partial class Api
    {
        // ---- humanlike races (Version 25)

        /// <summary>
        /// How IsoRim draws this pawn now (Api version 25): <see cref="FigureMode.Card"/> for every pawn that is not a
        /// humanlike drawn as a 3D figure (animals, mechs, races without an alien pack or kept as paper by the player),
        /// <see cref="FigureMode.Human"/> for people and races drawn with IsoRim's human figure, <see cref="FigureMode.Own"/>
        /// for races drawn with their pack's own models. It says what the pawn would be drawn as with the 3D view and
        /// person figures on.
        /// </summary>
        public static FigureMode FigureModeOf(Pawn pawn) => AlienFigures.ModeOf(pawn);

        /// <summary>
        /// Adds a rule asked for each humanlike race but people (Api version 25): return a mode to force it, or null to
        /// leave the race to the other rules and its alien pack. The player's "Keep as paper" list still wins. Asked once per
        /// race (the answer is kept until a rule is added or removed). Use it from a framework or pack with code; XML packs
        /// use <see cref="AlienModelDef"/>.
        /// </summary>
        public static void AddAlienFigureRule(Func<ThingDef, FigureMode?> rule) => AlienFigures.AddRule(rule);

        /// <summary>Removes a rule added with <see cref="AddAlienFigureRule"/>.</summary>
        public static void RemoveAlienFigureRule(Func<ThingDef, FigureMode?> rule) => AlienFigures.RemoveRule(rule);
    }
}
