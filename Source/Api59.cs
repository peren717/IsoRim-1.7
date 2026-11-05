using UnityEngine;
using Verse;

namespace IsoRim
{
    public static partial class Api
    {
        /// <summary>
        /// (Version 59) The long axis of the weapon's own sprite, as a unit vector in the picture (x across, y up; either end), from its
        /// opaque pixels, whatever way the game turns it in the hand. For a mod that lays a weapon along something: with
        /// <see cref="FigureFrame.AttachWeaponToBody"/>. Worked out once per sprite. Vector2.right without a sprite. Main thread. Throws on null.
        /// </summary>
        public static Vector2 WeaponAxis(Thing weapon)
        {
            if (weapon == null)
            {
                throw new System.ArgumentNullException(nameof(weapon));
            }
            return WeaponModels.AxisOf(weapon);
        }
    }
}
