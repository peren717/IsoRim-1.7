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
    // People drawn as 3D figures in place of their sprites.
    //
    // A figure keeps the game's own proportions (a big round head on a simple body with no arms
    // or legs) and is put together from plain parts, each coloured from the person it shows:
    //   - the body, in the game's five builds (and smaller for children);
    //   - skin colour; hair in the person's own colour and roughly their own cut; a beard;
    //   - what they are wearing: trousers and shirt colour the lower and upper body, a jacket or
    //     a long coat goes over them, armour adds plates, and a hat, helmet or hood goes on top.
    //     Coats, robes, suits and straps the mod has a shape for (CoatShapes) get it; a flak vest under one that is
    //     open at the front shows its front plate in the opening, and under a strap it shows whole with the strap
    //     made to go over it, as the game's sprites do. A corset, vest, sash or a shirt with a collar or panels
    //     (MidShapes) adds a thin over-shape on the shirt, shown alone, in an open coat's front and under a strap.
    // Because all of it is read from the person, anyone the game makes up gets a figure to match.
    // Like the animals, a figure turns to face the way it is really walking.
    // Anomaly: a ghoul gets its own head (by its forced head type), its own hair, the ridged ghoulskin over the
    // body and its implanted barbs and plating (by hediff); a shambler keeps its own head and clothes, with
    // scars on the face and, while nothing covers the body, wounds on it; a creepjoiner (race CreepJoiner)
    // with one of the special head types (leathery, dark scholar, cult escapee, timeless) gets its own face,
    // an eye that is missing left out.
    public static class People
    {
        // What one person looks like. Worked out from the pawn now and then, not every frame.
        internal class Look
        {
            public int frame;
            public bool upperWorn;   // some clothing covers the upper body (the shirt part shows it)
            public bool lowerWorn;   // and the legs
            public float size = 1f;
            public float headWide = 1f;
            public float shoulders = 1f;
            public Vector3 bodyScale = Vector3.one;   // the body and all worn on it, by body type (BodyScales)
            public Model lower;
            public Model upper;
            public Model coat;
            public Model underCoat;   // a flak vest under the coat: its front plate in an open coat's front (no outline), or whole under a strap
            public Model mid;         // a Middle-layer garment's thin over-shape on the shirt (corset, vest, sash; MidShapes)
            public Model shirtWear;   // an on-skin garment's over-shape (ruffle collar and jabot, eltex shirt wedges), under mid
            public Color midColor = Color.white;
            public Color shirtWearColor = Color.white;
            public Model overCoat;    // a garment the game draws over the Shell layer (VE Framework's tabards, mantles), fitted over the coat
            public Color overCoatColor = Color.white;
            public Model hair;
            public Model hairTop;     // a hairstyle's separate part (topbun_tail's bun), drawn with the hair
            public bool hairShown;    // the game draws hair now (no headgear hides it)
            public string hatKind;    // for Report
            public Model beard;
            public Model hat;
            public Model legArmour;
            public bool pads;
            public bool visor;
            public bool faceHidden;   // the game leaves out the eyes (skip flag Eyes): no face, eyes or scars
            public bool faceDecal;    // Facial Animation's face goes on this head (FaceDecal): IsoRim's eyes left out
            public bool femaleEyes;   // a Female_* head type: vanilla draws its eyes with the longer upturned lash
            public bool headHidden;   // the game leaves out the whole head (skip flag Head, the burka): its covering (hat) instead
            public bool bedHidesHat;  // in a bed that hides headgear when this look was worked out (People.BedHidesHeadgear)
            public Color skin = Color.white;
            public Color hairColor = Color.white;
            public Color upperColor = Color.white;
            public Color lowerColor = Color.white;
            public Color coatColor = Color.white;
            public Color underCoatColor = Color.white;
            public Color hatColor = Color.white;
            public Color legArmourColor = Color.white;
            public Model mouthWear;   // a mouth-only item (cloth mask), head space
            public Model eyeWear;     // an eye cover (blindfold), head space
            public Model neckWear;    // a neck-only item (slave collar), body space
            public Color mouthColor = Color.white;
            public Color eyeColor = Color.white;
            public Color neckColor = Color.white;
            public Model ownHead;     // replaces head and face (ghoul heads), already as wide as the head type
            public Model ownFace;     // replaces the face (creepjoiner faces), tinted faceTint
            public Model eyeLeft;     // the creepjoiner face's separate eyes (cult escapee), left out when missing
            public Model eyeRight;
            public Model scars;       // over the face (shambler)
            public Model skinOverlay; // over the body (ghoulskin ridges, shambler wounds)
            public Model barbs;       // ghoul implants
            public Model plating;
            public bool ownHair;      // hair made for the ghoul head: not widened again, fixed colour
            public Color faceTint = Color.white;
            public Model skull;       // dried to bones: replaces head and face (Bones)
            public bool upperBones;   // upper / lower is the skeleton's part: not body-scaled, own colours
            public bool lowerBones;
            // Packs, belts and waist tools (WornGear), each with its place (figure space, before bodyScale) and colour;
            // rigid ones keep their own size (only their place takes bodyScale).
            public readonly List<Model> gear = new List<Model>();
            public readonly List<Matrix4x4> gearPlace = new List<Matrix4x4>();
            public readonly List<Color> gearTint = new List<Color>();
            public readonly List<bool> gearRigid = new List<bool>();
            public readonly List<Vector3> gearAnchor = new List<Vector3>();   // a rigid one's own point that goes to its place
            // Apparel model bindings (ApparelModels, Api 9): the held shield (drawn each frame, not merged), what a bound
            // hat hides besides the game's own rules, and whether a bound part burns (its live flames drawn each frame).
            public Model shield;
            public Color shieldColor = Color.white;
            public ApparelModels.Binding shieldBinding;
            public int bindHides;
            public bool flames;
            // Which worn apparel each body garment shows (Dress; null: none), for the apparel decals (ApparelDecals, Api 10),
            // and the decals worked out for this look (null: none). Not part of SameAs: decals are drawn apart from the
            // merged meshes and carried over to a look kept as it was (LookOf).
            public Apparel coatFrom, overCoatFrom, midFrom, shirtWearFrom;
            public List<ApparelDecals.Placed> decals;
            public AlienLook alien;     // a race drawn as a 3D figure (Api 25, AlienFigures): its placing, own models, addon cards; null for people

            public bool SameGear(Look o)
            {
                if (o.gear.Count != gear.Count)
                {
                    return false;
                }
                for (int i = 0; i < gear.Count; i++)
                {
                    if (o.gear[i] != gear[i] || o.gearPlace[i] != gearPlace[i] || o.gearTint[i] != gearTint[i])
                    {
                        return false;
                    }
                }
                return true;
            }

            // Where gear i goes on the figure, with the body type's scale.
            public Matrix4x4 GearOnBody(int i)
            {
                if (!gearRigid[i])
                {
                    return Matrix4x4.Scale(bodyScale) * gearPlace[i];
                }
                Vector3 t = gearPlace[i].GetColumn(3);
                return Matrix4x4.Translate(Vector3.Scale(bodyScale, t) - gearAnchor[i]);
            }

            // Ready-made meshes of this look, by facing, body or head, and alive or dead.
            public readonly Dictionary<int, List<DynamicModels.Part>> meshes = new Dictionary<int, List<DynamicModels.Part>>();
            public Color statueTint;   // the colour the statue meshes (keys 4 and 6) were carved in

            public bool SameAs(Look o)
            {
                return o != null && size == o.size && headWide == o.headWide && shoulders == o.shoulders && bodyScale == o.bodyScale && lower == o.lower && upper == o.upper
                    && coat == o.coat && underCoat == o.underCoat && underCoatColor == o.underCoatColor && hair == o.hair && hairTop == o.hairTop && beard == o.beard && hat == o.hat && pads == o.pads && visor == o.visor
                    && legArmour == o.legArmour && legArmourColor == o.legArmourColor && faceHidden == o.faceHidden && faceDecal == o.faceDecal && femaleEyes == o.femaleEyes && headHidden == o.headHidden
                    && skin == o.skin && hairColor == o.hairColor && upperColor == o.upperColor && lowerColor == o.lowerColor
                    && coatColor == o.coatColor && hatColor == o.hatColor && ownHead == o.ownHead && ownFace == o.ownFace && eyeLeft == o.eyeLeft
                    && eyeRight == o.eyeRight && scars == o.scars && skinOverlay == o.skinOverlay && barbs == o.barbs && plating == o.plating
                    && ownHair == o.ownHair && faceTint == o.faceTint && SameGear(o)
                    && skull == o.skull && upperBones == o.upperBones && lowerBones == o.lowerBones
                    && mouthWear == o.mouthWear && eyeWear == o.eyeWear && neckWear == o.neckWear
                    && mouthColor == o.mouthColor && eyeColor == o.eyeColor && neckColor == o.neckColor
                    && mid == o.mid && shirtWear == o.shirtWear && midColor == o.midColor && shirtWearColor == o.shirtWearColor
                    && overCoat == o.overCoat && overCoatColor == o.overCoatColor
                    && shield == o.shield && shieldColor == o.shieldColor && shieldBinding == o.shieldBinding && bindHides == o.bindHides && flames == o.flames
                    && (alien == null ? o.alien == null : alien.SameAs(o.alien));
            }

            public void Forget()
            {
                foreach (List<DynamicModels.Part> parts in meshes.Values)
                {
                    DynamicModels.Destroy(parts);
                }
                meshes.Clear();
            }
        }

        private static readonly List<Model> mergeModels = new List<Model>();
        private static readonly List<Matrix4x4> mergeLocals = new List<Matrix4x4>();
        private static readonly List<Color> mergeTints = new List<Color>();

        // The body (head = false) or the head of a figure as ready-made meshes.
        // The parts hidden by figure hooks or a face drawer (Api 21, FigureParts) that change the head's or the body's
        // meshes (Legs goes through noLegs, Shield is drawn apart).
        private const int HeadHides = (int)(FigureParts.Head | FigureParts.Face | FigureParts.Hair | FigureParts.Beard | FigureParts.Headgear
            | FigureParts.EyeWear | FigureParts.MouthWear);
        private const int BodyHides = (int)(FigureParts.Body | FigureParts.Clothes | FigureParts.Gear);

        private static List<DynamicModels.Part> Merged(Look look, bool headPart, bool dead, int turned, Color? mono = null, bool noLegs = false,
            FigureParts hidden = FigureParts.None)
        {
            // The meshes carry no shading (the shader lights them), so one set serves every light and turn.
            // A statue's subject (mono: the statue colour) has its own set, made again when the statue is repainted.
            // Without legs (Api 13, AttachPose.SitNoLegs): the body's faces below the hips left out (key 8).
            // Parts hidden (Api 21): a set of its own for each mix (key bits 16 and up; nothing hidden: the usual set).
            // (a race's head in a bed that hides the body: its parts below the chin left out, as its body is, Api 25)
            noLegs &= (!headPart || look.alien != null) && !mono.HasValue;
            int hide = mono.HasValue ? 0 : (int)hidden & (headPart ? HeadHides : BodyHides);
            int key = (headPart ? 2 : 0) + (dead ? 1 : 0) + (mono.HasValue ? 4 : 0) + (noLegs ? 8 : 0) + (hide << 4);
            List<DynamicModels.Part> parts;
            if (mono.HasValue && look.statueTint != mono.Value)
            {
                for (int k = 4; k < 8; k++)
                {
                    if (look.meshes.TryGetValue(k, out parts))
                    {
                        DynamicModels.Destroy(parts);
                        look.meshes.Remove(k);
                    }
                }
                look.statueTint = mono.Value;
            }
            if (look.meshes.TryGetValue(key, out parts))
            {
                return parts;
            }
            mergeModels.Clear();
            mergeLocals.Clear();
            mergeTints.Clear();
            if (headPart)
            {
                Matrix4x4 headPlace = Matrix4x4.Scale(new Vector3(look.headWide, 1f, 1f));
                Color skin = dead ? Color.Lerp(look.skin, new Color(0.55f, 0.58f, 0.52f), 0.35f) : look.skin;
                Model hat = (hide & (int)FigureParts.Headgear) == 0 ? look.hat : null;   // (parts hidden: Api 21)
                Model eyeWear = (hide & (int)FigureParts.EyeWear) == 0 ? look.eyeWear : null;
                Model mouthWear = (hide & (int)FigureParts.MouthWear) == 0 ? look.mouthWear : null;
                if (look.headHidden || (hide & (int)FigureParts.Head) != 0)
                {
                    // No head at all, as the game draws it: the covering (look.hat) takes its place.
                }
                else if (look.skull != null)
                {
                    AddPart(look.skull, Matrix4x4.identity, Color.white);   // one size, as the game's Skull head
                }
                else if (look.ownHead != null)
                {
                    AddPart(look.ownHead, Matrix4x4.identity, look.alien != null ? skin : Color.white);   // its skin colour is in the model (a race's own head: the skin's, Api 25)
                }
                else
                {
                    AddPart(head, headPlace, skin);
                    if (!look.faceHidden && !look.faceDecal && (hide & (int)FigureParts.Face) == 0)
                    {
                        AddPart(look.ownFace ?? (look.femaleEyes && faceFemale != null ? faceFemale : face), headPlace, look.ownFace != null ? look.faceTint : Color.white);
                        AddPart(look.eyeLeft, headPlace, Color.white);
                        AddPart(look.eyeRight, headPlace, Color.white);
                        AddPart(look.scars, headPlace, Color.white);
                    }
                }
                if ((hide & (int)FigureParts.Hair) != 0)
                {
                    // hidden
                }
                else if (look.ownHair)
                {
                    AddPart(look.hair, Matrix4x4.identity, Color.white);
                }
                else if (look.headHidden)
                {
                    AddPart(look.hair, headPlace, look.hairColor);
                    AddPart(look.hairTop, headPlace, look.hairColor);
                }
                else
                {
                    // Hair shown with headgear goes inside it instead of through it (HairTuck); unchanged without.
                    AddPart(HairTuck.Under(look.hair, hat, mouthWear, eyeWear), headPlace, look.hairColor);
                    AddPart(HairTuck.Under(look.hairTop, hat, mouthWear, eyeWear), headPlace, look.hairColor);
                }
                if ((hide & (int)FigureParts.Beard) == 0)
                {
                    AddPart(look.headHidden ? look.beard : HairTuck.Under(look.beard, hat, mouthWear, eyeWear), headPlace, look.hairColor);
                }
                AddPart(eyeWear, headPlace, look.eyeColor);
                AddPart(mouthWear, headPlace, look.mouthColor);
                AddPart(hat, headPlace, look.hatColor);
                if (hat != null && look.visor)
                {
                    AddPart(visor, headPlace, Color.white);
                }
            }
            else
            {
                Matrix4x4 bodyPlace = Matrix4x4.Scale(look.bodyScale);
                bool body = (hide & (int)FigureParts.Body) == 0;   // (parts hidden: Api 21)
                bool clothes = (hide & (int)FigureParts.Clothes) == 0;
                if (body)
                {
                    AddPart(look.lower, look.lowerBones ? Matrix4x4.identity : bodyPlace, look.lowerBones ? Color.white : look.lowerColor);
                    AddPart(look.upper, look.upperBones ? Matrix4x4.identity : bodyPlace, look.upperBones ? Color.white : look.upperColor);
                }
                if (clothes)
                {
                    AddPart(look.shirtWear, bodyPlace, look.shirtWearColor);
                    AddPart(look.mid, bodyPlace, look.midColor);
                }
                if (body)
                {
                    AddPart(look.skinOverlay, bodyPlace, Color.white);
                    AddPart(look.barbs, bodyPlace, Color.white);
                    AddPart(look.plating, bodyPlace, Color.white);
                }
                if (clothes)
                {
                    AddPart(look.legArmour, bodyPlace, look.legArmourColor);
                    AddPart(look.underCoat, bodyPlace, look.underCoatColor);
                    AddPart(look.coat, bodyPlace, look.coatColor);
                    AddPart(look.overCoat, bodyPlace, look.overCoatColor);
                    AddPart(look.neckWear, bodyPlace, look.neckColor);
                }
                for (int i = 0; (hide & (int)FigureParts.Gear) == 0 && i < look.gear.Count; i++)
                {
                    AddPart(look.gear[i], look.GearOnBody(i), look.gearTint[i]);
                }
                if (look.pads && clothes)
                {
                    AddPart(pads, bodyPlace * Matrix4x4.Scale(new Vector3(look.shoulders, 1f, 1.05f)), look.coat != null ? look.coatColor : look.upperColor);
                }
            }
            AlienFigures.AddParts(look, headPart, hide, mergeModels, mergeLocals, mergeTints,   // a race's addon models, eyes on its own head (Api 25)
                noLegs && headPart ? AlienFigures.ChinClip(look) : float.NegativeInfinity);
            parts = DynamicModels.BuildMerged(mergeModels, mergeLocals, mergeTints, turned, mono,
                noLegs ? (headPart ? AlienFigures.ChinClip(look) : Attachments.Hips * look.bodyScale.y) : float.NegativeInfinity);
            look.meshes[key] = parts;
            return parts;
        }

        private static void AddPart(Model m, Matrix4x4 local, Color tint)
        {
            if (m == null)
            {
                return;
            }
            mergeModels.Add(m);
            mergeLocals.Add(local);
            mergeTints.Add(tint);
        }

        private const int RefreshFrames = 90;
        private const float HeadHeight = 0.98f;   // where the middle of the head is on the figure
        private const float GibbetScale = 0.78f;  // a corpse hanging in a gibbet cage, shrunk to stay under its dome
        private const float FigureHeight = 1.12f; // top of the head of a figure of size 1
        private const float CarryReach = 0.24f;   // a carried thing's middle, in front of the figure
        private const float CarryHeight = 0.36f;  // and the height of its bottom
        private const float HandY = 0.46f;        // where the hands are on the figure (a held weapon, a work tool)

        private static readonly Dictionary<int, Look> looks = new Dictionary<int, Look>();
        private static readonly Dictionary<string, Model> models = new Dictionary<string, Model>();
        private static Model head;
        private static Model face;
        private static Model faceFemale;   // person_face_female: the eyes of the Female_* head types
        private static Model visor;
        private static Model pads;
        private static bool tried;

        // Draws the person at drawLoc, standing with what they hold and wear over themselves as the game draws it
        // (FigureWeapons; neverAim: the game's flag not to aim the weapon). Returns false if they cannot be drawn as a
        // figure, in which case the game's sprite must be drawn as usual.
        public static bool Draw(Pawn pawn, Vector3 drawLoc, bool neverAim)
        {
            bool standing = false;
            if (pawn.story == null || !FigureRace(pawn) || Hidden(pawn) || !Corpses.ShownAsModel(pawn))
            {
                return false;
            }
            if (!tried)
            {
                tried = true;
                head = Part("person_head");
                face = Part("person_face");
                faceFemale = Part("person_face_female");
                foreach (Model eyes in new[] { face, faceFemale })
                {
                    if (eyes != null)
                    {
                        // The eyes are flat decals on the face: an outline shell would poke out as dark spikes at grazing angles.
                        eyes.noOutline = new bool[eyes.positions.Length];
                        for (int i = 0; i < eyes.noOutline.Length; i++)
                        {
                            eyes.noOutline[i] = true;
                        }
                    }
                }
                visor = Part("person_visor");
                pads = Part("person_pads");
            }
            if (head == null)
            {
                return false;
            }
            Look look = LookOf(pawn);
            if (look == null || look.upper == null || look.lower == null)
            {
                return false;
            }

            Color? stone = Statues.Colour(pawn);
            if (stone.HasValue)
            {
                // A statue's subject (Statues.cs): standing still on the statue's base, facing south, carved in its colour.
                const float South = 180f;
                Vector3 onBase = new Vector3(drawLoc.x, Statues.ground, drawLoc.z);
                Matrix4x4 scaled = Matrix4x4.Scale(new Vector3(look.size, look.size, look.size));
                FigureScope.Root(onBase, South);
                DynamicModels.DrawParts(Merged(look, false, false, 4, stone), onBase, South, scaled);
                DynamicModels.DrawParts(Merged(look, true, false, 4, stone), onBase, South, look.alien != null ? scaled * look.alien.head : scaled);
                return true;   // nothing held, no weapon
            }

            bool dead = pawn.Dead;
            float lift = Carried.LiftOf(pawn, ref drawLoc);   // in mid-jump, or carried by another 3D pawn
            // A corpse in a gibbet cage shown as its model hangs upright in the cage (lying, an
            // adult would reach out through the bars), feet on the cage floor.
            Building_GibbetCage gibbet = dead ? GibbetOf(pawn) : null;
            // A person held in a glass vat shown as its model that floats them upright (growth vat):
            // standing in the vat, shrunk to fit under its roof.
            Building holder = dead ? null : Glass.HolderOf(pawn);
            ModelEntry vat = holder != null ? Glass.EntryFor(holder) : null;
            if (vat != null && !vat.def.heldPawnUpright)
            {
                vat = null;
            }
            // Attached to a host (Api 13, Attachments): on its anchor, posed and turned as the attachment says.
            Attachments.Placed att;
            bool attached = Attached(pawn, look, !dead && gibbet == null && vat == null, neverAim, true, out att);
            bool noLegs = attached && att.a.pose == AttachPose.SitNoLegs;
            if (attached)
            {
                drawLoc.x = att.at.x;
                drawLoc.z = att.at.z;
                lift = 0f;
            }
            bool lying = attached ? att.a.pose == AttachPose.Lie : gibbet == null && vat == null && (dead || pawn.GetPosture() != PawnPosture.Standing);
            // Crawling (1.7): the game's own test (Pawn.Crawling: downed, able to crawl, on a job done crawling, not in bed).
            bool crawling = lying && !attached && !dead && gibbet == null && vat == null && pawn.Crawling;
            // Sitting (Poses): the game's shift up the screen taken out of drawLoc before the facing is tracked.
            Poses.Seat seat = default(Poses.Seat);
            bool seated = attached ? AttachedSeat(att, out seat) : !lying && gibbet == null && vat == null && lift <= 0f && Poses.Seated(pawn, ref drawLoc, out seat);
            float size = look.size * (gibbet != null ? GibbetScale : 1f);
            float rise = 0f;
            if (vat != null)
            {
                rise = Glass.FloatRise(Find.TickManager.TicksGame);
                size = Mathf.Min(size, (Glass.Room(vat) - rise - 0.02f) / FigureHeight);
            }
            float yaw;
            float phase = 0f;
            float gait = 0f;
            if (attached)
            {
                yaw = att.yaw;   // still: its host steps (the host's bob is in the anchor)
            }
            else if (gibbet != null)
            {
                yaw = 180f + (Hashing.Unit(pawn.thingIDNumber, 3, 9) - 0.5f) * 50f;
            }
            else if (vat != null)
            {
                yaw = holder.Rotation.AsAngle + vat.def.rotation + 180f;   // facing the vat's front
            }
            else if (dead)
            {
                yaw = pawn.Rotation.AsAngle + (Hashing.Unit(pawn.thingIDNumber, 3, 9) - 0.5f) * 60f;
            }
            else
            {
                // Aiming, the body turns smoothly to face the aim as drawn (the game turns the pawn to face its target).
                float aimGame, aimDrawn = 0f;
                bool aiming = !lying && FigureWeapons.AimOf(pawn, neverAim, out aimGame, out aimDrawn);
                Animals.Track(pawn, drawLoc, lying && !crawling, 0.9f * size, out yaw, out phase, out gait, aiming ? aimDrawn : (float?)null);
            }

            float ground = 0f;
            bool headOnly = false;
            Matrix4x4 pose;
            Model workTool = null;
            Matrix4x4 workLocal = Matrix4x4.identity;
            float workLean = 0f;
            float workDip = 0f;
            float headLift = 0f;
            if (lying)
            {
                // On their back, head toward the south of the figure's own space before it is turned.
                Quaternion onBack = Quaternion.AngleAxis(-90f, Vector3.right);
                Building_Bed bed = dead || attached ? null : pawn.CurrentBed();
                if (attached)
                {
                    // Lying on its host (Api 13), along its facing, the middle of the body on the anchor.
                    ground = att.at.y;
                    pose = Matrix4x4.Translate(new Vector3(0f, 0.15f * size, 0.55f * size)) * Matrix4x4.Rotate(onBack);
                }
                else if (bed != null)
                {
                    // In a bed the person's place is where their head is, and they lie along the bed.
                    yaw = bed.Rotation.AsAngle;
                    ground = Poses.SleeperHeight(pawn, bed);   // the bed's surface, or their slot's (Api 28: a bunk's level)
                    headOnly = ground > 0.05f && bed.def.building != null && !bed.def.building.bed_showSleeperBody;
                    pose = Matrix4x4.Translate(new Vector3(0f, (headOnly ? 0.24f : 0.15f) * size, HeadHeight * size)) * Matrix4x4.Rotate(onBack);
                }
                else if (!dead && holder != null && pawn.ParentHolder is IThingHolderWithDrawnPawn drawnIn)
                {
                    // Lying in a building that shows who is inside (gene extractor, biosculpter pod): on its
                    // couch, along the body angle the game draws them at, centred where the game draws them.
                    yaw = drawnIn.HeldPawnBodyAngle + 180f;
                    ground = Forms.HeightOf(holder);
                    float swayAngle;
                    Vector3 swayShift;
                    if (holder is Building_HoldingPlatform && HoldingChains.Sway(pawn, out swayAngle, out swayShift))
                    {
                        yaw += swayAngle;   // straining against the chains, as the game moves its sprite
                        drawLoc += swayShift;
                    }
                    pose = Matrix4x4.Translate(new Vector3(0f, 0.15f * size, HeadHeight * 0.5f * size)) * Matrix4x4.Rotate(onBack);
                }
                else if (crawling)
                {
                    // Crawling: face down, head first the way they go, chest and head raised as the game's sprite shows it
                    // (Crawl); the game's lurch is in its drawn position. Other mods' turn or shift of the sprite go on top.
                    float turn;
                    Vector3 shift;
                    if (BodyAnims.ReadCrawl(pawn, drawLoc, out shift, out turn))
                    {
                        yaw += turn;
                        drawLoc += shift;
                    }
                    headLift = Crawl.HeadLift;
                    float chest = Crawl.ChestLift;
                    float front = look.upper.halfDepth * look.bodyScale.z * Mathf.Cos(chest * Mathf.Deg2Rad);   // the front of the feet on the ground
                    pose = Matrix4x4.Translate(new Vector3(0f, front * size, -0.55f * size)) * Matrix4x4.Rotate(Quaternion.AngleAxis(90f - chest, Vector3.right));
                }
                else
                {
                    ground = Corpses.RestHeight(pawn);
                    pose = Matrix4x4.Translate(new Vector3(0f, 0.15f * size, 0.55f * size)) * Matrix4x4.Rotate(onBack);
                }
            }
            else if (attached)
            {
                // Standing or sitting on its host (Api 13): no step of its own; without legs the hips go on the anchor.
                pose = Matrix4x4.identity;
                standing = true;
                ground = seated ? seat.Lift : Attachments.Feet(att.a.pose, att.at.y, size, Attachments.Hips * look.bodyScale.y);
            }
            else if (vat != null)
            {
                // Floating still in the middle of the vat.
                ground = Glass.Floor(vat) + rise;
                pose = Matrix4x4.identity;
                Vector3 c = Glass.ContentCentre(holder, vat);
                drawLoc = new Vector3(c.x, drawLoc.y, c.z);
            }
            else if (gibbet != null)
            {
                // Hanging slack: a slight lean, no step, nothing held.
                ground = Forms.HeightOf(gibbet);
                pose = Matrix4x4.Rotate(Quaternion.AngleAxis(5f, Vector3.right));
                Vector3 c = gibbet.TrueCenter();
                drawLoc = new Vector3(c.x, drawLoc.y, c.z);
            }
            else
            {
                // A little waddle and bounce in step.
                float bob = Mathf.Abs(Mathf.Sin(phase)) * 0.035f * size * gait;
                float sway = Mathf.Sin(phase) * 4.5f * gait;
                pose = Matrix4x4.Translate(new Vector3(0f, bob, 0f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(sway, Vector3.forward));
                standing = true;
                if (seated)
                {
                    ground = seat.Lift;   // on the seat (the body squashed below)
                }

                // At work: face the work, lean into each stroke and swing the tool.
                if (IsoRimMod.Settings.workAnimations && WorkAnimations.Animate(pawn, drawLoc, ref yaw, out workTool, out workLocal, out workLean, out workDip))
                {
                    pose = Matrix4x4.Translate(new Vector3(0f, -workDip * size, 0f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(workLean, Vector3.right));
                }

                // A body animation another mod gives the game's sprite (BodyAnims: none, unless a mod patches it).
                pose = BodyAnims.Apply(pawn, drawLoc, yaw, size, pose);
            }

            Vector3 at = new Vector3(drawLoc.x, ground + lift, drawLoc.z);
            Matrix4x4 whole = pose * Matrix4x4.Scale(new Vector3(size, size, size));
            // Seated: the body squashed down onto the seat, the head (and the hands) come down by as much.
            float squash = seated ? seat.Squash : 1f;
            Matrix4x4 bodyPlace = seated ? whole * Matrix4x4.Scale(new Vector3(1f, squash, 1f)) : whole;
            Matrix4x4 headPlace = seated ? whole * Matrix4x4.Translate(new Vector3(0f, -(1f - squash) * Poses.BodyTop, 0f)) : whole;
            if (headLift != 0f)
            {
                headPlace = whole * Matrix4x4.Translate(new Vector3(0f, Poses.BodyTop, 0f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(-headLift, Vector3.right))
                    * Matrix4x4.Translate(new Vector3(0f, -Poses.BodyTop, 0f));   // tipped back about the neck: looking ahead
            }
            if (look.alien != null)
            {
                headPlace = headPlace * look.alien.head;   // a race's head placing (Api 25): everything on the head follows
            }

            // The whole figure is two ready-made meshes (body and head), each a single draw, put
            // together once for this look and the way it is facing.
            int turned = Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 45f) % 8;
            FigureScope.Root(at, yaw);   // (Api.DrawFigure: placed elsewhere)
            FigureParts hidden = FigureParts.None;
            if (FigureHooks.Count > 0)
            {
                // Other mods' attachments (Api.AddFigureHook), placed in this head and body; before the figure is drawn, as
                // they may hide its own parts (Api 21).
                hidden = FigureHooks.Raise(pawn, look, at, yaw, headPlace, bodyPlace, size, lift, lying, crawling, seated || noLegs, dead, !headOnly);
            }
            // (a race: where its face goes, Api 25; an Own race's face on its own head at the face anchor)
            Matrix4x4 headLocal = look.alien != null ? headPlace * look.alien.face : headPlace * Matrix4x4.Scale(new Vector3(look.headWide, 1f, 1f));
            if (FaceDrawers.Count > 0 && !FigureScope.Measuring && (hidden & (FigureParts.Head | FigureParts.Face)) == 0)
            {
                // Where the game leaves the face out, only drawers added with evenWhenHidden are asked (Api 21).
                FaceHiddenBy hiddenBy = FaceDrawers.HiddenBy(look);
                if ((hiddenBy == FaceHiddenBy.None || FaceDrawers.Always > 0)
                    && FaceDrawers.Ask(pawn, look, head, at, yaw, headLocal, lying, crawling, dead, hiddenBy) && hiddenBy == FaceHiddenBy.None)
                {
                    hidden |= FigureParts.Face;   // another mod drew the face (Api 21): IsoRim's own left out (already out when hidden)
                }
            }
            if (!headOnly)
            {
                DynamicModels.DrawParts(Merged(look, false, dead, turned, null, noLegs || (hidden & FigureParts.Legs) != 0, hidden), at, yaw, bodyPlace);
                if (look.decals != null && (hidden & FigureParts.Clothes) == 0)
                {
                    ApparelDecals.Draw(look, at, yaw, bodyPlace);   // other mods' decals on the body garments (Api 10)
                }
            }
            DynamicModels.DrawParts(Merged(look, true, dead, turned, null, headOnly && look.alien != null, hidden), at, yaw, headPlace);
            if (look.alien != null)
            {
                AlienCards.Draw(look, at, yaw, headPlace, bodyPlace, hidden, !headOnly, lying, crawling);   // a race's addons as cards (Api 25)
            }
            if (look.faceDecal && !look.headHidden && look.skull == null && (hidden & (FigureParts.Head | FigureParts.Face)) == 0)
            {
                FaceDecal.Draw(pawn, head, at, yaw, headLocal);
            }
            // The game's wounds, bandages and scars on the body and head (WoundMarks; a body without legs keeps none).
            WoundMarks.DrawPerson(pawn, look, at, yaw, bodyPlace, headPlace,
                !headOnly && !noLegs && (hidden & (FigureParts.Body | FigureParts.Legs)) == 0, !look.headHidden && (hidden & FigureParts.Head) == 0);
            if (look.flames && !dead)
            {
                // A bound model's flame* parts (a torch belt): IsoRim's live flame (ApparelModels).
                ApparelModels.DrawFlames(look, pawn.thingIDNumber, at, yaw, headPlace, bodyPlace, !headOnly, hidden);
            }
            if (workTool != null)
            {
                // The tool is placed in the figure's own space, so it leans and dips with it.
                Matrix4x4 hands = seated ? whole * Matrix4x4.Translate(new Vector3(0f, -(1f - squash) * HandY, 0f)) : whole;
                hands = AlienHands(look, hands);
                DynamicModels.DrawLocal(workTool, at, yaw, hands * workLocal, Color.white, "Wood");
            }

            if (ground <= 0f && lift <= 0f)
            {
                GroundShadows.Blob(at, yaw, (lying ? 0.30f : 0.34f) * size * look.bodyScale.x, (lying ? 0.70f : 0.30f * look.bodyScale.z) * size);
            }
            standing &= !FigureScope.Active;   // a figure drawn for another mod (Api.DrawFigure): without what it holds
            if (standing && pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null)
            {
                // Held in front, at the hands (the game draws it a little in front of the pawn the way it faces).
                Vector3 front = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.forward;
                float reach = CarryReach * size * Mathf.Max(1f, look.bodyScale.z);   // clear of a deeper body
                Carried.Draw(pawn, new Vector3(at.x + front.x * reach, at.y + (CarryHeight + (look.alien != null ? look.alien.handShift : 0f)) * size * squash, at.z + front.z * reach), yaw);
            }
            if (standing)
            {
                Matrix4x4 hands = seated ? whole * Matrix4x4.Translate(new Vector3(0f, -(1f - squash) * HandY, 0f)) : whole;
                hands = AlienHands(look, hands);
                Model upper = look.upper;
                FigureWeapons.Draw(pawn, neverAim, drawLoc, at, yaw, hands, size, upper.halfDepth * look.bodyScale.z,
                    Mathf.Max(-upper.minX, upper.maxX) * look.bodyScale.x);
                if (look.shield != null && (hidden & FigureParts.Shield) == 0 && ApparelModels.ShieldShown(pawn, look))
                {
                    // A bound shield, held on the left forearm (ApparelModels; after FigureWeapons, which notes whether the
                    // game drew its worn picture).
                    ApparelModels.DrawShield(look, at, yaw, hands);
                }
            }
            return true;
        }

        // A standing figure that is not being drawn now (its floor hidden, off screen) posed as Draw would pose it, without
        // drawing anything: only where its aimed weapon would be (FigureWeapons measure: Projectiles.FigureTip), so its shot
        // still starts from that weapon. Standing still (no step), facing its aim as the body turns to it while aiming.
        // False if it would not be drawn as a standing figure.
        public static bool MeasureWeapon(Pawn pawn)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (!st.pawns3D || !st.models3D || pawn == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike || !pawn.Spawned || pawn.Dead
                || pawn.GetPosture() != PawnPosture.Standing || pawn.story == null || !FigureRace(pawn) || Hidden(pawn) || !Corpses.ShownAsModel(pawn)
                || Statues.Colour(pawn).HasValue || head == null)
            {
                return false;
            }
            Look look = LookOf(pawn);
            if (look == null || look.upper == null || look.lower == null)
            {
                return false;
            }
            Vector3 drawLoc = pawn.DrawPos;
            float lift = Carried.LiftOf(pawn, ref drawLoc);
            float size = look.size;
            float aimGame, aimDrawn;
            bool aiming = FigureWeapons.AimOf(pawn, false, out aimGame, out aimDrawn);
            Attachments.Placed att;
            if (Attached(pawn, look, true, false, false, out att))
            {
                // On a host (Api 13), as Draw places it.
                if (att.a.pose == AttachPose.Lie)
                {
                    return false;
                }
                Poses.Seat onHost;
                bool sits = AttachedSeat(att, out onHost);
                float yawOn = aiming && att.a.facing != AttachFacing.Host ? aimDrawn : att.yaw;
                Vector3 atOn = new Vector3(att.at.x, sits ? onHost.Lift : Attachments.Feet(att.a.pose, att.at.y, size, Attachments.Hips * look.bodyScale.y), att.at.z);
                Matrix4x4 wholeOn = Matrix4x4.Scale(new Vector3(size, size, size));
                Matrix4x4 handsOn = sits ? wholeOn * Matrix4x4.Translate(new Vector3(0f, -(1f - onHost.Squash) * HandY, 0f)) : wholeOn;
                handsOn = AlienHands(look, handsOn);
                Rand.PushState();
                try
                {
                    FigureWeapons.Draw(pawn, false, new Vector3(att.at.x, drawLoc.y, att.at.z), atOn, yawOn, handsOn, size, look.upper.halfDepth * look.bodyScale.z,
                        Mathf.Max(-look.upper.minX, look.upper.maxX) * look.bodyScale.x, true);
                }
                finally
                {
                    Rand.PopState();
                }
                return true;
            }
            Poses.Seat seat = default(Poses.Seat);
            bool seated = lift <= 0f && Poses.Seated(pawn, ref drawLoc, out seat);
            float yaw = aiming ? aimDrawn : pawn.Rotation.AsAngle;
            Matrix4x4 pose = BodyAnims.Apply(pawn, drawLoc, yaw, size, Matrix4x4.identity);
            Vector3 at = new Vector3(drawLoc.x, (seated ? seat.Lift : 0f) + lift, drawLoc.z);
            Matrix4x4 whole = pose * Matrix4x4.Scale(new Vector3(size, size, size));
            float squash = seated ? seat.Squash : 1f;
            Matrix4x4 hands = seated ? whole * Matrix4x4.Translate(new Vector3(0f, -(1f - squash) * HandY, 0f)) : whole;
            hands = AlienHands(look, hands);
            Model upper = look.upper;
            // (it may run in a game tick, for a shot's flash: whatever the game's drawing code rolls stays out of the game's
            // random numbers)
            Rand.PushState();
            try
            {
                FigureWeapons.Draw(pawn, false, drawLoc, at, yaw, hands, size, upper.halfDepth * look.bodyScale.z,
                    Mathf.Max(-upper.minX, upper.maxX) * look.bodyScale.x, true);
            }
            finally
            {
                Rand.PopState();
            }
            return true;
        }

        // Attached to a host (Api 13, Attachments): where and how, with the aim as drawn for the figure's own turn (drawing:
        // from Draw this frame). may: the figure could be posed on a host now (alive, not caged or in a vat).
        private static bool Attached(Pawn pawn, Look look, bool may, bool neverAim, bool drawing, out Attachments.Placed att)
        {
            att = default(Attachments.Placed);
            if (!may || !Attachments.Any)
            {
                return false;
            }
            float aimGame, aimDrawn;
            bool aiming = FigureWeapons.AimOf(pawn, neverAim, out aimGame, out aimDrawn);
            return Attachments.Place(pawn, drawing, aiming ? aimDrawn : (float?)null, out att);
        }

        // AttachPose.Sit: seated on the anchor as on a chair (the body squashed as on a seat).
        private static bool AttachedSeat(Attachments.Placed att, out Poses.Seat seat)
        {
            seat = default(Poses.Seat);
            if (att.a.pose != AttachPose.Sit)
            {
                return false;
            }
            seat.height = att.at.y;
            seat.amount = 1f;
            return true;
        }

        // The gibbet cage holding this dead person's corpse, while the cage is drawn as its model; else null.
        public static Building_GibbetCage GibbetOf(Pawn pawn)
        {
            Corpse corpse = pawn.Corpse;
            Building_GibbetCage cage = corpse != null ? corpse.ParentHolder as Building_GibbetCage : null;
            return cage != null && cage.Spawned && View3D.Ready && Forms.For(cage.def).form == Form3D.Model ? cage : null;
        }

        // For the harness (probe entitycheck): the Anomaly parts of a person's figure.
        public static string Report(Pawn pawn)
        {
            if (pawn.story == null || !FigureRace(pawn))
            {
                return "no figure (" + (pawn.def != null ? pawn.def.defName : "?") + ")";
            }
            Look l = Work(pawn);
            Func<Model, string> n = m => m == null ? "-" : (m == head ? "person_head" : (m == face ? "person_face" : m.id.ToString()));
            return "race " + pawn.def.defName + ", mutant " + (pawn.IsMutant ? pawn.mutant.Def.defName : "none") + ", head " + (pawn.story.headType != null ? pawn.story.headType.defName : "?")
                + ", hair " + (pawn.story.hairDef != null ? pawn.story.hairDef.defName : "?") + " -> " + (HairParts.Hair(pawn.story.hairDef) ?? "none")
                + (l.hair != null ? "" : " (not drawn)") + (l.hairTop != null ? " +bun" : "")
                + ", beard " + (pawn.style != null && pawn.style.beardDef != null ? pawn.style.beardDef.defName : "?") + " -> "
                + (pawn.style != null ? HairParts.Beard(pawn.style.beardDef) ?? "none" : "none") + (l.beard != null ? "" : " (not drawn)") + ", body " + (pawn.story.bodyType != null ? pawn.story.bodyType.defName : "?")
                + ", ownHead " + (l.ownHead != null) + ", ownFace " + (l.ownFace != null) + ", eyes " + (l.eyeLeft != null) + "/" + (l.eyeRight != null)
                + ", scars " + (l.scars != null) + ", overlay " + (l.skinOverlay != null) + ", barbs " + (l.barbs != null) + ", plating " + (l.plating != null)
                + ", ownHair " + l.ownHair + ", hat " + (l.hatKind ?? "-") + ", faceHidden " + l.faceHidden + ", headHidden " + l.headHidden + ", skips " + SkipNames(VanillaSkips(pawn)) + ", skin " + l.skin + ", faceTint " + l.faceTint + ", drawn " + Patches.DrawnAsModel(pawn) + AlienFigures.Report(l);
        }

        // The races drawn as figures: people, and creepjoiners (people in all but race).
        public static bool FigureRace(Pawn pawn)
        {
            return AlienFigures.Drawn3D(pawn);   // people, creepjoiners, and races an alien pack makes 3D (Api 25)
        }

        // People who are invisible must stay so: they keep their sprite, which the game hides.
        public static bool Hidden(Pawn pawn)
        {
            if (pawn.health == null || pawn.health.hediffSet == null)
            {
                return false;
            }
            List<Hediff> all = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].def != null && all[i].def.defName != null && all[i].def.defName.IndexOf("Invisib", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        internal static Model Part(string name)
        {
            Model m;
            if (models.TryGetValue(name, out m))
            {
                return m;
            }
            m = IsoRimMod.RootDir != null ? ModelLibrary.LoadFile(Path.Combine(IsoRimMod.RootDir, "Models", name + ".obj")) : null;
            models[name] = m;
            return m;
        }

        // Every look worked out again the next time it is drawn (Api 25: the race settings or rules changed).
        internal static void RefreshAll()
        {
            foreach (Look l in looks.Values)
            {
                l.frame = int.MinValue / 2;
            }
        }

        // A race's hands (Api 25, an Own race's handLeft / handRight anchors): the held weapon, work tool and carried thing
        // moved up or down with them; people unchanged.
        private static Matrix4x4 AlienHands(Look look, Matrix4x4 hands)
        {
            return look.alien != null && look.alien.handShift != 0f ? hands * Matrix4x4.Translate(new Vector3(0f, look.alien.handShift, 0f)) : hands;
        }

        // Api.RefreshApparelDecals: the person's look is worked out again the next time it is drawn.
        internal static void Refresh(Pawn pawn)
        {
            Look look;
            if (pawn != null && looks.TryGetValue(pawn.thingIDNumber, out look))
            {
                look.frame = int.MinValue / 2;
            }
        }

        // For the harness (ProbeApi9Check): the person's look as last worked out, or null.
        internal static Look LastLook(Pawn pawn)
        {
            Look look;
            return pawn != null && looks.TryGetValue(pawn.thingIDNumber, out look) ? look : null;
        }

        private static Look LookOf(Pawn pawn)
        {
            Look look;
            int frame = Time.frameCount;
            if (hairUnderHatsSeen != IsoRimMod.Settings.hairUnderHats)
            {
                // The option was switched: every look is worked out again at once (changed heads get new meshes).
                hairUnderHatsSeen = IsoRimMod.Settings.hairUnderHats;
                foreach (Look l in looks.Values)
                {
                    l.frame = int.MinValue / 2;
                }
            }
            // Worked out again at once on getting into or out of a bed that hides headgear.
            if (looks.TryGetValue(pawn.thingIDNumber, out look) && frame - look.frame < RefreshFrames && look.bedHidesHat == BedHidesHeadgear(pawn))
            {
                return look;
            }
            Look old = look;
            if (looks.Count > 600)
            {
                foreach (Look l in looks.Values)
                {
                    l.Forget();
                }
                looks.Clear();   // people long gone; the ones still about are worked out again
                old = null;
            }
            try
            {
                look = Work(pawn);
                if (old != null)
                {
                    if (look != null && old.SameAs(look))
                    {
                        old.decals = look.decals;   // (asked anew each time: ApparelDecals)
                        old.alien = look.alien;       // (the same placing; its addon cards anew: AlienCards)
                        look = old;   // nothing has changed: keep the meshes already made
                    }
                    else
                    {
                        old.Forget();
                    }
                }
            }
            catch (Exception e)
            {
                look = null;
                Log.ErrorOnce("[IsoRim] Working out what a person looks like failed: " + e, 93611090);
            }
            if (look != null)
            {
                // Spread the refreshes out so a crowd is not all worked out on the same frame.
                look.frame = frame - (pawn.thingIDNumber & 31);
                looks[pawn.thingIDNumber] = look;
            }
            return look;
        }

        private static bool Has(string text, string word)
        {
            return text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Look Work(Pawn pawn)
        {
            Look look = new Look();
            Pawn_StoryTracker story = pawn.story;

            // Build.
            string build = BuildOf(story.bodyType);
            AlienLook alien = AlienFigures.Prepare(pawn, ref build);   // a race drawn as a 3D figure (Api 25); null for people
            look.alien = alien;
            look.shoulders = Shoulders(build);
            look.bodyScale = alien != null ? AlienFigures.BodyScale(alien) : BodyScales.For(story.bodyType);
            float grown = 1f;
            if (pawn.ageTracker != null && pawn.ageTracker.CurLifeStage != null)
            {
                grown = pawn.ageTracker.CurLifeStage.bodySizeFactor;
            }
            look.size = SizeFor(grown) * (alien != null ? alien.scale : 1f);

            // Head, hair and beard.
            string headName = story.headType != null ? (story.headType.defName ?? "") : "";
            look.headWide = Has(headName, "Narrow") ? 0.9f : (Has(headName, "Wide") ? 1.07f : 1f);
            // Vanilla's eyes are part of the head art: Female_* heads have the upturned lash, all others the male eye.
            look.femaleEyes = headName.StartsWith("Female", StringComparison.OrdinalIgnoreCase);
            look.skin = Corpses.Skin(pawn, story.SkinColor);   // rotting: the game's rotten colour
            look.hairColor = story.HairColor;
            string hairKind = HairParts.Hair(story.hairDef);
            string beardKind = pawn.style != null ? HairParts.Beard(pawn.style.beardDef) : null;

            string hatKind = Dress(look, build, alien != null ? AlienFigures.Worn(pawn, alien) : (pawn.apparel != null ? pawn.apparel.WornApparel : null), look.skin, null);

            // Headgear is left out where the game leaves it out (in a bed that hides the sleeper's body); then
            // hair, beard and eyes show under it exactly when the game draws them (its own skip flags).
            look.bedHidesHat = BedHidesHeadgear(pawn);
            PawnDrawParms parms = DrawParms(pawn);
            if (!PawnRenderNodeWorker_Apparel_Head.HeadgearVisible(parms))
            {
                hatKind = null;
                look.hat = null;
                look.visor = false;
                look.mouthWear = null;
                look.eyeWear = null;
                look.neckWear = null;
            }
            ulong skip = VanillaSkips(pawn, parms);
            // A garment that hides the whole head (skip flag Head: the burka): no head, a closed covering in its colour.
            Apparel hidesHead = (skip & RenderSkipFlagDefOf.Head) != 0 ? HeadHider(pawn) : null;
            if (hidesHead != null)
            {
                look.headHidden = true;
                look.hat = Part("person_hat_burka");
                look.hatColor = ApparelColour(hidesHead);
                look.visor = false;
                look.mouthWear = null;
                look.eyeWear = null;
                look.bindHides = 0;
                hatKind = "burka";
            }
            if (look.hat == null)
            {
                look.bindHides = 0;   // a bound hat hides only while it is drawn
            }
            look.hatKind = hatKind;
            look.hairShown = !Skips(skip, RenderSkipFlagDefOf.Hair) || HairUnderHat(pawn, skip);
            if (hairKind != null && look.hairShown)
            {
                look.hair = Part("person_hair_" + hairKind);
                if (HairParts.Exists("person_hair_" + hairKind + "_bun"))
                {
                    look.hairTop = Part("person_hair_" + hairKind + "_bun");
                }
            }
            if (beardKind != null && !Skips(skip, RenderSkipFlagDefOf.Beard))
            {
                look.beard = Part("person_beard_" + beardKind);
            }
            look.faceHidden = (skip & RenderSkipFlagDefOf.Eyes) != 0;
            if (look.bindHides != 0)
            {
                // What a bound hat hides besides the game's own rules (ApparelModelDef.hides).
                if ((look.bindHides & ApparelModels.HideHair) != 0)
                {
                    look.hair = null;
                    look.hairTop = null;
                    look.hairShown = false;
                }
                if ((look.bindHides & ApparelModels.HideBeard) != 0)
                {
                    look.beard = null;
                }
                if ((look.bindHides & ApparelModels.HideFace) != 0)
                {
                    look.faceHidden = true;
                }
            }
            Anomaly(pawn, look, build, headName);
            look.faceDecal = !look.faceHidden && look.ownFace == null && look.ownHead == null && FaceDecal.Wanted(pawn);
            if (Corpses.Bones(pawn))
            {
                Bones(look, build);
            }
            if (ApparelDecals.Count > 0)
            {
                look.decals = ApparelDecals.Collect(pawn, look);   // other mods' decals on the body garments (Api 10)
            }
            if (alien != null)
            {
                AlienFigures.Finish(pawn, look, alien);   // its own models, eyes, addon models and cards (Api 25)
            }
            return look;
        }

        // Dried to bones, as the game draws it (PawnRenderNode_Body: the body type's bodyDessicatedGraphicPath,
        // Male for Male/Fat/Hulk and Thin for Female/Thin; PawnRenderNode_Head: the Skull head; hair, beard,
        // tattoos and the face left out, every worn garment and headgear still drawn): the skeleton's upper and
        // lower parts where no garment covers that part of the body (Models/skeleton_<male|thin>_<upper|lower>.obj,
        // one size, not body-scaled), the skull (Models/person_skull.obj) for the head. Children take the thin one
        // at their size. Clothing keeps its own shape over the bones.
        private static void Bones(Look look, string build)
        {
            Model skull = Part("person_skull");
            string bones = build == "male" || build == "fat" || build == "hulk" ? "male" : "thin";
            Model upper = look.upperWorn ? null : Part("skeleton_" + bones + "_upper");
            Model lower = look.lowerWorn ? null : Part("skeleton_" + bones + "_lower");
            if (skull == null || (!look.upperWorn && upper == null) || (!look.lowerWorn && lower == null))
            {
                return;   // models missing: the person as they were
            }
            if (upper != null)
            {
                look.upper = upper;
                look.upperBones = true;
            }
            if (lower != null)
            {
                look.lower = lower;
                look.lowerBones = true;
            }
            look.skull = skull;
            look.ownHead = null;
            look.ownFace = null;
            look.eyeLeft = null;
            look.eyeRight = null;
            look.scars = null;
            look.skinOverlay = null;
            look.hair = null;
            look.hairTop = null;
            look.ownHair = false;
            look.beard = null;
            look.faceHidden = true;
        }

        // The game's own choice of what to leave out of a person's head (PawnRenderTree.AdjustParms: each worn
        // apparel's renderSkipFlags, or for apparel without them Hair under an UpperHead piece and Hair, Beard and
        // Eyes under a FullHead piece, the eyes put back for facings in forceEyesVisibleForRotations), as the
        // person is on the map facing south (the side the figure's face is on), in their bed if any (no flags at all
        // where the bed hides headgear). Patches to it apply too.
        private delegate void AdjustParmsCall(PawnRenderTree tree, ref PawnDrawParms parms);
        private static AdjustParmsCall adjustParms;
        private static bool adjustParmsLooked;

        internal static ulong VanillaSkips(Pawn pawn)
        {
            return VanillaSkips(pawn, DrawParms(pawn));
        }

        // The game's draw parameters that matter for headgear (PawnRenderer.GetDrawParms on the map): the person,
        // dead or not, posture and the bed they lie in, facing south.
        private static PawnDrawParms DrawParms(Pawn pawn)
        {
            PawnDrawParms parms = PawnDrawParms.Default;
            parms.pawn = pawn;
            parms.dead = pawn.Dead;
            parms.posture = pawn.GetPosture();
            parms.bed = pawn.CurrentBed();
            return parms;
        }

        // True while the person lies in a bed that hides headgear (PawnRenderNodeWorker_Apparel_Head.HeadgearVisible:
        // a bed without bed_showSleeperBody).
        internal static bool BedHidesHeadgear(Pawn pawn)
        {
            if (pawn.Dead || pawn.GetPosture() == PawnPosture.Standing)
            {
                return false;
            }
            Building_Bed bed = pawn.CurrentBed();
            return bed != null && bed.def.building != null && !bed.def.building.bed_showSleeperBody;
        }

        // The worn garment that hides the whole head (renderSkipFlags Head), or null.
        private static Apparel HeadHider(Pawn pawn)
        {
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            for (int i = 0; worn != null && i < worn.Count; i++)
            {
                if (worn[i].def.apparel != null && worn[i].def.apparel.renderSkipFlags.NotNullAndContains(RenderSkipFlagDefOf.Head))
                {
                    return worn[i];
                }
            }
            return null;
        }

        private static ulong VanillaSkips(Pawn pawn, PawnDrawParms parms)
        {
            if (!adjustParmsLooked)
            {
                adjustParmsLooked = true;
                MethodInfo m = AccessTools.Method(typeof(PawnRenderTree), "AdjustParms");
                try
                {
                    adjustParms = m != null ? (AdjustParmsCall)Delegate.CreateDelegate(typeof(AdjustParmsCall), null, m) : null;
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Can't use the game's headgear rule: " + e.Message, 93611091);
                }
            }
            PawnRenderTree tree = pawn.Drawer != null && pawn.Drawer.renderer != null ? pawn.Drawer.renderer.renderTree : null;
            if (adjustParms == null || tree == null)
            {
                return 0;
            }
            adjustParms(tree, ref parms);
            return parms.skipFlags;
        }

        // For Report: the names of the set skip flags.
        private static string SkipNames(ulong skip)
        {
            List<string> names = new List<string>();
            foreach (RenderSkipFlagDef f in DefDatabase<RenderSkipFlagDef>.AllDefsListForReading)
            {
                if (f != RenderSkipFlagDefOf.None && (skip & f) != 0)
                {
                    names.Add(f.defName);
                }
            }
            return names.Count > 0 ? string.Join("+", names) : "none";
        }

        private static bool hairUnderHatsSeen;

        // Option hairUnderHats: hair is shown (and tucked in, HairTuck) although the game hides it, when every worn item
        // that hides it does so only by the game's default for headgear on the upper head. Items with their own skip
        // flags (a mod's choice), headgear over the full head and anything that hides the whole head keep hiding it.
        private static bool HairUnderHat(Pawn pawn, ulong skip)
        {
            if (!IsoRimMod.Settings.hairUnderHats || (skip & RenderSkipFlagDefOf.Head) != 0 || pawn.apparel == null)
            {
                return false;
            }
            bool upper = false;
            List<Apparel> worn = pawn.apparel.WornApparel;
            for (int i = 0; i < worn.Count; i++)
            {
                ApparelProperties props = worn[i].def.apparel;
                if (props == null)
                {
                    continue;
                }
                if (props.renderSkipFlags != null)
                {
                    if (props.renderSkipFlags.Contains(RenderSkipFlagDefOf.Hair))
                    {
                        return false;
                    }
                    continue;
                }
                if (props.bodyPartGroups == null)
                {
                    continue;
                }
                if (props.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead))
                {
                    return false;
                }
                upper |= props.bodyPartGroups.Contains(BodyPartGroupDefOf.UpperHead);
            }
            return upper;
        }

        // Hair or beard is left out when its own flag is set, or Head (both hang under the head).
        private static bool Skips(ulong skip, RenderSkipFlagDef flag)
        {
            return (skip & flag) != 0 || (skip & RenderSkipFlagDefOf.Head) != 0;
        }

        // Ghoul, shambler and creepjoiner parts (Anomaly), on top of what Work found.
        private static void Anomaly(Pawn pawn, Look look, string build, string headName)
        {
            if (pawn.IsGhoul)
            {
                string headKind = headName.StartsWith("Ghoul_") ? headName.Substring(6).ToLowerInvariant() : "normal";
                if (headKind != "normal" && headKind != "heavy" && headKind != "narrow" && headKind != "wide")
                {
                    headKind = "normal";
                }
                look.ownHead = Part("ghoul_head_" + headKind);
                if (look.ownHead != null)
                {
                    string hairName = pawn.story.hairDef != null ? (pawn.story.hairDef.defName ?? "") : "";
                    Model hair = hairName.StartsWith("Ghoul_") && look.hairShown ? Part("hairs_" + hairName.Substring(6).ToLowerInvariant() + "_" + headKind) : null;
                    if (hair != null)
                    {
                        look.hair = hair;
                        look.ownHair = true;
                    }
                    look.beard = null;
                }
                look.skinOverlay = Part("ghoul_" + build + "_body_overlay");
                HediffSet hediffs = pawn.health != null ? pawn.health.hediffSet : null;
                if (hediffs != null && HediffDefOf.GhoulBarbs != null && hediffs.HasHediff(HediffDefOf.GhoulBarbs))
                {
                    look.barbs = Part("implants_" + build + "_barbs");
                }
                if (hediffs != null && HediffDefOf.GhoulPlating != null && hediffs.HasHediff(HediffDefOf.GhoulPlating))
                {
                    look.plating = Part("implants_" + build + "_plating");
                }
                return;
            }
            if (pawn.IsShambler)
            {
                look.scars = Part("shambler_head_scars");
                if (!look.upperWorn && !look.lowerWorn && look.coat == null)
                {
                    look.skinOverlay = Part("shambler_" + build + "_body_overlay");
                }
                return;
            }
            if (pawn.def.defName != "CreepJoiner")
            {
                return;
            }
            string face = null;
            switch (headName)
            {
                case "Leathery_Male": face = "leathery_male_face"; break;
                case "Leathery_Female": face = "leathery_female_face"; break;
                case "DarkScholar_Male": face = "scholar_male_face"; break;
                case "DarkScholar_Female": face = "scholar_female_face"; break;
                case "CultEscapee": face = "cult_escapee_face"; break;
                case "TimelessOne": face = "timeless_face"; break;
            }
            if (face == null)
            {
                return;
            }
            look.ownFace = Part(face);
            // The creases are a darker shade of the skin (the model's crease colour is for a mid skin).
            look.faceTint = new Color(Mathf.Clamp01(look.skin.r * 1.35f), Mathf.Clamp01(look.skin.g * 1.35f), Mathf.Clamp01(look.skin.b * 1.35f));
            if (headName == "CultEscapee")
            {
                // The figure faces +z, so its left eye is on the -x side.
                look.eyeLeft = EyeMissing(pawn, "left") ? null : Part("cult_escapee_eye_negative_x");
                look.eyeRight = EyeMissing(pawn, "right") ? null : Part("cult_escapee_eye_positive_x");
            }
        }

        // True if the person's eye on that side ("left" or "right", by the body part's label) is missing.
        private static bool EyeMissing(Pawn pawn, string side)
        {
            if (pawn.health == null || pawn.health.hediffSet == null || pawn.RaceProps == null || pawn.RaceProps.body == null)
            {
                return false;
            }
            List<BodyPartRecord> parts = pawn.RaceProps.body.AllParts;
            for (int i = 0; i < parts.Count; i++)
            {
                BodyPartRecord part = parts[i];
                if (part.def == BodyPartDefOf.Eye && part.customLabel != null && part.customLabel.IndexOf(side, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return pawn.health.hediffSet.PartIsMissing(part);
                }
            }
            return false;
        }

        // The mod's body shape ("male", "female", "thin", "hulk", "fat") for one of the game's body types.
        internal static string BuildOf(BodyTypeDef bodyType)
        {
            string bodyName = bodyType != null ? (bodyType.defName ?? "") : "";
            if (Has(bodyName, "Female"))
            {
                return "female";
            }
            if (Has(bodyName, "Thin") || Has(bodyName, "Child"))
            {
                return "thin";
            }
            if (Has(bodyName, "Hulk"))
            {
                return "hulk";
            }
            if (Has(bodyName, "Fat") || Has(bodyName, "Baby"))
            {
                return "fat";
            }
            return "male";
        }

        // A figure's size for a life stage's body size factor (children are smaller, not thinner).
        internal static float SizeFor(float bodySizeFactor)
        {
            return Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(bodySizeFactor));
        }

        internal static float Shoulders(string build)
        {
            return build == "hulk" ? 1.36f : (build == "male" || build == "fat" ? 1f : 0.84f);
        }

        // What a figure of this build wearing these clothes looks like (also the outfit stands, Stands.cs): the
        // body parts (trousers, shirt, coat or armour, leg armour, shoulder pads), the packs, belts and waist tools
        // (WornGear) and the hat, each in the colour of what it shows; bare is the colour of whatever no clothing
        // covers. Clothing with no 3D part goes into unshown if given. Returns the kind of hat worn, or null.
        internal static string Dress(Look look, string build, List<Apparel> worn, Color bare, List<Apparel> unshown)
        {
            // Clothes. Each layer over the body gives its colour to the part it covers; the
            // outermost one wins. A shell layer becomes a coat worn over the rest.
            look.lower = Part("person_" + build + "_lower");
            look.upper = Part("person_" + build + "_upper");
            look.upperColor = bare;
            look.lowerColor = bare;
            int upperLayer = -1;
            int lowerLayer = -1;
            string hatKind = null;
            string coatKind = null;
            string coatFallback = null;
            Under coatUnder = Under.Hidden;
            string legArmourKind = null;
            string vestKind = null;
            string shellArmourKind = null;
            string shellArmourFile = null;
            Color vestColour = Color.white;
            Color shellArmourColour = Color.white;
            List<Apparel> gearWorn = null;
            string midShape = null;
            string skinShape = null;
            Color midShapeColour = Color.white;
            Color skinShapeColour = Color.white;
            Color skinShapeTint = Color.white;
            bool skinUnder = false;
            bool skinBare = false;
            look.mouthWear = null;
            look.eyeWear = null;
            look.neckWear = null;
            look.mid = null;
            look.shirtWear = null;
            look.overCoat = null;
            look.shield = null;
            look.shieldBinding = null;
            look.bindHides = 0;
            // Apparel bound to a model of its own (ApparelModelDef, ApparelModels): the bound hat, coat, middle and on-skin
            // shapes (null: none, or IsoRim's own), and bound belt gear.
            ApparelModels.Binding hatBound = null;
            Model hatBoundModel = null;
            Model coatBoundModel = null;
            Model midBoundModel = null;
            Model skinBoundModel = null;
            List<Apparel> beltBound = null;
            // A garment the game draws over the Shell layer (AboveShell), bound or with a shape of IsoRim's own: over the
            // coat or armour, as the game draws it.
            Model overModel = null;
            Color overColour = Color.white;
            // The items behind the coat (bound or IsoRim's own shape), shell armour, flak vest, over-coat, middle and on-skin
            // shapes (ApparelDecals).
            Apparel coatFrom = null, shellArmourFrom = null, vestFrom = null, overFrom = null, midFrom = null, skinFrom = null;
            look.coatFrom = look.overCoatFrom = look.midFrom = look.shirtWearFrom = null;
            if (worn != null)
            {
                for (int i = 0; i < worn.Count; i++)
                {
                    Apparel a = worn[i];
                    ApparelProperties props = a.def != null ? a.def.apparel : null;
                    if (props == null)
                    {
                        continue;
                    }
                    string name = a.def.defName ?? "";
                    ApparelModels.Binding bound = ApparelModels.For(a.def);
                    Model boundModel = bound != null ? bound.ModelFor(build) : null;
                    if (boundModel != null)
                    {
                        // Its own model from a binding: in its slot, in its colour (stuff faces: ApparelModels.Tint).
                        Color tint = ApparelModels.Tint(bound, a);
                        switch (bound.slot)
                        {
                            case ApparelModels.Slot.Head:
                                if (OnLayer(props, "Overhead") || hatKind == null)
                                {
                                    hatKind = "bound:" + bound.def.defName;
                                    hatBound = bound;
                                    hatBoundModel = boundModel;
                                    look.hatColor = tint;
                                }
                                continue;
                            case ApparelModels.Slot.Face:
                                look.mouthWear = boundModel;
                                look.mouthColor = tint;
                                continue;
                            case ApparelModels.Slot.Eyes:
                                look.eyeWear = boundModel;
                                look.eyeColor = tint;
                                continue;
                            case ApparelModels.Slot.Neck:
                                look.neckWear = boundModel;
                                look.neckColor = tint;
                                continue;
                            case ApparelModels.Slot.Belt:
                                (beltBound ?? (beltBound = new List<Apparel>())).Add(a);
                                continue;
                            case ApparelModels.Slot.Shield:
                                look.shield = boundModel;
                                look.shieldColor = tint;
                                look.shieldBinding = bound;
                                continue;
                            case ApparelModels.Slot.Body:
                            {
                                bool torsoB = Covers(props, "Torso");
                                bool legsB = Covers(props, "Legs");
                                if (OnLayer(props, "Shell"))
                                {
                                    // A coat of its own shape (as CoatShapes): over everything, its own shoulders.
                                    coatKind = null;
                                    coatBoundModel = boundModel;
                                    coatUnder = bound.under;
                                    look.coatColor = tint;
                                    coatFrom = a;
                                    continue;
                                }
                                if (AboveShell(props))
                                {
                                    // Drawn by the game over the coat (VE Framework's VFEC_OuterShell): over it here too.
                                    overModel = boundModel;
                                    overColour = tint;
                                    overFrom = a;
                                    if (legsB)
                                    {
                                        look.lowerColor = tint;
                                        lowerLayer = 4;
                                    }
                                    continue;
                                }
                                if (OnLayer(props, "OnSkin"))
                                {
                                    // The shirt itself (as an on-skin MidShape): its shape over the shirt part, which takes its colour.
                                    skinShape = null;
                                    skinBoundModel = boundModel;
                                    skinShapeColour = tint;
                                    skinFrom = a;
                                    if (torsoB && 0 > upperLayer)
                                    {
                                        look.upperColor = tint;
                                        upperLayer = 0;
                                    }
                                    if (legsB && 0 > lowerLayer)
                                    {
                                        look.lowerColor = tint;
                                        lowerLayer = 0;
                                    }
                                    continue;
                                }
                                // A middle layer (as a Middle MidShape): its shape over the shirt, which keeps its own colour.
                                midShape = null;
                                midBoundModel = boundModel;
                                midShapeColour = tint;
                                midFrom = a;
                                if (legsB && 1 > lowerLayer)
                                {
                                    look.lowerColor = tint;
                                    lowerLayer = 1;
                                }
                                continue;
                            }
                        }
                    }
                    if (WornGear.For(a.def) != null)
                    {
                        (gearWorn ?? (gearWorn = new List<Apparel>())).Add(a);
                        continue;
                    }
                    Color colour = ApparelColour(a);
                    if (PaintedZones(name))
                    {
                        colour = a.DrawColor;   // its own model paints its fixed zones (gold): the item's colour, white when undyed
                    }
                    bool torso = Covers(props, "Torso");
                    bool legs = Covers(props, "Legs");
                    bool onHead = Covers(props, "FullHead") || Covers(props, "UpperHead");
                    bool armour = Has(name, "Armor") || Has(name, "Armour") || Has(name, "Flak") || Has(name, "Plate") || Has(name, "Cataphract")
                        || Has(name, "Marine") || Has(name, "Recon") || Has(name, "Phoenix") || Has(name, "Prestige");

                    string faceFile;
                    if (!onHead && !torso && !legs && OwnFaceWear.TryGetValue(name, out faceFile))
                    {
                        // A face, eye or neck item with its own model: its own slot, so it shows with a hat.
                        Model m = Part("person_hat_" + faceFile);
                        if (OnLayer(props, "EyeCover") || Covers(props, "Eyes"))
                        {
                            look.eyeWear = m;
                            look.eyeColor = colour;
                        }
                        else if (Covers(props, "Neck"))
                        {
                            look.neckWear = m;
                            look.neckColor = colour;
                        }
                        else
                        {
                            look.mouthWear = m;
                            look.mouthColor = colour;
                        }
                        continue;
                    }

                    string armourKind = onHead && !torso && !legs ? null : ArmourKind(name);
                    if (armourKind == "flakpants")
                    {
                        legArmourKind = armourKind;
                        look.legArmourColor = colour;
                        continue;
                    }
                    if (armourKind == "flakvest")
                    {
                        vestColour = colour;
                        vestKind = armourKind;
                        vestFrom = a;
                        continue;
                    }
                    if (armourKind != null && torso)
                    {
                        shellArmourKind = armourKind;
                        shellArmourFrom = a;
                        shellArmourFile = ArmourVariants.TryGetValue(name, out string variant) ? variant : armourKind;
                        shellArmourColour = colour;
                        if (legs && (armourKind == "marine" || armourKind == "cataphract" || armourKind == "recon"))
                        {
                            look.lowerColor = colour;
                            lowerLayer = 3;
                        }
                        continue;
                    }

                    if (onHead && !torso && !legs)
                    {
                        if (OnLayer(props, "Overhead") || hatKind == null)
                        {
                            hatKind = HatKind(name, Covers(props, "FullHead"));
                            hatBound = null;
                            hatBoundModel = null;
                            look.hatColor = colour;
                        }
                        continue;
                    }

                    MidShape mid;
                    if (MidShapes.TryGetValue(name, out mid))
                    {
                        // Its own thin over-shape on the shirt (Models/person_<build>_<file>.obj, painted zones: the item's
                        // colour, white when undyed). A Middle garment leaves the shirt its own colour; a shirt still colours
                        // the shirt part (below; the ruffle shirt, which lists only Arms and Neck, after the loop).
                        if (mid.onSkin)
                        {
                            skinShape = mid.file;
                            skinBoundModel = null;
                            skinFrom = a;
                            skinShapeColour = a.DrawColor;
                            skinShapeTint = colour;
                            skinUnder = mid.under;
                            skinBare = mid.bare;
                        }
                        else if (AboveShell(props))
                        {
                            // A shape of IsoRim's own on a layer the game draws over the coat: over it here too.
                            overModel = Part("person_" + build + "_" + mid.file);
                            overColour = a.DrawColor;
                            overFrom = a;
                            continue;
                        }
                        else
                        {
                            midShape = mid.file;
                            midBoundModel = null;
                            midShapeColour = a.DrawColor;
                            midFrom = a;
                            continue;
                        }
                    }
                    else if (!legs && props.wornGraphicPath.NullOrEmpty() && !OnLayer(props, "Shell"))
                    {
                        // Drawn as nothing by the game (no worn graphic: the heavy shield unit), so it colours nothing.
                        unshown?.Add(a);
                        continue;
                    }

                    int layer = OnLayer(props, "Shell") ? 2 : (OnLayer(props, "Middle") ? 1 : (OnLayer(props, "OnSkin") ? 0 : -1));
                    if (layer < 0)
                    {
                        unshown?.Add(a);   // belts and the like
                        continue;
                    }
                    if (layer == 2 && torso)
                    {
                        bool longCoat = legs || Has(name, "Duster") || Has(name, "Parka") || Has(name, "Robe") || Has(name, "Cape") || Has(name, "Coat");
                        coatFallback = longCoat && !armour ? "coat_long" : "coat_short";
                        coatBoundModel = null;
                        coatFrom = a;
                        CoatShape shape;
                        if (CoatShapes.TryGetValue(name, out shape))
                        {
                            // Its own shape paints its own light and dark zones: the item's colour, white when undyed
                            // (not the sprite's mean, which would grey a painted suit). Its own shoulders: no pads.
                            coatKind = shape.file;
                            coatUnder = shape.under;
                            look.coatColor = a.DrawColor;
                        }
                        else
                        {
                            coatKind = coatFallback;
                            coatUnder = Under.Hidden;
                            look.coatColor = colour;
                            look.pads |= armour;
                        }
                        if (armour && legs && layer > lowerLayer)
                        {
                            look.lowerColor = colour;   // armour that reaches the legs
                            lowerLayer = layer;
                        }
                        continue;
                    }
                    if (torso && layer > upperLayer && !(mid.onSkin && mid.bare))   // a bare-shouldered tunic leaves the shirt part bare
                    {
                        look.upperColor = colour;
                        upperLayer = layer;
                        look.pads |= armour;
                    }
                    if (legs && layer > lowerLayer)
                    {
                        look.lowerColor = colour;
                        lowerLayer = layer;
                    }
                }
            }
            // The game draws a shirt with an over-shape as a whole shirt, also the ruffle shirt (no Torso group): it colours
            // the shirt part when nothing else on the skin layer does.
            if (skinShape != null && upperLayer < 0 && !skinBare)
            {
                look.upperColor = skinShapeTint;
                upperLayer = 0;
            }
            // Middle and on-skin over-shapes show where the game shows those layers: with nothing over them, in the
            // open front of a coat (Under.FrontPlate) and under a strap (Under.Whole); they are thin enough to sit
            // inside every coat and strap. A closed coat, a generic stand-in coat or armour hides them. The ruffle
            // shirt's standing collar and full jabot only show with nothing over it (else its flat _under jabot).
            bool layersHidden = shellArmourKind != null || ((coatKind != null || coatBoundModel != null) && coatUnder == Under.Hidden);
            if (!layersHidden)
            {
                if (midBoundModel != null)
                {
                    look.mid = midBoundModel;
                    look.midColor = midShapeColour;
                    look.midFrom = midFrom;
                }
                if (skinBoundModel != null)
                {
                    look.shirtWear = skinBoundModel;
                    look.shirtWearColor = skinShapeColour;
                    look.shirtWearFrom = skinFrom;
                }
                if (midShape != null)
                {
                    look.mid = Part("person_" + build + "_" + midShape);
                    look.midColor = midShapeColour;
                    look.midFrom = midFrom;
                }
                if (skinShape != null)
                {
                    bool covered = coatKind != null || coatBoundModel != null || vestKind != null || midShape != null || midBoundModel != null;
                    look.shirtWear = Part("person_" + build + "_" + skinShape + (covered && skinUnder ? "_under" : ""));
                    look.shirtWearColor = skinShapeColour;
                    look.shirtWearFrom = skinFrom;
                }
            }

            // Armour the mod has a shape for takes the place of a coat (it is worn on the same
            // outer layer); a flak vest shows whole when nothing is worn over it or only a strap, and
            // its front plate in the opening of a coat that is open at the front.
            if (shellArmourKind != null)
            {
                look.coat = Part("person_" + build + "_armor_" + shellArmourFile) ?? Part("person_" + build + "_armor_" + shellArmourKind);
                look.coatColor = shellArmourColour;
                look.pads = false;
                look.coatFrom = shellArmourFrom;
            }
            else if (coatBoundModel != null)
            {
                // A bound coat: a flak vest under it shows as its binding says (no strap-over-vest shape of its own: the
                // whole vest under it).
                look.coat = coatBoundModel;
                look.coatFrom = coatFrom;
                if (vestKind != null && coatUnder == Under.FrontPlate)
                {
                    look.underCoat = NoOutline(Part("person_" + build + "_armor_" + vestKind + "_front"));
                    look.underCoatColor = vestColour;
                }
                else if (vestKind != null && coatUnder == Under.Whole)
                {
                    look.underCoat = Part("person_" + build + "_armor_" + vestKind);
                    look.underCoatColor = vestColour;
                }
            }
            else if (coatKind != null)
            {
                Model overVest = vestKind != null && coatUnder == Under.Whole ? Part("person_" + build + "_" + coatKind + "_overvest") : null;
                look.coat = overVest ?? Part("person_" + build + "_" + coatKind) ?? Part("person_" + build + "_" + coatFallback);
                look.coatFrom = coatFrom;
                if (vestKind != null && coatUnder == Under.FrontPlate)
                {
                    // The game draws the vest under the coat, so its plate shows in the open front; only that plate,
                    // seated inside the coat, and with no outline (its edge would draw over the coat).
                    look.underCoat = NoOutline(Part("person_" + build + "_armor_" + vestKind + "_front"));
                    look.underCoatColor = vestColour;
                }
                else if (overVest != null)
                {
                    // A strap over a vest: the whole vest, the strap made to go round it (<shape>_overvest).
                    look.underCoat = Part("person_" + build + "_armor_" + vestKind);
                    look.underCoatColor = vestColour;
                }
            }
            else if (vestKind != null)
            {
                look.coat = Part("person_" + build + "_armor_" + vestKind);
                look.coatColor = vestColour;
                look.pads = false;
                look.coatFrom = vestFrom;
            }
            if (legArmourKind != null)
            {
                look.legArmour = Part("person_" + build + "_armor_" + legArmourKind);
            }

            if (hatBoundModel != null)
            {
                look.hat = hatBoundModel;
                look.visor = hatBound.def.visor;
                look.bindHides = hatBound.hides;
            }
            else if (hatKind != null)
            {
                look.hat = Part("person_hat_" + hatKind);
                look.visor = hatKind == "fullhelm";
            }
            if (overModel != null)
            {
                // Over everything the game draws under it: fitted out over what reaches further than the bare figure.
                look.overCoat = FitOver(overModel, look);
                look.overCoatColor = overColour;
                look.overCoatFrom = overFrom;
            }
            look.upperWorn = upperLayer >= 0;
            look.lowerWorn = lowerLayer >= 0;

            // Packs, belts and waist tools, fitted to what is worn under them.
            look.gear.Clear();
            look.gearPlace.Clear();
            look.gearTint.Clear();
            look.gearRigid.Clear();
            look.gearAnchor.Clear();
            for (int i = 0; gearWorn != null && i < gearWorn.Count; i++)
            {
                WornGearDef g = WornGear.For(gearWorn[i].def);
                Color tint;
                Model m = WornGear.ModelFor(g, gearWorn[i], out tint);
                if (m == null)
                {
                    unshown?.Add(gearWorn[i]);
                    continue;
                }
                bool rigid;
                Vector3 anchor;
                Matrix4x4 place = WornGear.Place(g, look, out rigid, out anchor);
                look.gear.Add(m);
                look.gearPlace.Add(place);
                look.gearTint.Add(tint);
                look.gearRigid.Add(rigid);
                look.gearAnchor.Add(anchor);
            }
            for (int i = 0; beltBound != null && i < beltBound.Count; i++)
            {
                // Bound belt gear (ApparelModels): placed by its mount as a WornGearDef would be.
                ApparelModels.Binding b = ApparelModels.For(beltBound[i].def);
                bool rigid;
                Vector3 anchor;
                Matrix4x4 place = WornGear.Place(b.gear, look, out rigid, out anchor);
                look.gear.Add(b.ModelFor(build));
                look.gearPlace.Add(place);
                look.gearTint.Add(ApparelModels.Tint(b, beltBound[i]));
                look.gearRigid.Add(rigid);
                look.gearAnchor.Add(anchor);
            }
            look.flames = ApparelModels.Count > 0 && ApparelModels.Burns(look);
            return hatKind;
        }

        // What clothes dressed onto a look (Dress) put on a figure, without the figure itself: the body parts only where some
        // clothing covers them, then everything worn over them (body: figure space, no body-type scale), and what goes on the
        // head (head: eye cover, mask, hat and its visor; figure space). One list for the outfit stands (Stands) and for apparel
        // lying on the ground (GroundGear), so both show exactly what a colonist wears.
        internal static void ClothesParts(Look look, string build, Action<Model, Matrix4x4, Color> body, Action<Model, Matrix4x4, Color> head)
        {
            if (look.lowerWorn)
            {
                body(look.lower, Matrix4x4.identity, look.lowerColor);
            }
            if (look.upperWorn)
            {
                body(look.upper, Matrix4x4.identity, look.upperColor);
            }
            body(look.shirtWear, Matrix4x4.identity, look.shirtWearColor);   // a shirt's collar, jabot or panels (MidShapes)
            body(look.mid, Matrix4x4.identity, look.midColor);               // a corset, vest or sash over the shirt
            body(look.legArmour, Matrix4x4.identity, look.legArmourColor);
            body(look.underCoat, Matrix4x4.identity, look.underCoatColor);   // a flak vest under an open coat (its front plate) or a strap
            body(look.coat, Matrix4x4.identity, look.coatColor);
            body(look.overCoat, Matrix4x4.identity, look.overCoatColor);     // a tabard or mantle over the coat (AboveShell)
            body(look.neckWear, Matrix4x4.identity, look.neckColor);
            for (int i = 0; i < look.gear.Count; i++)
            {
                // No body-type scale: the gear as on a colonist of that build before it.
                Matrix4x4 place = look.gearRigid[i] ? Matrix4x4.Translate((Vector3)look.gearPlace[i].GetColumn(3) - look.gearAnchor[i]) : look.gearPlace[i];
                body(look.gear[i], place, look.gearTint[i]);
            }
            if (look.pads)
            {
                body(Part("person_pads"), Matrix4x4.Scale(new Vector3(Shoulders(build), 1f, 1.05f)), look.coat != null ? look.coatColor : look.upperColor);
            }
            head(look.eyeWear, Matrix4x4.identity, look.eyeColor);
            head(look.mouthWear, Matrix4x4.identity, look.mouthColor);
            head(look.hat, Matrix4x4.identity, look.hatColor);
            if (look.hat != null && look.visor)
            {
                head(Part("person_visor"), Matrix4x4.identity, Color.white);
            }
        }

        // Outer clothing with its own worn shape (Models/person_<build>_<file>.obj, materials in coats.mtl), and how
        // a flak vest worn under it shows, by vanilla's sprites: in the open front of the duster, lab coat, jacket,
        // royal robe, psyfocus robe and cape (its front plate); whole around the straps (the strap's _overvest shape
        // goes over it); not at all under the parka, robe and burka. The suits and the locust armour take the middle
        // layer, so no vest goes under them. Anything else on the shell layer gets the generic coat_long or coat_short.
        // Children wear the thin shapes (BuildOf), unscaled; the kid parka and kid vacsuit share the adult shapes.
        internal enum Under { Hidden, FrontPlate, Whole }

        internal struct CoatShape
        {
            public string file;
            public Under under;

            public CoatShape(string file, Under under)
            {
                this.file = file;
                this.under = under;
            }
        }

        internal static readonly Dictionary<string, CoatShape> CoatShapes = new Dictionary<string, CoatShape>
        {
            { "Apparel_Duster", new CoatShape("duster", Under.FrontPlate) },
            { "Apparel_LabCoat", new CoatShape("labcoat", Under.FrontPlate) },
            { "Apparel_Jacket", new CoatShape("jacket", Under.FrontPlate) },
            { "Apparel_Parka", new CoatShape("parka", Under.Hidden) },
            { "Apparel_KidParka", new CoatShape("parka", Under.Hidden) },
            { "Apparel_Robe", new CoatShape("robe", Under.Hidden) },
            { "Apparel_RobeRoyal", new CoatShape("robe_royal", Under.FrontPlate) },
            { "Apparel_PsyfocusRobe", new CoatShape("robe_psy", Under.FrontPlate) },
            { "Apparel_Burka", new CoatShape("burka_body", Under.Hidden) },
            { "Apparel_Cape", new CoatShape("cape", Under.FrontPlate) },
            { "Apparel_MechlordSuit", new CoatShape("mechlord", Under.Hidden) },
            { "Apparel_Vacsuit", new CoatShape("vacsuit", Under.Hidden) },
            { "Apparel_VacsuitChildren", new CoatShape("vacsuit", Under.Hidden) },
            { "Apparel_ArmorLocust", new CoatShape("locust", Under.Hidden) },
            { "Apparel_Bandolier", new CoatShape("bandolier", Under.Whole) },
            { "Apparel_BodyStrap", new CoatShape("bodystrap", Under.Whole) },
        };

        // Middle and on-skin garments with a thin over-shape of their own (Models/person_<build>_<file>.obj, midlayer.mtl,
        // tools/make_midlayer.py), drawn on the shirt part. A Middle one (corset, royal vest, eltex vest, sash) leaves the
        // shirt its own colour; an on-skin one (eltex shirt, ruffle shirt) is the shirt: it colours the shirt part and adds
        // its collar, jabot or dark wedges. under: it also has a flat <file>_under shape for under a coat, strap or vest.
        // Shown alone, in an open coat's front and under a strap; hidden under closed coats and armour (Dress).
        // bare: the shape is the whole garment and the shirt part stays bare (the tribal tunics' bare left shoulder).
        // Kid items are made on the thin model (children's build, unscaled); the kid shirt and the pants keep the tint
        // (plain sprite / no worn sprite), as do the basic shirt and pants.
        internal struct MidShape
        {
            public string file;
            public bool onSkin;
            public bool under;
            public bool bare;

            public MidShape(string file, bool onSkin, bool under = false, bool bare = false)
            {
                this.file = file;
                this.onSkin = onSkin;
                this.under = under;
                this.bare = bare;
            }
        }

        internal static readonly Dictionary<string, MidShape> MidShapes = new Dictionary<string, MidShape>
        {
            { "Apparel_Corset", new MidShape("corset", false) },
            { "Apparel_VestRoyal", new MidShape("vest_royal", false) },
            { "Apparel_PsyfocusVest", new MidShape("vest_psy", false) },
            { "Apparel_Sash", new MidShape("sash", false) },
            { "Apparel_PsyfocusShirt", new MidShape("shirt_psy", true) },
            { "Apparel_ShirtRuffle", new MidShape("shirt_ruffle", true, true) },
            { "Apparel_CollarShirt", new MidShape("shirt_collar", true, true) },   // coverage batch 1: collar + placket
            // coverage batch 2 (tools/make_coverage_b2.py)
            { "Apparel_TribalA", new MidShape("tribal", true, false, true) },        // one-shoulder tunic, ragged hem, seam
            { "Apparel_KidTribal", new MidShape("kid_tribal", true, false, true) },  // the same on the child
            { "Apparel_KidRomper", new MidShape("kid_romper", true) },               // ribbed hip band + front seam
        };

        // Armour variants with their own worn shape (Models/person_<build>_armor_<file>.obj, tools/make_coverage_b1.py):
        // the family's shape plus the variant's own parts. The family (ArmourKind) still decides everything else.
        internal static readonly Dictionary<string, string> ArmourVariants = new Dictionary<string, string>
        {
            { "Apparel_ArmorMarineGrenadier", "grenadier" },
            { "Apparel_ArmorMarinePrestige", "marine_prestige" },
            { "Apparel_ArmorCataphractPhoenix", "phoenix" },
            { "Apparel_ArmorCataphractPrestige", "cataphract_prestige" },
            { "Apparel_ArmorReconPrestige", "recon_prestige" },
        };

        // Items whose own model paints its fixed-colour zones (the prestige gold) and leaves the rest to the item's colour:
        // they take DrawColor (white when undyed), like the CoatShapes, not the sprite's mean, which the gold would turn beige.
        // (dyefix 2026-10-25: also the vacsuit helmet and the gas mask, which have no colour of their own until dyed.)
        internal static readonly HashSet<string> PaintedHats = new HashSet<string>
        {
            "Apparel_ArmorMarineHelmetPrestige", "Apparel_ArmorHelmetCataphractPrestige", "Apparel_ArmorHelmetReconPrestige",
            "Apparel_VacsuitHelmet", "Apparel_GasMask",   // dyefix: their shell / rubber is stuff_* (masks.mtl), visor and bands fixed
        };

        internal static bool PaintedZones(string name)
        {
            return ArmourVariants.ContainsKey(name) || PaintedHats.Contains(name);
        }

        // Whether the game draws this apparel over the Shell layer: it sorts worn apparel by the draw order of its last layer
        // (VE Framework's VFEC_OuterShell tabards and mantles; belts, hats and eye covers have their own slots).
        internal static bool AboveShell(ApparelProperties props)
        {
            if (props.layers == null || props.layers.Count == 0 || ApparelLayerDefOf.Shell == null)
            {
                return false;
            }
            ApparelLayerDef last = props.LastLayer;
            return last != null && last.drawOrder > ApparelLayerDefOf.Shell.drawOrder;
        }

        // An over-the-shell shape (look.overCoat), made on the bare figure, fitted over what the look wears under it: each
        // vertex moves out from the figure's middle line by how much further the clothes under it reach than the bare
        // figure (upper and lower) there, by height and direction, plus a small gap. As made when nothing under it reaches
        // further. Kept per shape and clothes under it.
        private const float OverStep = 0.04f;    // height of a bin
        private const int OverRows = 60;         // bins up to 2.4 cells
        private const int OverSectors = 24;      // directions round the middle line
        private const float OverGap = 0.012f;
        private static readonly Dictionary<int, float[]> overReach = new Dictionary<int, float[]>();
        private static readonly Dictionary<string, Model> overFitted = new Dictionary<string, Model>();

        internal static Model FitOver(Model over, Look look)
        {
            Model[] under = { look.shirtWear, look.mid, look.legArmour, look.underCoat, look.coat };
            string key = over.id.ToString();
            bool any = false;
            for (int i = 0; i < under.Length; i++)
            {
                key += "," + (under[i] != null ? under[i].id : 0);
                any |= under[i] != null;
            }
            if (!any)
            {
                return over;
            }
            key += "," + (look.upper != null ? look.upper.id : 0) + "," + (look.lower != null ? look.lower.id : 0);
            Model fitted;
            if (overFitted.TryGetValue(key, out fitted))
            {
                return fitted;
            }
            float[] bare = new float[OverRows * OverSectors];
            float[] worn = new float[OverRows * OverSectors];
            MaxReach(bare, Reach(look.upper));
            MaxReach(bare, Reach(look.lower));
            for (int i = 0; i < under.Length; i++)
            {
                MaxReach(worn, Reach(under[i]));
            }
            float[] push = new float[bare.Length];
            bool moves = false;
            for (int i = 0; i < push.Length; i++)
            {
                float d = worn[i] > 0f ? worn[i] - bare[i] : 0f;
                push[i] = d > 0f ? d + OverGap * Mathf.Min(1f, d / OverGap) : 0f;
                moves |= push[i] > 0f;
            }
            if (!moves)
            {
                overFitted[key] = over;
                return over;
            }
            Func<Vector3, Vector3> move = p =>
            {
                float r = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                if (r <= 1e-4f)
                {
                    return p;
                }
                float d = Sample(push, p.y, Mathf.Atan2(p.z, p.x));
                return new Vector3(p.x + p.x / r * d, p.y, p.z + p.z / r * d);
            };
            int n = over.positions.Length;
            Vector3[] pos = new Vector3[n];
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, maxY = 0f;
            for (int v = 0; v < n; v++)
            {
                Vector3 p = move(over.positions[v]);
                pos[v] = p;
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z);
                maxZ = Mathf.Max(maxZ, p.z);
                maxY = Mathf.Max(maxY, p.y);
            }
            fitted = new Model();
            fitted.positions = pos;
            fitted.normals = over.normals;
            fitted.colors = over.colors;
            fitted.tinted = over.tinted;
            fitted.uvs = over.uvs;
            fitted.material = over.material;
            fitted.materials = over.materials;
            fitted.hull = over.hull;
            fitted.noOutline = over.noOutline;
            fitted.maxY = maxY;
            fitted.minX = minX;
            fitted.maxX = maxX;
            fitted.minZ = minZ;
            fitted.maxZ = maxZ;
            fitted.halfDepth = Mathf.Max(maxZ, -minZ);
            fitted.id = ObjLoader.NewId();
            ApparelModels.MoveFlames(over, fitted, move);
            overFitted[key] = fitted;
            return fitted;
        }

        private static void MaxReach(float[] into, float[] from)
        {
            for (int i = 0; from != null && i < into.Length; i++)
            {
                into[i] = Mathf.Max(into[i], from[i]);
            }
        }

        // How far a model reaches out from the figure's middle line, per height bin and direction: its faces sampled, not
        // only its corners, so a big face counts everywhere it passes. Null for none.
        private static float[] Reach(Model m)
        {
            if (m == null)
            {
                return null;
            }
            float[] reach;
            if (overReach.TryGetValue(m.id, out reach))
            {
                return reach;
            }
            reach = new float[OverRows * OverSectors];
            Vector3[] p = m.positions;
            for (int t = 0; t + 2 < p.Length; t += 3)
            {
                Vector3 a = p[t], b = p[t + 1], c = p[t + 2];
                float edge = Mathf.Max((b - a).magnitude, Mathf.Max((c - b).magnitude, (a - c).magnitude));
                int k = Mathf.Clamp(Mathf.CeilToInt(edge / (OverStep * 0.5f)), 1, 48);
                for (int i = 0; i <= k; i++)
                {
                    for (int j = 0; i + j <= k; j++)
                    {
                        Vector3 q = a + (b - a) * ((float)i / k) + (c - a) * ((float)j / k);
                        int row = Mathf.FloorToInt(q.y / OverStep);
                        if (row < 0 || row >= OverRows)
                        {
                            continue;
                        }
                        int at = row * OverSectors + Sector(Mathf.Atan2(q.z, q.x));
                        reach[at] = Mathf.Max(reach[at], Mathf.Sqrt(q.x * q.x + q.z * q.z));
                    }
                }
            }
            overReach[m.id] = reach;
            return reach;
        }

        private static int Sector(float angle)
        {
            int s = Mathf.FloorToInt((angle + Mathf.PI) / (2f * Mathf.PI) * OverSectors);
            return ((s % OverSectors) + OverSectors) % OverSectors;
        }

        // The push at a height and direction, blended between the bins' middles.
        private static float Sample(float[] push, float y, float angle)
        {
            float fy = Mathf.Clamp(y / OverStep - 0.5f, 0f, OverRows - 1);
            int y0 = (int)fy;
            int y1 = Mathf.Min(y0 + 1, OverRows - 1);
            float ty = fy - y0;
            float fs = (angle + Mathf.PI) / (2f * Mathf.PI) * OverSectors - 0.5f;
            int s0 = Mathf.FloorToInt(fs);
            float ts = fs - s0;
            s0 = ((s0 % OverSectors) + OverSectors) % OverSectors;
            int s1 = (s0 + 1) % OverSectors;
            float lo = Mathf.Lerp(push[y0 * OverSectors + s0], push[y0 * OverSectors + s1], ts);
            float hi = Mathf.Lerp(push[y1 * OverSectors + s0], push[y1 * OverSectors + s1], ts);
            return Mathf.Lerp(lo, hi, ty);
        }

        // A part drawn with no outline at all (marks every vertex once; the part is shared).
        internal static Model NoOutline(Model m)
        {
            if (m != null && m.noOutline == null)
            {
                bool[] none = new bool[m.positions.Length];
                for (int i = 0; i < none.Length; i++)
                {
                    none[i] = true;
                }
                m.noOutline = none;
            }
            return m;
        }

        internal static bool Covers(ApparelProperties props, string group)
        {
            if (props.bodyPartGroups == null)
            {
                return false;
            }
            for (int i = 0; i < props.bodyPartGroups.Count; i++)
            {
                if (props.bodyPartGroups[i] != null && props.bodyPartGroups[i].defName == group)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool OnLayer(ApparelProperties props, string layer)
        {
            if (props.layers == null)
            {
                return false;
            }
            for (int i = 0; i < props.layers.Count; i++)
            {
                if (props.layers[i] != null && props.layers[i].defName == layer)
                {
                    return true;
                }
            }
            return false;
        }

        // Armour the mod has a worn shape for, by the game's names for it (and the names the
        // DLCs and most mods use for theirs). Null for anything else.
        private static string ArmourKind(string name)
        {
            if (Has(name, "Cataphract"))
            {
                return "cataphract";
            }
            if (Has(name, "PowerArmor") || Has(name, "Marine") || Has(name, "PowerArmour"))
            {
                return "marine";
            }
            if (Has(name, "Recon"))
            {
                return "recon";
            }
            if (Has(name, "PlateArmor") || Has(name, "PlateArmour"))
            {
                return "plate";
            }
            if (Has(name, "FlakJacket"))
            {
                return "flakjacket";
            }
            if (Has(name, "FlakVest"))
            {
                return "flakvest";
            }
            if (Has(name, "FlakPants"))
            {
                return "flakpants";
            }
            return null;
        }

        // The colour of a piece of clothing as the game shows it. Things made from cloth or
        // leather are drawn in that material's colour; things that are not (power armour, flak)
        // have their colours painted into the sprite, so its average colour is used instead.
        internal static Color ApparelColour(Apparel a)
        {
            Color c = a.DrawColor;
            if (a.def.MadeFromStuff)
            {
                return c;
            }
            CompColorable colorable = a.TryGetComp<CompColorable>();
            if (colorable != null && colorable.Active)
            {
                return c;
            }
            return SpriteColors.MeanFor(a);
        }

        // Hats with their own model (Models/person_hat_<file>.obj), by exact defName; one line per hat.
        // Others fall through to the keyword rules below (modded hats, masks, helmets).
        internal static readonly Dictionary<string, string> OwnHats = new Dictionary<string, string>
        {
            { "Apparel_Tuque", "tuque" },
            { "Apparel_Beret", "beret" },
            { "Apparel_AuthorityCap", "authority_cap" },
            { "Apparel_Flophat", "flophat" },
            { "Apparel_HatTop", "top_hat" },
            { "Apparel_EltexSkullcap", "eltex_skullcap" },
            { "Apparel_CowboyHat", "cowboy_hat" },
            { "Apparel_Shadecone", "shadecone" },
            { "Apparel_BowlerHat", "bowler_hat" },
            { "Apparel_HatLadies", "ladies_hat" },
            { "Apparel_Crown", "crown" },
            { "Apparel_Coronet", "coronet" },
            { "Apparel_TortureCrown", "torture_crown" },
            { "Apparel_HatHood", "cloth_hood" },
            { "Apparel_Slicecap", "slicecap" },
            { "Apparel_TribalHeaddress", "tribal_headdress" },
            { "Apparel_Tailcap", "tailcap" },
            { "Apparel_Headwrap", "headwrap" },
            { "Apparel_CrownStellic", "crown_stellic" },
            // Masks, veils, headsets and helmets (apparel step 3, tools/make_masks.py)
            { "Apparel_WarMask", "war_mask" },
            { "Apparel_VisageMask", "visage_mask" },
            { "Apparel_GasMask", "gas_mask" },
            { "Apparel_CultistMask", "cultist_mask" },
            { "Apparel_CeremonialCultistMask", "whisperer_hood" },
            { "Apparel_WarVeil", "war_veil" },
            { "Apparel_Broadwrap", "broadwrap" },
            { "Apparel_AirwireHeadset", "airwire_headset" },
            { "Apparel_ArrayHeadset", "array_headset" },
            { "Apparel_IntegratorHeadset", "integrator_headset" },
            { "Apparel_Gunlink", "gunlink" },
            { "Apparel_PsychicFoilHelmet", "psychic_foil_helmet" },
            { "Apparel_PsyfocusHelmet", "psyfocus_helmet" },
            { "Apparel_KidHelmet", "kid_helmet" },
            { "Apparel_VacsuitHelmet", "vacsuit_helmet" },
            { "Apparel_ArmorHelmetMechCommander", "mechcommander_helmet" },
            { "Apparel_ArmorHelmetMechlordHelmet", "mechlord_helmet" },
            // Prestige helmets: the family helmet + gold parts (coverage batch 1, tools/make_coverage_b1.py)
            { "Apparel_ArmorMarineHelmetPrestige", "marine_helm_prestige" },
            { "Apparel_ArmorHelmetCataphractPrestige", "cataphract_helm_prestige" },
            { "Apparel_ArmorHelmetReconPrestige", "recon_helm_prestige" },
        };

        // Face, eye and neck items with their own model (Models/person_hat_<file>.obj), by exact defName. The game lets
        // them be worn with a hat (another layer or other body parts), so each has its own slot instead of the hat's.
        internal static readonly Dictionary<string, string> OwnFaceWear = new Dictionary<string, string>
        {
            { "Apparel_ClothMask", "cloth_mask" },
            { "Apparel_Blindfold", "blindfold" },
            { "Apparel_Collar", "collar" },
        };

        private static string HatKind(string name, bool fullHead)
        {
            string own;
            if (OwnHats.TryGetValue(name, out own))
            {
                return own;
            }
            if (name == "Apparel_SimpleHelmet")
            {
                return "simple_helmet";   // its own model; other helmets keep the generic one
            }
            if (Has(name, "Helmet") || Has(name, "Helm"))
            {
                if (Has(name, "Cataphract"))
                {
                    return "cataphract_helm";
                }
                if (Has(name, "PowerArmor") || Has(name, "Marine"))
                {
                    return "marine_helm";
                }
                if (Has(name, "Recon"))
                {
                    return "recon_helm";
                }
                if (Has(name, "Advanced"))
                {
                    return "combat";
                }
                bool sealedHelmet = fullHead && (Has(name, "Power") || Has(name, "Recon") || Has(name, "Marine") || Has(name, "Cataphract")
                    || Has(name, "Armor") || Has(name, "Phoenix") || Has(name, "Gunlink") || Has(name, "Prestige"));
                return sealedHelmet ? "fullhelm" : "helmet";
            }
            if (Has(name, "Cowboy") || Has(name, "Shadecone") || Has(name, "Brim") || Has(name, "Sun"))
            {
                return "brim";
            }
            if (Has(name, "Bowler") || Has(name, "TopHat") || Has(name, "HatTop") || Has(name, "Ladies") || Has(name, "Crown") || Has(name, "Coronet") || Has(name, "Stellic"))
            {
                return "tall";
            }
            if (Has(name, "Hood") || Has(name, "Veil") || Has(name, "Burka") || Has(name, "Wrap") || Has(name, "Slicecap") || Has(name, "Collar"))
            {
                return "hood";
            }
            if (Has(name, "Mask") || Has(name, "Visage"))
            {
                return fullHead ? "helmet" : "cap";
            }
            return "cap";
        }
    }
}
