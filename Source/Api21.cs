using System;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 21 (the API sprint's fourth batch, people; design api-sprint-design.md): face drawers (FaceDrawer, FaceFrame,
    // FaceDrawers; opt-in drawers asked even where the game hides the face, FaceHiddenBy) and hiding a figure's own parts from a figure hook (FigureFrame.Hide, FigureParts; People.Merged).
    public static partial class Api
    {
        // ---- faces (Version 21)

        /// <summary>
        /// Registers a face drawer: IsoRim asks it for each person figure whose head shows a face (people and creepjoiners
        /// drawn as 3D figures, alive or dead, also through <see cref="DrawFigure"/>; not when the game leaves out the eyes,
        /// no head is drawn, a skull or a ghoul's own head stands in, or a figure hook hid the head or face), once a frame,
        /// after the figure hooks and before the head is drawn. Draw the face with the <see cref="FaceFrame"/> and return
        /// true: IsoRim then leaves out its own face for that figure this frame (its eyes, a creepjoiner's face, Facial
        /// Animation's face). Return false to leave the face to IsoRim (or to the next drawer). The first drawer that
        /// returns true wins. To be asked also where the game leaves the face out, use <see cref="AddFaceDrawer(FaceDrawer, bool)"/>.
        /// With none registered nothing extra runs. Registering the same drawer twice does nothing. An
        /// exception is logged once and counts as false. Call once, at startup.
        /// </summary>
        public static void AddFaceDrawer(FaceDrawer drawer) => FaceDrawers.Add(drawer, false);

        /// <summary>
        /// As <see cref="AddFaceDrawer(FaceDrawer)"/>; with evenWhenHidden true the drawer is also asked when the game
        /// leaves the face out (eyes covered, no head drawn, a skull or a ghoul's own head): <see cref="FaceFrame.HiddenByGame"/>
        /// says why (<see cref="FaceHiddenBy.None"/> when the face shows). Still not asked when a figure hook hid the head
        /// or face. Drawers added without it are not asked then, as before. The first registration of a drawer counts.
        /// </summary>
        public static void AddFaceDrawer(FaceDrawer drawer, bool evenWhenHidden) => FaceDrawers.Add(drawer, evenWhenHidden);

        /// <summary>Removes a drawer added with <see cref="AddFaceDrawer(FaceDrawer)"/>.</summary>
        public static void RemoveFaceDrawer(FaceDrawer drawer) => FaceDrawers.Remove(drawer);
    }

    /// <summary>
    /// Draws a person's face on the 3D head (Api version 21; <see cref="Api.AddFaceDrawer(FaceDrawer)"/>). Return true when it drew
    /// the face (IsoRim then leaves its own out), false to leave the face to IsoRim. The frame is only valid during the call.
    /// </summary>
    public delegate bool FaceDrawer(FaceFrame face);

    /// <summary>
    /// Why the game leaves a person's face out (Api version 21; <see cref="FaceFrame.HiddenByGame"/>), for drawers added
    /// with <see cref="Api.AddFaceDrawer(FaceDrawer, bool)"/> evenWhenHidden.
    /// </summary>
    public enum FaceHiddenBy
    {
        /// <summary>The face shows.</summary>
        None = 0,
        /// <summary>Worn apparel covers the eyes (the game's skip flag Eyes, or an apparel model binding that hides the face).
        /// Hats and helmets drawn still cover the face by depth.</summary>
        Apparel = 1,
        /// <summary>No head is drawn: a garment hides the whole head (the game's skip flag Head, such as a burka) and its
        /// covering stands in the head's place.</summary>
        NoHead = 2,
        /// <summary>Dried to bones: a skull stands in for the head.</summary>
        Skull = 3,
        /// <summary>A head model of its own stands in (a ghoul's head).</summary>
        OwnHead = 4,
    }

    /// <summary>
    /// Parts of IsoRim's own person figure, for <see cref="FigureFrame.Hide"/> (Api version 21).
    /// </summary>
    [Flags]
    public enum FigureParts
    {
        None = 0,
        /// <summary>The head model (skin), with the face on it; a skull or a ghoul's own head likewise.</summary>
        Head = 1,
        /// <summary>The face on the head: IsoRim's eyes, a creepjoiner's own face and eyes, a shambler's scars, Facial
        /// Animation's face. Face drawers are not asked.</summary>
        Face = 2,
        /// <summary>The hair (and a hairstyle's separate part).</summary>
        Hair = 4,
        Beard = 8,
        /// <summary>Hats, helmets and hoods (and a helmet's visor). Hair shown under a hidden hat is not tucked in.</summary>
        Headgear = 16,
        /// <summary>An eye cover (blindfold).</summary>
        EyeWear = 32,
        /// <summary>A mouth cover (cloth mask).</summary>
        MouthWear = 64,
        /// <summary>The bare body: the shirt and trousers parts, the ghoul's and shambler's skin overlays, barbs and plating.</summary>
        Body = 128,
        /// <summary>Garments drawn over the body: coats, armour, vests, collars, leg armour, shoulder pads, their decals and flames.</summary>
        Clothes = 256,
        /// <summary>Packs, belts and waist tools.</summary>
        Gear = 512,
        /// <summary>Everything below the hips (as <see cref="AttachPose.SitNoLegs"/>).</summary>
        Legs = 1024,
        /// <summary>A shield held on the left forearm (an <see cref="ApparelModelDef"/> shield binding).</summary>
        Shield = 2048,
        /// <summary>A race's addon cards (Api version 25: the race's own render nodes, such as HAR body addons, drawn as cards on a
        /// race shown as a 3D figure), for a hook that draws them itself. Addon models an alien pack gives stay.</summary>
        Addons = 1 << 20,
    }

    /// <summary>
    /// One face to draw, handed to each <see cref="FaceDrawer"/> (Api version 21). Valid only during the call: IsoRim reuses
    /// the object for the next figure, so keep none of it.
    /// <para>Head space: the space IsoRim's head model is modelled in (cells; the figure's feet at the origin, y up, +z the
    /// way the figure faces, the head's middle about 0.98 up), widened by the head type. <see cref="Matrix"/> maps it to
    /// where the head is drawn now (walking, lying, sitting, crawling, in bed; any facing; children smaller; on its stacked
    /// floor; placed by <see cref="Api.DrawFigure"/>), ready for <c>Graphics.DrawMesh</c>.</para>
    /// <para>The face meshes cover the front of the head model; their texture coordinates project a south-facing head
    /// sprite texture (the game's head art and Facial Animation's layers alike: the whole square texture) straight from the
    /// front, at the size and height the player set for faces (IsoRim's face options, and per worn apparel). A hat,
    /// helmet or hair drawn over the head covers the face by depth as it covers the head.</para>
    /// </summary>
    public sealed class FaceFrame
    {
        internal FaceFrame()
        {
        }

        internal Pawn pawn;
        internal People.Look look;
        internal Model head;
        internal Matrix4x4 placed;   // head space to drawn space, before FigureScope and the stacked floor
        internal float yaw;
        internal bool lying, crawling, dead, live;
        internal FaceHiddenBy hiddenBy;
        private bool adjustRead;
        private FaceAdjust adjust;

        internal void Set(Pawn pawn, People.Look look, Model head, Matrix4x4 placed, float yaw, bool lying, bool crawling, bool dead, FaceHiddenBy hiddenBy)
        {
            this.hiddenBy = hiddenBy;
            this.pawn = pawn;
            this.look = look;
            this.head = head;
            this.placed = placed;
            this.yaw = yaw;
            this.lying = lying;
            this.crawling = crawling;
            this.dead = dead;
            adjustRead = false;
            live = true;
        }

        internal void Clear()
        {
            live = false;
            pawn = null;
            look = null;
            head = null;
            hiddenBy = FaceHiddenBy.None;
        }

        /// <summary>The person.</summary>
        public Pawn Pawn => pawn;

        /// <summary>Why the game leaves this face out (<see cref="FaceHiddenBy.None"/>: it shows). Only drawers added with
        /// evenWhenHidden (<see cref="Api.AddFaceDrawer(FaceDrawer, bool)"/>) see anything else. The face meshes are still
        /// those of IsoRim's plain head.</summary>
        public FaceHiddenBy HiddenByGame => hiddenBy;

        /// <summary>Maps head space to where the head is drawn now (see the class notes), for <c>Graphics.DrawMesh</c>.</summary>
        public Matrix4x4 Matrix => FaceDecal.Drawn(placed, -1);

        /// <summary>
        /// <see cref="Matrix"/> for face layer i (0, 1, 2...): each layer a little further out from the head's middle than
        /// the one under it, as IsoRim lays Facial Animation's layers, so stacked transparent layers do not fight.
        /// </summary>
        public Matrix4x4 Layer(int i) => FaceDecal.Drawn(placed, Mathf.Max(0, i));

        /// <summary>Draws a mesh with your material on face layer i (<see cref="Layer"/>) this frame. The face is lit by the
        /// material's own shader: IsoRim's night and lamp light pass still darkens what lies on the head if the material
        /// writes no depth and its render queue is under 3099 (Facial Animation's own materials do this).</summary>
        public void DrawLayer(Mesh mesh, Material material, int i)
        {
            if (live && mesh != null && material != null && !FigureScope.Measuring)
            {
                Graphics.DrawMesh(mesh, Layer(i), material, 0);
            }
        }

        private FaceAdjust Adjust
        {
            get
            {
                if (!adjustRead)
                {
                    adjustRead = true;
                    adjust = FaceDecal.AdjustFor(pawn);
                }
                return adjust;
            }
        }

        /// <summary>The face's size the player set for this person (1 = as IsoRim fits the head art; IsoRim's face options,
        /// or the first worn apparel with its own).</summary>
        public float FaceSize => Mathf.Clamp(Adjust.size, 0.5f, 1.5f);

        /// <summary>The face's shift up (cells) the player set for this person.</summary>
        public float FaceHeight => Adjust.height;

        /// <summary>The mouth's zoom the player set (enlarges the texture round its mouth point).</summary>
        public float MouthSize => Mathf.Clamp(Adjust.mouthSize, 0.5f, 3f);

        /// <summary>The mouth's own shift up (cells), on top of <see cref="FaceHeight"/>.</summary>
        public float MouthHeight => Adjust.mouthHeight;

        /// <summary>The front of the head with the texture projected at <see cref="FaceSize"/> and <see cref="FaceHeight"/>.
        /// Null if the head has no front (never for IsoRim's head).</summary>
        public Mesh FaceMesh => MeshFor(FaceSize, FaceHeight, 1f);

        /// <summary>The same for a mouth layer: also shifted by <see cref="MouthHeight"/> and zoomed by <see cref="MouthSize"/>.</summary>
        public Mesh MouthMesh => MeshFor(FaceSize, FaceHeight + MouthHeight, MouthSize);

        /// <summary>
        /// The front of the head with the texture projected at a placing of your own: size scales the texture (1: IsoRim's
        /// fit, the texture's square 1.47 cells across on the head), height shifts it up (cells), zoom enlarges it round
        /// the texture point (0.5, 0.376) from the bottom left (Facial Animation's mouth). Made once per placing and kept.
        /// </summary>
        public Mesh MeshFor(float size, float height, float zoom) => head != null ? FaceDecal.MeshFor(head, size, height, zoom) : null;

        /// <summary>IsoRim would draw Facial Animation's face here (that mod is active and the face option is on), not its
        /// own eyes.</summary>
        public bool FacialAnimationFace => look != null && look.faceDecal;

        /// <summary>IsoRim's face for this person is one of its own (a creepjoiner's special face), not the plain eyes.</summary>
        public bool OwnFace => look != null && look.ownFace != null;

        /// <summary>The figure's turn about the vertical in degrees (0 north, 90 east), as drawn now (smooth).</summary>
        public float Yaw => yaw;

        /// <summary>The way the figure faces now, from <see cref="Yaw"/>. The face is always on the head's front: the head
        /// turns, so draw the south-facing art for every facing.</summary>
        public Rot4 Facing => Rot4.FromAngleFlat(yaw);

        /// <summary>Lying (dead, downed, in bed, crawling).</summary>
        public bool Lying => lying;

        /// <summary>Crawling (face down, head first).</summary>
        public bool Crawling => crawling;

        /// <summary>Dead (a corpse).</summary>
        public bool Dead => dead;

        /// <summary>The figure's size (1 for adults; children are smaller). Already in <see cref="Matrix"/>.</summary>
        public float Size => look != null ? look.size : 1f;

        /// <summary>How much wider the head is drawn than the head model, by head type (x). Already in <see cref="Matrix"/>.</summary>
        public float HeadWidth => look != null ? look.headWide : 1f;

        /// <summary>Headgear is drawn on the head now.</summary>
        public bool HeadgearShown => look != null && look.hat != null;

        /// <summary>The hair is drawn now.</summary>
        public bool HairShown => look != null && look.hairShown;
    }

    // The face drawers (Api.AddFaceDrawer): asked by People.Draw for each figure whose head shows a face, and those added
    // with evenWhenHidden (counted in Always) also where the game leaves the face out. Copy on write, so asking allocates
    // nothing; with none registered People.Draw only reads Count.
    internal static class FaceDrawers
    {
        private static FaceDrawer[] drawers = new FaceDrawer[0];
        private static bool[] evenHidden = new bool[0];
        internal static int Count, Always;
        private static readonly FaceFrame frame = new FaceFrame();

        internal static void Add(FaceDrawer drawer, bool evenWhenHidden)
        {
            if (drawer == null || Array.IndexOf(drawers, drawer) >= 0)
            {
                return;
            }
            FaceDrawer[] next = new FaceDrawer[drawers.Length + 1];
            bool[] nextHidden = new bool[drawers.Length + 1];
            drawers.CopyTo(next, 0);
            evenHidden.CopyTo(nextHidden, 0);
            next[drawers.Length] = drawer;
            nextHidden[drawers.Length] = evenWhenHidden;
            Set(next, nextHidden);
        }

        internal static void Remove(FaceDrawer drawer)
        {
            int i = Array.IndexOf(drawers, drawer);
            if (i < 0)
            {
                return;
            }
            FaceDrawer[] next = new FaceDrawer[drawers.Length - 1];
            bool[] nextHidden = new bool[drawers.Length - 1];
            Array.Copy(drawers, 0, next, 0, i);
            Array.Copy(drawers, i + 1, next, i, drawers.Length - i - 1);
            Array.Copy(evenHidden, 0, nextHidden, 0, i);
            Array.Copy(evenHidden, i + 1, nextHidden, i, drawers.Length - i - 1);
            Set(next, nextHidden);
        }

        private static void Set(FaceDrawer[] next, bool[] nextHidden)
        {
            int always = 0;
            for (int i = 0; i < nextHidden.Length; i++)
            {
                always += nextHidden[i] ? 1 : 0;
            }
            evenHidden = nextHidden;
            drawers = next;
            Count = next.Length;
            Always = always;
        }

        // Why the game leaves this look's face out (the skull first: dried bones also set faceHidden).
        internal static FaceHiddenBy HiddenBy(People.Look look)
        {
            if (look.skull != null)
            {
                return FaceHiddenBy.Skull;
            }
            if (look.headHidden)
            {
                return FaceHiddenBy.NoHead;
            }
            if (look.ownHead != null && !(look.alien != null && look.alien.faceShown && look.alien.ownHead == look.ownHead))   // (an Own race's head with IsoRim's face: as a person head, Api 25)
            {
                return FaceHiddenBy.OwnHead;
            }
            return look.faceHidden ? FaceHiddenBy.Apparel : FaceHiddenBy.None;
        }

        // True when a drawer drew the face. headLocal: the head's placing in the figure (headPlace) times its widening.
        // hiddenBy not None: only the drawers added with evenWhenHidden are asked.
        internal static bool Ask(Pawn pawn, People.Look look, Model head, Vector3 position, float yaw, Matrix4x4 headLocal, bool lying, bool crawling, bool dead,
            FaceHiddenBy hiddenBy)
        {
            if (frame.live)
            {
                return false;   // a drawer drawing a figure from inside a drawer: IsoRim's own face
            }
            FaceDrawer[] all = drawers;
            bool[] even = evenHidden;
            frame.Set(pawn, look, head, FaceDecal.Placed(position, yaw, headLocal), yaw, lying, crawling, dead, hiddenBy);
            try
            {
                for (int i = 0; i < all.Length; i++)
                {
                    if (hiddenBy != FaceHiddenBy.None && !even[i])
                    {
                        continue;
                    }
                    try
                    {
                        if (all[i](frame))
                        {
                            return true;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[IsoRim] A face drawer (" + (all[i].Method.DeclaringType != null ? all[i].Method.DeclaringType.FullName : "?") + ") failed: " + e,
                            93621000 ^ all[i].Method.GetHashCode());
                    }
                }
                return false;
            }
            finally
            {
                frame.Clear();
            }
        }
    }
}
