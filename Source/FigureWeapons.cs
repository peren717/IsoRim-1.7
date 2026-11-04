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
        // The game's picture units to figure units (2026-10-31, weaponfit): the game draws a man facing south 1.113 to 1.137
        // cells tall, feet to the top of his bare head (body and head pictures, opaque pixels; 1.125 the middle of the head
        // types; harness key vanillaheight), a figure of size 1 is 1.25 (person_head's top; Interaction.PickHeight). Every
        // picture the game draws for the pawn (its weapon, a mod's body animation) is scaled by this onto the figure, so the
        // weapon is as long against the figure as against the game's pawn (was 1.12 / 1.5: the whole 1.5-cell mesh, 33% small).
        internal const float FigureTop = 1.25f;
        internal const float VanillaHeight = 1.125f;
        internal const float Picture = FigureTop / VanillaHeight;
        private const float Middle = 0.56f;         // the body's middle on a figure of size 1 (the carried weapon, worn extras)
        private const float HandY = 0.46f;          // the hands on a figure of size 1 (People.HandY)
        private const float WeaponGap = 0.05f;      // between the chest and the carried weapon, figure units
        private const float Tilt = 35f;             // the carried weapon's turn about the figure's up: muzzle forward
        private const float MaxPitch = 70f;
        private const bool Guard = true;
        internal const float CarryClear = 0.13f;    // ModelDef.carryStand: the least the weapon stands ahead of the chest

        private struct Caught
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material material;
            public Thing weapon;   // null: not a weapon (a worn extra)
            public bool carried;
            public Recoil recoil;  // the recoil in this drawing (Shoulder takes it apart from other mods' motion)
            public bool own;       // (Api 54) drawn by a mod's own method (CatchWeaponDraws): it placed it, not kept clear of the body
        }

        // The recoil in the weapon picture being drawn (2026-10-31, headrecoil): the game's EquipmentUtility.Recoil as it gave
        // it (a shift, mostly back along the aim, and a turn of random side in the picture's plane) and Flourish's rise (the
        // turn it added to the aim, and its rise in degrees, up positive; Compat/FlourishMuzzle). Seen flat from above both
        // turns read as the muzzle climbing; on a figure Shoulder makes them a raise of the muzzle about the shoulder instead
        // of a turn sideways.
        public struct Recoil
        {
            public Vector3 shift;
            public float turn;
            public float riseTurn;
            public float rise;
        }

        private static readonly List<Caught> caught = new List<Caught>();
        public static bool Capturing;
        private static Thing drawingWeapon;
        private static bool carryPath;
        private static Recoil recoilNow;

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
            caught.Add(new Caught { mesh = mesh, matrix = matrix, material = material, weapon = drawingWeapon, carried = carryPath,
                recoil = drawingWeapon != null ? recoilNow : default(Recoil), own = catchingOwn });
        }

        // Patches.DrawEquipmentAiming_Prefix / _Finalizer: the game is drawing this weapon (null when done). (The prefix runs
        // first of all, before Flourish's.)
        public static void Weapon(Thing weapon)
        {
            recoilNow = default(Recoil);
            if (Capturing)
            {
                drawingWeapon = weapon;
            }
        }

        // Patches.Recoil_Postfix: EquipmentUtility.Recoil gave this (after other mods' postfixes) while a figure's weapon is drawn.
        public static void NoteRecoil(Vector3 drawOffset, float angleOffset)
        {
            if (Capturing && drawingWeapon != null)
            {
                recoilNow.shift = drawOffset;
                recoilNow.turn = angleOffset;
            }
        }

        // Compat/FlourishMuzzle: Flourish turned the aim by turn for its rise (degrees, up positive) while a figure's weapon is drawn.
        public static void FlourishRise(float turn, float rise)
        {
            if (Capturing && drawingWeapon != null)
            {
                recoilNow.riseTurn += turn;
                recoilNow.rise += rise;
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

        // (Api 54) A draw another mod's method makes straight to Unity (Api.CatchWeaponDraws): caught while a figure's weapon is
        // drawn (with a model or as its sprite made solid, as the game's own draw is); anything else goes on to Unity as that mod
        // wrote it: a pawn drawn as a card, the 2D view.
        public static void DrawOrCatchWeapon(Mesh mesh, Matrix4x4 matrix, Material material, int layer)
        {
            if (Capturing && drawingWeapon != null)
            {
                catchingOwn = true;
                Note(mesh, matrix, material);
                catchingOwn = false;
                return;
            }
            Graphics.DrawMesh(mesh, matrix, material, layer);
        }

        private static bool catchingOwn;

        public static IEnumerable<CodeInstruction> CatchWeaponDraws_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo to = AccessTools.Method(typeof(FigureWeapons), nameof(DrawOrCatchWeapon));
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

        private static readonly HashSet<MethodBase> catching = new HashSet<MethodBase>();
        private static Harmony catchHarmony;

        // Api.CatchWeaponDraws: false (one warning) if the method makes no such draw or cannot be patched.
        internal static bool CatchDrawsOf(MethodBase method)
        {
            if (method == null)
            {
                throw new ArgumentNullException(nameof(method));
            }
            if (catching.Contains(method))
            {
                return true;
            }
            try
            {
                bool draws = false;
                foreach (KeyValuePair<OpCode, object> instruction in PatchProcessor.ReadMethodBody(method))
                {
                    draws |= instruction.Value as MethodInfo == drawMesh4;
                }
                if (!draws)
                {
                    Log.Warning("[IsoRim] CatchWeaponDraws: " + method.DeclaringType + "." + method.Name + " makes no Graphics.DrawMesh(mesh, matrix, material, layer) call.");
                    return false;
                }
                catchHarmony = catchHarmony ?? new Harmony("isorim.api54");
                catchHarmony.Patch(method, transpiler: new HarmonyMethod(typeof(FigureWeapons), nameof(CatchWeaponDraws_Transpiler)));
                catching.Add(method);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] CatchWeaponDraws: " + method.DeclaringType + "." + method.Name + " could not be patched: " + e.Message);
                return false;
            }
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
            Thing modelled = null;           // (Api 54) a weapon drawn in several pictures shows one model
            Thing pictured = null;           // or as its sprites, one over the other, moved clear of the body as one
            int layer = 0;
            Vector3 sharedClear = Vector3.zero;
            float sharedNeed = 0f;
            for (int i = 0; i < caught.Count; i++)
            {
                Caught c = caught[i];
                if (c.weapon != null && c.weapon == modelled)
                {
                    continue;
                }
                layer = c.weapon != null && c.weapon == pictured ? layer + 1 : 0;
                pictured = c.weapon;
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
                    bool stood = models && CarriedStanding(pawn, c.weapon, placed * local, front);
                    bool asModel = stood || DrawPiece(c, placed * local * OnChest(m, flipped, front, side) * Over(layer), models);
                    if (asModel)
                    {
                        modelled = c.weapon;
                    }
                    if (report != null)
                    {
                        report += "carried " + c.weapon.def.defName + (flipped ? " (flipped)" : "") + " angle " + Angle(m).ToString("0") + (asModel ? " model" : "") + "; ";
                    }
                }
                else if (models && !measure && !c.weapon.def.IsRangedWeapon && CarriedStanding(pawn, c.weapon, placed * local, front))
                {
                    // (Api 54, carryStand) a melee swing keeps it standing: the figure's lunge is the swing
                    modelled = c.weapon;
                    if (report != null)
                    {
                        report += "swung " + c.weapon.def.defName + " standing; ";
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
                    // Kept out of the body: moved along the aim until its box clears the body (Clearance).
                    float need;
                    Vector3 clear = sharedClear;
                    need = sharedNeed;
                    if (layer == 0)
                    {
                        clear = sharedClear = Clearance(c, world, hold, model, figure, dir, front, side, out need);
                        sharedNeed = need;
                    }
                    if (Guard && need > 0f && !c.own)
                    {
                        world = Matrix4x4.Translate(clear) * world;
                    }
                    how += " clear " + need.ToString("0.000");
                    bool asModel = false;
                    if (!measure)
                    {
                        asModel = DrawPiece(c, Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f)) * world * Over(layer), models);
                    }
                    if (model != null)
                    {
                        modelled = c.weapon;
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
            return Matrix4x4.Translate(new Vector3(0f, Middle, front + WeaponGap))
                * Matrix4x4.Translate(new Vector3(side * 0.8f, 0f, 0f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(Tilt, Vector3.up))
                * Matrix4x4.Translate(new Vector3(-side * 0.8f, 0f, 0f))
                * Matrix4x4.Scale(new Vector3(Picture, Picture, Picture)) * PictureToFront * m * SlabToPlane(flipped);
        }

        // (Api 54) A sprite slab over the pictures of the same weapon before it: a little thicker, so that its faces show on both
        // sides instead of fighting theirs (a banner's field and its symbol). The first picture, as ever.
        private static Matrix4x4 Over(int layer)
        {
            return layer == 0 ? Matrix4x4.identity : Matrix4x4.Scale(new Vector3(1f, 1f, 1f + 0.3f * layer));
        }

        // (Api 54, ModelDef.carryStand) The model standing up in front of the figure. figure: drawn space, the figure's own (x its
        // right, y up, z forward, its pose and size in); front how far its chest reaches. pawn null: an outfit stand. False: no
        // model, or none that carries so.
        private static bool CarriedStanding(Pawn pawn, Thing weapon, Matrix4x4 figure, float front)
        {
            Model model = WeaponModels.ModelOf(weapon);
            ModelEntry entry;
            if (model == null || !ModelLibrary.TryGet(weapon.def, out entry) || entry.def.carryStand.y < 0f)
            {
                return false;
            }
            Vector3 stand = entry.def.carryStand;
            float lean = pawn != null && pawn.pather != null && pawn.pather.Moving ? entry.def.carryLean : 0f;
            Matrix4x4 at = figure * Matrix4x4.Translate(new Vector3(stand.x, HandY, Mathf.Max(stand.z, front + CarryClear)))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(lean, Vector3.right)) * Matrix4x4.Translate(new Vector3(0f, stand.y - HandY, 0f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.up)) * Matrix4x4.Scale(new Vector3(Picture, Picture, Picture));
            WeaponModels.DrawAt(weapon, model, at);
            return true;
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
            WeaponModels.Hold hold = g != null ? WeaponModels.HoldOf(model, c.material, g.drawSize, c.weapon.def.equippedAngleOffset, WeaponModels.AsBuilt(c.weapon)) : null;
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
        // shift, turn: the recoil's (Recoil), put in as the game puts its own (the drawn centre moved, the picture turned).
        private static Matrix4x4 Expected(Pawn pawn, Thing weapon, float aim, bool flipped, Vector3 shift, float turn)
        {
            float factor = pawn.ageTracker != null && pawn.ageTracker.CurLifeStage != null ? pawn.ageTracker.CurLifeStage.equipmentDrawDistanceFactor : 1f;
            Vector3 pos = new Vector3(0f, 0f, 0.4f + weapon.def.equippedDistanceOffset).RotatedBy(aim) * factor + new Vector3(shift.x, 0f, shift.z);
            float angle = (flipped ? aim - 270f - weapon.def.equippedAngleOffset : aim - 90f + weapon.def.equippedAngleOffset) + turn;
            Vector2 ds = weapon.Graphic != null ? weapon.Graphic.drawSize : Vector2.one;
            Matrix4x4 e = Matrix4x4.TRS(pos, Quaternion.AngleAxis(angle % 360f, Vector3.up), new Vector3(ds.x, 1f, ds.y));
            return e;
        }

        // A ranged weapon aimed from the right shoulder (true cells: x, z in the game's rows, y over the floor): SpriteSolids'
        // slab with its butt (slab space) at the shoulder (the settings: to the pawn's right of its aim, up, forward; for the
        // figure's build and size), its barrel along the aim, raised toward the target's height about the shoulder. On top goes
        // whatever the drawn picture m differs by from the game's own placing with its recoil (Expected, with the mesh it drew):
        // other mods' animation, as a shift along and across the aim, a turn about the weapon's middle, a stretch; shrunk to
        // nothing it is hidden (false). The recoil (the game's, Flourish's rise: Recoil) kicks it back along the aim and raises
        // the muzzle about the shoulder (was: turned it sideways with the rest, 2026-10-31).
        private static bool Shoulder(Pawn pawn, Caught c, Matrix4x4 m, bool flipped, float game, float aim, Vector3 dir, Vector3 right, Matrix4x4 figure,
            float size, float front, float side, Vector3 butt, out Matrix4x4 world, out float pitch, out string how)
        {
            world = Matrix4x4.identity;
            pitch = 0f;
            Matrix4x4 e = Expected(pawn, c.weapon, game, flipped, Vector3.zero, 0f);
            // The recoil apart (Recoil): the picture as the game places it with the recoil in, so what is left over is other
            // mods' motion; the recoil itself goes on as a kick back along the aim (its shift's part along it; the small shift
            // to a random side dropped) and a raise of the muzzle about the shoulder (its turns, the game's of random side).
            Recoil r = c.recoil;
            Matrix4x4 eSeen = Expected(pawn, c.weapon, game, flipped, r.shift, r.turn + r.riseTurn);
            Vector3 fGame = Quaternion.AngleAxis(game, Vector3.up) * Vector3.forward;
            Vector3 rGame = Vector3.Cross(Vector3.up, fGame);
            Vector3 dPos = (Vector3)m.GetColumn(3) - (Vector3)eSeen.GetColumn(3);
            dPos.y = 0f;
            float kick = Vector3.Dot(new Vector3(r.shift.x, 0f, r.shift.z), fGame);
            float climb = Mathf.Abs(r.turn) + r.rise;
            float along = Vector3.Dot(dPos, fGame) + kick, across = Vector3.Dot(dPos, rGame);
            Vector3 ex = eSeen.GetColumn(0), cx = m.GetColumn(0);
            ex.y = 0f;
            cx.y = 0f;
            float turn = ex.sqrMagnitude > 1e-8f && cx.sqrMagnitude > 1e-8f ? Vector3.SignedAngle(ex, cx, Vector3.up) : 0f;
            float stretchX = ((Vector3)m.GetColumn(0)).magnitude / Mathf.Max(1e-5f, ((Vector3)e.GetColumn(0)).magnitude);
            float stretchY = ((Vector3)m.GetColumn(2)).magnitude / Mathf.Max(1e-5f, ((Vector3)e.GetColumn(2)).magnitude);
            how = "shift " + along.ToString("0.000") + "/" + across.ToString("0.000") + " turn " + turn.ToString("0.0") + " stretch " + stretchX.ToString("0.00") + "/" + stretchY.ToString("0.00")
                + " recoil kick " + kick.ToString("0.000") + " climb " + climb.ToString("0.0") + " (game turn " + r.turn.ToString("0.0") + ", rise " + r.rise.ToString("0.0") + "/" + r.riseTurn.ToString("0.0") + ")";
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
            pitch = Mathf.Clamp(PitchOf(pawn, shoulder.y) + climb, -MaxPitch, MaxPitch);
            world = Matrix4x4.Translate(shoulder) * Matrix4x4.Rotate(Raise(dir, pitch)) * Matrix4x4.Translate((dir * along + right * across) * k)
                * Matrix4x4.Translate(-buttAt) * Matrix4x4.Rotate(Quaternion.AngleAxis(turn, Vector3.up)) * stood * Matrix4x4.Scale(new Vector3(stretchX, stretchY, 1f));
            how += " shoulder " + shoulder.ToString("F2");
            return true;
        }

        // ------------------------------------------------------------------ keeping the weapon out of the body

        private const float BodyTop = 0.8f;      // the body's top as far as a weapon can meet it (the shoulders and the neck)
        private const float ClearGap = 0.015f;   // between the body and the weapon's nearest point, figure units
        private const float Thick = 0.02f;       // a sprite's slab, half its thickness (slab units)
        private static readonly Vector2[] outline = Outline(48);

        // The body's cross-section as a unit rounded box (a superellipse of power 4: |x|^4 + |z|^4 = 1), in figure space x, z.
        private static Vector2[] Outline(int n)
        {
            Vector2[] o = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float t = i * Mathf.PI * 2f / n;
                float c = Mathf.Cos(t), s = Mathf.Sin(t);
                o[i] = new Vector2(Mathf.Sign(c) * Mathf.Sqrt(Mathf.Abs(c)), Mathf.Sign(s) * Mathf.Sqrt(Mathf.Abs(s)));
            }
            return o;
        }

        private static readonly Dictionary<int, Bounds> modelBounds = new Dictionary<int, Bounds>();

        // The weapon's box (its model's bounds through its hold frame, else the opaque part of its sprite's slab) at world.
        private static void Corners(Caught c, Matrix4x4 world, WeaponModels.Hold hold, Model model, Vector3[] into)
        {
            Matrix4x4 to;
            Vector3 lo, hi;
            if (model != null && hold != null)
            {
                Bounds b;
                if (!modelBounds.TryGetValue(model.id, out b))
                {
                    Vector3 l = model.positions[0], h = model.positions[0];
                    for (int i = 1; i < model.positions.Length; i++)
                    {
                        l = Vector3.Min(l, model.positions[i]);
                        h = Vector3.Max(h, model.positions[i]);
                    }
                    b = new Bounds((l + h) * 0.5f, h - l);
                    modelBounds[model.id] = b;
                }
                to = world * hold.slab;
                lo = b.min;
                hi = b.max;
            }
            else
            {
                Rect r = WeaponModels.SpriteBox(c.material);
                to = world;
                lo = new Vector3(r.xMin, r.yMin, -Thick);
                hi = new Vector3(r.xMax, r.yMax, Thick);
            }
            for (int i = 0; i < 8; i++)
            {
                into[i] = to.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? lo.x : hi.x, (i & 2) == 0 ? lo.y : hi.y, (i & 4) == 0 ? lo.z : hi.z));
            }
        }

        private static readonly Vector3[] corners = new Vector3[8];

        // How far (true cells, flat) the weapon must move along dir to clear the figure's body: every point of its box at the
        // body's height in front of the body's surface along dir (the body a rounded box of half width side and half depth
        // front, figure space; figure the true-cells figure matrix, its size included). Zero if it is clear.
        private static Vector3 Clearance(Caught c, Matrix4x4 world, WeaponModels.Hold hold, Model model, Matrix4x4 figure, Vector3 dir, float front, float side, out float need)
        {
            need = 0f;
            Corners(c, world, hold, model, corners);
            Matrix4x4 inv = figure.inverse;
            Vector3 d = inv.MultiplyVector(dir);
            d.y = 0f;
            if (d.sqrMagnitude < 1e-8f || front <= 0f || side <= 0f)
            {
                return Vector3.zero;
            }
            d.Normalize();
            Vector2 d2 = new Vector2(d.x, d.z), e2 = new Vector2(d.z, -d.x);
            float lMin = float.MaxValue, lMax = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 q = inv.MultiplyPoint3x4(corners[i]);
                if (q.y < 0f || q.y > BodyTop)
                {
                    continue;
                }
                float l = q.x * e2.x + q.z * e2.y;
                lMin = Mathf.Min(lMin, l);
                lMax = Mathf.Max(lMax, l);
            }
            if (lMin > lMax)
            {
                return Vector3.zero;   // nothing at the body's height
            }
            // The body's furthest point along d over the weapon's lateral span (its outline's points within it, and where its
            // edges cross the span's ends).
            float reach = float.MinValue;
            int n = outline.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = new Vector2(outline[i].x * side, outline[i].y * front), p1 = new Vector2(outline[(i + 1) % n].x * side, outline[(i + 1) % n].y * front);
                float l0 = Vector2.Dot(p0, e2), l1 = Vector2.Dot(p1, e2);
                if (l0 >= lMin && l0 <= lMax)
                {
                    reach = Mathf.Max(reach, Vector2.Dot(p0, d2));
                }
                for (int k = 0; k < 2; k++)
                {
                    float end = k == 0 ? lMin : lMax;
                    if ((l0 - end) * (l1 - end) < 0f)
                    {
                        reach = Mathf.Max(reach, Vector2.Dot(Vector2.Lerp(p0, p1, (end - l0) / (l1 - l0)), d2));
                    }
                }
            }
            if (reach == float.MinValue)
            {
                return Vector3.zero;   // beside the body
            }
            for (int i = 0; i < 8; i++)
            {
                Vector3 q = inv.MultiplyPoint3x4(corners[i]);
                if (q.y >= 0f && q.y <= BodyTop)
                {
                    need = Mathf.Max(need, reach + ClearGap - (q.x * d2.x + q.z * d2.y));
                }
            }
            Vector3 shift = figure.MultiplyVector(new Vector3(d.x, 0f, d.z) * need);
            shift.y = 0f;
            return shift;
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
            Thing modelled = null;
            Thing pictured = null;
            int layer = 0;
            for (int i = 0; i < caught.Count; i++)
            {
                Caught c = caught[i];
                if (c.weapon == null || c.weapon == modelled)
                {
                    continue;
                }
                layer = c.weapon == pictured ? layer + 1 : 0;
                pictured = c.weapon;
                Matrix4x4 m = c.matrix;
                m.m13 = 0f;
                Vector3 normal = Vector3.Cross(m.GetColumn(2), m.GetColumn(0)).normalized;
                m.SetColumn(1, new Vector4(normal.x, normal.y, normal.z, 0f));
                if (CarriedStanding(null, c.weapon, placed * local, front) || DrawPiece(c, placed * local * OnChest(m, c.mesh == MeshPool.plane10Flip, front, side) * Over(layer), true))
                {
                    modelled = c.weapon;
                }
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
            Vector3 centre = new Vector3(drawLoc.x + (p.x - pawnPos.x), at.y + Middle * size, drawLoc.z + (p.z - pawnPos.z));
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
