# IsoRim Waylith Api (for mod authors)

IsoRim's public surface is the static class `IsoRim.Api` and the small types next to it (`FigureFrame`, `FigureHook`,
`ModelHandle`, `ProjectileCardHook`, `IFloorLayout`, `ApparelDecal`, `DecalSide`, `ApparelDecalProvider`, `UprightScope`, `Attachment`, `AttachAnchor`, `AttachPose`, `AttachFacing`, `HostTransform`), the def `IsoRim.ApparelModelDef` (Api 9) and the `wallTop` (Api 11, part 5), `backHeight` (Api 13, part 7), animal wing (Api 14, part 8) and `aged` (Api 15, part 9) fields of `IsoRim.ModelDef`. Everything else in the assembly is internal detail and can change
in any update. Reference `IsoRim.dll` (Publicize is not needed) and keep the reference optional if IsoRim is optional for
your mod.

## Versioning

`Api.Version` goes up whenever members are added. Check it before you use newer members:

```csharp
if (IsoRim.Api.Version >= 8) { /* figure hooks and model drawing are there */ }
if (IsoRim.Api.Version >= 9) { /* IsoRim.ApparelModelDef is there */ }
if (IsoRim.Api.Version >= 10) { /* apparel decals; DrawModel follows the peek fade */ }
if (IsoRim.Api.Version >= 11) { /* ModelDef.wallTop (wall tops) and Form3D.Ground */ }
if (IsoRim.Api.Version >= 12) { /* the upright drawing scope: BeginUpright, EndUpright, Upright, DrawMesh */ }
if (IsoRim.Api.Version >= 13) { /* attachments: AddAttachments, ReadHost, AnchorPoint, AttachedPlace, AttachmentOf */ }
if (IsoRim.Api.Version >= 14) { /* ModelDef wing fields (animal wings that fold and beat), AirborneFactor */ }
if (IsoRim.Api.Version >= 15) { /* ModelDef.aged (the aged look) */ }
```

Members are never removed or changed in meaning within a version. Version 8 adds parts 1 and 2 below, version 9 part 3,
version 10 part 4 and the peek fade of `Api.DrawModel` (part 2), version 11 part 5, version 12 part 6, version 13 part 7, version 14 part 8, version 15 part 9.

## Spaces

- **True space**: x and z in map cells, y the height over the ground in cells.
- **Drawn space**: where IsoRim puts things: drawn y = true height / `Api.HeightSquash` (a very small number, so drawn
  y values are large). The figure frames below map into drawn space.
- Model files are OBJ, one unit = one cell, +y up. IsoRim flips the file's z, as for its own models.

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
