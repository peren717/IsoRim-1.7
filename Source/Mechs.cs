using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Mechanoids drawn as 3D models in place of their sprites (ModelDef mech = true).
    //
    // A mech is a body that does not move plus the pieces that do (ModelDef.mechParts), all built
    // in one space and turned about their own pivots. Everything is read from the game's own state,
    // so the model does what the sprite would: it turns to face the way it walks (and the game's
    // facing when it stands), its wheels roll by the ground covered and its legs step, and both stop
    // when it stops or the game is paused. Its faction marks take the faction's mech colour, its gun
    // shows when the game shows it, pointing where the game points it, a turret follows its comp,
    // a hauled thing is held in front of it, it rises with a jump, and downed or dead it lies on its
    // side. While it works, the parts made for the work (brushes, cutter discs, sensor heads, digging
    // arms) move. A mech the game draws with its other look (the Ancient sprites of mechs over 100
    // years old, which includes almost every raider) is drawn worn: a little darker and duller, faint rust, marks kept.
    public static class Mechs
    {
        private enum Motion { Fixed, Wheel, Leg, Spin, Pitch, Arm, Upper, Head, Weapon, Turret, Open }

        private class Piece
        {
            public MechPart def;
            public Motion motion;
            public Model model;
            public Model marked;      // with the faction marks tinted, or null
            public int parent = -1;
            public Model[] worn;      // weathered copies (Ancient), by variant; made when first needed
            public Model[] wornMarked;
            public int show;          // 0 always, 1 only while shut, 2 only while open (entities that open)
            public Vector3 tip;       // weapon and turret parts: the barrel's end in the body's space (Tip)
        }

        private class Rig
        {
            public Model body;
            public Model bodyMarked;
            public Color markBase = Color.white;
            public readonly List<Piece> pieces = new List<Piece>();
            public float minX, maxX, minZ, maxZ, height;   // the whole mech, guns left out
            public bool legs;
            public bool weaponPart;
            public bool opens;        // has parts that slide open or show by how far it is open
            public Model[] bodyWorn;
            public Model[] bodyWornMarked;
        }

        // Ancient mechs (the game's other sprite of the kind, over 100 years old) are drawn worn: a little darker, with faint rust
        // patches, in one of a few patterns picked by the mech's id (so each keeps its own).
        private const int WornVariants = 3;

        private static Vector3 WornOffset(int v)
        {
            return new Vector3(v * 17.3f, v * 3.1f, v * 9.7f);
        }

        // The model to draw for one piece (body: piece null): plain or worn, with or without faction marks.
        private static Model Pick(Rig rig, ModelDef def, Piece p, int worn, bool marked)
        {
            Model plain = p != null ? p.model : rig.body;
            if (worn < 0)
            {
                Model m = p != null ? p.marked : rig.bodyMarked;
                return marked && m != null ? m : plain;
            }
            if (p == null && rig.bodyWorn == null)
            {
                rig.bodyWorn = new Model[WornVariants];
                rig.bodyWornMarked = new Model[WornVariants];
            }
            if (p != null && p.worn == null)
            {
                p.worn = new Model[WornVariants];
                p.wornMarked = new Model[WornVariants];
            }
            Model[] w = p != null ? p.worn : rig.bodyWorn;
            Model[] wm = p != null ? p.wornMarked : rig.bodyWornMarked;
            if (w[worn] == null)
            {
                w[worn] = ObjLoader.Weathered(plain, WornOffset(worn), def.factionMaterials);
                Color unused;
                wm[worn] = ObjLoader.Marked(w[worn], def.factionMaterials, out unused);
            }
            return marked && wm[worn] != null ? wm[worn] : w[worn];
        }

        // Half the game's allegiance overlay: it lays the colour over the sprite's mask at half strength.
        private const float FactionMix = 0.5f;

        // Rigs by binding and look (index 0: the binding's own model; i + 1: looks[i]).
        private static readonly Dictionary<ModelDef, Rig[]> rigs = new Dictionary<ModelDef, Rig[]>();

        // An entity counts as shut while it has opened less than this (CompNociosphere.segScale).
        private const float OpenShown = 0.1f;

        // The look the game gave each entity individual (its alternate graphic), by thing id; it never changes.
        private static readonly Dictionary<int, int> alternateLooks = new Dictionary<int, int>();

        private static readonly HashSet<string> WorkJobs = new HashSet<string>
        {
            "CutPlant", "CutPlantDesignated", "Harvest", "HarvestDesignated", "Sow", "Mine", "FinishFrame", "Repair", "BuildRoof",
            "RemoveRoof", "Deconstruct", "Uninstall", "SmoothFloor", "SmoothWall", "RemoveFloor", "FixBrokenDownBuilding",
            "PlaceNoCostFrame", "Clean", "DoBill"
        };

        // True if the pawn is a mech with a model that will be drawn as it (the view and setting on).
        public static bool HasModel(Pawn pawn)
        {
            ModelEntry entry;
            return pawn != null && pawn.def != null && ModelLibrary.TryGet(pawn.def, out entry) && (entry.def.mech || entry.def.entity) && entry.HasModel
                && !(entry.def.entity ? pawn.IsPsychologicallyInvisible() : People.Hidden(pawn)) && (!entry.def.entity || EntityShown(pawn)) && Corpses.ShownAsModel(pawn);
        }

        // Draws the mech at drawLoc. Returns false if it has no model, in which
        // case the game's sprite must be drawn as usual.
        public static bool Draw(Pawn pawn, Vector3 drawLoc)
        {
            ModelEntry entry;
            if (pawn.def == null || !ModelLibrary.TryGet(pawn.def, out entry) || !(entry.def.mech || entry.def.entity) || !entry.HasModel
                || (entry.def.entity ? pawn.IsPsychologicallyInvisible() : People.Hidden(pawn)) || (entry.def.entity && !EntityShown(pawn)) || !Corpses.ShownAsModel(pawn))
            {
                return false;
            }
            Rig rig = RigFor(entry, entry.def.entity ? LookOf(pawn, entry.def) : -1);
            if (rig == null || rig.body == null)
            {
                return false;
            }
            ModelDef def = entry.def;
            IsoRimSettings st = IsoRimMod.Settings;

            float size = def.scale * (def.lookBy == "lifeStage" ? 1f : Animals.SizeOf(pawn));   // lifeStage looks are made at their stage's size
            bool dead = pawn.Dead;
            bool lying = dead || pawn.GetPosture() != PawnPosture.Standing;
            bool onSide = dead || pawn.Downed;
            float lift = Carried.LiftOf(pawn, ref drawLoc) + Corpses.RestHeight(pawn);
            // Held on a holding platform drawn as its model: as the game draws it (HeldPawnPosture LayingOnGroundFaceUp,
            // HeldPawnBodyAngle = the platform's rotation), lying on its back on the deck, head toward the platform's facing.
            Building_HoldingPlatform platform = pawn.ParentHolder as Building_HoldingPlatform;
            if (platform != null)
            {
                lift = Forms.HeightOf(platform);
                lying = true;
                onSide = false;
            }

            float open = rig.opens ? Openness(pawn) : 0f;

            float yaw;
            float phase = 0f;
            float gait = 0f;
            float travel = 0f;
            if (dead)
            {
                // Each wreck lies at its own slight angle.
                yaw = pawn.Rotation.AsAngle + (Hashing.Unit(pawn.thingIDNumber, 3, 9) - 0.5f) * 50f;
            }
            else if (platform != null)
            {
                yaw = platform.Rotation.AsAngle;
                float swayAngle;
                Vector3 swayShift;
                if (HoldingChains.Sway(pawn, out swayAngle, out swayShift))
                {
                    yaw += swayAngle;   // straining against the chains, as the game moves its sprite
                    drawLoc += swayShift;
                }
            }
            else if (def.fixedFacing)
            {
                yaw = Rot4.South.AsAngle;   // drawn facing south whatever its rotation (useFixedRotation)
            }
            else
            {
                Animals.Track(pawn, drawLoc, lying, def.stride * size, out yaw, out phase, out gait, out travel);
            }

            // At work: face the work, and move the parts made for it (timed by game ticks, so they stop when paused).
            float work = -1f;
            IntVec3 target;
            if (!lying && st.workAnimations && Working(pawn, out target))
            {
                Vector3 to = target.ToVector3Shifted() - drawLoc;
                if (to.x * to.x + to.z * to.z > 0.04f)
                {
                    yaw = to.AngleFlat();
                }
                work = (Find.TickManager.TicksGame + (pawn.thingIDNumber % 97)) / 60f;
            }

            // The gun, as the game shows it (PawnRenderUtility.DrawEquipmentAndApparelExtras).
            bool showGun = false;
            bool aiming = false;
            float aim = yaw;
            if (!lying && st.heldWeapons3D)
            {
                GunState(pawn, ref aim, out showGun, out aiming);
            }
            CompTurretGun turret = lying ? null : pawn.TryGetComp<CompTurretGun>();
            float turretAim = turret != null ? turret.curRotation - turret.Props.angleOffset : yaw;
            if (turret != null && turret.curRotation == 0f && pawn.Rotation == Rot4.West)
            {
                turretAim += 180f;   // never aimed yet: the game's sprite is turned round for a mech facing west
            }

            // How the whole mech is posed, before it is turned to face its heading.
            Matrix4x4 sized = Matrix4x4.Scale(new Vector3(size, size, size));
            Matrix4x4 pose;
            if (platform != null)
            {
                // On its back: its front up, its top toward its heading; the back on the deck, the body centred where the game draws it.
                pose = Matrix4x4.Translate(new Vector3(0f, -rig.minZ * size, -rig.height * 0.5f * size)) * Matrix4x4.Rotate(OnBack);
                gait = 0f;
            }
            else if (onSide)
            {
                // Rolled over onto its left side, which then rests on the ground.
                float centre = rig.height * 0.5f;
                pose = Matrix4x4.Translate(new Vector3(0f, -rig.minX * size, 0f))
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(90f, Vector3.forward))
                    * Matrix4x4.Translate(new Vector3(0f, -centre * size, 0f));
                gait = 0f;
            }
            else if (rig.legs)
            {
                // A slight rise and fall with each step.
                float bob = Mathf.Abs(Mathf.Sin(phase)) * 0.025f * rig.height * size * gait;
                pose = Matrix4x4.Translate(new Vector3(0f, bob, 0f));
            }
            else
            {
                pose = Matrix4x4.identity;
            }
            // (Api 32) hovering: the whole mech raised over the ground while it is up and about
            float hover = Hover.Lift(pawn, def, size, !lying && platform == null && lift <= 0f, true);
            if (hover > 0f)
            {
                pose = Matrix4x4.Translate(new Vector3(0f, hover, 0f)) * pose;
            }
            Matrix4x4 whole = pose * sized;
            Vector3 at = new Vector3(drawLoc.x, lift, drawLoc.z);
            FigureScope.Root(at, yaw);   // (Api.DrawFigure: placed elsewhere)

            Faction faction = pawn.Faction;
            Color mark;
            bool marked;
            if (def.entity)
            {
                // Glowing parts as bright as the entity is active (as the game sets its seam colour's alpha).
                CompActivity activity = def.glowMaterials != null ? pawn.TryGetComp<CompActivity>() : null;
                float a = activity != null && !dead ? Mathf.Clamp01(activity.ActivityLevel) : 0f;
                mark = Color.Lerp(rig.markBase * 0.5f, def.glowColor, a);
                marked = def.glowMaterials != null;
            }
            else
            {
                mark = faction != null ? Color.Lerp(rig.markBase, faction.AllegianceColor, FactionMix) : rig.markBase;
                marked = faction != null;
            }

            int worn = def.mech && OtherLook(pawn) ? (pawn.thingIDNumber & 0x7FFFFFFF) % WornVariants : -1;
            DynamicModels.DrawLocal(Corpses.Body(pawn, Pick(rig, def, null, worn, marked)), at, yaw, whole, mark, null);

            // Each moving piece, placed in the body's space (after its parent's movement).
            Matrix4x4[] places = new Matrix4x4[rig.pieces.Count];
            bool gunDrawn = false;
            for (int i = 0; i < rig.pieces.Count; i++)
            {
                Piece p = rig.pieces[i];
                Quaternion turn = Quaternion.identity;
                Vector3 slide = Vector3.zero;
                switch (p.motion)
                {
                    case Motion.Open:
                        slide = p.def.openOffset * open;
                        break;
                    case Motion.Wheel:
                        turn = Quaternion.AngleAxis(travel / Mathf.Max(0.05f, p.def.radius * size) * Mathf.Rad2Deg, Vector3.right);
                        break;
                    case Motion.Leg:
                        if (gait > 0.01f)
                        {
                            turn = Quaternion.AngleAxis(Mathf.Sin(phase + p.def.beat * 2f * Mathf.PI) * p.def.swing * Mathf.Clamp01(gait), Vector3.right);
                        }
                        break;
                    case Motion.Spin:
                        if (work >= 0f)
                        {
                            turn = Quaternion.AngleAxis(Mathf.Repeat(work * p.def.speed, 360f), Vector3.up);
                        }
                        break;
                    case Motion.Pitch:
                        if (work >= 0f)
                        {
                            turn = Quaternion.AngleAxis(Mathf.Sin(work * 2f * Mathf.PI / 1.4f) * p.def.swing, Vector3.right);
                        }
                        break;
                    case Motion.Arm:
                        if (work >= 0f)
                        {
                            // Down onto the work and back up, a stroke a second and a half.
                            float k = 0.5f - 0.5f * Mathf.Cos(work * 2f * Mathf.PI / 1.5f);
                            turn = Quaternion.AngleAxis(k * p.def.swing, Vector3.right);
                        }
                        break;
                    case Motion.Upper:
                        if (aiming)
                        {
                            turn = Quaternion.AngleAxis(Mathf.DeltaAngle(yaw, aim), Vector3.up);
                        }
                        break;
                    case Motion.Head:
                        if (aiming)
                        {
                            turn = Quaternion.AngleAxis(Mathf.Clamp(Mathf.DeltaAngle(yaw, aim), -p.def.maxTurn, p.def.maxTurn), Vector3.up);
                        }
                        break;
                    case Motion.Weapon:
                        // On an upper body the upper body turns it; otherwise it turns by itself.
                        if (aiming && (p.parent < 0 || rig.pieces[p.parent].motion != Motion.Upper))
                        {
                            turn = Quaternion.AngleAxis(Mathf.DeltaAngle(yaw, aim), Vector3.up);
                        }
                        break;
                    case Motion.Turret:
                        turn = Quaternion.AngleAxis(Mathf.DeltaAngle(yaw, turretAim), Vector3.up);
                        break;
                }
                Matrix4x4 own = turn == Quaternion.identity ? Matrix4x4.identity
                    : Matrix4x4.Translate(p.def.pivot) * Matrix4x4.Rotate(turn) * Matrix4x4.Translate(-p.def.pivot);
                if (slide != Vector3.zero)
                {
                    own = Matrix4x4.Translate(slide) * own;
                }
                places[i] = (p.parent >= 0 ? places[p.parent] : Matrix4x4.identity) * own;
                if (p.show == 1 && p.def.fadeTo > 0f)
                {
                    if (open >= p.def.fadeTo)
                    {
                        continue;
                    }
                    if (open > p.def.fadeFrom)
                    {
                        // Sinking into what it covers as the entity opens (the nociosphere's lining into its core).
                        float k = Mathf.Lerp(1f, p.def.fadeScale, Mathf.InverseLerp(p.def.fadeFrom, p.def.fadeTo, open));
                        places[i] = places[i] * Matrix4x4.Translate(p.def.pivot) * Matrix4x4.Scale(new Vector3(k, k, k)) * Matrix4x4.Translate(-p.def.pivot);
                    }
                }
                else if ((p.show == 1 && open >= OpenShown) || (p.show == 2 && open < OpenShown))
                {
                    continue;
                }

                if (p.motion == Motion.Weapon)
                {
                    if (!showGun)
                    {
                        continue;
                    }
                    gunDrawn = true;
                    Thing gun = pawn.equipment.Primary;
                    if (!p.def.weapon.NullOrEmpty() && gun.def.defName != p.def.weapon)
                    {
                        // Some other gun: drawn as that gun's own 3D form, at the mount.
                        Vector3 mount = WorldPoint(at, yaw, whole * places[i], p.def.pivot);
                        Patches.HeldWeapon3D(gun, mount, aim);
                        continue;
                    }
                }
                else if (p.motion == Motion.Turret && (turret == null || turret.TurretDestroyed))
                {
                    continue;
                }
                if (p.motion == Motion.Weapon || p.motion == Motion.Turret)
                {
                    // Where this gun's barrel ends as drawn: its shots and flash start there (Projectiles.MechTip).
                    ThingDef shooting = p.motion == Motion.Weapon ? pawn.equipment.Primary.def : turret?.gun?.def;
                    if (shooting != null)
                    {
                        Projectiles.MechTip(pawn, shooting, WorldPoint(at, yaw, whole * places[i], p.tip));
                    }
                }
                DynamicModels.DrawLocal(Corpses.Body(pawn, Pick(rig, def, p, worn, marked)), at, yaw, whole * places[i], mark, null);
            }
            if (showGun && !gunDrawn)
            {
                // A gun the model has no mount for: held in front, half way up.
                Vector3 front = WorldPoint(at, yaw, whole, new Vector3(0f, rig.height * 0.5f, rig.maxZ));
                Patches.HeldWeapon3D(pawn.equipment.Primary, front, aim);
            }

            if (lift <= 0f)
            {
                float halfX = Mathf.Max(-rig.minX, rig.maxX) * size;
                float halfZ = Mathf.Max(-rig.minZ, rig.maxZ) * size;
                float spread = Hover.ShadowScale(hover);   // (Api 32) a hovering mech's shadow, on the ground, a little smaller
                if (onSide)
                {
                    GroundShadows.Blob(at, yaw, rig.height * 0.5f * size * 1.2f * spread, halfZ * 1.15f * spread);
                }
                else
                {
                    GroundShadows.Blob(at, yaw, halfX * 1.2f * spread, halfZ * 1.15f * spread);
                }
            }

            if (!lying && pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null)
            {
                Vector3 hold = def.carryAt.y >= 0f ? def.carryAt : new Vector3(0f, rig.height * 0.45f, rig.maxZ + 0.08f);
                Carried.Draw(pawn, WorldPoint(at, yaw, whole, hold), yaw);
            }
            return true;
        }

        // Lying on its back: (x, y, z) to (-x, z, y), the front up and the top forward.
        private static readonly Quaternion OnBack = Quaternion.AngleAxis(180f, Vector3.forward) * Quaternion.AngleAxis(90f, Vector3.right);

        // How high an entity held on a holding platform lies as its model (cells over the deck), for the chains; false if it is not drawn so.
        public static bool HeldHeight(Pawn pawn, out float height)
        {
            height = 0f;
            ModelEntry entry;
            if (!HasModel(pawn) || !ModelLibrary.TryGet(pawn.def, out entry))
            {
                return false;
            }
            Rig rig = RigFor(entry, entry.def.entity ? LookOf(pawn, entry.def) : -1);
            if (rig == null || rig.body == null)
            {
                return false;
            }
            float size = entry.def.scale * (entry.def.lookBy == "lifeStage" ? 1f : Animals.SizeOf(pawn));
            height = (rig.maxZ - rig.minZ) * size;
            return true;
        }

        // A point of the model (its own space, facing north) on the map: x and z in cells, y the height above the ground.
        // A gun part's barrel end (body space): the middle of its front-most face. Gun parts are built pointing north (+z)
        // and turn about their pivot, so the barrel ends where the part reaches furthest forward.
        private static Vector3 Tip(Model m)
        {
            if (m == null || m.positions == null || m.positions.Length == 0)
            {
                return Vector3.zero;
            }
            float front = float.MinValue;
            for (int i = 0; i < m.positions.Length; i++)
            {
                front = Mathf.Max(front, m.positions[i].z);
            }
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < m.positions.Length; i++)
            {
                if (m.positions[i].z >= front - 0.02f)
                {
                    sum += m.positions[i];
                    n++;
                }
            }
            return sum / Mathf.Max(1, n);
        }

        // Where the mech's gun (gun: the weapon's or its turret comp's def) would end now, for a mech not drawn just now
        // (a hidden floor, off screen): as Draw would pose it, from its last drawn facing, its hover and the aim (dir, flat).
        // False if it has no gun part for that gun. Same space as Projectiles' tips (x, z rows, y cells over its floor).
        public static bool MeasureGun(Pawn pawn, ThingDef gun, Vector3 dir, out Vector3 at)
        {
            at = Vector3.zero;
            ModelEntry entry;
            if (pawn == null || gun == null || pawn.def == null || !ModelLibrary.TryGet(pawn.def, out entry) || !entry.def.mech || !entry.HasModel
                || pawn.GetPosture() != PawnPosture.Standing)
            {
                return false;
            }
            Rig rig = RigFor(entry, -1);
            if (rig == null)
            {
                return false;
            }
            CompTurretGun turret = pawn.TryGetComp<CompTurretGun>();
            for (int i = 0; i < rig.pieces.Count; i++)
            {
                Piece p = rig.pieces[i];
                bool weapon = p.motion == Motion.Weapon && pawn.equipment?.Primary != null && pawn.equipment.Primary.def == gun;
                bool tur = p.motion == Motion.Turret && turret?.gun != null && turret.gun.def == gun;
                if (!weapon && !tur)
                {
                    continue;
                }
                int age;
                float? drawn = Animals.DrawnYaw(pawn, out age);
                float yaw = drawn ?? pawn.Rotation.AsAngle;
                float aim = dir.sqrMagnitude > 0.0001f ? dir.AngleFlat() : yaw;
                float size = entry.def.scale * (entry.def.lookBy == "lifeStage" ? 1f : Animals.SizeOf(pawn));
                Matrix4x4 local = Matrix4x4.Translate(p.def.pivot) * Matrix4x4.Rotate(Quaternion.AngleAxis(Mathf.DeltaAngle(yaw, aim), Vector3.up)) * Matrix4x4.Translate(-p.def.pivot);
                Matrix4x4 whole = Matrix4x4.Translate(new Vector3(0f, Hover.Current(pawn), 0f)) * Matrix4x4.Scale(new Vector3(size, size, size));
                Vector3 loc = pawn.DrawPos;
                float lift = Carried.LiftOf(pawn, ref loc);
                at = WorldPoint(new Vector3(loc.x, lift, loc.z), yaw, whole * local, p.tip);
                return true;
            }
            return false;
        }

        private static Vector3 WorldPoint(Vector3 at, float yaw, Matrix4x4 local, Vector3 p)
        {
            Vector3 v = Quaternion.AngleAxis(yaw, Vector3.up) * local.MultiplyPoint3x4(p);
            return new Vector3(at.x + v.x, at.y + v.y, at.z + v.z);
        }

        // Whether the game shows the mech's gun now and where it points (as PawnRenderUtility.DrawEquipmentAndApparelExtras).
        private static void GunState(Pawn pawn, ref float aim, out bool show, out bool aiming)
        {
            show = false;
            aiming = false;
            if (pawn.equipment == null || pawn.equipment.Primary == null)
            {
                return;
            }
            Job job = pawn.CurJob;
            if (job == null || job.def == null || job.def.neverShowWeapon)
            {
                return;
            }
            Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
            if (busy != null && !busy.neverAimWeapon && busy.focusTarg.IsValid)
            {
                Vector3 to = Floors.FlatDelta(pawn.DrawPos, busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted());
                if (to.x * to.x + to.z * to.z > 0.001f)
                {
                    aim = to.AngleFlat();
                }
                Verb verb = pawn.CurrentEffectiveVerb;
                if (verb != null && verb.AimAngleOverride.HasValue)
                {
                    aim = verb.AimAngleOverride.Value;
                }
                show = true;
                aiming = true;
            }
            else if (PawnRenderUtility.CarryWeaponOpenly(pawn))
            {
                show = true;
            }
        }

        // True while the mech is at its work (a job with work parts, at the target, not walking).
        private static bool Working(Pawn pawn, out IntVec3 target)
        {
            target = IntVec3.Invalid;
            Job job = pawn.CurJob;
            if (job == null || job.def == null || pawn.Drafted || (pawn.pather != null && pawn.pather.Moving) || !WorkJobs.Contains(job.def.defName))
            {
                return false;
            }
            LocalTargetInfo a = job.targetA;
            if (!a.IsValid)
            {
                return false;
            }
            target = a.Cell;
            return target.IsValid && pawn.Position.DistanceToSquared(target) <= 8;
        }

        // True for a mech the game draws with a sprite other than its kind's first one (the Ancient look).
        public static bool OtherLook(Pawn pawn)
        {
            List<PawnKindLifeStage> stages = pawn.kindDef != null ? pawn.kindDef.lifeStages : null;
            if (stages == null || stages.Count < 2 || pawn.ageTracker == null)
            {
                return false;
            }
            PawnKindLifeStage cur = pawn.ageTracker.CurKindLifeStage;
            if (cur == null || cur == stages[0] || cur.bodyGraphicData == null || stages[0].bodyGraphicData == null)
            {
                return false;
            }
            return cur.bodyGraphicData.texPath != stages[0].bodyGraphicData.texPath;
        }

        private static Motion MotionOf(string s)
        {
            switch ((s ?? "fixed").ToLowerInvariant())
            {
                case "wheel": return Motion.Wheel;
                case "leg": return Motion.Leg;
                case "spin": return Motion.Spin;
                case "pitch": return Motion.Pitch;
                case "arm": return Motion.Arm;
                case "upper": return Motion.Upper;
                case "head": return Motion.Head;
                case "weapon": return Motion.Weapon;
                case "turret": return Motion.Turret;
                case "open": return Motion.Open;
                default: return Motion.Fixed;
            }
        }

        // look: -1 the binding's own model and parts, else looks[look].
        private static Rig RigFor(ModelEntry entry, int look)
        {
            ModelDef def = entry.def;
            Rig[] all;
            if (!rigs.TryGetValue(def, out all))
            {
                all = new Rig[(def.looks != null ? def.looks.Count : 0) + 1];
                rigs[def] = all;
            }
            int k = look < 0 || look + 1 >= all.Length ? 0 : look + 1;
            if (all[k] != null)
            {
                return all[k];
            }
            Rig rig = null;
            try
            {
                rig = Build(entry, k - 1);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Putting a mechanoid's model together failed (" + def.defName + "): " + e, 93611401);
            }
            if (rig == null && k > 0)
            {
                rig = RigFor(entry, -1);   // a look that cannot be built: the binding's own model
            }
            all[k] = rig;
            return rig;
        }

        private static Rig Build(ModelEntry entry, int look)
        {
            ModelDef def = entry.def;
            Rig rig = new Rig();
            string root = def.modContentPack != null ? def.modContentPack.RootDir : IsoRimMod.RootDir;
            EntityLook other = look >= 0 ? def.looks[look] : null;
            Model main = other != null ? (other.model.NullOrEmpty() ? null : ModelLibrary.LoadFile(Path.Combine(root, other.model))) : entry.variants[0];
            if (main == null)
            {
                return null;
            }
            List<MechPart> partDefs = other != null ? other.mechParts : def.mechParts;
            List<Model> still = new List<Model> { main };
            Dictionary<string, int> byName = new Dictionary<string, int>();
            List<string> parents = new List<string>();
            if (partDefs != null)
            {
                for (int i = 0; i < partDefs.Count; i++)
                {
                    MechPart part = partDefs[i];
                    Model m = part == null || part.model.NullOrEmpty() ? null : ModelLibrary.LoadFile(Path.Combine(root, part.model));
                    if (m == null)
                    {
                        continue;
                    }
                    Motion motion = MotionOf(part.motion);
                    int show = part.show == "closed" ? 1 : (part.show == "open" ? 2 : 0);
                    if (motion == Motion.Fixed && part.parent.NullOrEmpty() && show == 0)
                    {
                        still.Add(m);
                        continue;
                    }
                    Piece p = new Piece { def = part, motion = motion, model = m, show = show };
                    if (motion == Motion.Weapon || motion == Motion.Turret)
                    {
                        p.tip = Tip(m);
                    }
                    rig.opens |= motion == Motion.Open || show != 0;
                    byName[part.name.NullOrEmpty() ? Path.GetFileNameWithoutExtension(part.model) : part.name] = rig.pieces.Count;
                    rig.pieces.Add(p);
                    parents.Add(part.parent);
                    rig.legs |= motion == Motion.Leg;
                    rig.weaponPart |= motion == Motion.Weapon;
                }
            }
            for (int i = 0; i < rig.pieces.Count; i++)
            {
                int pi;
                if (!parents[i].NullOrEmpty() && byName.TryGetValue(parents[i], out pi))
                {
                    if (pi < i)
                    {
                        rig.pieces[i].parent = pi;
                    }
                    else
                    {
                        ModLog.Warn("[IsoRim] " + def.defName + ": mech part " + rig.pieces[i].def.model + " must come after its parent " + parents[i]);
                    }
                }
            }
            rig.body = still.Count > 1 ? ObjLoader.Merge(still) : still[0];

            Color c;
            rig.bodyMarked = def.entity ? ObjLoader.Glowing(rig.body, def.glowMaterials, out c) : ObjLoader.Marked(rig.body, def.factionMaterials, out c);
            bool haveBase = rig.bodyMarked != null;
            if (haveBase)
            {
                rig.markBase = c;
            }
            for (int i = 0; i < rig.pieces.Count; i++)
            {
                Piece p = rig.pieces[i];
                p.marked = def.entity ? ObjLoader.Glowing(p.model, def.glowMaterials, out c) : ObjLoader.Marked(p.model, def.factionMaterials, out c);
                if (p.marked != null && !haveBase)
                {
                    rig.markBase = c;
                    haveBase = true;
                }
            }

            // The size of the whole mech, guns left out.
            rig.minX = rig.body.minX;
            rig.maxX = rig.body.maxX;
            rig.minZ = rig.body.minZ;
            rig.maxZ = rig.body.maxZ;
            rig.height = rig.body.maxY;
            for (int i = 0; i < rig.pieces.Count; i++)
            {
                Piece p = rig.pieces[i];
                if (p.motion == Motion.Weapon || p.motion == Motion.Turret)
                {
                    continue;
                }
                rig.minX = Mathf.Min(rig.minX, p.model.minX);
                rig.maxX = Mathf.Max(rig.maxX, p.model.maxX);
                rig.minZ = Mathf.Min(rig.minZ, p.model.minZ);
                rig.maxZ = Mathf.Max(rig.maxZ, p.model.maxZ);
                rig.height = Mathf.Max(rig.height, p.model.maxY);
            }
            return rig;
        }

        // An entity is drawn as its model unless the game shows it some other way: held on a holding platform
        // that is not drawn as its model (the sprite lies on the flat platform). A corpse dried to bones keeps
        // the game's sprite too, as every pawn but people does (Corpses.ShownAsModel).
        private static bool EntityShown(Pawn pawn)
        {
            if (pawn.ParentHolder is Building_HoldingPlatform platform)
            {
                return platform.Spawned && SectionLayer_IsoRimBodies.StandsIn(platform);
            }
            return true;
        }

        // Which of an entity's looks the game's state picks now (-1: the binding's own model).
        private static int LookOf(Pawn pawn, ModelDef def)
        {
            if (def.looks == null || def.looks.Count == 0)
            {
                return -1;
            }
            switch (def.lookBy)
            {
                case "activity":
                {
                    // As Graphic_ActivityStaged.SubGraphicFor.
                    CompActivity activity = pawn.TryGetComp<CompActivity>();
                    if (activity == null)
                    {
                        return -1;
                    }
                    int n = def.looks.Count;
                    return Mathf.Clamp(Mathf.FloorToInt(n * activity.ActivityLevel), 0, n - 1);
                }
                case "lifeStage":
                {
                    // As the kind's life stage graphics: the look made for the stage it is in now.
                    string stage = pawn.ageTracker != null && pawn.ageTracker.CurLifeStage != null ? pawn.ageTracker.CurLifeStage.defName : null;
                    for (int i = 0; stage != null && i < def.looks.Count; i++)
                    {
                        if (def.looks[i].key == stage)
                        {
                            return i;
                        }
                    }
                    return -1;
                }
                case "digesting":
                {
                    // As PawnRenderNode_Devourer.GraphicFor: the closed mouth while it digests.
                    CompDevourer devourer = pawn.TryGetComp<CompDevourer>();
                    return devourer != null && devourer.Digesting ? 0 : -1;
                }
                default:
                {
                    int look;
                    if (alternateLooks.TryGetValue(pawn.thingIDNumber, out look))
                    {
                        return look;
                    }
                    look = -1;
                    AlternateGraphic ag;
                    int index;
                    if (pawn.kindDef != null && pawn.kindDef.alternateGraphics != null && pawn.TryGetAlternate(out ag, out index))
                    {
                        string tex = HarmonyLib.Traverse.Create(ag).Field("texPath").GetValue<string>();
                        if (tex.NullOrEmpty() && ag.graphicData != null)
                        {
                            tex = ag.graphicData.texPath;
                        }
                        for (int i = 0; tex != null && i < def.looks.Count; i++)
                        {
                            if (!def.looks[i].key.NullOrEmpty() && tex.EndsWith(def.looks[i].key, StringComparison.OrdinalIgnoreCase))
                            {
                                look = i;
                                break;
                            }
                        }
                    }
                    if (alternateLooks.Count > 4000)
                    {
                        alternateLooks.Clear();
                    }
                    alternateLooks[pawn.thingIDNumber] = look;
                    return look;
                }
            }
        }

        // How far an entity that opens has opened, 0 (shut) .. 1, briefly 1.2 (the nociosphere's shell segments, CompNociosphere.segScale,
        // eased by the game every tick, so it stops while paused): 0 dormant, about 0.3..0.7 breathing while active, wider
        // while an ability warms up.
        private static float Openness(Pawn pawn)
        {
            CompNociosphere sphere = pawn.TryGetComp<CompNociosphere>();
            return sphere != null ? Mathf.Clamp(sphere.segScale, 0f, 1.3f) : 0f;   // up to 1.2 just after an attack
        }

        // For the harness (probe mechcheck): how the mech is drawn now.
        public static string Report(Pawn pawn)
        {
            ModelEntry entry = null;
            bool bound = pawn.def != null && ModelLibrary.TryGet(pawn.def, out entry) && (entry.def.mech || entry.def.entity) && entry.HasModel;
            string s = "bound " + bound + ", ancient " + OtherLook(pawn) + ", model " + (bound && HasModel(pawn) && Patches.DrawnAsModel(pawn));
            if (bound && entry.def.entity)
            {
                s += ", look " + LookOf(pawn, entry.def) + ", open " + Openness(pawn).ToString("0.00") + ", shown " + EntityShown(pawn);
            }
            s += ", posture " + pawn.GetPosture() + (pawn.Downed ? " downed" : "") + (pawn.Dead ? " dead" : "") + ", faction " + (pawn.Faction != null ? pawn.Faction.def.defName : "none");
            if (pawn.ageTracker != null)
            {
                s += ", age " + pawn.ageTracker.AgeBiologicalYears;
            }
            if (pawn.Spawned && !pawn.Dead)
            {
                float aim = 0f;
                bool show, aiming;
                GunState(pawn, ref aim, out show, out aiming);
                IntVec3 t;
                s += ", gun " + (pawn.equipment != null && pawn.equipment.Primary != null ? pawn.equipment.Primary.def.defName : "none")
                    + (show ? (aiming ? " aiming " + aim.ToString("0") : " shown") : " hidden")
                    + ", job " + (pawn.CurJob != null ? pawn.CurJob.def.defName : "none") + (Working(pawn, out t) ? " working" : "")
                    + ", moving " + (pawn.pather != null && pawn.pather.Moving)
                    + ", carrying " + (pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null ? pawn.carryTracker.CarriedThing.LabelCap.ToString() : "nothing");
                CompTurretGun tg = pawn.TryGetComp<CompTurretGun>();
                if (tg != null)
                {
                    s += ", turret " + (tg.TurretDestroyed ? "destroyed" : "rot " + tg.curRotation.ToString("0"))
                        + " target " + HarmonyLib.Traverse.Create(tg).Field("currentTarget").GetValue<LocalTargetInfo>().ToString()
                        + " canShoot " + HarmonyLib.Traverse.Create(tg).Property("CanShoot").GetValue<bool>() + " fireAtWill " + HarmonyLib.Traverse.Create(tg).Field("fireAtWill").GetValue<bool>() + " cooldown " + HarmonyLib.Traverse.Create(tg).Field("burstCooldownTicksLeft").GetValue<int>();
                    CompCanBeDormant dorm = pawn.TryGetComp<CompCanBeDormant>();
                    s += ", awake " + (dorm == null || dorm.Awake);
                }
            }
            return s;
        }
    }
}
