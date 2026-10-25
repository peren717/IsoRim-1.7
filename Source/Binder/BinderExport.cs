using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security;
using System.Text;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The model binder's export: a pack folder under <save data>/IsoRimBinder/<pack>/ (never a Mods or Workshop folder) laid out
    // as IsoRim's own packs are (Docs/ModelPacks.md):
    //   Defs/IsoRimBinder_<thing>.xml     the ModelDef (defName R3D_<pack>_<thing>) WITHOUT thingDef and with only the fields
    //                                     every IsoRim with an Api has (BaseVersion), its model paths in Models/
    //   Patches/IsoRimBinder_<thing>.xml  IsoRimPackGate.PatchOperation_IfIsoRimApi adding the thingDef AND the newer fields when
    //                                     IsoRim's Api.Version is at least the highest version the fields used need: an older
    //                                     IsoRim never reads a field it doesn't know (an XML error) and never binds a
    //                                     half-configured model, it leaves the thing as it was
    //   Models/                           copies of the .obj files and the .mtl files they name
    //   About/About.xml, 1.7/Assemblies/IsoRimPackGate.dll   written once, if missing, so the folder loads as a mod
    // A def a patch adds has no mod of its own (modContentPack null) and IsoRim could not find its files: hence the def in
    // Defs and only the binding in the patch.
    public static class BinderExport
    {
        public const string GateClass = "IsoRimPackGate.PatchOperation_IfIsoRimApi";

        // The Api version a ModelDef field needs; fields not listed need the base binding (2). Every new ModelDef field claims
        // its number here.
        private static readonly Dictionary<string, int> FieldVersion = new Dictionary<string, int>
        {
            { "deadBellyUp", 5 }, { "hackedGlowOff", 5 }, { "legFromHip", 5 }, { "legLift", 5 }, { "legYaw", 5 },
            { "rotationByFacing", 6 }, { "style", 6 }, { "crownTop", 6 }, { "doorLeafRise", 6 }, { "byState", 6 },
            { "wallTop", 11 },
            { "backHeight", 13 },
            { "wingModel", 14 }, { "wings", 14 }, { "wingFold", 14 }, { "wingDroop", 14 }, { "wingFlap", 14 }, { "wingBeat", 14 },
            { "aged", 15 },
            { "graphicPath", 16 },
            { "doorLeafGather", 18 },
            { "cutaway", 20 },
            { "immatureModels", 24 }, { "immatureHide", 24 },
            { "bobByState", 27 },
            { "slots", 28 },
            { "glassTint", 30 }, { "glassOpacity", 30 }, { "glassGlow", 30 },
            { "topThroughRoof", 31 }, { "roofModel", 31 },
            { "hoverHeight", 32 }, { "hoverBob", 32 },
            { "beamFlicker", 33 }, { "beamPulse", 33 }, { "beamMotes", 33 },
        };
        private const int BaseVersion = 2;

        // A multi-cell supported door (not the game's Building_MultiTileDoor) is drawn from its models since this version.
        private const int WideSupportedDoorVersion = 29;

        // A door's glassModel (a window, a vac barrier's field) is drawn since this version.
        private const int DoorGlassVersion = 30;

        // Fields never written: the binding (the patch adds it) and the matching rules of shared entries.
        private static readonly HashSet<string> Skip = new HashSet<string> { "thingDef", "thingCategory", "match", "graphicPath", "fallback", "style" };

        public static string SafeName(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (char.IsLetterOrDigit(c) && c < 128 || c == '_')
                {
                    b.Append(c);
                }
            }
            return b.ToString();
        }

        // Writes the export; returns null and the pack folder, or a short error.
        public static string Write(ModelDef live, ThingDef thing, string packName, out string dir)
        {
            dir = null;
            string pack = SafeName(packName);
            if (pack.Length == 0)
            {
                return "R3D_Binder_BadPack".Translate();
            }
            if (live.model.NullOrEmpty() && (live.models == null || live.models.Count == 0))
            {
                return "R3D_Binder_NoFile".Translate();
            }
            string root = Path.Combine(ModelBinder.Folder, pack);
            dir = root;
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "Defs"));
                Directory.CreateDirectory(Path.Combine(dir, "Patches"));
                Directory.CreateDirectory(Path.Combine(dir, "Models"));

                // Model files: copied into Models/, paths in the def rewritten to Models/<name>.
                Dictionary<string, string> copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Func<string, string> mapPath = p =>
                {
                    string full = ModelBinder.FullPath(live, p);
                    string rel;
                    if (!copied.TryGetValue(full, out rel))
                    {
                        rel = "Models/" + CopyModel(full, Path.Combine(root, "Models"), copied.Count);
                        copied[full] = rel;
                    }
                    return rel;
                };

                HashSet<string> used = new HashSet<string>();
                string defName = "R3D_" + pack + "_" + thing.defName;
                StringBuilder body = new StringBuilder();
                StringBuilder newer = new StringBuilder();
                WriteFields(body, live, new ModelDef(), "    ", mapPath, used, newer);

                int minVersion = BaseVersion;
                foreach (string f in used)
                {
                    minVersion = Math.Max(minVersion, VersionOf(f));
                }
                if (Ghosts.WideSupported(thing))
                {
                    minVersion = Math.Max(minVersion, WideSupportedDoorVersion);   // drawn from its models only since Api 29
                }
                if (used.Contains("glassModel") && typeof(RimWorld.Building_Door).IsAssignableFrom(thing.thingClass))
                {
                    minVersion = Math.Max(minVersion, DoorGlassVersion);           // a door's glass part is drawn since Api 30
                }

                string source = string.Join(", ", new List<string>(copied.Keys).ConvertAll(Path.GetFullPath));
                StringBuilder defs = new StringBuilder();
                defs.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
                defs.Append("<!-- IsoRim model binder export, ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                    .Append(", IsoRim Api ").Append(Api.Version).Append(".\n     Model for ").Append(thing.defName).Append(" from: ")
                    .Append(Escape(source).Replace("--", "- -")).Append("\n     No thingDef here").Append(newer.Length > 0 ? " and no fields newer than Api " + BaseVersion : "")
                    .Append(": Patches/IsoRimBinder_").Append(thing.defName)
                    .Append(".xml adds them (see IsoRim's Docs/ModelPacks.md). -->\n");
                defs.Append("<Defs>\n  <IsoRim.ModelDef>\n    <defName>").Append(defName).Append("</defName>\n");
                defs.Append(body);
                defs.Append("  </IsoRim.ModelDef>\n</Defs>\n");
                File.WriteAllText(Path.Combine(dir, "Defs", "IsoRimBinder_" + thing.defName + ".xml"), defs.ToString(), new UTF8Encoding(false));

                StringBuilder patch = new StringBuilder();
                patch.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Patch>\n");
                patch.Append("  <!-- Binds ").Append(defName).Append(" to ").Append(thing.defName)
                    .Append(newer.Length > 0 ? " and adds its fields newer than Api " + BaseVersion : "")
                    .Append(" when IsoRim has Api ").Append(minVersion).Append(" or later (the fields it uses); an older IsoRim leaves the thing as it is. -->\n");
                patch.Append("  <Operation Class=\"").Append(GateClass).Append("\">\n");
                patch.Append("    <minVersion>").Append(minVersion).Append("</minVersion>\n");
                patch.Append("    <apply Class=\"PatchOperationAdd\">\n");
                patch.Append("      <xpath>/Defs/IsoRim.ModelDef[defName=\"").Append(defName).Append("\"]</xpath>\n");
                if (newer.Length == 0)
                {
                    patch.Append("      <value><thingDef>").Append(thing.defName).Append("</thingDef></value>\n");
                }
                else
                {
                    patch.Append("      <value>\n        <thingDef>").Append(thing.defName).Append("</thingDef>\n");
                    patch.Append(newer);
                    patch.Append("      </value>\n");
                }
                patch.Append("    </apply>\n  </Operation>\n</Patch>\n");
                File.WriteAllText(Path.Combine(dir, "Patches", "IsoRimBinder_" + thing.defName + ".xml"), patch.ToString(), new UTF8Encoding(false));

                WriteAbout(dir, pack);
                CopyGate(dir);
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Binder export failed: " + e);
                return "R3D_Binder_ExportFailed".Translate(e.Message);
            }
            return null;
        }

        // Copies an .obj and the .mtl files it names (mtllib, next to it) into models; returns the .obj's new name.
        private static string CopyModel(string full, string models, int index)
        {
            if (!File.Exists(full))
            {
                throw new FileNotFoundException(full);
            }
            string name = Path.GetFileName(full);
            File.Copy(full, Path.Combine(models, name), true);
            string from = Path.GetDirectoryName(full);
            foreach (string raw in File.ReadAllLines(full))
            {
                string line = raw.Trim();
                if (!line.StartsWith("mtllib "))
                {
                    continue;
                }
                string mtl = line.Substring(7).Trim();
                string src = Path.Combine(from, mtl);
                if (File.Exists(src) && Path.GetFileName(mtl) == mtl)
                {
                    File.Copy(src, Path.Combine(models, mtl), true);
                }
            }
            return name;
        }

        private static void WriteAbout(string dir, string pack)
        {
            string about = Path.Combine(dir, "About", "About.xml");
            if (File.Exists(about))
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(about));
            string text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ModMetaData>\n"
                + "  <name>" + pack + "</name>\n"
                + "  <author>YourName</author>\n"
                + "  <packageId>YourName." + pack + "</packageId>\n"
                + "  <supportedVersions>\n    <li>1.7</li>\n  </supportedVersions>\n"
                + "  <modDependencies>\n    <li>\n      <packageId>Peren.IsoRim17</packageId>\n      <displayName>IsoRim Waylith</displayName>\n"
                + "      <steamWorkshopUrl>steam://url/CommunityFilePage/3819577715</steamWorkshopUrl>\n    </li>\n  </modDependencies>\n"
                + "  <loadAfter>\n    <li>Peren.IsoRim17</li>\n  </loadAfter>\n"
                + "  <description>3D models for IsoRim Waylith, made with its model binder.</description>\n"
                + "</ModMetaData>\n";
            File.WriteAllText(about, text, new UTF8Encoding(false));
        }

        // The patch class lives in a tiny DLL every pack ships (the same file in each: IsoRim's Tools/PackGate).
        private static void CopyGate(string dir)
        {
            string gate = Path.Combine(IsoRimMod.RootDir, "Tools", "PackGate", "IsoRimPackGate.dll");
            string to = Path.Combine(dir, "1.7", "Assemblies", "IsoRimPackGate.dll");
            if (File.Exists(to) || !File.Exists(gate))
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(gate, to);
        }

        // ---- XML

        private static int VersionOf(string field)
        {
            int v;
            return FieldVersion.TryGetValue(field, out v) ? v : BaseVersion;
        }

        // The fields of obj that differ from those of a default instance, one element each. newer != null: obj is the
        // ModelDef (only its own fields, not Def's; Skip left out), and its fields that need more than BaseVersion go into
        // newer (indented for the patch's <value>) instead of into.
        private static void WriteFields(StringBuilder into, object obj, object defaults, string indent, Func<string, string> mapPath,
            HashSet<string> used, StringBuilder newer)
        {
            bool top = newer != null;
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | (top ? BindingFlags.DeclaredOnly : 0);
            foreach (FieldInfo f in obj.GetType().GetFields(flags))
            {
                if (top && Skip.Contains(f.Name))
                {
                    continue;
                }
                object v = f.GetValue(obj);
                object d = defaults != null ? f.GetValue(defaults) : null;
                if (Same(v, d))
                {
                    continue;
                }
                StringBuilder sb = into;
                string ind = indent;
                if (top && VersionOf(f.Name) > BaseVersion)
                {
                    sb = newer;
                    ind = "        ";   // inside the patch's <value>
                }
                if (v is IList list)
                {
                    if (list.Count == 0)
                    {
                        continue;
                    }
                    sb.Append(ind).Append('<').Append(f.Name).Append(">\n");
                    foreach (object item in list)
                    {
                        if (item == null || Simple(item.GetType()))
                        {
                            sb.Append(ind).Append("  <li>").Append(Format(item, mapPath)).Append("</li>\n");
                        }
                        else
                        {
                            sb.Append(ind).Append("  <li>\n");
                            WriteFields(sb, item, Activator.CreateInstance(item.GetType()), ind + "    ", mapPath, used, null);
                            sb.Append(ind).Append("  </li>\n");
                        }
                    }
                    sb.Append(ind).Append("</").Append(f.Name).Append(">\n");
                }
                else if (v != null && !Simple(v.GetType()))
                {
                    sb.Append(ind).Append('<').Append(f.Name).Append(">\n");
                    WriteFields(sb, v, Activator.CreateInstance(v.GetType()), ind + "  ", mapPath, used, null);
                    sb.Append(ind).Append("</").Append(f.Name).Append(">\n");
                }
                else
                {
                    sb.Append(ind).Append('<').Append(f.Name).Append('>').Append(Format(v, mapPath)).Append("</").Append(f.Name).Append(">\n");
                }
                if (top)
                {
                    used.Add(f.Name);
                }
            }
        }

        private static bool Simple(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Color)
                || t == typeof(IntVec2) || t == typeof(IntVec3);
        }

        private static bool Same(object a, object b)
        {
            bool noA = a == null || a is IList ea && ea.Count == 0;
            bool noB = b == null || b is IList eb && eb.Count == 0;
            if (noA || noB)
            {
                return noA && noB;
            }
            if (a is IList la && b is IList lb)
            {
                if (la.Count != lb.Count)
                {
                    return false;
                }
                for (int i = 0; i < la.Count; i++)
                {
                    if (!Equals(la[i], lb[i]))
                    {
                        return false;
                    }
                }
                return true;
            }
            if (!Simple(a.GetType()))
            {
                return false;   // a nested object (wallTop ...) set at all is written
            }
            return Equals(a, b);
        }

        private static string Format(object v, Func<string, string> mapPath)
        {
            if (v == null)
            {
                return "";
            }
            if (v is string s)
            {
                if (s.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                {
                    s = mapPath(s);
                }
                return Escape(s);
            }
            if (v is float f)
            {
                return Num(f);
            }
            if (v is bool b)
            {
                return b ? "true" : "false";
            }
            if (v is Vector2 v2)
            {
                return "(" + Num(v2.x) + ", " + Num(v2.y) + ")";
            }
            if (v is Vector3 v3)
            {
                return "(" + Num(v3.x) + ", " + Num(v3.y) + ", " + Num(v3.z) + ")";
            }
            if (v is Color c)
            {
                return "(" + Num(c.r) + ", " + Num(c.g) + ", " + Num(c.b) + ", " + Num(c.a) + ")";
            }
            if (v is IFormattable fm)
            {
                return fm.ToString(null, CultureInfo.InvariantCulture);
            }
            return Escape(v.ToString());
        }

        private static string Num(float f)
        {
            return f.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        private static string Escape(string s)
        {
            return SecurityElement.Escape(s) ?? "";
        }
    }
}
