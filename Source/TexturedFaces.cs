using UnityEngine;

namespace IsoRim
{
    // The one rule for a model's textured faces (an MTL material with map_Kd), for every writer of them: baked models
    // (ModelBaker.Write), moving models (DynamicModels.Build) and a person's merged figure (DynamicModels.BuildMerged, Api 75):
    //   - the face's material: its texture times Kd * ModelBaker.TextureLift, times the tint where the material takes it
    //     ("stuff" names, or every textured material under DynamicModels.TintTextured), times the second colour on "sheet"
    //     ones; a face with corners of their own colour (Model.OwnColour) takes Model.OwnBase and its colour from the corners.
    //     Glowing materials are unlit.
    //   - its plain copy (what shows with textured models off) sits ModelBaker.Inset of the outline width inside it (Under);
    //   - the textured face stands ModelBaker.FaceLift out along its normal (Over);
    //   - its outline is the colour it is drawn in, the texture taken at its mean (TextureOutline.Line).
    internal static class TexturedFaces
    {
        // Whether the model's textured faces are drawn as textured (the textured models setting, and the model has textures).
        internal static bool On(Model m)
        {
            return IsoRimMod.Settings.texturedModels && Has(m);
        }

        // Whether the model shows any textured face now (On, and some face's material has a texture: a material library may
        // hold others it never uses). GroundGear draws such laid clothes as moving models.
        internal static bool Shows(Model m)
        {
            if (!On(m))
            {
                return false;
            }
            for (int i = 0; i + 2 < m.material.Length; i += 3)
            {
                ModelMaterial mm = m.materials[m.material[i]];
                if (mm != null && mm.texture != null)
                {
                    return true;
                }
            }
            return false;
        }

        // Whether the model carries what textured faces need (materials, texture coordinates).
        internal static bool Has(Model m)
        {
            return m != null && m.materials != null && m.uvs != null && m.material != null;
        }

        // The material colour of a textured face of mm. tintAll: every textured material takes the tint (DynamicModels.TintTextured).
        // sheet: the thing's second colour ("sheet" materials; white where there is none).
        internal static Color Colour(ModelMaterial mm, bool own, Color tint, bool tintAll, Color sheet)
        {
            if (own)
            {
                return Model.OwnBase;
            }
            Color c = mm.color * ModelBaker.TextureLift;
            if (mm.tinted || tintAll)
            {
                c *= tint;
            }
            if (mm.sheet)
            {
                c *= sheet;
            }
            return c;
        }

        // The material a textured face of mm is drawn with, in colour c (Colour), or null where its texture is missing.
        // swatch: Wood, Stone, Metal or Fabric for "{stuff}" in the path. runtimeCurve: the light curve of a run-time texture
        // (R3DMaterials.LitTexture; 0 the model curve, 1 the wall curve).
        internal static Material MaterialFor(ModelMaterial mm, bool own, bool glow, Color c, string swatch, int runtimeCurve)
        {
            string ownKey = own ? Model.OwnKey : null;
            if (mm.runtimeTexture != null)
            {
                return R3DMaterials.LitTexture(mm.runtimeTexture, mm.runtimeName + ownKey, c, runtimeCurve);   // (Api 34, Api 62)
            }
            string path = mm.texture.Replace("{stuff}", swatch);
            return glow ? R3DTextures.MaterialFor(path, c, -1, ownKey) : R3DMaterials.LitTexture(path, c, 0, 0, ownKey);
        }

        // Whether mm's texture can be found without a swatch: a "{stuff}" path needs the thing's stuff (R3DTextures.ModelSwatch),
        // which a person's merged figure does not have (Api 75: such faces keep their plain colour there).
        internal static bool NeedsSwatch(ModelMaterial mm)
        {
            return mm.runtimeTexture == null && mm.texture != null && mm.texture.Contains("{stuff}");
        }

        // Where a textured face's plain copy sits: pulled in along the outline's push (hull) by ModelBaker.Inset.
        internal static Vector3 Under(Vector3 p, Vector3 hull)
        {
            return p - hull * ModelBaker.Inset;
        }

        // Where the textured face sits: ModelBaker.FaceLift out along normal (the face's, or a smooth corner normal).
        internal static Vector3 Over(Vector3 p, Vector3 normal)
        {
            return p + normal * ModelBaker.FaceLift;
        }
    }
}
