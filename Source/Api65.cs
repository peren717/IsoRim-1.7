using UnityEngine;
using Verse;

namespace IsoRim
{
    public static partial class Api
    {
        /// <summary>
        /// (Version 65) The models and colour IsoRim draws this animal with now, as its own animal draw picks them: its ModelDef's
        /// byBodyGraphic entry for the body graphic the game shows it with, else model / femaleModel (Version 38), and the tint
        /// (the entry's, else the vanilla graphic colour with tintFromGraphic, else white). tintTextured: the tint multiplies the
        /// textured materials too, not only the stuff-tinted ones (as tintFromGraphic draws). leg and wing are null when the animal has
        /// none (wing: the right one). For a figure drawer (Version 62) standing in for animals. A rotting body is not darkened
        /// here. False (all null, white) for a pawn without an animal model. Main thread.
        /// </summary>
        public static bool ReadAnimalModel(Pawn pawn, out ModelHandle body, out ModelHandle leg, out ModelHandle wing, out Color tint, out bool tintTextured)
        {
            body = null;
            leg = null;
            wing = null;
            tint = Color.white;
            tintTextured = false;
            ModelEntry entry;
            if (pawn == null || pawn.def == null || !ModelLibrary.TryGet(pawn.def, out entry) || !entry.def.animal || !entry.HasModel)
            {
                return false;
            }
            body = HandleOf(entry.Pick(pawn));
            if (body == null)
            {
                return false;
            }
            leg = HandleOf(entry.LegFor(pawn));
            wing = HandleOf(entry.WingFor(pawn, false));
            tint = BodyGraphicModels.Tint(entry, pawn, out tintTextured);
            return true;
        }
    }
}
