using System;
using Verse;

namespace IsoRim
{
    public static partial class Api
    {
        // ---- spin angle providers (Version 79)

        /// <summary>
        /// (Version 79) Says how far a thing's spin part (its ModelDef's <c>spinModel</c>, upright or on an axle) is turned, for
        /// a mod whose own thing keeps that angle (a water wheel, a fan, a turbine of its own). Asked every frame the part is
        /// drawn (things in view with the 3D view and 3D models on), before the def's <c>spinSource</c>: return the angle in
        /// degrees about the part's axis (<c>spinAxis</c>, model space facing north; positive turns as
        /// <c>Quaternion.AngleAxis</c> does), or null when the thing is not yours (the next provider, then the def's
        /// <c>spinSource</c>, answers). The angle is used exactly as given each frame: IsoRim does not add speed, smooth or wrap it.
        /// Providers are asked in the order added; the first that answers wins. Main thread only. A provider that throws, or
        /// answers a value that is not a number, is removed with one warning. Keep it cheap. Throws on null or when added twice.
        /// </summary>
        public static void AddSpinAngle(Func<Thing, float?> provider) => Spinners.AddProvider(provider);

        /// <summary>(Version 79) Removes a provider added with <see cref="AddSpinAngle"/>; nothing happens if it is not added.</summary>
        public static void RemoveSpinAngle(Func<Thing, float?> provider) => Spinners.RemoveProvider(provider);
    }
}
