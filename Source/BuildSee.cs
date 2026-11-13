using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // See-through while building (setting buildSeeThrough): while a designator is in hand (build, floors, zones, orders)
    // and the pointer is on the map, walls, rock, doors, buildings, tall plants, trees and roofs near the pointer go
    // see-through, so the cells being placed on show. Its own feature: the cut-away (Cutaway) does whatever the player's
    // own settings say as well, so with both on walls near the pointer are lowered and see-through.
    //
    // Screen-door transparency in the fragment stage (IsoRimSolid.frag.glsl, SeeThrough): a fragment of something that
    // may go see-through is dropped where a 4 x 4 ordered dither pattern (fixed on the screen) is below the coverage at
    // its spot, which is Coverage within Inner cells of the pointer (on the ground plane) and falls off smoothly to
    // nothing at Outer. Opaque drawing as before: no sorting, and every layer of a wall (front face, back face, its light
    // pass) drops the same pixels, so the ground shows through all of them at once.
    //
    // What may go see-through is tagged per vertex (texture coordinate 1, ShaderCut.See): everything the cut-away tags
    // (CutTags: walls, rock, gravship hull, damage marks, roofs), models baked into the map that are buildings or tall
    // plants (RoofClip.Tag via ModelBaker.Bake: furniture, machines, trees), and door leaves (DynamicModels.SeeParts).
    // Not tagged: the ground and floors (the game's own shaders), pawns, animals and held or worn models (moving models),
    // items and short plants, blueprints and frames (ghost shader, no tags read), overlays. IsoRim.vert.glsl and
    // IsoRimTex.vert.glsl hand the tag on in vNormal.x (+10).
    //
    // Stacked floors: only what is at or above the viewed floor's height goes see-through (the pointer stays on the
    // viewed floor while a designator is in hand, Floors.PointerFloorAt); the floors below are left alone.
    public static class BuildSee
    {
        // From the settings seeStrength and seeRadius (full coverage to Edge cells inside it); the harness key bsee
        // overrides them (negative = the setting). Before the settings: coverage 0.55, inner 1.5, outer 4.5.
        public const float Edge = 3f;           // cells over which it fades out to the radius
        public static float CoverageOverride = -1f;
        public static float InnerOverride = -1f;
        public static float OuterOverride = -1f;
        public static float DitherPx = 1f;      // screen pixels per dither cell (harness: probe bsee)

        // share of pixels dropped at the pointer
        public static float Coverage
        {
            get { return CoverageOverride >= 0f ? CoverageOverride : Mathf.Clamp(IsoRimMod.Settings.seeStrength, 0.2f, 1f); }
        }

        // cells: solid again from here
        public static float Outer
        {
            get { return OuterOverride >= 0f ? OuterOverride : Mathf.Clamp(IsoRimMod.Settings.seeRadius, 3f, 12f); }
        }

        // cells: full coverage out to here
        public static float Inner
        {
            get { return InnerOverride >= 0f ? InnerOverride : Mathf.Max(0f, Outer - Edge); }
        }

        private static readonly int seeId = Shader.PropertyToID("_IsoSee");
        private static readonly int see2Id = Shader.PropertyToID("_IsoSee2");

        private static bool active;

        // This frame: the setting is on, a designator is in hand and the pointer is on the map.
        public static bool Active
        {
            get { return active; }
        }

        // Baked models that may go see-through: buildings, and plants drawn taller than half a cell (trees, bushes, crops).
        public static bool Takes(Thing thing, ModelEntry entry)
        {
            if (thing is Building)
            {
                return true;
            }
            if (thing is Plant)
            {
                return entry != null && entry.DrawnHeight(thing.def) >= 0.5f;
            }
            return false;
        }

        // Every frame while the 3D view is on (Cutaway.TrackPointer, before the cut-away's own work).
        public static void Update(Map map)
        {
            active = false;
            Vector3 ground;
            if (IsoRimMod.Settings.buildSeeThrough && map != null && View3D.Active && Find.DesignatorManager != null
                && Find.DesignatorManager.SelectedDesignator != null && Cutaway.Pointer(out ground))
            {
                // To drawn space: a point on an upper floor is drawn over the ground rows, raised by the floor's height.
                Vector3 off = Floors.On ? Floors.RowOffset(Mathf.FloorToInt(ground.z)) : Vector3.zero;
                active = true;
                Shader.SetGlobalVector(seeId, new Vector4(ground.x, ground.z + off.z, off.y, Coverage));
                Shader.SetGlobalVector(see2Id, new Vector4(Inner, Outer, DitherPx, 0f));
                return;
            }
            Shader.SetGlobalVector(seeId, Off);
            Shader.SetGlobalVector(see2Id, new Vector4(Inner, Outer, DitherPx, 0f));
        }

        // _IsoSee with nothing see-through (w = 0: off); a photo without overlays draws with it (Api 76, Photos).
        internal static readonly Vector4 Off = new Vector4(-9999f, -9999f, 0f, 0f);

        internal static int SeeId
        {
            get { return seeId; }
        }
    }
}
