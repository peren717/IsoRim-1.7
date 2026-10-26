# IsoRim Waylith Api (for mod authors)

Model packs (XML and .obj files, no code), the dev-mode model binder and the coverage report: see [ModelPacks.md](ModelPacks.md).

IsoRim's public surface is the static class `IsoRim.Api` and the small types next to it (`FigureFrame`, `FigureHook`,
`ModelHandle`, `ProjectileCardHook`, `IFloorLayout`, `ApparelDecal`, `DecalSide`, `ApparelDecalProvider`, `UprightScope`, `FlatScope`, `Attachment`, `AttachAnchor`, `AttachPose`, `AttachFacing`, `HostTransform`, `CardLight`, `CutawayRule`, `FaceDrawer`, `FaceFrame`, `FigureParts`, `FigureMode`, `AlienFace`, `AlienHair`, `CornerRule`, `WallShapeRule`, `WallShape`), the defs `IsoRim.ModelDef` (part M), `IsoRim.ApparelModelDef` (Api 9) and `IsoRim.AlienModelDef` (Api 25), and the `ModelDef` fields listed in part M. Everything else in the assembly is internal detail and can change
in any update, even when it is public: do not patch or reflect into it, ask for an Api member instead. Reference `IsoRim.dll` (Publicize is not needed) and keep the reference optional if IsoRim is optional for
your mod.

`Api.Dev` is for addons' own dev tests: its members can change or go without notice and are outside the version promise.

## Versioning

`Api.Version` goes up whenever members are added. Check it before you use newer members:

```csharp
if (IsoRim.Api.Version >= 2) { /* view, projectiles, floors, drawing and options (part 0) */ }
if (IsoRim.Api.Version >= 3) { /* the fog lid lock: LockFogLid, UnlockFogLid, FogLidLockedBy, FogLidOn */ }
if (IsoRim.Api.Version >= 4) { /* collapse debris: Debris */ }
if (IsoRim.Api.Version >= 5) { /* XrayDesignator */ }
if (IsoRim.Api.Version >= 6) { /* model states: AddModelState, ModelStandsIn; ModelDef style, byState, crownTop, rotationByFacing, doorLeafRise */ }
if (IsoRim.Api.Version >= 7) { /* PeekUpperFloors */ }
if (IsoRim.Api.Version >= 8) { /* figure hooks and model drawing are there; also the card overlay IsCard, DrawOnCard */ }
if (IsoRim.Api.Version >= 9) { /* IsoRim.ApparelModelDef is there */ }
if (IsoRim.Api.Version >= 10) { /* apparel decals; DrawModel follows the peek fade */ }
if (IsoRim.Api.Version >= 11) { /* ModelDef.wallTop (wall tops) and Form3D.Ground */ }
if (IsoRim.Api.Version >= 12) { /* the upright drawing scope: BeginUpright, EndUpright, Upright, DrawMesh */ }
if (IsoRim.Api.Version >= 13) { /* attachments: AddAttachments, ReadHost, AnchorPoint, AttachedPlace, AttachmentOf */ }
if (IsoRim.Api.Version >= 14) { /* ModelDef wing fields (animal wings that fold and beat), AirborneFactor */ }
if (IsoRim.Api.Version >= 15) { /* ModelDef.aged (the aged look) */ }
if (IsoRim.Api.Version >= 16) { /* ModelDef.graphicPath: models bound by texture path */ }
if (IsoRim.Api.Version >= 17) { /* complete-wall requests for camera modes: RequestFullWalls */ }
if (IsoRim.Api.Version >= 18) { /* one-cell doors with their own thingDef models; ModelDef.doorLeafGather */ }
if (IsoRim.Api.Version >= 19) { /* facing holds: AddFacingHold, RemoveFacingHold */ }
if (IsoRim.Api.Version >= 20) { /* ModelDef.cutaway, view state, flat scopes, SkipWhenModelled, floor placing, DrawGhost,
                                   projectile arcs, XrayPlacing, HasModel (part 14) */ }
if (IsoRim.Api.Version >= 21) { /* face drawers: AddFaceDrawer, FaceFrame; FigureFrame.Hide (FigureParts) (part 15) */ }
if (IsoRim.Api.Version >= 22) { /* AddFaceDrawer(drawer, evenWhenHidden), FaceFrame.HiddenByGame (part 15) */ }
if (IsoRim.Api.Version >= 24) { /* ModelDef.immatureModels, immatureHide (young plants); sheet* materials take the thing's second colour (part M) */ }
if (IsoRim.Api.Version >= 25) { /* humanlike aliens: AlienModelDef alien packs, FigureModeOf, AddAlienFigureRule, FigureFrame.Anchor,
                                   FigureFrame.Mode, FigureParts.Addons, ApparelModelDef.aliens / alienBodyModels (part 16) */ }
if (IsoRim.Api.Version >= 26) { /* AddWallCorners, HideFlatLayer, RefreshWallShapes, FlyLayer, NotGeometryLayer (part 17) */ }
if (IsoRim.Api.Version >= 27) { /* ModelDef.bobByState: state models for the bobbing part (part M) */ }
if (IsoRim.Api.Version >= 28) { /* ModelDef.slots: one entry per sleeping slot, its surface height (a bunk bed's levels; part M) */ }
if (IsoRim.Api.Version >= 29) { /* multi-cell supported doors drawn from their models (part 18) */ }
if (IsoRim.Api.Version >= 30) { /* glass parts per thing: ModelDef glassTint / glassOpacity / glassGlow, AddGlassColor,
                                   RemoveGlassColor; supported doors draw their glassModel (part 19) */ }
if (IsoRim.Api.Version >= 31) { /* ModelDef.topThroughRoof, roofModel: a turret's gun and a fixed part rise through roofs (part 20) */ }
if (IsoRim.Api.Version >= 32) { /* ModelDef.hoverHeight, hoverBob: hovering animals and mechs (part 21) */ }
if (IsoRim.Api.Version >= 33) { /* beam parts: beam_* materials, ModelDef.beamFlicker / beamPulse / beamMotes (part 22) */ }
if (IsoRim.Api.Version >= 34) { /* wall shapes: AddWallShapes (WallShapeRule, WallShape), roofs clipped to them (part 23) */ }
```

Members are never removed or changed in meaning within a version. Versions 2 to 7 are part 0, version 8 adds parts 1 and 2 below, version 9 part 3,
version 10 part 4 and the peek fade of `Api.DrawModel` (part 2), version 11 part 5, version 12 part 6, version 13 part 7, version 14 part 8, version 15 part 9, version 16 part 10, version 17 part 11, version 18 part 12, version 19 part 13, version 20 part 14, version 21 part 15, version 22 part 15 (opt-in face drawers), version 24 part M (young plants, sheet colours), version 25 part 16, version 26 part 17, version 27 part M (bobByState), version 28 part M (slots), version 29 part 18, version 30 part 19, version 31 part 20, version 32 part 21, version 33 part 22, version 34 part 23 (Api 23 is claimed for hand attach points and is not out yet). Part M is the model pack reference.

## Spaces

- **True space**: x and z in map cells, y the height over the ground in cells.
- **Drawn space**: where IsoRim puts things: drawn y = true height / `Api.HeightSquash` (a very small number, so drawn
  y values are large). The figure frames below map into drawn space.
- Model files are OBJ, one unit = one cell, +y up. IsoRim flips the file's z, as for its own models.

## Part 0: view, floors, projectiles and drawing (versions 2 to 7)

The view (version 2):

| Member | What it does |
|---|---|
| `Api.ViewReady` | The 3D view is on and has drawn a frame (the camera members below are valid). |
| `Api.ViewPitch`, `Api.ViewYaw` | The camera's tilt (90 looks straight down) and turn about the vertical, degrees. |
| `Api.HeightSquash` | Drawn y = true height / `HeightSquash`. |
| `Api.WallHeight` | The height of a full wall in cells (the option, or the locked value). |
| `Api.LockView(string by, float wallHeight)` | For an addon that needs the 3D view: it starts on and cannot be turned off (`by` names the addon in the tooltip); `wallHeight` > 0 fixes the wall height. `Api.ViewLockedBy`, `Api.LockedWallHeight` read the lock. |
| `Api.WorldToScreen(Vector3 p)` | Where a true-space point shows on screen (pixels from the bottom left). |
| `Api.ScreenToGround(Vector2 px)`, `Api.ScreenToHeight(Vector2 px, float height)` | The ground point (or the point at a true height) under a screen pixel. |
| `Api.ScreenToMap(Vector2 px)` | The map point under a pixel, on the stacked floor the pointer ray lands on. |
| `Api.ShowRoofs(bool on)` | The player's roof switch. |
| `Api.WallCutAway(Map map, IntVec3 c)` | The cut-away has lowered the wall in this cell (it is near the pointer). |
| `Api.SetOption(string name, object value)` | Sets one of IsoRim's options by the name it is saved under in IsoRim's settings file (`freeYaw`, `isoPitch`, `wallHeight`, `cutHover`, `cutRooms`, `perspective`, `fov`, `groundShadows`, ...); returns the old value, or null for an unknown name. Not saved by itself. |
| `Api.LockFogLid(string tip, bool on)`, `Api.UnlockFogLid()`, `Api.FogLidLockedBy`, `Api.FogLidOn` | (version 3) Lock the fog lid on or off for the current game; the player's option is kept and greyed out with `tip`. |

Stacked floors (version 2): an addon that puts upper floors on the rows of the same map registers an `IFloorLayout` in
`Api.Floors` (ground rows, floor count, each floor's height and row shift and whether it is drawn, the pointer's floor, a
thing's extra offset, optional `ViewFloor` and `ShipCell`). IsoRim then draws each floor over the ground. Null (the
default) costs nothing. Also: `Api.HiddenRoof` (a `Predicate<RoofDef>` for roofs kept for the game but not drawn) and
`Api.RoofHidden(roof)`, `Api.ModelHeight(ThingDef)` (how tall a thing is drawn from its model, 0 for things that follow the
wall height), `Api.Debris(Map, IEnumerable<IntVec3>)` (version 4: collapse debris; call it before the cells' things are
removed), `Api.XrayDesignator(Type designator, bool below, bool above)` (version 5: the mining x-ray shows the floors
next to the viewed one while that designator is in hand), `Api.PeekUpperFloors` (version 7: set it each frame from your
own key to draw the floors above the viewed one faded instead of hidden).

Projectiles (version 2; they only change how shots are drawn):

| Member | What it does |
|---|---|
| `Api.MuzzleHeight(Thing launcher)` | How high a shooter fires from over its feet (cells). |
| `Api.ProjectileHeight(Projectile p, Vector3 at)` | The true height IsoRim draws a projectile at when the game has it at `at`. |
| `Api.AimHeight(Projectile p)` | The height of what it is aimed at over its floor. |
| `Api.Muzzle(Thing owner, ThingDef gun, Vector3 from, Vector3 dir)` | The muzzle: x, z on the map, y over the owner's feet. |
| `Api.SetLaunchPoint(Projectile p, Vector3 from)` | For an addon that moves a shot's launch point (onto another floor's rows): where it really left from. Call before it is first drawn. |
| `Api.DrawnStart(Projectile p)` | Where the drawn flight starts (x, z the game's rows, y over the ground). |
| `Api.ProjectileCard` | A `ProjectileCardHook`: replace the matrix of the cards IsoRim draws for plain projectiles; null draws them as usual. |
| `Api.SkyfallerPose(Skyfaller s, out ground, out height, out floorHeight, out rowShift)` | Where a skyfaller is drawn now. |

Models and drawing:

| Member | What it does |
|---|---|
| `Api.AddModelState(Func<Thing, string> state)` | (version 6) Name a thing's state; a ModelDef with that state in `byState` draws its model then. Call once. |
| `Api.ModelStandsIn(Thing t)` | (version 6) A 3D model stands in for the thing's body now (its sprite is not drawn). |
| `Api.MakeBlendMaterial(string name, int queue)` | A see-through, vertex-coloured, unlit material at that render queue. |
| `Api.DrawLit(Mesh mesh, Matrix4x4 matrix)` | Draws a vertex-coloured mesh lit as IsoRim lights its models. |
| `Api.IsCard(Pawn pawn)` | (check version 8) The pawn is drawn as an upright 2D card now. |
| `Api.DrawOnCard(Pawn pawn, Mesh mesh, Matrix4x4 gameMatrix, Material material, CardLight light = LikeCard, MaterialPropertyBlock props = null)` | (check version 8) Draws your mesh on the pawn's card this frame (`gameMatrix` as in vanilla); `CardLight.LikeCard` is darkened at night with the card, `Unlit` glows. False when the pawn is not a card: draw it as in vanilla. |

## Part 1: figure attachment points

Register a `FigureHook` once at startup. IsoRim calls it for each person figure it draws (people and creepjoiners shown as
3D figures, alive or dead, also figures drawn by `Api.DrawFigure`; not statues), after the figure is placed, normally once
a frame per figure. With no hook registered nothing extra runs.

| Member | What it does |
|---|---|
| `Api.AddFigureHook(FigureHook hook)` / `Api.RemoveFigureHook(hook)` | Register / remove a hook. |
| `frame.Pawn` | The person (a corpse's inner pawn). |
| `frame.Head`, `frame.Body` | `Matrix4x4` from the head / body frame to drawn space. A frame is the space the figure's own head / body parts are modelled in: cells, feet at the origin, y up, +z the way the figure faces, already posed (walking, lying, sitting, crawling) and scaled to the person's size. |
| `frame.HeadWidth`, `frame.BodyScale` | The head type's widening (x) and the body type's scale. IsoRim applies them to its own parts; they are not in the frames. |
| `frame.HeadCentre`, `frame.HeadSize` | The head's box in the head frame (with `HeadWidth`; the skull's box when dried to bones). |
| `frame.BodyCentre`, `frame.BodySize` | The body's box in the body frame (with `BodyScale`). |
| `frame.HeadSprite` | Maps a point of the game's south-facing head sprite (x right, y up, z into the picture; game cells from the head sprite's middle) onto the head frame: where the game's head art lands on the figure. z 0 is the head's middle plane. |
| `frame.BodySprite` | The same for the game's south-facing adult body sprite of the person's body type (origin at the pawn's draw position): sprite feet on the figure's feet, the head sprite's chin line on the figure's neck, the sprite's width across the body. |
| `frame.Yaw`, `frame.Facing` | The figure's turn (degrees, 0 north, 90 east; smooth) and the `Rot4` from it. |
| `frame.Size` | 1 for adults, smaller for children (already in the frames). |
| `frame.HeadHidden` | No head is drawn: a garment hides the whole head (skip flag Head, e.g. a burka); its covering stands in. |
| `frame.HeadgearShown`, `frame.FullHeadCovered` | Headgear is drawn; it covers the whole head (FullHead group: marine helmets and the like). |
| `frame.HairShown`, `frame.FaceHidden` | The game draws the hair now; the game leaves out the eyes. |
| `frame.SkipFlags` | The game's head render skip flags as the person is now (`(frame.SkipFlags & RenderSkipFlagDefOf.Hair) != 0`). |
| `frame.Skull`, `frame.Headless` | Dried to bones (a skull stands in); the game's own "has no head" state (IsoRim still draws the head). |
| `frame.Lying`, `frame.Crawling`, `frame.Seated`, `frame.Dead`, `frame.Carried`, `frame.Lift`, `frame.BodyShown` | Pose and state; `BodyShown` is false in a bed that shows only the head. |
| `frame.AttachToHead(ModelHandle model, Matrix4x4 local, Color colour, string swatch = null)` / `AttachToBody(...)` | Draws a model with the figure, `local` in the head / body frame. It follows pose and facing and is lit, outlined and shadowed like the figure's own parts (and moves with it on stacked floors and in `DrawFigure`). |
| `frame.AttachMeshToHead(Mesh mesh, Material material, Matrix4x4 local)` / `AttachMeshToBody(...)` | The same for your own mesh and material (no outline). |

The frame is only valid during the call: IsoRim reuses it for the next figure.

```csharp
static ModelHandle horn;
static void Init(ModContentPack content)                      // e.g. from your Mod constructor, after IsoRim loaded
{
    if (IsoRim.Api.Version < 8) return;
    horn = IsoRim.Api.LoadModel(content, "Models/horn.obj");   // faces under a "stuff..." material take the colour
    IsoRim.Api.AddFigureHook(f =>
    {
        if (f.HeadHidden || f.FullHeadCovered || f.Skull) return;   // under a full helmet: hidden
        Vector3 top = f.HeadCentre + new Vector3(0f, f.HeadSize.y * 0.5f, 0f);
        f.AttachToHead(horn, Matrix4x4.TRS(top, Quaternion.identity, Vector3.one * 0.2f), f.Pawn.story.HairColor);
    });
}
```

## Part 2: generic drawing

| Member | What it does |
|---|---|
| `Api.ModelsOn` | The 3D view is on and IsoRim draws models (its option). |
| `Api.LoadModel(ModContentPack mod, string path)` | Loads an OBJ (and its .mtl) from your mod's folder once; `null` if missing. `Api.LoadModel(string path)` finds the mod from the calling assembly. |
| `handle.Min`, `handle.Max` | The model's box in its own space. |
| `Api.DrawModel(ModelHandle m, Vector3 position, float yaw, Vector3 scale, Color colour, string swatch = null)` | Draws a model this frame as IsoRim draws its moving models. `position` true space, `yaw` degrees about the vertical, `scale` per axis. Does nothing while the 3D view is off. |
| `Api.DrawModel(ModelHandle m, Vector3 position, float yaw, Matrix4x4 local, Color colour, string swatch = null)` | The same with a whole placing in the model's own space (cells, true heights) before the turn and move. |
| `Api.StuffSwatch(ThingDef stuff)` / `Api.StuffSwatch(Thing thing)` | "Wood", "Stone", "Metal" or "Fabric": the surface IsoRim gives models of that stuff; pass it as `swatch`. |
| `Api.SpriteColour(Thing thing)` | The mean colour of a thing's own sprite, as IsoRim colours models from sprites. |
| `Api.DrawFigure(Pawn pawn, Vector3 position, float yaw, Matrix4x4 local, bool shadow)` | Draws a pawn's 3D figure (person, animal or mech; pass a corpse's `InnerPawn`) elsewhere, posed as IsoRim poses it now, without held or carried things. Its own placing is taken out; `local` is applied in the figure's own space (point under it at the origin, facing +z), then `yaw` and `position`. `shadow` false leaves out its ground blob. Returns false if it is not drawn as a figure now: draw its sprite then. |
| `Api.FigureBounds(Pawn pawn, out Vector3 min, out Vector3 max)` | The box the figure fills in that own space, measured without drawing. For fitting it into a slot. |

Call the drawing members from draw-time code (a `Draw`/`DrawAt` override, a `MapComponentUpdate`), every frame you want
the thing shown.

Stacked floors (version 10): a model drawn on the rows of an upper floor shows as that floor shows. On a hidden floor it is
not drawn; on a floor shown faded while an addon peeks (`Api.PeekUpperFloors`) it is faded like that floor's own things,
also when you draw it outside a thing's draw (from a `MapComponentUpdate`). So draw every level's models and let IsoRim
hide or fade them; before version 10 such a model drew solid on a faded floor (leave the levels over the viewed one out
there).

```csharp
// A crate model on a shelf top and a stored corpse lying beside it, each frame.
void DrawContents(Thing shelf, Corpse corpse, float shelfTop)
{
    if (IsoRim.Api.Version < 8 || !IsoRim.Api.ModelsOn) { /* draw sprites as usual */ return; }
    Vector3 c = shelf.TrueCenter();
    IsoRim.Api.DrawModel(crate, new Vector3(c.x - 0.3f, shelfTop, c.z), shelf.Rotation.AsAngle, Vector3.one * 0.5f,
        shelf.DrawColor, IsoRim.Api.StuffSwatch(shelf));
    Pawn body = corpse.InnerPawn;
    if (IsoRim.Api.FigureBounds(body, out Vector3 lo, out Vector3 hi))
    {
        float fit = Mathf.Min(1f, 0.9f / (hi.z - lo.z));
        Matrix4x4 local = Matrix4x4.Scale(Vector3.one * fit) * Matrix4x4.Translate(new Vector3(-(lo.x + hi.x) / 2, -lo.y, -(lo.z + hi.z) / 2));
        if (!IsoRim.Api.DrawFigure(body, new Vector3(c.x + 0.3f, shelfTop, c.z), 90f, local, false))
            corpse.DrawNowAt(new Vector3(c.x + 0.3f, 0f, c.z));
    }
}
```

## Part 3: apparel models (version 9)

A mod can give any apparel its own 3D model with an `IsoRim.ApparelModelDef` in its Defs. Apparel with a binding is drawn
with it on IsoRim's person figures (and outfit stands); apparel without one keeps IsoRim's own look. A binding whose
apparel is not loaded is skipped quietly. Since the def type only exists from version 9, a mod that must also load on an
older IsoRim adds these defs through a patch operation of its own that checks `IsoRim.Api.Version` first (the VFE Medieval
2 pack's `PatchOperation_IfIsoRimApi` is an example).

| Field | What it does |
|---|---|
| `apparel` | The apparel ThingDef's defName. |
| `slot` | `Head`, `Face`, `Eyes`, `Neck`, `Body`, `Belt` or `Shield` (below). |
| `model` | The OBJ file (and its .mtl), relative to your mod's folder. For a def added by a patch (it has no mod), the first active mod that has the file. |
| `bodyModels` | Body: `<male>`, `<female>`, `<thin>`, `<fat>`, `<hulk>` model paths, each made on that build; `model` is used for builds without one. |
| `hides` | Head: `<li>Hair</li>`, `Beard`, `Face`: also hidden while it is drawn, on top of the game's own rules (renderSkipFlags, FullHead / UpperHead). |
| `visor` | Head: IsoRim's dark visor strip, as on its sealed helmets. |
| `under` | Body on the Shell layer: how a flak vest under it shows: `Hidden` (default), `FrontPlate`, `Whole`. |
| `mount` | Belt: `belt` (default: stretched round the outermost waist), `hip` or `pack`, as `IsoRim.WornGearDef`. |
| `offset`, `rotation`, `scale` | A shift, a turn (Euler degrees) and a size applied to the model in its own space first (Shield: about the grip). |
| `alwaysShown` | Shield: shown whenever the person stands (default: see below). |
| `spriteColour` | Colour the `stuff` faces with the mean of the worn sprite instead of the item's colour. |

Colours are as for other IsoRim models: faces under a material whose name starts with `stuff` take the item's colour (its
stuff, its dye or the colour its mod gives it, as the game tints the sprite); the others keep their own `Kd`. Faces under a
`flame...` material are taken out and burn with IsoRim's live flame (a torch).

Slots and frames (the file's axes as IsoRim's `person_*.obj`: one unit = one cell, +y up, the figure faces -z of the file):

- **Head**: made on `person_head.obj` (same origin); drawn as the hat, widened with the head type. One hat at a time, as
  IsoRim's own (an Overhead one wins). Hair, beard and eyes show under it as the game's own rules say, plus `hides`.
- **Face / Eyes / Neck**: the same frame; drawn with a hat (a mouth or face item, an eye cover, a collar).
- **Body**: made on `person_<build>_upper.obj` / `_lower.obj` of each build, drawn with the body type's scale. On the Shell
  layer it is the coat (closed coats and armour hide what is under them as IsoRim's own); on the Middle layer a shape over
  the shirt, which keeps its colour; on the OnSkin layer it is the shirt (the shirt and leg parts take its colour).
  On a layer the game draws over Shell (by the layer's drawOrder, e.g. VE Framework's `VFEC_OuterShell` tabards and
  mantles) it is drawn over the coat or armour, as in 2D: made on the bare build, it is pushed out over whatever is worn
  under it.
- **Belt**: made in the `person_belt_*` frame (the band round y 0.27..0.34); placed by `mount`.
- **Shield**: the grip at the origin, the face toward -z of the file. Held on the left forearm, a little in front of the
  body's side at hand height, its face to the front and outward, following facing, walking and sitting. By default it shows
  only while the game shows the person's weapon (drafted, aiming, a job or duty that shows weapons) and, for apparel that
  draws its own worn picture (VE Framework's `Apparel_Shield`), while the game draws it (so not with a two-handed weapon);
  that worn picture gives way to the model. Lying and in bed it is not shown.

```xml
<!-- a hat, a coat with a model per build, a belt and a shield -->
<IsoRim.ApparelModelDef>
  <defName>MyMod_R3D_Hood</defName>
  <apparel>MyMod_Apparel_Hood</apparel>
  <slot>Head</slot>
  <model>Models/my_hood.obj</model>
  <hides><li>Hair</li></hides>
</IsoRim.ApparelModelDef>

<IsoRim.ApparelModelDef>
  <defName>MyMod_R3D_Gambeson</defName>
  <apparel>MyMod_Apparel_Gambeson</apparel>
  <slot>Body</slot>
  <model>Models/gambeson_male.obj</model>
  <bodyModels>
    <female>Models/gambeson_female.obj</female>
    <thin>Models/gambeson_thin.obj</thin>
    <fat>Models/gambeson_fat.obj</fat>
    <hulk>Models/gambeson_hulk.obj</hulk>
  </bodyModels>
  <under>FrontPlate</under>
</IsoRim.ApparelModelDef>

<IsoRim.ApparelModelDef>
  <defName>MyMod_R3D_PouchBelt</defName>
  <apparel>MyMod_Apparel_PouchBelt</apparel>
  <slot>Belt</slot>
  <model>Models/pouch_belt.obj</model>
  <mount>belt</mount>
</IsoRim.ApparelModelDef>

<IsoRim.ApparelModelDef>
  <defName>MyMod_R3D_Buckler</defName>
  <apparel>MyMod_Shield_Buckler</apparel>
  <slot>Shield</slot>
  <model>Models/buckler.obj</model>
  <offset>(0, 0.04, 0)</offset>      <!-- optional: a little higher on the arm -->
  <rotation>(0, -10, 0)</rotation>   <!-- optional: turned a little more outward -->
</IsoRim.ApparelModelDef>
```

## Part 4: apparel decals (version 10)

A mod can print pictures on worn garments of IsoRim's person figures: an emblem, a faction's colours, a number. Register a
provider for an apparel def (or for all apparel). IsoRim asks it for each worn item that shows as a body garment of its own
(a coat or armour, a garment drawn over the coat, a middle-layer or shirt shape; bound by an `ApparelModelDef` or IsoRim's
own shape) whenever it works out how the wearer looks: about every 1.5 seconds, and at once after
`Api.RefreshApparelDecals(pawn)`. Not every frame, so the provider may do some work. A decal is a rectangle of the game's
body sprite of one side, projected straight onto the faces of the garment that look that way: it follows pose, facing,
build, walking and stacked floors, shows only on the garment's own surface (inside its outline) and is hidden wherever the
garment is (under something worn over it, a hidden floor). Decals are drawn in the order added, each over the one before.

| Member | What it does |
|---|---|
| `Api.AddApparelDecals(ThingDef apparel, ApparelDecalProvider provider)` | Registers a provider for that apparel (null: every apparel). Twice does nothing. |
| `Api.RemoveApparelDecals(ThingDef apparel, ApparelDecalProvider provider)` | Removes it. |
| `Api.RefreshApparelDecals(Pawn pawn)` | Asks the providers again for that person on the next frame it is drawn. |
| `delegate void ApparelDecalProvider(Apparel apparel, Pawn wearer, List<ApparelDecal> decals)` | Add zero or more decals to `decals` (IsoRim clears and reuses the list). |
| `decal.material` | The material drawn, as is (a game `Graphic`'s `MatSouth`, one from `MaterialPool`, your own). |
| `decal.texture`, `decal.mask`, `decal.colour`, `decal.colourTwo` | Without a material: the picture, drawn with the game's `Cutout` shader tinted by `colour`, or with `CutoutComplex` when `mask` is set (`colour` where the mask is red, `colourTwo` elsewhere, as the game's masks). |
| `decal.side` | `DecalSide.Front` (placed as on the south body sprite) or `DecalSide.Back` (the north sprite, seen from behind). |
| `decal.centre`, `decal.size` | The picture's middle and size on that body sprite, in cells from the sprite's middle (the pawn's draw position): x to the right as the viewer of that side sees it, y up the picture. The adult body quad is 1.5 by 1.5 (the default size); a render node's offset `(x, 0, z)` is `centre (x, z)`. |
| `decal.wrap` | Also on the faces that turn away from that side (the garment's top and sides), the picture's edge stretched over them: a front and a back decal over the whole quad colour the whole garment. Default false: only the faces that look toward that side. |

Placing follows the game's sprite: the body sprite's feet land on the figure's feet, its chin line on the neck, its width
across the body (as `FigureFrame.BodySprite`, part 1), per body type. So offsets and sizes from a 2D render node carry
over unchanged. A provider's type only exists from version 10: keep it in code that runs only after a version check, and
create the delegate with `new IsoRim.ApparelDecalProvider(Method)` (a plain method group or lambda is cached by the
compiler in a static field of that type, which the game's start-up check reads even on an older IsoRim).

```csharp
// A guild emblem on the front and back of a coat, in the wearer's faction colour.
static Texture2D emblem;
static void Init()                                   // after defs load (a StaticConstructorOnStartup class)
{
    if (IsoRim.Api.Version < 10) return;
    Register();
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static void Register()
{
    emblem = ContentFinder<Texture2D>.Get("MyMod/Emblem");
    IsoRim.Api.AddApparelDecals(ThingDef.Named("MyMod_Apparel_GuildCoat"), new IsoRim.ApparelDecalProvider(Emblems));
}

static void Emblems(Apparel coat, Pawn wearer, List<IsoRim.ApparelDecal> decals)
{
    Color c = wearer.Faction != null ? wearer.Faction.Color : Color.white;
    decals.Add(new IsoRim.ApparelDecal { texture = emblem, colour = c, side = IsoRim.DecalSide.Front,
        centre = new Vector2(0f, -0.15f), size = new Vector2(0.3f, 0.3f) });
    decals.Add(new IsoRim.ApparelDecal { texture = emblem, colour = c, side = IsoRim.DecalSide.Back,
        centre = new Vector2(0f, -0.1f), size = new Vector2(0.3f, 0.3f) });
}
```

The VFE Medieval 2 pack's `Heraldry.cs` uses it to print each faction's heraldry (VE Framework render nodes asked for their
2D graphic) on the 3D tabard.

## Part 5: wall tops (version 11)

A one-cell linked wall (the game's `linkType` Basic or CornerFiller, fill 0.2 or more) can be drawn on IsoRim's own wall
path, the block every vanilla wall is, with its own height, a side texture cut from game textures at run time and a top
shaped by a rule: a castle wall's merlons, a low wall's ridge, a damaged wall's broken stubs. No model file is needed.
Declare it with a `wallTop` block on an `IsoRim.ModelDef` that names the thing (`thingDef`; no `model`).

Because the wall stays a block, it joins vanilla walls, doors and other blocks with no seams and no lines inside a run, its
side texture runs on along the map, and everything that asks IsoRim about walls (cut-away, floors built over it, fixtures
on it, the fog lid, rooms, stacked-floor wall columns) treats it as a block of its body height: fill 0.9 or more is a full
wall (cut by the cut-away), lower is a low wall (never cut, not a full wall). The top is a height field on a 1/16 cell
grid over the body. It depends only on which sides join a neighbour (one at least as tall as built, or a door), which
corners are inner corners, the map position, and the rule's numbers. So a run, an L, a T, a cross and a run's end all come
out of one rule, and the faces, rims and end lines of the raised parts follow WallBoxes' own outline rules, across cell
borders too. What stands above the body is the crown: it is left out where a floor is built right over the wall
(nothing rises above the wall height there), hidden while the cut-away lowers the wall, and clipped under a roof and in
the fog. Where a run ends against a lower block and either is a wall top, its end line starts at that block's top, so no
line runs down the joint where both faces lie flush.

With rule `Rubble` on a standable thing the game does not draw as a wall (fill 0, linked), the thing becomes
`Form3D.Ground`: a low shape on the ground drawn by the walls layer, joined to the walls, doors and rubble round it. It is
not a wall anywhere (no wall height, never cut, rooms see through it). Pawns walk over it at ground level.

| Field (`wallTop`) | Meaning |
|---|---|
| `wallHeight` | The wall height (cells) the heights below are given for; they follow the walls drawn now. Default 1.65. |
| `height` | The body's top (cells). -1 (default): the game's fill times the wall height. Ignored for Ground. |
| `rule` | `None` (a plain block with this height and texture), `Merlons`, `Ridge`, `Ragged` or `Rubble`. |
| `rise` | Merlons, Ridge: how far the raised part stands over the body (cells). |
| `band` | Merlons, Ragged: how deep the edge parts reach in from an open side; Ridge: how far the ridge keeps from an open side (cells, not scaled). Default 0.25. |
| `period`, `length` | Merlons: one merlon every `period` cells along a side, on the map grid, each `length` long; a square one in every outside and inner corner. Defaults 0.5, 0.25. |
| `segment` | Ragged: the length of one stub; Rubble: the stone and edge grid (cells, map grid). Default 0.25. |
| `steps` | Ragged: the stub heights picked from by map position; Rubble: the lowest and highest stone (cells). |
| `chance`, `bed`, `inset` | Rubble: share of grid squares with a stone (0.35), the slab's thickness (0.035), how far an open edge is set in, once or twice by map position (0.0625). |
| `texture`, `strips` | The side texture: strips (`texture` optional, else the spec's; `x`, `y`, `width`, `height` in pixels from the top left of the image file; `shift` pixels moved right, wrapping) cut from game textures at run time and laid top to bottom into one tile. All as wide as the first. None: plain sides. |
| `tile` | The side texture covers `tile.x` cells across and `tile.y` cells up. Default (0.5, 0.55). |
| `brightness` | The side texture's colour: the thing's colour times this. Default 1. |
| `topShade`, `partShade`, `sideShade` | The cap and raised tops (0.92), Rubble's stones' tops (-1: `topShade`), sides without a texture (0.8): the thing's colour times these. |

```xml
<!-- A castle wall: full height, merlons along every open side, bricks cut from the mod's own atlas. -->
<IsoRim.ModelDef>
  <defName>MyPack_CastleWall</defName>
  <thingDef>MyMod_CastleWall</thingDef>
  <wallTop>
    <height>1.65</height>
    <rule>Merlons</rule>
    <rise>0.22</rise>
    <band>0.1875</band>
    <texture>Things/Building/Linked/MyCastleWall_Atlas</texture>
    <strips>
      <li><x>337</x><y>252</y><width>40</width><height>18</height></li>
      <li><x>337</x><y>270</y><width>40</width><height>6</height></li>
    </strips>
    <tile>(0.5, 0.275)</tile>
  </wallTop>
</IsoRim.ModelDef>
```

An older IsoRim does not know the `wallTop` field and logs an XML error for it, so a pack that supports older versions
keeps the entry without `thingDef` and `wallTop` in its Defs and adds both with a patch that runs only on version 11 or
later (the VFE Medieval 2 pack's `Patches\VFEM2_CastleWalls.xml`, with its `PatchOperation_IfIsoRimApi`). Add fields to an
entry in the Defs folder; do not add a whole entry by patch (it has no mod of its own).

## Part 6: upright drawing scope (version 12)

IsoRim lays the game's flat 2D drawing on the ground or on top of the 3D model it belongs to, and stands status icons
and bubbles up facing the camera. A mod can ask for the same for its own flat drawing: a progress bar, a sign, an icon.
While an upright scope is open, the flat meshes IsoRim draws for the game stand up as one card facing the camera, at the
height you give. That covers `GenDraw.DrawFillableBar`, `GenDraw.DrawMeshNowOrLater`, a `Graphic`'s `Draw`, overlays, and
meshes drawn with `Api.DrawMesh`. A plain `UnityEngine.Graphics.DrawMesh` in your own code does not go through IsoRim:
call `Api.DrawMesh` instead.

| Member | What it does |
|---|---|
| `Api.BeginUpright(float height)` | Opens a scope. The card is hinged on the first mesh drawn in it; the other meshes keep their place round it as the game lays them out from above (east to the right, north up the card). |
| `Api.BeginUpright(float height, Vector3 pivot)` | The same, hinged on `pivot` (x and z in map cells). |
| `Api.EndUpright()` | Closes the innermost scope. With none open it does nothing. |
| `Api.Upright(float height)`, `Api.Upright(float height, Vector3 pivot)` | Open a scope and return an `UprightScope` (`IDisposable`) for a `using` block. Disposing it closes the scope and any left open inside it; a second Dispose does nothing. |
| `Api.DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer = 0)` | Draws a flat mesh as IsoRim draws the game's own: stood up in a scope, raised onto the model while its thing is drawn, moved onto its stacked floor. Without the 3D view it is `Graphics.DrawMesh`. |

- **Height** is in cells (true heights, part "Spaces") over the ground of the floor the thing stands on. Don't divide it by
  `Api.HeightSquash`: IsoRim does that. `Api.HeightSquash` stays the factor for your own drawn-space maths (drawn y = true
  height / `HeightSquash`), for example for a model you draw yourself.
- The card follows the camera's turn and tilt every frame. On a stacked floor it stands on that floor and is hidden with
  it. The peek fade treats it as the game's own flat drawing there: a material with a faded form fades, others (the
  game's solid-colour bar materials, overlay icons) stay solid.
- Scopes nest: the inner one is in charge until it ends, then the outer one is back. When a scope ends, IsoRim's own
  drawing state is back as it was before it (a thing's lift onto its model, for example).
- A scope left open (a forgotten End, an exception) is closed when the thing whose drawing opened it is done, or, outside
  a thing's drawing, by the next frame. IsoRim logs a warning once.
- Without the 3D view a scope changes nothing (Begin and End still pair up). Unused, it costs nothing.
- Call it on the main thread while drawing: a thing's `DrawAt`, a comp's `PostDraw`, a `MapComponentUpdate`.

Example: stand a building's two progress bars up 1.95 cells over the floor, and keep the mod working on an IsoRim older
than version 12 (the bars stay flat there). The Api calls are in methods of their own, which only run once the version
check has passed:

```csharp
[StaticConstructorOnStartup]
public static class IsoRimBars
{
    static IsoRimBars()
    {
        if (ModsConfig.IsActive("Peren.IsoRim17") && ApiVersion() >= 12)
        {
            new Harmony("me.mymod.isorim").Patch(AccessTools.Method(typeof(Building_MyStove), "DrawProgressBar"),
                prefix: new HarmonyMethod(typeof(IsoRimBars), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(IsoRimBars), nameof(Postfix)));
        }
    }

    private static int ApiVersion() => IsoRim.Api.Version;

    public static void Prefix() => IsoRim.Api.BeginUpright(1.95f);

    public static void Postfix() => IsoRim.Api.EndUpright();
}
```

A mod that does not reference `IsoRim.dll` can find the same public members by name: read `IsoRim.Api.Version`, then
make delegates for `BeginUpright(float)` and `EndUpright()` with `AccessTools.Method` and `AccessTools.MethodDelegate`.

## Part 7: attachments (version 13)

Draw one thing on another thing's 3D model, following it: a rider on a mount, a passenger in a vehicle seat, someone
carried on a back. It has two layers.

**1. Read a host's drawn place.** `Api.ReadHost(Thing host, out HostTransform h)` gives where IsoRim draws a thing now:

| `HostTransform` field | Meaning |
|---|---|
| `position` | Its base point, true space: x and z in map cells (its own floor's rows, as `DrawModel` takes them), y its height over its floor (in a jump, carried, flying; 0 on the ground). |
| `yaw` | The way it is drawn facing, degrees clockwise from north (a model's turn as drawn, a card's facing). |
| `bob` | The rise of its body in step now, cells (already in the anchors). |
| `scale` | Cells per model unit of its drawn model (1 for a card or flat thing). |
| `model` | Drawn as a 3D model now (false: an upright card or the game's flat drawing). |
| `floor`, `shown`, `fade` | Its stacked floor (0 the ground or no floors, -1 between floors), whether that floor is drawn, and its peek fade slot (0 solid, above 0 faded). |
| `floorOffset` | Its floor's and the floor layout's own offset (a pawn on a stair step), true space. |
| `back`, `top`, `backAt` | Heights over `position.y` of the `Back` and `Top` anchors (bob included), and the back's point in the host's own space. |
| `Point(local)`, `Anchor(anchor, offset)` | A point of the host's own space (x right, y up, z forward; cells), or an anchor plus offset, in true space. |

`Api.AnchorPoint(host, anchor, offset, out point)` is the short form. Anchors (`AttachAnchor`):

- `Back`: an animal model's back, measured once per model by a ray straight down through its body at the middle of its
  trunk (between its hips); `backHeight` in the animal's `ModelDef` (model units) overrides it. A person figure: its
  shoulders. A building: its surface (`surfaceHeight`). A card (no model, or models off): estimated from the sprite size.
- `Top`: the highest point of the host as drawn (a card: estimated).
- `Ground`: the host's base point; place by hand with the offset.

**2. Attachments.** Register a provider that says what a thing is attached to:

```csharp
IsoRim.Api.AddAttachments(Func<Thing, Attachment?> provider);    // RemoveAttachments to take it back
```

IsoRim asks it for the pawns it draws or measures while the 3D view is on, often (several times a frame per pawn, also
in game ticks), so keep it cheap; return null for "not attached". The first provider with an answer wins. With none
registered nothing of this runs. An `Attachment` has:

| Field | Meaning |
|---|---|
| `host` | What it is attached to: a spawned thing on the same map (anything else is ignored). |
| `anchor`, `offset` | Where: a named anchor plus an offset in the host's own space (x right, y up, z forward; cells). |
| `pose` (`AttachPose`) | `Stand` (feet on the anchor), `Sit` (seated as on a chair), `SitNoLegs` (hips on the anchor, legs left out: a rider), `Lie` (on its back along the host). |
| `facing` (`AttachFacing`) | `Host` (the host's facing), `Aim` (the host's facing, turned to the target while aiming, as standing figures do), `Own` (its own rotation, turned to the target while aiming). |
| `turn` | Degrees added to the host's facing (a seat facing sideways). |
| `leads` | The attached pawn leads the pair's movement (a rider steering its mount): the offset a floor layout gives it while it takes a stair, ladder or lift step is drawn for the host too. False: it takes the host's offset (a vehicle seat). |

What follows from it, in one place (`Attach.cs`, through `Poses`):

- The pawn is drawn on the anchor, following the host's drawn place, facing, walking bob, floor and peek fade. People
  figures take every pose; animal models stand or lie on the anchor; pawns drawn as cards stand (or sit, a little sunk)
  on it.
- Its `DrawPos` (x and z) is the anchor's while the 3D view is on, so the selection brackets, motes, aim lines and the
  game's other uses of the drawn position follow it.
- Picking and box selection, the name label (hung from the anchor), the held weapon and where its shots start, the
  bubbles over it and first person all follow the figure on the host. The pointer picks the host's own body below the
  attached pawn's figure.
- With the 3D view off nothing changes: the game's own drawing.

`Api.AttachmentOf(thing)` gives the providers' answer; `Api.AttachedPlace(thing, out point, out yaw)` where IsoRim puts
an attached thing now. Things that are not pawns are not drawn by IsoRim through attachments: draw your own model with
`Api.DrawModel` at `Api.AnchorPoint` / `HostTransform.Anchor`.

Example: riders of a mounting mod sit on their mounts' backs, turning to aim (the IsoRim Waylith - Giddy-Up Pack does
this). The Api 13 types live in a class of their own, so an older IsoRim never loads them:

```csharp
[StaticConstructorOnStartup]
public static class IsoRimRiders
{
    static IsoRimRiders()
    {
        if (ModsConfig.IsActive("Peren.IsoRim17") && ApiVersion() >= 13)
        {
            Riders.Register();
        }
    }

    private static int ApiVersion() => IsoRim.Api.Version;
}

internal static class Riders
{
    // Made from the MethodInfo on purpose: a plain method group (AddAttachments(Of)) is cached by the compiler in a static
    // field of type Func<Thing, Attachment?>, and the game's startup scan of static fields fails on it when IsoRim is older
    // than 13 (an error in the log, though nothing else breaks).
    internal static void Register() => IsoRim.Api.AddAttachments((Func<Thing, IsoRim.Attachment?>)Delegate.CreateDelegate(
        typeof(Func<Thing, IsoRim.Attachment?>), AccessTools.Method(typeof(Riders), nameof(Of))));

    private static IsoRim.Attachment? Of(Thing t)
    {
        Pawn mount = t is Pawn rider ? MyMounts.MountOf(rider) : null;   // your mod's own lookup; keep it cheap
        if (mount == null)
        {
            return null;
        }
        return new IsoRim.Attachment(mount, IsoRim.AttachAnchor.Back, IsoRim.AttachPose.SitNoLegs, IsoRim.AttachFacing.Aim) { leads = true };
    }
}
```

If your mod draws the attached pawn itself somewhere else (inside the host's render tree, say), switch that off while
`Api.ViewReady` is true, so the pawn is drawn once, on its own path, where IsoRim puts it.

## Part 8: animal wings (version 14)

Any animal drawn as an IsoRim model (an `IsoRim.ModelDef` with `<animal>true</animal>`, IsoRim's own or one in your
mod's Defs) can have wings. IsoRim draws one wing model at each hinge: folded on the ground, spread and beating in the
air. They open as the animal takes off and close as it lands, following the game's own take-off and landing curves
(`Pawn_FlightTracker.PositionOffsetFactor`). They also open in mid-jump, while the animal rides in a `PawnFlyer` (jump
abilities, an addon's leap). They stay folded when it sleeps, is downed or is dead. In the air the legs are tucked away.
Animals without wings cost nothing and only get the leg tuck.

### The wing OBJ

One file, the **right** wing, modelled **spread**:

- the hinge (where it joins the body) at the origin;
- the span along **+x** (the tip at the largest x);
- the leading edge toward **+z** (the animal's front; IsoRim flips the file's z as for every model, so in the file the
  front is -z, as in IsoRim's own animal OBJs);
- a thin **closed** plate, about 0.01 cells thick or more, so it gets the dark outline like the body (thinner plates and
  pieces under `outlineMinSize` get none). Solid colours only (no transparency).

IsoRim makes the left wing itself: the mirror image, used for every hinge with x < 0. Paths resolve against your own
mod's folder, as for `model` and `legModel`, and `mtllib` lines resolve next to the OBJ: ship the `.mtl` files your
OBJs name (copy `isorim.mtl` too if you use its colours).

### Fields

| Field | What it does | Default |
|---|---|---|
| `wingModel` | The wing OBJ above, relative to your mod's folder. | none (no wings) |
| `wings` | The hinge points on the body, facing north like `legs`, in cells before the animal's size. List both sides; x < 0 is a left wing. | none (no wings) |
| `wingFold` | On the ground: degrees the wings are swept back about the upright axis (90: tips straight back). | 90 |
| `wingDroop` | On the ground: degrees the tips are lowered, so they rest on the back (insects) or the flanks (birds). | 0 |
| `wingFlap` | In the air: degrees the wings beat up and down about the body's long axis (a sine). | 50 |
| `wingBeat` | In the air: beats per second at normal game speed. It counts game ticks: frozen when paused, faster at higher speeds. Insects about 6, big birds about 2. | 6 |

Each animal beats at its own point in the cycle, so a swarm or flock is not in step.

### A pack's own animal

Your mod (loaded after IsoRim) ships the OBJ files and a def:

```
YourMod/
  Defs/IsoRimModels.xml
  Models/my_bird.obj        body, facing north, standing on y = 0
  Models/my_bird_leg.obj    one leg, hip at the top
  Models/my_bird_wing.obj   the right wing, spread (above)
  Models/my_bird.mtl        the materials the OBJs name
```

```xml
<Defs>
  <IsoRim.ModelDef>
    <defName>MyMod_Animal_MyBird</defName>
    <thingDef>MyBird</thingDef>
    <model>Models/my_bird.obj</model>
    <animal>true</animal>
    <legModel>Models/my_bird_leg.obj</legModel>
    <legs>
      <li>(-0.03, 0.09, 0.00)</li>
      <li>(0.03, 0.09, 0.00)</li>
    </legs>
    <legLength>0.09</legLength>
    <stride>0.28</stride>
    <bodyCentre>0.17</bodyCentre>
    <bodyHalfWidth>0.07</bodyHalfWidth>
    <bodyHalfLength>0.21</bodyHalfLength>
    <wingModel>Models/my_bird_wing.obj</wingModel>
    <wings>
      <li>(-0.05, 0.22, 0.04)</li>
      <li>(0.05, 0.22, 0.04)</li>
    </wings>
    <wingFold>95</wingFold>   <!-- tips back along the flanks -->
    <wingDroop>25</wingDroop>
    <wingFlap>55</wingFlap>
    <wingBeat>3</wingBeat>
  </IsoRim.ModelDef>
</Defs>
```

IsoRim's own Locust (`Defs/Models.xml`, `R3D_Animal_Locust`) is a complete example with folded insect wings. The wing
fields are new in version 14. An older IsoRim reports them as unknown XML fields, so put such defs in a folder that
only loads with a new enough IsoRim (for example a LoadFolders entry), or require IsoRim 14 or later.

### Api.AirborneFactor

| Member | What it does |
|---|---|
| `Api.AirborneFactor(Pawn pawn)` | How far the pawn is into the air, 0 (on the ground) to 1 (fully up). Flying: the game's take-off and landing curve. In mid-jump (carried by a `PawnFlyer`): eased in over the first fifth of the jump and out over the last, 1 in between. 0 for a dead pawn and for null. |

IsoRim opens the wings (fully from 0.3) and tucks the legs (from 0.25) by this value. A mod that draws its own parts on
an animal (a rider, a harness, a glow) can follow the same blend:

```csharp
if (IsoRim.Api.Version >= 14)
{
    float air = IsoRim.Api.AirborneFactor(pawn);   // 0 on the ground .. 1 in the air
}
```

## Part 9: aged look (version 15)

`<aged>` on an `IsoRim.ModelDef` draws its models old and rusty, as the game draws its ancient ruins: a ruined copy of a
building can reuse the building's own model. The value is the strength, 0 (off, the default) to 1; IsoRim's ancient
furniture uses 0.6.

```xml
<IsoRim.ModelDef>
  <defName>MyPack_RuinedLathe</defName>
  <thingDef>MyRuinedLathe</thingDef>
  <model>Models/lathe.obj</model>
  <aged>0.6</aged>
</IsoRim.ModelDef>
```

What it does, all once when the model is loaded (no cost while drawing):
- colours not tinted by the stuff are dulled: most of the way to their own grey (a touch olive) and somewhat darker;
- the stuff colour of "stuff" parts is dulled the same way, so the stuff still reads; a thing made of no stuff (and with
  none in its cost list) whose sprite is drawn white takes old grey metal there, on the Metal swatch;
- textured faces get a rusty copy of their texture (rust flakes and grime, the same on every run; more rust with a higher
  value); faces with no texture are only dulled;
- glowing faces are switched off.

It applies to every body model of the def (`model`, `models`, `byAppearance`, `byStyle`, `byState`, `stackModels`,
`emptyModel`, `hackedModel`, `openModel`); glass, turret guns and spinning, bobbing or glowing parts keep their look.
Older IsoRim versions do not know the field (a config error), so a pack that supports them adds it by patch only when
`Api.Version >= 15`.

## Part 10: models bound by texture path (version 16)

An `IsoRim.ModelDef` with `<graphicPath>` gives its model to every thing whose `graphicData.texPath` is that path. It is
meant for defs the game makes itself, such as raw meat: the game makes one meat def per animal, vanilla and modded, but
they share five textures.

- The match is exact, ignoring case; `\` counts as `/` and a trailing `/` is ignored.
- Order: a `thingDef` entry wins, then `graphicPath`, then `thingCategory`, then `match`, then the small-plant fallback.
- Blueprints and frames are skipped. The other fields (`stackModels`, `overArt`, ...) work as in a `thingDef` entry.
- Model parts with a `stuff_` material take the thing's colour. For raw meat that is the race's `meatColor`, as on the
  vanilla sprite.
- Binding happens once at startup and costs nothing while drawing.

Example, IsoRim's own raw meat (Defs/Models.xml):

```xml
<IsoRim.ModelDef>
  <defName>R3D_Meat_Big</defName>
  <graphicPath>Things/Item/Resource/MeatFoodRaw/Meat_Big</graphicPath>
  <stackModels>
    <li>Models/item_meat_big_a.obj</li>
    <li>Models/item_meat_big_b.obj</li>
    <li>Models/item_meat_big_c.obj</li>
  </stackModels>
  <overArt>true</overArt>
</IsoRim.ModelDef>
```

An older IsoRim does not know the field and logs an XML error for it. A pack that supports older versions keeps the entry
without `graphicPath` in its Defs and adds the field with a patch that runs only on version 16 or later (see part 5).

## Part 11: complete walls for camera modes (version 17)

IsoRim's built-in first-person view temporarily suspends automatic wall cutaway. Overhead view continues to use the
player's settings. This does not change physical wall height, floors, movement, trees, roofs, or building-tool transparency.

An add-on camera can request the same behavior with `Api.RequestFullWalls(Map map, Pawn followedPawn = null)`. Call on
the main thread when entering the mode, for the current map in a running game. Keep the returned `IDisposable` until
leaving the mode, then dispose it. Each request is independent: releasing one never cancels another camera's request
or IsoRim's own first-person view. Switching display modes never writes the player's settings.

Requests expire when the viewed map or game changes, or when play ends. Passing a followed pawn also expires the request
if that pawn dies, despawns, is destroyed or leaves the map. An expired handle stays expired; request again on re-entry.
The change is applied at the next camera update. Null maps, requests outside the current playing map, and invalid followed
pawns throw argument/context exceptions. Calling `Dispose` repeatedly is safe, including after an automatic expiry.

```csharp
static IDisposable completeWalls;

// Call once when entering your camera mode. Isolate new API calls behind a version check for optional integrations.
static void EnterCamera(Map map, Pawn followedPawn)
{
    completeWalls?.Dispose();
    completeWalls = null;
    if (IsoRim.Api.Version >= 17)
        completeWalls = AcquireWalls(map, followedPawn);
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static IDisposable AcquireWalls(Map map, Pawn followedPawn) => IsoRim.Api.RequestFullWalls(map, followedPawn);

static void LeaveCamera()
{
    completeWalls?.Dispose();
    completeWalls = null;
}
```

Call `LeaveCamera` on your camera's normal exit and error/cleanup paths. Automatic expiry is a safety net for map/game
and followed-pawn changes; IsoRim cannot detect an arbitrary add-on camera's own exit. Content-only add-ons need no changes.

For a full mod build, including where to obtain the RimWorld 1.7 and Harmony references, see [Building](Building.md).

## Part 12: one-cell doors with their own models (version 18)

A one-cell door (`Building_Door`, not a supported door such as the vac barrier) can be drawn from an ordinary
`thingDef` entry: `model` is the frame, drawn fixed, and `doorLeafModel` is one leaf. Both are needed. The other leaf is
the same model turned half round. A style entry for the door (`style`, version 6) still wins over it.

- Build the models in door space: North rotation, so the doorway runs along x with walls east and west; origin at the
  cell centre on the ground; walls 1.2 high. IsoRim scales the height to the current wall height and cuts it down with
  the walls.
- The leaf spans x 0 to 0.5 from the doorway centre. By default the leaves slide apart into the walls as the game's
  movers do.
- `<doorLeafGather>` (0 to 1, default 0) makes them gather toward their jambs instead, like curtains drawn aside. Each
  leaf is squeezed along x about its jamb (x 0.5) to `1 - doorLeafGather * OpenPct` of its width.
- Materials work as for any model; `stuff_` parts take the door's stuff colour.

Example, IsoRim's own animal flap (Defs/Models.xml):

```xml
<IsoRim.ModelDef>
  <defName>R3D_AnimalFlap</defName>
  <thingDef>AnimalFlap</thingDef>
  <model>Models/animal_flap_frame.obj</model>
  <doorLeafModel>Models/animal_flap_leaf.obj</doorLeafModel>
  <doorLeafGather>0.78</doorLeafGather>
  <builtFor>(1, 1)</builtFor>
</IsoRim.ModelDef>
```

Before version 18 a `thingDef` entry on a one-cell door is not drawn, and `doorLeafGather` is an XML error. A pack that
supports older versions adds the field by patch only on version 18 or later (see part 5).

## Part 13: facing holds (version 19)

A moving figure (a person, an animal or a mech drawn as a model) normally turns to face the way it goes. A facing hold
makes it face the game's facing (`Thing.Rotation`) instead, also while it moves: a climber on a ladder faces the
ladder, going up and going down.

```csharp
// once, at startup (IsoRim.Api.Version >= 19)
IsoRim.Api.AddFacingHold(pawn => MyLadders.IsClimbing(pawn));
```

- `Api.AddFacingHold(Func<Pawn, bool> holds)`: while any added hold answers true for a pawn, its figure turns smoothly to
  the game's facing and keeps its legs together. Adding the same delegate twice adds it once.
- `Api.RemoveFacingHold(Func<Pawn, bool> holds)`: removes it again.

The hold is asked every frame the figure is drawn, so keep it cheap. Set the facing you want with the game's own means
(for example a patch on `Pawn_RotationTracker.UpdateRotation`); the hold only stops IsoRim from turning the figure to its
walking direction. An exception in a hold is logged once and counts as false. ProjectZ uses it for its ladders.

## Part 14: walls, view state, flat drawing, ghosts, arcs, x-ray placing (version 20)

### Wall cut rule: `ModelDef.cutaway`

`<cutaway>` takes `Auto` (default), `Never` or `Always` (enum `IsoRim.CutawayRule`) and decides whether the cut-away
(pointer hover, rooms, the player's cutHover / cutRooms options) lowers the thing:

- `Auto`: built walls are lowered: full walls, walls from a model (`linked` + `wallFit`) and, from version 20, **low walls**;
  natural rock follows the player's cutRock option; anything else (sandbags, barricades, fences) never.
- `Never`: never lowered (a glazed half wall that keeps its window).
- `Always`: always lowered, also a block IsoRim would not count as a wall (a tall linked fence drawn as a block).

Only things IsoRim draws as walls (blocks, walls from a model) are ever lowered; on other things the field does nothing.

A **low wall** is a wall IsoRim draws as a block (fill 0.9 or more, or linked as walls) standing lower than nine tenths of
the wall height: a model-less `ModelDef` whose `height` (or `wallTop` height) is under it, or a low fill (VFE Medieval 2's
low castle wall). From version 20 IsoRim treats it as such everywhere, with no patches needed:

- the cut-away lowers it (Auto; before version 20 low walls were never cut);
- a taller wall beside it draws its end line only from the low wall's top up (no line down the flush joint);
- it is not a full-height block for stacked floors: a wall standing on the floor above does not reach down to it (the
  opening between the low wall and the slab stays open, as a window) and the floor's slab is laid over it.

```xml
<!-- a half wall (a real wall to the game, fill 1) drawn 0.825 high that keeps its window under the cut-away -->
<IsoRim.ModelDef>
  <defName>R3D_MyHalfWall</defName>
  <thingDef>MyHalfWall</thingDef>
  <height>0.825</height>
  <cutaway>Never</cutaway>
</IsoRim.ModelDef>
```

Before version 20 `cutaway` is an XML error: add it by patch only on version 20 or later (part M).

### View state

| Member | What it does |
|---|---|
| `Api.ViewPerspective` | The view draws with a perspective camera now (its option, or first person); false for the flat camera or the view off. |
| `Api.FieldOfView` | The perspective camera's vertical field of view, degrees (first person: its own option); 0 when flat or off. |
| `Api.FirstPersonPawn` | The pawn whose eyes the view looks through (IsoRim's first person), or null. |
| `Api.ViewVersion` | A counter that goes up whenever how the view looks at the map changes: on or off, perspective or flat, field of view, `ViewYaw` or `ViewPitch` (every frame of a turn), first person on, off or to another pawn. Moving or zooming the camera does not count. |

```csharp
int seen = -1;
void Update()   // a post-processing mod
{
    if (IsoRim.Api.Version >= 20 && IsoRim.Api.ViewVersion != seen) { seen = IsoRim.Api.ViewVersion; ResetHistory(); }
}
```

### Flat drawing scopes and skipping 2D drawing under a model

The flat 2D meshes IsoRim draws for the game (a `Graphic`'s draw, `GenDraw.DrawMeshNowOrLater`, fillable bars, overlays,
and `Api.DrawMesh`; not your own `UnityEngine.Graphics` calls) can be changed for a stretch of drawing, on the same stack as
the upright scope (part 6): scopes nest, `End...` closes the innermost one, one left open is closed after the thing whose
drawing opened it or at the next frame, and with the 3D view off nothing changes.

| Member | What it does |
|---|---|
| `Api.BeginSkipFlat()` / `Api.EndSkipFlat()` / `using (Api.SkipFlat())` | Those meshes are not drawn. For an overlay your mod draws over a body a model stands in for (check `Api.ModelStandsIn(thing)` first). |
| `Api.BeginFlatTransform(Matrix4x4 m)` / `Api.EndFlatTransform()` / `using (Api.FlatTransform(m))` | Those meshes are drawn with `m` (drawn space) on top of their own matrix, in place of an outer upright scope or IsoRim's lift for the thing being drawn: a corpse's sprite laid on a shelf. |
| `Api.SkipWhenModelled(MethodBase method)` | IsoRim patches `method` (another mod's or the game's) so it is skipped while `ModelStandsIn` is true for its thing: the instance (a `Thing`, or a `ThingComp`'s parent), else its first `Thing` argument. For drawing methods (a comp's `PostDraw`, a `DrawAt`); a skipped method returns its type's default. False (and a warning) if it could not be patched. Call once at startup. |

```csharp
// once, at startup: a mod's "working" overlay is left out while IsoRim's model stands in
IsoRim.Api.SkipWhenModelled(AccessTools.Method(typeof(CompMyOverlay), nameof(CompMyOverlay.PostDraw)));

// or decide yourself, inside the draw
if (IsoRim.Api.ModelStandsIn(parent)) { using (IsoRim.Api.SkipFlat()) DrawBooks(); } else DrawBooks();

// a stored corpse's sprite, laid 0.4 cells up on a shelf
using (IsoRim.Api.FlatTransform(Matrix4x4.Translate(new Vector3(0f, 0.4f / IsoRim.Api.HeightSquash, 0f))))
    corpse.DrawNowAt(spot);
```

### Placing on stacked floors

| Member | What it does |
|---|---|
| `Api.DrawnMatrix(Matrix4x4 m)` | Where IsoRim draws a matrix given in the game's world space (drawn space) whose z may be a row of an upper floor: moved onto that floor (and by the layout's offset of the thing being drawn now). Unchanged without floors, on the ground and on a hidden floor. For meshes you draw with `UnityEngine.Graphics` yourself; `Api.DrawMesh`, `Api.DrawModel` and the game's own drawing are placed already. |
| `Api.DrawnPosition(Vector3 p)` | The same for a true-space point: where it is drawn, in drawn space. |
| `Api.FloorShown(Vector3 p)` | Whether things at that point (its row decides its floor) are drawn now: false on a hidden floor. |

### Build ghosts

`Api.DrawGhost(ThingDef def, IntVec3 cell, Rot4 rot, Color colour, ThingDef stuff = null, ThingStyleDef style = null)`
draws, this frame, the see-through 3D ghost IsoRim draws under the pointer while placing: the thing's model (stuff or
style variant) or its block, tinted from `colour` (the game's ghost colour; blueprint blue or frame sawdust likewise), on
its stacked floor. It returns false when nothing was drawn (3D view or IsoRim's 3D ghosts option off, or no 3D shape):
draw the game's own ghost then.

### Projectile arcs

`Api.AddProjectileArc(Func<Projectile, bool?> rule)` / `Api.RemoveProjectileArc(rule)`: how IsoRim draws a shot's
flight. `true`: on the high arc of indirect fire, as mortar shells; `false`: straight (with the game's own small arc);
`null`: IsoRim decides (the arc for projectiles that fly overhead). Asked once per flight, when the projectile is first
drawn; the first rule with an answer wins. Only the drawing changes. A projectile without `graphicData` never takes the
arc. A stacked-floors addon that declares a shot between floors direct answers `false` for it.

### X-ray while placing

`Api.XrayPlacing(BuildableDef def, bool below, bool above)`: while the player places `def`, the mining x-ray (IsoRim's
mineXray option) shows the floor below and/or above the viewed one, without the crosshair. Both false removes it. It wins
over `Api.XrayDesignator`. ProjectZ registers its stairs (the floor above, where the exit lands); IsoRim itself names no
addon's defs.

### Model query

`Api.HasModel(ThingDef def)`: a model file is bound to the def (by `thingDef`, `graphicPath`, category, name match or
fallback; not `flat`). It says nothing about the player's options: whether the model stands in for a thing now is
`Api.ModelStandsIn`.

## Part 15: face drawers and hidden figure parts (version 21)

### Face drawers

A face drawer draws a person's face on the 3D head instead of IsoRim (its eyes, a creepjoiner's face, or Facial
Animation's face, which IsoRim draws itself when that mod is active). Register it once at startup:

```csharp
// once, at startup (IsoRim.Api.Version >= 21; create the delegate inside a method that runs after the check)
IsoRim.Api.AddFaceDrawer(new IsoRim.FaceDrawer(DrawFace));

static bool DrawFace(IsoRim.FaceFrame f)
{
    Material eyes = MyFace.EyesFor(f.Pawn), mouth = MyFace.MouthFor(f.Pawn);   // south-facing head-sprite textures
    if (eyes == null) return false;          // not ours: IsoRim draws its own face
    f.DrawLayer(f.FaceMesh, eyes, 0);        // each layer a little further out than the one before
    f.DrawLayer(f.MouthMesh, mouth, 1);
    return true;                             // drawn: IsoRim leaves its own face out for this figure, this frame
}
```

- `Api.AddFaceDrawer(FaceDrawer drawer)` / `Api.RemoveFaceDrawer(drawer)`. `FaceDrawer` is `bool (FaceFrame face)`.
  IsoRim asks the drawers once a frame for each person figure whose head shows a face: not when the game leaves out the
  eyes (`FigureFrame.FaceHidden`), no head is drawn (a burka), a skull or a ghoul's own head stands in, or a figure hook
  hid `Head` or `Face`. The first drawer that returns true wins; false leaves the face to the next drawer and then to
  IsoRim. Drawers run after the figure hooks and before the figure is drawn. An exception is logged once and counts as false.
- Version 22: `Api.AddFaceDrawer(FaceDrawer drawer, bool evenWhenHidden)`: with `true` the drawer is also asked where the game leaves
  the face out; `face.HiddenByGame` (`FaceHiddenBy`) says why: `None` (the face shows), `Apparel` (eyes covered),
  `NoHead` (a burka), `Skull`, `OwnHead` (a ghoul). Still not asked when a figure hook hid `Head` or `Face`. Drawers added
  without it are not asked then. The face meshes are those of IsoRim's plain head; a hat or covering still hides them by depth.
- `face.Matrix`: head space to where the head is drawn now, ready for `Graphics.DrawMesh` (pose, facing, lying, sitting,
  crawling, in bed, the person's size, the head type's width, its stacked floor, `Api.DrawFigure` placing). Head space is
  the space IsoRim's head model is modelled in: cells, the figure's feet at the origin, y up, +z the way it faces.
- `face.Layer(int i)` / `face.DrawLayer(Mesh mesh, Material material, int i)`: the matrix for layer i (pushed out a
  little from the head's middle per layer, as IsoRim lays Facial Animation's layers), and a draw with it this frame.
- `face.FaceMesh`, `face.MouthMesh`: the front of the head with the texture projected straight from the front. The texture
  is a south-facing head sprite (the game's head art or Facial Animation's layers: the whole square texture), placed as the
  player set it in IsoRim's face options (and per worn apparel, `FaceDecalAdjust`); the mouth mesh also takes the mouth's
  shift and zoom (around the texture point (0.5, 0.376) from the bottom left). `face.MeshFor(size, height, zoom)` makes
  one at your own placing. `face.FaceSize`, `FaceHeight`, `MouthSize`, `MouthHeight` are the player's values.
- `face.FacialAnimationFace`: IsoRim would draw Facial Animation's face here (that mod is active, the option is on).
  `face.OwnFace`: IsoRim's face is a creepjoiner's special one.
- `face.Pawn`, `Yaw`, `Facing`, `Lying`, `Crawling`, `Dead`, `Size`, `HeadWidth`, `HeadgearShown`, `HairShown`.

The head turns with the figure, so draw the south-facing art for every facing. Hats, helmets and hair cover the face by
depth as they cover the head. A material that writes no depth and sits under render queue 3099 is still darkened at
night by IsoRim's light pass, as Facial Animation's own materials are. Facial Animation's empty layers use the game's
dummy graphic (`Things/Common/dummy`): skip those, they show grey on the head.

### Hiding the figure's own parts

A figure hook can leave out parts of IsoRim's own figure for that frame, to draw its own replacement:

```csharp
IsoRim.Api.AddFigureHook(f =>
{
    if (!MyRace.Has(f.Pawn)) return;
    f.Hide(IsoRim.FigureParts.Head | IsoRim.FigureParts.Hair | IsoRim.FigureParts.Beard);
    f.AttachToHead(myHead, Matrix4x4.TRS(f.HeadCentre, Quaternion.identity, Vector3.one), f.Pawn.story.SkinColor);
});
```

- `frame.Hide(FigureParts parts)`: from version 21 the figure hooks run before the figure is drawn, so the parts are left
  out this frame. Hide them again every frame; several hooks add up. `frame.Hidden` is what the hooks hid so far.
- `FigureParts` (flags): `Head` (the head model and the face on it), `Face` (the eyes, a creepjoiner's face, scars,
  Facial Animation's face; face drawers are not asked), `Hair`, `Beard`, `Headgear` (hair shown under a hidden hat is no
  longer tucked in; hair the game hides under it stays hidden), `EyeWear`, `MouthWear`, `Body` (shirt and trousers
  parts, skin overlays), `Clothes` (coats, armour, vests, collars, leg armour, shoulder pads, their decals and flames),
  `Gear` (packs, belts, waist tools), `Legs` (everything below the hips), `Shield` (a held shield binding).
- Each mix of hidden parts gets its own merged meshes per person, made once and kept: keep the mix steady rather than
  changing it every frame.

With no drawer and no hook registered nothing changes. Hooks registered before version 21 now run before the figure is
drawn instead of after it; what they draw is the same.

## Part 16: humanlike aliens (version 25)

Every humanlike race other than the game's own people (Human, CreepJoiner), called an **alien** here, is drawn as a flat card, as before. A race
becomes a 3D figure only through an **alien pack** (`IsoRim.AlienModelDef`, see [ModelPacks.md](ModelPacks.md#alien-packs)) or a
rule from code. The player's setting **Keep as paper** lists races that stay cards whatever packs and rules say. IsoRim has no
reference to Humanoid Alien Races (HAR): it reads the vanilla calls HAR patches (the body and head mesh sets, which carry HAR's
draw sizes; `PawnRenderer.BaseHeadOffsetAt`; the render tree with HAR's addon nodes; the apparel texture path), so any race
framework that goes through them works the same. Without an alien pack nothing changes, and people look the same with and without
HAR.

```csharp
// IsoRim.Api.Version >= 25
IsoRim.FigureMode mode = IsoRim.Api.FigureModeOf(pawn);   // Card, Human (IsoRim's human figure) or Own (the pack's models)
```

- `Api.FigureModeOf(Pawn)` → `FigureMode` `Card` (not a 3D person: animals, mechs, races without a pack or kept as paper),
  `Human` (people and races drawn with IsoRim's human figure), `Own` (a race drawn with its pack's own models).
- `Api.AddAlienFigureRule(Func<ThingDef, FigureMode?> rule)` / `Api.RemoveAlienFigureRule(rule)`: asked once per humanlike race
  (not people); return a mode to force it, null to leave the race to its pack. "Keep as paper" still wins. Create the delegate
  explicitly inside a method that runs after the version check (part M, notes).
- `FigureFrame.Mode`: the same for the figure a hook is drawing.
- `FigureFrame.Anchor(string name, out Matrix4x4 m)`: a named point of the figure in drawn space, the same names for people and
  races: `head` (the head's pivot on the neck), `face` (the front of the face), `neck`, `back`, `waist`, `handLeft`, `handRight`,
  `tailRoot` (case does not matter; `hand_l`, `hand_r`, `tail_root` too). `head` and `face` move with the head (its pose, a
  race's head offset and fit), the others with the body. The matrix is the head or body frame moved to the point. For people
  they are IsoRim's own figure points with the body type's scale; an alien pack sets its own. Use them instead of hard-coded
  human offsets, so an attachment lands on any race.
- `FigureParts.Addons`: hide a race's addon cards (its own render nodes drawn as cards, below) from a figure hook, for a mod that
  draws them itself.
- `ApparelModelDef` (part 3) gets `aliens` (the binding is only for these races; for them it wins over a binding without `aliens`)
  and `alienBodyModels` (Body: `<li><bodyType>MyBody</bodyType><model>Models/x.obj</model></li>`, before `bodyModels`).

How a race figure is placed (all of it goes into the figure's root and head matrices, so everything placed on the figure
follows: IsoRim's clothes, hats, hair and eyes, held weapons, work tools, carried things, figure hooks (part 1), face drawers
(part 15), apparel models (part 3), attachments (part 7)):

- **Human**: IsoRim's human figure, scaled by the race's scale (default: the game's body mesh against a human's, i.e. HAR's
  `customDrawSize`), its head scaled about the neck (default: head mesh / body mesh, HAR's `customHeadDrawSize`) and moved (default:
  the game's head offset against the body type's own, HAR's `headOffset`, south for up, east for forward). The race's body type
  maps to one of IsoRim's builds (pack `build`, else vanilla names, else the body type's own art). IsoRim's face on the head
  (pack `face` `None`: no face).
- **Own**: the pack's body (upper and lower, like `person_<build>_upper/_lower`) and head models in the race's own figure space.
  IsoRim's head parts (hats, hair, head hooks) go through the `head` anchor and `headFit`; clothes, packs and belts made
  for the body's `fitAs` build through `bodyFit`; held weapons and carried things move with the hands' height. With `face`
  `IsoRim` (the default) IsoRim's face (its eyes, Facial Animation's face) goes on the race head at the `face` anchor, scaled by
  `faceFit`, and face drawers (part 15) are asked as for people: `FaceFrame.Matrix` maps IsoRim's head space onto that place.
  With `face` `None` the head model has its own face: drawers see `FaceHiddenBy.OwnHead`. Dried to bones, an Own race keeps
  its card (IsoRim has no skeleton of its shape).
- **Hair**: pack `hair` `IsoRim` (IsoRim's hair shapes by the hairstyle, the default), `Card` (the game's own hair and beard
  sprites as cards, like the addons: for races whose hair art IsoRim's shapes don't match) or `None`.
- **Addons**: the race's render nodes of another mod's class (HAR body addons: ears, tails, horns, hair drawn as an addon) are
  drawn as cards of the game's own sprites, as the game would draw them for the facing the camera sees (lying on the back:
  the front picture, crawling: the back one): HAR's offsets per facing, body type and head type, its colour channels,
  conditions and skip flags (a hat hiding the ears) all come from the render tree. A pack can give one a model (by its
  texture path), hide it, or give it `hatLift`: while headgear is drawn, a head addon (card or model) is lifted that share of
  the way from its lowest drawn point to the hat's top, in the head's middle plane, so ears the game shows over hats stick
  out of the 3D hat. The game's own gene parts on people are not drawn by IsoRim (they are left to other mods, such as
  "IsoRim - Biotech Pawn Parts"); a mod that draws a race's addons itself hides IsoRim's cards with `FigureParts.Addons`.
- **Dev mode**: Debug actions > IsoRim > **Alien tuning...** (a race pawn selected, or click one): live sliders for scale, head
  scale and offset, every anchor and `headFit`, `bodyFit`, `faceFit`, the figure and face buttons, and **Copy XML** (the
  fields and anchors as `AlienModelDef` XML on the clipboard). Changes show at once on every pawn of the race until Reset or
  a new game; untouched fields keep the pack's (or measured) values and are not written.

## Part 17: angled wall pieces and the gravship scene (version 26)

A mod that draws its own wall corners in a flat section layer (gravship armour corners) can have IsoRim stand 3D pieces
there, drawn like its wall blocks. A mod with its own section layers on a gravship can have them fly with the ship in IsoRim's take-off and landing scene.

```csharp
// IsoRim.Api.Version >= 26
IsoRim.Api.AddWallCorners(myWallDef, MyCornerRule);                     // wedges in empty cells between two blocks
IsoRim.Api.HideFlatLayer(typeof(MyCornerLayer), m => m.mainTexture.name.StartsWith("MyCorner"));
IsoRim.Api.FlyLayer(typeof(MyCornerLayer), flatPieces: true);
IsoRim.Api.NotGeometryLayer(typeof(MyMaskLayer));
```

- `Api.AddWallCorners(ThingDef wall, CornerRule rule, ulong changeFlags = 0)`: in an empty cell between two blocks of that wall
  the rule says yes for, IsoRim stands the same wedge as the vanilla gravship hull's corner pieces: the half of the cell
  against the two legs, as tall as the lower leg, slanted face looking out, with the wall's look and the rule's colour.
  `CornerRule(Map, IntVec3 cell, out IntVec3 legA, out IntVec3 legB, out Color colour)`: the legs are two directions at a right
  angle (two of `IntVec3.North`, `East`, `South`, `West`).
- The rule is asked while a block of the map is redrawn, on the main thread, only while walls are IsoRim blocks (3D view, textured
  3D walls). `changeFlags` (the game's `MapMeshFlag` bits) are added to what redraws IsoRim's walls layer, for rules that
  depend on more than buildings; `Api.RefreshWallShapes(map, cell)` redraws the walls around a cell when nothing else reports
  the change. A rule that throws is logged once and left out.
- `Api.HideFlatLayer(Type sectionLayer, Func<Material, bool> which = null)`: while IsoRim's 3D walls stand in for them, the
  layer's sub-meshes whose material the filter accepts (all if null) are emptied after each rebuild. With the 3D view or the
  textured 3D walls off the layer draws as before. Give a filter when the layer also holds things that must stay.
- `Api.FlyLayer(Type sectionLayer, bool flatPieces = false)`: the layer's geometry over the ship's cells goes with IsoRim's ship
  snapshot; `flatPieces` adds one ring of cells round the ship (pieces just past the hull, like the game's hull corners).
- `Api.NotGeometryLayer(Type sectionLayer)`: a mask or overlay layer: not flown and not reported as a layer left behind.
- Call them once, at startup, from code that runs after the version check (part M, notes).

## Part 18: supported doors with their own models, also multi-cell (version 29)

A supported door (`Building_SupportedDoor` and its subclasses: the vac barrier, the ancient blast door, a mod's airlock)
is drawn from an ordinary `thingDef` entry with its parts: `model` is the frame, drawn fixed; `doorTopModel` the lintel
across the doorway (left out where the cut-away lowers the walls); `doorLeafModel` one panel, the other the same turned
half round; `hackTerminalModel` a hacking terminal, drawn while the door's `CompHackable` is not hacked. Leaves are
optional (a vac barrier has none: the game's own field sprite stays). One-cell supported doors work since IsoRim's coop
batch 3; since version 29 a multi-cell one does too, except the game's `Building_MultiTileDoor`, which keeps its own
wide-door path.

- Build the models in door space: North rotation, so the doorway runs along x with walls east and west; origin at the
  door's middle on the ground (a multi-cell door: the middle of its footprint); walls 1.2 high; `builtFor` = the door's
  size. IsoRim scales the height to the current wall height and cuts it down with the walls.
- A leaf spans x 0 to half the door's width from the middle. The leaves slide apart as the game's movers do, each
  `0.45 x width x OpenPct` (a one-cell door: 0.45).
- The placement preview shows the parts as ghosts. With the 3D view or IsoRim's 3D doors off, the game's sprites stand.

```xml
<IsoRim.ModelDef>
  <defName>MyPack_WideAirlock</defName>
  <thingDef>MyMod_Airlock3x1</thingDef>
  <model>Models/airlock_3x1_frame.obj</model>
  <doorTopModel>Models/airlock_3x1_top.obj</doorTopModel>
  <doorLeafModel>Models/airlock_3x1_leaf.obj</doorLeafModel>
  <builtFor>(3, 1)</builtFor>
</IsoRim.ModelDef>
```

An older IsoRim leaves a multi-cell supported door with these fields as the game's flat sprites (no error), so a pack
needs no patch for it; the model binder's export gates such a binding on version 29.

## Part 19: glass parts per thing (version 30)

A `glassModel` (part M) is drawn see-through over the body, in IsoRim's one pale glass colour. Since version 30 a def or
an addon can give it its own look, and a supported door (part 18) draws its `glassModel` too (built in door space, like
its frame): a window, or a vac barrier's field.

- `glassTint` (r, g, b): the glass colour; unset = IsoRim's pale glass. `glassOpacity` (0 to 1): -1 = IsoRim's 0.4.
  `glassGlow`: drawn unlit, so it keeps its brightness at night (a force field); otherwise it darkens with the scene.
- A vac barrier's (`Building_VacBarrier`) glass part is its field: it shows only while the game shows the field sprite
  (the barrier powered and on), in place of that sprite. Its colour: a colour source's, else the def's `glassTint`,
  else the field sprite's colour.
- `AddGlassColor(Func<Thing, Color?> source)`, `RemoveGlassColor(source)`: a colour source, asked each frame for each
  thing whose glass part is drawn. Return r, g, b = tint and a = opacity (0 = not drawn), or null to leave the thing to
  its def. The first source that answers wins. Keep it cheap; an exception is logged once and the source is left out.
- Things with none of these keep IsoRim's one glass look exactly. With the 3D view off the game's sprites stand.

```csharp
if (IsoRim.Api.Version >= 30)
{
    IsoRim.Api.AddGlassColor(t => t is MyRecolourableBarrier b ? new Color(b.colour.r, b.colour.g, b.colour.b, 0.35f) : (Color?)null);
}
```

An older IsoRim ignores these fields and a door's `glassModel` (no error); the model binder's export gates them on version 30.

## Part 20: parts through roofs (version 31)

What stands under a roof is clipped at the roof's underside, so nothing pokes out of it (RoofClip). A thing meant to show
above its roof (a ship gun on a frame, its pedestal and gun over the roof; a mast) uses:

- `topThroughRoof` (bool): a turret's gun (`topModel`) is not clipped by a built, rock or ship roof over it.
- `roofModel` (OBJ path): a fixed part built in the same space as `model` and baked with it (placed, turned and fitted
  as the model; blueprints and frames too), not clipped by a roof over it. It counts for the thing's drawn height.

The fog still hides both like anything else, and so does a floor's slab over them (with stacked floors). The base
(`model`) stays clipped under the roof as before. Roofs clip by the thing's own cell: keep a `roofModel` part over the
thing's middle.

```xml
<IsoRim.ModelDef>
  <defName>MyPack_ShipGun</defName>
  <thingDef>MyMod_ShipGun</thingDef>
  <model>Models/ship_gun_frame.obj</model>
  <builtFor>(3, 3)</builtFor>
  <topModel>Models/ship_gun.obj</topModel>
  <topHeight>1.74</topHeight>
  <topThroughRoof>true</topThroughRoof>
  <roofModel>Models/ship_gun_pedestal.obj</roofModel>
</IsoRim.ModelDef>
```

A pitched built roof over a wide room can rise higher than the gun; the roof then hides it where it is higher. An older
IsoRim does not know the field (an XML error in Defs): add it through `PatchOperation_IfIsoRimApi` (minVersion 31).

## Part 21: hovering animals and mechs (version 32)

An animal (`animal`) or mech (`mech`) binding can make its pawn float over the ground, for drones, floating worms and
the like whose game sprite has no legs or wheels under it:

- `hoverHeight` (cells, default 0): while the pawn is up and about (standing, walking, working, aiming) its whole figure
  (body, legs, parts, gun, what it carries) is raised this far, times its size.
- `hoverBob` (cells, default 0): it drifts up and down by this much around `hoverHeight`, a slow swell (about 2.7 s at
  normal speed) with its own phase per pawn. Keep it below `hoverHeight` so it never dips into the ground.

Downed, dead, asleep or resting (any posture but standing) it settles onto the ground, lying as it would without the
fields; getting up it rises again. Both are eased over about 45 ticks and stop while the game is paused. Its ground
shadow stays on the ground under it, a little smaller the higher it floats. Flying (Pawn_FlightTracker), carried or
attached pawns are placed as before (no hover on top). Build the model standing on the ground as usual; the hover is
added when drawn. Icons over the pawn and picking follow it.

```xml
<IsoRim.ModelDef>
  <defName>MyPack_Drone</defName>
  <thingDef>MyMod_Drone</thingDef>
  <model>Models/drone_body.obj</model>
  <mech>true</mech>
  <hoverHeight>0.45</hoverHeight>
  <hoverBob>0.06</hoverBob>
</IsoRim.ModelDef>
```

An older IsoRim does not know the fields (an XML error in Defs): add them through `PatchOperation_IfIsoRimApi`
(minVersion 32). Tests: `Api.Dev.HoverLift(pawn)` (cells, as last drawn) and `Api.Dev.HoverState(pawn)` (0 on the
ground .. 1 afloat, -1 none).

## Part 22: beam parts (version 33)

Beams are light, not things. In the 3D view IsoRim draws them unlit and additive (they only brighten what is behind
them), with no outline, no shadow and no depth write, as bright at night as by day (ShaderDef `IsoRimAdd`).

- **Model parts**: faces whose material name starts with `beam_` are taken out of the model when it loads and drawn as
  light in their MTL colour (`Kd`): a laser line, an energy core, a field between two posts. They get no hull or
  outline, cast no shadow, are not glass and do not count as a turret's barrel end. They go out with the model's
  glowing parts (unpowered). Keep them thin; the colour is added to the scene, so a dark `Kd` shows little.
- `beamFlicker` (0 to 1, default 0): the beam parts flicker, dimming by up to this share.
- `beamPulse` (Hz, default 0): the beam parts pulse slowly between 60% and full brightness.
- **Line motes** (opt-in): a line mote (MoteDualAttached) that IsoRim draws as a 3D strip and that a ModelDef lists
  in `beamMotes` is drawn as a glowing strip (white-ish core, the beam's colour, soft edges). Every other line mote,
  the game's own beams (`MoteBeam`: graser, beam repeater) included, keeps the game's drawing. Colour: the mote's colour times its
  texture's colour; width: the part of the drawn width its texture lights; it fades in and out with the mote.

```xml
<IsoRim.ModelDef>
  <defName>MyPack_LaserFence</defName>
  <thingDef>MyMod_LaserFence</thingDef>
  <model>Models/laser_fence.obj</model>   <!-- usemtl beam_red for the laser faces -->
  <beamFlicker>0.3</beamFlicker>
  <beamPulse>1.5</beamPulse>
  <beamMotes><li>MyMod_Mote_LaserBeam</li></beamMotes>
</IsoRim.ModelDef>
```

An older IsoRim draws `beam_` faces as ordinary faces and does not know the fields (an XML error in Defs): add them
through `PatchOperation_IfIsoRimApi` (minVersion 33). 3D view off: nothing changes. Tests: `Api.Dev.BeamMoteDraws`,
`BeamPartDraws`, `BeamPartBaked`, `BeamPartShader`, `BeamDrawn(mote, out material, out from, out to)`.

## Part 23: wall shapes (version 34)

A mod that gives walls a shape of their own in some cells (diagonal joins, trimmed wall cells) tells IsoRim the shape
cell by cell, and IsoRim stands it up as its own 3D walls: the owner wall's height (the wall height setting; a wall
upstairs reaches down to the wall below), its stuff texture and colour (paint included), cap, outlines and corner
lines, sun lighting, the cut-away (each piece goes down with its owner's cell), the gravship take-off and landing
scene and culling. Built roofs (pitched, flat and ship decks) over such cells follow the shapes: their edge runs along
the shape's outer side, and near shapes a pitched roof rises from that edge (a hipped roof over an octagon).
An owner that is a wall drawn from a model (a model pack's linked wall with wallFit) stands in its shapes too: its own
straight run is turned along the slant, repeated one cell apart (not stretched) and cut to the shapes, with the cuts
closed in the wall's side colour.
A block wall with a shaped top (part 11's wallTop: merlons, a ridge, stubs) keeps its top in such cells: the straight
run's top is laid along the slant the same way and cut to the shapes.

```csharp
// IsoRim.Api.Version >= 34, once at startup
IsoRim.Api.AddWallShapes((Map map, IntVec3 cell, IsoRim.WallShape into) =>
{
    if (!MyShapes.Has(map, cell)) return false;
    into.ReplacesBlock = true;                                  // a trimmed wall cell: its square block is left out
    into.Add(new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f) }, cell.GetEdifice(map));
    return true;
});
IsoRim.Api.HideFlatLayer(typeof(MySectionLayer));               // the mod's own flat drawing, while IsoRim draws it in 3D
```

- `WallShapeRule(Map, IntVec3 cell, WallShape into)`: fill `into` with convex polygons, cell-local (0..1, x east, z
  north, either winding), each with the wall `Building` whose look it takes (it must be a wall IsoRim draws as a block:
  others are left out). Return false for cells the rule doesn't touch. `into.ReplacesBlock`: leave out the cell's own
  block (only the polygons stand).
- Which sides show is worked out by IsoRim: a side shows where nothing else (another shape, a block) stands just
  outside it at least as tall. Let pieces meet along whole sides or along the cell's sides.
- Asked on the main thread while a block of the map is redrawn (walls and roofs), several times per cell: keep it cheap
  and deterministic. Only while walls are IsoRim blocks (3D view, textured 3D walls); 3D view off: nothing changes.
- `changeFlags` (the game's `MapMeshFlag` bits) are added to what redraws IsoRim's walls layer; buildings and things
  already do. `Api.RefreshWallShapes(map, cell)` (version 26) redraws the walls round a cell when nothing else reports
  the change. A rule that throws is logged once and left out.
- Roofs: in a cell with shapes the roof covers the shapes whose owner carries that roof and the rest of the cell as
  that cell's roof; in a cell whose block the shapes replace, each free part takes the roof of the open cell beside it.
- Stacked floors (Floors): a shape upstairs over the same shape below reaches down to it, as walls do. Damage marks on
  a replaced cell stay on its shape's cap.

First user: Vanilla Diagonal Walls (its diagonal bands and trimmed wall cells, through a small assembly it loads only
with IsoRim).

## Part M: model packs (IsoRim.ModelDef)

A model pack is an XML-only mod (or a mod with a small DLL) that adds `IsoRim.ModelDef`s binding OBJ models to other
mods' things.

### Binding and placing

| Field | Meaning |
|---|---|
| `thingDef` | The ThingDef drawn with this model. A later def for the same thing wins (DefDatabase order). |
| `graphicPath` (16) | Every thing whose `graphicData.texPath` is this, unless it has its own entry. |
| `thingCategory`, `match`, `fallback` | Every thing of a category; things whose defName contains one of the words; `SmallPlants`. |
| `model` / `models` | The OBJ path, relative to the root folder of the mod that holds the ModelDef; several: each thing picks one. |
| `builtFor` | The footprint (x, z cells) the model was built for; it is stretched to the thing's size (and turned when the long sides differ). |
| `scale`, `offset`, `rotation`, `rotationByFacing` (6), `ignoreRotation` | Placing in model space: `world = thing centre + R(yaw)·(local ∘ fit + offset)`, yaw = the thing's rotation + `rotation` + `rotationByFacing[facing]`. The model faces north (+z). |
| `height`, `flat` | Without `model`: the height of IsoRim's automatic shape (a block for walls), or keep the flat sprite. |
| `surfaceHeight`, `seatHeight`, `beddingTop` | Where things rest, sit and lie on it (-1: the model's top). |
| `slots` (28) | One entry per sleeping slot, in the game's slot order (`RestUtility.CurrentBed`'s slot; a bunk bed mod numbers its levels so): `<li><height>1.25</height></li>` is that slot's surface height, in model units like `surfaceHeight`. The 3D figure lies there, a flat sprite as far above it as `beddingTop` is above `surfaceHeight`, the icons over the head go up with it. A missing list, a slot past its end or a height of -1 keeps `surfaceHeight`. Getting in and out snaps, as in the game. Later per-slot settings (offset, facing, pose) go into the same entries. |
| `wallFit` | > 0: the model was built for walls this tall; its height follows the wall height drawn now. |
| `linked` | The first model stands in the cell, the second is added toward each joined neighbour (with `wallFit` on a wall: a wall from a model). |
| `wallMounted`, `mountHeight` | Fixed to the wall it faces, that far up. |
| `byAppearance`, `byState` (6), `byStyle`, `style` (6) | Other models by the stuff's look, by an addon's state (`Api.AddModelState`), by Ideology style. |
| `bobByState` (27) | With `bobModel` (the bobbing part, e.g. a grav engine's orb): another bob part for things in an addon's state (`Api.AddModelState`), the same `<li><state>..</state><model>..</model></li>` list as `byState`; other states use `bobModel`. Asked each frame the part is drawn. |
| `immatureModels`, `immatureHide` (24) | A plant's young stage, drawn while it is not yet harvestable (as the game's `immatureGraphic`: growth up to `harvestMinGrowth`): its own models (one per variant of `models`, same order), or the mature models without the `immatureHide` materials (the produce). `growthScale` still shrinks it. |
| `spriteTint`, `paintedHull`, `outline`, `outlineMinSize`, `groundShadow`, `aged` (15) | Colour and look. |
| `wallTop` (11), `cutaway` (20), `backHeight` (13), wing fields (14), `doorLeafModel`, `doorLeafGather` (18), `crownTop`, `doorLeafRise` (6) | See their parts. |

The numbers in brackets are the Api version a field needs; fields without one are there from version 2.

### OBJ and materials

- One unit = one cell, +y up, origin at the footprint's centre on the ground; IsoRim flips z on load.
- Materials come from the `.mtl` (`Kd` colour, `map_Kd` texture). Faces whose material name starts with `stuff` take the
  thing's stuff colour (or its paint, its sprite with `spriteTint`); `sheet*` faces (24) are multiplied by the thing's second
  colour (`Thing.DrawColorTwo`: a bed's sheet colour for colonists, medical, prisoners and slaves, as the game tints its
  sprite; white for most other things, so give them a light grey `Kd`); `glow*`, `flame*` and `ember*` faces are unlit;
  `leaf*` faces are a plant's leaves.
- Keep shapes big and simple; IsoRim draws a dark outline round each piece (`outline`).

### Gating fields by version

A pack that must load on an older IsoRim keeps its ModelDefs in `Defs/` **without** `thingDef` (IsoRim skips an entry that
names no thing) and adds `thingDef` and the newer fields by patch, only when IsoRim is new enough. The patch class lives in
the pack's own DLL (a missing patch class is a load error):

```csharp
public class PatchOperation_IfIsoRimApi : PatchOperation
{
    public int minVersion;
    public PatchOperation apply;
    protected override bool ApplyWorker(XmlDocument xml)
    {
        int have;
        try { have = IsoRim.Api.Version; } catch (Exception) { have = 0; }
        return have < minVersion || apply == null || apply.Apply(xml);
    }
}
```

```xml
<Operation Class="MyPack.PatchOperation_IfIsoRimApi">
  <minVersion>20</minVersion>
  <apply Class="PatchOperationAdd">
    <xpath>/Defs/IsoRim.ModelDef[defName="R3D_MyHalfWall"]</xpath>
    <value><thingDef>MyHalfWall</thingDef><cutaway>Never</cutaway></value>
  </apply>
</Operation>
```

### Notes for pack authors

- A def a patch adds has no mod of its own (`modContentPack` is null), so IsoRim cannot find its model files: keep whole
  ModelDefs in `Defs/` and let patches only add fields to them.
- In C#, a method group of a delegate type that only a newer IsoRim has (`Api.AddProjectileArc(MyRule)`) is cached by the
  compiler in a static field, and the game's startup scan of static fields then throws on an older IsoRim. Create the
  delegate explicitly (`new Func<Projectile, bool?>(MyRule)`) inside a method that runs only after the version check, and
  keep the calls in a `[MethodImpl(MethodImplOptions.NoInlining)]` method.
- Check `Api.Version` once and remember it; read only Api members, never IsoRim's other public types.

## Notes

- Figure hooks also run for figures another mod draws through IsoRim's internals; attachments then follow that mod's
  placing as well.
- The test modes for this Api are `Source/ProbeApi8.cs` and `Source/ProbeApi9.cs` / `ProbeApi9Check.cs` (harness only: dev mode, `-isorim3d` and a probe file).
  Version 10 was tested with harness fixtures outside this repository (the VFEM2 pack's tabards, ProjectZ's lift models),
  version 11 likewise (the VFEM2 pack's castle walls and wall turrets, harness layouts `layout_wtcw*`, `layout_wtall`).
  Version 12: `Source/ProbeApi12.cs` (keys `upright`, `upcheck`, `uptest`; harness layout `layout_uprt`).
  Version 13 was tested with the IsoRim Waylith - Giddy-Up Pack and a harness helper outside this repository (riders on
  model and card mounts, picking, labels, shots, first person, stacked-floor stairs, ladders and lifts).
  Version 15: probe key `aged=x` (`Source/ProbeScene.cs`, harness only: every aged def at strength x; harness layouts
  `layout_ancient`, `layout_ancientfin`).
  Version 16: IsoRim's raw meat (harness layout `layout_meat`; the model sweep's "how" column shows `graphicPath:`).
  Version 17: `Source/ProbeApi17.cs` (keys `fwfp`, `fwreq`, `fwrel`, `fwcheck`, `fwapi`, `fwmapseq`, `fwgame`; harness layouts `layout_fw`, `layout_fwpz`).
  Version 18: IsoRim's animal flap (harness layouts `layout_flap*`, real pawn and animal jobs through the doorway).
  Version 19 was tested with ProjectZ's test mode `ladderpose` (parts PW and PWR: the drawn yaw of climbers in real time,
  through `Api.Dev.DrawnYaw`).
  Version 20 was tested with a throwaway harness addon outside this repository that uses each member as a third-party
  mod would (half walls with `cutaway` Auto and Never, a low block with Always, overlays skipped by `SkipWhenModelled` and
  `SkipFlat`, a sprite moved by `FlatTransform`, `DrawnMatrix` markers on a ProjectZ floor, `DrawGhost`, arc rules on four
  projectiles, the view state through turns, perspective and first person, `XrayPlacing` with ProjectZ's `xray` test mode),
  with the VFE Medieval 2 castle walls rendered before and after.
  Version 21 was tested with a throwaway harness addon outside this repository: a face drawer drawing a test face on
  every face and figure hooks hiding parts (with cube replacements), on people in four facings, children, hats and
  helmets, masks, a burka, in beds and downed, with hairUnderHats off and on; with nothing registered the renders match
  version 20.
  Version 25 was tested with XML-only harness races outside this repository (HAR test races with a custom body type, draw
  size 0.8, head draw size 0.9, head offsets, a tail and ear addons; an alien pack with a `Human` race, an `Own` race with box
  models, a tail model and a race-only hat binding; a race without a pack) and the probe keys `alienpawn`, `alienhooks`,
  `keeppaper`, `goto`, `aliencheck` (`Source/ProbeAliens.cs`): four facings, apparel, hats, weapons, packs, eating at a table,
  resting in bed, crawling downed, carrying, aiming, walking, figure hooks and face drawers, "Keep as paper", the pack on an
  older IsoRim (no change, no error) and the races without the pack (cards). Round 2 added `face` on and off (set live in the
  alien tuning window, probe keys `alientune`, `alientuneset`, `alientunecopy`), cards while lying and crawling, and a trial pack for
  Kiiro Race (HAR, Workshop 2988200143) with `hair` Card, ear `hatLift` and Kiiro's own and vanilla hats.
