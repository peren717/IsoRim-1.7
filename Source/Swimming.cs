using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // People swimming (Odyssey: the game's GoSwimming job, Pawn.Swimming) as 3D figures, the one place of the rule.
    // The game, while a person swims: no headgear, no clothes and no body drawn (PawnRenderer.ParallelGetPreRenderResults' render
    // flags), the head drawn half a cell lower (PawnRenderNodeWorker_Head), no shadow (PawnRenderer.DrawShadowInternal); its ripples
    // (PawnWaterRippleMaker) go on as they are. Its 2D drawing (people as cards in the 3D view, portraits) is the game's own: unchanged.
    //   When: the game's own answer, Pawn.Swimming, on a person's figure drawn standing on the map (People.Draw: not lying, seated,
    //     crouching, attached, carried or in mid-jump, none of which swims). Placing reads (Api 62: figure drawers, ReadFigurePlacement)
    //     and figures drawn for another mod elsewhere (Api.DrawFigure, FigureBounds) get the figure as before: whole, standing on the
    //     surface, nothing hidden but the headgear the game hides.
    //   How deep: sunk until the water line (the ground's height, where IsoRim draws the water) is at its shoulders (WaterLine: the
    //     top ring of the body model the figure wears, under its neck), so the head and a little of the shoulders show. The figure is
    //     drawn still (no step's bob or sway), so the water line stays on that ring.
    //   What hides: everything under the water line (People.Merged: its merged meshes cut at that plane, the outline shell and the
    //     textured faces alike, made once per look and kept with it); headgear, as the game (GameFlags, through People.DrawParms: hair,
    //     beard and face then show as the game shows them); what is held or carried, the shield, the work tool; the figure's ground
    //     shadow. For other mods' parts and IsoRim's per-frame extras the body counts as not shown, as the game draws no body:
    //     FigureFrame.BodyShown false for figure hooks (Api 21), and apparel decals, body wound marks, a race's body cards and bound
    //     flames left out.
    //   Elsewhere: Poses.DrawnSpot gives the swimmer's head as drawn (picking, icons and bars over it, speech, first person).
    // Nothing is worked out for a person who is not swimming: one Pawn.Swimming read per figure drawn.
    [StaticConstructorOnStartup]
    internal static class Swimming
    {
        // Rings of the body model closer than this under its top count as the top itself (the neck), figure units.
        private const float RingGap = 0.01f;

        private static readonly Dictionary<int, float> shoulders = new Dictionary<int, float>();

        // The one gate of the rule: a person (a humanlike with a story, as People's figures) the game says swims now. Every entry
        // point asks this (People.Draw, LookOf / Work, SwimDepth, GameFlags); a non-person on a swimming job never reaches the look.
        internal static bool On(Pawn pawn)
        {
            return pawn.story != null && pawn.RaceProps != null && pawn.RaceProps.Humanlike && pawn.Swimming;
        }

        // The water line on the figure: its height in the figure's own space (feet at 0, before its size), the shoulder ring of the body
        // model it wears (the build's person_*_upper, a race's own) with the body type's height. IsoRim's own builds: 0.64 (fat) to 0.68
        // (female) of the neck's 0.74.
        internal static float WaterLine(People.Look look)
        {
            float line = ShoulderOf(look.upper);
            return look.upperBones ? line : line * look.bodyScale.y;
        }

        // How far the figure sinks, true cells: the water line at the figure's size.
        internal static float Depth(People.Look look, float size)
        {
            return WaterLine(look) * size;
        }

        // The plane the swimmer's merged meshes are cut at, in their own space: kept where dot(xyz, corner) + w >= 0. place maps that
        // space to the figure's (identity for the body; a race's head placing for its head, Api 25).
        internal static Vector4 Cut(People.Look look, Matrix4x4 place)
        {
            return new Vector4(place.m10, place.m11, place.m12, place.m13 - WaterLine(look));
        }

        // The height of a body model's top ring under its neck (its highest corners below the top), once per model.
        private static float ShoulderOf(Model upper)
        {
            if (upper == null || upper.positions == null)
            {
                return 0f;
            }
            float line;
            if (shoulders.TryGetValue(upper.id, out line))
            {
                return line;
            }
            float top = float.MinValue;
            for (int i = 0; i < upper.positions.Length; i++)
            {
                top = Mathf.Max(top, upper.positions[i].y);
            }
            line = float.MinValue;
            for (int i = 0; i < upper.positions.Length; i++)
            {
                float y = upper.positions[i].y;
                if (y < top - RingGap && y > line)
                {
                    line = y;
                }
            }
            if (line == float.MinValue)
            {
                line = top;
            }
            shoulders[upper.id] = line;
            return line;
        }

        // The game's render flags for a swimmer, on parms (People.DrawParms: what the game would draw now). Copied from
        // Verse.PawnRenderer.ParallelGetPreRenderResults (1.7): while Pawn.Swimming, the flags lose Headgear, Clothes and
        // NeverAimWeapon (mask 0xFFFFFF1F) and gain NoBody, and parms.swimming is set (PawnRenderer.GetDrawParms). Watched (CopyWatch).
        internal static void GameFlags(Pawn pawn, ref PawnDrawParms parms)
        {
            if (!On(pawn))
            {
                return;
            }
            parms.swimming = true;
            parms.flags = (PawnRenderFlags)((uint)parms.flags & 0xFFFFFF1Fu);
            parms.flags |= PawnRenderFlags.NoBody;
        }

        // CopyHash of PawnRenderer.ParallelGetPreRenderResults when GameFlags was copied (RimWorld 1.7.5005); checked once loading is
        // done (CopyWatch: one warning if the game changed it).
        private const string FlagsSourceHash = "a97f23ebbcc4565b";

        static Swimming()
        {
            CopyWatch.Add(AccessTools.Method(typeof(PawnRenderer), "ParallelGetPreRenderResults"), FlagsSourceHash, "IsoRim, Swimming.GameFlags");
        }

        // ---- harness (ProbeSwim): what the last frame drew of each person's figure

        internal static bool Reporting;
        internal struct Drawn
        {
            public int frame;
            public bool swim;
            public float waterLine;   // figure units (0 when not swimming)
            public float lowest;      // true cells: the lowest point of its merged meshes as drawn (feet: 0)
            public float highest;
            public bool hat;          // headgear drawn
            public bool held;         // what it holds or carries was drawn (FigureWeapons)
            public bool shadow;       // its ground shadow was drawn
            public bool bodyShown;    // the body counts as shown for other mods' parts
        }
        internal static readonly Dictionary<int, Drawn> drawn = new Dictionary<int, Drawn>();

        // (Reporting) the lowest and highest point (true cells) of parts drawn as DynamicModels.DrawParts places them.
        internal static void Span(List<DynamicModels.Part> parts, Vector3 position, float yaw, Matrix4x4 local, ref float lo, ref float hi)
        {
            Matrix4x4 matrix = Matrix4x4.TRS(position, Quaternion.AngleAxis(yaw, Vector3.up), Vector3.one) * local;
            for (int i = 0; parts != null && i < parts.Count; i++)
            {
                Mesh m = parts[i].mesh;
                if (m == null)
                {
                    continue;
                }
                Bounds b = m.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = new Vector3((c & 1) != 0 ? b.min.x : b.max.x, (c & 2) != 0 ? b.min.y : b.max.y, (c & 4) != 0 ? b.min.z : b.max.z);
                    float y = matrix.MultiplyPoint3x4(corner).y;
                    lo = Mathf.Min(lo, y);
                    hi = Mathf.Max(hi, y);
                }
            }
        }
    }
}
