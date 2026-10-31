using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 44: a weapon's 3D muzzle. For a mod that puts its own effects at a gun's muzzle (flashes, smoke, sparks, lines along
    // the shot): where the muzzle is as IsoRim draws the gun now, and a way to have a fleck or mote put there drawn standing
    // in the air at it instead of flat on the ground under it. The same point IsoRim's own shot flashes and bullets start from.
    // Generic: no mod names.
    public static partial class Api
    {
        /// <summary>
        /// (Api version 44) The muzzle of a weapon as IsoRim draws it now, in the 3D view: a pawn's held gun (the end of the
        /// barrel of its 3D model, of its sprite made solid on a 3D figure, or of its sprite on an upright 2D card), a mech's
        /// gun part, or a turret's gun (the front of its 3D gun model, posed as drawn). The same point IsoRim's own shot flash
        /// and the drawn bullet start from. weaponOrTurret: the weapon held by holder (null: whatever it holds), or a turret
        /// (holder then ignored; a turret manned by a pawn counts as the turret). muzzle: true space, x and z in map cells in the
        /// game's rows (where the game would put a fleck or mote there; on a stacked floor the holder's own rows), y the height
        /// over the holder's floor in cells. aim: the way the shot leaves, degrees clockwise from north on the map (the game's
        /// angle, as Vector3.AngleFlat). A gun not drawn just now (off screen, its floor hidden) is posed for it; with no drawn
        /// barrel IsoRim's own estimate (in front of the holder at hand height) is used, as for its own flashes. Call from the
        /// main thread (ticks or drawing). Returns false, and leaves muzzle at zero, outside the 3D view or for a thing not
        /// spawned: then use your own position. To draw there: create a fleck at (muzzle.x, 0, muzzle.z) after
        /// <see cref="StandFleck"/>, a mote at it then <see cref="StandMote"/>, or a mesh of your own at
        /// <see cref="DrawnPosition"/>(muzzle).
        /// </summary>
        public static bool TryGetMuzzle(Thing weaponOrTurret, Pawn holder, out Vector3 muzzle, out float aim)
        {
            muzzle = Vector3.zero;
            aim = 0f;
            Thing caster = weaponOrTurret is Building_Turret ? weaponOrTurret : holder;
            if (!View3D.Ready || !Projectiles.FromMuzzle || caster == null || !caster.Spawned || caster.Map == null)
            {
                return false;
            }
            try
            {
                Vector3 dir = Projectiles.HolderAim(caster);
                ThingDef gun = weaponOrTurret != null ? weaponOrTurret.def : null;
                muzzle = Projectiles.ShotMuzzle(caster, gun, dir, caster.Map);
                aim = dir.AngleFlat();
                return true;
            }
            catch (System.Exception e)
            {
                Log.ErrorOnce("[IsoRim] Api.TryGetMuzzle failed: " + e, 93611941);
                muzzle = Vector3.zero;
                return false;
            }
        }

        /// <summary>(Api version 44) <see cref="TryGetMuzzle(Thing, Pawn, out Vector3, out float)"/> without the aim.</summary>
        public static bool TryGetMuzzle(Thing weaponOrTurret, Pawn holder, out Vector3 muzzle)
        {
            float aim;
            return TryGetMuzzle(weaponOrTurret, holder, out muzzle, out aim);
        }

        /// <summary>
        /// (Api version 44) The flecks of fleck's def created at point.x, point.z (exactly: <c>FleckMaker.Static(new
        /// Vector3(point.x, 0, point.z), ...)</c> or <c>FleckMaker.GetDataStatic</c> there) while that fleck lasts are drawn
        /// standing up facing the camera at point.y cells over the ground (true space, as <see cref="TryGetMuzzle(Thing, Pawn, out Vector3, out float)"/>
        /// gives it), as IsoRim draws its own shot flashes, instead of flat on the ground. Call just before creating the fleck,
        /// from tick-time code (not while drawing). Nothing happens outside the 3D view.
        /// </summary>
        public static void StandFleck(Vector3 point, FleckDef fleck)
        {
            if (!View3D.Ready || fleck == null)
            {
                return;
            }
            Emitters.Raise(new Vector3(point.x, 0f, point.z), point.y, 0f, fleck);
        }

        /// <summary>
        /// (Api version 44) Draws mote, for as long as it lives, at height cells over the ground under where the game draws it
        /// (true space): standing up facing the camera (a flash, a glow), or with flat lying flat at that height (a line along
        /// a shot, a ring). Put the mote at the muzzle's x and z (an attached overlay's offset from its pawn, a static mote's
        /// position) and pass muzzle.y. Nothing changes outside the 3D view.
        /// </summary>
        public static void StandMote(Mote mote, float height, bool flat)
        {
            Emitters.StandMote(mote, height, flat);
        }
    }
}
