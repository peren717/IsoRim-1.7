using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>(Api version 62) Which figures a <see cref="FigureDrawer"/> is asked for.</summary>
    [Flags]
    public enum FigureKinds
    {
        People = 1,
        Animals = 2,
        Mechs = 4
    }

    /// <summary>
    /// (Api version 62) Asked where IsoRim would draw a pawn's 3D figure; true: the drawer drew it, IsoRim draws nothing of it.
    /// <see cref="Api.AddFigureDrawer"/>.
    /// </summary>
    public delegate bool FigureDrawer(FigureDrawContext ctx);

    /// <summary>(Api version 62) What IsoRim decided for a person's figure (<see cref="FigurePlacement.Situation"/>).</summary>
    public enum FigureSituation
    {
        Standing,
        Seated,
        LyingBack,
        InBed,
        OnCouch,
        Crawling,
        Crouched,
        Prone,
        Attached,
        InVat,
        Gibbet,
        Corpse
    }

    /// <summary>(Api version 62) Options for reading a placing: Stride (cells per step cycle; 0 or less: IsoRim's) and Face (a yaw to
    /// turn toward while standing, in place of the game's facing). The stride tracker steps once a frame: the first read of the frame
    /// decides.</summary>
    public struct FigurePlacementOptions
    {
        public float Stride;
        public float? Face;
    }

    /// <summary>
    /// (Api version 62) A person's figure as IsoRim places it now. Position is in true space (x, z map cells, y the height of the
    /// feet: the ground, a seat, a bed, a couch, a vat's floor, plus any lift), before stacked floors move it. Yaw in degrees (0 north,
    /// 90 east). BodyPlace and HeadPlace are IsoRim's own body and head placings in the figure's space (feet at the origin, +z the
    /// way it faces), with pose and size in them, as <see cref="FigureFrame"/> gives them.
    /// </summary>
    public struct FigurePlacement
    {
        public FigureSituation Situation;
        public Vector3 Position;
        public float Yaw;
        public float StridePhase;   // radians, grows with the ground covered
        public float Gait;          // 0 still .. 1 walking
        public float Size;          // 1 for adults
        public float Lift;          // carried, jumping
        public float Crouch;        // 0..1 (Api 45)
        public bool HeadOnlyInBed;
        public bool Dead;
        public bool Bones;
        public bool Carried;
        public bool Aiming;
        public float AimGame;       // yaw the game aims at
        public float AimDrawn;      // yaw toward where the target shows
        public float ChestLift;     // crawling or face down, degrees
        public Thing Seat;          // what it sits on
        public Thing Holder;        // bed, couch, vat or gibbet
        public Color Skin;          // with rot and dessication as IsoRim shows them
        public Matrix4x4 BodyPlace;
        public Matrix4x4 HeadPlace;
    }

    /// <summary>
    /// (Api version 62) Handed to a <see cref="FigureDrawer"/>. Valid only during the call (IsoRim reuses it).
    /// </summary>
    public sealed class FigureDrawContext
    {
        private Pawn pawn;
        private Vector3 drawLoc;
        private bool neverAim;
        private FigureKinds kind;
        private float flash;
        private bool live;
        private int placedState;   // 0 not read, 1 read, -1 none
        private FigurePlacement placed;
        internal bool ringNoted;

        internal void Set(Pawn p, Vector3 at, bool never, FigureKinds k)
        {
            pawn = p;
            drawLoc = at;
            neverAim = never;
            kind = k;
            flash = FigureDrawers.FlashOf(p);
            live = true;
            placedState = 0;
            ringNoted = false;
        }

        internal void Clear()
        {
            live = false;
            pawn = null;
        }

        /// <summary>The pawn (a corpse's inner pawn).</summary>
        public Pawn Pawn => pawn;

        /// <summary>The game's draw position, as the game's renderer got it.</summary>
        public Vector3 DrawLoc => drawLoc;

        /// <summary>The game's flag not to aim the weapon.</summary>
        public bool NeverAim => neverAim;

        /// <summary>People, Animals or Mechs.</summary>
        public FigureKinds Kind => kind;

        /// <summary>Drawn for another mod (<see cref="Api.DrawFigure"/>, <see cref="Api.FigureBounds"/>): draw it without what it
        /// holds or carries; what you submit is moved to the caller's placing (or measured).</summary>
        public bool ForOtherMod => FigureScope.Active;

        /// <summary>Measured only (<see cref="Api.FigureBounds"/>): submitted meshes are measured, not drawn.</summary>
        public bool Measuring => FigureScope.Measuring;

        /// <summary>The game's red damage flash now, 0..1 in sixteenths, as IsoRim flashes its figures (0 for another mod's draw).
        /// Pass it to <see cref="Api.SubmitFigureMesh"/>.</summary>
        public float Flash => flash;

        /// <summary>Reads IsoRim's placing of this person (people only; false for animals and mechs, or when IsoRim would not
        /// place it).</summary>
        public bool ReadPlacement(FigurePlacementOptions options, out FigurePlacement placement)
        {
            placement = default(FigurePlacement);
            if (!live || kind != FigureKinds.People)
            {
                return false;
            }
            if (!Placing.Read(pawn, drawLoc, neverAim, options.Stride, options.Face, out placement))
            {
                placedState = -1;
                return false;
            }
            placed = placement;
            placedState = 1;
            return true;
        }

        // The placing read last in this call, or IsoRim's own if none was read yet.
        internal bool Placed(out FigurePlacement p)
        {
            if (placedState == 0)
            {
                ReadPlacement(default(FigurePlacementOptions), out p);
            }
            p = placed;
            return placedState == 1 && Placing.Holds(pawn);
        }

        /// <summary>Notes where the selection ring goes: a box (min..max) in the figure's own space at position (true space) turned
        /// by yaw, standing on ground (true height). Without this a person's ring goes round IsoRim's own placing of the body.</summary>
        public void NoteSelectionBox(Vector3 position, float yaw, float ground, Vector3 min, Vector3 max)
        {
            if (!live)
            {
                return;
            }
            SelectionRings.NoteBox(pawn, position, yaw, ground, Matrix4x4.identity, min, max);
            ringNoted = true;
        }

        /// <summary>IsoRim's head box (centre and size) in its head placing, and its body box in its body placing: fit your head
        /// and body placings to these before <see cref="RaiseFigureHooks"/>. False for animals and mechs.</summary>
        public bool ReadFigureBoxes(out Vector3 headCentre, out Vector3 headSize, out Vector3 bodyCentre, out Vector3 bodySize)
        {
            headCentre = headSize = bodyCentre = bodySize = Vector3.zero;
            if (!Placed(out _) || Placing.look == null)
            {
                return false;
            }
            FigureBoxes.Box h = FigureBoxes.Head(Placing.look);
            FigureBoxes.Box b = FigureBoxes.Body(Placing.look);
            headCentre = h.centre;
            headSize = h.size;
            bodyCentre = b.centre;
            bodySize = b.size;
            return true;
        }

        /// <summary>Raises the other mods' figure hooks (<see cref="Api.AddFigureHook"/>) on your figure: their attachments are
        /// drawn in your head and body placings (figure space, as <see cref="FigurePlacement.HeadPlace"/>) at position (true space)
        /// and yaw, flashing with the figure. Returns the parts the hooks hide. People only; at most once per call.</summary>
        public FigureParts RaiseFigureHooks(Vector3 position, float yaw, Matrix4x4 headPlace, Matrix4x4 bodyPlace)
        {
            if (!live || FigureHooks.Count == 0 || !Placed(out FigurePlacement p) || Placing.look == null)
            {
                return FigureParts.None;
            }
            bool flashed = flash > 0f;
            if (flashed)
            {
                HitFlash.Begin(pawn);
            }
            try
            {
                return FigureHooks.Raise(pawn, Placing.look, position, yaw, headPlace, bodyPlace, p.Size, p.Lift, Placing.lying, Placing.crawling,
                    Placing.seated, p.Dead, Placing.bodyShown);
            }
            finally
            {
                if (flashed)
                {
                    HitFlash.End();
                }
            }
        }
    }

    /// <summary>(Api version 62) Flags for <see cref="Api.SubmitFigureMesh"/>.</summary>
    [Flags]
    public enum FigureMeshFlags
    {
        None = 0,
        /// <summary>An outline shell: left out while IsoRim's outlines are off; never flashes.</summary>
        Outline = 1,
        /// <summary>Also draw IsoRim's light pass of the material (own-cell night light; <see cref="Api.ConfigureFigureMaterial"/>).</summary>
        LightPass = 2,
        /// <summary>Not culled by the mesh's own box (cull the whole figure with <see cref="Api.FigureVisible"/>).</summary>
        NoCull = 4
    }

    /// <summary>(Api version 62) Kinds for <see cref="Api.ConfigureFigureMaterial"/>.</summary>
    public enum FigureMaterialKind
    {
        Solid,
        Textured,
        Outline,
        Unlit
    }

    /// <summary>(Api version 62) One of IsoRim's ready meshes of a model (<see cref="Api.ModelParts"/>). IsoRim's: never keep or
    /// destroy it.</summary>
    public struct FigureMeshPart
    {
        public Mesh Mesh;
        public Material Material;
        public bool Outline;
    }

    /// <summary>(Api version 62) A model made at run time (<see cref="Api.CreateModel"/>): a triangle soup, three corners a face.</summary>
    public sealed class ModelData
    {
        public Vector3[] Positions;     // cells, y up, +z the front
        public Vector3[] Normals;       // per corner; null: the faces' own
        public Vector3[] Hull;          // per corner outline offsets; null: IsoRim's
        public Color[] Colours;         // per corner; null: white
        public bool[] Tinted;           // per corner: times the colour it is drawn with; null: none
        public Vector2[] UVs;           // with Materials
        public int[] Material;          // per corner: index into Materials
        public ModelDataMaterial[] Materials;
    }

    /// <summary>(Api version 62) A material of <see cref="ModelData"/>: a texture made or loaded at run time.</summary>
    public struct ModelDataMaterial
    {
        public string Name;
        public Texture2D Texture;
        public bool Tinted;
    }

    /// <summary>(Api version 62) What <see cref="Api.ReadThingModel"/> reads.</summary>
    public enum ThingModelUse
    {
        Held,
        Worn,
        Ground
    }

    /// <summary>(Api version 62) Where worn gear sits (<see cref="ThingModelInfo.Mount"/>).</summary>
    public enum WornMount
    {
        None,
        Back,
        Belt,
        Hip
    }

    /// <summary>
    /// (Api version 62) A thing's model as IsoRim shows it (<see cref="Api.ReadThingModel"/>). Held: OnSprite lays the model into the
    /// weapon's picture space (picture cells: x right, y up, z out of it, the drawn size, middle at the origin) and HandScale is the
    /// size factor of a figure's hand (draw it with pictureSpace * Scale(HandScale) * OnSprite); BarrelEnd in model space. Worn: Mount
    /// and, for a hip, MountAnchor (the model point that goes on the hip). Ground: Scale and Yaw as IsoRim places it.
    /// </summary>
    public struct ThingModelInfo
    {
        public ModelHandle Model;
        public Color Tint;
        public string Swatch;
        public Matrix4x4 OnSprite;
        public float HandScale;
        public Vector3 BarrelEnd;
        public bool Thrown;
        public WornMount Mount;
        public Vector3 MountAnchor;
        public float Scale;
        public float Yaw;
        public string HowBound;
    }

    /// <summary>(Api version 62) A rule's answer for an item on the ground (<see cref="Api.AddGroundModelRule"/>): the model, its scale,
    /// its yaw (degrees) and colour (tinted faces). Swatch textures are the model's own.</summary>
    public struct GroundModel
    {
        public ModelHandle Model;
        public float Scale;
        public float Yaw;
        public Color Tint;
    }

    /// <summary>(Api version 62) What a ground rule is asked about: the thing, the model IsoRim would bake (null: none) and whether
    /// IsoRim would lay it as worn clothes.</summary>
    public struct GroundModelContext
    {
        public Thing Thing { get; internal set; }
        public ModelHandle IsoRimModel { get; internal set; }
        public bool WornLook { get; internal set; }
    }

    public static partial class Api
    {
        // ---- custom figure renderers (Version 62, Docs/Api.md part 43)

        /// <summary>The figure shader contract (Docs/Api.md part 43): raised whenever a promised name, binding or tag changes.</summary>
        public static int FigureShaderContract => 1;

        /// <summary>A drawer asked for each figure of these kinds IsoRim would draw, in the order added; the first true wins. Throws on
        /// null, a drawer added twice or no kind. A drawer that throws is removed (one warning) and IsoRim draws the figure.</summary>
        public static void AddFigureDrawer(FigureDrawer drawer, FigureKinds kinds) => FigureDrawers.Add(drawer, kinds);

        public static void RemoveFigureDrawer(FigureDrawer drawer) => FigureDrawers.Remove(drawer);

        /// <summary>IsoRim's placing of a person it would draw as a figure now; false if it would not (statues' subjects, cards, the
        /// 3D view off). Main thread, draw time.</summary>
        public static bool ReadFigurePlacement(Pawn pawn, Vector3 drawLoc, bool neverAim, FigurePlacementOptions options, out FigurePlacement placement)
            => Placing.Read(pawn, drawLoc, neverAim, options.Stride, options.Face, out placement);

        /// <summary>How far a person's aim is raised (degrees, down negative) from hands at handsHeight (cells over its feet) toward its
        /// target as drawn, between floors too; 0 without a target.</summary>
        public static float AimPitch(Pawn pawn, float handsHeight) => pawn != null ? FigureWeapons.PitchOf(pawn, handsHeight) : 0f;

        /// <summary>The height of a building's top surface (a table, a couch, cover) as IsoRim models it, cells.</summary>
        public static float SurfaceHeight(Thing building) => building != null ? Forms.HeightOf(building) : 0f;

        /// <summary>The flat direction from one world point to another as drawn on stacked floors.</summary>
        public static Vector3 DrawnDelta(Vector3 from, Vector3 to) => IsoRim.Floors.FlatDelta(from, to);

        /// <summary>The terrain is drawn as water by IsoRim.</summary>
        public static bool IsDrawnWater(TerrainDef terrain) => Ground.IsWater(terrain);

        /// <summary>A garment's colour as IsoRim shows it (made from stuff, dyed).</summary>
        public static Color ApparelColour(Apparel apparel) => apparel != null ? People.ApparelColour(apparel) : Color.white;

        /// <summary>A figure's size for a body size factor (life stages a race does not grow through).</summary>
        public static float FigureSizeFor(float bodySizeFactor) => People.SizeFor(bodySizeFactor);

        /// <summary>Draws a mesh as IsoRim draws a moving model's part: trueSpace places it in true space (Translate(position) *
        /// Rotate(yaw) * local); IsoRim squashes it, moves it for Api.DrawFigure or measures it, moves it onto its stacked floor with
        /// that floor's fade, culls it (unless NoCull), writes _IsoOwnerFloor and _IsoFlash into block (or uses its own), draws the light
        /// pass (LightPass). flash: 0..1 (FigureDrawContext.Flash). Never batched. Nothing while the 3D view is off.</summary>
        public static void SubmitFigureMesh(Mesh mesh, Material material, Matrix4x4 trueSpace, MaterialPropertyBlock block = null,
            FigureMeshFlags flags = FigureMeshFlags.LightPass, int submesh = 0, float flash = 0f)
        {
            if (mesh == null || material == null || !View3D.Ready)
            {
                return;
            }
            DynamicModels.SubmitOwn(mesh, material, Matrix4x4.Scale(new Vector3(1f, 1f / View3D.Squash, 1f)) * trueSpace, block, flags, submesh, flash);
        }

        /// <summary>Whether a box (local, in the space trueSpace places) can be on screen by IsoRim's cull (the shown camera, its
        /// stacked floor). True while drawn or measured for another mod, and while nothing is culled.</summary>
        public static bool FigureVisible(Bounds local, Matrix4x4 trueSpace)
        {
            if (FigureScope.Active || !View3D.Ready)
            {
                return true;
            }
            Matrix4x4 drawn = IsoRim.Floors.DrawnMatrix(Matrix4x4.Scale(new Vector3(1f, 1f / View3D.Squash, 1f)) * trueSpace);
            return Cull.LocalBoxVisible(local.min, local.max, drawn);
        }

        /// <summary>Sets up a material of yours as IsoRim's moving models': light curve, own-cell night light with IsoRim's light pass
        /// (lightPassShader replaces that pass's program, for a vertex stage of your own), an outline's push. False until IsoRim's
        /// materials exist. Throws on null.</summary>
        public static bool ConfigureFigureMaterial(Material material, FigureMaterialKind kind, Shader lightPassShader = null)
        {
            if (material == null)
            {
                throw new ArgumentNullException(nameof(material));
            }
            if (R3DMaterials.Solid == null)
            {
                return false;
            }
            switch (kind)
            {
                case FigureMaterialKind.Solid:
                case FigureMaterialKind.Textured:
                    material.SetFloat(LightModeId, R3DMaterials.LightModel);
                    IsoGlow.Use(material, IsoGlow.OwnCell, kind == FigureMaterialKind.Textured);
                    break;
                case FigureMaterialKind.Outline:
                    material.SetFloat(LightModeId, R3DMaterials.LightUnlit);
                    material.SetFloat(OutlinePushId, View3D.OutlinePush);
                    if (lightPassShader != null)
                    {
                        IsoGlow.Use(material, IsoGlow.OwnCell, false);
                    }
                    else
                    {
                        IsoGlow.Use(material, IsoGlow.None);
                    }
                    break;
                default:
                    material.SetFloat(LightModeId, R3DMaterials.LightUnlit);
                    IsoGlow.Use(material, IsoGlow.None);
                    break;
            }
            Material mark = lightPassShader != null ? IsoGlow.MarkFor(material) : null;
            if (mark != null)
            {
                mark.shader = lightPassShader;
                FadeFloors.RegisterStaticMaterial(mark);
            }
            // On a peeked (faded) floor IsoRim draws a copy of the material with _IsoFade and _IsoFadeFrom set (part 43 contract);
            // a shader that reads neither draws solid there.
            FadeFloors.RegisterStaticMaterial(material);
            return true;
        }

        private static readonly int LightModeId = Shader.PropertyToID("_LightMode");
        private static readonly int OutlinePushId = Shader.PropertyToID("_OutlinePush");

        /// <summary>A tint rounded as IsoRim's model parts round theirs (so a mesh of yours matches a model drawn in that tint).</summary>
        public static Color RoundTint(Color tint)
        {
            Color32 t = tint;
            t.r = (byte)(t.r & 0xF8);
            t.g = (byte)(t.g & 0xF8);
            t.b = (byte)(t.b & 0xF8);
            t.a = 255;
            return t;
        }

        /// <summary>The vertex colour IsoRim gives a model's solid (or outline) faces of this tint.</summary>
        public static Color32 ModelVertexColour(Color tint, bool outline)
            => R3DMaterials.Shaded(RoundTint(tint), outline ? ModelBaker.OutlineShade : 1f);

        /// <summary>The outline's width (cells) of IsoRim's models.</summary>
        public static float OutlineWidth => ObjLoader.OutlineWidthCells;

        /// <summary>How far IsoRim pushes moving outline shells away from the camera (cells).</summary>
        public static float OutlinePush => View3D.OutlinePush;

        /// <summary>The outline offsets of a triangle soup (positions and normals per corner) as IsoRim's models have them.</summary>
        public static Vector3[] BuildOutlineHull(Vector3[] positions, Vector3[] normals, float width)
        {
            if (positions == null || normals == null || positions.Length != normals.Length)
            {
                throw new ArgumentException("[IsoRim] Api.BuildOutlineHull: positions and normals of the same length are needed.");
            }
            return ObjLoader.HullOf(positions, normals, width);
        }

        /// <summary>The second texture coordinate IsoRim gives every corner of a moving mesh (clipped under the roof over it).</summary>
        public static Vector3 MovingMeshTag => RoofClip.MovingTag;

        /// <summary>Destroys a mesh a few frames later (it may still be queued to draw this frame).</summary>
        public static void RetireMesh(Mesh mesh) => DynamicModels.Retire(mesh);

        /// <summary>Screen-space shadows or ambient occlusion are drawn now.</summary>
        public static bool ScreenShadingOn
        {
            get
            {
                IsoRimSettings st = IsoRimMod.Settings;
                return SunShadowsSS.WillDraw || (st != null && st.ambientOcclusion && AmbientOcclusion.Available);
            }
        }

        /// <summary>The value of one of IsoRim's options by its field name (SetOption's counterpart); null if there is none.</summary>
        public static object GetOption(string name)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            System.Reflection.FieldInfo f = st == null || name.NullOrEmpty() ? null : HarmonyLib.AccessTools.Field(typeof(IsoRimSettings), name);
            return f != null ? f.GetValue(st) : null;
        }

        /// <summary>Makes a model from a triangle soup; usable everywhere a loaded one is. Checks and copies the arrays; throws on wrong
        /// ones. Main thread.</summary>
        public static ModelHandle CreateModel(ModelData data)
        {
            if (data == null || data.Positions == null || data.Positions.Length < 3 || data.Positions.Length % 3 != 0)
            {
                throw new ArgumentException("[IsoRim] Api.CreateModel: Positions must hold whole triangles.");
            }
            int n = data.Positions.Length;
            if ((data.Normals != null && data.Normals.Length != n) || (data.Hull != null && data.Hull.Length != n) || (data.Colours != null && data.Colours.Length != n)
                || (data.Tinted != null && data.Tinted.Length != n))
            {
                throw new ArgumentException("[IsoRim] Api.CreateModel: every per-corner array needs one entry per corner.");
            }
            bool textured = data.Materials != null && data.Materials.Length > 0;
            if (textured && (data.UVs == null || data.UVs.Length != n || data.Material == null || data.Material.Length != n))
            {
                throw new ArgumentException("[IsoRim] Api.CreateModel: with Materials, UVs and Material are needed per corner.");
            }
            if (textured)
            {
                for (int i = 0; i < data.Materials.Length; i++)
                {
                    if (data.Materials[i].Texture == null)
                    {
                        throw new ArgumentException("[IsoRim] Api.CreateModel: material " + i + " has no texture (leave Materials out for a plain coloured model).");
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    if (data.Material[i] < 0 || data.Material[i] >= data.Materials.Length)
                    {
                        throw new ArgumentException("[IsoRim] Api.CreateModel: Material index " + data.Material[i] + " at corner " + i + " is out of range.");
                    }
                }
            }
            Model m = new Model();
            m.positions = (Vector3[])data.Positions.Clone();
            if (data.Normals != null)
            {
                m.normals = (Vector3[])data.Normals.Clone();
            }
            else
            {
                m.normals = new Vector3[n];
                for (int i = 0; i + 2 < n; i += 3)
                {
                    Vector3 nf = Vector3.Cross(m.positions[i + 1] - m.positions[i], m.positions[i + 2] - m.positions[i]).normalized;
                    m.normals[i] = m.normals[i + 1] = m.normals[i + 2] = nf;
                }
            }
            m.colors = new Color[n];
            m.tinted = new bool[n];
            for (int i = 0; i < n; i++)
            {
                m.colors[i] = data.Colours != null ? data.Colours[i] : Color.white;
                m.tinted[i] = data.Tinted != null && data.Tinted[i];
            }
            if (textured)
            {
                m.uvs = (Vector2[])data.UVs.Clone();
                m.material = (int[])data.Material.Clone();
                m.materials = new ModelMaterial[data.Materials.Length];
                for (int i = 0; i < data.Materials.Length; i++)
                {
                    ModelDataMaterial src = data.Materials[i];
                    string name = src.Name.NullOrEmpty() ? "created" + i : src.Name;
                    m.materials[i] = new ModelMaterial
                    {
                        name = name,
                        texture = src.Texture.name,
                        color = Color.white,
                        tinted = src.Tinted,
                        runtimeTexture = src.Texture,
                        runtimeName = "api62:" + name + ":" + src.Texture.GetInstanceID(),
                        runtimeModelLight = true
                    };
                }
            }
            m.hull = data.Hull != null ? (Vector3[])data.Hull.Clone() : ObjLoader.HullOf(m.positions, m.normals);
            Vector3 lo = m.positions[0], hi = m.positions[0];
            for (int i = 1; i < n; i++)
            {
                lo = Vector3.Min(lo, m.positions[i]);
                hi = Vector3.Max(hi, m.positions[i]);
            }
            m.maxY = hi.y;
            m.minX = lo.x;
            m.maxX = hi.x;
            m.minZ = lo.z;
            m.maxZ = hi.z;
            m.halfDepth = Mathf.Max(Mathf.Abs(lo.z), Mathf.Abs(hi.z));
            m.id = ObjLoader.NewId();
            return HandleOf(m);
        }

        /// <summary>IsoRim's ready meshes of a model in this tint and swatch, added to into (cleared first). IsoRim's: ask each frame
        /// (cached), never keep or destroy them.</summary>
        public static void ModelParts(ModelHandle model, Color tint, string swatch, List<FigureMeshPart> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }
            into.Clear();
            if (model == null || model.model == null || R3DMaterials.Solid == null)
            {
                return;
            }
            List<DynamicModels.Part> parts = DynamicModels.PartsFor(model.model, tint, swatch);
            for (int i = 0; i < parts.Count; i++)
            {
                DynamicModels.Part p = parts[i];
                into.Add(new FigureMeshPart { Mesh = p.mesh, Material = p.material, Outline = p.outline });
            }
        }

        /// <summary>A texture's pixels at size x size (rows bottom up), read on the CPU or the graphics card; null for a sprite sheet
        /// or what cannot be read. Not every frame.</summary>
        public static Color32[] ReadSpritePixels(Texture texture, int size)
        {
            if (texture == null || size < 1 || size > 1024)
            {
                return null;
            }
            Color32[] px = SpritePixels.Sample(texture, Vector2.one, Vector2.zero, size);
            if (px == null && !SpritePixels.IsSheet(texture))
            {
                px = SpritePixels.GpuSample(texture, size);
            }
            return px;
        }

        /// <summary>What IsoRim shows for a thing held (a weapon), worn (packs, belts, hip gear) or lying on the ground; false: its
        /// sprite (or nothing of its own).</summary>
        public static bool ReadThingModel(Thing thing, ThingModelUse use, out ThingModelInfo info)
        {
            info = default(ThingModelInfo);
            if (thing == null || thing.def == null)
            {
                return false;
            }
            info.HowBound = ModelLibrary.HowBound(thing.def);
            switch (use)
            {
                case ThingModelUse.Held:
                {
                    info.Thrown = Projectiles.Thrown(thing.def);
                    Graphic g;
                    Material mat = WeaponModels.ArtOf(thing, out g);
                    WeaponModels.Hold hold = mat != null && WeaponModels.On ? FigureWeapons.HoldFor(thing, mat) : null;
                    Model model = hold != null ? WeaponModels.ModelOf(thing) : null;
                    if (model == null)
                    {
                        return false;
                    }
                    info.Model = HandleOf(model);
                    info.Tint = R3DTextures.ModelTint(thing);
                    info.Swatch = R3DTextures.ModelSwatch(thing);
                    info.OnSprite = Matrix4x4.Scale(new Vector3(g.drawSize.x, g.drawSize.y, 1f)) * hold.slab;
                    info.HandScale = FigureWeapons.Picture;
                    info.BarrelEnd = Projectiles.GunEnd(model);
                    info.Scale = 1f;
                    return true;
                }
                case ThingModelUse.Worn:
                {
                    WornGearDef def = WornGear.For(thing.def);
                    Color tint = Color.white;
                    Model model = def != null ? WornGear.ModelFor(def, thing as Apparel, out tint) : null;
                    if (model == null)
                    {
                        return false;
                    }
                    info.Model = HandleOf(model);
                    info.Tint = tint;
                    info.Swatch = R3DTextures.StuffSwatch(thing);
                    info.Mount = def.mount == "belt" ? WornMount.Belt : def.mount == "hip" ? WornMount.Hip : def.mount == "pack" ? WornMount.Back : WornMount.None;
                    info.MountAnchor = def.mount == "hip" ? new Vector3(WornGear.HipClipAnchor, 0f, 0f) : Vector3.zero;
                    info.Scale = 1f;
                    return true;
                }
                default:
                {
                    ThingForm form = Forms.For(thing);
                    if (form.form != Form3D.Model || form.model == null || !form.model.HasModel)
                    {
                        return false;
                    }
                    Model model = form.model.Pick(thing);
                    if (model == null)
                    {
                        return false;
                    }
                    info.Model = HandleOf(model);
                    info.Tint = form.model.def.spriteTint ? SpriteColors.MeanFor(thing) : thing.DrawColor;
                    info.Swatch = R3DTextures.StuffSwatch(thing);
                    info.Scale = form.model.def.scale;
                    info.Yaw = form.model.def.rotation;
                    return true;
                }
            }
        }

        /// <summary>Draws a thing (a weapon) as IsoRim's figures hold one: its model (weapons as 3D models) or its sprite made solid,
        /// stuff colour, outline, shadow. pictureTrueSpace places the weapon's picture (x right, y up, z out of it; one unit a cell,
        /// no scale in it) in true space; drawn at a figure's hand size times scale. False: held weapons in 3D off, nothing to draw.</summary>
        public static bool DrawHeldThing(Thing thing, Matrix4x4 pictureTrueSpace, float scale = 1f)
        {
            if (thing == null || !View3D.Ready)
            {
                return false;
            }
            bool flashWas = HitFlash.Pause();   // the game does not flash weapons
            try
            {
                return FigureWeapons.DrawAttached(thing, Matrix4x4.Scale(new Vector3(1f, 1f / View3D.Squash, 1f)) * pictureTrueSpace, scale);
            }
            finally
            {
                HitFlash.Resume(flashWas);
            }
        }

        /// <summary>Where this frame's drawn barrel end (tip) and the drawn weapon's middle are (true space): the pawn's next shots
        /// start there, as from IsoRim's own figures.</summary>
        public static void ReportMuzzle(Pawn pawn, Thing weapon, Vector3 tip, Vector3 centre)
        {
            if (pawn != null && weapon != null)
            {
                Projectiles.FigureTipAt(pawn, weapon, tip, centre);
            }
        }

        /// <summary>Draws what the carrier carries as IsoRim's figures do (between the hands, a meal on its table): at = the bottom
        /// of the carried thing in true space, yaw the way the carrier faces.</summary>
        public static void DrawCarried(Pawn carrier, Vector3 at, float yaw)
        {
            if (carrier != null && View3D.Ready)
            {
                Carried.Draw(carrier, at, yaw);
            }
        }

        /// <summary>A rule asked first whether a weapon def shows its model: true its bound model (any binding, its category too),
        /// false its sprite, null IsoRim's own rules. In the order added; a rule that throws is removed. Throws on null or twice.
        /// Dirty the map meshes yourself when an answer changes.</summary>
        public static void AddWeaponModelRule(Func<ThingDef, bool?> rule) => WeaponRules.Add(rule);

        public static void RemoveWeaponModelRule(Func<ThingDef, bool?> rule) => WeaponRules.Remove(rule);

        /// <summary>A rule asked while an item on the ground is printed into the map (not blueprints or damage marks): a model to bake
        /// in its place, or null for IsoRim's own. In the order added; a rule that throws or answers no model is removed.</summary>
        public static void AddGroundModelRule(Func<GroundModelContext, GroundModel?> rule) => GroundRules.Add(rule);

        public static void RemoveGroundModelRule(Func<GroundModelContext, GroundModel?> rule) => GroundRules.Remove(rule);

        internal static ModelHandle HandleFor(Model m) => HandleOf(m);
    }
}
