# Model packs for IsoRim Waylith (for model authors)

A model pack gives buildings and items of the game or of another mod their own 3D model in IsoRim's 3D view. It is XML
and `.obj` files only: no code of your own. This page covers how to build the model, how to bind it, how to gate the
binding on IsoRim's version, and the two dev-mode tools that help: the **model binder** and the **coverage report**.

Things without a model are not broken: IsoRim raises their sprite into a solid shape ("raised sprite"), or keeps it flat.
A model is worth it for things players see often and whose raised sprite reads badly.

## Pack layout

```
MyModelPack/
  About/About.xml                  your mod; depends on and loads after Peren.IsoRim17 (and the mod whose things you model)
  1.7/Assemblies/IsoRimPackGate.dll   the patch class below (copy it from IsoRim's Tools/PackGate/ or from an export)
  Defs/MyPack_Models.xml           the ModelDefs, WITHOUT <thingDef>
  Patches/MyPack_Models.xml        adds each <thingDef> when IsoRim's Api is new enough
  Models/*.obj, *.mtl              the models and their materials
```

The model binder's export writes exactly this layout. IsoRim's VFE Medieval 2 pack is a full-size example.

## Making the model

### Units, axes, origin

| | Rule |
|---|---|
| Unit | 1 unit = 1 map cell. |
| Axes in Blender | +Z up, +Y north, +X east. Export OBJ with Blender's defaults (Forward -Z, Up Y, scale 1). In the file Y is up and -Z is north; IsoRim flips z on load. Don't change the export axes. |
| Origin | The centre of the whole footprint, on the ground (y = 0). A 2x1 thing has its origin on the line between its two cells. |
| Facing | Build the thing as it stands facing north (`Rot4.North`), matching the game's `*_north.png` seen from above. IsoRim turns it for the other facings (clockwise seen from above: north, east, south, west). Workbenches: the worker stands south (`interactionCellOffset (0,0,-1)`). |
| Footprint | X from -size.x/2 to +size.x/2, north-south from -size.z/2 to +size.z/2. Keep 0.04-0.07 clear at each edge so outlines of neighbours don't merge. |
| Ground | Nothing below y = 0 (it is flattened onto the ground). No bottom faces needed for things standing on the ground. |

Height references (walls default to 1.2 cells): bed 0.72, shelf 0.60, cryptosleep casket 0.60, tube television 1.40, tool
cabinet 1.70, comms console 2.42, wind turbine 3.19. Models don't follow the wall height unless they set `wallFit`.

### Mesh

- **Closed shells, normals outward.** The dark outline is a slightly grown copy of the shell drawn inside out; a face turned
  the wrong way has no outline there. IsoRim turns a closed model that is entirely inside out round by itself; open parts
  can't be fixed. In Blender: Recalculate Outside, Face Orientation all blue.
- **Thickness.** Give thin parts at least 0.02 thickness. The outline is 0.03 cells: details under about 0.04 vanish in it.
  A single sheet (zero thickness) gets no outline.
- **Coplanar faces flicker.** Lift a face that lies on another face of a different material by 0.003 along its normal.
- **Triangulate on export** (or use only triangles and convex quads): polygons are split as fans.
- `vn`, `o`, `g`, `s` are ignored: shading is flat per face. A file is one model; parts that move must be separate files.
- **Face budget** (triangles): 1x1 furniture 60-200, 2x1 to 3x2 things 150-350, large things up to about 800. More faces
  cost map-mesh space and read worse, not better.

### Style: big simple shapes

At play zoom a model is 30-100 pixels tall. Keep only what reads there: a clear silhouette and one or two colour zones per
part. Concept art is a guide to the silhouette, not a list of details. Cylinders: 6-10 sides. Normals are quantized to 26
directions, so give slopes a clear angle. Colours as vanilla: low saturation, two or three shades per part (1.0 / 0.8 /
0.62 of a base colour).

### Materials

The `.obj` names its `.mtl` files with `mtllib` (one or several lines, paths relative to the `.obj`). IsoRim reads `Kd`
(colour, 0-1 as it looks) and `map_Kd` (a texture by its game content path: the part after `Textures/`, no extension).
Everything else in the `.mtl` is ignored. Write the `.mtl` by hand: Blender writes absolute texture paths and linear colours.

- Tileable textures that come with IsoRim: `IsoRim/Swatches/` Wood, Stone, Metal, Fabric, Rock, Bark, Leaf, Soil, Solar,
  Glow, Flame. Each face draws its `Kd` colour, and the texture times `Kd` over it; players can switch textures off, so
  `Kd` alone must already be the right colour.
- IsoRim's own materials are in its `Models/isorim.mtl` (stuff, metal, hull, glass, glow ...). To use them, ship a copy of
  that file next to your `.obj` (`mtllib isorim.mtl`) and never edit it. Own materials go in your own `.mtl` with your own
  prefix (`mypack_trim`).
- **Material name prefixes have a meaning:**

| Prefix | Effect |
|---|---|
| `stuff...` | Tinted by what the thing is made of; `{stuff}` in its texture path becomes Wood, Stone, Metal or Fabric. Use `stuff` / `stuff_dark` / `stuff_deep` for the body of things made of stuff. |
| `glow...`, `flame...`, `ember...` | Unlit (screens, lamps, fire). |
| `leaf...`, `twig...` | Trees only (hidden / shown while bare). |

Names like `stuffing` or `glowing_rock` are caught by the prefixes too. Things not made of stuff: prefer fixed colours;
their `stuff*` faces take the colour of the main material in their cost list (steel grey, wood brown).

## Binding: `IsoRim.ModelDef`

```xml
<IsoRim.ModelDef>
  <defName>R3D_MyPack_Workbench</defName>   <!-- unique; R3D_<pack>_<thing> -->
  <model>Models/my_workbench.obj</model>    <!-- relative to the root of the mod that holds this def -->
  <builtFor>(3, 1)</builtFor>               <!-- the footprint the model was built for -->
</IsoRim.ModelDef>
```

`<thingDef>` (the ThingDef's defName, exact) is what binds it; in a pack the patch adds it (next section). Other ways to
match, in this order: `thingDef`, `graphicPath` (every thing drawn with that texture path), `thingCategory`, `match` (words
of the defName), `fallback`. With two `thingDef` entries for one thing, the def loaded later wins: load your pack after the
mod that already has one.

Common fields:

| Field | Default | Meaning |
|---|---|---|
| `model` | | The `.obj` file. `models` (a list): variants, each thing keeps one |
| `builtFor` | (0, 0) | Footprint (x, z) the model was built for. On a thing of another size it is stretched to fit; turned a quarter turn if its long side runs the other way. Always fill it in |
| `offset` | (0, 0, 0) | Move in model space (cells; turns with the thing). y lifts it |
| `rotation` | 0 | Extra turn in degrees, clockwise seen from above (e.g. 180 for a model built facing south) |
| `rotationByFacing` | | Api 6: one more turn per facing (north, east, south, west), for things whose game sprite for one facing is another's |
| `scale` | 1 | Uniform size |
| `surfaceHeight` | -1 | Where a lying pawn or things placed on it rest; -1 = top of the model. `seatHeight`, `beddingTop` for seats and beds |
| `wallMounted`, `mountHeight` | false, -1 | Hung on the wall the thing faces. Build it in the cell in front of the wall, against the wall at z = +0.5 (north) |
| `wallFit` | 0 | The model was built for walls this tall; its height follows the wall height setting |
| `cutaway` | Auto | Api 20: whether the cut-away lowers this wall: `Auto` (built walls, low walls included), `Never` (e.g. a half wall that keeps its window), `Always` (also a block IsoRim would not count as a wall). Walls only; see `Docs/Api.md` |
| `overArt` | false | Items: drawn with the model even while items use the game's art (the default; needs the option "3D raw materials") |
| `layFlat` | false | Items built as held: tipped onto their side on the ground |
| `outline`, `outlineMinSize` | 1, 0 | Outline strength (0: none); pieces smaller than `outlineMinSize` get none |
| `groundShadow` | true | Soft shading on the ground under it |
| `byAppearance`, `byState`, `byStyle` | | Other models by stuff look (Planks, Bricks ...), by an addon's state (Api 6), by Ideology style |
| `aged` | 0 | Api 15: drawn old and rusty |

Doors (`doorLeafModel`, ...), turrets (`topModel`), animals, mechs and many special parts have their own fields; see the
comments in IsoRim's `Source/Models.cs` and `Docs/Api.md`.

Examples:

```xml
<!-- An item (1x1), drawn with its model also while items use the game's art -->
<IsoRim.ModelDef>
  <defName>R3D_MyPack_Gizmo</defName>
  <model>Models/my_gizmo.obj</model>
  <builtFor>(1, 1)</builtFor>
  <overArt>true</overArt>
</IsoRim.ModelDef>

<!-- A wall lamp -->
<IsoRim.ModelDef>
  <defName>R3D_MyPack_WallLamp</defName>
  <model>Models/my_wall_lamp.obj</model>
  <builtFor>(1, 1)</builtFor>
  <wallMounted>true</wallMounted>
</IsoRim.ModelDef>
```

## Gating on IsoRim's version

Keep the ModelDefs in `Defs/` **without** `<thingDef>` (IsoRim skips an entry that names no thing) and let a patch add the
binding only when IsoRim's `Api.Version` is new enough for the fields the entry uses. An entry with only base fields
(those without a number below):

```xml
<Patch>
  <Operation Class="IsoRimPackGate.PatchOperation_IfIsoRimApi">
    <minVersion>2</minVersion>
    <apply Class="PatchOperationAdd">
      <xpath>/Defs/IsoRim.ModelDef[defName="R3D_MyPack_Workbench"]</xpath>
      <value><thingDef>MyMod_Workbench</thingDef></value>
    </apply>
  </Operation>
</Patch>
```

`IsoRimPackGate.dll` (in your `1.7/Assemblies`) holds that class. It needs no reference to IsoRim and does nothing when
IsoRim is missing or older than `minVersion`, so the pack loads either way. Every pack ships the same file; its source is in
IsoRim's `Tools/PackGate/`. A pack with code of its own may use its own copy of the class instead (the VFE Medieval 2 pack
does).

**Newer fields go into the patch too, never into `Defs/`.** An IsoRim older than a field doesn't know it, and a field it
doesn't know in `Defs/` is an XML error. Put the fields that need more than version 2 into the same `<value>` as
`<thingDef>`, and gate it on the highest version among them; an older IsoRim then leaves the thing as it is (no error, no
half-set model), a new enough one gets the whole entry:

```xml
<Operation Class="IsoRimPackGate.PatchOperation_IfIsoRimApi">
  <minVersion>20</minVersion>
  <apply Class="PatchOperationAdd">
    <xpath>/Defs/IsoRim.ModelDef[defName="R3D_MyPack_Tank"]</xpath>
    <value>
      <thingDef>MyMod_Tank</thingDef>
      <rotationByFacing>
        <li>0</li>
        <li>90</li>
        <li>0</li>
        <li>0</li>
      </rotationByFacing>
      <cutaway>Never</cutaway>
    </value>
  </apply>
</Operation>
```

`minVersion` by field: base fields and the plain binding 2; `hackedGlowOff`, `deadBellyUp`, `legFromHip`, `legLift`,
`legYaw` 5; `rotationByFacing`, `byState`, `style`, `crownTop`, `doorLeafRise` 6; `wallTop` 11; `backHeight` 13; wing
fields 14; `aged` 15; `graphicPath` 16; `doorLeafGather` 18; `cutaway` 20. The highest one used counts.

## Pitfalls

- **Never add or replace a whole ModelDef from a patch.** A def that a patch adds has no mod of its own
  (`modContentPack` is null), so IsoRim finds none of its model files and the thing stays as it was. Defs in `Defs/`,
  only the binding in the patch.
- **A pack with code: no method groups of Api delegate types.** The compiler caches a method group in a static field of the
  delegate's type, and the game's startup scan of static fields throws `TypeLoadException` on an older IsoRim that lacks
  the type, even if your code checks `Api.Version` first. Use `new IsoRim.SomeDelegate(Method)` or
  `Delegate.CreateDelegate(...)`, inside a method that only runs after the version check.
- **Items** use the game's art by default ("Items and weapons use the game's art"); only entries with `overArt` show
  their model then, and only with "3D raw materials" on. Apparel and weapons on the ground are drawn by IsoRim's ground gear
  (worn clothes laid down, held sprites), not by a model entry.
- **Doors, turrets, animals, mechs** need their special fields and drawers; a plain `model` on them is not enough.
- **Textures** named in an `.mtl` are loaded once; a changed texture needs a game restart (a changed `.obj` doesn't, see
  Reload below).
- **Load order:** after IsoRim and after the mod whose things you model. A binding for a thing from a mod that is not
  loaded is skipped quietly.

## Dev-mode model binder

With the game's development mode on and the 3D view on: Debug actions menu → **IsoRim → Model binder...**. With one
building or item selected it opens on that; otherwise click one on the map. It takes plain buildings (also ones the game
draws every frame) and items; it refuses doors, turrets, pawns, styled things and things whose model is shared by many defs
(a category or name match), with the reason.

The panel (it does not pause the game; typing in its fields doesn't move the camera):

- **File**: paste a full path to an `.obj` (for example a fresh Blender export), or **Pick** one from a searchable list:
  every `Models/*.obj` of IsoRim and of each active mod in your local Mods folder, and the workspace
  `<save data>/IsoRimBinder/Models/`. Under it: triangles and size, and warnings (below the ground, wider than the
  footprint, stuff faces on a thing without stuff). **Reload** reads the file again; a file saved again is also reloaded
  by itself within a second.
- **Built for, Offset, Rotation** (with 0/90/180/270 buttons), **Per facing** (`rotationByFacing`), **Turn** (turns the
  thing itself a quarter turn, to check each facing; the line under it shows the resulting turn per facing), **Scale**,
  **Surface height** (seat, bedding for seats and beds), **Wall fit**, **On the wall** and **Mount height**, **Cut-away**
  (`cutaway`, buildings, Api 20), **Over the
  game's art** and **Lay flat** for items, **Outline**, **Ground shadow**.
- Every change shows at once: the panel edits a copy of the thing's ModelDef and IsoRim draws the thing with it until you
  press **Revert** or load a game. Closing the panel keeps the change; opening it again on the thing carries on.
- **Copy from existing**: start from a model another thing already has (or this thing's own, to edit it).
- **Export** with a **Pack** name writes `<save data>/IsoRimBinder/<Pack>/`: `Defs/` (the ModelDef, defName
  `R3D_<Pack>_<thing>`, no thingDef, base fields only), `Patches/` (the binding plus the newer fields, gated on the
  `minVersion` they need), `Models/`
  (copies of the `.obj` and the `.mtl` files it names), and, the first time, `About/About.xml` and the gate DLL. Exporting
  more things with the same pack name adds them to the same folder. Fill in `About.xml` (author, packageId) and move the
  folder into your Mods folder to load it. The binder never writes into a mod or Workshop folder.

`<save data>` is the folder with your saves and config (on Windows `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld
by Ludeon Studios`).

## Coverage report

Debug actions menu → **IsoRim → Model coverage...**: for every loaded mod, its buildings and items that have no 3D model
(and no entry that sets their look otherwise: a turret gun, a wall top, kept flat ...), with their size and what IsoRim draws instead with your current options (raised sprite, flat sprite, block, IsoRim's 3D door,
worn look ...). Click a mod to list its things. **Export** writes the full list as `.tsv` and `.csv` to
`<save data>/IsoRimBinder/Coverage/`. Natural rock is left out; apparel and weapons are listed but usually need no model
(ground gear draws them).
