using System;
using UnityEngine;
using Verse;

namespace IsoRim
{
    public static partial class Api
    {
        // ---- first-person eye providers (Version 77)

        /// <summary>
        /// (Version 77) Says where a person's eye is for IsoRim's first person, for a mod that draws its own figures (a figure
        /// drawer, Api 62) with heads at other heights than IsoRim's. Asked whenever IsoRim works out a first-person eye: every
        /// frame (at least once) while the camera is in a colonist's head, and for each <see cref="ViewPose.FirstPersonOf"/>
        /// (photos, Api 76). Return the eye in a figure drawer's true space, as <see cref="FigurePlacement"/>.Position and
        /// <see cref="ReportMuzzle"/>: x and z in map cells in the person's own rows (as pawn.DrawPos), y the height over the
        /// floor they stand on, in cells; IsoRim moves it onto that floor (stacked floors, a step, a bridge deck) as it moves
        /// your figure. Return null to keep IsoRim's own eye for that person. The answer is used exactly as given: IsoRim's
        /// eye height option, its head drop (sitting, crouching, swimming) and its small nudge forward do not apply, and IsoRim
        /// does not smooth it (the look direction stays IsoRim's). Providers are asked in the order added; the first that
        /// answers wins. Main thread only (off it none is asked). A provider that throws, or answers a point that is not a
        /// number, is removed with one warning and IsoRim's eye is used. Inside a provider, <see cref="ViewPose.FirstPersonOf"/>
        /// gives IsoRim's own eye (no provider is asked then) in the same space as your answer (before the floor's offset): use
        /// it as the base eye to adjust and return. Keep it cheap. Throws on null or when added twice.
        /// </summary>
        public static void AddFirstPersonEye(Func<Pawn, Vector3?> provider) => FirstPerson.AddEyeProvider(provider);

        /// <summary>(Version 77) Removes a provider added with <see cref="AddFirstPersonEye"/>; nothing happens if it is not added.</summary>
        public static void RemoveFirstPersonEye(Func<Pawn, Vector3?> provider) => FirstPerson.RemoveEyeProvider(provider);
    }
}
