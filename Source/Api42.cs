using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace IsoRim
{
    // Api 42: model rules. An addon binds models to things from code: while IsoRim builds its model table it asks the rules
    // for every thing with no thingDef entry, and a ModelDef named by a rule is bound as if it named that thing. Generic: no
    // mod names.
    public static partial class Api
    {
        /// <summary>
        /// (Api version 42) Adds a model rule: while IsoRim builds its model table at startup, it calls rule once for every
        /// ThingDef that has no <c>thingDef</c> ModelDef entry (blueprints and frames are not asked), after the thingDef entries
        /// and before the graphicPath, thingCategory, match and fallback entries. Return the defName of an IsoRim.ModelDef to
        /// bind that thing to it exactly as if the ModelDef's <c>thingDef</c> named it (its models, stack models, byGraphic,
        /// colours and all other fields apply), or null to pass. Rules are asked in the order they were added; the first known
        /// ModelDef answered wins. Register from your Mod constructor: the table is built once, and a rule added after that is
        /// ignored (with a warning). The ModelDef should be in your mod's Defs/ so its model files are found; it needs no
        /// thingDef of its own. A rule that throws is logged once and passes; an unknown or style ModelDef name is warned
        /// about once and the next rule is asked.
        /// </summary>
        public static void AddModelRule(Func<ThingDef, string> rule)
        {
            ModelRules.Add(rule);
        }
    }

    // The registered model rules (Api 42), asked by ModelLibrary.LoadAll.
    internal static class ModelRules
    {
        private sealed class Rule
        {
            public Func<ThingDef, string> ask;
            public string owner;   // the package id of the mod whose assembly holds the rule (for logs and HowBound)
            public bool failed;    // logged once already
        }

        private static readonly List<Rule> rules = new List<Rule>();
        private static readonly HashSet<string> warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool built;
        private static bool warnedLate;

        public static bool Any => rules.Count > 0;

        public static void Add(Func<ThingDef, string> rule)
        {
            if (rule == null)
            {
                Log.Warning("[IsoRim] Api.AddModelRule: null rule ignored.");
                return;
            }
            string owner = OwnerOf(rule);
            if (built)
            {
                if (!warnedLate)
                {
                    warnedLate = true;
                    Log.Warning("[IsoRim] Api.AddModelRule from " + owner + " after the model table was built: ignored. Add model rules in your Mod constructor.");
                }
                return;
            }
            for (int i = 0; i < rules.Count; i++)
            {
                if (rules[i].ask == rule)
                {
                    return;   // the same delegate twice
                }
            }
            rules.Add(new Rule { ask = rule, owner = owner });
        }

        // Marks the table built: later rules are ignored.
        public static void Seal()
        {
            built = true;
        }

        // The ModelDef the rules give thing, or null. Fails soft: a throwing rule passes (logged once per rule), an unknown or
        // style ModelDef name is warned about once per name and the next rule is asked.
        public static ModelDef Resolve(ThingDef thing)
        {
            for (int i = 0; i < rules.Count; i++)
            {
                Rule r = rules[i];
                string name;
                try
                {
                    name = r.ask(thing);
                }
                catch (Exception e)
                {
                    if (!r.failed)
                    {
                        r.failed = true;
                        Log.Error("[IsoRim] A model rule from " + r.owner + " threw for " + thing.defName + " (logged once for this rule; it passes when it throws): " + e);
                    }
                    continue;
                }
                if (name.NullOrEmpty())
                {
                    continue;
                }
                ModelDef md = DefDatabase<ModelDef>.GetNamedSilentFail(name);
                if (md == null)
                {
                    if (warned.Add(name))
                    {
                        Log.Warning("[IsoRim] A model rule from " + r.owner + " names ModelDef " + name + ", which does not exist (first for " + thing.defName + "; warned once per name).");
                    }
                    continue;
                }
                if (!md.style.NullOrEmpty())
                {
                    if (warned.Add(name))
                    {
                        Log.Warning("[IsoRim] A model rule from " + r.owner + " names ModelDef " + name + ", a style def, which a rule cannot bind (warned once per name).");
                    }
                    continue;
                }
                return md;
            }
            return null;
        }

        private static string OwnerOf(Func<ThingDef, string> rule)
        {
            try
            {
                Assembly asm = rule.Method.DeclaringType != null ? rule.Method.DeclaringType.Assembly : null;
                if (asm != null)
                {
                    foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
                    {
                        if (pack.assemblies != null && pack.assemblies.loadedAssemblies.Contains(asm))
                        {
                            return pack.PackageIdPlayerFacing;
                        }
                    }
                    return asm.GetName().Name;
                }
            }
            catch (Exception)
            {
            }
            return "an unknown mod";
        }
    }
}
