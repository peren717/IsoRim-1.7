using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Pawn name labels in the 3D view (2026-11-01):
    //   - above the head (option "Labels above heads", on by default) instead of at the feet, where the selection ring and the
    //     ground marks are: the label's bottom a little over the figure's top as posed now. One height source: Overhead's
    //     (the same "above the head" the overhead icons use: standing, sitting, lying or downed, in a bed; animals and mechs by
    //     their pick height, hovering and on a host included), lowered for a crouch and brought down for face down (Api 45).
    //     Placed with the camera's turn and the pawn's floor as Patches.LabelDrawPosFor_Prefix places the feet label.
    //     Patches.LabelDrawPosFor_Prefix asks AboveHead for a pawn's name label (the game's PawnUIOverlay offset).
    //   - drawn on a small dark rounded plate (DrawLabel, from Patches.DrawPawnLabel_Prefix), more opaque for a selected pawn,
    //     whose name is drawn at full strength; the text colour stays the game's name colour. With the option off the plate
    //     and the selected highlight stay; the label is at the feet as before.
    // Only pawn name labels; items, corpses and other labels are left as they are. 3D view off: the game's own.
    internal static class PawnLabels
    {
        private const float PawnLabelOffset = -0.6f;   // PawnUIOverlay.PawnLabelOffsetY: the game's own name label call
        private const float Gap = 0.2f;                // between the figure's top and the label's bottom, cells (clear of hair tufts)
        private const float ProneTop = 0.45f;          // a figure face down (Api 45): about a lying figure's top
        private const float PlateAlpha = 0.45f;        // the plate behind an unselected pawn's name
        private const float PlateAlphaSelected = 0.72f;

        // Whether a label call is the pawn's name label and goes over the head now.
        internal static bool Above(Thing t, float offset)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            return t is Pawn && st != null && st.labelsAboveHeads && View3D.Ready && Mathf.Abs(offset - PawnLabelOffset) < 0.01f;
        }

        // How high the figure's top stands over its floor now, in cells.
        internal static float Top(Pawn p)
        {
            float top = Overhead.Height(p) - Overhead.Gap;
            Poses.Crouch c;
            if (!p.Dead && !p.Downed && p.GetPosture() == PawnPosture.Standing && Poses.Crouched(p, out c))
            {
                float crouched = top - c.drop * c.size;   // the head comes down with the shoulders
                top = Mathf.Lerp(crouched, ProneTop, Mathf.Clamp01(c.prone));
            }
            Building_Bed bed = p.Dead ? null : p.CurrentBed();
            if (bed != null)
            {
                // a sleeper's head on the pillow: a lying figure's top over where the sleeper lies (Api 28: a bunk's level)
                top = Mathf.Max(top, Poses.SleeperHeight(p, bed) + Overhead.LyingTop);
            }
            return top;
        }

        // The label's top-left anchor (as GenMapUI.LabelDrawPosFor returns it: UI space, x centred) with the label above the
        // pawn's head.
        internal static Vector2 AboveHead(Pawn p)
        {
            Vector3 lift = Floors.DrawnOffset(p);   // on an upper floor (Floors)
            Attachments.Placed attached;
            if (Attachments.Any && p.RaceProps != null && p.RaceProps.Humanlike && Attachments.Place(p, false, null, out attached))
            {
                lift.y += attached.at.y / View3D.Squash;   // a rider (Api 13): over its seat on the host (animals' pick height has it)
            }
            Vector3 at = p.DrawPos;
            IsoRimSettings st = IsoRimMod.Settings;
            float raise = st != null ? st.labelHeight : 0f;   // the player's "Label height", cells
            Vector3 world = new Vector3(at.x, (Top(p) + Gap + raise) / View3D.Squash, at.z) + new Vector3(lift.x, lift.y, lift.z);
            Vector3 onScreen = Find.Camera.WorldToScreenPoint(world);
            float scale = Prefs.UIScale;
            Vector2 r = new Vector2(onScreen.x / scale, Verse.UI.screenHeight - onScreen.y / scale);
            r.y -= LabelHeight;   // the label (drawn down from its anchor) ends at the point over the head
            return r;
        }

        // GenMapUI.GetPawnLabelNameWidth: the game's own width of the name, as its backing uses.
        private static Func<Pawn, float, Dictionary<string, string>, GameFont, float> nameWidth;

        private static float NameWidth(Pawn pawn, float truncateToWidth, Dictionary<string, string> cache, GameFont font)
        {
            if (nameWidth == null)
            {
                nameWidth = AccessTools.MethodDelegate<Func<Pawn, float, Dictionary<string, string>, GameFont, float>>(
                    AccessTools.Method(typeof(GenMapUI), "GetPawnLabelNameWidth"));
            }
            return nameWidth(pawn, truncateToWidth, cache, font);
        }

        private static float LabelHeight
        {
            get { return Prefs.DisableTinyText ? 16f : 12f; }
        }

        // ---- the plate

        private static Texture2D disc;

        private static Texture2D Disc
        {
            get
            {
                if (disc == null)
                {
                    const int n = 32;
                    Color32[] px = new Color32[n * n];
                    for (int y = 0; y < n; y++)
                    {
                        for (int x = 0; x < n; x++)
                        {
                            float u = x + 0.5f - n * 0.5f, v = y + 0.5f - n * 0.5f;
                            float a = Mathf.Clamp01(n * 0.5f - Mathf.Sqrt(u * u + v * v) + 0.5f);
                            px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                        }
                    }
                    disc = new Texture2D(n, n, TextureFormat.RGBA32, false, false);
                    disc.name = "IsoRim_LabelPlate";
                    disc.wrapMode = TextureWrapMode.Clamp;
                    disc.filterMode = FilterMode.Bilinear;
                    disc.SetPixels32(px);
                    disc.Apply(false);
                }
                return disc;
            }
        }

        // A dark pill over r (round ends of r's height), in the current GUI space.
        private static void Plate(Rect r, float alpha)
        {
            float h = r.height;
            float cap = h * 0.5f;
            Color was = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, alpha);
            Texture2D d = Disc;
            GUI.DrawTextureWithTexCoords(new Rect(r.x - cap, r.y, cap, h), d, new Rect(0f, 0f, 0.5f, 1f));
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, h), BaseContent.WhiteTex);
            GUI.DrawTextureWithTexCoords(new Rect(r.xMax, r.y, cap, h), d, new Rect(0.5f, 0f, 0.5f, 1f));
            GUI.color = was;
        }

        // The game's pawn name label (GenMapUI.DrawPawnLabel at a point) on the plate; the game draws the name, the drafted line
        // and, for the hurt, its health bar as before, its grey backing left out. alpha: the label's fade (far labels).
        internal static void DrawLabel(Pawn pawn, Vector2 pos, float alpha, float truncateToWidth, Dictionary<string, string> cache, GameFont font, bool alignCenter)
        {
            bool selected = Find.Selector != null && Find.Selector.IsSelected(pawn);
            if (selected)
            {
                alpha = 1f;   // the name at full strength
            }
            float width = NameWidth(pawn, truncateToWidth, cache, font);
            float pad = Prefs.DisableTinyText ? 6f : 4f;
            Rect bg = new Rect(pos.x - width / 2f - pad, pos.y, width + pad * 2f, LabelHeight);
            Plate(bg, (selected ? PlateAlphaSelected : PlateAlpha) * alpha);
            GenMapUI.DrawPawnLabel(pawn, bg, alpha, truncateToWidth, cache, font, false, alignCenter);
        }
    }
}
