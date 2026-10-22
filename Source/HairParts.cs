using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Which 3D hair and beard part (Models/person_hair_<kind>.obj, person_beard_<kind>.obj) each of the game's
    // styles gets. Every vanilla and DLC style is listed by name, each with the part nearest its own sprite.
    // Some parts are not made yet: until their file is in Models, the part listed in StandIn is drawn instead
    // (a chain, ending in nothing), so a new part is picked up as soon as its file is there.
    // Styles from other mods go by their style tags, and by their name only when no tag helps.
    internal static class HairParts
    {
        // Until a part's file exists, this one is drawn in its place (null: nothing).
        private static readonly Dictionary<string, string> hairStandIn = new Dictionary<string, string>
        {
            { "buzz", null }, { "topknot", "mohawk" }, { "crest", "mohawk" }, { "shoulder", "long" }, { "bob", "medium" },
            { "twintails", "ponytail" }, { "sidebuns", "bun" }, { "jane", "sidebuns" }, { "balding", "short" }, { "topbun_tail", "bun" },
        };
        private static readonly Dictionary<string, string> beardStandIn = new Dictionary<string, string>
        {
            { "long", "full" }, { "moustache", null },
        };

        // Every vanilla and DLC HairDef (null: no hair).
        private static readonly Dictionary<string, string> hairOf = new Dictionary<string, string>
        {
            { "Bald", null },
            { "Shaved", "buzz" }, { "RoughShave", "buzz" }, { "PatchyShave", "buzz" },
            { "ScorpionTail", "topknot" }, { "ShaveTopBraid", "topknot" },
            { "Rookie", "crest" }, { "Tuft", "crest" }, { "DoubleTuft", "crest" }, { "Primal", "crest" },
            { "Snazzy", "shoulder" }, { "Sticky", "shoulder" }, { "Scrapper", "shoulder" }, { "Locks", "shoulder" },
            { "Rockstar", "shoulder" }, { "Savage", "shoulder" }, { "Junkie", "shoulder" },
            { "Curly", "bob" }, { "Flowy", "bob" }, { "Frozen", "bob" }, { "Keeper", "bob" }, { "Cleopatra", "bob" },
            { "Artist", "bob" }, { "Mary", "bob" },
            { "Ponytails", "twintails" }, { "Pigtails", "twintails" },
            { "Henry", "sidebuns" }, { "Richard", "sidebuns" }, { "Lisa", "sidebuns" }, { "Jane", "jane" }, { "Cute", "sidebuns" },
            { "Elder", "balding" }, { "Scatman", "balding" }, { "Stephen", "balding" },
            { "Victoria", "topbun_tail" }, { "Senorita", "topbun_tail" },
            { "Mohawk", "mohawk" }, { "ShavedMohawk", "mohawk" },
            { "Afro", "afro" },
            { "FancyBun", "bun" }, { "Anne", "bun" }, { "Elisabeth", "bun" },
            { "BriefPonytail", "ponytail" }, { "BraidedKnot", "ponytail" },
            { "Spikes", "spiky" }, { "SideSpikes", "spiky" }, { "Firestarter", "spiky" }, { "LargeTuft", "spiky" },
            { "Long", "long" }, { "LongOily", "long" }, { "LongMess", "long" }, { "Wavy", "long" }, { "Princess", "long" },
            { "Mop", "medium" }, { "Bowlcut", "medium" }, { "GreasySwoop", "medium" }, { "Lackland", "medium" }, { "Decent", "medium" },
            { "Bob", "medium" }, { "Messy", "medium" }, { "Randy", "medium" }, { "Parted", "medium" }, { "SideCut", "medium" },
            { "Bravo", "short" }, { "William", "short" }, { "Fringe", "short" }, { "Burgundy", "short" }, { "Revolt", "short" },
            { "Troubadour", "short" }, { "Recruit", "short" }, { "ShortCut", "short" }, { "Topdog", "short" }, { "Warden", "short" },
            // Ghoul hairs: a ghoul's own head brings its own (People.Anomaly); this is for a ghoul head that is missing.
            { "Ghoul_Furious", "short" }, { "Ghoul_Hungry", "short" }, { "Ghoul_Ravenous", "short" }, { "Ghoul_Twisted", "short" },
            { "Ghoul_Vengeful", "short" }, { "Ghoul_Warped", "short" }, { "Ghoul_Wicked", "short" },
        };

        // Every vanilla and DLC BeardDef (null: no beard).
        private static readonly Dictionary<string, string> beardOf = new Dictionary<string, string>
        {
            { "NoBeard", null }, { "Stubble", null },
            { "Wizard", "long" }, { "LongDutch", "long" }, { "Bushy", "long" }, { "BushyStyled", "long" }, { "Seer", "long" },
            { "Urist", "long" }, { "BeardCurly", "long" }, { "TriBraid", "long" }, { "Fork", "long" },
            { "BeardFurskinMegabraid", "long" }, { "BeardFurskinSenile", "long" },
            { "Moustache", "moustache" }, { "Machete", "moustache" }, { "Classy", "moustache" },
            { "BeardFurskinMoustache", "moustache" }, { "FurskinCurlyMoustache", "moustache" },
            { "Goatee", "goatee" }, { "SoulPatch", "goatee" },
            { "Full", "full" }, { "Boxed", "full" }, { "Circle", "full" }, { "Curtain", "full" }, { "Ducktail", "full" },
            { "Lincoln", "full" }, { "MuttonChops", "full" }, { "OldDutch", "full" }, { "SideWhiskers", "full" }, { "French", "full" },
            { "VanDyke", "full" }, { "Braided", "full" }, { "Norse", "full" }, { "StacheAndChops", "full" }, { "Anchor", "full" },
            { "BeardFurskinKnots", "full" }, { "BeardFurskinMonkey", "full" }, { "BeardFurskinScruffy", "full" },
            { "BeardFurskinSideChops", "full" }, { "BeardFurskinSideTufts", "full" }, { "BeardFurskinStrongChops", "full" },
        };

        private static readonly Dictionary<string, bool> fileThere = new Dictionary<string, bool>();

        // The hair kind drawn for this style (a part whose file exists), or null for none.
        public static string Hair(HairDef def)
        {
            if (def == null)
            {
                return null;
            }
            string kind;
            if (!hairOf.TryGetValue(def.defName ?? "", out kind))
            {
                kind = HairByTags(def);
            }
            return Drawn("person_hair_", kind, hairStandIn);
        }

        // The beard kind drawn for this style, or null for none.
        public static string Beard(BeardDef def)
        {
            if (def == null)
            {
                return null;
            }
            string kind;
            if (!beardOf.TryGetValue(def.defName ?? "", out kind))
            {
                kind = HasTag(def.styleTags, "MoustacheOnly") ? "moustache" : BeardByName(def.defName ?? "");
            }
            return Drawn("person_beard_", kind, beardStandIn);
        }

        // The kind itself if its file is there, else its stand-in (and so on).
        private static string Drawn(string prefix, string kind, Dictionary<string, string> standIn)
        {
            for (int guard = 0; kind != null && guard < 8; guard++)
            {
                if (Exists(prefix + kind))
                {
                    return kind;
                }
                string next;
                kind = standIn.TryGetValue(kind, out next) ? next : null;
            }
            return null;
        }

        public static bool Exists(string part)
        {
            bool there;
            if (!fileThere.TryGetValue(part, out there))
            {
                there = IsoRimMod.RootDir != null && File.Exists(Path.Combine(IsoRimMod.RootDir, "Models", part + ".obj"));
                fileThere[part] = there;
            }
            return there;
        }

        // A modded hair style: by its style tags, as the game sorts styles; by its name when no tag says.
        private static string HairByTags(HairDef def)
        {
            List<string> tags = def.styleTags;
            if (HasTag(tags, "Bald"))
            {
                return null;
            }
            if (HasTag(tags, "Shaved"))
            {
                return "buzz";
            }
            if (HasTag(tags, "Balding"))
            {
                return "balding";
            }
            if (HasTag(tags, "HairLong"))
            {
                return "long";
            }
            if (HasTag(tags, "HairShort"))
            {
                return "short";
            }
            return HairByName(def.defName ?? "");
        }

        private static bool HasTag(List<string> tags, string tag)
        {
            return tags != null && tags.Contains(tag);
        }

        private static bool Has(string text, string word)
        {
            return text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Last resort for a modded hair style: the nearest shape going by words in its name.
        private static string HairByName(string name)
        {
            if (name.Length == 0 || Has(name, "Bald") || Has(name, "Shaved"))
            {
                return null;
            }
            if (Has(name, "Mohawk") || Has(name, "Fauxhawk") || Has(name, "Warhawk") || Has(name, "Fin"))
            {
                return "mohawk";
            }
            if (Has(name, "Afro") || Has(name, "Curly") || Has(name, "Poof") || Has(name, "Fro"))
            {
                return "afro";
            }
            if (Has(name, "Pony") || Has(name, "Tail") || Has(name, "Braid") || Has(name, "Queue"))
            {
                return "ponytail";
            }
            if (Has(name, "Bun") || Has(name, "Topknot") || Has(name, "Knot") || Has(name, "Dango"))
            {
                return "bun";
            }
            if (Has(name, "Spik") || Has(name, "Mess") || Has(name, "Wild") || Has(name, "Scat") || Has(name, "Rebel") || Has(name, "Flowy"))
            {
                return "spiky";
            }
            if (Has(name, "Long") || Has(name, "Princess") || Has(name, "Wavy") || Has(name, "Lace") || Has(name, "Mane") || Has(name, "Elder")
                || Has(name, "Senorita") || Has(name, "Curtain"))
            {
                return "long";
            }
            if (Has(name, "Bob") || Has(name, "Mop") || Has(name, "Shoulder") || Has(name, "Pigtails") || Has(name, "Decent") || Has(name, "Greasy")
                || Has(name, "Bowl") || Has(name, "Lackland") || Has(name, "Tuft"))
            {
                return "medium";
            }
            return "short";
        }

        // Last resort for a modded beard style.
        private static string BeardByName(string name)
        {
            if (Has(name, "NoBeard") || Has(name, "Stubble"))
            {
                return null;
            }
            if (Has(name, "Moustache") || Has(name, "Mustache"))
            {
                return "moustache";
            }
            return Has(name, "Goatee") || Has(name, "Soul") || Has(name, "Chin") ? "goatee" : "full";
        }
    }
}
