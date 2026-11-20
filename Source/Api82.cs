using RimWorld;
using Verse;

namespace IsoRim
{
    public static partial class Api
    {
        // ---- stored items on boards (Version 82)

        /// <summary>
        /// (Version 82) How high a spawned item rests as IsoRim draws it, in cells over the ground of its cell: the top of
        /// the storage building it is stored on, or the board of that building it stands on (ModelDef <c>itemShelves</c>);
        /// the top of the work table or table it lies on; 0 on the ground, for anything that is not a spawned item and while
        /// the 3D view is off. The item's own height is not in it. Where it stands on the map is the game's own answer
        /// (<c>GenThing.TrueCenter</c> / <c>Thing.DrawPos</c>), which IsoRim sets for items on boards. Main thread;
        /// cheap enough to ask every frame. <see cref="SurfaceHeight"/> stays the building's top.
        /// </summary>
        public static float ItemRestHeight(Thing item)
            => item != null && item.Spawned && item.def.category == ThingCategory.Item ? Forms.BaseUnder(item, true) : 0f;
    }
}
