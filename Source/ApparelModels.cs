using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>
    /// A 3D model for a piece of apparel on IsoRim's person figures (Api version 9). Any mod can ship these in its Defs; an
    /// apparel with a binding is drawn with it, everything else keeps IsoRim's own look. See Docs/Api.md.
    /// <code>
    ///   &lt;IsoRim.ApparelModelDef&gt;
    ///     &lt;defName&gt;R3DApparel_MyHat&lt;/defName&gt;
    ///     &lt;apparel&gt;MyMod_Apparel_Hat&lt;/apparel&gt;     the apparel ThingDef (skipped quietly when not loaded)
    ///     &lt;slot&gt;Head&lt;/slot&gt;                       Head, Face, Eyes, Neck, Body, Belt or Shield
    ///     &lt;model&gt;Models/my_hat.obj&lt;/model&gt;       relative to the mod's folder
    ///   &lt;/IsoRim.ApparelModelDef&gt;
    /// </code>
    /// </summary>
    public class ApparelModelDef : Def
    {
        /// <summary>The apparel ThingDef's defName.</summary>
        public string apparel;

        /// <summary>Head (headgear, in person_head's frame), Face (a mouth or face item worn with a hat), Eyes, Neck, Body
        /// (clothing in person_&lt;build&gt;_upper's frame), Belt (in the person_belt frame), Shield (grip at the origin, face
        /// toward -z of the file).</summary>
        public string slot = "Head";

        /// <summary>The OBJ file, relative to the mod's folder (Models/...). For Body, the model for builds without one of
        /// their own in bodyModels.</summary>
        public string model;

        /// <summary>Body: a model per body type (male, female, thin, fat, hulk), each made on that build's person_*_upper.</summary>
        public ApparelBodyModels bodyModels;

        /// <summary>Head: also hide these while the item is drawn (Hair, Beard, Face), on top of what the game's own rules
        /// hide (renderSkipFlags, FullHead / UpperHead coverage).</summary>
        public List<string> hides;

        /// <summary>Head: IsoRim's dark visor strip over the eyes, as on its own sealed helmets.</summary>
        public bool visor;

        /// <summary>Body on the outer (Shell) layer: how a flak vest worn under it shows: Hidden, FrontPlate (in an open
        /// front) or Whole.</summary>
        public string under = "Hidden";

        /// <summary>Belt: belt (round the waist, stretched to the outermost waist), hip or pack, as IsoRim.WornGearDef.</summary>
        public string mount = "belt";

        /// <summary>A shift, turn (Euler degrees x, y, z) and size applied to the model in its own space first (for
        /// Shield: about the grip, before IsoRim's held-on-the-left-forearm placing).</summary>
        public Vector3 offset = Vector3.zero;

        public Vector3 rotation = Vector3.zero;

        public float scale = 1f;

        /// <summary>Shield: shown whenever the person stands; by default only while the game shows the person's weapon
        /// (drafted, aiming, a job or duty that shows weapons) and, for apparel that draws its own worn picture (VE
        /// Framework's shields), while the game draws it.</summary>
        public bool alwaysShown;

        /// <summary>Faces under a material whose name starts with "stuff" take the item's colour (its stuff, its dye, its
        /// colour). spriteColour: the mean colour of its worn sprite instead (for items whose colour is painted into it).</summary>
        public bool spriteColour;

        /// <summary>Api 25: only for these races (ThingDef defNames). For a race, a binding naming it wins over one without races;
        /// a binding with races is not used for anyone else.</summary>
        public List<string> aliens;

        /// <summary>Api 25, Body: a model per race body type (BodyTypeDef defName), used before bodyModels.</summary>
        public List<ApparelAlienBodyModel> alienBodyModels;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (apparel.NullOrEmpty())
            {
                yield return "no apparel";
            }
            if (model.NullOrEmpty() && (bodyModels == null || !bodyModels.Any) && alienBodyModels.NullOrEmpty())
            {
                yield return "no model";
            }
            if (ApparelModels.SlotOf(slot) == ApparelModels.Slot.None)
            {
                yield return "unknown slot " + slot + " (Head, Face, Eyes, Neck, Body, Belt, Shield)";
            }
            if (under != "Hidden" && under != "FrontPlate" && under != "Whole")
            {
                yield return "unknown under " + under + " (Hidden, FrontPlate, Whole)";
            }
            if (mount != "belt" && mount != "hip" && mount != "pack")
            {
                yield return "unknown mount " + mount + " (belt, hip, pack)";
            }
            if (hides != null)
            {
                foreach (string h in hides)
                {
                    if (h != "Hair" && h != "Beard" && h != "Face")
                    {
                        yield return "unknown hides entry " + h + " (Hair, Beard, Face)";
                    }
                }
            }
        }
    }

    /// <summary>A Body binding's model for one race body type (Api 25, ApparelModelDef.alienBodyModels).</summary>
    public class ApparelAlienBodyModel
    {
        public string bodyType;
        public string model;
    }

    /// <summary>Per body type models of a Body binding (ApparelModelDef.bodyModels).</summary>
    public class ApparelBodyModels
    {
        public string male;
        public string female;
        public string thin;
        public string fat;
        public string hulk;

        internal bool Any => !male.NullOrEmpty() || !female.NullOrEmpty() || !thin.NullOrEmpty() || !fat.NullOrEmpty() || !hulk.NullOrEmpty();

        internal string For(string build)
        {
            switch (build)
            {
                case "male": return male;
                case "female": return female;
                case "thin": return thin;
                case "fat": return fat;
                case "hulk": return hulk;
            }
            return null;
        }
    }

    // The apparel bindings (ApparelModelDef), worked out once: read by People.Dress when a look is worked out, the held
    // shield drawn by People.Draw. With no bindings loaded nothing here runs per frame.
    internal static class ApparelModels
    {
        internal enum Slot { None, Head, Face, Eyes, Neck, Body, Belt, Shield }

        internal const int HideHair = 1, HideBeard = 2, HideFace = 4;

        // One flame of a model (its flame* faces, one per separate piece): foot (bottom middle), height and width, model space.
        internal struct Flame
        {
            public Vector3 foot;
            public float height;
            public float width;
            public bool dark;
        }

        internal sealed class Binding
        {
            public ApparelModelDef def;
            public Slot slot;
            public int hides;
            public People.Under under;
            public WornGearDef gear;        // Belt: the mount, as a worn gear def
            public bool drawsOwn;           // its class draws its own worn picture (DrawWornExtras overridden)
            public bool broken;
            private string root;
            private readonly Dictionary<string, Model> byBuild = new Dictionary<string, Model>();

            // The model for this build (Body: its own if it has one), flames taken out; null if it could not be loaded.
            public Model ModelFor(string build)
            {
                if (broken)
                {
                    return null;
                }
                string path = slot == Slot.Body ? AlienBodyPath(def) : null;
                if (path.NullOrEmpty())
                {
                    path = slot == Slot.Body && def.bodyModels != null ? def.bodyModels.For(build) : null;
                }
                if (path.NullOrEmpty())
                {
                    path = def.model;
                }
                if (path.NullOrEmpty())
                {
                    return null;
                }
                Model m;
                if (byBuild.TryGetValue(path, out m))
                {
                    return m;
                }
                m = Load(path);
                byBuild[path] = m;
                return m;
            }

            private Model Load(string path)
            {
                if (root == null)
                {
                    root = RootFor(def, path) ?? "";
                }
                if (root.Length == 0)
                {
                    Log.ErrorOnce("[IsoRim] Apparel model " + def.defName + ": " + path + " not found in its mod's folder.", def.shortHash ^ 93619000);
                    return null;
                }
                Model raw = ModelLibrary.LoadFile(Path.GetFullPath(Path.Combine(root, path)));
                if (raw == null)
                {
                    return null;
                }
                if (def.offset != Vector3.zero || def.rotation != Vector3.zero || def.scale != 1f)
                {
                    Matrix4x4 local = Matrix4x4.TRS(def.offset, Quaternion.Euler(def.rotation), Vector3.one * def.scale);
                    raw = ObjLoader.Transformed(raw, p => local.MultiplyPoint3x4(p), n => local.MultiplyVector(n).normalized);
                }
                return WithLiveFlames(raw);
            }
        }

        private static Dictionary<string, Binding> byApparel;
        private static Dictionary<string, Binding> byAlien;   // "apparel|race" (Api 25)
        private static readonly Dictionary<int, Flame[]> flames = new Dictionary<int, Flame[]>();   // by Model.id
        internal static int Count;

        internal static Slot SlotOf(string slot)
        {
            switch (slot)
            {
                case "Head": return Slot.Head;
                case "Face": return Slot.Face;
                case "Eyes": return Slot.Eyes;
                case "Neck": return Slot.Neck;
                case "Body": return Slot.Body;
                case "Belt": return Slot.Belt;
                case "Shield": return Slot.Shield;
            }
            return Slot.None;
        }

        private static void Build()
        {
            if (byApparel != null)
            {
                return;
            }
            byApparel = new Dictionary<string, Binding>();
            byAlien = new Dictionary<string, Binding>();
            foreach (ApparelModelDef d in DefDatabase<ApparelModelDef>.AllDefsListForReading)
            {
                Slot slot = SlotOf(d.slot);
                ThingDef thing = d.apparel.NullOrEmpty() ? null : DefDatabase<ThingDef>.GetNamedSilentFail(d.apparel);
                if (slot == Slot.None || thing == null || thing.apparel == null)
                {
                    continue;   // its apparel is not loaded (a mod that is not active): skipped quietly
                }
                Binding b = new Binding { def = d, slot = slot };
                if (d.hides != null)
                {
                    b.hides = (d.hides.Contains("Hair") ? HideHair : 0) | (d.hides.Contains("Beard") ? HideBeard : 0) | (d.hides.Contains("Face") ? HideFace : 0);
                }
                b.under = d.under == "FrontPlate" ? People.Under.FrontPlate : (d.under == "Whole" ? People.Under.Whole : People.Under.Hidden);
                if (slot == Slot.Belt)
                {
                    b.gear = new WornGearDef { defName = d.defName, apparel = d.apparel, model = d.model, mount = d.mount };
                }
                Type cls = thing.thingClass ?? typeof(Apparel);
                System.Reflection.MethodInfo extras = AccessTools.Method(cls, nameof(Apparel.DrawWornExtras));
                b.drawsOwn = extras != null && extras.DeclaringType != typeof(Apparel) && extras.DeclaringType != typeof(ThingWithComps) && extras.DeclaringType != typeof(Thing);
                if (!d.aliens.NullOrEmpty())
                {
                    foreach (string race in d.aliens)
                    {
                        byAlien[thing.defName + "|" + race] = b;   // (Api 25: only for that race)
                    }
                    continue;
                }
                byApparel[thing.defName] = b;   // a later def for the same apparel wins (a pack overriding another)
            }
            Count = byApparel.Count + byAlien.Count;
        }

        // The binding of this apparel, or null (then IsoRim's own look).
        internal static Binding For(ThingDef apparel)
        {
            if (byApparel == null)
            {
                Build();
            }
            Binding b;
            if (AlienFigures.dressingAlien != null && Count > 0 && apparel != null && byAlien.TryGetValue(apparel.defName + "|" + AlienFigures.dressingAlien.defName, out b))
            {
                return b;   // a binding for the race the look is dressed for (Api 25)
            }
            return Count > 0 && apparel != null && byApparel.TryGetValue(apparel.defName, out b) ? b : null;
        }

        // A binding of this apparel made for this race (Api 25), or null.
        internal static Binding ForAlien(ThingDef apparel, ThingDef race)
        {
            if (byApparel == null)
            {
                Build();
            }
            Binding b;
            return Count > 0 && apparel != null && race != null && byAlien.TryGetValue(apparel.defName + "|" + race.defName, out b) ? b : null;
        }

        // A race binding's model for the race body type the look is dressed for (Api 25), or null.
        private static string AlienBodyPath(ApparelModelDef def)
        {
            BodyTypeDef body = AlienFigures.dressingBody;
            if (def.alienBodyModels == null || body == null || AlienFigures.dressingAlien == null)
            {
                return null;
            }
            foreach (ApparelAlienBodyModel m in def.alienBodyModels)
            {
                if (m.bodyType == body.defName)
                {
                    return m.model;
                }
            }
            return null;
        }

        // The colour the item's "stuff" faces take.
        internal static Color Tint(Binding b, Apparel a)
        {
            return b.def.spriteColour ? SpriteColors.MeanFor(a) : a.DrawColor;
        }

        // The folder an apparel model's path is relative to: its mod's; for a def added by a patch (it has no mod), the
        // first active mod that has the file.
        private static string RootFor(ApparelModelDef def, string path)
        {
            if (def.modContentPack != null)
            {
                return def.modContentPack.RootDir;
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

        // ---- live flames

        // The model without its flame* faces (IsoRim's live flame burns there instead), with its flames noted; the model
        // itself if it has none.
        private static Model WithLiveFlames(Model m)
        {
            if (m.materials == null || m.material == null)
            {
                return m;
            }
            int n = m.positions.Length;
            bool any = false;
            for (int i = 0; i < n && !any; i++)
            {
                any = Effects.IsFlame(m.materials[m.material[i]]);
            }
            if (!any)
            {
                return m;
            }
            // Flame faces joined by shared corners make one flame.
            List<int> tris = new List<int>();
            for (int i = 0; i + 2 < n; i += 3)
            {
                if (Effects.IsFlame(m.materials[m.material[i]]))
                {
                    tris.Add(i);
                }
            }
            int[] group = new int[tris.Count];
            for (int t = 0; t < group.Length; t++)
            {
                group[t] = t;
            }
            Func<int, int> find = null;
            find = x => group[x] == x ? x : (group[x] = find(group[x]));
            for (int a = 0; a < tris.Count; a++)
            {
                for (int b = a + 1; b < tris.Count; b++)
                {
                    if (Touch(m, tris[a], tris[b]))
                    {
                        group[find(a)] = find(b);
                    }
                }
            }
            Dictionary<int, Bounds> boxes = new Dictionary<int, Bounds>();
            Dictionary<int, bool> darks = new Dictionary<int, bool>();
            for (int t = 0; t < tris.Count; t++)
            {
                int g = find(t);
                for (int v = tris[t]; v < tris[t] + 3; v++)
                {
                    Bounds bx;
                    if (boxes.TryGetValue(g, out bx))
                    {
                        bx.Encapsulate(m.positions[v]);
                    }
                    else
                    {
                        bx = new Bounds(m.positions[v], Vector3.zero);
                    }
                    boxes[g] = bx;
                }
                darks[g] = (darks.TryGetValue(g, out bool d) && d) || m.materials[m.material[tris[t]]].name.IndexOf("_dark", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            List<Flame> list = new List<Flame>();
            foreach (KeyValuePair<int, Bounds> p in boxes)
            {
                Bounds b = p.Value;
                list.Add(new Flame { foot = new Vector3(b.center.x, b.min.y, b.center.z), height = b.size.y, width = Mathf.Max(b.size.x, b.size.z), dark = darks[p.Key] });
            }
            Model without = ObjLoader.Without(m, mm => Effects.IsFlame(mm));
            if (without == null)
            {
                return m;
            }
            flames[without.id] = list.ToArray();
            return without;
        }

        private static bool Touch(Model m, int a, int b)
        {
            for (int i = a; i < a + 3; i++)
            {
                for (int k = b; k < b + 3; k++)
                {
                    if ((m.positions[i] - m.positions[k]).sqrMagnitude < 1e-8f)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // A model made from another in code (People.FitOver) burns where that one does, its flames moved as its faces were.
        internal static void MoveFlames(Model from, Model to, Func<Vector3, Vector3> move)
        {
            Flame[] f = FlamesOf(from);
            if (f == null)
            {
                return;
            }
            Flame[] moved = (Flame[])f.Clone();
            for (int i = 0; i < moved.Length; i++)
            {
                moved[i].foot = move(moved[i].foot);
            }
            flames[to.id] = moved;
        }

        // The flames of a bound model (null: none).
        internal static Flame[] FlamesOf(Model m)
        {
            Flame[] f;
            return m != null && flames.Count > 0 && flames.TryGetValue(m.id, out f) ? f : null;
        }

        // Whether any part of the look burns (People.Dress).
        internal static bool Burns(People.Look look)
        {
            if (flames.Count == 0)
            {
                return false;
            }
            if (FlamesOf(look.hat) != null || FlamesOf(look.coat) != null || FlamesOf(look.mid) != null || FlamesOf(look.shirtWear) != null
                || FlamesOf(look.mouthWear) != null || FlamesOf(look.eyeWear) != null || FlamesOf(look.neckWear) != null || FlamesOf(look.overCoat) != null)
            {
                return true;
            }
            for (int i = 0; i < look.gear.Count; i++)
            {
                if (FlamesOf(look.gear[i]) != null)
                {
                    return true;
                }
            }
            return false;
        }

        // The live flames on a figure (People.Draw, while look.flames): at, yaw and the head and body placings as the figure
        // is drawn.
        internal static void DrawFlames(People.Look look, int id, Vector3 at, float yaw, Matrix4x4 headPlace, Matrix4x4 bodyPlace, bool body,
            FigureParts hidden = FigureParts.None)
        {
            Matrix4x4 root = Matrix4x4.Translate(at) * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up));
            Matrix4x4 head = root * headPlace * Matrix4x4.Scale(new Vector3(look.headWide, 1f, 1f));
            bool clothes = (hidden & FigureParts.Clothes) == 0;   // (parts hidden: Api 21)
            Burn((hidden & FigureParts.Headgear) == 0 ? FlamesOf(look.hat) : null, head, id);
            Burn((hidden & FigureParts.MouthWear) == 0 ? FlamesOf(look.mouthWear) : null, head, id + 1);
            Burn((hidden & FigureParts.EyeWear) == 0 ? FlamesOf(look.eyeWear) : null, head, id + 2);
            if (!body)
            {
                return;
            }
            Matrix4x4 b = root * bodyPlace * Matrix4x4.Scale(look.bodyScale);
            if (clothes)
            {
                Burn(FlamesOf(look.neckWear), b, id + 3);
                Burn(FlamesOf(look.coat), b, id + 4);
                Burn(FlamesOf(look.mid), b, id + 5);
                Burn(FlamesOf(look.shirtWear), b, id + 6);
            }
            for (int i = 0; (hidden & FigureParts.Gear) == 0 && i < look.gear.Count; i++)
            {
                Flame[] f = FlamesOf(look.gear[i]);
                if (f != null)
                {
                    Burn(f, root * bodyPlace * look.GearOnBody(i), id + 7 + i);
                }
            }
            Burn(clothes ? FlamesOf(look.overCoat) : null, b, id + 7 + look.gear.Count);
        }

        private static void Burn(Flame[] f, Matrix4x4 place, int id)
        {
            if (f == null)
            {
                return;
            }
            float up = ((Vector3)place.GetColumn(1)).magnitude;
            float across = Mathf.Max(((Vector3)place.GetColumn(0)).magnitude, ((Vector3)place.GetColumn(2)).magnitude);
            for (int i = 0; i < f.Length; i++)
            {
                Effects.FigureFlame(place.MultiplyPoint3x4(f[i].foot), f[i].height * up, f[i].width * across, f[i].dark, (id + i) * 0.37f);
            }
        }

        // ---- held shields

        // Set by the patched DrawWornExtras of a shield binding's apparel while FigureWeapons captures the game's drawing:
        // what it draws is dropped (the model stands in) and noted as drawn.
        internal static bool Swallowing;
        internal static bool GameDrew;

        // Hooks the worn-picture drawing of every bound shield's class (Patches.ApplyAll). Nothing when no shield is bound.
        internal static void Patch(Harmony h)
        {
            Build();
            HashSet<System.Reflection.MethodInfo> done = new HashSet<System.Reflection.MethodInfo>();
            foreach (Binding b in byApparel.Values)
            {
                if (b.slot != Slot.Shield || !b.drawsOwn)
                {
                    continue;
                }
                ThingDef thing = DefDatabase<ThingDef>.GetNamedSilentFail(b.def.apparel);
                System.Reflection.MethodInfo m = AccessTools.Method(thing.thingClass, nameof(Apparel.DrawWornExtras));
                if (m != null && done.Add(m))
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(ApparelModels), nameof(DrawWornExtras_Prefix)),
                        finalizer: new HarmonyMethod(typeof(ApparelModels), nameof(DrawWornExtras_Finalizer)));
                }
            }
        }

        public static void DrawWornExtras_Prefix(Apparel __instance, out bool __state)
        {
            __state = false;
            if (FigureWeapons.Capturing && !Swallowing && __instance != null)
            {
                Binding b = For(__instance.def);
                if (b != null && b.slot == Slot.Shield && b.ModelFor("male") != null)
                {
                    Swallowing = true;
                    __state = true;
                }
            }
        }

        public static Exception DrawWornExtras_Finalizer(Exception __exception, bool __state)
        {
            if (__state)
            {
                Swallowing = false;
            }
            return __exception;
        }

        // Where the grip of a held shield is on the figure (figure space, size 1), how far it is turned outward from the
        // front, and how its face leans back: on the left forearm (the figure's left is -x), a little in front of the
        // body's side, at the hands' height, its face toward the front and outward, as VE Framework's shield sprites show it.
        private const float GripY = 0.44f;
        private const float GripOut = 0.035f;     // past the body's side
        private const float GripForward = 0.05f;  // in front of the body's middle
        private const float FaceOut = 35f;        // degrees from the front toward the left
        private const float Lean = 6f;            // its top back a little

        // Whether the held shield shows now (People.Draw, standing only): always (alwaysShown), else while the game shows
        // the person's weapon and, for apparel that draws its own worn picture, while the game drew it (GameDrew, caught
        // by FigureWeapons.Draw just before).
        internal static bool ShieldShown(Pawn pawn, People.Look look)
        {
            Binding b = look.shieldBinding;
            if (b == null || look.shield == null)
            {
                return false;
            }
            if (b.def.alwaysShown)
            {
                return true;
            }
            if (b.drawsOwn && !GameDrew)
            {
                return false;
            }
            return WeaponShown(pawn);
        }

        // As the game shows a pawn's weapon (PawnRenderUtility.DrawEquipmentAndApparelExtras): aiming, or carried openly.
        private static bool WeaponShown(Pawn pawn)
        {
            if (pawn.CurJob != null && pawn.CurJob.def.neverShowWeapon)
            {
                return false;
            }
            Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
            if (busy != null && !busy.neverAimWeapon && busy.focusTarg.IsValid)
            {
                return true;
            }
            return PawnRenderUtility.CarryWeaponOpenly(pawn);
        }

        // Draws the held shield. at, yaw: the figure's place and turn; hands: its pose in figure space (People.Draw, with the
        // seat's squash); size its size.
        internal static void DrawShield(People.Look look, Vector3 at, float yaw, Matrix4x4 hands)
        {
            float side = Side(look.upper) * look.bodyScale.x;
            side = Mathf.Max(side, Side(look.coat) * look.bodyScale.x);
            side = Mathf.Max(side, Side(look.mid) * look.bodyScale.x);
            side = Mathf.Max(side, Side(look.overCoat) * look.bodyScale.x);
            Matrix4x4 mount = Matrix4x4.Translate(new Vector3(-(side + GripOut), GripY, GripForward))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(-FaceOut, Vector3.up) * Quaternion.AngleAxis(-Lean, Vector3.right));
            DynamicModels.DrawLocal(look.shield, at, yaw, hands * mount, look.shieldColor, null);
        }

        private static float Side(Model m)
        {
            return m == null ? 0f : Mathf.Max(-m.minX, m.maxX);
        }

        // For the harness: what a person's bound items are now.
        internal static string Report(People.Look look)
        {
            return "bindings " + Count + ", hat " + (look.hat != null ? look.hat.id.ToString() : "-") + ", shield " + (look.shield != null ? look.shieldBinding.def.defName : "-")
                + ", gear " + look.gear.Count + ", flames " + look.flames;
        }
    }
}
