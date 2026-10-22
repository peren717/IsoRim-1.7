using System.Collections.Generic;
using Verse;

namespace IsoRim
{
    // Dev-mode model binder (Binder/): a thing's model entry replaced for this session by one built from a ModelDef the binder
    // edits (a clone, never a loaded def). Nothing here is saved; every override is dropped when a game is started or loaded
    // (View3D.NewGame). OverrideGeneration is part of the view's state signature, so each change rebuilds the map (debounced).
    public static partial class ModelLibrary
    {
        private struct Original
        {
            public ModelEntry entry;   // null: the thing had no entry
            public string how;
        }

        private static readonly Dictionary<ThingDef, Original> overridden = new Dictionary<ThingDef, Original>();

        // Goes up with every override change (View3D.StateSignature).
        public static int OverrideGeneration;

        public static bool IsOverridden(ThingDef def)
        {
            return def != null && overridden.ContainsKey(def);
        }

        // The thing's own entry before any override (null if it had none); the current one if it has no override.
        public static ModelEntry OriginalEntry(ThingDef def)
        {
            Original o;
            if (overridden.TryGetValue(def, out o))
            {
                return o.entry;
            }
            ModelEntry e;
            return byThing.TryGetValue(def, out e) ? e : null;
        }

        public static string OriginalHow(ThingDef def)
        {
            Original o;
            return overridden.TryGetValue(def, out o) ? o.how : HowBound(def);
        }

        // Draws def with an entry made from live (its paths relative to live.modContentPack, or absolute) until cleared.
        public static ModelEntry SetOverride(ThingDef def, ModelDef live)
        {
            if (!overridden.ContainsKey(def))
            {
                Original o;
                byThing.TryGetValue(def, out o.entry);
                how.TryGetValue(def, out o.how);
                overridden[def] = o;
            }
            ModelEntry entry = MakeEntry(live);
            byThing[def] = entry;
            how[def] = "binder";
            OverrideGeneration++;
            return entry;
        }

        public static void ClearOverride(ThingDef def)
        {
            Original o;
            if (!overridden.TryGetValue(def, out o))
            {
                return;
            }
            overridden.Remove(def);
            if (o.entry != null)
            {
                byThing[def] = o.entry;
            }
            else
            {
                byThing.Remove(def);
            }
            if (o.how != null)
            {
                how[def] = o.how;
            }
            else
            {
                how.Remove(def);
            }
            OverrideGeneration++;
        }

        public static void ClearOverrides()
        {
            if (overridden.Count == 0)
            {
                return;
            }
            foreach (ThingDef def in new List<ThingDef>(overridden.Keys))
            {
                ClearOverride(def);
            }
        }

        // The next LoadFile of this path reads the file again (the binder's Reload).
        public static void Forget(string path)
        {
            if (path != null)
            {
                byPath.Remove(path);
            }
        }
    }
}
