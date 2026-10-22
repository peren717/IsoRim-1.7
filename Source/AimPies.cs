using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // What the game draws after a pawn in PawnRenderer.RenderPawnAt (the stance: the aim pie and the cooldown circle of a
    // selected pawn; the debug path; the rope lines to roped animals), which a pawn drawn as a 3D model skips with the
    // rest of its sprite drawing (Patches.RenderPawnAt_Prefix returns false): ModelPawnExtras draws it again.
    // A 3D figure aiming a gun shows its aim pie at the gun's muzzle (Projectiles.Muzzle, where its shots start), stood up
    // facing the camera as a small card (DrawHook's billboard, Api 12's upright scope), its wedge toward the target as
    // seen; the game's pie keeps growing and shrinking with the warm-up. Everything else keeps the game's pie on the
    // ground: melee, abilities, a pawn without a gun (animals), a 2D card pawn, turrets.
    [StaticConstructorOnStartup]
    internal static class AimPies
    {
        // Harness only (probe muzzlepie=0): the game's pie on the ground for every pawn.
        public static bool AtMuzzle = true;

        // Pies drawn at a muzzle so far (harness checks), the last one's card angle and size.
        public static int Drawn;
        public static float LastAngle, LastDegrees;
        public static Vector3 LastMuzzle;

        private const float Size = 0.6f;    // of the game's pie (radius 0.55 cells): a third of a cell
        private const float Ahead = 0.1f;   // cells on the card from the muzzle to the pie's middle (its point), toward the target

        public static void ModelPawnExtras(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead)
            {
                return;
            }
            try
            {
                pawn.stances?.StanceTrackerDraw();
                pawn.pather?.PatherDraw();
                pawn.roping?.RopingDraw();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a model pawn's aim pie or ropes failed: " + e, 93621902);
            }
        }

        // GenDraw.DrawAimPie(Thing shooter, LocalTargetInfo target, int degreesWide, float offsetDist).
        public static bool DrawAimPie_Prefix(Thing shooter, LocalTargetInfo target, int degreesWide)
        {
            if (!AtMuzzle || degreesWide <= 0 || !View3D.Ready || !(shooter is Pawn pawn) || !target.IsValid)
            {
                return true;
            }
            try
            {
                Stance_Warmup warmup = pawn.stances != null ? pawn.stances.curStance as Stance_Warmup : null;
                Verb verb = warmup != null ? warmup.verb : null;
                ThingWithComps gun = verb != null ? verb.EquipmentSource : null;
                if (gun == null || verb.IsMeleeAttack || verb is Verb_CastAbility || !gun.def.IsRangedWeapon || !Patches.DrawnAsModel(pawn))
                {
                    return true;
                }
                Vector3 to = target.HasThing ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
                Vector3 aim = Floors.FlatDelta(pawn.DrawPos, to);
                aim = aim.x * aim.x + aim.z * aim.z > 0.001f ? aim.normalized : Vector3.zero;
                Vector3 tip = Projectiles.Muzzle(pawn, gun.def, pawn.DrawPos, aim);

                // The way to the target on screen, from the muzzle: across (right) and up the screen, a height difference
                // showing up the screen by the camera's tilt.
                Vector3 flat = Floors.FlatDelta(new Vector3(tip.x, 0f, tip.z), to);
                Vector3 a = new Vector3(pawn.DrawPos.x, 0f, pawn.DrawPos.z);
                Vector3 b = new Vector3(to.x, 0f, to.z);
                Floors.Placed(ref a);
                Floors.Placed(ref b);
                float rise = (b.y - a.y) * View3D.Squash + (target.HasThing ? Interaction.PickHeight(target.Thing) * 0.5f : 0f) - tip.y;
                float pitch = View3D.Pitch * Mathf.Deg2Rad;
                Vector3 right = Quaternion.Euler(0f, View3D.Yaw, 0f) * Vector3.right;
                float across = Vector3.Dot(flat, right);
                float up = Vector3.Dot(flat, View3D.GroundForward) * Mathf.Sin(pitch) + rise * Mathf.Cos(pitch);
                float angle = across * across + up * up > 1e-6f ? Mathf.Atan2(across, up) * Mathf.Rad2Deg : 0f;

                int degrees = Mathf.Min(degreesWide, 360);
                Vector3 pivot = new Vector3(tip.x, pawn.DrawPos.y, tip.z);
                float r = angle * Mathf.Deg2Rad;
                Vector3 centre = pivot + new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r)) * Ahead;
                Matrix4x4 m = Matrix4x4.TRS(centre, Quaternion.AngleAxis(angle + degrees / 2 - 90f, Vector3.up), new Vector3(Size, 1f, Size));
                using (UprightScopes.Begin(tip.y, true, pivot))
                {
                    DrawHook.DrawMesh(MeshPool.pies[degrees], m, PieMaterial, 0);
                }
                Drawn++;
                LastAngle = angle;
                LastDegrees = degrees;
                LastMuzzle = tip;
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing an aim pie at the muzzle failed: " + e, 93621903);
                return true;
            }
        }

        private static Material pieMaterial;

        // The game's pie colour (GenDraw.AimPieMaterial: white at 0.3), over the figures (a pie in front of the gun is
        // never hidden in the figure holding it).
        private static Material PieMaterial
        {
            get
            {
                if (pieMaterial == null)
                {
                    pieMaterial = new Material(SolidColorMaterials.SimpleSolidColorMaterial(new Color(1f, 1f, 1f, 0.3f)));
                    pieMaterial.renderQueue = 3900;
                }
                return pieMaterial;
            }
        }
    }
}
