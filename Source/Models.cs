using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace IsoRim
{
    // (Api 20) ModelDef.cutaway: whether the cut-away (pointer, rooms, IsoRim's cutHover / cutRooms options) lowers a wall.
    public enum CutawayRule
    {
        Auto,       // as IsoRim decides: built walls (full ones, low walls, walls from a model) yes; natural rock by the cutRock option
        Never,      // never lowered (a glazed half wall that must keep its window)
        Always      // always lowered, also a block IsoRim would not count as a wall (a tall linked fence)
    }

    // XML settings for how one thing is drawn in 3D. Any mod can add these:
    //
    //   <IsoRim.ModelDef>
    //     <defName>R3D_TorchLamp</defName>
    //     <thingDef>TorchLamp</thingDef>
    //     <model>Models/torch_lamp.obj</model>   (path inside the mod that contains this def)
    //   </IsoRim.ModelDef>
    //
    // Leave <model> out to keep the automatic shape and only change it with <height> or <flat>.
    public class ModelDef : Def
    {
        public string thingDef;
        public string model;
        public float scale = 1f;
        public Vector3 offset = Vector3.zero;
        public float rotation = 0f;         // extra turn around the vertical axis, degrees
        public List<float> rotationByFacing; // optional: one more turn (degrees) for each facing of the thing, north, east,
                                            // south, west, added to rotation (ModelEntry.Yaw): (0, 180, 0, 0) draws a thing
                                            // facing east as it looks facing west, where the game's east sprite is its west
                                            // one (VFEM2 forge bellows). Not used by style defs (ModelDef.style)
        public float surfaceHeight = -1f;   // where a pawn lying on it rests; -1 uses the top of the model
                                            // (also the height the game's per-frame drawing of it is raised to:
                                            // contents such as a reliquary's relic, a gibbet's corpse, a cocoon's dryad)
        public float seatHeight = -1f;      // a seat (the game's isSittable): top of the seat, where a sitting pawn's base
                                            // goes (Poses); -1 uses surfaceHeight
        public float beddingTop = -1f;      // a bed: top of the bedding (pillow, blanket), where a sleeper drawn as a flat
                                            // sprite lies (Poses); -1 uses surfaceHeight
        // (Api 28) One entry per place a pawn lies (or, later, sits) on the thing, in the game's slot order: a bed's sleeping
        // slots as the game numbers them (RestUtility.CurrentBed's slot; a mod's stacked bunk bed numbers its levels so).
        // <li><height>1.25</height></li>: that slot's surface height, like surfaceHeight (model units, before the footprint
        // fit); a missing list, a slot past its end or a height of -1 keeps surfaceHeight. More per-slot settings (offset,
        // facing, pose) go into the same entries later (ModelSlot).
        public List<ModelSlot> slots;
        public Vector3 contentOffset = Vector3.zero; // those contents moved this far (x, z; model space, facing north)
                                            // from where the game draws them, to where the model holds them
        public float height = -1f;          // height in cells of the automatic shape; -1 lets the mod decide
        public bool flat = false;           // true keeps the thing as a flat sprite

        public List<string> models;         // several models: each thing picks one and keeps it
        public List<string> stackModels;    // items: one model per stack sprite (a, b, c), picked by stack count as
                                            // the game's Graphic_StackCount: 1 / more / a full stack
        public bool overArt = false;        // items: drawn with this model even while items use the game's art
                                            // (setting "3D raw materials")
        public List<AppearanceModel> byAppearance;  // a different model for things made of stuff of this look (the
                                            // game's Graphic_Appearances: Planks, Bricks, Smooth ...); others use model
        public List<StateModel> byState;    // a different model for things in a state an addon names (Api.AddModelState):
                                            // <li><state>full</state><model>Models/x.obj</model></li>; others use model
        public List<StyleModel> byStyle;    // a different model for things of this style (Ideology's ThingStyleDef):
                                            // <li><style>Morbid_AltarSmall</style><model>Models/x.obj</model></li>;
                                            // wins over byAppearance; things of no listed style use model
        public string style;                // with thingDef: a def of its own for things of that thing in this ThingStyleDef
                                            // only, from any mod (a pack's style models for a thing whose own entry another
                                            // mod gives). Its own builtFor, rotation, offset and scale; several models are
                                            // picked per thing as the game's Graphic_Random. Fitted into the thing's own
                                            // entry as its byStyle (ModelLibrary.AddStyles), so everything draws it as
                                            // the thing's model; a one-cell door draws its model as the frame and its
                                            // doorLeafModel as the two leaves (Doors.DrawOne). Never an entry by itself.
                                            // Its surfaceHeight, if set, is where things rest on it (Forms.SurfaceOf: a shelf's items)
        public string thingCategory;       // apply to every thing in this category instead of one thingDef
        public string graphicPath;         // (Api 16) every thing whose graphicData.texPath is this, unless it has its own
                                           // thingDef entry: the game's generated per-animal meat shares 5 texPaths.
                                           // Case-insensitive, \ = /, a trailing / ignored. Wins over thingCategory
        public bool randomRotation = false; // give each thing its own turn
        public float scaleJitter = 0f;      // vary each thing's size by up to this fraction
        public bool growthScale = false;    // scale with how grown a plant is
        public List<string> immatureModels; // (Api 24) a plant's young stage: drawn instead while it is not yet harvestable,
                                            // as the game draws its immatureGraphic (Plant.Graphic: !HarvestableNow, i.e. growth
                                            // up to harvestMinGrowth; a plant that yields nothing: growth up to
                                            // harvestMinGrowth too). One per variant of models (same order; fewer: they repeat).
                                            // growthScale still shrinks it. The game redraws a sown crop's cell as it grows
                                            // (every 10 % of growth), so the switch shows at the next step, as vanilla's
        public List<string> immatureHide;   // (Api 24) materials left out of the young stage (case-insensitive): the produce
                                            // (cobs, berries, flowers). With no immatureModels the young stage is the mature
                                            // model (each variant) without them; with them, they are left out of those too
        public bool keepLeaves = false;     // a plant's leaf* parts stay while it is leafless (cold, poison): for plants
                                            // whose vanilla look has no leafless sprite (Polux); thinning near the pointer still applies
        public bool layFlat = false;        // the model is built as held; on the ground it is tipped onto its side
        public float scatter = 0f;          // move each thing up to this far (in cells) off the middle of its cell
        public bool spriteTint = false;     // colour "stuff" parts from the thing's own sprite instead of its material
        public bool paintedHull = false;    // a paintable building made of no stuff: its "stuff" parts are the paint,
                                            // white while unpainted and the paint colour once painted, on the Metal swatch
        public string fallback;             // "SmallPlants": every plant that is not a tree and has no other entry

        public bool wallMounted = false;    // fixed to the wall the thing faces (the game's wall attachments always are, see WallMount)
        public float mountHeight = -1f;     // how far up that wall, as a fraction of its height; -1: WallMount's rule (top over a colonist's head)
        public bool alongRun = false;       // turned a quarter turn where its row runs north-south (sandbags)
        public bool fullVariant = false;    // the second model is used once the thing holds something (graves)
        public string emptyModel;           // used instead while the game draws the thing as empty (CompEmptyStateGraphic: an opened mech capsule)
        public bool groundShadow = true;    // soft shading on the ground under it
        public Vector3 flameAt = new Vector3(0f, -1f, 0f);   // foot of the live flame (Effects), model space; y < 0: from the model's
                                            // own flame* parts (torches, braziers), none if it has none
        public Vector2 flameSize;           // with flameAt: the live flame's height and width (cells, model space)
        public float sinkHole = 0f;         // > 0: a hole sunk into the ground (pits), this the radius (cells, model space) of
                                            // its opening: the model may reach below the ground, the ground cells the opening
                                            // touches are cut out of the map mesh and the ground round it hides what lies under
                                            // it (SinkHoles.cs). The model must cover the cut cells outside the opening (an
                                            // apron); a random turn is kept to quarter turns
        public float aged = 0f;             // > 0 (up to 1, how strongly): drawn old and rusty, as the game's ancient ruins
                                            // (dulled colours, rust flakes on the textures, dead lights; stuff parts dulled,
                                            // old grey metal for a thing of no stuff). Made once at load (Aged.cs)
        public float outline = 1f;          // how bold the dark outline is; 0 for none
        public float outlineMinSize = 0f;   // separate pieces smaller than this (cells, largest side), and flat sheets,
                                            // get no outline (a chess table's pieces and board), so small detail does not turn black
        public bool linked = false;         // the first model stands in the cell; the second is added toward each neighbour it joins
                                            // (with wallFit on a building the game draws as a wall: a wall drawn from a model,
                                            // WallBoxes.ModelWall: cut down, built over and hung with fixtures as a wall; what
                                            // stands above wallFit is its crown, left out under a floor built over it)

        // Matching by name: every building or item whose defName has one of these as a whole word or
        // run of words (ignoring case, plural allowed; see ModelLibrary.NameMatches) and that has no entry of its own or of its category. Used for the wrecks and junk
        // that other content scatters over maps, whose exact names vary.
        public List<string> match;
        public Vector2 builtFor = Vector2.zero; // the footprint (x, z) in cells the model was built for; it is stretched to the thing's real size
        public string topModel;             // turrets: the gun that turns, drawn every frame
        public string roofModel;            // (Api 31) a fixed part built in the same space as model and baked with it (placed,
                                            // turned and fitted as the model), which a roof over it does not clip: it rises
                                            // through the roof (a ship gun's pedestal, a mast); the fog and a floor's slab still
                                            // clip it (RoofClip.ThroughRoof)
        public float topHeight = 0.5f;      // height of the turret's pivot, in cells
        public bool topThroughRoof = false; // (Api 31) the gun (topModel) is not clipped by a roof over it (RoofClip): it rises
                                            // through the roof; the fog and a floor's slab still clip it, and the base stays
                                            // clipped under the roof (a tall ship gun on a frame, its gun over the roof)
        public bool ignoreRotation = false; // drawn as built (facing north) whatever the thing's rotation, as the game draws a
                                            // rotatable thing whose sprite does not turn (the mech inferno turret's base; its
                                            // gun still turns). Square footprints only (ModelEntry.Yaw)
        public float wallFit = 0f;          // > 0: the model was built for walls this tall (cells); its height follows the walls
                                            // drawn now (View3D.WallHeight / wallFit, in Fit), so things that close a wall line
                                            // (gravship thrusters) stay as tall as the walls; 0 = fixed height
        public WallTopSpec wallTop;         // (Api 11) a one-cell linked wall drawn on IsoRim's own wall path (a block, as vanilla
                                            // walls: no model needed) with its own height, side texture and a top shaped by a
                                            // rule (merlons, ridge, ragged, rubble). See WallTops.cs and Docs/Api.md part 5
        public CutawayRule cutaway = CutawayRule.Auto;  // (Api 20) whether the cut-away lowers this wall (WallBoxes.CanBeCut);
                                            // only things IsoRim draws as walls (blocks, walls from a model) are ever lowered
        // Doors wider than one cell (ornate door): model is the fixed frame over the doorway, doorLeafModel one
        // leaf (spanning x 0..1 from the doorway centre; the other leaf is the same turned half round),
        // doorTopModel an optional fixed piece on top (the arch). Drawn by Doors.DrawWide.
        // One-cell doors (Api 18; a style def's door since Api 6): model is the frame, doorLeafModel one leaf spanning
        // x 0..0.5 (the other the same turned half round), built for walls 1.2 high. A thingDef entry with both is drawn
        // by Doors.DrawOne when the door has no style entry of its own.
        public string doorLeafModel;
        public string doorTopModel;
        // doorUpperLeafModel (wide doors, optional): a second leaf pair drawn like doorLeafModel but sliding as the game's
        // upperMoverGraphic does, 0.35 * clamp01(OpenPct * 2.5) door widths, so it is fully back at 40 % open (security
        // door: the armour plates retract first, then the slabs part).
        public string doorUpperLeafModel;
        // doorLeafRise (wide doors, optional): > 0 makes doorLeafModel one piece across the whole doorway (a portcullis,
        // built centred and shut) that rises this far (cells, at the walls the models are built for) as the door opens,
        // OpenPct of the way, instead of two leaves sliding apart.
        public float doorLeafRise = 0f;
        // doorLeafGather (Api 18, one-cell doors with their own models, optional): > 0 makes the two leaves gather toward
        // their jambs as the door opens, like curtains drawn aside, instead of sliding into the walls: each leaf (x 0..0.5
        // from the doorway centre) is squeezed along x about its jamb (x 0.5) to 1 - doorLeafGather * OpenPct of its
        // width (the animal flap: 0.78). Clamped to 0..1. Doors.DrawOne.
        public float doorLeafGather = 0f;
        // crownTop (wide doors, optional): > 0 gives the frame a crown over the walls: its top stands this many wall
        // heights high at any wall height (frame, leaves and doorLeafRise scaled to it, in height only). Under a floor
        // built right over the door (a slab) there is no room for the crown: everything above the wall top is left out,
        // not squeezed, and a flat lintel closes the frame flush with the walls (Doors.DrawWide).
        public float crownTop = 0f;
        // One-cell supported doors (vac barrier, ancient blast door; Doors.DrawSupported) use model (frame),
        // doorTopModel (lintel) and doorLeafModel (optional, two sliding leaves) the same way, plus
        // hackTerminalModel: drawn while the door's CompHackable is not hacked (the blast door's terminal).
        public string hackTerminalModel;
        // Used instead of model once the thing's CompHackable is hacked, as the game prints its hackedGraphicData (the
        // relay stabilizer's dark core). Picked by StateModels.Choose; hacking redraws the map mesh (CompHackable.Hack).
        public string hackedModel;
        // hackedGlowOff: once hacked, the model's own glow faces go dark instead (no second model file): the ancient grav
        // reactor's panel, as the game's hackedGraphicData (_Off sprite). A hackedModel, if given, wins.
        public bool hackedGlowOff;
        public List<Vector3> emitters;      // nozzles (x, height, z; model space, facing north) the game's thrown smoke or dust comes out of (Emitters.cs)
        public float emitterRise = 0.4f;    // how fast those puffs rise, in cells a second
        public string topModelLoaded;       // the gun while the game shows its loaded gun sprite (rocketswarm launcher: fully loaded)
        // A part that turns about the thing's upright axis (the lightball's ball), drawn every frame
        // (Spinners.cs). Built in the same space as model (same ground origin, facing north); it turns
        // while the thing is powered and wants to be on, and stands still otherwise.
        public string spinModel;
        public float spinSpeed = 90f;       // degrees a second at normal game speed
        public string spinMote;             // the game's flat mote for that part (Mote_LightBall), left out in 3D
        public string litMaterial;          // a material of spinModel that lights up while the part turns (the lightball's
                                            // lenses, party_lens): unlit, in litColors, one colour per separate piece in turn
        public List<Color> litColors;
        // A part that bobs up and down over the model (the grav engine's orb, the Cerebrex core's brain, the mech relay's
        // crystal), drawn every frame (Bobbers.cs). Built in the same space as model (same ground origin, facing north) at
        // its lowest point; it rises bobHeight cells and sinks back once every bobPeriod game ticks (the game's own sine),
        // standing still while the game is paused. The game's own flat sprite of that part is left out (Bobbers prefixes).
        public string bobModel;
        // (Api 27) A different bob part for things in a state an addon names (Api.AddModelState), the same list as byState:
        // <li><state>cooldown</state><model>Models/x_orb_cool.obj</model></li>; other states (and none) use bobModel. Asked
        // each frame the part is drawn, so it follows the state at once (no rebuild).
        public List<StateModel> bobByState;
        public float bobHeight = 0.3f;
        public int bobPeriod = 500;
        // Parts that beat (the fleshmass heart, Pulses.cs): each built round its own pivot, drawn every frame at that
        // pivot and scaled about it by the game's own beat (Building_FleshmassHeart.GetBeatScale: a 15-tick sine after
        // each beat, each part up to its maxScale, delayed by its delay in ticks). The game's flat parts are left out.
        public List<PulsePart> pulseParts;
        // A glow part (the obelisks' runes, ActivityGlow.cs): built in the same space as model, drawn every frame unlit
        // in a colour that follows the thing's activity (CompActivity.ActivityLevel, as the game's Graphic_ActivityMask
        // lights its mask): hidden below 0.1, then from 0.6 x the part's own colour up to the def's colorTwo (else
        // glowColor), brighter and pulsing faster as it rises, flickering and throwing glow flecks near the top.
        // glowMaterials names the part's glowing faces (all of it if left out).
        public string activityGlowModel;
        // See-through parts (Glass.cs): a glass cover or window built in the same space as model, drawn
        // every frame over the body and over what is inside it, in one shared pale glass colour, darkening
        // with the scene at night. The game's flat glass, top and window sprites over the contents are
        // left out in 3D (mech gestators, growth vat, biosculpter pod, wastepack atomizer).
        public string glassModel;
        // (Api 30) The glass part's look for this def: glassTint (r, g, b; unset = IsoRim's pale glass), glassOpacity (0 to 1;
        // -1 = IsoRim's 0.4) and glassGlow (drawn unlit, keeping its brightness at night: a force field). A colour source
        // (Api.AddGlassColor) can set tint and opacity per thing. Things with none of these keep IsoRim's one glass look.
        // Supported doors draw their glass part too (Doors in Dynamic.cs): a window, or a vac barrier's field, shown while
        // the game shows its field sprite and tinted by that sprite's colour unless set here or by a colour source.
        public Color glassTint = new Color(0f, 0f, 0f, 0f);
        public float glassOpacity = -1f;
        public bool glassGlow = false;
        // (Api 33) Beam parts (Beams.cs): faces whose material name starts with "beam_" are drawn as light (unlit, additive, no
        // outline or shadow) in their MTL colour. beamFlicker (0 to 1) makes them flicker by up to that share, beamPulse (Hz)
        // pulse slowly. beamMotes: line motes (ThingDef names, e.g. another mod's laser) drawn as beams in 3D (opt-in; other
        // line motes, the game's own beams too, keep the game's drawing). Any ModelDef may list them.
        public float beamFlicker = 0f;
        public float beamPulse = 0f;
        public List<string> beamMotes;
        public float contentTop = -1f;      // roof of the chamber under the glass: what stands inside (a forming
                                            // mech, a vat embryo or child) is fitted between surfaceHeight and this
        public bool heldPawnUpright = false; // the person held inside floats upright (growth vat) instead of lying
        public float barHeight = -1f;       // height of the game's progress bar over it (mech gestator console); -1 surfaceHeight
        // Harvesters (WorkParts.cs). cableAnchors: the connectors (x, height, z; model space, facing north) the
        // game's cables to holding platforms (CompCableConnection) run up to, taken in turn. workPanel: the two
        // ends (x, height, z) of a strip on the model where lights run while the building works (stopped while
        // paused, dark while idle), in workPanelColor, workPanelWidth wide.
        public List<Vector3> cableAnchors;
        public List<Vector3> workPanel;
        public Color workPanelColor = new Color(1f, 0.5f, 0.2f);
        public float workPanelWidth = 0.08f;
        // Holding platform (HoldingChains.cs): the eye rings (x, height, z; model space, facing north) the game's
        // restraint chains are fixed to. Each chain runs from the ring nearest the game's own end point on
        // the platform up to the held entity (or across it, ring to ring, for an entity with no points of its own).
        public List<Vector3> chainAnchors;
        // Outfit stands (Stands.cs): dressed as a 3D colonist of the stand's body type (People's clothing parts,
        // figure space in cells, as a colonist), lifted dressRise cells onto the stand (scale sizes the stand model
        // to that figure); headModel is the stand's head,
        // drawn over the colonist's head and only while no worn apparel hides the head (as the game).
        // Clothing with no 3D part is left out (as on a colonist); a weapon with no 3D form is the game's sprite,
        // stood upright facing the camera at apparelHeight, apparelScale times the game's size.
        public string headModel;
        public float dressRise = 0f;
        public float apparelHeight = 0.85f;
        public float apparelScale = 1f;
        // Things made of several OBJ files that stay together: parts are merged into model when loaded (an
        // ancient stockpile hatch's frame, cover and readout; the stockpile exit's frame and ladder).
        public List<string> parts;
        // A map portal (MapPortal: the ancient stockpile hatch) drawn with openModel (plus openParts merged into
        // it) while it can be entered, as the game draws its open sprite; rebuilt when the game redraws it.
        public string openModel;
        public List<string> openParts;
        // The game's extra per-frame sprites (CompDrawAdditionalGraphics: the stockpile exit's ladder) are left
        // out while the model stands in, because the model has them as parts.
        public bool hideExtraGraphics = false;

        // Bookcases: the books they hold are drawn on these shelves (Bookcases.cs).
        public List<float> bookShelves;     // height of each shelf top, in cells, lowest first
        public List<float> bookSlots;       // x of each book's middle along a shelf, from the left as seen from the front
        public float bookRow = 0f;          // z of the books' middle (the model faces north, +z)
        public List<int> bookShelfSlots;    // optional: how many of bookSlots each shelf has, lowest first (bookSlots then
                                            // lists every shelf's places in turn); left out, every shelf has all of bookSlots

        // Animals. thingDef is the animal's race; model is its body, facing north.
        public bool animal = false;
        public string legModel;             // one leg, standing on the ground with its hip at the top
        public List<Vector3> legs;          // where each hip is on the body
        public float legLength = 0f;        // height of the hip above the foot
        public float legSwing = 26f;        // how far the legs swing when walking, degrees
        public float legSplay = 0f;         // how far the legs lean outward (lizards, insects), degrees
        // A bent leg (a spider's) built from its hip at the origin, reaching out along +x and down to its foot,
        // instead of standing under its hip: drawn as it is on the right, turned half round on the left (not
        // mirrored), and swung about the hip's upright axis so the foot stays on the ground, lifted as it comes
        // forward. legSplay is not used; legLength is still the hip's height (asleep, carrying).
        public bool legFromHip = false;
        public List<float> legYaw;          // with legFromHip: how far each leg (in legs order) is turned toward the front, degrees
        public float legLift = 12f;         // with legFromHip: how far a foot is raised as it comes forward, degrees
        public bool deadBellyUp = false;    // dead (not downed), it lies on its back with its legs up and curled in (insects)
        // Wings (Api 14; Animals.cs): one wing model drawn at each hinge, folded on the ground and flapping in flight
        // (Pawn_FlightTracker), blended by the game's own take-off and landing height. Animals without wings pay nothing.
        public string wingModel;            // one (right) wing, spread: hinge at the origin, span along +x, leading edge +z;
                                            // the left wing is its mirror image
        public List<Vector3> wings;         // where each hinge is on the body (facing north, like legs); x < 0 is a left wing
        public float wingFold = 90f;        // on the ground: how far the wings are swept back about the upright axis, degrees
        public float wingDroop = 0f;        // on the ground: how far their tips are lowered, degrees (resting on the back or flanks)
        public float wingFlap = 50f;        // in flight: how far they beat up and down about the body's long axis, degrees
        public float wingBeat = 6f;         // in flight: beats per second at normal game speed (insects ~6, big birds ~2)
        // (rotation, for an animal, turns its body model only; legs and hips are given facing north.)
        public float stride = 1f;           // cells walked for one full swing of the legs
        // Hovering (Api 32; Hover.cs), for animals and mechs: while it is up and about (standing, walking, working) the whole
        // figure floats hoverHeight cells over the ground (times its size) and drifts up and down by hoverBob cells; downed,
        // dead, asleep or resting it settles onto the ground (eased, not snapped). Its ground shadow stays on the ground under it.
        public float hoverHeight = 0f;
        public float hoverBob = 0f;
        public float bodyCentre = 0f;       // height of the middle of the trunk
        public float bodyHalfWidth = 0.2f;  // half the width of the trunk; where it rests when on its side
        public float bodyHalfLength = 0.4f; // half the length of the animal, for the shading under it
        public float backHeight = -1f;      // top of the back at the trunk middle, model units (Api 13 anchor Back); -1 measures it

        // Mechanoids (Mechs.cs). thingDef is the mech's race; model is the body (everything that does not move),
        // facing north, standing on the ground; mechParts are the pieces that move, each built in the same space
        // as the body (not re-centred), turned about its pivot. A mech whose game sprite is not the first one of
        // its kind (the Ancient look, over 100 years old) is drawn worn (ObjLoader.Weathered).
        public bool mech = false;
        public List<MechPart> mechParts;
        public List<string> factionMaterials; // faces of these materials take the faction's mech colour, half and half
                                            // (as the game lays its allegiance overlay over the sprite's mask)
        public Vector3 carryAt = new Vector3(0f, -1f, 0f); // where a hauled thing is held (x, height, z; model space);
                                            // height -1: in front of the body at half its height

        // Entities (Anomaly creatures) drawn on the same path as mechs (Mechs.cs): entity = true, model and
        // mechParts as for a mech (no faction marks, no worn look). looks are whole other models the game's own
        // state picks (each its own model and mechParts), chosen by lookBy:
        //   alternate  the kind's alternate graphic the game gave this individual (key = the end of its texPath,
        //              e.g. Chimera_B); none: model
        //   activity   the stage Graphic_ActivityStaged shows: looks[min(floor(count * activity), count - 1)]
        //   digesting  looks[0] while CompDevourer is digesting (the closed mouth), else model
        //   lifeStage  the look whose key is the entity's life stage (e.g. MetalhorrorLarva); none: model. These models are
        //              made at each stage's own size, so the life stage does not shrink them again
        // fixedFacing: always faces south, as the game draws it (useFixedRotation; the fleshmass nucleus).
        public bool entity = false;
        public string lookBy;
        public List<EntityLook> looks;
        public bool fixedFacing = false;
        // Materials of an entity that give off light (unlit) in a colour that follows its activity
        // (CompActivity.ActivityLevel): from half their own colour at 0 to glowColor at 1 (the nociosphere's red).
        public List<string> glowMaterials;
        public Color glowColor = new Color(0.89f, 0.21f, 0.13f);
    }

    // One beating part (ModelDef.pulseParts): <li><model>Models/x.obj</model><pivot>(0, -0.35)</pivot>
    // <maxScale>(1.15, 1.2)</maxScale><delay>0</delay></li>; pivot (x, z) in the thing's space, the model built round it.
    public class PulsePart
    {
        public string model;
        public Vector2 pivot = Vector2.zero;
        public Vector2 maxScale = new Vector2(1.1f, 1.1f);
        public int delay = 0;
    }

    // One other look of an entity (ModelDef.looks).
    public class EntityLook
    {
        public string key;
        public string model;
        public List<MechPart> mechParts;
    }

    // One moving piece of a mechanoid (ModelDef.mechParts):
    //   <li><model>Models/x_wheel.obj</model><motion>wheel</motion><pivot>(0, 0.225, -0.05)</pivot><radius>0.225</radius></li>
    // pivot is (x, height, z) in the body's space (Blender (x, y, z) is (x, z, y) here).
    // motion:
    //   fixed   merged into the body (segments, hatches)
    //   wheel   rolls about its east-west axis by the ground covered (radius in cells)
    //   leg     swings about its east-west axis in step (beat: where in the stride, 0..1; swing: degrees)
    //   spin    turns about the upright axis while the mech works (speed: degrees a second) - brushes, cutter discs
    //   pitch   nods about the east-west axis while the mech works (swing: degrees) - sensor heads
    //   arm     swings down and up about the east-west axis while the mech works (swing: degrees) - digging arms
    //   upper   turns about the upright axis toward what the mech aims at - upper bodies
    //   head    the same, at most maxTurn degrees either way
    //   weapon  the mech's gun: shown only when the game shows it (aiming, drafted ...), pointing where the game
    //           points it; weapon names the gun ThingDef it shows (another gun is drawn as that gun's own 3D form)
    //   turret  a CompTurretGun's gun, pointing where the comp points it; hidden once the turret is destroyed
    //   open    slides out by openOffset (x, height, z) times how far the entity has opened (the nociosphere's
    //           shell segments, by CompNociosphere.segScale 0..1)
    // parent: name of another part (its file name without folder and .obj) this one moves with (claws on an arm,
    // a gun on an upper body).
    // show: "closed" or "open" - drawn only while the entity is shut (opened under 0.1) or open (the nociosphere's
    // dark lining and red core); left out, always. A closed part with fadeFrom/fadeTo shrinks (to fadeScale, about its
    // pivot) between those two and is gone after fadeTo, so it sinks into what it covered instead of popping.
    public class MechPart
    {
        public string model;
        public string name;
        public string motion = "fixed";
        public Vector3 pivot = Vector3.zero;
        public float radius = 0.2f;
        public float beat = 0f;
        public float swing = 20f;
        public float speed = 240f;
        public float maxTurn = 60f;
        public string parent;
        public string weapon;
        public Vector3 openOffset = Vector3.zero;
        public string show;
        public float fadeFrom = -1f;        // show closed: from this openness it shrinks about its pivot toward fadeScale,
        public float fadeTo = -1f;          // and from this one it is gone (instead of vanishing at 0.1)
        public float fadeScale = 0.9f;
    }

    // Triangle soup: every three entries make one triangle, already in game axes
    // (X east, Y up, Z north), one cell per unit, centred on the thing's footprint.
    public class Model
    {
        public Vector3[] positions;
        public Vector3[] normals;
        public Color[] colors;
        public bool[] tinted;   // true where the colour is multiplied by the thing's stuff colour
        public Vector2[] uvs;   // texture coordinates
        public int[] material;  // which entry of materials each vertex uses
        public ModelMaterial[] materials;
        public Vector3[] hull;  // how far each vertex moves outward to form the dark outline
        public bool[] noOutline; // per vertex: true where its triangle gets no outline (ModelDef.outlineMinSize); null for none
        public float maxY;
        public float halfDepth; // furthest the model reaches north or south of its centre
        public float minX, maxX, minZ, maxZ;   // the box the model covers on the ground
        public int id;          // unique per loaded model
        public ShadowShape shadow;   // worked out the first time the model has to cast a shadow
        public bool shadowTried;
        public Vector2[] footprint;      // outline of the model seen from above, and of the model
        public Vector2[] footprintLaid;  // tipped onto its side; worked out when first needed
        public bool footprintTried;
        public Model beam;               // (Api 33) its beam_* faces, drawn as light (Beams.cs); null for none
        public float beamFlicker, beamPulse;   // on a beam part: its def's beamFlicker / beamPulse (Beams.Tag)
    }

    // A simple stand-in for a model when it casts a shadow: the outline of its leaves seen from
    // above, held at one height, on top of a square post for the trunk. Flat pieces like these
    // never lie over themselves however the sun moves them, which the real model's hundreds of
    // faces would.
    public class ShadowShape
    {
        public Vector2[] crown;     // corners of the leaf outline (x, z), anticlockwise; null if there are no leaves
        public float crownHeight;
        public float trunkHalf;     // half the width of the trunk
        public float trunkTop;      // the height of everything that is not a leaf

        public static ShadowShape For(Model m)
        {
            if (!m.shadowTried)
            {
                m.shadowTried = true;
                try
                {
                    m.shadow = Build(m);
                }
                catch (Exception e)
                {
                    m.shadow = null;
                    Log.ErrorOnce("[IsoRim] Working out a model's shadow failed: " + e, 93611061);
                }
            }
            return m.shadow;
        }

        // The outline of the ground a model covers, corners anticlockwise in (x, z); null if it has none.
        public static Vector2[] Footprint(Model m, bool laid)
        {
            if (!m.footprintTried)
            {
                m.footprintTried = true;
                try
                {
                    List<Vector2> standing = new List<Vector2>(m.positions.Length);
                    List<Vector2> tipped = new List<Vector2>(m.positions.Length);
                    for (int i = 0; i < m.positions.Length; i++)
                    {
                        Vector3 p = m.positions[i];
                        standing.Add(new Vector2(p.x, p.z));
                        tipped.Add(new Vector2(p.x, p.y));   // tipped over, what was up points north
                    }
                    Vector2[] a = Hull(standing);
                    Vector2[] b = Hull(tipped);
                    m.footprint = a.Length >= 3 ? a : null;
                    m.footprintLaid = b.Length >= 3 ? b : null;
                }
                catch (Exception e)
                {
                    m.footprint = null;
                    m.footprintLaid = null;
                    Log.ErrorOnce("[IsoRim] Working out a model's footprint failed: " + e, 93611062);
                }
            }
            return laid ? m.footprintLaid : m.footprint;
        }

        private static ShadowShape Build(Model m)
        {
            int n = m.positions.Length;
            bool[] leaf = new bool[n];
            List<Vector2> leaves = new List<Vector2>();
            float leafLow = float.MaxValue;
            float leafHigh = 0f;
            float woodTop = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = m.positions[i];
                ModelMaterial mm = m.materials != null && m.material != null ? m.materials[m.material[i]] : null;
                leaf[i] = mm != null && mm.leaf;
                if (leaf[i])
                {
                    leaves.Add(new Vector2(p.x, p.z));
                    leafLow = Mathf.Min(leafLow, p.y);
                    leafHigh = Mathf.Max(leafHigh, p.y);
                }
                else
                {
                    woodTop = Mathf.Max(woodTop, p.y);
                }
            }

            // The trunk is as wide as the woody part is in its bottom third.
            float limit = Mathf.Max(0.05f, woodTop * 0.33f);
            float half = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = m.positions[i];
                if (!leaf[i] && p.y <= limit)
                {
                    half = Mathf.Max(half, Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.z)));
                }
            }

            ShadowShape s = new ShadowShape();
            s.trunkHalf = Mathf.Clamp(half * 0.8f, 0.02f, 0.25f);
            s.trunkTop = woodTop;
            if (leaves.Count >= 3)
            {
                Vector2[] hull = Hull(leaves);
                if (hull.Length >= 3)
                {
                    s.crown = hull;
                    s.crownHeight = leafLow + (leafHigh - leafLow) * 0.45f;
                }
            }
            if (s.crown == null && woodTop <= 0.01f)
            {
                return null;   // nothing to cast a shadow
            }
            return s;
        }

        // The smallest outline with no dents that holds every point, corners in anticlockwise order.
        private static Vector2[] Hull(List<Vector2> points)
        {
            points.Sort(delegate (Vector2 a, Vector2 b)
            {
                int c = a.x.CompareTo(b.x);
                return c != 0 ? c : a.y.CompareTo(b.y);
            });
            int n = points.Count;
            Vector2[] h = new Vector2[2 * n];
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                while (k >= 2 && Cross(h[k - 2], h[k - 1], points[i]) <= 1e-9f)
                {
                    k--;
                }
                h[k++] = points[i];
            }
            int lower = k + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                while (k >= lower && Cross(h[k - 2], h[k - 1], points[i]) <= 1e-9f)
                {
                    k--;
                }
                h[k++] = points[i];
            }
            int count = Mathf.Max(0, k - 1);   // the last point repeats the first
            Vector2[] result = new Vector2[count];
            Array.Copy(h, result, count);
            return result;
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }
    }

    public class ModelMaterial
    {
        public string name = "";
        public string texture;   // path under Textures/ without extension; null for a plain colour
        public Color color = new Color(0.8f, 0.8f, 0.8f);
        public bool tinted;
        public bool sheet;       // (Api 24) a name starting with "sheet": its colour times the thing's second colour
                                 // (Thing.DrawColorTwo: a bed's sheets, normal / medical / prisoner / slave; R3DTextures.SheetTint)
        public bool leaf;        // left out while a tree is bare
        public bool twig;        // only drawn while a tree is bare (fine branches the leaves would hide)
        public bool glow;        // gives off its own light (flames, embers, lamps): never shaded
        public bool fire;        // a flame or embers: left out while the thing's fire is out
        public bool beam;        // (Api 33) a name starting with "beam_": its faces are the model's beam part (Model.beam)
        public Texture2D runtimeTexture;   // (Api 34) a texture made at run time (a wall top's side, ShapeTops) instead of texture's
                                           // path, named runtimeName; texture is then only a placeholder
        public string runtimeName;
    }

    // One entry of ModelDef.byAppearance: <li><appearance>Planks</appearance><model>Models/x.obj</model></li>
    public class AppearanceModel
    {
        public string appearance;   // a StuffAppearanceDef defName
        public string model;
    }

    // (Api 28) One entry of ModelDef.slots: one place a pawn lies on the thing. Only height for now; later settings for the
    // same place (offset, facing, pose) are added here as fields, so packs keep one list.
    public class ModelSlot
    {
        public float height = -1f;   // the slot's surface height (model units, before the fit, as surfaceHeight); -1 surfaceHeight
    }

    // One entry of ModelDef.byState (and of bobByState, Api 27).
    public class StateModel
    {
        public string state;    // a state name an addon gives things (Api.AddModelState)
        public string model;
    }

    // One entry of ModelDef.byStyle.
    public class StyleModel
    {
        public string style;    // a ThingStyleDef defName
        public string model;
    }

    public class ModelEntry
    {
        public ModelDef def;
        public Model[] variants;
        public Model[] stack;     // stackModels (a, b, c), or null
        public Dictionary<string, Model> byAppearance;   // StuffAppearanceDef defName -> model; null if none
        public Dictionary<string, Model[]> byStyle;      // ThingStyleDef defName -> its models (one per random variant); null if none
        public Dictionary<string, Model> byState;        // addon state name -> model (byState); null if none
        public Model[] immature;   // (Api 24) a plant's young stage, one per variant (immatureModels / immatureHide); null if none
        public Model leg;   // animals only
        public Model wing;        // animals with wings only (wingModel), the right wing
        public Model wingMirror;  // the same, mirrored: the left wing
        public Model top;   // turrets only
        public Model empty;       // emptyModel, or null
        public Model doorLeaf;    // wide doors, and one-cell doors with their own models (Doors.DrawOne)
        public Model doorUpperLeaf;   // wide doors only, doorUpperLeafModel
        public Model doorTop;
        public Model topLoaded;   // turrets whose gun sprite changes once loaded (rocket launchers)
        public Model spin;        // spinModel, or null
        public Model spinLit;     // spinModel with its litMaterial lit, or null
        public Model bob;         // bobModel, or null
        public Dictionary<string, Model> bobByState;   // (Api 27) addon state name -> bob part (bobByState); null if none
        public Model[] pulse;     // pulseParts' models (same order), or null
        public Model activityGlow; // activityGlowModel with its glowing faces lit (ObjLoader.Glowing), or null
        public Color activityGlowBase; // the lit faces' own colour
        public Model glass;       // glassModel, or null
        public Model roofPart;    // roofModel, or null (Api 31)
        public Model hackTerminal; // hackTerminalModel, or null
        public Model head;        // headModel, or null
        public Model open;        // openModel (with openParts), or null
        public Model hacked;      // hackedModel, or null

        public bool HasModel
        {
            get { return variants != null && variants.Length > 0; }
        }

        // True if the model was built long east-west but the thing is long north-south (or the
        // other way round): it is then turned a quarter turn to fit.
        public bool Swapped(ThingDef thing)
        {
            if (def.builtFor.x <= 0f || def.builtFor.y <= 0f || thing == null || Mathf.Approximately(def.builtFor.x, def.builtFor.y)
                || thing.size.x == thing.size.z)
            {
                return false;
            }
            return (def.builtFor.x > def.builtFor.y) != (thing.size.x > thing.size.z);
        }

        // How much the model is stretched along x, up and along z to fit a thing of this kind:
        // (1, 1, 1) unless the model was built for a different footprint.
        internal Vector3 FootprintFit(ThingDef thing)
        {
            if (def.builtFor.x <= 0f || def.builtFor.y <= 0f || thing == null)
            {
                return Vector3.one;
            }
            bool swap = Swapped(thing);
            float sx = (swap ? thing.size.z : thing.size.x) / def.builtFor.x;
            float sz = (swap ? thing.size.x : thing.size.z) / def.builtFor.y;
            if (Mathf.Abs(sx - 1f) < 0.01f && Mathf.Abs(sz - 1f) < 0.01f)
            {
                return Vector3.one;
            }
            return new Vector3(sx, Mathf.Sqrt(sx * sz), sz);
        }

        // How much the model is stretched: the footprint fit above, and up to the walls for a model that follows them
        // (WallFit).
        public Vector3 Fit(ThingDef thing)
        {
            Vector3 f = FootprintFit(thing);
            float fit = WallFit(thing);
            if (fit > 0f)
            {
                f.y *= View3D.WallHeight / fit;
            }
            return f;
        }

        // Height of the walls the library's models are built against (the default wall height, cells).
        public const float BuiltForWalls = 1.2f;

        // The wall height a thing's model was built to meet, in cells: its height follows the walls drawn now (Fit).
        // wallFit if the def sets it; else, for a building that stands in for a wall (it holds the roof up as walls do,
        // the game's holdsRoof: columns, wide doors; or it is built into a wall line, building.canPlaceOverWall: coolers,
        // vents) and whose model reaches the top of the walls it was built against, those walls, so it meets the roof
        // and the walls beside it at any wall height. 0: the model keeps its own height.
        public float WallFit(ThingDef thing)
        {
            if (def.wallFit > 0f)
            {
                return def.wallFit;
            }
            if (thing == null || !(thing.holdsRoof || (thing.building != null && thing.building.canPlaceOverWall)) || def.flat || !HasModel)
            {
                return 0f;
            }
            // Its top at the wall top, not over it: a model built taller on purpose (stairs up to an upper floor)
            // keeps its height.
            float top = Tallest() * def.scale * FootprintFit(thing).y + def.offset.y;
            return Mathf.Abs(top - BuiltForWalls) <= 0.06f ? BuiltForWalls : 0f;
        }

        // The turn (degrees) a thing's model is drawn at for the thing's rotation: the rotation (none with
        // ignoreRotation) plus the def's extra rotation and its rotationByFacing for that facing. Every drawer of a building's model uses this.
        public float Yaw(Rot4 rot)
        {
            float yaw = (def.ignoreRotation ? 0f : rot.AsAngle) + def.rotation;
            if (def.rotationByFacing != null && rot.AsInt < def.rotationByFacing.Count)
            {
                yaw += def.rotationByFacing[rot.AsInt];
            }
            return yaw;
        }

        // The same thing always gets the same variant.
        public Model Pick(Thing t)
        {
            if (!HasModel)
            {
                return null;
            }
            if (byStyle != null && t != null)
            {
                Model styled = ForStyle(StyleOf(t), t);
                if (styled != null)
                {
                    return styled;
                }
            }
            if (byAppearance != null && t != null)
            {
                // As Graphic_Appearances: a blueprint or frame by the stuff it will be built of.
                IConstructible building = t as IConstructible;
                Model own = ForStuff(building != null ? building.EntityToBuildStuff() : t.Stuff);
                if (own != null)
                {
                    return own;
                }
            }
            if (stack != null && stack.Length > 0)
            {
                return stack[StackIndex(t)];
            }
            // As the game picks the sprite: Graphic_Indexed and Graphic_Random take a forced index first
            // (Thing.OverrideGraphicIndex: the void monolith's level, its attachments', an activated void structure's);
            // otherwise Graphic_Indexed shows its first sprite and Graphic_Random one by the thing's ID.
            int? forced = t != null ? t.OverrideGraphicIndex : null;
            int n = forced ?? (t == null ? 0 : IndexedGraphic(t.def) ? 0 : t.thingIDNumber);
            if (n < 0)
            {
                n = -n;
            }
            return variants[n % variants.Length];
        }

        private static bool IndexedGraphic(ThingDef def)
        {
            System.Type gc = def != null && def.graphicData != null ? def.graphicData.graphicClass : null;
            return gc != null && typeof(Graphic_Indexed).IsAssignableFrom(gc);
        }

        // Which of the stack models a stack of this size uses: Graphic_StackCount's rule for 1-3 sprites.
        private int StackIndex(Thing t)
        {
            int n = stack.Length;
            if (t == null || n == 1 || t.stackCount <= 1)
            {
                return 0;
            }
            return n >= 3 && t.stackCount >= t.def.stackLimit ? n - 1 : 1;
        }

        // The style a thing is built in (a blueprint: will be built in); null for none.
        public static ThingStyleDef StyleOf(Thing t)
        {
            Blueprint bp = t as Blueprint;
            return bp != null ? bp.EntityToBuildStyle() : t.StyleDef;
        }

        // The model of byStyle for things of this style; null if there is none. Of several (a style's Graphic_Random), the
        // one the game's sprite is for thing t (forced index first, else by ID), the first for no thing.
        public Model ForStyle(ThingStyleDef style, Thing t = null)
        {
            Model[] own;
            if (byStyle == null || style == null || !byStyle.TryGetValue(style.defName, out own) || own.Length == 0)
            {
                return null;
            }
            return own[RandomIndex(t, own.Length)];
        }

        // Graphic_Random's pick of n sprites for thing t: its forced index (Thing.OverrideGraphicIndex) or its ID; 0 for none.
        public static int RandomIndex(Thing t, int n)
        {
            if (t == null || n <= 1)
            {
                return 0;
            }
            int k = t.OverrideGraphicIndex ?? t.thingIDNumber;
            return (k < 0 ? -k : k) % n;
        }

        // The model of byAppearance for things of this stuff; null if there is none.
        public Model ForStuff(ThingDef stuff)
        {
            StuffAppearanceDef look = stuff != null && stuff.stuffProps != null ? stuff.stuffProps.appearance : null;
            Model own;
            return byAppearance != null && look != null && byAppearance.TryGetValue(look.defName, out own) ? own : null;
        }

        public float SurfaceHeight
        {
            get
            {
                if (def.surfaceHeight >= 0f)
                {
                    return def.surfaceHeight;
                }
                return HasModel ? variants[0].maxY * def.scale : 0f;
            }
        }

        // Top of the seat (seatHeight, else SurfaceHeight) and of the bedding (beddingTop, else SurfaceHeight), before the
        // footprint fit (Poses applies it).
        public float SeatHeight
        {
            get { return def.seatHeight >= 0f ? def.seatHeight : SurfaceHeight; }
        }

        public float BeddingTop
        {
            get { return def.beddingTop >= 0f ? def.beddingTop : SurfaceHeight; }
        }

        // (Api 28) The surface height of a slot (ModelDef.slots), before the footprint fit; false when the slot has none of
        // its own (no list, past its end, height -1): the thing's own surface then.
        public bool SlotHeight(int slot, out float height)
        {
            height = 0f;
            List<ModelSlot> slots = def.slots;
            if (slots == null || slot < 0 || slot >= slots.Count || slots[slot] == null || slots[slot].height < 0f)
            {
                return false;
            }
            height = slots[slot].height;
            return true;
        }

        // How tall a thing of this kind is drawn, in cells: the top of its tallest model (variants, per-stuff and
        // per-style models, a turret's gun on its pivot) times the scale and the footprint fit, plus the offset.
        public float DrawnHeight(ThingDef thing)
        {
            if (!HasModel)
            {
                return 0f;
            }
            float h = Tallest() * def.scale * Fit(thing).y;
            if (this.top != null)
            {
                h = Mathf.Max(h, def.topHeight + this.top.maxY * def.scale);
            }
            return Mathf.Max(0f, h + def.offset.y);
        }

        // Top of the tallest model (variants, per-stuff and per-style models), in model space.
        private float Tallest()
        {
            float tallest = 0f;
            for (int i = 0; i < variants.Length; i++)
            {
                tallest = Mathf.Max(tallest, variants[i] != null ? variants[i].maxY : 0f);
            }
            if (byAppearance != null)
            {
                foreach (Model m in byAppearance.Values)
                {
                    tallest = Mathf.Max(tallest, m != null ? m.maxY : 0f);
                }
            }
            if (byStyle != null)
            {
                foreach (Model[] ms in byStyle.Values)
                {
                    foreach (Model m in ms)
                    {
                        tallest = Mathf.Max(tallest, m != null ? m.maxY : 0f);
                    }
                }
            }
            if (byState != null)
            {
                foreach (Model m in byState.Values)
                {
                    tallest = Mathf.Max(tallest, m != null ? m.maxY : 0f);
                }
            }
            if (roofPart != null)
            {
                tallest = Mathf.Max(tallest, roofPart.maxY);   // (Api 31) the part over the roof
            }
            return tallest;
        }
    }

    public static partial class ModelLibrary   // (dev-mode binder overrides: ModelLibraryOverrides.cs)
    {
        private static readonly Dictionary<ThingDef, ModelEntry> byThing = new Dictionary<ThingDef, ModelEntry>();
        private static readonly Dictionary<string, Model> byPath = new Dictionary<string, Model>();
        private static readonly Dictionary<ThingDef, string> how = new Dictionary<ThingDef, string>();

        // ModelDefs with a style (ModelDef.style): thing -> style defName -> that def's own entry.
        private static readonly Dictionary<ThingDef, Dictionary<string, ModelEntry>> styled = new Dictionary<ThingDef, Dictionary<string, ModelEntry>>();

        // (Api 24) A plant's young-stage models (ModelEntry.immature): its immatureModels (or, with none, its variants), each
        // without the immatureHide materials. Null when the def gives neither, or nothing is left.
        private static Model[] ImmatureModels(ModelDef md, Model[] variants)
        {
            bool own = md.immatureModels != null && md.immatureModels.Count > 0;
            bool hide = md.immatureHide != null && md.immatureHide.Count > 0;
            if (!own && !hide)
            {
                return null;
            }
            List<Model> from = new List<Model>();
            if (own)
            {
                if (md.modContentPack == null)
                {
                    return null;
                }
                for (int i = 0; i < md.immatureModels.Count; i++)
                {
                    Model m = LoadFile(Path.Combine(md.modContentPack.RootDir, md.immatureModels[i]));
                    if (m != null && md.outlineMinSize > 0f)
                    {
                        m = ObjLoader.WithoutSmallOutlines(m, md.outlineMinSize);
                    }
                    if (m != null)
                    {
                        from.Add(m);
                    }
                }
            }
            else if (variants != null)
            {
                from.AddRange(variants);
            }
            List<Model> young = new List<Model>();
            for (int i = 0; i < from.Count; i++)
            {
                Model m = hide ? ObjLoader.WithoutMaterials(from[i], md.immatureHide) : from[i];
                if (m != null)
                {
                    young.Add(m);
                }
            }
            if (young.Count == 0)
            {
                ModLog.Warn("[IsoRim] " + md.defName + ": no young-stage model left (immatureModels / immatureHide)");
                return null;
            }
            return young.ToArray();
        }

        public static bool TryGet(ThingDef def, out ModelEntry entry)
        {
            return byThing.TryGetValue(def, out entry);
        }

        // The own entry of a style def (ModelDef.style) for thing t's style (a blueprint: the style it will be built in);
        // null if there is none. Drawers that do not use the thing's entry (one-cell doors) draw it directly.
        public static ModelEntry StyledEntry(Thing t)
        {
            Dictionary<string, ModelEntry> forThing;
            ThingStyleDef style = t != null && styled.TryGetValue(t.def, out forThing) ? ModelEntry.StyleOf(t) : null;
            ModelEntry own;
            return style != null && styled[t.def].TryGetValue(style.defName, out own) ? own : null;
        }

        // The same for a def placed in a style (the placement preview).
        public static ModelEntry StyledEntry(ThingDef def, ThingStyleDef style)
        {
            Dictionary<string, ModelEntry> forThing;
            ModelEntry own;
            return def != null && style != null && styled.TryGetValue(def, out forThing) && forThing.TryGetValue(style.defName, out own) ? own : null;
        }

        // How a thing got its entry: "thingDef", "category:<name>", "match:<name part>" or
        // "fallback:SmallPlants"; null if it has none (for the harness's model sweep).
        public static string HowBound(ThingDef def)
        {
            string h;
            return how.TryGetValue(def, out h) ? h : null;
        }

        // A texture path as graphicPath compares it (Api 16): lower case, / for \, no trailing /. Null if empty.
        internal static string NormalPath(string path)
        {
            if (path.NullOrEmpty())
            {
                return null;
            }
            path = path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
            return path.Length == 0 ? null : path;
        }

        // Whether a <match> name part names a word (or run of words) of the defName: it must start
        // where a capitalised word starts and end where one ends, ignoring case. "Car" finds
        // AncientCar and RustedCarWreck but not AncientCardboardBox; "APC" finds AncientAPC but not
        // ScrapCubeSculpture. Plain substring matching put a car wreck in place of a cardboard box.
        public static bool NameMatches(string defName, string part)
        {
            if (defName.NullOrEmpty() || part.NullOrEmpty())
            {
                return false;
            }
            int from = 0;
            while (true)
            {
                int at = defName.IndexOf(part, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    return false;
                }
                int end = at + part.Length;
                bool starts = at == 0 || !char.IsLetter(defName[at - 1]) || char.IsUpper(defName[at]);
                // A plural counts too: "AncientPipe" finds AncientPipes.
                bool ends = end == defName.Length || !char.IsLower(defName[end])
                    || (defName[end] == 's' && (end + 1 == defName.Length || !char.IsLower(defName[end + 1])));
                if (starts && ends)
                {
                    return true;
                }
                from = at + 1;
            }
        }

        // Loads a model file once; null if it is missing or unreadable.
        public static Model LoadFile(string path)
        {
            Model model;
            if (byPath.TryGetValue(path, out model))
            {
                return model;
            }
            try
            {
                model = ObjLoader.Load(path);
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Could not read model " + path + ": " + e.Message);
                model = null;
            }
            byPath[path] = model;
            return model;
        }

        // The model with the given part files merged into it (missing parts are left out).
        private static Model WithParts(Model main, List<string> parts, string root)
        {
            List<Model> all = new List<Model> { main };
            for (int i = 0; i < parts.Count; i++)
            {
                Model p = parts[i].NullOrEmpty() ? null : LoadFile(Path.Combine(root, parts[i]));
                if (p != null)
                {
                    all.Add(p);
                }
            }
            return all.Count > 1 ? ObjLoader.Merge(all) : main;
        }

        private static ModelEntry MakeEntry(ModelDef md)
        {
            List<string> files = new List<string>();
            if (!md.model.NullOrEmpty())
            {
                files.Add(md.model);
            }
            if (md.models != null)
            {
                files.AddRange(md.models);
            }

            List<Model> loaded = new List<Model>();
            if (md.modContentPack != null)
            {
                for (int i = 0; i < files.Count; i++)
                {
                    Model m = LoadFile(Path.Combine(md.modContentPack.RootDir, files[i]));
                    if (m != null)
                    {
                        loaded.Add(m);
                    }
                }
            }

            if (md.parts != null && md.parts.Count > 0 && loaded.Count > 0 && md.modContentPack != null)
            {
                loaded[0] = WithParts(loaded[0], md.parts, md.modContentPack.RootDir);
            }
            if (md.outlineMinSize > 0f)
            {
                for (int i = 0; i < loaded.Count; i++)
                {
                    loaded[i] = ObjLoader.WithoutSmallOutlines(loaded[i], md.outlineMinSize);
                }
            }
            ModelEntry entry = new ModelEntry();
            entry.def = md;
            entry.variants = loaded.ToArray();
            if (md.stackModels != null && md.stackModels.Count > 0 && md.modContentPack != null)
            {
                List<Model> stack = new List<Model>();
                for (int i = 0; i < md.stackModels.Count; i++)
                {
                    Model m = LoadFile(Path.Combine(md.modContentPack.RootDir, md.stackModels[i]));
                    if (m == null)
                    {
                        stack = null;   // a missing file: not used at all
                        break;
                    }
                    stack.Add(m);
                }
                if (stack != null)
                {
                    entry.stack = stack.ToArray();
                    if (entry.variants.Length == 0)
                    {
                        entry.variants = new[] { entry.stack[entry.stack.Length - 1] };   // the full stack: its height
                    }
                }
            }
            if (md.byAppearance != null && md.modContentPack != null)
            {
                for (int i = 0; i < md.byAppearance.Count; i++)
                {
                    AppearanceModel am = md.byAppearance[i];
                    Model m = am != null && !am.appearance.NullOrEmpty() && !am.model.NullOrEmpty()
                        ? LoadFile(Path.Combine(md.modContentPack.RootDir, am.model)) : null;
                    if (m != null)
                    {
                        if (entry.byAppearance == null)
                        {
                            entry.byAppearance = new Dictionary<string, Model>();
                        }
                        entry.byAppearance[am.appearance] = m;
                    }
                }
            }
            if (md.byStyle != null && md.modContentPack != null)
            {
                for (int i = 0; i < md.byStyle.Count; i++)
                {
                    StyleModel sm = md.byStyle[i];
                    Model m = sm != null && !sm.style.NullOrEmpty() && !sm.model.NullOrEmpty()
                        ? LoadFile(Path.Combine(md.modContentPack.RootDir, sm.model)) : null;
                    if (m != null)
                    {
                        if (entry.byStyle == null)
                        {
                            entry.byStyle = new Dictionary<string, Model[]>();
                        }
                        entry.byStyle[sm.style] = new[] { m };
                    }
                }
            }
            if (md.byState != null && md.modContentPack != null)
            {
                for (int i = 0; i < md.byState.Count; i++)
                {
                    StateModel st = md.byState[i];
                    Model m = st != null && !st.state.NullOrEmpty() && !st.model.NullOrEmpty()
                        ? LoadFile(Path.Combine(md.modContentPack.RootDir, st.model)) : null;
                    if (m != null)
                    {
                        if (entry.byState == null)
                        {
                            entry.byState = new Dictionary<string, Model>();
                        }
                        entry.byState[st.state] = m;
                    }
                }
            }
            entry.immature = ImmatureModels(md, entry.variants);
            if (!md.legModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.leg = LoadFile(Path.Combine(md.modContentPack.RootDir, md.legModel));
            }
            if (!md.wingModel.NullOrEmpty() && md.wings != null && md.wings.Count > 0 && md.modContentPack != null)
            {
                entry.wing = LoadFile(Path.Combine(md.modContentPack.RootDir, md.wingModel));
                entry.wingMirror = entry.wing != null ? ObjLoader.Mirrored(entry.wing) : null;
            }
            if (!md.topModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.top = LoadFile(Path.Combine(md.modContentPack.RootDir, md.topModel));
            }
            if (!md.doorLeafModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.doorLeaf = LoadFile(Path.Combine(md.modContentPack.RootDir, md.doorLeafModel));
            }
            if (!md.doorUpperLeafModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.doorUpperLeaf = LoadFile(Path.Combine(md.modContentPack.RootDir, md.doorUpperLeafModel));
            }
            if (!md.doorTopModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.doorTop = LoadFile(Path.Combine(md.modContentPack.RootDir, md.doorTopModel));
            }
            if (!md.emptyModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.empty = LoadFile(Path.Combine(md.modContentPack.RootDir, md.emptyModel));
            }
            if (!md.hackedModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.hacked = LoadFile(Path.Combine(md.modContentPack.RootDir, md.hackedModel));
            }
            if (!md.topModelLoaded.NullOrEmpty() && md.modContentPack != null)
            {
                entry.topLoaded = LoadFile(Path.Combine(md.modContentPack.RootDir, md.topModelLoaded));
            }
            if (!md.spinModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.spin = LoadFile(Path.Combine(md.modContentPack.RootDir, md.spinModel));
                if (entry.spin != null && !md.litMaterial.NullOrEmpty())
                {
                    entry.spinLit = ObjLoader.Relit(entry.spin, md.litMaterial, md.litColors);
                }
            }
            if (!md.bobModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.bob = LoadFile(Path.Combine(md.modContentPack.RootDir, md.bobModel));
                if (md.bobByState != null)
                {
                    for (int i = 0; i < md.bobByState.Count; i++)
                    {
                        StateModel st = md.bobByState[i];
                        Model m = st != null && !st.state.NullOrEmpty() && !st.model.NullOrEmpty()
                            ? LoadFile(Path.Combine(md.modContentPack.RootDir, st.model)) : null;
                        if (m != null)
                        {
                            if (entry.bobByState == null)
                            {
                                entry.bobByState = new Dictionary<string, Model>();
                            }
                            entry.bobByState[st.state] = m;
                        }
                    }
                }
            }
            if (md.pulseParts != null && md.pulseParts.Count > 0 && md.modContentPack != null)
            {
                entry.pulse = new Model[md.pulseParts.Count];
                for (int i = 0; i < md.pulseParts.Count; i++)
                {
                    entry.pulse[i] = md.pulseParts[i].model.NullOrEmpty() ? null
                        : LoadFile(Path.Combine(md.modContentPack.RootDir, md.pulseParts[i].model));
                }
            }
            if (!md.activityGlowModel.NullOrEmpty() && md.modContentPack != null)
            {
                Model raw = LoadFile(Path.Combine(md.modContentPack.RootDir, md.activityGlowModel));
                if (raw != null)
                {
                    List<string> names = md.glowMaterials;
                    if (names == null || names.Count == 0)
                    {
                        names = new List<string>();
                        foreach (ModelMaterial mm in raw.materials)
                        {
                            names.Add(mm.name);
                        }
                    }
                    entry.activityGlow = ObjLoader.Glowing(raw, names, out entry.activityGlowBase);
                }
            }
            if (!md.hackTerminalModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.hackTerminal = LoadFile(Path.Combine(md.modContentPack.RootDir, md.hackTerminalModel));
            }
            if (!md.openModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.open = LoadFile(Path.Combine(md.modContentPack.RootDir, md.openModel));
                if (entry.open != null && md.openParts != null && md.openParts.Count > 0)
                {
                    entry.open = WithParts(entry.open, md.openParts, md.modContentPack.RootDir);
                }
            }
            if (!md.headModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.head = LoadFile(Path.Combine(md.modContentPack.RootDir, md.headModel));
            }
            if (!md.glassModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.glass = LoadFile(Path.Combine(md.modContentPack.RootDir, md.glassModel));
            }
            if (!md.roofModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.roofPart = LoadFile(Path.Combine(md.modContentPack.RootDir, md.roofModel));
            }
            Aged.Apply(entry, Aged.StrengthOf(md));   // ModelDef.aged: its body models old and rusty
            Beams.Tag(entry);   // (Api 33) beamFlicker / beamPulse onto its beam parts
            return entry;
        }

        public static void LoadAll()
        {
            List<ModelDef> all = DefDatabase<ModelDef>.AllDefsListForReading;

            // Entries naming one thing come first, so they win over a category the thing is in.
            for (int i = 0; i < all.Count; i++)
            {
                ModelDef md = all[i];
                if (md.thingDef.NullOrEmpty() || !md.style.NullOrEmpty())
                {
                    continue;   // style defs are added last (AddStyles)
                }
                // A thing from a DLC or mod that is not installed is skipped quietly.
                ThingDef thing = DefDatabase<ThingDef>.GetNamedSilentFail(md.thingDef);
                if (thing != null)
                {
                    byThing[thing] = MakeEntry(md);
                    how[thing] = "thingDef";
                }
            }

            // Then texture paths (Api 16): one model for every thing drawn with the same art, e.g. the meat the game
            // generates per animal (vanilla and modded), before categories.
            for (int i = 0; i < all.Count; i++)
            {
                ModelDef md = all[i];
                string path = NormalPath(md.graphicPath);
                if (path == null || !md.style.NullOrEmpty())
                {
                    continue;
                }
                ModelEntry entry = null;
                foreach (ThingDef thing in DefDatabase<ThingDef>.AllDefs)
                {
                    if (thing.graphicData == null || thing.IsBlueprint || thing.IsFrame || byThing.ContainsKey(thing)
                        || NormalPath(thing.graphicData.texPath) != path)
                    {
                        continue;
                    }
                    if (entry == null)
                    {
                        entry = MakeEntry(md);
                    }
                    byThing[thing] = entry;
                    how[thing] = "graphicPath:" + md.graphicPath;
                }
            }

            for (int i = 0; i < all.Count; i++)
            {
                ModelDef md = all[i];
                if (md.thingCategory.NullOrEmpty())
                {
                    continue;
                }
                ThingCategoryDef category = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(md.thingCategory);
                if (category == null)
                {
                    continue;
                }
                ModelEntry entry = null;
                foreach (ThingDef thing in DefDatabase<ThingDef>.AllDefs)
                {
                    if (thing.thingCategories == null || !thing.thingCategories.Contains(category) || byThing.ContainsKey(thing))
                    {
                        continue;
                    }
                    if (entry == null)
                    {
                        entry = MakeEntry(md);
                    }
                    byThing[thing] = entry;
                    how[thing] = "category:" + md.thingCategory;
                }
            }

            // Then names: the wrecks, junk and ruins other content scatters about. Earlier entries win.
            for (int i = 0; i < all.Count; i++)
            {
                ModelDef md = all[i];
                if (md.match == null || md.match.Count == 0)
                {
                    continue;
                }
                ModelEntry entry = null;
                foreach (ThingDef thing in DefDatabase<ThingDef>.AllDefs)
                {
                    if (byThing.ContainsKey(thing) || thing.defName == null || thing.IsBlueprint || thing.IsFrame
                        || (thing.category != ThingCategory.Building && thing.category != ThingCategory.Item))
                    {
                        continue;
                    }
                    string hit = null;
                    for (int k = 0; k < md.match.Count && hit == null; k++)
                    {
                        if (NameMatches(thing.defName, md.match[k]))
                        {
                            hit = md.match[k];
                        }
                    }
                    if (hit == null)
                    {
                        continue;
                    }
                    if (entry == null)
                    {
                        entry = MakeEntry(md);
                    }
                    byThing[thing] = entry;
                    how[thing] = "match:" + hit;
                }
            }

            // Last, the catch-all for small plants: grass, flowers and crops from DLC and other mods.
            for (int i = 0; i < all.Count; i++)
            {
                ModelDef md = all[i];
                if (md.fallback != "SmallPlants")
                {
                    continue;
                }
                ModelEntry entry = null;
                foreach (ThingDef thing in DefDatabase<ThingDef>.AllDefs)
                {
                    if (thing.category != ThingCategory.Plant || thing.plant == null || thing.plant.IsTree || byThing.ContainsKey(thing))
                    {
                        continue;
                    }
                    // Tree stumps and burned trees are plants too, but a clump of leaves would not suit them.
                    if (thing.defName.Contains("Stump") || thing.defName.Contains("Burned"))
                    {
                        continue;
                    }
                    if (entry == null)
                    {
                        entry = MakeEntry(md);
                    }
                    byThing[thing] = entry;
                    how[thing] = "fallback:SmallPlants";
                }
            }

            AddStyles(all);

            int models = 0;
            foreach (KeyValuePair<ThingDef, ModelEntry> pair in byThing)
            {
                if (pair.Value.HasModel)
                {
                    models++;
                }
            }
            ModLog.Note("[IsoRim] " + models + " things have a hand-made 3D model.");
        }

        // Style defs (ModelDef.style), from any mod: each gets its own entry (StyledEntry), and its models, fitted from
        // its own space (builtFor, rotation, offset, scale) into that of the thing's own entry, become that entry's byStyle
        // for the style, so every drawer of the thing's model draws them as it is placed. A thing with no model entry
        // keeps only the own entry (one-cell doors draw it, Doors.DrawOne).
        private static void AddStyles(List<ModelDef> all)
        {
            styled.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                ModelDef md = all[i];
                if (md.style.NullOrEmpty() || md.thingDef.NullOrEmpty())
                {
                    continue;
                }
                ThingDef thing = DefDatabase<ThingDef>.GetNamedSilentFail(md.thingDef);
                if (thing == null || DefDatabase<ThingStyleDef>.GetNamedSilentFail(md.style) == null)
                {
                    continue;   // a thing or style from a mod that is not loaded
                }
                ModelEntry own = MakeEntry(md);
                if (!own.HasModel)
                {
                    continue;
                }
                Dictionary<string, ModelEntry> forThing;
                if (!styled.TryGetValue(thing, out forThing))
                {
                    forThing = new Dictionary<string, ModelEntry>();
                    styled[thing] = forThing;
                }
                forThing[md.style] = own;

                ModelEntry entry;
                if (!byThing.TryGetValue(thing, out entry) || !entry.HasModel)
                {
                    continue;
                }
                Model[] fitted = new Model[own.variants.Length];
                for (int k = 0; k < fitted.Length; k++)
                {
                    fitted[k] = FitInto(own.variants[k], own, entry, thing);
                }
                if (entry.byStyle == null)
                {
                    entry.byStyle = new Dictionary<string, Model[]>();
                }
                entry.byStyle[md.style] = fitted;
            }
        }

        // Model m of entry from, moved into the model space of entry to so that, drawn as to places its models on a thing
        // of this kind (Baking.Place: turned by Yaw and a quarter turn if Swapped, footprint fit times scale, offset), it
        // stands as from would place it.
        private static Model FitInto(Model m, ModelEntry from, ModelEntry to, ThingDef thing)
        {
            Vector3 fitFrom = from.FootprintFit(thing) * from.def.scale;
            Vector3 fitTo = to.FootprintFit(thing) * to.def.scale;
            float turn = (from.def.rotation + (from.Swapped(thing) ? 90f : 0f)) - (to.def.rotation + (to.Swapped(thing) ? 90f : 0f));
            Quaternion q = Quaternion.AngleAxis(turn, Vector3.up);
            Vector3 offFrom = from.def.offset, offTo = to.def.offset;
            if (Mathf.Abs(Mathf.DeltaAngle(0f, turn)) < 0.01f && (fitFrom - fitTo).sqrMagnitude < 1e-8f && (offFrom - offTo).sqrMagnitude < 1e-8f)
            {
                return m;   // the same space
            }
            return ObjLoader.Transformed(m,
                p => Div(q * (Vector3.Scale(p, fitFrom) + offFrom) - offTo, fitTo),
                n => Vector3.Scale(q * Div(n, fitFrom), fitTo).normalized);
        }

        private static Vector3 Div(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x / b.x, a.y / b.y, a.z / b.z);
        }
    }

    // Reads Wavefront OBJ, the plain-text format Blender exports with its default axes
    // (Y up, -Z forward), with its .mtl file. Per material: Kd is the colour, map_Kd an optional
    // texture, and a name starting with "stuff" takes the colour of whatever the thing is made of; one starting with
    // "sheet" is multiplied by the thing's second colour (Api 24: a bed's sheets, R3DTextures.SheetTint).
    public static class ObjLoader
    {
        private static readonly char[] Separators = { ' ', '\t' };
        private static readonly Color DefaultColor = new Color(0.8f, 0.8f, 0.8f);
        private const float OutlineWidth = 0.03f;   // in cells
        private const float MitreLimit = 1.5f;      // longest outline corner offset, in outline widths
        private static int nextId = 1;

        // A fresh model id, for a model made in code from another (People.FitOver).
        internal static int NewId()
        {
            return nextId++;
        }

        // (Api 34) The outline offsets of a model made in code (ShapeModels): as a loaded model's.
        internal static Vector3[] HullOf(Vector3[] positions, Vector3[] normals)
        {
            return BuildHull(positions, normals, OutlineWidth);
        }

        // A copy of a model whose faces of one material give off light (unlit, see ModelMaterial.glow),
        // each separate piece of them (faces joined by shared corners) in the next of colors in turn:
        // the lightball's lenses during a party. Null if the model has no such material.
        public static Model Relit(Model src, string material, List<Color> colors)
        {
            if (src == null || src.materials == null || src.material == null)
            {
                return null;
            }
            int from = -1;
            for (int i = 0; i < src.materials.Length; i++)
            {
                if (string.Equals(src.materials[i].name, material, StringComparison.OrdinalIgnoreCase))
                {
                    from = i;
                }
            }
            if (from < 0)
            {
                ModLog.Warn("[IsoRim] litMaterial " + material + " is not used by the model");
                return null;
            }
            if (colors == null || colors.Count == 0)
            {
                colors = new List<Color> { Color.white };
            }
            ModelMaterial lit = new ModelMaterial();
            lit.name = "glow_" + material;
            lit.glow = true;
            lit.color = Color.white;
            List<ModelMaterial> mats = new List<ModelMaterial>(src.materials);
            mats.Add(lit);
            int to = mats.Count - 1;

            // The material's triangles grouped into pieces by shared corners.
            int n = src.positions.Length;
            Dictionary<long, int> corner = new Dictionary<long, int>();
            List<int> parent = new List<int>();
            int[] triPiece = new int[n / 3];
            for (int i = 0; i + 2 < n; i += 3)
            {
                if (src.material[i] != from)
                {
                    continue;
                }
                int t = parent.Count;
                triPiece[i / 3] = t;
                parent.Add(t);
                for (int k = i; k < i + 3; k++)
                {
                    Vector3 p = src.positions[k];
                    long key = (((long)Mathf.RoundToInt(p.x * 2000f) & 0xFFFFF) << 40) | (((long)Mathf.RoundToInt(p.y * 2000f) & 0xFFFFF) << 20)
                        | ((long)Mathf.RoundToInt(p.z * 2000f) & 0xFFFFF);
                    int other;
                    if (corner.TryGetValue(key, out other))
                    {
                        parent[Root(parent, other)] = Root(parent, t);
                    }
                    else
                    {
                        corner[key] = t;
                    }
                }
            }

            Model m = new Model();
            m.positions = src.positions;
            m.normals = src.normals;
            m.uvs = src.uvs;
            m.hull = src.hull;
            m.beam = src.beam;   // (Api 33) the beam part as it was
            m.noOutline = src.noOutline;
            m.maxY = src.maxY;
            m.halfDepth = src.halfDepth;
            m.minX = src.minX;
            m.maxX = src.maxX;
            m.minZ = src.minZ;
            m.maxZ = src.maxZ;
            m.materials = mats.ToArray();
            m.colors = (Color[])src.colors.Clone();
            m.tinted = (bool[])src.tinted.Clone();
            m.material = (int[])src.material.Clone();
            Dictionary<int, int> pieceOf = new Dictionary<int, int>();
            for (int i = 0; i + 2 < n; i += 3)
            {
                if (src.material[i] != from)
                {
                    continue;
                }
                int root = Root(parent, triPiece[i / 3]);
                int piece;
                if (!pieceOf.TryGetValue(root, out piece))
                {
                    piece = pieceOf.Count;
                    pieceOf[root] = piece;
                }
                for (int k = i; k < i + 3; k++)
                {
                    m.material[k] = to;
                    m.colors[k] = colors[piece % colors.Count];
                    m.tinted[k] = false;
                }
            }
            m.id = nextId++;
            return m;
        }

        // A copy of a model in which the faces of the named materials take the colour it is drawn with
        // (white, tinted) and no other face does (a mechanoid's faction marks); the source colour of
        // the first such material goes to baseColor. Null if the model uses none of them.
        public static Model Marked(Model src, List<string> names, out Color baseColor)
        {
            baseColor = Color.white;
            if (src == null || src.materials == null || src.material == null || names == null || names.Count == 0)
            {
                return null;
            }
            bool[] marked = new bool[src.materials.Length];
            bool any = false;
            for (int i = 0; i < src.materials.Length; i++)
            {
                for (int k = 0; k < names.Count; k++)
                {
                    if (string.Equals(src.materials[i].name, names[k], StringComparison.OrdinalIgnoreCase))
                    {
                        marked[i] = true;
                    }
                }
            }
            Model m = new Model();
            m.positions = src.positions;
            m.normals = src.normals;
            m.uvs = src.uvs;
            m.hull = src.hull;
            m.beam = src.beam;   // (Api 33) the beam part as it was
            m.noOutline = src.noOutline;
            m.maxY = src.maxY;
            m.halfDepth = src.halfDepth;
            m.minX = src.minX;
            m.maxX = src.maxX;
            m.minZ = src.minZ;
            m.maxZ = src.maxZ;
            m.materials = src.materials;
            m.material = src.material;
            m.colors = (Color[])src.colors.Clone();
            m.tinted = new bool[src.tinted.Length];
            for (int i = 0; i < m.colors.Length; i++)
            {
                if (marked[src.material[i]])
                {
                    if (!any)
                    {
                        baseColor = src.colors[i];
                        any = true;
                    }
                    m.colors[i] = Color.white;
                    m.tinted[i] = true;
                }
            }
            if (!any)
            {
                return null;
            }
            m.id = nextId++;
            return m;
        }

        // Like Marked, but the named materials' faces also give off light (unlit), in the colour the model is
        // drawn with: an entity's glowing parts (the nociosphere's red core and seams, by its activity).
        public static Model Glowing(Model src, List<string> names, out Color baseColor)
        {
            Model m = Marked(src, names, out baseColor);
            if (m == null)
            {
                return null;
            }
            ModelMaterial lit = new ModelMaterial();
            lit.name = "glow_entity";
            lit.glow = true;
            lit.color = Color.white;
            List<ModelMaterial> mats = new List<ModelMaterial>(src.materials);
            mats.Add(lit);
            int to = mats.Count - 1;
            m.materials = mats.ToArray();
            m.material = (int[])src.material.Clone();
            for (int i = 0; i < m.material.Length; i++)
            {
                if (m.tinted[i])
                {
                    m.material[i] = to;
                }
            }
            return m;
        }

        // A copy of a model whose glowing faces (glow*, flame*, ember*) are switched off: shaded like the rest, in a dark
        // shade of their own colour (a powered-down panel). The source itself if it has none.
        public static Model GlowOff(Model src)
        {
            return GlowOff(src, GlowOffShade, false);
        }

        // The same, the switched-off faces in shade times their own colour; keepFire leaves flames and embers
        // (ModelMaterial.fire) glowing, for the fuel to rule (ModelBaker.NoFire). An unlit lamp: shade 1, keepFire.
        public static Model GlowOff(Model src, float shade, bool keepFire)
        {
            if (src == null || src.materials == null || src.material == null
                || (!Array.Exists(src.materials, mm => mm.glow && !(keepFire && mm.fire)) && src.beam == null))
            {
                return src;
            }
            Model m = Merge(new List<Model> { src });   // an own copy with its own id (meshes are cached by id)
            m.beam = null;   // (Api 33) beam parts go out with the glowing parts
            for (int i = 0; i < m.colors.Length; i++)
            {
                ModelMaterial mm = m.materials[m.material[i]];
                if (mm.glow && !(keepFire && mm.fire))
                {
                    Color c = m.colors[i];
                    m.colors[i] = new Color(c.r * shade, c.g * shade, c.b * shade, c.a);
                }
            }
            ModelMaterial[] mats = new ModelMaterial[m.materials.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                ModelMaterial o = m.materials[i];
                mats[i] = !o.glow || (keepFire && o.fire) ? o : new ModelMaterial
                {
                    name = o.name, texture = o.texture, tinted = o.tinted, sheet = o.sheet, leaf = o.leaf, twig = o.twig, glow = false, fire = o.fire,
                    color = o.color * shade
                };
            }
            m.materials = mats;
            return m;
        }

        private const float GlowOffShade = 0.3f;

        private static readonly Color Rust = new Color(0.42f, 0.24f, 0.13f);
        private static readonly Color RustDark = new Color(0.27f, 0.16f, 0.10f);

        // How worn a worn copy looks: used but kept clean, as the game's own Ancient sprites (pale, unrusted), with only
        // faint wear left (rust share about 0.02 of the surface).
        private const float RustFrom = 0.72f;    // noise level where rust flecks start (higher: fewer, smaller flecks)
        private const float RustMix = 0.45f;     // how far a full fleck goes to its colour
        private const float RustBrown = 0.6f;    // fleck colour: 1 = rust brown, 0 = the face's own dark grime
        private const float WornShade = 0.86f;   // the face's brightness: WornShade + WornShadeVar * fine noise
        private const float WornShadeVar = 0.07f;
        private const float WornDull = 0.16f;    // how far the face goes toward its own brown-grey

        // A worn copy of a model (Ancient mechanoids): every face a little darker and duller, with faint rust flecks
        // where a smooth noise over the model's own space is high. offset (in model cells) shifts the noise, so
        // copies made with different offsets rust in different places; the same offset always rusts the same
        // way. Faces whose material is in keep (lenses, faction marks), glowing faces and faces tinted by the
        // thing's colour are left as they are.
        public static Model Weathered(Model src, Vector3 offset, List<string> keep)
        {
            if (src == null || src.materials == null || src.material == null)
            {
                return src;
            }
            Model m = new Model();
            m.positions = src.positions;
            m.normals = src.normals;
            m.uvs = src.uvs;
            m.hull = src.hull;
            m.beam = src.beam;   // (Api 33) the beam part as it was
            m.noOutline = src.noOutline;
            m.maxY = src.maxY;
            m.halfDepth = src.halfDepth;
            m.minX = src.minX;
            m.maxX = src.maxX;
            m.minZ = src.minZ;
            m.maxZ = src.maxZ;
            m.materials = src.materials;
            m.material = src.material;
            m.tinted = src.tinted;
            m.colors = (Color[])src.colors.Clone();
            int n = src.positions.Length;
            for (int i = 0; i + 2 < n; i += 3)
            {
                ModelMaterial mm = src.materials[src.material[i]];
                if (mm.glow || src.tinted[i] || (keep != null && keep.Exists(k => string.Equals(k, mm.name, StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }
                Vector3 c = (src.positions[i] + src.positions[i + 1] + src.positions[i + 2]) / 3f + offset;
                float rust = Mathf.Clamp01((Blotch(c * 5.5f) - RustFrom) * 4f);   // flecks
                float grime = Blotch(c * 13f + new Vector3(7.1f, 3.3f, 1.9f));     // fine variation
                for (int k = i; k < i + 3; k++)
                {
                    Color col = src.colors[k];
                    float lum = col.r * 0.3f + col.g * 0.59f + col.b * 0.11f;
                    // Darker, a little desaturated toward a brown-grey.
                    Color worn = Color.Lerp(col, new Color(lum, lum * 0.95f, lum * 0.86f), WornDull) * (WornShade + WornShadeVar * grime);
                    Color rusty = Color.Lerp(worn * 0.62f, Color.Lerp(Rust, RustDark, grime) * Mathf.Lerp(0.7f, 1.1f, lum), RustBrown);
                    Color o = Color.Lerp(worn, rusty, rust * RustMix);
                    o.a = col.a;
                    m.colors[k] = o;
                }
            }
            m.id = nextId++;
            return m;
        }

        // Smooth value noise, 0..1.
        private static float Blotch(Vector3 p)
        {
            Vector3 f = new Vector3(Mathf.Floor(p.x), Mathf.Floor(p.y), Mathf.Floor(p.z));
            Vector3 t = p - f;
            t = new Vector3(t.x * t.x * (3f - 2f * t.x), t.y * t.y * (3f - 2f * t.y), t.z * t.z * (3f - 2f * t.z));
            float v = 0f;
            for (int dz = 0; dz < 2; dz++)
            {
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        float w = (dx == 1 ? t.x : 1f - t.x) * (dy == 1 ? t.y : 1f - t.y) * (dz == 1 ? t.z : 1f - t.z);
                        v += w * Hash01((int)f.x + dx, (int)f.y + dy, (int)f.z + dz);
                    }
                }
            }
            return v;
        }

        private static float Hash01(int x, int y, int z)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }

        // Several models in one: their triangles one after another, each keeping its own materials.
        public static Model Merge(List<Model> models)
        {
            List<Vector3> pos = new List<Vector3>(), nor = new List<Vector3>(), hull = new List<Vector3>();
            List<Color> col = new List<Color>();
            List<bool> tint = new List<bool>();
            List<Vector2> uv = new List<Vector2>();
            List<int> mat = new List<int>();
            List<ModelMaterial> mats = new List<ModelMaterial>();
            foreach (Model src in models)
            {
                int offset = mats.Count;
                mats.AddRange(src.materials);
                pos.AddRange(src.positions);
                nor.AddRange(src.normals);
                col.AddRange(src.colors);
                tint.AddRange(src.tinted);
                uv.AddRange(src.uvs);
                hull.AddRange(src.hull);
                for (int i = 0; i < src.material.Length; i++)
                {
                    mat.Add(src.material[i] + offset);
                }
            }
            Model m = new Model();
            m.positions = pos.ToArray();
            m.normals = nor.ToArray();
            m.colors = col.ToArray();
            m.tinted = tint.ToArray();
            m.uvs = uv.ToArray();
            m.hull = hull.ToArray();
            m.beam = Beams.Merged(models);   // (Api 33) their beam parts together
            m.material = mat.ToArray();
            m.materials = mats.ToArray();
            m.minX = float.MaxValue;
            m.minZ = float.MaxValue;
            m.maxX = float.MinValue;
            m.maxZ = float.MinValue;
            foreach (Model src in models)
            {
                m.maxY = Mathf.Max(m.maxY, src.maxY);
                m.halfDepth = Mathf.Max(m.halfDepth, src.halfDepth);
                m.minX = Mathf.Min(m.minX, src.minX);
                m.maxX = Mathf.Max(m.maxX, src.maxX);
                m.minZ = Mathf.Min(m.minZ, src.minZ);
                m.maxZ = Mathf.Max(m.maxZ, src.maxZ);
            }
            if (models.Exists(src => src.noOutline != null))
            {
                List<bool> none = new List<bool>();
                foreach (Model src in models)
                {
                    none.AddRange(src.noOutline ?? new bool[src.positions.Length]);
                }
                m.noOutline = none.ToArray();
            }
            m.id = nextId++;
            return m;
        }

        // The model with no outline on its small separate pieces (corners shared by position make
        // one piece): every piece whose box is under size cells on its largest side, and every flat
        // sheet (under 0.01 cells on its smallest side; a sheet of faces gets a broken outline). A chess
        // table keeps the outline of its top and legs but not of its pieces and squares.
        // The model mirrored left to right (x -> -x), each triangle turned round so it still faces outward (wings: Api 14).
        public static Model Mirrored(Model src)
        {
            int n = src.positions.Length;
            Model m = new Model();
            m.positions = new Vector3[n];
            m.normals = src.normals != null ? new Vector3[n] : null;
            m.hull = src.hull != null ? new Vector3[n] : null;
            m.beam = Beams.Moved(src.beam, p => new Vector3(-p.x, p.y, p.z), q => new Vector3(-q.x, q.y, q.z));   // (Api 33)
            m.colors = src.colors != null ? new Color[n] : null;
            m.tinted = src.tinted != null ? new bool[n] : null;
            m.uvs = src.uvs != null ? new Vector2[n] : null;
            m.material = src.material != null ? new int[n] : null;
            m.noOutline = src.noOutline != null ? new bool[n] : null;
            for (int i = 0; i < n; i++)
            {
                int j = i % 3 == 1 ? i + 1 : i % 3 == 2 ? i - 1 : i;   // corners 1 and 2 swapped: the winding stays outward
                if (j >= n)
                {
                    j = i;
                }
                Vector3 p = src.positions[j];
                m.positions[i] = new Vector3(-p.x, p.y, p.z);
                if (m.normals != null) { Vector3 q = src.normals[j]; m.normals[i] = new Vector3(-q.x, q.y, q.z); }
                if (m.hull != null) { Vector3 q = src.hull[j]; m.hull[i] = new Vector3(-q.x, q.y, q.z); }
                if (m.colors != null) m.colors[i] = src.colors[j];
                if (m.tinted != null) m.tinted[i] = src.tinted[j];
                if (m.uvs != null) m.uvs[i] = src.uvs[j];
                if (m.material != null) m.material[i] = src.material[j];
                if (m.noOutline != null) m.noOutline[i] = src.noOutline[j];
            }
            m.materials = src.materials;
            m.maxY = src.maxY;
            m.halfDepth = src.halfDepth;
            m.minX = -src.maxX;
            m.maxX = -src.minX;
            m.minZ = src.minZ;
            m.maxZ = src.maxZ;
            m.id = nextId++;
            return m;
        }

        // (Api 24) The model without the faces of the named materials (case-insensitive): a crop's young stage made from its
        // mature model, its produce left out (ModelDef.immatureHide). The source itself if it uses none of them; null if
        // nothing would be left.
        public static Model WithoutMaterials(Model src, List<string> names)
        {
            if (src == null || src.materials == null || src.material == null || names == null || names.Count == 0)
            {
                return src;
            }
            bool[] drop = new bool[src.materials.Length];
            bool any = false;
            for (int i = 0; i < src.materials.Length; i++)
            {
                drop[i] = names.Exists(x => string.Equals(x, src.materials[i].name, StringComparison.OrdinalIgnoreCase));
                any |= drop[i];
            }
            if (!any)
            {
                return src;
            }
            int n = src.positions.Length;
            List<int> keep = new List<int>(n);
            for (int i = 0; i + 2 < n; i += 3)
            {
                if (!drop[src.material[i]])
                {
                    keep.Add(i);
                    keep.Add(i + 1);
                    keep.Add(i + 2);
                }
            }
            if (keep.Count == 0)
            {
                return null;
            }
            int k = keep.Count;
            Model m = new Model();
            m.positions = new Vector3[k];
            m.normals = src.normals != null ? new Vector3[k] : null;
            m.hull = src.hull != null ? new Vector3[k] : null;
            m.beam = src.beam;   // (Api 33) the beam part as it was
            m.colors = src.colors != null ? new Color[k] : null;
            m.tinted = src.tinted != null ? new bool[k] : null;
            m.uvs = src.uvs != null ? new Vector2[k] : null;
            m.material = new int[k];
            m.noOutline = src.noOutline != null ? new bool[k] : null;
            m.minX = m.minZ = float.MaxValue;
            m.maxX = m.maxZ = float.MinValue;
            for (int i = 0; i < k; i++)
            {
                int j = keep[i];
                Vector3 p = src.positions[j];
                m.positions[i] = p;
                if (m.normals != null) m.normals[i] = src.normals[j];
                if (m.hull != null) m.hull[i] = src.hull[j];
                if (m.colors != null) m.colors[i] = src.colors[j];
                if (m.tinted != null) m.tinted[i] = src.tinted[j];
                if (m.uvs != null) m.uvs[i] = src.uvs[j];
                m.material[i] = src.material[j];
                if (m.noOutline != null) m.noOutline[i] = src.noOutline[j];
                m.maxY = Mathf.Max(m.maxY, p.y);
                m.halfDepth = Mathf.Max(m.halfDepth, Mathf.Abs(p.z));
                m.minX = Mathf.Min(m.minX, p.x);
                m.maxX = Mathf.Max(m.maxX, p.x);
                m.minZ = Mathf.Min(m.minZ, p.z);
                m.maxZ = Mathf.Max(m.maxZ, p.z);
            }
            m.materials = src.materials;
            m.id = nextId++;
            return m;
        }

        // (Api 24) A copy of a model whose "sheet" faces (ModelMaterial.sheet) are multiplied by two, for drawing paths that cache
        // ready-made meshes by model and one tint (DynamicModels): the copy's sheet faces carry the colour, so they are not
        // sheet faces any more. Copies are kept per model and colour (rounded as DynamicModels rounds its tints).
        public static Model SheetTinted(Model src, Color two)
        {
            if (src == null || src.material == null || src.colors == null || !R3DTextures.HasSheet(src)
                || (two.r >= 0.98f && two.g >= 0.98f && two.b >= 0.98f))
            {
                return src;   // (white: the sheet faces keep their own colour)
            }
            Color32 t = two;
            t.r = (byte)(t.r & 0xF8);
            t.g = (byte)(t.g & 0xF8);
            t.b = (byte)(t.b & 0xF8);
            long key = ((long)src.id << 24) | (uint)((t.r << 16) | (t.g << 8) | t.b);
            Model m;
            if (sheetTinted.TryGetValue(key, out m))
            {
                return m;
            }
            Color c2 = new Color(t.r / 255f, t.g / 255f, t.b / 255f, 1f);
            m = new Model();
            m.positions = src.positions;
            m.normals = src.normals;
            m.uvs = src.uvs;
            m.hull = src.hull;
            m.beam = src.beam;   // (Api 33) the beam part as it was
            m.noOutline = src.noOutline;
            m.tinted = src.tinted;
            m.material = src.material;
            m.maxY = src.maxY;
            m.halfDepth = src.halfDepth;
            m.minX = src.minX;
            m.maxX = src.maxX;
            m.minZ = src.minZ;
            m.maxZ = src.maxZ;
            m.materials = new ModelMaterial[src.materials.Length];
            for (int i = 0; i < src.materials.Length; i++)
            {
                ModelMaterial o = src.materials[i];
                m.materials[i] = !o.sheet ? o : new ModelMaterial
                {
                    name = o.name, texture = o.texture, tinted = o.tinted, sheet = false, leaf = o.leaf, twig = o.twig, glow = o.glow, fire = o.fire,
                    color = o.color * c2
                };
            }
            m.colors = (Color[])src.colors.Clone();
            for (int i = 0; i < m.colors.Length; i++)
            {
                if (src.materials[src.material[i]].sheet)
                {
                    Color c = m.colors[i];
                    m.colors[i] = new Color(c.r * c2.r, c.g * c2.g, c.b * c2.b, c.a);
                }
            }
            m.id = nextId++;
            if (sheetTinted.Count > 512)
            {
                sheetTinted.Clear();
            }
            sheetTinted[key] = m;
            return m;
        }

        private static readonly Dictionary<long, Model> sheetTinted = new Dictionary<long, Model>();

        public static Model WithoutSmallOutlines(Model src, float size)
        {
            int n = src.positions.Length;
            List<int> parent = new List<int>();
            Dictionary<Vector3Int, int> corner = new Dictionary<Vector3Int, int>();
            for (int i = 0; i + 2 < n; i += 3)
            {
                int t = i / 3;
                parent.Add(t);
                for (int k = i; k < i + 3; k++)
                {
                    Vector3Int key = HullKey(src.positions[k]);
                    int other;
                    if (corner.TryGetValue(key, out other))
                    {
                        int a = Root(parent, t), b = Root(parent, other);
                        if (a != b)
                        {
                            parent[a] = b;
                        }
                    }
                    else
                    {
                        corner[key] = t;
                    }
                }
            }
            Dictionary<int, Bounds> boxes = new Dictionary<int, Bounds>();
            for (int i = 0; i + 2 < n; i += 3)
            {
                int r = Root(parent, i / 3);
                Bounds b;
                if (!boxes.TryGetValue(r, out b))
                {
                    b = new Bounds(src.positions[i], Vector3.zero);
                }
                for (int k = i; k < i + 3; k++)
                {
                    b.Encapsulate(src.positions[k]);
                }
                boxes[r] = b;
            }
            bool[] none = src.noOutline != null ? (bool[])src.noOutline.Clone() : new bool[n];
            for (int i = 0; i + 2 < n; i += 3)
            {
                Vector3 s = boxes[Root(parent, i / 3)].size;
                if (Mathf.Max(s.x, Mathf.Max(s.y, s.z)) < size || Mathf.Min(s.x, Mathf.Min(s.y, s.z)) < 0.01f)
                {
                    none[i] = none[i + 1] = none[i + 2] = true;
                }
            }

            Model m = new Model();
            m.positions = src.positions;
            m.normals = src.normals;
            m.colors = src.colors;
            m.tinted = src.tinted;
            m.uvs = src.uvs;
            m.material = src.material;
            m.materials = src.materials;
            m.hull = src.hull;
            m.beam = src.beam;   // (Api 33) the beam part as it was
            m.noOutline = none;
            m.maxY = src.maxY;
            m.halfDepth = src.halfDepth;
            m.minX = src.minX;
            m.maxX = src.maxX;
            m.minZ = src.minZ;
            m.maxZ = src.maxZ;
            m.id = nextId++;
            return m;
        }

        private static int Root(List<int> parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        public static Model Load(string path)
        {
            if (!File.Exists(path))
            {
                ModLog.Warn("[IsoRim] Model file not found: " + path);
                return null;
            }

            List<Vector3> pos = new List<Vector3>();
            List<Vector2> tex = new List<Vector2>();
            List<Color> vertColor = new List<Color>();
            List<bool> hasVertColor = new List<bool>();

            List<ModelMaterial> materials = new List<ModelMaterial>();
            Dictionary<string, int> materialIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            materials.Add(new ModelMaterial());   // used by faces that name no material
            int current = 0;

            List<Vector3> outPos = new List<Vector3>();
            List<Vector3> outNormal = new List<Vector3>();
            List<Color> outColor = new List<Color>();
            List<bool> outTint = new List<bool>();
            List<Vector2> outUV = new List<Vector2>();
            List<int> outMaterial = new List<int>();

            string dir = Path.GetDirectoryName(path);
            List<int> face = new List<int>();
            List<int> faceUV = new List<int>();

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] t = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

                if (t[0] == "mtllib" && t.Length > 1)
                {
                    ReadMaterials(Path.Combine(dir, line.Substring(6).Trim()), materials, materialIndex);
                }
                else if (t[0] == "usemtl")
                {
                    string name = t.Length > 1 ? t[1] : "";
                    if (!materialIndex.TryGetValue(name, out current))
                    {
                        current = AddMaterial(name, materials, materialIndex);
                    }
                }
                else if (t[0] == "v" && t.Length >= 4)
                {
                    // OBJ is right-handed; flipping Z turns it into the game's axes.
                    pos.Add(new Vector3(Num(t[1]), Num(t[2]), -Num(t[3])));
                    if (t.Length >= 7)
                    {
                        vertColor.Add(new Color(Num(t[4]), Num(t[5]), Num(t[6])));
                        hasVertColor.Add(true);
                    }
                    else
                    {
                        vertColor.Add(Color.white);
                        hasVertColor.Add(false);
                    }
                }
                else if (t[0] == "vt" && t.Length >= 3)
                {
                    tex.Add(new Vector2(Num(t[1]), Num(t[2])));
                }
                else if (t[0] == "f" && t.Length >= 4)
                {
                    // Each corner is written as position, position/texture or position/texture/normal.
                    face.Clear();
                    faceUV.Clear();
                    bool bad = false;
                    for (int i = 1; i < t.Length; i++)
                    {
                        string[] parts = t[i].Split('/');
                        int idx = Index(parts[0], pos.Count);
                        if (idx < 0)
                        {
                            bad = true;
                            break;
                        }
                        face.Add(idx);
                        faceUV.Add(parts.Length > 1 ? Index(parts[1], tex.Count) : -1);
                    }
                    if (bad)
                    {
                        continue;
                    }

                    ModelMaterial mat = materials[current];

                    // Split polygons into a fan of triangles.
                    for (int i = 1; i + 1 < face.Count; i++)
                    {
                        Vector3 a = pos[face[0]];
                        Vector3 b = pos[face[i]];
                        Vector3 c = pos[face[i + 1]];
                        Vector3 n = Vector3.Cross(b - a, c - a);
                        if (n.sqrMagnitude < 1e-12f)
                        {
                            continue;
                        }
                        n.Normalize();

                        for (int k = 0; k < 3; k++)
                        {
                            int corner = k == 0 ? 0 : (k == 1 ? i : i + 1);
                            int pi = face[corner];
                            int ti = faceUV[corner];
                            outPos.Add(pos[pi]);
                            outNormal.Add(n);
                            outColor.Add(hasVertColor[pi] ? vertColor[pi] : mat.color);
                            outTint.Add(mat.tinted);
                            outUV.Add(ti >= 0 ? tex[ti] : Vector2.zero);
                            outMaterial.Add(current);
                        }
                    }
                }
            }

            if (outPos.Count == 0)
            {
                ModLog.Warn("[IsoRim] Model has no faces: " + path);
                return null;
            }

            TurnRoundIfInsideOut(outPos, outNormal, outColor, outTint, outUV, outMaterial);
            // (Api 33) beam_* faces out of the model into its beam part: no hull, outline, shadow or glass for them
            Model beamPart = Beams.Split(outPos, outNormal, outColor, outTint, outUV, outMaterial, materials);

            Model m = new Model();
            m.beam = beamPart;
            m.positions = outPos.ToArray();
            m.normals = outNormal.ToArray();
            m.colors = outColor.ToArray();
            m.tinted = outTint.ToArray();
            m.uvs = outUV.ToArray();
            m.material = outMaterial.ToArray();
            m.materials = materials.ToArray();
            float maxY = 0f;
            for (int i = 0; i < m.positions.Length; i++)
            {
                maxY = Mathf.Max(maxY, m.positions[i].y);
            }
            m.maxY = maxY;
            float depth = 0f;
            for (int i = 0; i < m.positions.Length; i++)
            {
                depth = Mathf.Max(depth, Mathf.Abs(m.positions[i].z));
            }
            m.halfDepth = depth;
            m.minX = float.MaxValue;
            m.minZ = float.MaxValue;
            m.maxX = float.MinValue;
            m.maxZ = float.MinValue;
            for (int i = 0; i < m.positions.Length; i++)
            {
                Vector3 p = m.positions[i];
                m.minX = Mathf.Min(m.minX, p.x);
                m.maxX = Mathf.Max(m.maxX, p.x);
                m.minZ = Mathf.Min(m.minZ, p.z);
                m.maxZ = Mathf.Max(m.maxZ, p.z);
            }
            m.id = nextId++;
            m.hull = BuildHull(m.positions, m.normals, OutlineWidth);
            return m;
        }

        // Turns a 1-based OBJ index (negative counts back from the end) into a list index, or -1.
        private static int Index(string token, int count)
        {
            int idx;
            if (token.Length == 0 || !int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
            {
                return -1;
            }
            idx = idx < 0 ? count + idx : idx - 1;
            return idx >= 0 && idx < count ? idx : -1;
        }

        private static int AddMaterial(string name, List<ModelMaterial> materials, Dictionary<string, int> materialIndex)
        {
            ModelMaterial mat = new ModelMaterial();
            mat.name = name;
            mat.tinted = name.StartsWith("stuff", StringComparison.OrdinalIgnoreCase);
            mat.sheet = name.StartsWith("sheet", StringComparison.OrdinalIgnoreCase);
            mat.leaf = name.StartsWith("leaf", StringComparison.OrdinalIgnoreCase);
            mat.twig = name.StartsWith("twig", StringComparison.OrdinalIgnoreCase);
            mat.glow = name.StartsWith("flame", StringComparison.OrdinalIgnoreCase) || name.StartsWith("glow", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ember", StringComparison.OrdinalIgnoreCase);
            mat.fire = name.StartsWith("flame", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ember", StringComparison.OrdinalIgnoreCase);
            mat.color = mat.tinted ? Color.white : DefaultColor;
            materials.Add(mat);
            materialIndex[name] = materials.Count - 1;
            return materials.Count - 1;
        }

        // The mod's own model files wind each face clockwise seen from outside (in the file's
        // axes), so that after the Z flip the face normals point out. An OBJ written the usual way
        // (counter-clockwise, e.g. Blender's exporter) comes out inside out: the body still shows
        // (it is drawn from both sides) but its outline shell is pulled in and disappears. A
        // closed model whose faces enclose a clearly negative volume is turned round here.
        // (Open shapes such as grass blades enclose almost nothing and are left as they are.)
        private static void TurnRoundIfInsideOut(List<Vector3> pos, List<Vector3> normal, List<Color> color, List<bool> tint,
            List<Vector2> uv, List<int> material)
        {
            float volume = 0f;
            Vector3 min = pos[0];
            Vector3 max = pos[0];
            for (int i = 0; i + 2 < pos.Count; i += 3)
            {
                volume += Vector3.Dot(pos[i], Vector3.Cross(pos[i + 1], pos[i + 2])) / 6f;
            }
            for (int i = 0; i < pos.Count; i++)
            {
                min = Vector3.Min(min, pos[i]);
                max = Vector3.Max(max, pos[i]);
            }
            Vector3 size = max - min;
            // A thin or airy closed model (a drape, a lattice mast) encloses little of its box, so a
            // negative volume alone is not enough to tell it from an open shape; a closed one (every
            // edge shared by a face running the other way) is turned round whenever its volume is negative.
            if (volume >= 0f || (volume >= -0.1f * size.x * size.y * size.z && !IsClosed(pos)))
            {
                return;
            }
            for (int i = 0; i + 2 < pos.Count; i += 3)
            {
                Swap(pos, i + 1, i + 2);
                Swap(color, i + 1, i + 2);
                Swap(tint, i + 1, i + 2);
                Swap(uv, i + 1, i + 2);
                Swap(material, i + 1, i + 2);
                for (int k = i; k < i + 3; k++)
                {
                    normal[k] = -normal[k];
                }
            }
        }

        // True when every triangle edge (corners matched by position) is also an edge of a triangle
        // that runs along it the other way, i.e. the faces close round a solid.
        private static bool IsClosed(List<Vector3> pos)
        {
            HashSet<(Vector3Int, Vector3Int)> edges = new HashSet<(Vector3Int, Vector3Int)>();
            for (int i = 0; i + 2 < pos.Count; i += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    edges.Add((HullKey(pos[i + k]), HullKey(pos[i + (k + 1) % 3])));
                }
            }
            foreach ((Vector3Int a, Vector3Int b) in edges)
            {
                if (!edges.Contains((b, a)))
                {
                    return false;
                }
            }
            return true;
        }

        private static void Swap<T>(List<T> list, int a, int b)
        {
            T t = list[a];
            list[a] = list[b];
            list[b] = t;
        }

        // A copy of a model with every corner moved by point and every normal by normal (a turn and stretch that keeps
        // faces the same way out); colours, materials and outline choices kept, its box and outline worked out anew.
        public static Model Transformed(Model src, Func<Vector3, Vector3> point, Func<Vector3, Vector3> normal)
        {
            int n = src.positions.Length;
            Model m = new Model();
            m.positions = new Vector3[n];
            m.normals = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                m.positions[i] = point(src.positions[i]);
                m.normals[i] = normal(src.normals[i]);
            }
            m.colors = src.colors;
            m.tinted = src.tinted;
            m.uvs = src.uvs;
            m.material = src.material;
            m.materials = src.materials;
            m.noOutline = src.noOutline;
            m.minX = float.MaxValue;
            m.minZ = float.MaxValue;
            m.maxX = float.MinValue;
            m.maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = m.positions[i];
                m.maxY = Mathf.Max(m.maxY, p.y);
                m.halfDepth = Mathf.Max(m.halfDepth, Mathf.Abs(p.z));
                m.minX = Mathf.Min(m.minX, p.x);
                m.maxX = Mathf.Max(m.maxX, p.x);
                m.minZ = Mathf.Min(m.minZ, p.z);
                m.maxZ = Mathf.Max(m.maxZ, p.z);
            }
            m.hull = BuildHull(m.positions, m.normals, OutlineWidth);
            m.beam = Beams.Moved(src.beam, point, normal);   // (Api 33)
            m.id = nextId++;
            return m;
        }

        // A copy of a model without the faces whose material drop picks (a worn torch's flame* faces, where IsoRim's live
        // flame burns instead: ApparelModels); each kept face keeps its colour and outline. Null if nothing is left.
        public static Model Without(Model src, Func<ModelMaterial, bool> drop)
        {
            if (src == null || src.materials == null || src.material == null)
            {
                return src;
            }
            List<int> keep = new List<int>();
            for (int i = 0; i + 2 < src.positions.Length; i += 3)
            {
                if (!drop(src.materials[src.material[i]]))
                {
                    keep.Add(i);
                }
            }
            if (keep.Count == 0)
            {
                return null;
            }
            int n = keep.Count * 3;
            Model m = new Model();
            m.positions = new Vector3[n];
            m.normals = new Vector3[n];
            m.colors = new Color[n];
            m.tinted = new bool[n];
            m.uvs = new Vector2[n];
            m.material = new int[n];
            m.hull = new Vector3[n];
            m.beam = src.beam;   // (Api 33) the beam part as it was
            m.noOutline = src.noOutline != null ? new bool[n] : null;
            m.materials = src.materials;
            int k = 0;
            foreach (int i in keep)
            {
                for (int v = i; v < i + 3; v++, k++)
                {
                    m.positions[k] = src.positions[v];
                    m.normals[k] = src.normals[v];
                    m.colors[k] = src.colors[v];
                    m.tinted[k] = src.tinted[v];
                    m.uvs[k] = src.uvs != null && v < src.uvs.Length ? src.uvs[v] : Vector2.zero;
                    m.material[k] = src.material[v];
                    m.hull[k] = src.hull != null ? src.hull[v] : Vector3.zero;
                    if (m.noOutline != null)
                    {
                        m.noOutline[k] = src.noOutline[v];
                    }
                }
            }
            m.minX = float.MaxValue;
            m.minZ = float.MaxValue;
            m.maxX = float.MinValue;
            m.maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = m.positions[i];
                m.maxY = Mathf.Max(m.maxY, p.y);
                m.halfDepth = Mathf.Max(m.halfDepth, Mathf.Abs(p.z));
                m.minX = Mathf.Min(m.minX, p.x);
                m.maxX = Mathf.Max(m.maxX, p.x);
                m.minZ = Mathf.Min(m.minZ, p.z);
                m.maxZ = Mathf.Max(m.maxZ, p.z);
            }
            m.id = nextId++;
            return m;
        }

        // The part of a model below height top (model space): every face cut off there, nothing squeezed. lid: a flat
        // slab (the model's stuff colour) over the model's whole ground box closes the cut at that height. Its own
        // outline. Null if nothing is left.
        public static Model ClipBelow(Model src, float top, bool lid)
        {
            if (src == null || src.positions == null)
            {
                return null;
            }
            List<Vector3> pos = new List<Vector3>(), nor = new List<Vector3>();
            List<Color> col = new List<Color>();
            List<bool> tint = new List<bool>(), none = src.noOutline != null ? new List<bool>() : null;
            List<Vector2> uv = new List<Vector2>();
            List<int> mat = new List<int>();
            int n = src.positions.Length;
            int[] poly = new int[4];
            float[] polyT = new float[4];
            int[] polyB = new int[4];
            for (int i = 0; i + 2 < n; i += 3)
            {
                // Sutherland-Hodgman against y <= top: a corner is (a, b, t), the point t of the way from corner a to b.
                int count = 0;
                for (int k = 0; k < 3; k++)
                {
                    int a = i + k;
                    int b = i + (k + 1) % 3;
                    bool ina = src.positions[a].y <= top;
                    bool inb = src.positions[b].y <= top;
                    if (ina)
                    {
                        poly[count] = a;
                        polyB[count] = a;
                        polyT[count] = 0f;
                        count++;
                    }
                    if (ina != inb)
                    {
                        float ya = src.positions[a].y;
                        float yb = src.positions[b].y;
                        poly[count] = a;
                        polyB[count] = b;
                        polyT[count] = Mathf.Clamp01((top - ya) / (yb - ya));
                        count++;
                    }
                }
                for (int k = 1; k + 1 < count; k++)
                {
                    foreach (int j in new[] { 0, k, k + 1 })
                    {
                        int a = poly[j];
                        int b = polyB[j];
                        float t = polyT[j];
                        pos.Add(Vector3.Lerp(src.positions[a], src.positions[b], t));
                        Vector3 nn = Vector3.Lerp(src.normals[a], src.normals[b], t);
                        nor.Add(nn.sqrMagnitude > 1e-8f ? nn.normalized : src.normals[a]);
                        col.Add(Color.Lerp(src.colors[a], src.colors[b], t));
                        tint.Add(src.tinted[i]);
                        uv.Add(Vector2.Lerp(src.uvs[a], src.uvs[b], t));
                        mat.Add(src.material[i]);
                        if (none != null)
                        {
                            none.Add(src.noOutline[i]);
                        }
                    }
                }
            }
            List<ModelMaterial> mats = new List<ModelMaterial>(src.materials);
            if (lid && top > 0.05f && src.maxX > src.minX && src.maxZ > src.minZ)
            {
                Model box = Box(new Vector3(src.minX, top - 0.06f, src.minZ), new Vector3(src.maxX, top, src.maxZ));
                int offset = mats.Count;
                mats.AddRange(box.materials);
                for (int i = 0; i < box.positions.Length; i++)
                {
                    pos.Add(box.positions[i]);
                    nor.Add(box.normals[i]);
                    col.Add(box.colors[i]);
                    tint.Add(box.tinted[i]);
                    uv.Add(box.uvs[i]);
                    mat.Add(box.material[i] + offset);
                    none?.Add(false);
                }
            }
            if (pos.Count == 0)
            {
                return null;
            }
            Model m = new Model();
            m.positions = pos.ToArray();
            m.normals = nor.ToArray();
            m.colors = col.ToArray();
            m.tinted = tint.ToArray();
            m.uvs = uv.ToArray();
            m.material = mat.ToArray();
            m.materials = mats.ToArray();
            m.noOutline = none?.ToArray();
            m.minX = float.MaxValue;
            m.minZ = float.MaxValue;
            m.maxX = float.MinValue;
            m.maxZ = float.MinValue;
            for (int i = 0; i < m.positions.Length; i++)
            {
                Vector3 p = m.positions[i];
                m.maxY = Mathf.Max(m.maxY, p.y);
                m.halfDepth = Mathf.Max(m.halfDepth, Mathf.Abs(p.z));
                m.minX = Mathf.Min(m.minX, p.x);
                m.maxX = Mathf.Max(m.maxX, p.x);
                m.minZ = Mathf.Min(m.minZ, p.z);
                m.maxZ = Mathf.Max(m.maxZ, p.z);
            }
            m.hull = BuildHull(m.positions, m.normals, OutlineWidth);
            m.id = nextId++;
            return m;
        }

        // A plain box from min to max (game axes), coloured by the tint it is drawn with (its top,
        // if given, a fixed colour instead), with the same outline as a loaded model. Used for the
        // books on a bookcase's shelves.
        public static Model Box(Vector3 min, Vector3 max, Color? top = null)
        {
            Vector3[] n = { Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.right, Vector3.left };
            List<Vector3> pos = new List<Vector3>(36);
            List<Vector3> nor = new List<Vector3>(36);
            for (int f = 0; f < 6; f++)
            {
                Vector3 normal = n[f];
                // Two axes across the face and the corners of the face on its side of the box.
                Vector3 u = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.up;
                Vector3 v = Vector3.Cross(normal, u);
                Vector3[] q = new Vector3[4];
                for (int k = 0; k < 4; k++)
                {
                    float su = k == 1 || k == 2 ? 1f : -1f;
                    float sv = k >= 2 ? 1f : -1f;
                    Vector3 d = normal + u * su + v * sv;   // a corner direction from the middle, each component -1 or 1
                    q[k] = new Vector3(d.x > 0f ? max.x : min.x, d.y > 0f ? max.y : min.y, d.z > 0f ? max.z : min.z);
                }
                // Clockwise seen from outside (the game's front faces): the cross product points out.
                if (Vector3.Dot(Vector3.Cross(q[1] - q[0], q[2] - q[0]), normal) < 0f)
                {
                    Vector3 t = q[1];
                    q[1] = q[3];
                    q[3] = t;
                }
                int[] order = { 0, 1, 2, 0, 2, 3 };
                for (int k = 0; k < 6; k++)
                {
                    pos.Add(q[order[k]]);
                    nor.Add(normal);
                }
            }
            Model m = new Model();
            m.positions = pos.ToArray();
            m.normals = nor.ToArray();
            m.colors = new Color[pos.Count];
            m.tinted = new bool[pos.Count];
            m.uvs = new Vector2[pos.Count];
            m.material = new int[pos.Count];
            ModelMaterial mat = new ModelMaterial();
            mat.name = "stuff";
            mat.tinted = true;
            mat.color = Color.white;
            m.materials = new[] { mat };
            for (int i = 0; i < pos.Count; i++)
            {
                bool lid = top.HasValue && nor[i].y > 0.5f;
                m.colors[i] = lid ? top.Value : Color.white;
                m.tinted[i] = !lid;
            }
            m.maxY = max.y;
            m.halfDepth = Mathf.Max(Mathf.Abs(min.z), Mathf.Abs(max.z));
            m.minX = min.x;
            m.maxX = max.x;
            m.minZ = min.z;
            m.maxZ = max.z;
            m.id = nextId++;
            m.hull = BuildHull(m.positions, m.normals, OutlineWidth);
            return m;
        }

        private static Vector3Int HullKey(Vector3 p)
        {
            return new Vector3Int(Mathf.RoundToInt(p.x * 10000f), Mathf.RoundToInt(p.y * 10000f), Mathf.RoundToInt(p.z * 10000f));
        }

        // Each welded corner moves by the shortest offset that puts every face meeting there at
        // least `width` further out (offset · normal >= width for each distinct face normal): the
        // true mitre, whatever number of triangles each side has. Where that offset would be longer
        // than MitreLimit × width (sharp corners) it is cut to that, so a tip gets a thinner line
        // instead of a spike. Where no such offset exists (faces back to back: thin sheets, needle
        // tips) the corner moves width along the mean normal.
        private static Vector3[] BuildHull(Vector3[] positions, Vector3[] normals, float width)
        {
            Dictionary<Vector3Int, List<Vector3>> facesAt = new Dictionary<Vector3Int, List<Vector3>>();
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3Int key = HullKey(positions[i]);
                List<Vector3> list;
                if (!facesAt.TryGetValue(key, out list))
                {
                    list = new List<Vector3>();
                    facesAt[key] = list;
                }
                Vector3 n = normals[i];
                if (positions[i].y < 0.001f)
                {
                    n.y = 0f;   // corners on the ground only move sideways
                    if (n.sqrMagnitude < 0.01f)
                    {
                        continue;
                    }
                    n.Normalize();
                }
                bool known = false;
                for (int k = 0; k < list.Count; k++)
                {
                    if (Vector3.Dot(list[k], n) > 0.999f)
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                {
                    list.Add(n);
                }
            }

            Dictionary<Vector3Int, Vector3> offsetAt = new Dictionary<Vector3Int, Vector3>();
            foreach (KeyValuePair<Vector3Int, List<Vector3>> kv in facesAt)
            {
                List<Vector3> l = kv.Value;
                Vector3 d;
                if (!ShortestMitre(l, out d))
                {
                    Vector3 sum = Vector3.zero;
                    for (int k = 0; k < l.Count; k++)
                    {
                        sum += l[k];
                    }
                    d = sum.sqrMagnitude < 1e-6f ? Vector3.zero : sum.normalized;
                }
                if (d.magnitude > MitreLimit)
                {
                    d = d.normalized * MitreLimit;
                }
                offsetAt[kv.Key] = d * width;
            }

            Vector3[] hull = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                hull[i] = offsetAt[HullKey(positions[i])];
            }
            return hull;
        }

        // The shortest d with d · n >= 1 for every n (unit normals), if there is one. The answer
        // touches at most three of the planes d · n = 1, so every set of one to three is tried.
        private static bool ShortestMitre(List<Vector3> n, out Vector3 best)
        {
            best = Vector3.zero;
            float bestSq = float.MaxValue;
            int c = n.Count;
            if (c == 0)
            {
                return false;
            }
            for (int a = 0; a < c; a++)
            {
                TryMitre(n, n[a], ref best, ref bestSq);
                for (int b = a + 1; b < c; b++)
                {
                    float ab = Vector3.Dot(n[a], n[b]);
                    if (ab > -0.9999f && ab < 0.9999f)
                    {
                        TryMitre(n, (n[a] + n[b]) / (1f + ab), ref best, ref bestSq);
                    }
                    for (int e = b + 1; e < c; e++)
                    {
                        // d = x n[a] + y n[b] + z n[e] with d · each = 1: Gram system.
                        float ae = Vector3.Dot(n[a], n[e]), be = Vector3.Dot(n[b], n[e]);
                        Vector3 r0 = new Vector3(1f, ab, ae), r1 = new Vector3(ab, 1f, be), r2 = new Vector3(ae, be, 1f);
                        float det = Vector3.Dot(r0, Vector3.Cross(r1, r2));
                        if (Mathf.Abs(det) < 1e-5f)
                        {
                            continue;
                        }
                        Vector3 one = Vector3.one;
                        float x = Vector3.Dot(one, Vector3.Cross(r1, r2)) / det;
                        float y = Vector3.Dot(r0, Vector3.Cross(one, r2)) / det;
                        float z = Vector3.Dot(r0, Vector3.Cross(r1, one)) / det;
                        TryMitre(n, n[a] * x + n[b] * y + n[e] * z, ref best, ref bestSq);
                    }
                }
            }
            return bestSq < float.MaxValue;
        }

        private static void TryMitre(List<Vector3> n, Vector3 d, ref Vector3 best, ref float bestSq)
        {
            float sq = d.sqrMagnitude;
            if (sq >= bestSq)
            {
                return;
            }
            for (int k = 0; k < n.Count; k++)
            {
                if (Vector3.Dot(d, n[k]) < 0.999f)
                {
                    return;
                }
            }
            best = d;
            bestSq = sq;
        }

        private static void ReadMaterials(string path, List<ModelMaterial> materials, Dictionary<string, int> materialIndex)
        {
            if (!File.Exists(path))
            {
                ModLog.Warn("[IsoRim] Material file not found: " + path);
                return;
            }
            ModelMaterial current = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] t = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
                if (t[0] == "newmtl" && t.Length > 1)
                {
                    int index;
                    if (!materialIndex.TryGetValue(t[1], out index))
                    {
                        index = AddMaterial(t[1], materials, materialIndex);
                    }
                    current = materials[index];
                }
                else if (t[0] == "Kd" && t.Length >= 4 && current != null)
                {
                    current.color = new Color(Num(t[1]), Num(t[2]), Num(t[3]));
                }
                else if (t[0] == "map_Kd" && t.Length >= 2 && current != null)
                {
                    current.texture = TexturePath(line.Substring(6).Trim());
                }
            }
        }

        // Accepts either a game content path (IsoRim/Swatches/Wood) or a file path that Blender
        // wrote (C:\...\Textures\IsoRim\Swatches\Wood.png) and returns the content path.
        private static string TexturePath(string value)
        {
            string p = value.Replace('\\', '/');
            int cut = p.LastIndexOf("Textures/", StringComparison.OrdinalIgnoreCase);
            if (cut >= 0)
            {
                p = p.Substring(cut + 9);
            }
            int dot = p.LastIndexOf('.');
            if (dot > p.LastIndexOf('/'))
            {
                p = p.Substring(0, dot);
            }
            p = p.Trim().TrimStart('/');
            return p.Length == 0 ? null : p;
        }

        private static float Num(string s)
        {
            float f;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : 0f;
        }
    }

    // The mod's own textures, from its Textures folder. They are drawn with the same shader the
    // game uses for its sprites, with the colour (build material and shading) set per material.
    public static class R3DTextures
    {
        private static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

        public static Texture2D Get(string path)
        {
            Texture2D tex;
            if (cache.TryGetValue(path, out tex))
            {
                return tex;
            }
            if (Aged.IsAgedPath(path))
            {
                tex = null;
                try
                {
                    tex = Aged.Texture(path);   // a rusty copy made at run time (ModelDef.aged); already set to tile
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Making an aged texture failed: " + e, 93611300);
                }
                cache[path] = tex;
                return tex;
            }
            tex = ContentFinder<Texture2D>.Get(path, false);
            if (tex != null)
            {
                tex.wrapMode = TextureWrapMode.Repeat;   // the textures are made to tile
                // Smoothly filtered at a slant and at a distance, so fine patterns do not
                // shimmer as the camera moves.
                tex.filterMode = FilterMode.Trilinear;
                Aniso.Own(tex);   // level 4, or the anisotropic filtering option's if higher
            }
            else
            {
                ModLog.Warn("[IsoRim] Texture not found: " + path);
            }
            cache[path] = tex;
            return tex;
        }

        // A material showing this texture multiplied by a colour, or null if the texture is missing.
        // queue -1 = the textured def's own queue (2001, opaque).
        public static Material MaterialFor(string path, Color color, int queue = -1)
        {
            Texture2D tex = Get(path);
            if (tex == null)
            {
                return null;
            }
            color.a = 1f;
            Color32 c = color;
            string key = path + "|" + c.r + "," + c.g + "," + c.b + "|" + queue;
            Material m;
            if (!materials.TryGetValue(key, out m))
            {
                m = R3DMaterials.Make(R3DMaterials.SolidTexDef, "IsoRim_Tex_" + path, queue);
                if (m != null)
                {
                    m.mainTexture = tex;
                    m.color = color;
                    IsoGlow.Use(m, IsoGlow.OwnCell);
                }
                materials[key] = m;
            }
            return m;
        }

        // The same for a texture that is not one of the mod's own (the game's terrain, for natural rock caps).
        public static Material MaterialFor(Texture2D tex, string name, Color color)
        {
            color.a = 1f;
            Color32 c = color;
            string key = "#" + name + "|" + c.r + "," + c.g + "," + c.b;
            Material m;
            if (!materials.TryGetValue(key, out m))
            {
                m = R3DMaterials.Make(R3DMaterials.SolidTexDef, name);
                if (m != null)
                {
                    m.mainTexture = tex;
                    m.color = color;
                    IsoGlow.Use(m, IsoGlow.OwnCell);
                }
                materials[key] = m;
            }
            return m;
        }

        private static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();

        public static int SwatchIndex(string swatch)
        {
            switch (swatch)
            {
                case "Stone":
                    return 1;
                case "Metal":
                    return 2;
                case "Fabric":
                    return 3;
                default:
                    return 0;
            }
        }

        // The colour and swatch the "stuff" parts of a model take. A thing with a build material
        // takes its colour (Thing.DrawColor). A thing made of no stuff and drawn in plain white
        // (a fueled stove, a television: steel in the cost list) takes the stuff its cost list
        // needs most of, as if it were built of that, instead of coming out white.
        public static Color ModelTint(Thing t)
        {
            if (PaintedHull(t))
            {
                Building b = t as Building;
                return b != null && b.PaintColorDef != null ? b.PaintColorDef.color : Color.white;
            }
            ThingDef cost = t.Stuff == null ? CostStuffIfWhite(t.def, t.DrawColor) : null;
            float aged = Aged.StrengthOf(t);
            if (aged > 0f)
            {
                // An aged thing (ModelDef.aged): its stuff colour dulled; made of no stuff and drawn white, old grey metal
                // (Aged.OldMetal on the Metal swatch, ModelSwatch), as the grey of the game's ancient sprites.
                Color c = cost != null ? t.def.GetColorForStuff(cost) : t.Stuff == null && IsWhite(t.DrawColor) ? Aged.OldMetal : t.DrawColor;
                return Aged.Dull(c, aged);
            }
            return cost != null ? t.def.GetColorForStuff(cost) : t.DrawColor;
        }

        // (Api 24) The colour a model's "sheet" parts (ModelMaterial.sheet) are multiplied by: the thing's second colour, as
        // the game's CutoutComplex sprites take it in their mask (Thing.DrawColorTwo: Building_Bed gives its sheet colour by
        // owner type and medical, a mod bed overriding DrawColorTwo its own; other things their graphicData.colorTwo).
        // White without a thing.
        public static Color SheetTint(Thing t)
        {
            if (t == null)
            {
                return Color.white;
            }
            try
            {
                return t.DrawColorTwo;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] DrawColorTwo of " + t.def?.defName + " failed: " + e.Message, 93611412);
                return Color.white;
            }
        }

        // True if any of the model's materials is a "sheet" one (the second tint matters for it).
        public static bool HasSheet(Model m)
        {
            if (m == null || m.materials == null)
            {
                return false;
            }
            for (int i = 0; i < m.materials.Length; i++)
            {
                if (m.materials[i].sheet)
                {
                    return true;
                }
            }
            return false;
        }

        public static string ModelSwatch(Thing t)
        {
            if (PaintedHull(t))
            {
                return "Metal";
            }
            ThingDef stuff = t.Stuff ?? CostStuffIfWhite(t.def, t.DrawColor);
            if (stuff == null && IsWhite(t.DrawColor) && Aged.StrengthOf(t) > 0f)
            {
                return "Metal";   // an aged thing of no stuff: old metal (ModelTint)
            }
            return SwatchOf(stuff);
        }

        private static bool IsWhite(Color c)
        {
            return c.r >= 0.98f && c.g >= 0.98f && c.b >= 0.98f;
        }

        // A building whose binding says its "stuff" parts are its paint (ModelDef.paintedHull, the
        // passenger shuttle): unpainted it stays the white of its sprite instead of taking the colour
        // of the steel in its cost list, painted it takes the paint as the game's sprite does.
        private static bool PaintedHull(Thing t)
        {
            ModelEntry entry;
            return t != null && t.Stuff == null && t.def != null && ModelLibrary.TryGet(t.def, out entry) && entry.def.paintedHull;
        }

        // Harness colour sweep: the same for a def made of stuff (null = none), unspawned.
        public static Color TintFor(ThingDef def, ThingDef stuff)
        {
            if (stuff != null)
            {
                return def.GetColorForStuff(stuff);
            }
            ModelEntry painted;
            if (def != null && ModelLibrary.TryGet(def, out painted) && painted.def.paintedHull)
            {
                return Color.white;
            }
            Color c = def.graphicData != null ? def.graphicData.color : Color.white;
            ThingDef cost = CostStuffIfWhite(def, c);
            return cost != null ? def.GetColorForStuff(cost) : c;
        }

        public static string SwatchFor(ThingDef def, ThingDef stuff)
        {
            ModelEntry painted;
            if (stuff == null && def != null && ModelLibrary.TryGet(def, out painted) && painted.def.paintedHull)
            {
                return "Metal";
            }
            return SwatchOf(stuff ?? CostStuffIfWhite(def, def.graphicData != null ? def.graphicData.color : Color.white));
        }

        private static readonly Dictionary<ThingDef, ThingDef> costStuff = new Dictionary<ThingDef, ThingDef>();

        private static ThingDef CostStuffIfWhite(ThingDef def, Color drawColor)
        {
            if (def == null || def.MadeFromStuff || drawColor.r < 0.98f || drawColor.g < 0.98f || drawColor.b < 0.98f)
            {
                return null;
            }
            ThingDef best;
            if (!costStuff.TryGetValue(def, out best))
            {
                int most = 0;
                if (def.costList != null)
                {
                    for (int i = 0; i < def.costList.Count; i++)
                    {
                        ThingDefCountClass c = def.costList[i];
                        if (c.thingDef != null && c.thingDef.stuffProps != null && c.count > most)
                        {
                            best = c.thingDef;
                            most = c.count;
                        }
                    }
                }
                costStuff[def] = best;
            }
            return best;
        }

        // Which of the mod's surface textures matches what a thing is built from.
        public static string StuffSwatch(Thing t)
        {
            return SwatchOf(t.Stuff);
        }

        public static string SwatchOf(ThingDef stuff)
        {
            if (stuff == null || stuff.stuffProps == null)
            {
                return "Wood";
            }
            string name = stuff.stuffProps.appearance != null ? stuff.stuffProps.appearance.defName : "";
            if (name == "Bricks")
            {
                return "Stone";
            }
            if (name == "Smooth")
            {
                return "Metal";
            }
            if (name == "Planks")
            {
                return "Wood";
            }
            if (stuff.stuffProps.categories != null)
            {
                for (int i = 0; i < stuff.stuffProps.categories.Count; i++)
                {
                    string cat = stuff.stuffProps.categories[i].defName;
                    if (cat == "Stony")
                    {
                        return "Stone";
                    }
                    if (cat == "Metallic")
                    {
                        return "Metal";
                    }
                    if (cat == "Fabric" || cat == "Leathery")
                    {
                        return "Fabric";
                    }
                }
            }
            return "Wood";
        }
    }

    // The average colour of a thing's own sprite, used to colour the stand-in model of a plant
    // the mod has no model for, so it still looks like that plant from a distance.
    public static class SpriteColors
    {
        private const int N = 16;
        private static readonly Color Fallback = new Color(0.40f, 0.55f, 0.26f);
        private static readonly Dictionary<int, Color> cache = new Dictionary<int, Color>();

        public static Color MeanFor(Thing t)
        {
            try
            {
                Graphic g = t.Graphic;
                return MeanOf(g != null ? g.MatSingleFor(t) : null);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading a sprite's colour failed: " + e, 93611040);
                return Fallback;
            }
        }

        // The lifted mean colour of a sprite material (its texture times its colour), read once per texture.
        public static Color MeanOf(Material mat)
        {
            try
            {
                if (mat == null || !mat.HasProperty("_MainTex") || mat.mainTexture == null)
                {
                    return Fallback;
                }
                Texture tex = mat.mainTexture;
                Color c;
                if (!cache.TryGetValue(tex.GetInstanceID(), out c))
                {
                    c = Read(tex);
                    cache[tex.GetInstanceID()] = c;
                }
                if (mat.HasProperty("_Color"))
                {
                    c *= mat.color;
                }
                c.a = 1f;
                return c;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading a sprite's colour failed: " + e, 93611040);
                return Fallback;
            }
        }

        public const int SampleSize = N;

        // A 16 x 16 sample of the whole sprite (SpritePixels: on the CPU, as the 1.5 mod's Blit and
        // ReadPixels gave it; the 1.5 way on the GPU if the pixels cannot be read).
        private static Color Read(Texture tex)
        {
            Color32[] px = SpritePixels.Sample(tex, Vector2.one, Vector2.zero, N);
            if (px == null && !SpritePixels.IsSheet(tex))
            {
                px = SpritePixels.GpuSample(tex, N);
            }
            return MeanOf(px);
        }

        // The lifted mean colour of the solid pixels of a sample; the fallback if there are none.
        public static Color MeanOf(Color32[] px)
        {
            float r = 0f;
            float g = 0f;
            float b = 0f;
            int count = 0;
            if (px != null)
            {
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].a >= 128)
                    {
                        r += px[i].r;
                        g += px[i].g;
                        b += px[i].b;
                        count++;
                    }
                }
            }
            if (count == 0)
            {
                return Fallback;
            }

            // The average includes the sprite's dark outlines, so lift it back up a little.
            Color mean = new Color(r / count / 255f, g / count / 255f, b / count / 255f, 1f);
            float brightest = Mathf.Max(mean.r, Mathf.Max(mean.g, mean.b));
            float lift = brightest > 0.01f ? Mathf.Clamp(0.62f / brightest, 1f, 1.6f) : 1f;
            return new Color(Mathf.Min(1f, mean.r * lift), Mathf.Min(1f, mean.g * lift), Mathf.Min(1f, mean.b * lift), 1f);
        }
    }

    // The materials of all solid 3D geometry: colour from the vertices (stuff tint, fixed factors)
    // times the material colour, lit per pixel by IsoRim's sun (Shaders/IsoRimSolid.frag.glsl).
    [StaticConstructorOnStartup]
    public static class R3DMaterials
    {
        private static bool tried;
        private static Material solid;
        private static Material outline;
        private static bool shadowTried;
        private static Material gameShadow;
        private static Material plainShadow;
        private static bool groundShadowTried;
        private static Material groundShadow;

        // Light comes from above and from the south-west, so the faces turned toward the camera are lit.
        private static readonly Vector3 BaseLight = new Vector3(-0.35f, 0.8f, -0.5f).normalized;
        private static Vector3 LightDir = BaseLight;
        private static bool lightSet;

        // The direction toward IsoRim's sun (or moon) right now, in true (unsquashed) space.
        public static Vector3 SunDir
        {
            get
            {
                if (!lightSet)
                {
                    SetSunHour(10f);
                }
                return LightDir;
            }
        }

        // Where the sun is at a given hour: up in the east at six, high in the south at noon,
        // down in the west at six in the evening, and a fixed moonlight through the night, with
        // an hour's blend at dusk and dawn.
        public static Vector3 SunDirAt(float hour)
        {
            Vector3 moon = Dir(200f, 55f);
            if (hour >= 6f && hour <= 18f)
            {
                float t = (hour - 6f) / 12f;
                return Dir(95f + 170f * t, 22f + 40f * Mathf.Sin(t * Mathf.PI));
            }
            float k;
            Vector3 edge;
            if (hour > 18f)
            {
                k = Mathf.Clamp01(hour - 18f);
                edge = Dir(265f, 22f);
            }
            else
            {
                k = Mathf.Clamp01(6f - hour);
                edge = Dir(95f, 22f);
            }
            return Vector3.Slerp(edge, moon, k).normalized;
        }

        private static Vector3 Dir(float azimuth, float elevation)
        {
            float az = azimuth * Mathf.Deg2Rad;
            float el = elevation * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el)).normalized;
        }

        private static readonly int isoSunId = Shader.PropertyToID("_IsoSun");
        private static readonly int isoViewId = Shader.PropertyToID("_IsoView");

        // Moves the sun to the given hour of the day and hands it to the shaders (global _IsoSun),
        // which light every face per pixel. Cheap enough to call every frame.
        public static void SetSunHour(float hour)
        {
            SetLight(SunDirAt(hour));
        }

        // Night follows the game's moon (user decision 2026-10-13). The game swings its moon from
        // east to west through the night (GenCelestial.GetLightSourceInfo, night branch: shadows point
        // west in the evening, south at midnight, east before dawn). In 3D the game's shadows are
        // mirrored north-south (Patches.GetLightSourceInfo_Postfix), so the light comes from the
        // opposite side of the mirrored cast. Its height comes from the cast's length, by the same
        // relation the day sun has to the game's day casts: 62 degrees at the noon length (1.5),
        // 22 degrees at the length at six (about 8), never lower or higher.
        public static Vector3 MoonDir(Map map)
        {
            Vector2 cast = GenCelestial.GetLightSourceInfo(map, GenCelestial.LightType.LightingMoon).vector;
            cast.y = -cast.y;   // mirrored like the 3D shadows
            float len = cast.magnitude;
            if (len < 1e-4f)
            {
                return Dir(180f, 62f);
            }
            float elevation = Mathf.Lerp(62f, 22f, Mathf.InverseLerp(1.5f, 8.04f, len)) * Mathf.Deg2Rad;
            Vector2 flat = -cast / len;
            return new Vector3(flat.x * Mathf.Cos(elevation), Mathf.Sin(elevation), flat.y * Mathf.Cos(elevation)).normalized;
        }

        // How far the light has gone over to the moon: 0 in daylight, 1 at night. The game switches
        // its shadows from sun to moon where the sun's glow crosses 0.6 (they fade out there), so the
        // light swings over smoothly across that point instead of jumping.
        public const float NightGlowFull = 0.52f;
        public const float DayGlowFull = 0.68f;

        public static float NightWeight(float sunGlow)
        {
            float t = Mathf.InverseLerp(DayGlowFull, NightGlowFull, sunGlow);
            return t * t * (3f - 2f * t);
        }

        // The light right now: IsoRim's own sun by day (unchanged), the game's moon at night.
        public static Vector3 LightAt(Map map, float hour)
        {
            Vector3 sun = SunDirAt(Mathf.Clamp(hour, 6f, 18f));
            if (map == null)
            {
                return sun;
            }
            float night = NightWeight(GenCelestial.CurCelestialSunGlow(map));
            if (night <= 0f)
            {
                return sun;
            }
            Vector3 moon = MoonDir(map);
            return night >= 1f ? moon : Vector3.Slerp(sun, moon, night).normalized;
        }

        public static void SetLight(Vector3 dir)
        {
            lightSet = true;
            LightDir = dir;
            Shader.SetGlobalVector(isoSunId, new Vector4(LightDir.x, LightDir.y, LightDir.z, 0f));
        }

        // The direction toward the camera in true space, and the squash, for the shaders' face
        // normals (global _IsoView). Set by View3D every frame while the 3D view is on.
        public static void SetView(Vector3 towardCamera, float squash)
        {
            Shader.SetGlobalVector(isoViewId, new Vector4(towardCamera.x, towardCamera.y, towardCamera.z, squash));
        }

        // ---- lit materials
        //
        // Faces are lit in the shader (Shaders\IsoRimSolid.frag.glsl): it works out which way each
        // face points, sorts it into one of these direction groups (straight up, straight down, or
        // one of eight compass points at three steepnesses) and shades it for this frame's sun.
        // So one material per shading curve serves every direction, and nothing is recoloured or
        // rebuilt as the sun moves. BinOf remains for callers that still pass a group (it is ignored).
        public const int Bins = 26;
        public const int LightModel = 1;   // _LightMode: model curve (curve 0)
        public const int LightWall = 2;    // _LightMode: wall and rock curve (curve 1)
        public const int LightRoof = 3;    // _LightMode: roof slates (model curve * 1.05, at most 1.1)
        private static readonly Material[] litSolid = new Material[2];
        private static readonly Dictionary<string, Material> litByKey = new Dictionary<string, Material>();

        // Which direction group a face pointing along normal belongs to (edges as BinNormal in
        // Shaders\IsoRimSolid.frag.glsl: a little off round angles, so model faces do not sit on them).
        public static int BinOf(Vector3 normal)
        {
            float len = normal.magnitude;
            if (len < 1e-6f)
            {
                return 0;
            }
            normal /= len;
            if (normal.y >= 0.90f)
            {
                return 0;
            }
            if (normal.y <= -0.485f)
            {
                return 1;
            }
            int band = normal.y < 0.235f ? 0 : (normal.y < 0.635f ? 1 : 2);
            float az = Mathf.Atan2(normal.x, normal.z) * Mathf.Rad2Deg;
            int sector = Mathf.FloorToInt((Mathf.Repeat(az, 360f) + 1.5f) / 45f + 0.5f) % 8;
            return 2 + band * 8 + sector;
        }

        private static int ModeOf(int curve)
        {
            return curve == 1 ? LightWall : LightModel;
        }

        // The plain solid material lit by the sun: vertex colours times the shade of the face's
        // direction. bin is ignored now (the shader finds each face's direction itself).
        public static Material Lit(int bin, int curve = 0)
        {
            if (Solid == null)
            {
                return null;
            }
            curve = curve == 1 ? 1 : 0;
            Material m = litSolid[curve];
            if (m == null)
            {
                m = Make(SolidDef, curve == 1 ? "IsoRim_LitWall" : "IsoRim_LitModel", -1, ModeOf(curve));
                IsoGlow.Use(m, IsoGlow.OwnCell);
                litSolid[curve] = m;
            }
            return m;
        }

        // The lit plain material for models: pawns, animals, baked models, extruded sides.
        public static Material LitModel
        {
            get { return Lit(0, 0); }
        }

        // A texture times a colour, lit by the sun. Shared by every face with the same texture,
        // colour and curve; bin is ignored (see Lit). Null if the texture is missing.
        public static Material LitTexture(string path, Color baseColor, int bin = 0, int curve = 0)
        {
            curve = curve == 1 ? 1 : 0;
            Color32 c = baseColor;
            string key = path + "|" + c.r + "," + c.g + "," + c.b + "|" + curve;
            Material m;
            if (litByKey.TryGetValue(key, out m))
            {
                return m;
            }
            Texture2D tex = R3DTextures.Get(path);
            if (tex != null)
            {
                m = Make(SolidTexDef, curve == 1 ? "IsoRim_LitTexWall" : "IsoRim_LitTex", -1, ModeOf(curve));
            }
            if (m != null)
            {
                m.mainTexture = tex;
                m.color = new Color(baseColor.r, baseColor.g, baseColor.b, 1f);
                IsoGlow.Use(m, IsoGlow.OwnCell);
            }
            litByKey[key] = m;
            return m;
        }

        // The same for a texture made at run time (WallTops: a side texture cut from another mod's atlas), named by key.
        public static Material LitTexture(Texture2D tex, string name, Color baseColor, int curve = 0)
        {
            if (tex == null)
            {
                return null;
            }
            curve = curve == 1 ? 1 : 0;
            Color32 c = baseColor;
            string key = "#" + name + "|" + c.r + "," + c.g + "," + c.b + "|" + curve;
            Material m;
            if (litByKey.TryGetValue(key, out m))
            {
                return m;
            }
            m = Make(SolidTexDef, curve == 1 ? "IsoRim_LitTexWall" : "IsoRim_LitTex", -1, ModeOf(curve));
            if (m != null)
            {
                m.mainTexture = tex;
                m.color = new Color(baseColor.r, baseColor.g, baseColor.b, 1f);
                IsoGlow.Use(m, IsoGlow.OwnCell);
            }
            litByKey[key] = m;
            return m;
        }

        public static Material Solid
        {
            get
            {
                if (!tried)
                {
                    Init();
                }
                return solid;
            }
        }

        // Same as Solid but drawn inside out: only the faces turned away from the camera show.
        public static Material Outline
        {
            get
            {
                if (!tried)
                {
                    Init();
                }
                return outline;
            }
        }

        // The outline of moving models (people, animals, doors, tools). Each part (body, head,
        // hair, hat) has its own shell; where one part tucks into another, the lower rim of its
        // shell sits just in front of the other part and showed as a dark ring. The vertex shader
        // pushes the whole shell straight away from the camera by View3D.OutlinePush, which hides
        // those rims behind the surface without moving anything on screen (same as 45b20f5).
        private static Material outlineMoving;

        public static Material OutlineMoving
        {
            get
            {
                if (outlineMoving == null && Outline != null)
                {
                    outlineMoving = Make(OutlineDef, "IsoRim_OutlineMoving");
                    outlineMoving.SetFloat(Shader.PropertyToID("_OutlinePush"), View3D.OutlinePush);
                }
                return outlineMoving;
            }
        }

        // ---- shaders (Defs\ShaderDefs\IsoRim_Shaders.xml). Every IsoRim def runs the same
        // fragment program (vertex colour * _Color [* texture] * shade); the def sets the render
        // state. _LightMode picks the shade: 0 unlit, 1 model curve, 2 wall curve, 3 roof slates.
        public const string SolidDef = "IsoRimSolid";       // opaque, depth write, cull off, 2000
        public const string SolidTexDef = "IsoRimSolidTex"; // same + texture, cut out at alpha 0.5, 2001
        public const string OutlineDef = "IsoRimOutline";   // opaque, cull front, pushed back in depth
        public const string GhostDef = "IsoRimGhost";       // alpha, depth write, cull back, 3100
        public const string BlendDef = "IsoRimBlend";       // alpha, depth test, no depth write, 3050
        public const string OverlayDef = "IsoRimOverlay";   // alpha, no depth test or write, 3650
        public const string GhostXrayDef = "IsoRimGhostXray"; // alpha, no depth test or write, cull back, 3651
        public const string WaveDef = "IsoRimWave";         // the game's Transparent with a repeating texture

        public const int LightUnlit = 0;
        private static readonly int lightModeId = Shader.PropertyToID("_LightMode");
        private static readonly Dictionary<string, Shader> shaders = new Dictionary<string, Shader>();

        // The shader of one of the defs above, or null (logged once) if it is missing.
        public static Shader ShaderOf(string defName)
        {
            Shader s;
            if (shaders.TryGetValue(defName, out s))
            {
                return s;
            }
            ShaderDef def = DefDatabase<ShaderDef>.GetNamedSilentFail(defName);
            s = def != null ? def.Shader : null;
            if (s == null)
            {
                Log.Error("[IsoRim] ShaderDef " + defName + " is missing, so parts of the 3D view cannot be drawn.");
            }
            shaders[defName] = s;
            return s;
        }

        // A new material on one of the IsoRim defs. queue -1 keeps the def's queue.
        public static Material Make(string defName, string name, int queue = -1, int lightMode = LightUnlit)
        {
            Shader s = ShaderOf(defName);
            if (s == null)
            {
                return null;
            }
            Material m = new Material(s);
            m.name = name;
            m.SetFloat(lightModeId, lightMode);
            Aniso.Track(m);   // may show one of the mod's textures: refreshed when the filtering option changes
            if (queue >= 0)
            {
                m.renderQueue = queue;
            }
            return m;
        }

        private static void Init()
        {
            tried = true;
            solid = Make(SolidDef, "IsoRim_Solid");
            if (solid == null)
            {
                return;
            }
            // Night, lamp and indoor light per pixel from each face's own cell (see IsoGlow);
            // until the first 3D frame binds the map's light, the light is neutral.
            IsoGlow.BindNeutral();
            IsoGlow.Use(solid, IsoGlow.OwnCell);
            // The shell is pushed back in depth by the def, so where it runs close to the model's
            // own faces it can never show through them.
            outline = Make(OutlineDef, "IsoRim_Outline");
        }

        // See-through, coloured from the vertices, for the ghosts of things still to be built.
        private static Material ghost;

        public static Material Ghost
        {
            get
            {
                if (ghost == null && Solid != null)
                {
                    // After the game's lighting overlay (3100), lit by its own cell: drawn before
                    // it, a ghost over a face that carries its own light was never darkened.
                    ghost = Make(GhostDef, "IsoRim_Ghost", 3101);
                    IsoGlow.Use(ghost, IsoGlow.OwnCellUnmarked);
                }
                return ghost;
            }
        }

        // The same ghost seen through what stands in front of it (no depth test, as the mining x-ray): for a thing to be built
        // inside a wall or other solid building (Ghosts.Occupied: a door replacing a wall section), which the plain ghost,
        // depth tested, would leave hidden inside it.
        private static Material ghostXray;

        public static Material GhostXray
        {
            get
            {
                if (ghostXray == null && Solid != null)
                {
                    ghostXray = Make(GhostXrayDef, "IsoRim_GhostXray", 3651);
                    IsoGlow.Use(ghostXray, IsoGlow.OwnCellUnmarked);
                }
                return ghostXray;
            }
        }

        // The solid material again, drawn late (after the night and indoor darkness) so that roofs
        // are not darkened by the rooms under them. Roofs dim with the daylight themselves.
        private static Material roof;

        public static Material Roof
        {
            get
            {
                if (roof == null && Solid != null)
                {
                    roof = Make(SolidDef, "IsoRim_Roof", 3600);
                    IsoGlow.Use(roof, IsoGlow.SkyOnly);   // sky colour only: lamps under a roof do not light its top
                }
                return roof;
            }
        }

        // See-through, coloured from the vertices, drawn after the ground and the game's own water,
        // so the water's moving shader shows through the mod's bands of colour and foam.
        private static Material waterOverlay;

        public static Material WaterOverlay
        {
            get
            {
                if (waterOverlay == null && Solid != null)
                {
                    waterOverlay = Make(BlendDef, "IsoRim_Water", 3050);
                }
                return waterOverlay;
            }
        }

        // The game's own shadow material. Whatever is drawn with it is slid along the ground by
        // the game according to the time of day, takes the game's shadow colour and fades out at
        // night, exactly like the shadows of walls and colonists. Null if the game has changed.
        public static Material GameShadow
        {
            get
            {
                if (!shadowTried)
                {
                    InitShadow();
                }
                return gameShadow;
            }
        }

        // A plain see-through black, used for shadows only when GameShadow is missing.
        public static Material PlainShadow
        {
            get
            {
                if (!shadowTried)
                {
                    InitShadow();
                }
                return plainShadow;
            }
        }

        // See-through black for the soft shading under things. How dark it is at each corner comes
        // from the corner's own colour, so one piece can fade from dark to nothing. It is drawn
        // straight after the ground, before anything that stands on it.
        public static Material GroundShadow
        {
            get
            {
                if (!groundShadowTried)
                {
                    groundShadowTried = true;
                    Material basis = Solid;
                    if (basis != null)
                    {
                        groundShadow = Make(BlendDef, "IsoRim_GroundShadow", 2420);
                    }
                }
                return groundShadow;
            }
        }

        private static void InitShadow()
        {
            shadowTried = true;
            try
            {
                Type bases = AccessTools.TypeByName("Verse.MatBases");
                string[] names = { "SunShadowFade", "SunShadow" };
                for (int i = 0; i < names.Length && gameShadow == null && bases != null; i++)
                {
                    FieldInfo f = AccessTools.Field(bases, names[i]);
                    gameShadow = f != null ? f.GetValue(null) as Material : null;
                }
            }
            catch (Exception e)
            {
                gameShadow = null;
                ModLog.Warn("[IsoRim] Looking up the game's shadow material failed: " + e.Message);
            }
            if (gameShadow != null)
            {
                return;
            }

            ModLog.Warn("[IsoRim] The game's shadow material was not found, so tree shadows will not move with the sun.");
            Material basis = Solid;
            if (basis == null)
            {
                return;
            }
            plainShadow = Make(BlendDef, "IsoRim_Shadow", 2900);
        }

        public static Color32 Shaded(Color c, float shade)
        {
            return new Color32(
                (byte)Mathf.Clamp(c.r * shade * 255f, 0f, 255f),
                (byte)Mathf.Clamp(c.g * shade * 255f, 0f, 255f),
                (byte)Mathf.Clamp(c.b * shade * 255f, 0f, 255f),
                255);
        }
    }
}
