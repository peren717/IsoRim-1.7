namespace IsoRim
{
    public static partial class Api
    {
        // ---- glass opacity from the bound model (Version 73)

        /// <summary>
        /// (Version 73) A glass colour source (AddGlassColor) may answer a colour with a negative alpha: IsoRim then draws the
        /// glass part in that tint at the opacity of the thing's bound ModelDef (its glassOpacity, else IsoRim's own glass
        /// opacity), so a source that only sets the tint needs no def lookup of its own. GlassOpacity is that opacity for a
        /// thing: the one IsoRim gives its glass part from its bound ModelDef; NaN when the thing has no model with a glass
        /// part (or no thing). A source's own non-negative alpha is drawn as before.
        /// </summary>
        public const float GlassOpacityFromModel = -1f;

        /// <summary>(Version 73) The opacity IsoRim gives t's glass part from its bound ModelDef (glassOpacity, else IsoRim's
        /// own); NaN when t has no model with a glass part.</summary>
        public static float GlassOpacity(Verse.Thing t) => Glass.BoundOpacity(t);
    }
}
