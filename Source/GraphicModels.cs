using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 39) Models by the sprite the game draws a thing with now (ModelDef.byGraphic). Some mods override Thing.Graphic
    // and pick the sprite at runtime from the thing's surroundings (a conveyor piece: straight, a turn, a merger, a
    // splitter; a hopper lit for input or output). The entry whose path matches the thing's Graphic.path stands in:
    //   - ModelBaker.Bake picks its model (StateModels.Choose) and turn (YawFor: a Graphic_Multi turns with the thing's
    //     facing, any other graphic is drawn north-up as the game draws such a sprite), plus the entry's yaw;
    //   - each frame the game draws the thing (Patches.ThingDraw_Prefix -> StateModels.Track) the matching entry is looked
    //     up again (a dictionary lookup by Graphic object) and the block rebuilt if it changed;
    //   - while the model stands in, the thing's own sprite drawn every frame by a custom DrawAt (Graphic.Draw of
    //     thing.Graphic) is left out (Patches.GraphicDraw_Prefix -> SkipsSprite); what it draws besides (items, comps,
    //     overlays from other graphics) is kept.
    // (Api 50) The path matched is that of the sprite the thing really shows (Matched): a Graphic_RandomRotated (the game's
    // wrapper turning a weapon on the ground; its own path is empty) is unwrapped, and of a Graphic_Random the variant this
    // thing draws is matched first (Graphic_Random.SubGraphicFor: its folder's path plus its own name, a unique weapon's
    // .../UniqueAssaultRifleB), then its folder's path. A weapon so bound shows the entry's model where its sprite matches
    // (WeaponModels.ModelOf), else its def's model, else its sprite.
    public static class GraphicModels
    {
        public sealed class Entry
        {
            public string path;      // normalised: lower case, '/' separators, no trailing '/'
            public bool prefix;
            public Model model;
            public float yaw;
            public int facing = -1;  // only for a thing facing this way (Rot4.AsInt); -1 any facing
        }

        // Defs of things built with a byGraphic entry (a quick test for the per-frame checks).
        private static readonly HashSet<ThingDef> defs = new HashSet<ThingDef>();
        // Things built with a byGraphic entry: the index of the entry they were built with (-1: none matched).
        private static readonly Dictionary<int, int> baked = new Dictionary<int, int>();
        // Per ModelEntry: Graphic object -> index of the first matching entry per facing (-1 none, MinValue not looked up yet);
        // graphics are pooled by the game, so it stays small.
        private static readonly Dictionary<ModelEntry, Dictionary<Graphic, int[]>> lookup = new Dictionary<ModelEntry, Dictionary<Graphic, int[]>>();
        // (Api 50) A Graphic_Random's variant -> that Graphic_Random's (folder) path, normalised: matched after the variant's.
        private static readonly Dictionary<Graphic, string> folders = new Dictionary<Graphic, string>();

        internal static Entry[] Load(ModelDef md)
        {
            if (md.byGraphic == null || md.byGraphic.Count == 0 || md.modContentPack == null)
            {
                return null;
            }
            List<Entry> list = new List<Entry>();
            for (int i = 0; i < md.byGraphic.Count; i++)
            {
                GraphicModel g = md.byGraphic[i];
                if (g == null || g.path.NullOrEmpty() || g.model.NullOrEmpty())
                {
                    Log.Warning("[IsoRim] " + md.defName + ": a byGraphic entry needs a path and a model.");
                    continue;
                }
                Model m = ModelLibrary.LoadFile(Path.Combine(md.modContentPack.RootDir, g.model));
                if (m == null)
                {
                    continue;   // LoadFile has reported it
                }
                int facing = -1;
                if (!g.facing.NullOrEmpty())
                {
                    facing = FacingOf(g.facing);
                    if (facing < 0)
                    {
                        Log.Warning("[IsoRim] " + md.defName + ": byGraphic facing '" + g.facing + "' is not north, east, south or west; the entry is left out.");
                        continue;
                    }
                }
                list.Add(new Entry { path = Normalise(g.path), prefix = g.prefix, model = m, yaw = g.yaw, facing = facing });
            }
            if (list.Count == 0)
            {
                return null;
            }
            return list.ToArray();
        }

        private static int FacingOf(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "north": return 0;
                case "east": return 1;
                case "south": return 2;
                case "west": return 3;
                default: return -1;
            }
        }

        public static string Normalise(string p)
        {
            if (p == null)
            {
                return "";
            }
            p = p.Replace('\\', '/').ToLowerInvariant();
            return p.TrimEnd('/');
        }

        // The thing's current graphic, or null (never throws).
        private static Graphic GraphicOf(Thing t)
        {
            try
            {
                return t.Graphic;
            }
            catch
            {
                return null;
            }
        }

        // (Api 50) The graphic whose path the entries match for thing t drawn with g: a Graphic_RandomRotated unwrapped, of a
        // Graphic_Random the variant t draws (its folder noted, matched after it).
        public static Graphic Matched(Graphic g, Thing t)
        {
            Graphic_RandomRotated rr = g as Graphic_RandomRotated;
            if (rr != null && rr.SubGraphic != null)
            {
                g = rr.SubGraphic;
            }
            Graphic_Random random = g as Graphic_Random;
            if (random != null && random.SubGraphicsCount > 0)
            {
                Graphic sub = random.SubGraphicFor(t);
                if (sub != null && sub != g)
                {
                    if (!folders.ContainsKey(sub))
                    {
                        folders[sub] = Normalise(random.path);
                    }
                    return sub;
                }
            }
            return g;
        }

        // The graphic the entries match for the thing now (Matched of its Graphic), or null.
        private static Graphic MatchedOf(Thing t)
        {
            Graphic g = GraphicOf(t);
            return g != null ? Matched(g, t) : null;
        }

        // Index of the first entry of e matching graphic g on a thing facing rot; -1 for none. A thing's graphic goes through
        // Matched first; a Graphic_Random's variant is matched by its own path, then by its folder's (Api 50).
        public static int IndexFor(ModelEntry e, Graphic g, Rot4 rot)
        {
            if (e == null || e.byGraphic == null || g == null)
            {
                return -1;
            }
            Dictionary<Graphic, int[]> map;
            if (!lookup.TryGetValue(e, out map))
            {
                map = new Dictionary<Graphic, int[]>();
                lookup[e] = map;
            }
            int f = rot.IsValid ? rot.AsInt : 0;
            int[] byFacing;
            if (!map.TryGetValue(g, out byFacing))
            {
                byFacing = new[] { int.MinValue, int.MinValue, int.MinValue, int.MinValue };
                map[g] = byFacing;
            }
            if (byFacing[f] != int.MinValue)
            {
                return byFacing[f];
            }
            int idx = -1;
            string folder;
            folders.TryGetValue(g, out folder);
            for (int pass = 0; pass < 2 && idx < 0; pass++)
            {
                string path = pass == 0 ? Normalise(g.path) : folder;
                if (path == null)
                {
                    break;
                }
                for (int i = 0; i < e.byGraphic.Length; i++)
                {
                    Entry b = e.byGraphic[i];
                    if ((b.facing < 0 || b.facing == f) && (b.prefix ? path.StartsWith(b.path, StringComparison.Ordinal) : path == b.path))
                    {
                        idx = i;
                        break;
                    }
                }
            }
            byFacing[f] = idx;
            return idx;
        }

        // The matching entry for the thing now, or null.
        public static Entry For(Thing t, ModelEntry e)
        {
            if (e == null || e.byGraphic == null || t == null)
            {
                return null;
            }
            int i = IndexFor(e, MatchedOf(t), t.Rotation);
            return i >= 0 ? e.byGraphic[i] : null;
        }

        // StateModels.Choose: the model to build the real thing with; normal if no entry matches. Noted for Track.
        internal static Model Choose(Thing t, ModelEntry e, Model normal)
        {
            int i = IndexFor(e, MatchedOf(t), t.Rotation);
            baked[t.thingIDNumber] = i;
            defs.Add(t.def);
            return i >= 0 ? e.byGraphic[i].model : normal;
        }

        // ModelBaker.StartPlace: the turn of a thing's model, with its byGraphic entry if one matches.
        public static float YawFor(Thing t, ModelEntry e)
        {
            if (e.byGraphic != null && t != null)
            {
                Graphic g = MatchedOf(t);
                int i = IndexFor(e, g, t.Rotation);
                if (i >= 0)
                {
                    // a Graphic_Multi turns with the facing as any model does (rotation, rotationByFacing, ignoreRotation:
                    // ModelEntry.Yaw); any other graphic is drawn north-up (only the def's rotation)
                    float baseYaw = g is Graphic_Multi ? e.Yaw(t.Rotation) : e.def.rotation;
                    return baseYaw + e.byGraphic[i].yaw;
                }
            }
            return e.Yaw(t.Rotation);
        }

        // Each frame the game draws the thing (StateModels.Track): rebuilds its block when the matching entry changed.
        internal static void Track(Thing t)
        {
            int was;
            if (defs.Count == 0 || !defs.Contains(t.def) || !baked.TryGetValue(t.thingIDNumber, out was) || t.Map == null)
            {
                return;
            }
            ThingForm f = Forms.For(t.def);
            if (f.form != Form3D.Model || f.model == null || f.model.byGraphic == null)
            {
                return;
            }
            int now = IndexFor(f.model, MatchedOf(t), t.Rotation);
            if (now == was)
            {
                return;
            }
            baked[t.thingIDNumber] = now;
            Rebuilds++;
            if (t.def.drawerType == DrawerType.RealtimeOnly)
            {
                MapSections.QueueAround(t.Map, t.Position, 0, typeof(SectionLayer_IsoRimBodies), true);
            }
            else
            {
                t.DirtyMapMesh(t.Map);
            }
        }

        // True while a byGraphic model stands in for t and g is the sprite the thing's own drawing would put over it.
        internal static bool SkipsSprite(Graphic g, Thing t)
        {
            if (t == null || defs.Count == 0 || !defs.Contains(t.def) || !View3D.Ready || !IsoRimMod.Settings.models3D)
            {
                return false;
            }
            if (!baked.ContainsKey(t.thingIDNumber) || !ReferenceEquals(g, GraphicOf(t)) || !Api.ModelStandsIn(t))
            {
                return false;
            }
            Skipped++;
            return true;
        }

        // For the harness: the entry index a thing was last built with (-1 none matched), or null if never built so.
        public static int? BakedIndex(Thing t)
        {
            int i;
            return t != null && baked.TryGetValue(t.thingIDNumber, out i) ? i : (int?)null;
        }

        // For the harness: how many blocks Track rebuilt, how many sprites SkipsSprite left out.
        public static int Rebuilds;
        public static int Skipped;
    }
}
