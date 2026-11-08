using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // A pack, belt or waist tool worn on the belt layer, drawn on 3D colonists and outfit stands (People.Dress).
    //   <IsoRim.WornGearDef>
    //     <defName>R3DGear_Apparel_PackJump</defName>
    //     <apparel>Apparel_PackJump</apparel>       the apparel ThingDef
    //     <model>Models/person_pack_jump.obj</model> made on the male figure (People's figure space, facing +z)
    //     <mount>pack</mount>                       pack, belt or hip
    //     <tintMaterials><li>utility_shell</li></tintMaterials>   take the apparel's colour when it has one of its own
    //   </IsoRim.WornGearDef>
    // Mounts:
    //   pack  the back plane is z = 0 (the pack behind it, heights as worn): pushed back onto the outermost back
    //         (shirt, coat or armour) of that body; its own size kept, its place moved with the body type (BodyScales)
    //   belt  round the waist, the inner ring made for a half width of 0.19 and half depth of 0.132: stretched
    //         (not in height) to go round the outermost waist (trousers, shirt, coat or armour), then with the body
    //   hip   hung at the right hip, its clip at x = 0.19: moved out to the outermost waist, its own size kept
    // Belt-layer apparel with no WornGearDef has no 3D part and is left out (as before).
    public class WornGearDef : Def
    {
        public string apparel;
        public string model;
        public string mount = "pack";
        public List<string> tintMaterials;
    }

    internal static class WornGear
    {
        private const float BeltHalfWidth = 0.19f;
        private const float BeltHalfDepth = 0.132f;
        private const float HipClip = 0.19f;
        internal const float HipClipAnchor = HipClip;   // (Api 62, ThingModelInfo.MountAnchor)
        private const float Gap = 0.006f;

        private class Loaded
        {
            public Model model;
            public Color baseColour = Color.white;
        }

        private static Dictionary<string, WornGearDef> byApparel;
        private static readonly Dictionary<WornGearDef, Loaded> loaded = new Dictionary<WornGearDef, Loaded>();
        private static readonly Dictionary<int, Vector2> waists = new Dictionary<int, Vector2>();
        private static readonly Dictionary<int, float> backs = new Dictionary<int, float>();

        public static WornGearDef For(ThingDef apparel)
        {
            if (byApparel == null)
            {
                byApparel = new Dictionary<string, WornGearDef>();
                foreach (WornGearDef d in DefDatabase<WornGearDef>.AllDefsListForReading)
                {
                    if (!d.apparel.NullOrEmpty())
                    {
                        byApparel[d.apparel] = d;
                    }
                }
            }
            WornGearDef def;
            return apparel != null && byApparel.TryGetValue(apparel.defName, out def) ? def : null;
        }

        // The gear's model (its tint materials white, to take tint) and the colour to draw it with.
        public static Model ModelFor(WornGearDef def, Apparel a, out Color tint)
        {
            Loaded l;
            if (!loaded.TryGetValue(def, out l))
            {
                l = new Loaded();
                string root = def.modContentPack != null ? def.modContentPack.RootDir : IsoRimMod.RootDir;
                Model m = root != null && !def.model.NullOrEmpty() ? ModelLibrary.LoadFile(Path.Combine(root, def.model)) : null;
                Color c = Color.white;
                Model marked = m != null && def.tintMaterials != null ? ObjLoader.Marked(m, def.tintMaterials, out c) : null;
                l.model = marked ?? m;
                l.baseColour = marked != null ? c : Color.white;
                loaded[def] = l;
            }
            tint = l.baseColour;
            if (l.model != null && a != null && def.tintMaterials != null)
            {
                CompColorable colorable = a.TryGetComp<CompColorable>();
                if (a.def.MadeFromStuff || (colorable != null && colorable.Active))
                {
                    tint = a.DrawColor;
                }
            }
            return l.model;
        }

        // Where the gear goes on a figure dressed as look (People's figure space, before the body type's scale).
        // rigid: keeps its own size; then the result is where its own point anchor goes (only that place takes the body
        // type's scale), and the gear is drawn moved by place - anchor.
        public static Matrix4x4 Place(WornGearDef def, People.Look look, out bool rigid, out Vector3 anchor)
        {
            rigid = def.mount != "belt";
            anchor = Vector3.zero;
            Model[] body = { look.lower, look.upper, look.mid, look.coat, look.legArmour, look.overCoat };
            if (def.mount == "pack")
            {
                float back = 0f;
                for (int i = 0; i < body.Length; i++)
                {
                    back = Mathf.Max(back, Back(body[i]));
                }
                return Matrix4x4.Translate(new Vector3(0f, 0f, -(back + Gap)));
            }
            Vector2 waist = Vector2.zero;
            for (int i = 0; i < body.Length; i++)
            {
                Vector2 w = Waist(body[i]);
                waist = Vector2.Max(waist, w);
            }
            if (waist.x <= 0f)
            {
                waist = new Vector2(BeltHalfWidth, BeltHalfDepth);
            }
            if (def.mount == "hip")
            {
                anchor = new Vector3(HipClip, 0f, 0f);
                return Matrix4x4.Translate(new Vector3(waist.x + Gap, 0f, 0f));
            }
            float sx = Mathf.Clamp((waist.x + Gap) / BeltHalfWidth, 0.7f, 2.4f);
            float sz = Mathf.Clamp((waist.y + Gap) / BeltHalfDepth, 0.7f, 2.4f);
            return Matrix4x4.Scale(new Vector3(sx, 1f, sz));
        }

        // How far a body part reaches out at the belt's height: half width (x) and half depth (y).
        private static Vector2 Waist(Model m)
        {
            if (m == null)
            {
                return Vector2.zero;
            }
            Vector2 w;
            if (waists.TryGetValue(m.id, out w))
            {
                return w;
            }
            for (int i = 0; i < m.positions.Length; i++)
            {
                Vector3 p = m.positions[i];
                if (p.y >= 0.25f && p.y <= 0.35f)
                {
                    w.x = Mathf.Max(w.x, Mathf.Abs(p.x));
                    w.y = Mathf.Max(w.y, Mathf.Abs(p.z));
                }
            }
            waists[m.id] = w;
            return w;
        }

        // How far a body part reaches back (-z) behind the middle of the back, at a pack's height.
        private static float Back(Model m)
        {
            if (m == null)
            {
                return 0f;
            }
            float b;
            if (backs.TryGetValue(m.id, out b))
            {
                return b;
            }
            b = 0f;
            for (int i = 0; i < m.positions.Length; i++)
            {
                Vector3 p = m.positions[i];
                if (p.y >= 0.36f && p.y <= 0.76f && Mathf.Abs(p.x) <= 0.18f)
                {
                    b = Mathf.Max(b, -p.z);
                }
            }
            backs[m.id] = b;
            return b;
        }
    }
}
