using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>
    /// (Api version 48) How a pawn's selection ring looks in IsoRim's 3D view: the circle drawn on the ground round a selected
    /// pawn. Every field is optional; what is left unset keeps the look from the next source (a code rule, then this mod
    /// extension on the pawn's race ThingDef, then IsoRim's default: the game's name colour, green for the colony, fitted to
    /// the body).
    /// From XML (load-gate it: e.g. a LoadFolders folder with IfModActive="Peren.IsoRim17"), on a race or animal ThingDef:
    ///   &lt;modExtensions&gt;&lt;li Class="IsoRim.SelectionRing"&gt;&lt;color&gt;(0.3, 0.6, 1)&lt;/color&gt;&lt;radiusScale&gt;1.2&lt;/radiusScale&gt;&lt;/li&gt;&lt;/modExtensions&gt;
    /// or from code as what a rule returns (<see cref="Api.AddSelectionRingRule"/>).
    /// </summary>
    public class SelectionRing : DefModExtension
    {
        /// <summary>The ring's colour; null keeps the colour from the next source.</summary>
        public Color? color;

        /// <summary>Times the fitted radius (1: as fitted; 0 or less: as 1).</summary>
        public float radiusScale = 1f;

        /// <summary>No ring for the pawn (true wins over every source).</summary>
        public bool hide;

        /// <summary>XML: a texture path (as a graphic's texPath, without extension) of the ring's own picture, any shape, drawn
        /// flat on the ground, tinted by the colour (white texels take it as it is), filling a square two radii across.</summary>
        public string texPath;

        /// <summary>Code: the ring's own picture (takes the place of texPath).</summary>
        public Texture2D texture;

        private Texture2D loaded;
        private bool tried;

        internal Texture2D Texture
        {
            get
            {
                if (texture != null)
                {
                    return texture;
                }
                if (!tried && !texPath.NullOrEmpty())
                {
                    tried = true;
                    loaded = ContentFinder<Texture2D>.Get(texPath, false);
                    if (loaded == null)
                    {
                        Log.Warning("[IsoRim] SelectionRing: no texture at " + texPath + "; the built-in ring is drawn.");
                    }
                }
                return loaded;
            }
        }
    }

    public static partial class Api
    {
        /// <summary>
        /// (Api version 48) Adds a selection ring rule: IsoRim asks it for each selected pawn it draws a ring for, every frame
        /// (main thread). Return null to pass, or a <see cref="SelectionRing"/> with the fields you set (colour, radius scale,
        /// picture, hide). Rules are asked in the order they were added; the first that does not return null wins. Its unset
        /// fields keep the XML extension's, then IsoRim's default. Keep the rule cheap (reuse the returned objects). A rule that
        /// throws is logged once and passes from then on. Only a standard <c>System.Func</c>, so code calling it after an
        /// <c>Api.Version &gt;= 48</c> check never breaks on an older IsoRim.
        /// </summary>
        public static void AddSelectionRingRule(Func<Pawn, SelectionRing> rule)
        {
            SelectionRingRules.Add(rule);
        }

        /// <summary>
        /// (Api version 48) The pawn's selection ring as IsoRim draws it now: colour and radius (cells, to the middle of the
        /// band; for a ring picture, half the square it fills). True when a ring is drawn for it (3D view on, the option on,
        /// spawned on the shown map, selected and not hidden by a rule or the XML extension). For a pawn not selected, colour
        /// and radius are the ones it would get (the radius from its position and body size, not its pose).
        /// </summary>
        public static bool SelectionRingOf(Pawn pawn, out Color color, out float radius)
        {
            color = Color.clear;
            radius = 0f;
            if (pawn == null)
            {
                return false;
            }
            try
            {
                SelectionRings.Ring r = SelectionRings.LookOf(pawn);
                color = r.colour;
                radius = r.radius;
                IsoRimSettings st = IsoRimMod.Settings;
                Thing at = pawn.SpawnedParentOrMe;
                return r.draw && View3D.Ready && st != null && st.selectionRings && at != null && at.Map == Find.CurrentMap
                    && Find.Selector != null && Find.Selector.IsSelected(pawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Api.SelectionRingOf failed: " + e, 93619482);
                return false;
            }
        }
    }

    // The registered selection ring rules (Api 48) and the XML extensions by race.
    internal static class SelectionRingRules
    {
        private sealed class Rule
        {
            public Func<Pawn, SelectionRing> ask;
            public string owner;
            public bool failed;   // threw once: logged, passes from then on
        }

        private static readonly List<Rule> rules = new List<Rule>();
        private static readonly Dictionary<ThingDef, SelectionRing> byRace = new Dictionary<ThingDef, SelectionRing>();

        public static void Add(Func<Pawn, SelectionRing> rule)
        {
            if (rule == null)
            {
                Log.Warning("[IsoRim] Api.AddSelectionRingRule: null rule ignored.");
                return;
            }
            foreach (Rule r in rules)
            {
                if (r.ask == rule)
                {
                    return;
                }
            }
            rules.Add(new Rule { ask = rule, owner = ModelRules.OwnerOf(rule) });
        }

        // The first rule's answer that is not null, or null.
        public static SelectionRing Ask(Pawn pawn)
        {
            for (int i = 0; i < rules.Count; i++)
            {
                Rule r = rules[i];
                if (r.failed)
                {
                    continue;
                }
                try
                {
                    SelectionRing look = r.ask(pawn);
                    if (look != null)
                    {
                        return look;
                    }
                }
                catch (Exception e)
                {
                    r.failed = true;
                    Log.Error("[IsoRim] A selection ring rule from " + r.owner + " threw (logged once; it passes from now on): " + e);
                }
            }
            return null;
        }

        // The XML extension on the pawn's race, or null.
        public static SelectionRing XmlOf(Pawn pawn)
        {
            ThingDef def = pawn.def;
            if (def == null)
            {
                return null;
            }
            SelectionRing ext;
            if (!byRace.TryGetValue(def, out ext))
            {
                ext = def.GetModExtension<SelectionRing>();
                byRace[def] = ext;
            }
            return ext;
        }

        // Harness: forget what was read from the defs (an extension added at run time).
        internal static void Forget()
        {
            byRace.Clear();
        }
    }
}
