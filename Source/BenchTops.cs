using RimWorld;
using Verse;

namespace IsoRim
{
    // Items lying on furniture that carries things on its top. The game puts a bill's ingredients on the work table's
    // own cells (Building_WorkTable.IngredientStackCells), and things may be dropped onto a table; drawn flat on the
    // ground they sat inside the 3D bench. The game's own flag for "things rest on this" is ThingDef.surfaceType (Item:
    // work tables, end tables, dressers; Eat: dining tables; None: everything else, campfires among them). An item on such
    // a building drawn as a model (or raised from its sprite) is drawn on its surface (the model's surfaceHeight), as the
    // items on a shelf; so is one on any work table (Building_WorkTable: its ingredients go there whatever its surfaceType,
    // the subcore encoder's is None). Only the drawing changes: the item's cell is the game's.
    //
    // A full top needs nothing of its own: the game lets one item lie in a cell (GenSpawn pushes an older one aside), and
    // ingredients that find the table's cells taken go to cells round its interaction spot (Toils_JobTransforms
    // .SetTargetToIngredientPlaceCell), which are drawn on the ground where the game has them. A model whose surfaceHeight
    // is 0 (a campfire, a crematorium) has no top: what lies in its cells stays on the ground.
    public static class BenchTops
    {
        // Harness switch (probe key benchtops=0|1): off draws items on such furniture on the ground as before.
        public static bool On = true;

        // The furniture under an item whose top the item is drawn on (not shelves: those are Building_Storage, Forms.BaseUnder).
        public static bool Carries(Building b)
        {
            return On && b != null && (b.def.surfaceType != SurfaceType.None || b is Building_WorkTable) && !(b is Building_Storage);
        }

        // The height of the furniture's top, in cells over its floor; 0 when it has none to rest things on.
        public static float TopOf(Building b)
        {
            ThingForm f = Forms.For(b.def);
            return f.form == Form3D.Model ? Forms.SurfaceOf(b, f) : f.form == Form3D.Extruded ? f.height : 0f;
        }
    }
}
