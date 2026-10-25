using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Icons the game draws over a pawn, flat at the pawn's feet in its top-down picture, stood up above the 3D figure's
    // head instead (overhead, 2026-10-23). One place answers where "above the head" is (Height: standing, sitting, lying
    // or downed, in a bed; Floor: an upper floor) for every such icon:
    //   - the game's status overlays on pawns (OverlayDrawer: the quest / trade question mark over creepjoiners, visitors,
    //     traders and lodgers; a colony mech's self-shutdown sign): Patches.OverlayRender_Prefix
    //   - icons thrown from a pawn's cell (FleckMaker.ThrowMetaIcon and the like: the downed pawn's "!", sleep Zs,
    //     hearts, healing crosses, meditation; any MetaOverlays fleck where a pawn or a pawn's casket is):
    //     OnFleck, drawn stood up by Emitters (Stand)
    // Speech, thought and mood bubbles and the signs thrown off a pawn (motes attached to it) keep their own layout
    // beside the head (Patches.BubbleOwner); name labels follow the figure in Patches.LabelDrawPosFor_Prefix.
    public static class Overhead
    {
        public const float Gap = 0.2f;             // between the head's top and an icon's middle, in cells
        private const float LyingTop = 0.38f;      // a lying (downed, sleeping on the ground) figure's top
        private const float BedTop = 0.55f;        // a figure lying in a bed

        // Where the middle of an icon over this pawn stands, in cells above its floor.
        public static float Height(Pawn p)
        {
            if (p == null)
            {
                return 1.45f;
            }
            PawnPosture posture = p.GetPosture();
            if (p.Dead || p.Downed || posture != PawnPosture.Standing)
            {
                Building_Bed bed = p.CurrentBed();
                return (bed != null ? BedTop + Poses.SlotRise(p, bed) : LyingTop) + Gap;   // up to their slot (Api 28: a bunk's level)
            }
            return Interaction.PickHeight(p) + Gap;
        }

        // The upper floor's matrix the pawn's figure is drawn with (identity on the ground or with no floors).
        public static Matrix4x4 Floor(Thing t)
        {
            return t != null && t.Spawned ? Floors.ExtraMatrix(t) : Matrix4x4.identity;
        }

        // Called as the game creates a fleck (FleckManager.CreateFleck, while it ticks): an overhead icon thrown from a
        // cell with a pawn in it (or a casket or bed holding one) is drawn standing up over that pawn's head.
        public static void OnFleck(Map map, FleckCreationData data)
        {
            // (MetaOverlays: the icons; MoteOverhead flecks are impacts, blood and puffs at the pawn's body, left as they are)
            if (Off || !View3D.Ready || map == null || data.def == null || data.def.altitudeLayer != AltitudeLayer.MetaOverlays)
            {
                return;
            }
            IntVec3 c = data.spawnPosition.ToIntVec3();
            if (!c.InBounds(map))
            {
                return;
            }
            Thing owner;
            Pawn p = PawnAt(map, c, out owner);
            if (owner == null)
            {
                return;
            }
            float h = p != null && owner == p ? Height(p) : Interaction.PickHeight(owner) + Gap;
            Emitters.Raise(data.spawnPosition, h, 0f, data.def, Floor(owner));
            Thrown++;
            int n;
            thrownBy.TryGetValue(data.def, out n);
            thrownBy[data.def] = n + 1;
        }

        private static readonly Dictionary<FleckDef, int> thrownBy = new Dictionary<FleckDef, int>();

        internal static int ThrownOf(FleckDef def)
        {
            int n;
            return def != null && thrownBy.TryGetValue(def, out n) ? n : 0;
        }

        internal static string ThrownList()
        {
            return string.Join(", ", thrownBy.Select(kv => kv.Key.defName + " " + kv.Value));
        }

        // The pawn in the cell (spawned there), or a building there holding one (a cryptosleep casket); owner is what
        // the icon floats over.
        private static Pawn PawnAt(Map map, IntVec3 c, out Thing owner)
        {
            owner = null;
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Pawn pawn)
                {
                    owner = pawn;
                    return pawn;
                }
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Building b && b is IThingHolder holder && holder.GetDirectlyHeldThings() is ThingOwner held && held.Count > 0 && held[0] is Pawn inside)
                {
                    owner = b;
                    return inside;
                }
            }
            return null;
        }

        // Harness counter (overheadcheck): icons thrown from a pawn's cell stood up this session.
        internal static int Thrown;

        // Harness switch (overheadfix=0): the icons as before (flat at the feet; the status icons on pawns not stood up).
        public static bool Off;
    }
}
