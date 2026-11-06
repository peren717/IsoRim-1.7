using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The game's picture for "no art": a thing whose def has no graphicData answers Thing.Graphic with BaseContent.BadGraphic, one
    // object shared by all of them. The game never draws it for what draws itself (a corpse draws its body), and a mod that changes
    // a graphic's drawSize and does not put it back changes it for all of them (Adaptive Storage Framework grows it to the size of
    // the map, whose plane then covers the ground in red). In the 3D view the picture is always one cell, so the error stays in
    // sight and cannot grow: the game's two ways of drawing a graphic stop here (Graphic.DrawWorker, Graphic.Print), and what
    // IsoRim draws from a thing's graphic itself takes its size from SizeOf. Each def is named in the log once. The shared
    // graphic is not changed. Corpses never come here: ItemDraw draws their body.
    internal static class BadArt
    {
        private static readonly HashSet<ThingDef> named = new HashSet<ThingDef>();

        // The drawn size of a graphic: one cell for the "no art" one.
        public static Vector2 SizeOf(Graphic g, Thing t)
        {
            if (g != BaseContent.BadGraphic)
            {
                return g.drawSize;
            }
            Name(t != null ? t.def : null);
            return Vector2.one;
        }

        // From Graphic.DrawWorker: true when it has drawn the picture in the game's place.
        public static bool Draw(Graphic g, Vector3 at, ThingDef def)
        {
            if (g != BaseContent.BadGraphic || !View3D.Active)
            {
                return false;
            }
            Name(def);
            DrawHook.DrawMesh(MeshPool.plane10, at, Quaternion.identity, BaseContent.BadMat, 0);
            return true;
        }

        // From Graphic.Print: the same, into the map mesh.
        public static bool Print(Graphic g, SectionLayer layer, Thing t)
        {
            if (g != BaseContent.BadGraphic || !View3D.Active)
            {
                return false;
            }
            Name(t.def);
            Printer_Plane.PrintPlane(layer, t.TrueCenter(), Vector2.one, BaseContent.BadMat);
            return true;
        }

        private static void Name(ThingDef def)
        {
            if (def == null)
            {
                return;
            }
            lock (named)
            {
                if (!named.Add(def))
                {
                    return;
                }
            }
            Log.Warning("[IsoRim] " + def.defName + " has no art, shown as a red square of one cell.");
        }
    }
}
