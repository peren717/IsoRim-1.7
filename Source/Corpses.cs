using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // How the game draws a pawn's body as it rots (PawnRenderer.CurRotDrawMode: a corpse's rot stage, or the
    // rot stage of a mutant drawn with corpse graphics, such as a shambler), for every pawn drawn as a 3D model
    // (people, animals, mechs, entities):
    //   - dried to bones (Dessicated), the game draws its skeleton sprite (Bodies/Dessicated, the Skull head):
    //     a person's figure becomes a 3D skeleton (People: Bones, the parts no garment covers; apparel and
    //     headgear stay, as the game draws them); every other pawn keeps the sprite then (ShownAsModel false),
    //     drawn the way any lying sprite is (flat on the ground, on the gibbet cage's floor, or in the carrier's hands);
    //   - rotting, the game lays its rotten colour (PawnRenderUtility.GetRottenColor) over the skin and over an
    //     animal's whole body; hair and the clothes worn take it themselves (HairColor, Apparel.DrawColor).
    public static class Corpses
    {
        // A rotting body's copy of each model (every face but glowing ones darkened as the game's rotten colour).
        private static readonly Dictionary<Model, Model> rotten = new Dictionary<Model, Model>();

        public static RotDrawMode RotOf(Pawn pawn)
        {
            PawnRenderer r = pawn != null && pawn.Drawer != null ? pawn.Drawer.renderer : null;
            return r != null ? r.CurRotDrawMode : RotDrawMode.Fresh;
        }

        // False while the game draws the pawn as bones and it has no 3D skeleton (all but people): it is not
        // drawn as its model then.
        public static bool ShownAsModel(Pawn pawn)
        {
            return !Bones(pawn) || AlienFigures.BonesAsModel(pawn);   // (people and Human-figure races, Api 25)
        }

        // True while the game draws the pawn as bones.
        public static bool Bones(Pawn pawn)
        {
            return RotOf(pawn) == RotDrawMode.Dessicated;
        }

        // How high a corpse lying on the map rests: on top of the shelf it is stored on (as the items there); 0 otherwise.
        public static float RestHeight(Pawn pawn)
        {
            Corpse corpse = pawn != null && pawn.Dead ? pawn.Corpse : null;
            return corpse != null && corpse.Spawned ? Forms.BaseUnder(corpse, true) : 0f;
        }

        // A person's skin colour as the game draws it now.
        public static Color Skin(Pawn pawn, Color skin)
        {
            return RotOf(pawn) == RotDrawMode.Rotting ? PawnRenderUtility.GetRottenColor(skin) : skin;
        }

        // A whole-body model (an animal, an entity) as the game colours the body now: the game multiplies the
        // body's texture by the rotten colour of its white; here the model's face colours stand for the texture.
        public static Model Body(Pawn pawn, Model m)
        {
            if (m == null || RotOf(pawn) != RotDrawMode.Rotting)
            {
                return m;
            }
            Model r;
            if (!rotten.TryGetValue(m, out r))
            {
                r = Rotten(m);
                rotten[m] = r;
            }
            return r;
        }

        private static Model Rotten(Model src)
        {
            try
            {
                Color f = PawnRenderUtility.GetRottenColor(Color.white);
                Model m = ObjLoader.Merge(new List<Model> { src });   // an own copy with its own id (meshes are cached by id)
                if (m.materials == null || m.material == null)
                {
                    return src;
                }
                for (int i = 0; i < m.colors.Length; i++)
                {
                    if (!m.materials[m.material[i]].glow)
                    {
                        Color c = m.colors[i];
                        m.colors[i] = new Color(c.r * f.r, c.g * f.g, c.b * f.b, c.a);
                    }
                }
                ModelMaterial[] mats = new ModelMaterial[m.materials.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    ModelMaterial o = m.materials[i];
                    mats[i] = new ModelMaterial
                    {
                        name = o.name, texture = o.texture, tinted = o.tinted, sheet = o.sheet, leaf = o.leaf, twig = o.twig, glow = o.glow, fire = o.fire,
                        color = o.glow ? o.color : new Color(o.color.r * f.r, o.color.g * f.g, o.color.b * f.b, o.color.a)
                    };
                }
                m.materials = mats;
                return m;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Making a rotting body's model failed: " + e, 93611470);
                return src;
            }
        }
    }
}
