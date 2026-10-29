using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // What a 3D figure holds and wears over itself, posed by the game (2026-10-17, isorim-figure-weapon-anim.md).
    //
    // The game's own PawnRenderUtility.DrawEquipmentAndApparelExtras is asked to draw the figure's weapon (and worn extras such
    // as the shield belt's bubble) as it would for the pawn's sprite, with every mod that patches it, DrawCarriedWeapon,
    // DrawEquipmentAiming or the recoil. What it draws is caught on its way to Unity (DrawHook: Capturing) instead of drawn,
    // and each piece is put on the figure:
    //   - carried (drafted, standing guard, a job, duty or lord that shows the weapon): the game's picture of the pawn seen
    //     from the front (south) laid on the figure's chest, turned Tilt degrees so the muzzle comes forward, riding the
    //     figure's pose (bob, sway, seat, lean);
    //   - aimed (warm-up, burst, cool-down, melee): the game's top-down picture turned onto the aim as drawn (between floors
    //     too), stood up about the aim line (the picture's top up), pitched toward the target's height, hinged at the hands;
    //     recoil and any mod's offsets come out as drawn. The barrel's end as drawn is where shots start (Projectiles);
    //   - anything else (worn extras: the shield bubble) a card facing the camera round the figure's middle.
    // Nothing caught means nothing shows: the game would not draw a weapon now (sheathed, carrying something, eating).
    // With "Weapons as 3D models" (WeaponModels.On, 2026-10-27): a weapon with a model shows it where its slab would be (its hold
    // frame); a ranged weapon that is not thrown is aimed from the right shoulder (the settings), its butt there and its barrel
    // along the aim, and whatever the game's drawing differs by from the game's own placing (other mods' animation, the
    // recoil) goes on top (Shoulder); an outfit stand carries its weapon as a colonist does (DrawOnStand). Off: as before.
    public static class FigureWeapons
    {
        private const float PersonHeight = 1.12f;   // a figure of size 1, feet to the top of the head (People.FigureHeight)
        private const float PictureHeight = 1.5f;   // the game's picture of a pawn (its 1.5-cell body mesh)
        private const float Picture = PersonHeight / PictureHeight;   // the game's picture units to figure units
        private const float HandY = 0.46f;          // the hands on a figure of size 1 (People.HandY)
        private const float WeaponGap = 0.05f;      // between the chest and the carried weapon, figure units
        private const float Tilt = 35f;             // the carried weapon's turn about the figure's up: muzzle forward
        private const float MaxPitch = 70f;

        private struct Caught
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material material;
            public Thing weapon;   // null: not a weapon (a worn extra)
            public bool carried;
        }

        private static readonly List<Caught> caught = new List<Caught>();
        public static bool Capturing;
        private static Thing drawingWeapon;
        private static bool carryPath;

        // For the harness (weaponcheck): what was drawn last for each pawn.
        private static readonly Dictionary<int, string> reports = new Dictionary<int, string>();
        private static readonly bool Reporting = Probe.On;

        // DrawHook, while capturing: one mesh the game drew.
        public static void Note(Mesh mesh, Matrix4x4 matrix, Material material)
        {
            if (ApparelModels.Swallowing)
            {
                ApparelModels.GameDrew = true;   // a bound shield's own worn picture: its model stands in (People.Draw)
                return;
            }
            caught.Add(new Caught { mesh = mesh, matrix = matrix, material = material, weapon = drawingWeapon, carried = carryPath });
        }

        // Patches.DrawEquipmentAiming_Prefix / _Finalizer: the game is drawing this weapon (null when done).
        public static void Weapon(Thing weapon)
        {
            if (Capturing)
            {
                drawingWeapon = weapon;
            }
        }

        // PawnRenderUtility.DrawCarriedWeapon: the game chose the carry pose.
        public static void DrawCarriedWeapon_Prefix()
        {
            if (Capturing)
            {
                carryPath = true;
            }
        }

        public static void DrawCarriedWeapon_Finalizer()
        {
            carryPath = false;
        }

        // A worn extra drawn straight to Unity (CompShield's bubble): caught while capturing, else drawn as before.
        public static void DrawOrCatch(Mesh mesh, Matrix4x4 matrix, Material material, int layer)
        {
            if (Capturing)
            {
                Note(mesh, matrix, material);
                return;
            }
            Graphics.DrawMesh(mesh, matrix, material, layer);
        }

        private static readonly MethodInfo drawMesh4 = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh),
            new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int) });

        public static IEnumerable<CodeInstruction> CatchDraws_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo to = AccessTools.Method(typeof(FigureWeapons), nameof(DrawOrCatch));
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand as MethodInfo == drawMesh4)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = to;
                }
                yield return ci;
            }
        }

        // Whether the game aims this pawn's weapon now (Patches.CardAim: its tests), and the aim as drawn: flat, degrees
        // clockwise from north, toward where the target shows (another floor too), or the verb's own aim (a spray gun).
        public static bool AimOf(Pawn pawn, bool neverAim, out float game, out float drawn)
        {
            float seen;
            drawn = 0f;
            if (!Patches.CardAim(pawn, neverAim, out game, out seen))
            {
                return false;
            }
            drawn = game;
            Stance_Busy busy = (Stance_Busy)pawn.stances.curStance;
            Verb verb = busy.verb;   // (the stance's verb, as CardAim: thread-safe)
            if (verb != null && verb.AimAngleOverride.HasValue)
            {
                return true;
            }
            Vector3 to = busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted();
            Vector3 d = Floors.FlatDelta(pawn.DrawPos, to);
            if (d.x * d.x + d.z * d.z > 0.001f)
            {
                drawn = d.AngleFlat();
            }
            return true;
        }

        // The figure's weapon and worn extras. at: the figure's base (x, z in the game's rows, y its height over the floor,
        // cells), yaw its facing, local its pose in figure space (People: pose and size), size its size, front how far its
        // chest reaches forward and side how far its body reaches to the side (figure units, size 1). measure: nothing is
        // drawn, only where the aimed weapon is (Projectiles.FigureTip), for a figure not drawn now (People.MeasureWeapon).
        public static void Draw(Pawn pawn, bool neverAim, Vector3 drawLoc, Vector3 at, float yaw, Matrix4x4 local, float size, float front, float side, bool measure = false)
        {
            caught.Clear();
            ApparelModels.GameDrew = false;
            Capturing = true;
            try
            {
                PawnRenderUtility.DrawEquipmentAndApparelExtras(pawn, new Vector3(drawLoc.x, 0f, drawLoc.z), Rot4.South,
                    neverAim ? PawnRenderFlags.NeverAimWeapon : PawnRenderFlags.None);
            }
            finally
            {
                Capturing = false;
                drawingWeapon = null;
                carryPath = false;
            }
            string report = Reporting && !measure ? "" : null;
            if (caught.Count == 0)
            {
                if (report != null)
                {
                    reports[pawn.thingIDNumber] = "nothing drawn, figure yaw " + yaw.ToString("0") + (BodyAnims.Facing(pawn) != pawn.Rotation ? "; mod facing " + BodyAnims.Facing(pawn) : "");
                }
                return;
            }
            float s = View3D.Squash;
            Matrix4x4 placed = Matrix4x4.Translate(new Vector3(at.x, at.y / s, at.z)) * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up));
            Matrix4x4 figure = Matrix4x4.Translate(at) * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up)) * local;   // true cells
            Vector3 origin = new Vector3(drawLoc.x, 0f, drawLoc.z);
            bool weapons = IsoRimMod.Settings.heldWeapons3D;
            bool models = WeaponModels.On;   // the new hold (models, the shoulder); off: as before
            for (int i = 0; i < caught.Count; i++)
            {
                Caught c = caught[i];
                // The game's drawing about the pawn's middle, flat (its drawing layers' heights dropped).
                Matrix4x4 m = Matrix4x4.Translate(-origin) * c.matrix;
                m.m13 = 0f;
                // The game draws its plane with no height (scale 0 along its normal): a unit normal, for the slab's thickness.
                Vector3 normal = Vector3.Cross(m.GetColumn(2), m.GetColumn(0)).normalized;
                m.SetColumn(1, new Vector4(normal.x, normal.y, normal.z, 0f));
                bool flipped = c.mesh == MeshPool.plane10Flip;
                if (measure && (c.weapon == null || c.carried))
                {
                    continue;
                }
                if (c.weapon == null)
                {
                    DrawExtra(pawn, c, drawLoc, at, size);
                    if (report != null)
                    {
                        report += "extra " + (c.material != null ? c.material.name : "?") + "; ";
                    }
                }
                else if (!weapons)
                {
                    continue;
                }
                else if (c.carried)
                {
                    Matrix4x4 slab = placed * local * OnChest(m, flipped, front, side);
                    bool asModel = DrawPiece(c, slab, models);
                    if (report != null)
                    {
                        report += "carried " + c.weapon.def.defName + (flipped ? " (flipped)" : "") + " angle " + Angle(m).ToString("0") + (asModel ? " model" : "") + "; ";
                    }
                }
                else
                {
                    float game, aim;
                    bool known = AimOf(pawn, neverAim, out game, out aim);
                    if (!known)
                    {
                        game = aim = c.matrix.rotation.eulerAngles.y + 90f;   // a mod aiming on its own: along the picture
                    }
                    Vector3 dir = Quaternion.AngleAxis(aim, Vector3.up) * Vector3.forward;
                    Vector3 right = Vector3.Cross(Vector3.up, dir);
                    Matrix4x4 world;
                    string how;
                    float pitch;
                    WeaponModels.Hold hold = models ? HoldFor(c) : null;
                    Model model = hold != null ? WeaponModels.ModelOf(c.weapon) : null;
                    if (models && known && AtShoulder(c.weapon.def))
                    {
                        // A ranged weapon (with the new hold): its butt at the right shoulder, along the aim, other mods' motion
                        // on top as it differs from the game's own placing (Shoulder).
                        Vector3 butt = hold != null ? hold.butt : WeaponModels.SpriteButt(c.material, c.weapon.Graphic.drawSize, c.weapon.def.equippedAngleOffset);
                        if (!Shoulder(pawn, c, m, flipped, game, aim, dir, right, figure, size, front, side, butt, out world, out pitch, out how))
                        {
                            if (report != null)
                            {
                                report += "aimed " + c.weapon.def.defName + " hidden (" + how + "); ";
                            }
                            continue;
                        }
                    }
                    else
                    {
                        // Onto the aim as drawn (another floor: the target shows elsewhere than in the game's rows).
                        Matrix4x4 turned = Matrix4x4.Rotate(Quaternion.AngleAxis(Mathf.DeltaAngle(game, aim), Vector3.up)) * m;
                        // Stood up about the aim line: the side of it nearest the picture's top goes up.
                        Quaternion standUp = StandUp(turned, right);
                        Vector3 hands = figure.MultiplyPoint3x4(new Vector3(0f, HandY, 0f));
                        pitch = PitchOf(pawn, hands.y);
                        world = Matrix4x4.Translate(hands) * Matrix4x4.Rotate(Raise(dir, pitch) * standUp)
                            * Matrix4x4.Scale(new Vector3(Picture * size, Picture * size, Picture * size)) * turned * SlabToPlane(flipped);
                        how = "hands " + hands.ToString("F2");
                    }
                    bool asModel = false;
                    if (!measure)
                    {
                        asModel = DrawPiece(c, Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f)) * world, models);
                    }
                    if (model != null)
                    {
                        Projectiles.FigureTipAt(pawn, c.weapon, world.MultiplyPoint3x4(hold.tip), world.MultiplyPoint3x4(Vector3.zero));
                    }
                    else
                    {
                        Projectiles.FigureTip(pawn, c.weapon, world);
                    }
                    if (report != null)
                    {
                        Vector3 tip;
                        int age;
                        Projectiles.LastTip(pawn, out tip, out age);
                        report += "aimed " + c.weapon.def.defName + (flipped ? " (flipped)" : "") + " game " + game.ToString("0") + " drawn " + aim.ToString("0")
                            + " pitch " + pitch.ToString("0.0") + " picture angle " + Angle(m).ToString("0") + " reach " + new Vector2(m.m03, m.m23).magnitude.ToString("0.000")
                            + " " + how + " tip " + tip.ToString("F3") + " figure yaw " + yaw.ToString("0") + (asModel ? " model" : "") + "; ";
                    }
                }
            }
            if (report != null)
            {
                Vector3 shift;
                float turn;
                if (BodyAnims.Facing(pawn) != pawn.Rotation)
                {
                    report += "mod facing " + BodyAnims.Facing(pawn) + "; ";
                }
                if (BodyAnims.Active && BodyAnims.Read(pawn, drawLoc, out shift, out turn))
                {
                    report += "body anim shift " + shift.ToString("F3") + " turn " + turn.ToString("0.0") + "; ";
                }
                reports[pawn.thingIDNumber] = report;
            }
        }

        // The harness: what was drawn for this pawn last.
        public static string Report(Pawn pawn)
        {
            string r;
            return reports.TryGetValue(pawn.thingIDNumber, out r) ? r : "not drawn as a figure yet";
        }

        // The front picture on the chest (figure space): picture x to the figure's left (as seen from its front), picture up
        // (north) to up, at the body's middle, turned about the body's side so the muzzle comes forward. m: the game's flat
        // picture about the pawn's middle (its normal a unit column). Gives SpriteSolids' slab.
        private static Matrix4x4 OnChest(Matrix4x4 m, bool flipped, float front, float side)
        {
            return Matrix4x4.Translate(new Vector3(0f, PersonHeight * 0.5f, front + WeaponGap))
                * Matrix4x4.Translate(new Vector3(side * 0.8f, 0f, 0f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(Tilt, Vector3.up))
                * Matrix4x4.Translate(new Vector3(-side * 0.8f, 0f, 0f))
                * Matrix4x4.Scale(new Vector3(Picture, Picture, Picture)) * PictureToFront * m * SlabToPlane(flipped);
        }

        // The weapon's piece drawn at slab (SpriteSolids' slab matrix, drawn space before the floor shift): its 3D model through
        // its hold frame (models: the setting), else its sprite made solid. True if the model was drawn.
        private static bool DrawPiece(Caught c, Matrix4x4 slab, bool models)
        {
            WeaponModels.Hold hold = models ? HoldFor(c) : null;
            Model model = hold != null ? WeaponModels.ModelOf(c.weapon) : null;
            if (model != null)
            {
                WeaponModels.Draw(c.weapon, model, hold, slab);
                return true;
            }
            SpriteSolids.DrawSlab(c.material, Floors.DrawnMatrix(slab));
            return false;
        }

        // The weapon's model fitted to the sprite the game drew (null: no model, or none to fit).
        private static WeaponModels.Hold HoldFor(Caught c)
        {
            Model model = WeaponModels.ModelOf(c.weapon);
            Graphic g = model != null ? c.weapon.Graphic : null;
            WeaponModels.Hold hold = g != null ? WeaponModels.HoldOf(model, c.material, g.drawSize, c.weapon.def.equippedAngleOffset) : null;
            return hold != null && Projectiles.Thrown(c.weapon.def) ? WeaponModels.AtGroundSize(hold, model, c.weapon.def, Picture) : hold;
        }

        // A weapon aimed from the right shoulder: every ranged weapon (bows too) but what is thrown (a grenade, a pilum: from
        // the hands, as before); melee weapons keep the hands.
        private static bool AtShoulder(ThingDef def)
        {
            return def.IsRangedWeapon && !Projectiles.Thrown(def);
        }

        private static Quaternion StandUp(Matrix4x4 turned, Vector3 right)
        {
            Vector3 top = ((Vector3)turned.GetColumn(2)).normalized;
            Vector3 side2 = Vector3.Dot(top, right) >= 0f ? right : -right;
            return Quaternion.FromToRotation(side2, Vector3.up);
        }

        private static Quaternion Raise(Vector3 dir, float pitch)
        {
            return Quaternion.FromToRotation(dir, dir * Mathf.Cos(pitch * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(pitch * Mathf.Deg2Rad));
        }

        // The game's own placing of an aimed weapon (PawnRenderUtility.DrawEquipmentAndApparelExtras / DrawEquipmentAiming,
        // 1.7) about the pawn's middle, flat, without the recoil: the centre 0.4 (plus equippedDistanceOffset) toward the aim,
        // times the life stage's equipmentDrawDistanceFactor; turned aim - 90 + equippedAngleOffset, or with the flipped mesh
        // aim - 270 - equippedAngleOffset; the drawn size. Its normal a unit column, as the caught pictures get.
        private static Matrix4x4 Expected(Pawn pawn, Thing weapon, float aim, bool flipped)
        {
            float factor = pawn.ageTracker != null && pawn.ageTracker.CurLifeStage != null ? pawn.ageTracker.CurLifeStage.equipmentDrawDistanceFactor : 1f;
            Vector3 pos = new Vector3(0f, 0f, 0.4f + weapon.def.equippedDistanceOffset).RotatedBy(aim) * factor;
            float angle = flipped ? aim - 270f - weapon.def.equippedAngleOffset : aim - 90f + weapon.def.equippedAngleOffset;
            Vector2 ds = weapon.Graphic != null ? weapon.Graphic.drawSize : Vector2.one;
            Matrix4x4 e = Matrix4x4.TRS(pos, Quaternion.AngleAxis(angle % 360f, Vector3.up), new Vector3(ds.x, 1f, ds.y));
            return e;
        }

        // A ranged weapon aimed from the right shoulder (true cells: x, z in the game's rows, y over the floor): SpriteSolids'
        // slab with its butt (slab space) at the shoulder (the settings: to the pawn's right of its aim, up, forward; for the
        // figure's build and size), its barrel along the aim, raised toward the target's height about the shoulder. On top goes
        // whatever the drawn picture m differs by from the game's own placing (Expected, with the mesh it drew): other mods'
        // animation and the game's recoil, as a shift along and across the aim, a turn about the weapon's middle, a stretch;
        // shrunk to nothing it is hidden (false).
        private static bool Shoulder(Pawn pawn, Caught c, Matrix4x4 m, bool flipped, float game, float aim, Vector3 dir, Vector3 right, Matrix4x4 figure,
            float size, float front, float side, Vector3 butt, out Matrix4x4 world, out float pitch, out string how)
        {
            world = Matrix4x4.identity;
            pitch = 0f;
            Matrix4x4 e = Expected(pawn, c.weapon, game, flipped);
            Vector3 fGame = Quaternion.AngleAxis(game, Vector3.up) * Vector3.forward;
            Vector3 rGame = Vector3.Cross(Vector3.up, fGame);
            Vector3 dPos = (Vector3)m.GetColumn(3) - (Vector3)e.GetColumn(3);
            dPos.y = 0f;
            float along = Vector3.Dot(dPos, fGame), across = Vector3.Dot(dPos, rGame);
            Vector3 ex = e.GetColumn(0), cx = m.GetColumn(0);
            ex.y = 0f;
            cx.y = 0f;
            float turn = ex.sqrMagnitude > 1e-8f && cx.sqrMagnitude > 1e-8f ? Vector3.SignedAngle(ex, cx, Vector3.up) : 0f;
            float stretchX = ((Vector3)m.GetColumn(0)).magnitude / Mathf.Max(1e-5f, ((Vector3)e.GetColumn(0)).magnitude);
            float stretchY = ((Vector3)m.GetColumn(2)).magnitude / Mathf.Max(1e-5f, ((Vector3)e.GetColumn(2)).magnitude);
            how = "shift " + along.ToString("0.000") + "/" + across.ToString("0.000") + " turn " + turn.ToString("0.0") + " stretch " + stretchX.ToString("0.00") + "/" + stretchY.ToString("0.00");
            if (stretchX < 0.05f || stretchY < 0.05f)
            {
                return false;
            }
            // The game's own placing stood up about the aim as drawn (the side nearest the picture's top up), about its middle.
            Matrix4x4 eFlat = e;
            eFlat.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
            Vector3 normal = Vector3.Cross(eFlat.GetColumn(2), eFlat.GetColumn(0)).normalized;
            eFlat.SetColumn(1, new Vector4(normal.x, normal.y, normal.z, 0f));
            Matrix4x4 turned = Matrix4x4.Rotate(Quaternion.AngleAxis(Mathf.DeltaAngle(game, aim), Vector3.up)) * eFlat;
            float k = Picture * size;
            Matrix4x4 stood = Matrix4x4.Rotate(StandUp(turned, right)) * Matrix4x4.Scale(new Vector3(k, k, k)) * turned * SlabToPlane(flipped);
            Vector3 buttAt = stood.MultiplyPoint3x4(butt);
            // The shoulder: to the pawn's right of its aim, scaled to its build (a man's shoulders: half width 0.231, chest 0.134).
            IsoRimSettings st = IsoRimMod.Settings;
            Vector3 shoulder = figure.MultiplyPoint3x4(new Vector3(0f, st.aimHeight, 0f))
                + right * (st.aimSide * size * (side / 0.231f)) + dir * (st.aimReach * size * (front / 0.134f));
            pitch = PitchOf(pawn, shoulder.y);
            world = Matrix4x4.Translate(shoulder) * Matrix4x4.Rotate(Raise(dir, pitch)) * Matrix4x4.Translate((dir * along + right * across) * k)
                * Matrix4x4.Translate(-buttAt) * Matrix4x4.Rotate(Quaternion.AngleAxis(turn, Vector3.up)) * stood * Matrix4x4.Scale(new Vector3(stretchX, stretchY, 1f));
            how += " shoulder " + shoulder.ToString("F2");
            return true;
        }

        // An outfit stand's weapon (Stands), as a colonist carries it: the game's carry picture of the stand seen from the south
        // (PawnRenderUtility.DrawCarriedWeapon, caught), on the chest of the figure it is dressed as. ground: the stand's middle on
        // the ground; yaw its figure's facing; local the figure (its lift onto the stand and size); front and side its body's
        // reach (figure units). False if the game drew nothing.
        public static bool DrawOnStand(ThingWithComps weapon, float factor, Vector3 ground, float yaw, Matrix4x4 local, float front, float side)
        {
            caught.Clear();
            Capturing = true;
            try
            {
                PawnRenderUtility.DrawCarriedWeapon(weapon, Vector3.zero, Rot4.South, factor);
            }
            finally
            {
                Capturing = false;
                drawingWeapon = null;
                carryPath = false;
            }
            float s = View3D.Squash;
            Matrix4x4 placed = Matrix4x4.Translate(new Vector3(ground.x, ground.y / s, ground.z)) * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up));
            bool any = false;
            for (int i = 0; i < caught.Count; i++)
            {
                Caught c = caught[i];
                if (c.weapon == null)
                {
                    continue;
                }
                Matrix4x4 m = c.matrix;
                m.m13 = 0f;
                Vector3 normal = Vector3.Cross(m.GetColumn(2), m.GetColumn(0)).normalized;
                m.SetColumn(1, new Vector4(normal.x, normal.y, normal.z, 0f));
                DrawPiece(c, placed * local * OnChest(m, c.mesh == MeshPool.plane10Flip, front, side), true);
                any = true;
            }
            caught.Clear();
            return any;
        }

        private static float Angle(Matrix4x4 m)
        {
            return Mathf.Atan2(m.m02, m.m22) * Mathf.Rad2Deg;   // where the picture's top points, clockwise from north
        }

        // The game's flat picture (x, z; y its normal) onto the figure's front (figure space: x its right, y up, z forward):
        // picture x to the figure's left, picture z (up the picture) up, the picture's normal forward.
        private static readonly Matrix4x4 PictureToFront = new Matrix4x4(
            new Vector4(-1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));

        // SpriteSolids' slab (sprite across x, up y, thickness z) onto the game's plane mesh (x, z; the flipped mesh shows the
        // sprite mirrored in x). Both keep the handedness (the slab is the same on both faces).
        private static readonly Matrix4x4 SlabPlane = new Matrix4x4(
            new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, -1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));
        private static readonly Matrix4x4 SlabPlaneFlip = new Matrix4x4(
            new Vector4(-1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));

        private static Matrix4x4 SlabToPlane(bool flipped)
        {
            return flipped ? SlabPlaneFlip : SlabPlane;
        }

        // How far the aim is raised (degrees, down negative): from the hands to the target's middle, between floors as drawn.
        private static float PitchOf(Pawn pawn, float handsY)
        {
            Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
            if (busy == null || !busy.focusTarg.IsValid)
            {
                return 0f;
            }
            Vector3 from = pawn.DrawPos;
            Vector3 to = busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted();
            Vector3 a = from, b = to;
            Floors.Placed(ref a);   // (a hidden floor too: a figure measured there, People.MeasureWeapon)
            Floors.Placed(ref b);
            float rise = ((b.y - to.y) - (a.y - from.y)) * View3D.Squash;   // the floors' height between them, cells
            Vector3 d = b - a;
            d.y = 0f;
            float flat = Mathf.Max(d.magnitude, 0.5f);
            float targetY = rise + TargetHeight(busy.focusTarg);
            float p = Mathf.Atan2(targetY - handsY, flat) * Mathf.Rad2Deg;
            return Mathf.Clamp(p, -MaxPitch, MaxPitch);
        }

        // How high the target's middle is over its floor (Projectiles.AimHeight's measure).
        private static float TargetHeight(LocalTargetInfo t)
        {
            if (!t.HasThing)
            {
                return 0.1f;
            }
            if (t.Thing is Pawn p)
            {
                return p.Dead || p.GetPosture() != PawnPosture.Standing ? 0.12f : Interaction.PickHeight(p) * 0.45f;
            }
            return Mathf.Clamp(Interaction.PickHeight(t.Thing) * 0.5f, 0.05f, 1f);
        }

        // A worn extra (the shield bubble): a card facing the camera round the figure's middle, keeping its offset from the
        // pawn (an impact's jolt), its turn and size as the game draws it.
        private static void DrawExtra(Pawn pawn, Caught c, Vector3 drawLoc, Vector3 at, float size)
        {
            Vector3 p = c.matrix.GetColumn(3);
            Vector3 pawnPos = pawn.Drawer.DrawPos;
            Matrix4x4 local = Matrix4x4.Translate(new Vector3(-p.x, -p.y, -p.z)) * c.matrix;
            float s = View3D.Squash;
            Vector3 centre = new Vector3(drawLoc.x + (p.x - pawnPos.x), at.y + PersonHeight * 0.5f * size, drawLoc.z + (p.z - pawnPos.z));
            Matrix4x4 m = Matrix4x4.Translate(new Vector3(centre.x, centre.y / s, centre.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(View3D.Pitch - 90f, View3D.Yaw, 0f))
                * Matrix4x4.Scale(new Vector3(size, 1f, size))
                * local;
            m = Floors.DrawnMatrix(m);
            if (!ShipCapture.Note(c.mesh, m, c.material, null))
            {
                Graphics.DrawMesh(c.mesh, m, c.material, 0);
            }
        }
    }
}
