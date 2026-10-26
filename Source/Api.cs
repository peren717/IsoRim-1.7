using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Asked for each projectile card IsoRim draws itself (Projectiles.DrawAt: a projectile without a graphic class of its
    // own, not a lobbed shell). at and rotation are where and how IsoRim would draw the card (drawn space). Return true
    // and the full matrix to draw it with instead; false draws it as usual.
    public delegate bool ProjectileCardHook(Projectile p, Vector3 at, Quaternion rotation, out Matrix4x4 matrix);

    /// <summary>
    /// Called for each person figure IsoRim draws (Api version 8; <see cref="Api.AddFigureHook"/>), after the figure is
    /// placed, normally once a frame per figure. Read the frame and attach models with it; it is only valid during the
    /// call. From version 21 it is called before the figure itself is drawn, so <see cref="FigureFrame.Hide"/> takes effect
    /// at once.
    /// </summary>
    public delegate void FigureHook(FigureFrame frame);

    // The public surface for gameplay addons (stacked floors and the like). Addons should use only Api, IFloorLayout and
    // the delegate and enum types next to them: everything else in IsoRim is internal detail and may change between
    // versions.
    // Positions: "true space" is x and z in map cells, y the height in cells; "drawn space" is where IsoRim puts things
    // (drawn y = true height / HeightSquash).
    public static partial class Api
    {
        // Raised when members are added (2: view, projectile, skyfaller, drawing and option members of this file; 3: the
        // fog lid lock; 4: collapse debris, Debris.cs; 5: XrayDesignator; 6: model states and ModelStandsIn; 7: PeekUpperFloors;
        // 8: figure hooks (FigureFrame) and model drawing: LoadModel, DrawModel, DrawFigure, FigureBounds, the colour helpers;
        // 9: apparel model bindings (ApparelModelDef) and held shields; 10: DrawModel follows the peek fade of the floor it draws
        // on, apparel decals: AddApparelDecals, RemoveApparelDecals, RefreshApparelDecals, ApparelDecal; 11: wall tops, ModelDef.wallTop
        // (WallTopSpec, WallTopStrip, WallTopRule) and Form3D.Ground, WallTops.cs; 12: the upright drawing scope: BeginUpright,
        // EndUpright, Upright (UprightScope) and DrawMesh, Upright.cs; 13: attachments: AddAttachments, RemoveAttachments,
        // AttachmentOf, AttachedPlace, ReadHost, AnchorPoint (Attachment, AttachAnchor, AttachPose, AttachFacing,
        // HostTransform; Attach.cs) and ModelDef.backHeight; 14: animal wings, ModelDef.wingModel, wings, wingFold, wingDroop,
        // wingFlap, wingBeat, and AirborneFactor, Animals.cs; 15: the aged look, ModelDef.aged, Aged.cs; 16: ModelDef.graphicPath,
        // models bound by texture path; 17: RequestFullWalls (complete-wall requests for camera modes; PR #5 by Ryuu-64),
        // WallCutaway.cs; 18: one-cell doors drawn from their own thingDef ModelDef (model + doorLeafModel) and
        // ModelDef.doorLeafGather (leaves gather toward their jambs), Doors.DrawOne; 19: facing holds, AddFacingHold,
        // RemoveFacingHold, Animals.cs; 20: ModelDef.cutaway (CutawayRule) with low walls cut like other walls, ViewPerspective,
        // FieldOfView, FirstPersonPawn, ViewVersion, the flat scopes BeginSkipFlat, EndSkipFlat, SkipFlat, BeginFlatTransform,
        // EndFlatTransform, FlatTransform (FlatScope), SkipWhenModelled, DrawnMatrix, DrawnPosition, FloorShown, DrawGhost,
        // AddProjectileArc, RemoveProjectileArc, XrayPlacing, HasModel; Api20.cs; 21: face drawers, AddFaceDrawer, RemoveFaceDrawer
        // (FaceDrawer, FaceFrame), and
        // FigureFrame.Hide, FigureFrame.Hidden (FigureParts) with figure hooks raised before the figure is drawn; Api21.cs; 22:
        // AddFaceDrawer(drawer, evenWhenHidden) with FaceFrame.HiddenByGame (FaceHiddenBy), Api21.cs; 23: claimed for hand
        // attach points and work tools, not in this build yet; 24: model packs: ModelDef.immatureModels and immatureHide (a
        // plant's young stage, StateModels.Choose) and "sheet" materials tinted by the thing's second colour (a bed's sheets,
        // R3DTextures.SheetTint); 25: humanlike aliens: AlienModelDef alien packs (FigureMode Own, Human, Card), FigureModeOf,
        // AddAlienFigureRule, RemoveAlienFigureRule, FigureFrame.Anchor, FigureFrame.Mode, FigureParts.Addons,
        // ApparelModelDef.aliens and alienBodyModels, AlienModelDef.face (AlienFace) / hair (AlienHair) / faceFit / addon
        // hatLift, the "Keep as paper" setting, the dev-mode alien tuning window; Aliens.cs, AlienCards.cs, AlienTune.cs; 26: other mods'
        // angled wall pieces and gravship scene layers: AddWallCorners (CornerRule),
        // HideFlatLayer, RefreshWallShapes, FlyLayer, NotGeometryLayer; Api26.cs); 27: ModelDef.bobByState; 28: ModelDef.slots
        // (one entry per sleeping slot, its surface height: a stacked bunk bed's levels; Poses.SleeperHeight).
        // The card overlay (IsCard, DrawOnCard,
        // CardLight; CardOverlay.cs) came in before version 8 without a number of its own: check >= 8 for it.
        // 29: multi-cell supported doors (Building_SupportedDoor subclasses other than the game's
        // Building_MultiTileDoor) drawn from their models (model, doorTopModel, doorLeafModel), as one-cell ones; Doors in Dynamic.cs.
        // 30: glass parts per thing: ModelDef glassTint / glassOpacity / glassGlow, AddGlassColor / RemoveGlassColor; supported
        // doors draw their glass part (a window, a vac barrier's field across the doorway); Glass.cs.
        // 31: ModelDef.topThroughRoof (a turret's gun) and ModelDef.roofModel (a fixed part baked with the model) not clipped
        // by a roof over them (the fog and floor slabs still clip them).
        // 32: ModelDef.hoverHeight / hoverBob: animals and mechs that float over the ground while up and about (Hover.cs).
        // 33: beam parts (Beams.cs): beam_* model materials and line motes listed in beamMotes drawn as light (IsoRimAdd: unlit, additive, no
        // outline or shadow), ModelDef.beamFlicker / beamPulse / beamMotes; Api.Dev.BeamMoteDraws, BeamPartDraws, BeamPartBaked, BeamDrawn.
        // 34: wall shapes (Api34.cs): AddWallShapes (WallShapeRule, WallShape): another mod's convex wall pieces per cell, drawn as
        // IsoRim's 3D walls (optionally instead of the cell's block), roofs over them clipped to the shapes (WallShapeRoofs).
        public static int Version => 34;

        // ---- glass parts (Version 30)

        /// <summary>
        /// Adds a colour source for glass parts (ModelDef.glassModel; Api version 30): asked each frame for each thing whose
        /// glass part is drawn; return its colour (r, g, b = tint, a = opacity, 0 = not drawn) or null to leave it to the
        /// def's glassTint / glassOpacity (else IsoRim's own glass look). The first source that answers wins. Main thread;
        /// an exception is logged once and the source is left out. Adding the same delegate twice adds it once.
        /// </summary>
        public static void AddGlassColor(Func<Thing, Color?> source) => Glass.AddSource(source);

        /// <summary>Removes a colour source added with AddGlassColor (Api version 30).</summary>
        public static void RemoveGlassColor(Func<Thing, Color?> source) => Glass.RemoveSource(source);

        // ---- facing holds (Version 19)

        // A moving figure normally faces the way it goes. While any hold added here answers true for a pawn, its figure
        // (person, animal or mech model) turns to the game's facing (Thing.Rotation) instead, also while it moves, legs
        // together: a climber on a ladder faces the ladder. Asked each frame the figure is drawn: keep it cheap. Adding
        // the same delegate twice adds it once.
        public static void AddFacingHold(Func<Pawn, bool> holds) => Animals.AddFacingHold(holds);

        public static void RemoveFacingHold(Func<Pawn, bool> holds) => Animals.RemoveFacingHold(holds);

        // ---- animals in the air (Version 14)

        // How far the pawn is into the air, 0 (on the ground) to 1 (fully up): flying (Pawn_FlightTracker, eased by the
        // game's take-off and landing curves) or in mid-jump (carried by a PawnFlyer, eased in and out over the first and
        // last fifth of the jump). IsoRim opens animal wings and tucks legs by it; a mod drawing its own parts can follow
        // the same blend. 0 for a dead pawn and for null.
        public static float AirborneFactor(Pawn pawn)
        {
            return Animals.AirborneFactor(pawn);
        }

        // ---- the view

        // The 3D view is on and has drawn a frame (its camera maths below are valid).
        public static bool ViewReady => View3D.Ready;

        // The camera's tilt in degrees (90 looks straight down) and its turn about the vertical in degrees.
        public static float ViewPitch => View3D.Pitch;

        public static float ViewYaw => View3D.Yaw;

        // How the 3D view squashes heights: drawn y = true height / HeightSquash.
        public static float HeightSquash => View3D.Squash;

        // The height of a full wall in cells (the option, or the locked value).
        public static float WallHeight => View3D.WallHeight;

        // For an addon that needs the 3D view: it starts on and can't be turned off (by names the addon in the tooltip),
        // and the wall height is fixed at wallHeight cells (0 leaves the option to the player).
        public static void LockView(string by, float wallHeight)
        {
            View3D.LockedOn = true;
            View3D.LockedBy = by;
            View3D.LockedWallHeight = wallHeight;
        }

        // Who locked the view on (null when nobody did), and the locked wall height (0: the option decides).
        public static string ViewLockedBy => View3D.LockedOn ? View3D.LockedBy : null;

        public static float LockedWallHeight => View3D.LockedWallHeight;

        // For an addon whose current game needs the fog lid on or off (Version 3): the lid follows on, the player's own
        // option is kept unchanged (back after UnlockFogLid, and saved as it was), and the option is greyed out with tip
        // as its tooltip (who locked it, in the player's language). Not saved: lock again for each game that needs it.
        public static void LockFogLid(string tip, bool on)
        {
            View3D.FogLidLocked = true;
            View3D.LockedFogLid = on;
            View3D.FogLidLockTip = tip;
        }

        // The player's own fog lid option decides again.
        public static void UnlockFogLid()
        {
            View3D.FogLidLocked = false;
            View3D.FogLidLockTip = null;
        }

        // The fog lid lock's tooltip (null when not locked), and whether the fog lid is drawn now (the option, or the
        // locked value).
        public static string FogLidLockedBy => View3D.FogLidLocked ? View3D.FogLidLockTip ?? "" : null;

        public static bool FogLidOn => View3D.FogLid;

        // Where a true-space point shows on screen, pixels from the bottom left.
        public static Vector2 WorldToScreen(Vector3 p) => View3D.WorldToScreen(p);

        // The ground point under a screen pixel (y 0), and the point at a true height under it.
        public static Vector3 ScreenToGround(Vector2 px) => View3D.ScreenToGround(px);

        public static Vector3 ScreenToHeight(Vector2 px, float height) => View3D.ScreenToHeight(px, height);

        // The map position under a screen pixel, on the floor the pointer ray lands on (stacked floors), else the ground.
        public static Vector3 ScreenToMap(Vector2 px) => IsoRim.Floors.ScreenToMap(px);

        // Roofs shown or hidden (the player's roof switch; saved with IsoRim's settings).
        public static void ShowRoofs(bool on) => Roofs.Set(on);

        // The cut-away has lowered the wall in this cell (it is near the pointer).
        public static bool WallCutAway(Map map, IntVec3 c) => Cutaway.Hovered(map, c);

        // Sets one of IsoRim's options by the name it is saved under in IsoRim's settings file (for example "freeYaw",
        // "isoPitch", "wallHeight", "groundShadows", "lightingFix"); returns the old value, or null for an unknown name.
        // Not saved: the next save of the settings keeps it.
        public static object SetOption(string name, object value)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            FieldInfo f = st == null ? null : AccessTools.Field(typeof(IsoRimSettings), name);
            if (f == null)
            {
                return null;
            }
            object old = f.GetValue(st);
            f.SetValue(st, value);
            return old;
        }

        // For an addon's own designators (Version 5): while one of this type (or a subclass) is in hand, the mining x-ray
        // (the Mine tool's faint view of the floors next to the viewed one; IsoRim's mineXray option) shows the floor
        // below and/or the floor above, without the crosshair. Both false removes it.
        public static void XrayDesignator(System.Type designator, bool below, bool above) => MineXray.Register(designator, below, above);

        // For an addon's "peek" control (Version 7): while true, the floors above the viewed one are drawn faded instead of
        // hidden (FadeFloors); the pointer still works only on the viewed floor. Set it each frame from the addon's own key
        // (IsoRim has no key for it); false (the default) hides them as usual. Not saved.
        public static bool PeekUpperFloors
        {
            get => FadeFloors.Peek;
            set => FadeFloors.Peek = value;
        }

        // ---- projectiles (all only change how they are drawn)

        // How high a shooter fires from over its feet, in cells (where its bullets start).
        public static float MuzzleHeight(Thing launcher) => Projectiles.MuzzleHeight(launcher);

        // The true height (cells over the ground) IsoRim draws a projectile at when the game has it at point at of its
        // flat path.
        public static float ProjectileHeight(Projectile p, Vector3 at) => Projectiles.Height(p, at);

        // The height of what a projectile is aimed at, over its floor.
        public static float AimHeight(Projectile p) => Projectiles.AimHeight(p);

        // The muzzle of a gun: x and z on the map, y the height over the owner's feet.
        public static Vector3 Muzzle(Thing owner, ThingDef gun, Vector3 from, Vector3 dir) => Projectiles.Muzzle(owner, gun, from, dir);

        // For an addon that moves a shot's launch point elsewhere on the map (stacked floors: onto the target floor's
        // rows): where the shot really left from. Call before the projectile is first drawn.
        public static void SetLaunchPoint(Projectile p, Vector3 from) => Projectiles.SetLaunchPoint(p, from);

        // Where the drawn flight starts: x and z in the game's rows, y the height over the ground in cells.
        public static Vector3 DrawnStart(Projectile p) => Projectiles.DrawnStart(p);

        // Replaces the matrix of projectile cards (e.g. shots between floors pitched along their 3D flight). Null:
        // drawn as usual.
        public static ProjectileCardHook ProjectileCard;

        // ---- models (Version 6)

        // For an addon whose things look different in a state the game shows with sprites of its own (a mod's processor
        // drawing a "working" overlay): state names the thing's state now, or returns null for things it does not know.
        // A thing whose ModelDef has that state in byState is built with that state's model, and rebuilt when the state
        // changes (checked each frame the game draws the thing). Call once, at startup.
        public static void AddModelState(Func<Thing, string> state) => StateModels.AddState(state);

        // Whether a 3D model stands in for the thing's body now (the game's own sprite of it is not drawn), so an addon
        // can leave out flat overlays it would draw over that body.
        public static bool ModelStandsIn(Thing t)
        {
            if (t == null || t.def == null || !t.Spawned || !View3D.Ready || Forms.For(t.def).form != Form3D.Model)
            {
                return false;
            }
            return t.def.drawerType != DrawerType.RealtimeOnly || SectionLayer_IsoRimBodies.StandsIn(t);
        }

        // ---- skyfallers

        // Where IsoRim draws a skyfaller now: its ground point (ground-band x, z), the height of its bottom over the
        // ground, its landing floor's height and the rows from that floor down to the ground band. False when the 3D view
        // is off or its floor is hidden.
        public static bool SkyfallerPose(Skyfaller s, out Vector3 ground, out float height, out float floorHeight, out float rowShift)
        {
            ground = Vector3.zero;
            height = floorHeight = rowShift = 0f;
            Skyfallers.Pose p;
            if (s == null || !View3D.Ready || !Skyfallers.PoseOf(s, s.DrawPos, out p))
            {
                return false;
            }
            ground = p.ground;
            height = p.height;
            floorHeight = p.floorHeight;
            rowShift = p.rowShift;
            return true;
        }

        // ---- drawing

        // A new see-through, vertex-coloured material: depth-tested without depth write, both sides, unlit, at this
        // render queue.
        public static Material MakeBlendMaterial(string name, int queue) => R3DMaterials.Make(R3DMaterials.BlendDef, name, queue);

        // Draws a vertex-coloured mesh lit as IsoRim lights its models (sun shading per face, night and lamp light per
        // cell). Call from draw-time code; does nothing before the material is ready.
        public static void DrawLit(Mesh mesh, Matrix4x4 matrix)
        {
            Material mat = R3DMaterials.LitModel;
            if (mesh != null && mat != null)
            {
                IsoGlow.DrawLit(mesh, matrix, mat, 0);
            }
        }

        // ---- figures and models (Version 8)

        /// <summary>
        /// Registers a hook IsoRim calls for each person figure it draws (people and creepjoiners drawn as 3D figures,
        /// alive or dead, also when drawn by <see cref="DrawFigure"/>; not statues), after the figure is placed. With no
        /// hook registered this costs nothing per frame. Registering the same hook twice does nothing. Call once, at startup.
        /// </summary>
        public static void AddFigureHook(FigureHook hook) => FigureHooks.Add(hook);

        /// <summary>Removes a hook added with <see cref="AddFigureHook"/>.</summary>
        public static void RemoveFigureHook(FigureHook hook) => FigureHooks.Remove(hook);

        /// <summary>The 3D view is on and IsoRim draws models (its 3D models option): what the drawing calls below need.</summary>
        public static bool ModelsOn => View3D.Ready && IsoRimMod.Settings != null && IsoRimMod.Settings.models3D;

        /// <summary>
        /// Loads an OBJ model file (with its .mtl, if it names one) from a mod's folder, once; null (and a warning in the
        /// log) if it is missing or unreadable. path is relative to the mod's root folder, e.g. "Models/horn.obj". Faces
        /// whose material name starts with "stuff" take the colour given when drawing; the others keep their own colour.
        /// </summary>
        public static ModelHandle LoadModel(ModContentPack mod, string path)
        {
            if (mod == null || path.NullOrEmpty())
            {
                return null;
            }
            return HandleOf(ModelLibrary.LoadFile(System.IO.Path.GetFullPath(System.IO.Path.Combine(mod.RootDir, path))));
        }

        /// <summary>The same, relative to the folder of the mod whose assembly calls it.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static ModelHandle LoadModel(string path)
        {
            Assembly caller = Assembly.GetCallingAssembly();
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod.assemblies != null && mod.assemblies.loadedAssemblies.Contains(caller))
                {
                    return LoadModel(mod, path);
                }
            }
            Log.Warning("[IsoRim] Api.LoadModel: no mod found for assembly " + caller.GetName().Name + "; use LoadModel(ModContentPack, path)");
            return null;
        }

        private static readonly System.Collections.Generic.Dictionary<Model, ModelHandle> handles = new System.Collections.Generic.Dictionary<Model, ModelHandle>();

        private static ModelHandle HandleOf(Model m)
        {
            ModelHandle h;
            if (m == null)
            {
                return null;
            }
            if (!handles.TryGetValue(m, out h))
            {
                h = new ModelHandle(m);
                handles[m] = h;
            }
            return h;
        }

        /// <summary>
        /// Draws a model this frame as IsoRim draws its moving models (lit, outlined, shadowed, on its stacked floor). position:
        /// true space (x, z map cells, y the height over the ground in cells); yaw: degrees about the vertical (0 = as modelled,
        /// clockwise seen from above); scale: per axis, before the turn. colour tints the faces whose material starts with
        /// "stuff"; swatch (Wood, Stone, Metal, Fabric; null: Wood, see <see cref="StuffSwatch(ThingDef)"/>) picks the
        /// surface texture of its "{stuff}" textures. Call from draw-time code; does nothing while the 3D view is off.
        /// From version 10, a model drawn on the rows of a stacked floor that is shown faded (peeking, <see cref="PeekUpperFloors"/>)
        /// is faded as that floor's own things, also when drawn outside a thing's draw; on a hidden floor nothing shows.
        /// </summary>
        public static void DrawModel(ModelHandle model, Vector3 position, float yaw, Vector3 scale, Color colour, string swatch = null)
        {
            if (model != null && View3D.Ready)
            {
                DynamicModels.Draw(model.model, position, yaw, scale, colour, swatch);
            }
        }

        /// <summary>
        /// The same with a whole placing in the model's own space: local (cells, true heights) is applied first, then the
        /// turn by yaw and the move to position, as IsoRim places the parts of an animal or a held tool.
        /// </summary>
        public static void DrawModel(ModelHandle model, Vector3 position, float yaw, Matrix4x4 local, Color colour, string swatch = null)
        {
            if (model != null && View3D.Ready)
            {
                DynamicModels.DrawLocal(model.model, position, yaw, local, colour, swatch);
            }
        }

        /// <summary>The surface IsoRim gives models made of this stuff: "Wood", "Stone", "Metal" or "Fabric" (null or no
        /// stuff: "Wood"). Pass it as the swatch of the drawing calls.</summary>
        public static string StuffSwatch(ThingDef stuff) => R3DTextures.SwatchOf(stuff);

        /// <summary>The same for what a thing is made of.</summary>
        public static string StuffSwatch(Thing thing) => thing != null ? R3DTextures.StuffSwatch(thing) : R3DTextures.SwatchOf(null);

        /// <summary>The mean colour of a thing's own sprite (its texture times its colour, read once per texture), as IsoRim
        /// colours models that take their colour from the sprite.</summary>
        public static Color SpriteColour(Thing thing) => SpriteColors.MeanFor(thing);

        /// <summary>
        /// Draws a pawn's 3D figure this frame (a person, an animal or a mech IsoRim has a model for; alive or dead: pass a
        /// corpse's InnerPawn) somewhere else than where it is, as IsoRim poses it now (a corpse lying, a downed pawn on its
        /// back, ...), without what it holds or carries. The figure's own placing (its spot, its turn and the height it rests
        /// at) is taken out: local is applied to the figure in its own space (cells, true heights; the point under it at the
        /// origin, facing +z), then the turn by yaw and the move to position (true space), as DrawModel. shadow: its blob
        /// shadow on the ground under position; false leaves it out (on a shelf). Figure hooks run for it (their frames
        /// moved alike). False if IsoRim does not draw this pawn as a figure now (3D view, models or that kind of figure off,
        /// no model): draw its sprite then.
        /// </summary>
        public static bool DrawFigure(Pawn pawn, Vector3 position, float yaw, Matrix4x4 local, bool shadow)
        {
            return FigureScope.Draw(pawn, position, yaw, local, shadow);
        }

        /// <summary>
        /// The box a pawn's figure fills as <see cref="DrawFigure"/> would draw it with local = identity, position = origin,
        /// yaw = 0 (its own space: cells, true heights, the point under it at the origin), measured from its model parts
        /// (outlines left out); nothing is drawn. For fitting a figure into a slot. False if it is not drawn as a figure now.
        /// </summary>
        public static bool FigureBounds(Pawn pawn, out Vector3 min, out Vector3 max)
        {
            return FigureScope.Bounds(pawn, out min, out max);
        }

        // ---- apparel decals (Version 10)

        /// <summary>
        /// Registers a provider of decals (pictures printed on the garment, <see cref="ApparelDecal"/>) for worn apparel of
        /// this def (null: every apparel). IsoRim asks it for each worn item that shows as a body garment of its own on a 3D
        /// person figure (a coat or armour, a garment over the coat, a middle-layer or shirt shape; bound by an
        /// <see cref="ApparelModelDef"/> or IsoRim's own shape) whenever it works out how the wearer looks (about every 1.5
        /// seconds, and at once after <see cref="RefreshApparelDecals"/>). With no provider registered this costs nothing.
        /// Registering the same provider twice does nothing. Call once, at startup.
        /// </summary>
        public static void AddApparelDecals(ThingDef apparel, ApparelDecalProvider provider) => ApparelDecals.Add(apparel, provider);

        /// <summary>Removes a provider added with <see cref="AddApparelDecals"/>.</summary>
        public static void RemoveApparelDecals(ThingDef apparel, ApparelDecalProvider provider) => ApparelDecals.Remove(apparel, provider);

        /// <summary>Asks the providers again for this person the next time the figure is drawn (after a change the provider
        /// knows of, such as a new emblem).</summary>
        public static void RefreshApparelDecals(Pawn pawn) => People.Refresh(pawn);

        // ---- upright drawing (Version 12)

        /// <summary>
        /// Opens an upright scope: until <see cref="EndUpright"/>, the flat 2D meshes IsoRim draws for the game (a
        /// <c>GenDraw.DrawFillableBar</c>, <c>GenDraw.DrawMeshNowOrLater</c>, a <c>Graphic</c>'s draw, an overlay) and those drawn
        /// with <see cref="DrawMesh"/> stand up facing the camera, as one card, as IsoRim stands up status icons and
        /// bubbles. The card is hinged on the first mesh drawn in the scope and raised by height: cells (true heights) over
        /// the ground of the thing's floor (the peek fade treats it as the game's own flat drawing there); the other
        /// meshes keep their place round it as the game lays them out from above (east to the right, north up the card).
        /// The camera's turn and tilt are followed every frame. height is not divided by <see cref="HeightSquash"/>: IsoRim
        /// does that. Scopes nest (the inner one wins until it ends). Without the 3D view nothing changes (the scope is
        /// still counted, so Begin and End stay paired). A scope left open is closed after the thing whose drawing opened it,
        /// or at the next frame. Call it on the main thread, while drawing (a thing's DrawAt, PostDraw and the like).
        /// </summary>
        public static void BeginUpright(float height) => UprightScopes.Begin(height, false, Vector3.zero);

        /// <summary>The same, the card hinged on pivot (x and z in map cells; y is not used) instead of the first mesh.</summary>
        public static void BeginUpright(float height, Vector3 pivot) => UprightScopes.Begin(height, true, pivot);

        /// <summary>Closes the innermost upright scope. With none open it does nothing.</summary>
        public static void EndUpright() => UprightScopes.End();

        /// <summary>
        /// <see cref="BeginUpright(float)"/> for a <c>using</c> block: <c>using (Api.Upright(1.9f)) { ... }</c>. Disposing it
        /// closes its scope (and any left open inside it); disposing it twice, or after IsoRim closed it, does nothing.
        /// </summary>
        public static UprightScope Upright(float height) => UprightScopes.Begin(height, false, Vector3.zero);

        /// <summary>The same with the card hinged on pivot.</summary>
        public static UprightScope Upright(float height, Vector3 pivot) => UprightScopes.Begin(height, true, pivot);

        /// <summary>
        /// Draws a flat mesh as IsoRim draws the game's own 2D meshes (UnityEngine.Graphics.DrawMesh does not go through
        /// IsoRim): stood up in an upright scope, raised onto a 3D model while that model's thing is drawn, moved onto its
        /// stacked floor. Without the 3D view it is Graphics.DrawMesh.
        /// </summary>
        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer = 0) => DrawHook.DrawMesh(mesh, matrix, material, layer);

        // ---- attachments (Version 13)

        /// <summary>
        /// Registers a provider that says what a thing is attached to (<see cref="Attachment"/>; null: not attached). While
        /// the 3D view is on, IsoRim asks it for the pawns it draws or measures, often (several times a frame per pawn, also
        /// in game ticks): keep it cheap. An attached pawn is drawn on its host's anchor, following the host's drawn place,
        /// facing, walking bob, floor and peek fade; its picking and box selection, name label, held weapon and shot origin,
        /// bubbles and first person follow it, and its DrawPos (x and z) is the anchor's. People figures take every
        /// <see cref="AttachPose"/>; animal models stand or lie on the anchor; pawns drawn as cards stand (or sit, sunk a
        /// little) on it. Things that are not pawns are not drawn by it: draw them with <see cref="DrawModel(ModelHandle, Vector3, float, Vector3, Color, string)"/>
        /// at <see cref="AnchorPoint"/>. The first provider with an answer wins. With none registered this costs nothing.
        /// Registering the same provider twice does nothing. With the 3D view off nothing changes (the game's own drawing).
        /// Call once, at startup.
        /// </summary>
        public static void AddAttachments(Func<Thing, Attachment?> provider) => Attachments.Add(provider);

        /// <summary>Removes a provider added with <see cref="AddAttachments"/>.</summary>
        public static void RemoveAttachments(Func<Thing, Attachment?> provider) => Attachments.Remove(provider);

        /// <summary>What the providers say thing is attached to now (null: nothing, or its host is not spawned on its map).</summary>
        public static Attachment? AttachmentOf(Thing thing)
        {
            Attachment a;
            return Attachments.Of(thing, out a) ? a : (Attachment?)null;
        }

        /// <summary>
        /// Where IsoRim puts an attached thing now: point, the anchor in true space (x, z map cells; y over its floor), and
        /// yaw, the way it faces as drawn. False when it is not attached or the 3D view is off.
        /// </summary>
        public static bool AttachedPlace(Thing thing, out Vector3 point, out float yaw)
        {
            Attachments.Placed p;
            bool ok = Attachments.Place(thing, false, null, out p);
            point = p.at;
            yaw = p.yaw;
            return ok;
        }

        /// <summary>
        /// A thing's place as IsoRim draws it now (<see cref="IsoRim.HostTransform"/>): its base point, drawn facing, walking
        /// bob, model scale, floor and peek fade, and its anchors (an animal model as drawn; a person figure; a card or flat
        /// pawn estimated from its sprite; a building by its model). False while the 3D view is off or it is not spawned.
        /// </summary>
        public static bool ReadHost(Thing host, out HostTransform transform) => Attachments.Host(host, false, out transform);

        /// <summary>A named anchor of a host plus offset (its own space: x right, y up, z forward; cells), in true space.
        /// False while the 3D view is off or the host is not spawned.</summary>
        public static bool AnchorPoint(Thing host, AttachAnchor anchor, Vector3 offset, out Vector3 point)
        {
            HostTransform h;
            bool ok = Attachments.Host(host, false, out h);
            point = ok ? h.Anchor(anchor, offset) : Vector3.zero;
            return ok;
        }

        // ---- for addons' dev tests only: may change without notice

        public static partial class Dev
        {
            // Stacked floors are drawn this frame.
            public static bool FloorsDrawn => IsoRim.Floors.On;

            // The 3D view's switch, without its usual side effects (caller regenerates the map meshes).
            public static bool View3DActive
            {
                get { return View3D.Active; }
                set { View3D.Active = value; }
            }

            // Upper floors' sun shadows fill the gap between their parts (A/B switch).
            public static bool ShadowGapFill
            {
                get { return IsoRim.Floors.ShadowGapFill; }
                set { IsoRim.Floors.ShadowGapFill = value; }
            }

            // The floor above the cell exists (the cell's slab).
            public static bool SlabOver(Map map, IntVec3 c) => IsoRim.Floors.SlabOver(map, c);

            private static readonly AccessTools.FieldRef<IsoGlowMap, Color32[]> corners = AccessTools.FieldRefAccess<IsoGlowMap, Color32[]>("corners");

            // The light IsoRim's glow texture has at the south-west corner of a cell; null when out of range.
            public static Color32? GlowCorner(Map map, IntVec3 c)
            {
                IsoGlowMap g = IsoGlowMap.For(map);
                Color32[] all = g == null ? null : corners(g);
                int i = c.z * (g == null ? 0 : g.width) + c.x;
                return all != null && i >= 0 && i < all.Length ? all[i] : (Color32?)null;
            }

            // Version 19: the yaw a figure (person, animal, mech model) was last drawn facing, degrees clockwise from north,
            // and how many frames ago; null if it was never drawn as one.
            public static float? DrawnYaw(Pawn pawn, out int age) => Animals.DrawnYaw(pawn, out age);

            // (Api 32) Hovering, for tests: how far the pawn's figure is raised as last drawn (cells), and its easing
            // state (0 on the ground .. 1 afloat; -1 not drawn yet or not hovering).
            public static float HoverLift(Pawn pawn) => Hover.Current(pawn);
            public static float HoverState(Pawn pawn) => Hover.UpOf(pawn);

            // Version 19: aim pies drawn at a muzzle so far, the last one's wedge angle on screen (degrees clockwise from up the
            // screen) and its width in degrees; MuzzlePies off: the game's pie on the ground for every pawn.
            public static int MuzzlePieCount => AimPies.Drawn;

            public static float MuzzlePieAngle => AimPies.LastAngle;

            public static float MuzzlePieDegrees => AimPies.LastDegrees;

            public static bool MuzzlePies
            {
                get { return AimPies.AtMuzzle; }
                set { AimPies.AtMuzzle = value; }
            }
        }
    }
}
