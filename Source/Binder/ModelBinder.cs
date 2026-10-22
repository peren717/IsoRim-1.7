using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Dev-mode model binder (dev tool, not part of the Api): pick a building or item on the map, pick an .obj, tune how it sits
    // live, and export a ModelDef plus its gated binding patch as a small pack (BinderExport). Entry: the game's debug actions,
    // "IsoRim / Model binder..." (dev mode only). Changes are drawn through ModelLibrary.SetOverride on a clone of the thing's
    // ModelDef, never on a loaded def, and last until Revert or the next game. See Docs/ModelPacks.md.
    public static class ModelBinder
    {
        // The binder's own folder under the game's save data: exports (<pack>/) and the model workspace (Models/).
        public static string Folder
        {
            get { return Path.Combine(GenFilePaths.SaveDataFolderPath, "IsoRimBinder"); }
        }

        public static string Workspace
        {
            get { return Path.Combine(Folder, "Models"); }
        }

        [DebugAction("IsoRim", "Model binder...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void OpenFromDebugMenu()
        {
            Thing sel = Find.Selector.SingleSelectedThing;
            if (sel != null)
            {
                Open(sel);
                return;
            }
            DebugTools.curTool = new DebugTool("R3D_Binder_PickThing".Translate(), PickAtMouse);
        }

        // The debug tool's click: the building or item under the pointer (IsoRim picks the cell in 3D); several in a menu.
        private static void PickAtMouse()
        {
            Map map = Find.CurrentMap;
            IntVec3 c = UI.MouseCell();
            if (map == null || !c.InBounds(map))
            {
                return;
            }
            List<Thing> things = new List<Thing>();
            foreach (Thing t in c.GetThingList(map))
            {
                if (t.def.category == ThingCategory.Building || t.def.category == ThingCategory.Item)
                {
                    things.Add(t);
                }
            }
            if (things.Count == 1)
            {
                DebugTools.curTool = null;
                Open(things[0]);
            }
            else if (things.Count > 1)
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (Thing t in things)
                {
                    Thing target = t;
                    options.Add(new FloatMenuOption(t.LabelCap + " (" + t.def.defName + ")", () =>
                    {
                        DebugTools.curTool = null;
                        Open(target);
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }

        // Opens the binder on thing t, or says in a message why it can't.
        public static BinderWindow Open(Thing t)
        {
            string why = Refusal(t);
            if (why != null)
            {
                Messages.Message(why, MessageTypeDefOf.RejectInput, false);
                return null;
            }
            BinderWindow open = Find.WindowStack.WindowOfType<BinderWindow>();
            if (open != null)
            {
                open.Close(false);
            }
            BinderWindow w = new BinderWindow(t);
            Find.WindowStack.Add(w);
            return w;
        }

        // Why the binder (v1) can't take thing t; null if it can. Plain buildings (also ones the game draws every frame) and
        // items only: doors, turrets, pawns, styled things and things whose model is shared by many defs need their own drawers
        // or entries.
        public static string Refusal(Thing t)
        {
            if (t == null)
            {
                return "R3D_Binder_OnlyThings".Translate();
            }
            if (!View3D.Active)
            {
                return "R3D_Binder_NeedView".Translate();
            }
            ThingDef def = t.def;
            if (def.category != ThingCategory.Building && def.category != ThingCategory.Item || def.IsBlueprint || def.IsFrame
                || t is MinifiedThing || t is Corpse)
            {
                return "R3D_Binder_OnlyThings".Translate();
            }
            if (typeof(Building_Door).IsAssignableFrom(def.thingClass))
            {
                return "R3D_Binder_NoDoors".Translate();
            }
            ModelEntry orig = ModelLibrary.OriginalEntry(def);
            if (typeof(Building_Turret).IsAssignableFrom(def.thingClass) || orig != null && orig.top != null)
            {
                return "R3D_Binder_NoTurrets".Translate();
            }
            if (orig != null && (orig.def.animal || orig.def.mech || orig.def.entity))
            {
                return "R3D_Binder_OnlyThings".Translate();
            }
            if (t.StyleDef != null)
            {
                return "R3D_Binder_NoStyles".Translate();
            }
            string how = ModelLibrary.OriginalHow(def);
            if (how != null && how != "thingDef")
            {
                return "R3D_Binder_Shared".Translate(how);
            }
            return null;
        }

        // A copy of md the binder may change freely: lists copied, the matching fields of a shared entry cleared. Paths stay
        // relative to md's mod (modContentPack is kept).
        public static ModelDef Clone(ModelDef md)
        {
            ModelDef c = (ModelDef)CloneMethod.Invoke(md, null);
            foreach (FieldInfo f in typeof(ModelDef).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object v = f.GetValue(c);
                if (v is System.Collections.IList list && f.FieldType.IsGenericType)
                {
                    System.Collections.IList copy = (System.Collections.IList)Activator.CreateInstance(f.FieldType);
                    foreach (object o in list)
                    {
                        copy.Add(o);
                    }
                    f.SetValue(c, copy);
                }
            }
            c.thingCategory = null;
            c.match = null;
            c.graphicPath = null;
            c.fallback = null;
            c.style = null;
            return c;
        }

        private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        // A new def for thing def: no model yet, built for its footprint; items drawn over the game's art.
        public static ModelDef Fresh(ThingDef def)
        {
            ModelDef md = new ModelDef();
            md.defName = "R3D_" + def.defName;
            md.thingDef = def.defName;
            md.builtFor = new Vector2(def.size.x, def.size.z);
            md.overArt = def.category == ThingCategory.Item;
            md.modContentPack = IsoRimContent;
            return md;
        }

        public static ModContentPack IsoRimContent
        {
            get
            {
                IsoRimMod mod = LoadedModManager.GetMod<IsoRimMod>();
                return mod != null ? mod.Content : null;
            }
        }

        // The full path of a def's model file (path relative to its mod, or absolute); null if none.
        public static string FullPath(ModelDef md, string file)
        {
            if (file.NullOrEmpty())
            {
                return null;
            }
            if (Path.IsPathRooted(file) || md.modContentPack == null)
            {
                return file;
            }
            return Path.Combine(md.modContentPack.RootDir, file);
        }

        // One .obj the picker offers: its mod's name (or the workspace) and its full path.
        public struct ObjFile
        {
            public string group;
            public string path;
            public string name;
        }

        // Every .obj under Models/ of IsoRim and of each active mod from the local Mods folder, and in the workspace.
        public static List<ObjFile> ObjFiles()
        {
            List<ObjFile> files = new List<ObjFile>();
            try
            {
                Directory.CreateDirectory(Workspace);
            }
            catch (Exception)
            {
            }
            AddObjs(files, "R3D_Binder_Workspace".Translate(), Workspace);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
            {
                bool local = pack.ModMetaData != null && pack.ModMetaData.Source == ContentSource.ModsFolder;
                if ((!local && pack != IsoRimContent) || !seen.Add(pack.RootDir))
                {
                    continue;
                }
                AddObjs(files, pack.Name, Path.Combine(pack.RootDir, "Models"));
            }
            return files;
        }

        private static void AddObjs(List<ObjFile> files, string group, string dir)
        {
            if (!Directory.Exists(dir))
            {
                return;
            }
            try
            {
                List<string> found = new List<string>(Directory.GetFiles(dir, "*.obj", SearchOption.AllDirectories));
                found.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string p in found)
                {
                    files.Add(new ObjFile { group = group, path = p, name = p.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/') });
                }
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Binder could not list " + dir + ": " + e.Message);
            }
        }

        // Things with a model entry of their own (a thingDef entry, or the binder's), for "Copy from existing".
        public static List<ThingDef> ModelledDefs()
        {
            List<ThingDef> defs = new List<ThingDef>();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                ModelEntry e;
                if (ModelLibrary.TryGet(d, out e) && e.HasModel && e.def != null && !e.def.animal && !e.def.mech && !e.def.entity)
                {
                    defs.Add(d);
                }
            }
            defs.SortBy(d => d.defName);
            return defs;
        }
    }
}
