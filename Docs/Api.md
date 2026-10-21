# IsoRim Waylith Api (for mod authors)

IsoRim's public surface is the static class `IsoRim.Api` and the small types next to it (`FigureFrame`, `FigureHook`,
`ModelHandle`, `ProjectileCardHook`, `IFloorLayout`, `ApparelDecal`, `DecalSide`, `ApparelDecalProvider`, `UprightScope`), the def `IsoRim.ApparelModelDef` (Api 9) and the `wallTop` field of `IsoRim.ModelDef` (Api 11, part 5). Everything else in the assembly is internal detail and can change
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
if (IsoRim.Api.Version >= 17) { /* complete-wall requests for camera modes: RequestFullWalls */ }
```

Members are never removed or changed in meaning within a version. Version 8 adds parts 1 and 2 below, version 9 part 3,
version 10 part 4 and the peek fade of `Api.DrawModel` (part 2), version 11 part 5, version 12 part 6, version 17 part 10.

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

## Part 10: complete walls for camera modes (version 17)

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

### Verification

Run `dotnet run --project Tests/WallCutaway.Tests.csproj`. These tests compile the production request policy and native
first-person lifecycle against minimal game/engine stand-ins. They check entry/exit, overlapping requests, invalid input,
map/game changes, pawn loss, stale handles and redraw invalidation counts. They do not validate in-game drawing.

In RimWorld 1.7, check these cases both with textured walls (shader cutaway) and without them (rebuilt geometry), alone
and with ProjectZ:

1. Enable pointer, colonist and room cutaway. Lower walls, then enter first person: whole walls must return, including
   walls already lowered, damage marks and doors. Leave first person: the player's cutaway settings apply again.
2. Repeat with those settings disabled and with a non-default remaining-wall height. Confirm the saved settings do not
   change, including when settings are edited/saved while in first person.
3. Hold two independent complete-wall requests and release them in both orders. Also overlap a request with native first
   person. Only leaving the last active mode restores normal cutaway.
4. Switch maps, lose the followed pawn, return to the menu and start/load another game. Old requests must stay expired;
   disposing an old handle must not cancel a new request. Repeat while the game is paused.

## Notes

- Figure hooks also run for figures another mod draws through IsoRim's internals; attachments then follow that mod's
  placing as well.
- The test modes for this Api are `Source/ProbeApi8.cs` and `Source/ProbeApi9.cs` / `ProbeApi9Check.cs` (harness only: dev mode, `-isorim3d` and a probe file).
  Version 10 was tested with harness fixtures outside this repository (the VFEM2 pack's tabards, ProjectZ's lift models),
  version 11 likewise (the VFEM2 pack's castle walls and wall turrets, harness layouts `layout_wtcw*`, `layout_wtall`).
  Version 12: `Source/ProbeApi12.cs` (keys `upright`, `upcheck`, `uptest`; harness layout `layout_uprt`).
