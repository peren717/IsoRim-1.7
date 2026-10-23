using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Outfit stands (Building_OutfitStand, Building_KidOutfitStand) drawn with their model.
    //
    // The game draws a stand every frame by itself (it never calls base.DrawAt): a flat base, body
    // and head, the apparel it holds over the body and head, and the weapon it holds. With the model,
    // the stand and its body are baked like any realtime building (SectionLayer_IsoRimBodies); here
    // go the rest, so that a stand looks like a 3D colonist wearing the same outfit, minus the colonist:
    //   - the figure is a colonist of the stand's body type and size (People's figure space, as is),
    //     lifted dressRise onto the stand, so heads, hats and clothes have a colonist's sizes and places;
    //   - the stand's head model (headModel) over the colonist's head (person_head), left out as the game
    //     leaves out its head while worn apparel hides the head (renderSkipFlags Head: the burka, shown
    //     as the same closed burka covering a colonist gets, person_hat_burka, in its colour);
    //   - the clothes as the same 3D parts a colonist of that body type wears (People.Dress: trousers,
    //     shirt, coat or armour, hat or helmet), in the same colours;
    //   - packs, belts and waist tools with a 3D part (WornGear) on it as on a colonist; clothing with no 3D part
    //     (glasses, some belt-layer artifacts) left out, as a 3D colonist leaves it out;
    //   - the weapon made solid from its sprite (as a colonist's held weapon), placed and turned as the
    //     game carries it on a stand (DrawWeapon); with no 3D form, its sprite stood upright over the
    //     figure (apparelHeight, apparelScale).
    public static class Stands
    {
        private const float DepthScale = 0.3f;   // the game's drawing heights, squeezed into a thin stack toward the camera
        private const float Nudge = 0.36f;       // and that stack held this far in front of the model's body, in cells
        private const float PersonHeight = 1.12f;        // a figure of size 1, feet to the top of the head (People.FigureHeight)
        private const float PictureHeight = 1.5f;        // the game's picture of a pawn (its 1.5-cell body mesh)
        private const float WeaponGap = 0.05f;           // between the dressed body and the weapon held in front of it, figure units

        // The game's carried-weapon offsets (PawnRenderUtility.EqLoc*), by facing.
        private static readonly Vector3[] EqLoc =
        {
            new Vector3(0f, 0f, -0.11f), new Vector3(0.22f, 0f, -0.22f), new Vector3(0f, 0f, -0.22f), new Vector3(-0.22f, 0f, -0.22f),
        };

        // What one stand shows, worked out when its contents change.
        private class Outfit
        {
            public int version;
            public bool made;
            public string bodyKey;
            public string headKey;
            public float halfDepth;   // how far the dressed body reaches north or south of its middle, figure units (facing north)
            public float halfWidth;   // and east or west
        }

        private static readonly Dictionary<int, Outfit> outfits = new Dictionary<int, Outfit>();
        // Ready-made meshes of the dressed parts, shared by every stand that shows the same.
        private static readonly Dictionary<string, List<DynamicModels.Part>> meshes = new Dictionary<string, List<DynamicModels.Part>>();
        private static readonly List<Model> partModels = new List<Model>();
        private static readonly List<Matrix4x4> partLocals = new List<Matrix4x4>();
        private static readonly List<Color> partTints = new List<Color>();

        private static FieldInfo skipHeadField;
        private static MethodInfo bodyTypeGetter;
        private static MethodInfo weaponFactorGetter;
        private static bool reflected;

        public static bool Draw(Building_OutfitStand stand, ModelEntry entry, Vector3 drawLoc)
        {
            if (!reflected)
            {
                reflected = true;
                skipHeadField = AccessTools.Field(typeof(Building_OutfitStand), "cachedApparelRenderInfoSkipHead");
                bodyTypeGetter = AccessTools.PropertyGetter(typeof(Building_OutfitStand), "BodyTypeDefForRendering");
                weaponFactorGetter = AccessTools.PropertyGetter(typeof(Building_OutfitStand), "WeaponDrawDistanceFactor");
            }
            if (skipHeadField == null || bodyTypeGetter == null || weaponFactorGetter == null)
            {
                return false;
            }
            ModelDef def = entry.def;
            BodyTypeDef body = bodyTypeGetter.Invoke(stand, null) as BodyTypeDef;
            bool skipHead = (bool)skipHeadField.GetValue(stand);
            float yaw = Glass.YawOf(stand, entry);
            Vector3 c = stand.TrueCenter();
            Vector3 ground = new Vector3(c.x, 0f, c.z);
            Vector3 fit = entry.Fit(stand.def) * def.scale;
            // The dressed figure: a colonist of the stand's body type and size (People's figure space in cells, as a
            // colonist is drawn), lifted dressRise cells onto the stand (the stand model is sized to it: ModelDef.scale).
            float size = FigureSize(body);
            Matrix4x4 figure = Matrix4x4.Translate(new Vector3(0f, def.dressRise, 0f))
                * Matrix4x4.Scale(Vector3.one * size);

            if (!skipHead && entry.head != null)
            {
                // The stand's own head, the size of a colonist's head and where it sits (person_head).
                DynamicModels.DrawLocal(ObjLoader.SheetTinted(entry.head, R3DTextures.SheetTint(stand)), ground, yaw, figure * HeadFit(entry.head), R3DTextures.ModelTint(stand), R3DTextures.ModelSwatch(stand));
            }

            Outfit outfit = OutfitOf(stand, body, skipHead);
            DynamicModels.DrawParts(MeshesFor(outfit.bodyKey), ground, yaw, figure);
            DynamicModels.DrawParts(MeshesFor(outfit.headKey), ground, yaw, figure);

            ThingWithComps weapon = stand.HeldWeapon;
            bool weaponDrawn = weapon == null;
            if (weapon != null && IsoRimMod.Settings.heldWeapons3D)
            {
                weaponDrawn = DrawWeapon(stand, weapon, def, size, c, outfit);
            }

            // Clothing with no 3D part is left out, as on a 3D colonist (People.Dress).
            if (weaponDrawn)
            {
                return true;
            }

            bool wasActive = DrawHook.active, wasBillboard = DrawHook.billboard, wasSkip = DrawHook.skip;
            Matrix4x4 wasXform = DrawHook.xform;
            try
            {
                DrawHook.active = true;
                DrawHook.billboard = false;
                DrawHook.skip = false;
                // No 3D form (or 3D weapons off): the game's sprite, as it carries it, stood up over the figure.
                Rot4 seen = Seen(stand.Rotation);
                float itemY = AltitudeLayer.Item.AltitudeFor();
                DrawHook.xform = Card(c, def.apparelHeight * fit.y, def.apparelScale * fit.y, new Vector3(drawLoc.x, itemY, drawLoc.z));
                Vector3 at = seen != Rot4.North
                    ? new Vector3(drawLoc.x, AltitudeLayer.ItemImportant.AltitudeFor() + PawnRenderUtility.AltitudeForLayer(90f), drawLoc.z)
                    : new Vector3(drawLoc.x, itemY - 0.05f, drawLoc.z);
                PawnRenderUtility.DrawCarriedWeapon(weapon, at, seen, (float)weaponFactorGetter.Invoke(stand, null));
            }
            finally
            {
                DrawHook.active = wasActive;
                DrawHook.billboard = wasBillboard;
                DrawHook.skip = wasSkip;
                DrawHook.xform = wasXform;
            }
            return true;
        }

        // The weapon as the game carries it on a stand (PawnRenderUtility.DrawCarriedWeapon, DrawEquipmentAiming):
        // the game's flat picture is the stand seen from the south, so the weapon (its sprite made solid, as a
        // colonist's) stands in the east-west upright plane, turned and mirrored as the game turns its sprite,
        // moved by the game's offset for the stand's facing (east-west across the picture, south = lower on the
        // figure), held just clear of the dressed body: in front of it (south), or behind it for a stand facing
        // north, which the game draws under the body. Sized with the dressed figure.
        private static bool DrawWeapon(Building_OutfitStand stand, ThingWithComps weapon, ModelDef def, float size, Vector3 c, Outfit outfit)
        {
            Rot4 facing = stand.Rotation;
            float aim = facing == Rot4.West ? 217f : 143f;
            Vector3 off = EqLoc[facing.AsInt] * (float)weaponFactorGetter.Invoke(stand, null);
            float picture = PersonHeight / PictureHeight;   // the game's picture units to figure units
            float height = def.dressRise + (PersonHeight * 0.5f + off.z * picture) * size;
            float across = off.x * picture * size;
            // Clear of the body: its reach toward the south (its depth, or its width if it faces east or west).
            float reach = facing.IsHorizontal ? outfit.halfWidth : outfit.halfDepth;
            float clear = (reach + WeaponGap) * size;
            Vector3 centre = new Vector3(c.x + across, height, facing == Rot4.North ? c.z + clear : c.z - clear);

            bool flip = aim > 200f && aim < 340f;
            float angle = flip ? aim - 270f - weapon.def.equippedAngleOffset : aim - 90f + weapon.def.equippedAngleOffset;
            if (GroundGear.HeldSprite(weapon.def) && SpriteSolids.DrawUpright(weapon, centre, angle, flip, size))
            {
                return true;   // its sprite made solid, as a 3D colonist holds it
            }
            return Patches.HeldWeapon3D(weapon, centre, aim);   // another mod's model for it (or no sprite to make solid), level
        }

        // The size of the colonist figure a stand is dressed as: People's size for the stand's body (the kid
        // stand's child body: a child's life stage; adult stands 1).
        private static float FigureSize(BodyTypeDef body)
        {
            LifeStageDef child = LifeStageDefOf.HumanlikeChild;
            return People.SizeFor(body == BodyTypeDefOf.Child && child != null ? child.bodySizeFactor : 1f);
        }

        // Places a stand's head model over a colonist's head (person_head: its box, in figure space).
        private static Matrix4x4 HeadFit(Model head)
        {
            Model person = People.Part("person_head");
            if (person == null)
            {
                return Matrix4x4.identity;
            }
            Bounds from = BoxOf(head), to = BoxOf(person);
            Vector3 scale = new Vector3(to.size.x / Mathf.Max(from.size.x, 1e-4f), to.size.y / Mathf.Max(from.size.y, 1e-4f), to.size.z / Mathf.Max(from.size.z, 1e-4f));
            return Matrix4x4.Translate(to.center) * Matrix4x4.Scale(scale) * Matrix4x4.Translate(-from.center);
        }

        private static readonly Dictionary<int, Bounds> boxes = new Dictionary<int, Bounds>();

        private static Bounds BoxOf(Model m)
        {
            Bounds b;
            if (boxes.TryGetValue(m.id, out b))
            {
                return b;
            }
            b = new Bounds(m.positions.Length > 0 ? m.positions[0] : Vector3.zero, Vector3.zero);
            for (int i = 1; i < m.positions.Length; i++)
            {
                b.Encapsulate(m.positions[i]);
            }
            boxes[m.id] = b;
            return b;
        }

        // The stand's clothes as figure parts (People.Dress), kept until its contents change.
        private static Outfit OutfitOf(Building_OutfitStand stand, BodyTypeDef body, bool skipHead)
        {
            ThingOwner things = ((IThingHolder)stand).GetDirectlyHeldThings();
            int version = things.Count * 2 + (skipHead ? 1 : 0);
            for (int i = 0; i < things.Count; i++)
            {
                version = version * 31 + things[i].thingIDNumber;
            }
            Outfit outfit;
            if (outfits.TryGetValue(stand.thingIDNumber, out outfit) && outfit.version == version && outfit.made)
            {
                return outfit;
            }
            if (outfit == null)
            {
                if (outfits.Count > 500)
                {
                    outfits.Clear();
                }
                outfit = new Outfit();
                outfits[stand.thingIDNumber] = outfit;
            }
            outfit.version = version;

            List<Apparel> worn = new List<Apparel>();
            Apparel hides = null;
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Apparel a)
                {
                    worn.Add(a);
                    if (hides == null && a.def.apparel != null && a.def.apparel.renderSkipFlags.NotNullAndContains(RenderSkipFlagDefOf.Head))
                    {
                        hides = a;
                    }
                }
            }
            string build = People.BuildOf(body);
            People.Look look = new People.Look();
            People.Dress(look, build, worn, Color.white, null);   // as a colonist: clothing with no 3D part is left out

            // The body: only what some clothing covers (the stand's own body shows elsewhere).
            Begin();
            List<(Model, Matrix4x4, Color)> headParts = new List<(Model, Matrix4x4, Color)>();
            People.ClothesParts(look, build, Add, (m, l, c) => { if (m != null) headParts.Add((m, l, c)); });
            // How far the dressed body reaches (the shirt part stands for the stand's own body), for the weapon.
            outfit.halfDepth = look.upper != null ? look.upper.halfDepth : 0.15f;
            outfit.halfWidth = look.upper != null ? Mathf.Max(-look.upper.minX, look.upper.maxX) : 0.25f;
            for (int i = 0; i < partModels.Count; i++)
            {
                outfit.halfDepth = Mathf.Max(outfit.halfDepth, partModels[i].halfDepth * partLocals[i].m22);
                outfit.halfWidth = Mathf.Max(outfit.halfWidth, Mathf.Max(-partModels[i].minX, partModels[i].maxX) * partLocals[i].m00);
            }
            outfit.bodyKey = Key("b");

            // The head: the hat on the stand's head, as the game draws headgear only while the head shows;
            // with the head hidden by a garment (renderSkipFlags Head: the burka), the closed covering a colonist gets
            // (People: person_hat_burka) in that garment's colour.
            Begin();
            if (!skipHead)
            {
                foreach ((Model m, Matrix4x4 l, Color c) in headParts)
                {
                    Add(m, l, c);
                }
            }
            else if (hides != null)
            {
                Add(People.Part("person_hat_burka"), Matrix4x4.identity, People.ApparelColour(hides));
            }
            outfit.headKey = Key("h");
            outfit.made = true;
            return outfit;
        }

        private static void Begin()
        {
            partModels.Clear();
            partLocals.Clear();
            partTints.Clear();
        }

        private static void Add(Model m, Matrix4x4 local, Color tint)
        {
            if (m == null)
            {
                return;
            }
            partModels.Add(m);
            partLocals.Add(local);
            partTints.Add(tint);
        }

        // A name for the parts gathered (models, placings, colours); their meshes are made once per name.
        private static string Key(string kind)
        {
            if (partModels.Count == 0)
            {
                return null;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder(kind);
            for (int i = 0; i < partModels.Count; i++)
            {
                Color32 t = partTints[i];
                sb.Append('|').Append(partModels[i].id).Append(':').Append(t.r).Append(',').Append(t.g).Append(',').Append(t.b)
                    .Append(':').Append(partLocals[i].m00.ToString("0.###")).Append(',').Append(partLocals[i].m22.ToString("0.###"))
                    .Append(',').Append(partLocals[i].m03.ToString("0.###")).Append(',').Append(partLocals[i].m23.ToString("0.###"));
            }
            string key = sb.ToString();
            if (!meshes.ContainsKey(key))
            {
                if (meshes.Count > 300)
                {
                    foreach (List<DynamicModels.Part> old in meshes.Values)
                    {
                        DynamicModels.Destroy(old);
                    }
                    meshes.Clear();
                    foreach (Outfit o in outfits.Values)
                    {
                        o.made = false;   // worked out again, and its meshes made again, when next drawn
                    }
                }
                meshes[key] = DynamicModels.BuildMerged(new List<Model>(partModels), new List<Matrix4x4>(partLocals), new List<Color>(partTints), 0);
            }
            return key;
        }

        private static List<DynamicModels.Part> MeshesFor(string key)
        {
            List<DynamicModels.Part> parts;
            return key != null && meshes.TryGetValue(key, out parts) ? parts : null;
        }

        // Stands what the game draws flat about pivot upright over the stand at centre, square to the
        // camera but straight up (leaning back toward the camera, as a pawn's sprite does, its top would
        // sink into the model's body and head): pivot at height cells, scale times the game's size, the
        // game's heights squeezed into a thin stack held a little in front of the model.
        private static Matrix4x4 Card(Vector3 centre, float height, float scale, Vector3 pivot)
        {
            float s = View3D.Squash;
            return Matrix4x4.Translate(new Vector3(centre.x, height / s, centre.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(-90f, View3D.Yaw, 0f))
                * Matrix4x4.Translate(new Vector3(0f, Nudge, 0f))
                * Matrix4x4.Scale(new Vector3(scale, DepthScale, scale))
                * Matrix4x4.Translate(-pivot);
        }

        // The side of the stand the camera sees: its own facing while the camera looks roughly north,
        // turned with the camera beyond that (the same rule as upright pawn sprites).
        public static Rot4 Seen(Rot4 facing)
        {
            return View3D.SeenFacing(facing);
        }

        // For the harness: what the stand shows and how.
        public static string Report(Building_OutfitStand stand)
        {
            bool skip = skipHeadField != null && (bool)skipHeadField.GetValue(stand);
            int apparel = 0;
            foreach (Thing t in ((IThingHolder)stand).GetDirectlyHeldThings())
            {
                if (t is Apparel)
                {
                    apparel++;
                }
            }
            Outfit o;
            outfits.TryGetValue(stand.thingIDNumber, out o);
            return "apparel " + apparel + ", weapon " + (stand.HeldWeapon != null ? stand.HeldWeapon.def.defName : "none") + ", head hidden " + skip
                + ", seen " + Seen(stand.Rotation) + ", model " + SectionLayer_IsoRimBodies.StandsIn(stand)
                + (o != null ? ", 3D parts " + (o.bodyKey ?? "-") + " / " + (o.headKey ?? "-") : "");
        }
    }
}
