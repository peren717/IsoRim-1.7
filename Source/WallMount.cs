using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Things fixed to a wall: the game marks them building.isAttachment (Placeworker_AttachedToWall: wall lamp,
    // wall torch, Odyssey's oxygen pump, life support unit and ancient emergency lights); a model can also ask
    // for it (ModelDef.wallMounted). One rule places all of them, on the face of the wall they face, at a height
    // that follows that wall: the top of the fixture just over a standing colonist's head, or just under the top
    // of a lower wall. Always the wall as built: the cut-away never moves a fixture (it lowers walls in the vertex
    // stage every frame and rebuilds nothing, so a fixture baked from a cut wall stayed low). Models (baked and
    // placement preview), the game's own sprite for those without a model (stood on the wall face) and the height
    // used for clicks and status icons all come from here.
    public static class WallMount
    {
        private const float HeadClear = 1.55f;   // top of a fixture on a tall wall: over a colonist's head (Interaction.PickHeight 1.25)
        private const float TopGap = 0.06f;      // under the top of a lower wall
        private const float FaceGap = 0.015f;    // a sprite card stands this far off the wall face

        public static bool Is(ThingDef def)
        {
            ThingDef built = def != null ? GenConstruct.BuiltDefOf(def) as ThingDef : null;
            if (built == null)
            {
                return false;
            }
            if (built.building != null && built.building.isAttachment)
            {
                return true;
            }
            ModelEntry entry;
            return ModelLibrary.TryGet(built, out entry) && entry.def.wallMounted;
        }

        // The wall a fixture in cell c facing rot hangs on, and where its model stands: in c facing the wall
        // (yaw unchanged), or, for one sitting in the wall's own cell, one cell out and turned round.
        // Returns the wall's height as built (never cut down); the full wall height if there is none.
        public static float Wall(Map map, IntVec3 c, Rot4 rot, ref Vector3 center, ref float yaw)
        {
            IntVec3 ahead = rot.FacingCell;
            center = new Vector3(c.x + 0.5f, center.y, c.z + 0.5f);
            float wall = map != null ? WallBoxes.BuiltHeightAt(map, c + ahead) : 0f;
            if (wall > 0f)
            {
                return wall;
            }
            wall = map != null ? WallBoxes.BuiltHeightAt(map, c) : 0f;
            if (wall > 0f)
            {
                // The fitting sits in the wall's own cell and faces out of it: the same thing as standing one
                // cell out and facing back.
                center += ahead.ToVector3();
                yaw += 180f;
                return wall;
            }
            return View3D.WallHeight;
        }

        // The height to raise a fixture by, given the wall and the fixture's own lowest and highest points
        // (cells, as built): its top at the rule's height, never sunk into the floor.
        public static float Lift(float wall, float bottom, float top)
        {
            float want = Mathf.Min(wall - TopGap, HeadClear);
            return Mathf.Max(want - top, -bottom);
        }

        // A model fixture whose top (as built and scaled) is top: its def's own fraction of the wall if it sets one.
        public static float ModelLift(ModelDef def, float wall, float top)
        {
            return def.mountHeight >= 0f ? wall * Mathf.Clamp01(def.mountHeight) : Lift(wall, 0f, top);
        }

        // The top of a mounted thing, for clicking it and for the icons over it.
        public static float TopOf(Thing t)
        {
            Vector3 center = Vector3.zero;
            float yaw = 0f;
            float wall = Wall(t.Map, t.Position, t.Rotation, ref center, ref yaw);
            ThingForm f = Forms.For(t.def);
            if (f.form == Form3D.Model && f.model != null && f.model.HasModel)
            {
                float h = f.model.variants[0].maxY * f.model.def.scale * f.model.Fit(t.def).y;
                return ModelLift(f.model.def, wall, h) + h;
            }
            return Mathf.Min(wall - TopGap, HeadClear);
        }

        // A fixture without a model: the game's front view of it (its north sprite) stood on the wall face as a
        // flat card, the middle of the card at a quarter of its height under the rule's top (the drawing fills
        // about the middle half of its square). False if there is nothing to print.
        public static bool PrintCard(SectionLayer layer, Thing t)
        {
            Graphic g = t.Graphic;
            Graphic_Multi multi = g as Graphic_Multi;
            Material mat = multi != null ? multi.MatNorth : g != null ? g.MatAt(Rot4.North, t) : null;
            if (mat == null || t.Map == null)
            {
                return false;
            }
            Vector2 size = BadArt.SizeOf(g, t);
            Vector3 center = Vector3.zero;
            float yaw = t.Rotation.AsAngle;
            float wall = Wall(t.Map, t.Position, t.Rotation, ref center, ref yaw);
            Vector3 toWall = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.forward;
            Vector3 right = new Vector3(toWall.z, 0f, -toWall.x);
            float s = View3D.Squash;
            float mid = Mathf.Min(wall - TopGap, HeadClear) - size.y * 0.25f;
            float bottom = (mid - size.y * 0.5f) / s;
            float top = (mid + size.y * 0.5f) / s;
            Vector3 face = center + toWall * (0.5f - FaceGap);
            face.y = AltitudeLayer.BuildingOnTop.AltitudeFor();
            Vector3 half = right * (size.x * 0.5f);
            LayerSubMesh sm = MeshWriter.Sub(layer, mat);
            if (sm == null)
            {
                return false;
            }
            MeshWriter.TexturedQuad(sm,
                face - half + new Vector3(0f, bottom, 0f), face - half + new Vector3(0f, top, 0f),
                face + half + new Vector3(0f, top, 0f), face + half + new Vector3(0f, bottom, 0f),
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f),
                new Color32(255, 255, 255, 255));
            return true;
        }
    }
}
