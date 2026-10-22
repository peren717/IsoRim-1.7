using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Buildings the game draws differently once they are empty (CompEmptyStateGraphic: an opened
    // mech capsule). With a model whose entry has an emptyModel, ModelBaker.Bake picks that model
    // while the game would draw the empty state, and the block is rebuilt when the state changes
    // (checked as the game draws the thing each frame, Patches.ThingDraw_Prefix). Either way, once
    // a model stands in, the game's flat "empty" sprite drawn every frame over the body is left
    // out (Patches.EmptyStatePostDraw_Prefix); the one it prints into the map mesh already is,
    // with the rest of the sprite.
    // Addon states (ModelDef.byState): an addon names a thing's state (Api.AddModelState: a VFE boilpot that holds a
    // brew), ModelBaker.Bake picks that state's model, and the block is rebuilt when the state changes (same check).
    public static class StateModels
    {
        // The addons' state functions (Api.AddModelState), asked in turn.
        private static readonly List<Func<Thing, string>> stateFuncs = new List<Func<Thing, string>>();
        // Things with byState models in a built mesh, with the state they were built in ("" for none).
        private static readonly Dictionary<int, string> stateBaked = new Dictionary<int, string>();

        public static void AddState(Func<Thing, string> f)
        {
            if (f != null && !stateFuncs.Contains(f))
            {
                stateFuncs.Add(f);
            }
        }

        // The thing's state as the first addon that knows it names it; null if none does (or an addon failed).
        public static string StateOf(Thing t)
        {
            for (int i = 0; i < stateFuncs.Count; i++)
            {
                string s;
                try
                {
                    s = stateFuncs[i](t);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] An addon's model state failed: " + e, 93611291);
                    continue;
                }
                if (s != null)
                {
                    return s;
                }
            }
            return null;
        }

        // Things with an empty-state sprite whose model is in a built mesh, with the state it was built in.
        private static readonly Dictionary<int, bool> baked = new Dictionary<int, bool>();
        // hackedGlowOff copies, by the id of the model they were made from.
        private static readonly Dictionary<int, Model> glowOff = new Dictionary<int, Model>();

        public static CompEmptyStateGraphic CompOf(Thing t)
        {
            ThingWithComps twc = t as ThingWithComps;
            return twc != null ? twc.GetComp<CompEmptyStateGraphic>() : null;
        }

        // (Api 24) True while the plant shows its young stage: as the game's Plant.Graphic shows immatureGraphic, while it is
        // not HarvestableNow (growth up to harvestMinGrowth); a plant that yields nothing (a rose) by the same growth.
        public static bool Immature(Plant p)
        {
            if (p.def.plant == null)
            {
                return false;
            }
            return p.def.plant.Harvestable ? !p.HarvestableNow : p.Growth <= p.def.plant.harvestMinGrowth;
        }

        // Plants with a young stage in a built mesh, and whether they were built young (harness checks: YoungBaked).
        private static readonly Dictionary<int, bool> youngBaked = new Dictionary<int, bool>();
        internal static bool TrackYoung;   // set by the harness probe (plantcheck); off in play, so nothing is kept per plant

        // How the plant was last built: young, mature, or null if never built with a young-stage entry.
        internal static bool? YoungBaked(Thing t)
        {
            bool y;
            return youngBaked.TryGetValue(t.thingIDNumber, out y) ? y : (bool?)null;
        }

        public static bool IsEmpty(Thing t)
        {
            CompEmptyStateGraphic c = CompOf(t);
            return c != null && c.ParentIsEmpty;
        }

        // Called by ModelBaker.Bake for the real thing (not a ghost): which model to use, and noted.
        public static Model Choose(Thing t, ModelEntry entry, Model normal)
        {
            // (Api 24) A plant's young stage (ModelDef.immatureModels / immatureHide): while it is not yet harvestable, as the
            // game's Plant.Graphic picks its immatureGraphic. The variant keeps its place in the list (its ID picks it).
            if (entry.immature != null && t is Plant plant)
            {
                bool young = Immature(plant);
                if (TrackYoung)
                {
                    youngBaked[t.thingIDNumber] = young;   // (harness only)
                }
                if (young)
                {
                    int i = entry.variants != null ? Array.IndexOf(entry.variants, normal) : -1;
                    return entry.immature[(i < 0 ? 0 : i) % entry.immature.Length];
                }
                return normal;
            }
            // A map portal with an open model (ancient stockpile hatch): open while it can be entered, as the
            // game's Print picks its open sprite. The game redraws the map mesh when that changes (hacked).
            if (entry.open != null && t is MapPortal portal)
            {
                return portal.IsEnterable(out string _) ? entry.open : normal;
            }
            // A hacked model (relay stabilizer: its core goes dark), as the game's CompHackable.PostPrintOnto prints its
            // hacked sprite. Hacking redraws the map mesh (CompHackable.Hack: DirtyMapMesh), so the block is rebuilt.
            // hackedGlowOff: the same model with its glow faces dark (the ancient grav reactor's panel).
            if (entry.hacked != null || entry.def.hackedGlowOff)
            {
                CompHackable hack = (t as ThingWithComps)?.GetComp<CompHackable>();
                if (hack != null && hack.IsHacked)
                {
                    if (entry.hacked != null)
                    {
                        return entry.hacked;
                    }
                    Model off;
                    if (!glowOff.TryGetValue(normal.id, out off))
                    {
                        off = ObjLoader.GlowOff(normal);
                        glowOff[normal.id] = off;
                    }
                    return off;
                }
            }
            if (entry.byState != null)
            {
                string key = StateOf(t);
                stateBaked[t.thingIDNumber] = key ?? "";
                Model own;
                if (key != null && entry.byState.TryGetValue(key, out own))
                {
                    return own;
                }
            }
            CompEmptyStateGraphic c = CompOf(t);
            if (c == null)
            {
                return normal;
            }
            bool empty = c.ParentIsEmpty;
            baked[t.thingIDNumber] = empty;
            return empty && entry.empty != null ? entry.empty : normal;
        }

        // Unlit copies (glow faces in their own colour, shaded), by the id of the model they were made from.
        private static readonly Dictionary<int, Model> unlit = new Dictionary<int, Model>();

        // Called by ModelBaker.Bake after Choose for the real thing: a lamp whose light is out draws its glow* faces unlit,
        // in their own colour, as the rest of the model. Out is the game's own state: a spawned thing with a CompGlower
        // none of whose glowers is lit (CompGlower.Glows: power, fuel, a flick switch, a sun lamp's schedule ...).
        // Flames and embers are left to the fuel (ModelBaker.NoFire). The game redraws the map mesh at the thing when its
        // light goes on or off (CompGlower.UpdateLit: MapMeshDirty Things), so the block is rebuilt.
        public static Model ForLight(Thing t, Model m)
        {
            if (m == null || !LightOut(t))
            {
                return m;
            }
            Model off;
            if (!unlit.TryGetValue(m.id, out off))
            {
                off = ObjLoader.GlowOff(m, 1f, true);
                unlit[m.id] = off;
            }
            return off;
        }

        // True for a spawned thing with a CompGlower that is not lit now.
        public static bool LightOut(Thing t)
        {
            ThingWithComps twc = t as ThingWithComps;
            if (twc == null || !twc.Spawned)
            {
                return false;
            }
            bool any = false;
            List<ThingComp> comps = twc.AllComps;
            for (int i = 0; i < comps.Count; i++)
            {
                CompGlower g = comps[i] as CompGlower;
                if (g != null)
                {
                    if (g.Glows)
                    {
                        return false;
                    }
                    any = true;
                }
            }
            return any;
        }

        // True while a model stands in for this thing's body (and so for its empty-state sprite).
        public static bool StandsIn(Thing t)
        {
            return View3D.Ready && baked.ContainsKey(t.thingIDNumber) && Forms.For(t.def).form == Form3D.Model;
        }

        // Each frame the game draws the thing: rebuilds its block when it has just been emptied (or filled).
        public static void Track(Thing t)
        {
            TrackState(t);
            bool was;
            if (!View3D.Ready || !baked.TryGetValue(t.thingIDNumber, out was))
            {
                return;
            }
            ThingForm f = Forms.For(t.def);
            if (f.form != Form3D.Model || f.model == null || f.model.empty == null)
            {
                return;
            }
            bool now = IsEmpty(t);
            if (now == was || t.Map == null)
            {
                return;
            }
            baked[t.thingIDNumber] = now;
            if (t.def.drawerType == DrawerType.RealtimeOnly)
            {
                MapSections.QueueAround(t.Map, t.Position, 0, typeof(SectionLayer_IsoRimBodies), true);
            }
            else
            {
                t.DirtyMapMesh(t.Map);
            }
        }

        // Each frame the game draws the thing: rebuilds its block when an addon's state for it has changed.
        private static void TrackState(Thing t)
        {
            string was;
            if (stateFuncs.Count == 0 || !View3D.Ready || !stateBaked.TryGetValue(t.thingIDNumber, out was))
            {
                return;
            }
            ThingForm f = Forms.For(t.def);
            if (f.form != Form3D.Model || f.model == null || f.model.byState == null || t.Map == null)
            {
                return;
            }
            string now = StateOf(t) ?? "";
            if (now == was)
            {
                return;
            }
            stateBaked[t.thingIDNumber] = now;
            if (t.def.drawerType == DrawerType.RealtimeOnly)
            {
                MapSections.QueueAround(t.Map, t.Position, 0, typeof(SectionLayer_IsoRimBodies), true);
            }
            else
            {
                t.DirtyMapMesh(t.Map);
            }
        }

        // For the harness: the addon state a thing was last built in; null if none.
        public static string StateReport(Thing t)
        {
            string was;
            return stateBaked.TryGetValue(t.thingIDNumber, out was) ? was : null;
        }

        // For the harness: the state each such thing was last built in.
        public static string Report(Thing t)
        {
            bool was;
            return baked.TryGetValue(t.thingIDNumber, out was) ? (was ? "empty" : "full") : "not built";
        }
    }
}
