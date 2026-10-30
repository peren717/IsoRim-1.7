using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 40: drawing an item as IsoRim shows items. For a mod that draws items itself somewhere of its own (on a belt, in a
    // display, in a machine's tray) instead of leaving them on the map: the same look as an item lying on the ground, by the
    // player's item settings, wherever the mod puts it. Generic: no mod names.
    public static partial class Api
    {
        /// <summary>
        /// (Api version 40) Draws item this frame as IsoRim draws an item lying on the ground, by the player's item settings
        /// (followed live): its 3D model (stack models by its stack count, its stuff's colour and surface), its own sprite made
        /// solid ("items from the game's art", "item thickness"), or, when those settings make it flat, the game's flat sprite of
        /// it lying at that height. position: true space, x and z in map cells (the item's middle), y the height of its underside
        /// over its floor in cells (a belt's top); on a stacked floor by its row, as <see cref="DrawModel(ModelHandle, Vector3, float, Vector3, Color, string)"/>
        /// (nothing on a hidden floor). yaw: degrees clockwise seen from above, added to the item's own turn; scale: of its
        /// drawn size. The item need not be spawned (one held in a container). Call from draw-time code, main thread. Returns
        /// false and draws nothing outside the 3D view, for a null or non-item thing, or if nothing could be drawn: then draw it
        /// your own way. (Apparel shown "as worn" on the ground is drawn here as its sprite made solid.)
        /// </summary>
        public static bool DrawItem(Thing item, Vector3 position, float yaw = 0f, float scale = 1f)
        {
            if (!View3D.Ready || item == null || item.def == null || item.def.category != ThingCategory.Item)
            {
                return false;
            }
            try
            {
                return ItemDraw.Draw(item, position, yaw, scale, true);
            }
            catch (System.Exception e)
            {
                Log.ErrorOnce("[IsoRim] Api.DrawItem failed for " + item.def.defName + ": " + e, 93611901);
                return false;
            }
        }
    }
}
