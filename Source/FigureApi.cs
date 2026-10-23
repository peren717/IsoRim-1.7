using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>
    /// A model loaded through <see cref="Api.LoadModel(ModContentPack, string)"/> (Api version 8). Pass it to
    /// <see cref="Api.DrawModel(ModelHandle, Vector3, float, Vector3, Color, string)"/> or attach it to a figure
    /// (<see cref="FigureFrame.AttachToHead"/>). The same file loaded twice gives the same handle.
    /// </summary>
    public sealed class ModelHandle
    {
        internal readonly Model model;

        internal ModelHandle(Model model)
        {
            this.model = model;
            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Vector3[] p = model.positions;
            for (int i = 0; i < p.Length; i++)
            {
                lo = Vector3.Min(lo, p[i]);
                hi = Vector3.Max(hi, p[i]);
            }
            Min = lo;
            Max = hi;
        }

        /// <summary>The lowest corner of the box the model fills, in its own space (cells; x east, y up, z north, as
        /// IsoRim reads the file: the OBJ's z is flipped).</summary>
        public Vector3 Min { get; }

        /// <summary>The highest corner of that box.</summary>
        public Vector3 Max { get; }
    }

    /// <summary>
    /// One drawn person figure, handed to each <see cref="FigureHook"/> (Api version 8). Valid only during the call:
    /// IsoRim reuses the object for the next figure, so keep none of it.
    /// <para>Spaces: the head and body frames are the spaces the figure's own head and body parts are modelled in
    /// (cells; feet at the origin, y up, +z the way the figure faces, size 1), already posed (walking, lying, sitting,
    /// crawling) and scaled to the person's size. The head's widening (<see cref="HeadWidth"/>) and the body type's
    /// scale (<see cref="BodyScale"/>) are not in the frames: IsoRim applies them to its own parts, so apply them to
    /// yours where you want them.</para>
    /// </summary>
    public sealed class FigureFrame
    {
        internal FigureFrame()
        {
        }

        internal Pawn pawn;
        internal People.Look look;
        internal Matrix4x4 headRaw, bodyRaw;
        internal float yaw, size, lift;
        internal bool lying, crawling, seated, dead, bodyShown, live;
        private bool skipsRead;
        private ulong skips;
        internal FigureParts hidden;

        internal void Set(Pawn pawn, People.Look look, Matrix4x4 head, Matrix4x4 body, float yaw, float size, float lift,
            bool lying, bool crawling, bool seated, bool dead, bool bodyShown)
        {
            this.pawn = pawn;
            this.look = look;
            headRaw = head;
            bodyRaw = body;
            this.yaw = yaw;
            this.size = size;
            this.lift = lift;
            this.lying = lying;
            this.crawling = crawling;
            this.seated = seated;
            this.dead = dead;
            this.bodyShown = bodyShown;
            skipsRead = false;
            hidden = FigureParts.None;
            live = true;
        }

        internal void Clear()
        {
            live = false;
            pawn = null;
            look = null;
        }

        /// <summary>The person drawn (a corpse's inner pawn for a corpse).</summary>
        public Pawn Pawn => pawn;

        /// <summary>Maps the head frame to drawn space (drawn y = true height / <see cref="Api.HeightSquash"/>), where
        /// IsoRim draws the head parts. On an upper stacked floor IsoRim still moves the result onto its floor when
        /// drawing (attachments get that too).</summary>
        public Matrix4x4 Head => FigureScope.Active ? FigureScope.Extra * headRaw : headRaw;

        /// <summary>The same for the body frame.</summary>
        public Matrix4x4 Body => FigureScope.Active ? FigureScope.Extra * bodyRaw : bodyRaw;

        /// <summary>The figure's turn about the vertical in degrees (0 north, 90 east), as drawn now (smooth).</summary>
        public float Yaw => yaw;

        /// <summary>The way the figure faces now, from <see cref="Yaw"/>.</summary>
        public Rot4 Facing => Rot4.FromAngleFlat(yaw);

        /// <summary>The figure's size (1 for adults; children are smaller). Already in the frames.</summary>
        public float Size => size;

        /// <summary>How much wider (or narrower) the head is drawn than the head model, by head type (x only).</summary>
        public float HeadWidth => look.headWide;

        /// <summary>The body type's scale of the body and all worn on it.</summary>
        public Vector3 BodyScale => look.bodyScale;

        /// <summary>No head is drawn: a garment hides the whole head (the game's skip flag Head, such as a burka); IsoRim
        /// draws its covering in the head's place.</summary>
        public bool HeadHidden => look.headHidden;

        /// <summary>Headgear is drawn on the head now.</summary>
        public bool HeadgearShown => look.hat != null;

        /// <summary>The game draws the hair now (no headgear hides it).</summary>
        public bool HairShown => look.hairShown;

        /// <summary>The game leaves out the eyes (its skip flag Eyes: headgear over the eyes or the whole face).</summary>
        public bool FaceHidden => look.faceHidden;

        /// <summary>Headgear drawn now covers the whole head (the game's FullHead body part group: marine and power armour
        /// helmets, hoods that close round the head). A part sticking out of the head (ears, horns) would be under it.</summary>
        public bool FullHeadCovered
        {
            get
            {
                if (look.hat == null || pawn == null || pawn.apparel == null)
                {
                    return false;
                }
                List<Apparel> worn = pawn.apparel.WornApparel;
                for (int i = 0; i < worn.Count; i++)
                {
                    ApparelProperties a = worn[i].def.apparel;
                    if (a != null && a.bodyPartGroups != null && a.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>The game's render skip flags for the head as the person is now (each worn apparel's renderSkipFlags,
        /// the game's FullHead/UpperHead rules; none in a bed that hides headgear). Test with
        /// <c>(frame.SkipFlags &amp; RenderSkipFlagDefOf.Hair) != 0</c>. Worked out on first read.</summary>
        public ulong SkipFlags
        {
            get
            {
                if (!skipsRead && pawn != null)
                {
                    skipsRead = true;
                    skips = People.VanillaSkips(pawn);
                }
                return skips;
            }
        }

        /// <summary>Dried to bones: a skull stands in for head and face.</summary>
        public bool Skull => look.skull != null;

        /// <summary>The person has no head (the game's own health state). IsoRim still draws the figure's head.</summary>
        public bool Headless => pawn != null && pawn.health != null && pawn.health.hediffSet != null && !pawn.health.hediffSet.HasHead;

        /// <summary>Lying (dead, downed, in bed, crawling).</summary>
        public bool Lying => lying;

        /// <summary>Crawling (face down, head first).</summary>
        public bool Crawling => crawling;

        /// <summary>Sitting on a seat (the body squashed onto it).</summary>
        public bool Seated => seated;

        /// <summary>Dead (a corpse).</summary>
        public bool Dead => dead;

        /// <summary>Carried by another pawn.</summary>
        public bool Carried => pawn != null && pawn.CarriedBy != null;

        /// <summary>How high the figure is lifted off its ground in cells (carried, jumping, flying in); 0 otherwise.</summary>
        public float Lift => lift;

        /// <summary>The body is drawn (false in a bed that shows only the head).</summary>
        public bool BodyShown => bodyShown;

        /// <summary>The middle of the head in the head frame (the head model's box, widened by <see cref="HeadWidth"/>;
        /// the skull's box when dried to bones).</summary>
        public Vector3 HeadCentre
        {
            get
            {
                FigureBoxes.Box b = FigureBoxes.Head(look);
                return b.centre;
            }
        }

        /// <summary>The size of that box (x across, y up, z front to back).</summary>
        public Vector3 HeadSize => FigureBoxes.Head(look).size;

        /// <summary>The middle of the body (shirt and trousers parts with the body type's scale) in the body frame.</summary>
        public Vector3 BodyCentre => FigureBoxes.Body(look).centre;

        /// <summary>The size of that box.</summary>
        public Vector3 BodySize => FigureBoxes.Body(look).size;

        /// <summary>
        /// Where the game's head sprite lands on the head, in the head frame: maps a point of the game's south-facing
        /// head sprite (x right on the picture, y up on the picture, z into the picture; in the game's cells, origin at
        /// the head sprite's middle, the 1.5-cell head quad) to the head frame. The front of the sprite is the figure's
        /// front; IsoRim's own face decal (Facial Animation) uses the same placing. z 0 is the head's middle plane: add
        /// <c>-HeadSize.z / 2</c> in z to come out on the face.
        /// </summary>
        public Matrix4x4 HeadSprite => FigureBoxes.HeadSprite;

        /// <summary>
        /// The same for the game's south-facing adult body sprite of the person's body type (origin at the body sprite's
        /// middle, the pawn's draw position; the 1.5-cell body quad) and the body frame: the sprite's feet land on the
        /// figure's feet, the line where the game's head sprite meets the body (its chin) on the figure's neck, the sprite's
        /// width (arms included) across the figure's body. z 0 is the body's middle plane.
        /// </summary>
        public Matrix4x4 BodySprite => FigureBoxes.BodySprite(look, pawn != null && pawn.story != null ? pawn.story.bodyType : null);

        /// <summary>Draws a model with the figure, placed by local in the head frame (it follows the head's pose and
        /// facing; lit, outlined and shadowed as the figure's own parts). colour tints the model's faces whose material
        /// name starts with "stuff"; swatch (Wood, Stone, Metal, Fabric; null: Wood) picks the surface texture of its
        /// "{stuff}" textures. Call only from a figure hook, for this frame.</summary>
        public void AttachToHead(ModelHandle model, Matrix4x4 local, Color colour, string swatch = null)
        {
            if (live && model != null)
            {
                DynamicModels.DrawMatrix(model.model, headRaw * local, colour, swatch);
            }
        }

        /// <summary>The same in the body frame.</summary>
        public void AttachToBody(ModelHandle model, Matrix4x4 local, Color colour, string swatch = null)
        {
            if (live && model != null)
            {
                DynamicModels.DrawMatrix(model.model, bodyRaw * local, colour, swatch);
            }
        }

        /// <summary>Draws a mesh of your own with your own material, placed by local in the head frame (lit by the
        /// material's own shader; no outline). Call only from a figure hook, for this frame.</summary>
        public void AttachMeshToHead(Mesh mesh, Material material, Matrix4x4 local)
        {
            if (live)
            {
                FigureScope.DrawMesh(mesh, material, headRaw * local);
            }
        }

        /// <summary>The same in the body frame.</summary>
        public void AttachMeshToBody(Mesh mesh, Material material, Matrix4x4 local)
        {
            if (live)
            {
                FigureScope.DrawMesh(mesh, material, bodyRaw * local);
            }
        }

        /// <summary>
        /// Leaves out these parts of IsoRim's own figure this frame (Api version 21), for a hook that draws its own
        /// replacement (a head, hair, a body). Hooks run before the figure is drawn, so this frame already goes without
        /// them; ask again every frame (several hooks add up). Hiding the head or face also skips the face drawers.
        /// </summary>
        public void Hide(FigureParts parts)
        {
            if (live)
            {
                hidden |= parts;
            }
        }

        /// <summary>The parts hidden so far this frame by the hooks (<see cref="Hide"/>).</summary>
        public FigureParts Hidden => hidden;

        /// <summary>How the figure is drawn (Api version 25): <see cref="FigureMode.Human"/> for people and races drawn with
        /// IsoRim's human figure, <see cref="FigureMode.Own"/> for a race drawn with its alien pack's own models.</summary>
        public FigureMode Mode => look != null && look.alien != null ? look.alien.mode : FigureMode.Human;

        /// <summary>
        /// A named point of the figure in drawn space (Api version 25), the same for people and races: <c>head</c> (the head's
        /// pivot on the neck), <c>face</c> (the front of the face), <c>neck</c>, <c>back</c>, <c>waist</c>, <c>handLeft</c>,
        /// <c>handRight</c>, <c>tailRoot</c> (case does not matter; <c>hand_l</c>, <c>hand_r</c>, <c>tail_root</c> too).
        /// <c>head</c> and <c>face</c> move with the head (its pose, a race's head offset and fit), the others with the
        /// body. For people they are IsoRim's figure's points with the body type's scale; an alien pack can set its own.
        /// The matrix is the head or body frame moved to the point (not turned). False for an unknown name.
        /// </summary>
        public bool Anchor(string name, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            Vector3 p;
            bool onHead;
            if (!live || look == null || !AlienFigures.Anchor(look, name, out p, out onHead))
            {
                return false;
            }
            matrix = (onHead ? Head : Body) * Matrix4x4.Translate(p);
            return true;
        }
    }

    // Boxes of a figure's parts, worked out once per model (no allocation after the first).
    internal static class FigureBoxes
    {
        internal struct Box
        {
            public Vector3 centre;
            public Vector3 size;
        }

        private static readonly Dictionary<int, Box> byModel = new Dictionary<int, Box>();

        private static Box Of(Model m)
        {
            Box b;
            if (m == null)
            {
                return default(Box);
            }
            if (!byModel.TryGetValue(m.id, out b))
            {
                Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int i = 0; i < m.positions.Length; i++)
                {
                    lo = Vector3.Min(lo, m.positions[i]);
                    hi = Vector3.Max(hi, m.positions[i]);
                }
                b.centre = (lo + hi) * 0.5f;
                b.size = hi - lo;
                byModel[m.id] = b;
            }
            return b;
        }

        internal static Box Head(People.Look look)
        {
            if (look.skull != null)
            {
                return Of(look.skull);
            }
            if (look.ownHead != null)
            {
                return Of(look.ownHead);
            }
            Box b = Of(People.Part("person_head"));
            b.centre.x *= look.headWide;
            b.size.x *= look.headWide;
            return b;
        }

        internal static Box Body(People.Look look)
        {
            Box u = Of(look.upper);
            Box l = Of(look.lower);
            Vector3 lo = Vector3.Min(u.centre - u.size * 0.5f, l.centre - l.size * 0.5f);
            Vector3 hi = Vector3.Max(u.centre + u.size * 0.5f, l.centre + l.size * 0.5f);
            Vector3 s = look.upperBones ? Vector3.one : look.bodyScale;
            Box b;
            b.centre = Vector3.Scale((lo + hi) * 0.5f, s);
            b.size = Vector3.Scale(hi - lo, s);
            return b;
        }

        // The game's head quad is 1.5 cells; FaceDecal fits the 128-pixel head texture over SpriteCells of the head, with
        // the texture's middle at CentreHeight. Turned half round: the picture's right is the figure's -x.
        private const float GameQuad = 1.5f;
        internal static readonly Matrix4x4 HeadSprite = Matrix4x4.Translate(new Vector3(0f, FaceDecal.CentreHeight, 0f))
            * Matrix4x4.Scale(Vector3.one * (FaceDecal.SpriteCells / GameQuad))
            * Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.up));

        // The game's south body sprites (128 px over the 1.5-cell quad), measured in game (probe api8sprite, 2026-10-20):
        // the lowest opaque row (the feet) and half the opaque width (arms included), in cells from the quad's middle, by
        // IsoRim's build (People.BuildOf). The head sprite's opaque head is 0.287 cells each side of its middle, which the
        // game puts headOffset.y over the body's middle: its chin is where the sprite body meets the head.
        private const float SpriteHeadHalf = 0.287f;

        private static void SpriteOf(string build, out float feet, out float half)
        {
            switch (build)
            {
                case "female": feet = -0.568f; half = 0.281f; break;
                case "thin": feet = -0.510f; half = 0.152f; break;
                case "hulk": feet = -0.721f; half = 0.439f; break;
                case "fat": feet = -0.568f; half = 0.463f; break;
                default: feet = -0.510f; half = 0.299f; break;
            }
        }

        internal static Matrix4x4 BodySprite(People.Look look, BodyTypeDef type)
        {
            return BodySprite(look, type, Rot4.South);
        }

        // The same for the body sprite of each facing (WoundMarks): south on the figure's front as above, north on its back
        // (seen from behind: not turned), east on its right side and west on its left, the side sprites' width running
        // front to back at the body's own proportion (up), centred on the body's depth.
        internal static Matrix4x4 BodySprite(People.Look look, BodyTypeDef type, Rot4 facing)
        {
            float feet, half;
            SpriteOf(People.BuildOf(type), out feet, out half);
            float chin = (type != null ? type.headOffset.y : 0.34f) - SpriteHeadHalf;
            Box body = Body(look);
            float up = Poses.BodyTop / Mathf.Max(0.1f, chin - feet);
            float across = body.size.x * 0.5f / half;
            if (facing.IsHorizontal)
            {
                return Matrix4x4.Translate(new Vector3(0f, -feet * up, body.centre.z))
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(SideTurn(facing), Vector3.up))
                    * Matrix4x4.Scale(new Vector3(up, up, up));
            }
            return Matrix4x4.Translate(new Vector3(0f, -feet * up, 0f))
                * Matrix4x4.Scale(new Vector3(across, up, up))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(SideTurn(facing), Vector3.up));
        }

        // The head sprite of each facing on the head (WoundMarks), as HeadSprite for south; wide: the head type's widening
        // (People.Look.headWide) across the front and back.
        internal static Matrix4x4 HeadSpriteFacing(Rot4 facing, float wide, float depthCentre)
        {
            float k = FaceDecal.SpriteCells / GameQuad;
            return Matrix4x4.Translate(new Vector3(0f, FaceDecal.CentreHeight, facing.IsHorizontal ? depthCentre : 0f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(SideTurn(facing), Vector3.up))
                * Matrix4x4.Scale(new Vector3(facing.IsHorizontal ? k : k * wide, k, k));
        }

        // How a sprite of that facing is turned onto the figure (which faces +z): its right (x) and its depth (z, into the
        // picture) become south: -x, -z (the front, seen face on); north: x, z (the back); east: z, -x (the right side);
        // west: -z, x (the left side).
        internal static float SideTurn(Rot4 facing)
        {
            switch (facing.AsInt)
            {
                case 0: return 0f;      // north
                case 1: return -90f;    // east
                case 3: return 90f;     // west
                default: return 180f;   // south
            }
        }
    }

    // The figure hooks (Api.AddFigureHook): called by People.Draw for each person figure drawn. Copy on write, so
    // raising them allocates nothing; with none registered People.Draw only reads Count.
    internal static class FigureHooks
    {
        private static FigureHook[] hooks = new FigureHook[0];
        internal static int Count;
        private static readonly FigureFrame frame = new FigureFrame();

        internal static void Add(FigureHook hook)
        {
            if (hook == null || Array.IndexOf(hooks, hook) >= 0)
            {
                return;
            }
            FigureHook[] next = new FigureHook[hooks.Length + 1];
            hooks.CopyTo(next, 0);
            next[hooks.Length] = hook;
            hooks = next;
            Count = next.Length;
        }

        internal static void Remove(FigureHook hook)
        {
            int i = Array.IndexOf(hooks, hook);
            if (i < 0)
            {
                return;
            }
            FigureHook[] next = new FigureHook[hooks.Length - 1];
            Array.Copy(hooks, 0, next, 0, i);
            Array.Copy(hooks, i + 1, next, i, hooks.Length - i - 1);
            hooks = next;
            Count = next.Length;
        }

        // position, yaw, headPlace and bodyPlace as People.Draw hands them to DynamicModels.DrawParts. Raised before the
        // figure is drawn (Api 21); returns the parts the hooks hid (FigureFrame.Hide).
        internal static FigureParts Raise(Pawn pawn, People.Look look, Vector3 position, float yaw, Matrix4x4 headPlace, Matrix4x4 bodyPlace,
            float size, float lift, bool lying, bool crawling, bool seated, bool dead, bool bodyShown)
        {
            if (frame.live)
            {
                return FigureParts.None;   // a hook drawing a figure from inside a hook: not raised again
            }
            float s = View3D.Squash;
            Matrix4x4 placed = Matrix4x4.Translate(new Vector3(position.x, position.y / s, position.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up));
            frame.Set(pawn, look, placed * headPlace, placed * bodyPlace, yaw, size, lift, lying, crawling, seated, dead, bodyShown);
            FigureHook[] all = hooks;
            try
            {
                for (int i = 0; i < all.Length; i++)
                {
                    try
                    {
                        all[i](frame);
                    }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[IsoRim] A figure hook (" + (all[i].Method.DeclaringType != null ? all[i].Method.DeclaringType.FullName : "?") + ") failed: " + e,
                            93618000 ^ all[i].Method.GetHashCode());
                    }
                }
                return frame.hidden;
            }
            finally
            {
                frame.Clear();
            }
        }
    }

    // A figure drawn by Api.DrawFigure or measured by Api.FigureBounds: while Active, every model part the figure's
    // drawer submits (DynamicModels.Submit), its face decal and its ground shadow are moved from where the drawer put
    // the figure (its root, People/Animals/Mechs.Draw call Root) to the caller's placing; measuring draws nothing and
    // collects the parts' box instead. Outside these calls Active is false and nothing changes.
    internal static class FigureScope
    {
        internal static bool Active;
        private static bool measuring;
        private static bool shadow;
        private static Matrix4x4 target = Matrix4x4.identity;
        private static Matrix4x4 extra = Matrix4x4.identity;
        private static float targetYaw;
        private static float rootYaw;
        private static Vector3 min, max;
        private static bool measured;

        internal static Matrix4x4 Extra => extra;

        internal static bool Measuring => Active && measuring;

        private static Matrix4x4 Placed(Vector3 at, float yaw)
        {
            float s = View3D.Squash;
            return Matrix4x4.Translate(new Vector3(at.x, at.y / s, at.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up));
        }

        // The drawer's own placing of the whole figure (before its first part is drawn).
        internal static void Root(Vector3 at, float yaw)
        {
            if (!Active)
            {
                return;
            }
            rootYaw = yaw;
            extra = target * Placed(at, yaw).inverse;
        }

        internal static bool Draw(Pawn pawn, Vector3 position, float yaw, Matrix4x4 local, bool withShadow)
        {
            return Run(pawn, Placed(position, yaw) * local, yaw, false, withShadow);
        }

        internal static bool Bounds(Pawn pawn, out Vector3 lo, out Vector3 hi)
        {
            bool ok = Run(pawn, Matrix4x4.identity, 0f, true, false) && measured;
            lo = ok ? min : Vector3.zero;   // (the figure's own space, true heights: Extra took the root's squash out)
            hi = ok ? max : Vector3.zero;
            return ok;
        }

        private static bool Run(Pawn pawn, Matrix4x4 placing, float yaw, bool measure, bool withShadow)
        {
            if (Active || pawn == null || pawn.RaceProps == null || !View3D.Ready)
            {
                return false;
            }
            IsoRimSettings st = IsoRimMod.Settings;
            if (st == null || !st.models3D)
            {
                return false;
            }
            Vector3 source = pawn.Spawned ? pawn.DrawPos : (pawn.Corpse != null && pawn.Corpse.Spawned ? pawn.Corpse.DrawPos : placing.MultiplyPoint3x4(Vector3.zero));
            source.y = 0f;
            Active = true;
            measuring = measure;
            shadow = withShadow;
            target = placing;
            targetYaw = yaw;
            rootYaw = 0f;
            extra = target * Placed(source, 0f).inverse;   // a drawer that names no root: placed from the source point
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            measured = false;
            try
            {
                RaceProperties race = pawn.RaceProps;
                if (race.IsMechanoid)
                {
                    return st.mechs3D && Mechs.Draw(pawn, source);
                }
                if (!race.Humanlike)
                {
                    return st.animals3D && (Mechs.Draw(pawn, source) || Animals.Draw(pawn, source));
                }
                return st.pawns3D && People.Draw(pawn, source, true);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a figure for another mod failed: " + e, 93618001);
                return false;
            }
            finally
            {
                Active = false;
                measuring = false;
                extra = Matrix4x4.identity;
                target = Matrix4x4.identity;
            }
        }

        // From DynamicModels.Submit: moves the parts; true while measuring (then nothing is drawn).
        internal static bool Submit(List<DynamicModels.Part> parts, ref Matrix4x4 matrix)
        {
            matrix = extra * matrix;
            if (!measuring)
            {
                return false;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                DynamicModels.Part p = parts[i];
                if (p.outline || p.mesh == null)
                {
                    continue;
                }
                Bounds b = p.mesh.bounds;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((k & 1) == 0 ? -1f : 1f, (k & 2) == 0 ? -1f : 1f, (k & 4) == 0 ? -1f : 1f));
                    Vector3 w = matrix.MultiplyPoint3x4(corner);
                    min = Vector3.Min(min, w);
                    max = Vector3.Max(max, w);
                    measured = true;
                }
            }
            return true;
        }

        // From GroundShadows.Blob: false to leave the blob out, else it is moved under the placed figure.
        internal static bool Blob(ref Vector3 position, ref float yaw)
        {
            if (measuring || !shadow)
            {
                return false;
            }
            float s = View3D.Squash;
            Vector3 p = extra.MultiplyPoint3x4(new Vector3(position.x, position.y / s, position.z));
            position = new Vector3(p.x, p.y * s, p.z);
            yaw = yaw - rootYaw + targetYaw;
            return true;
        }

        // Other meshes drawn with the figure (FaceDecal): moved as its parts are.
        internal static Matrix4x4 Apply(Matrix4x4 matrix)
        {
            return Active ? extra * matrix : matrix;
        }

        // A figure attachment of the caller's own mesh (FigureFrame.AttachMesh*), placed as the figure's parts.
        internal static void DrawMesh(Mesh mesh, Material material, Matrix4x4 matrix)
        {
            if (mesh == null || material == null || Measuring)
            {
                return;
            }
            IsoGlow.DrawLit(mesh, Floors.DrawnMatrix(Apply(matrix)), material, 0);
        }
    }
}
