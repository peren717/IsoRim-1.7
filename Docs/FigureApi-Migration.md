# Moving a custom figure mod from IsoRim internals to Api 62

This is for mods that draw pawns their own way, such as skinned or animated figures, and that today reach into IsoRim's
internal or non-Api members by reflection or Harmony. Api 62 (Docs/Api.md part 43) covers those needs with public, versioned
members. Internals can change in any update; Api members cannot.

Gate everything on `IsoRim.Api.Version >= 62`, and keep the old path for older IsoRim builds if you need it.

## 1. Standing in for the figure

| Today | Api 62 | Notes |
|---|---|---|
| Harmony prefix on `People.Draw` | `Api.AddFigureDrawer(drawer, FigureKinds.People)` | Return true to draw the figure yourself, false to let IsoRim draw it. Your drawer is asked after IsoRim's gates (3D view, races drawn as figures, hidden pawns, corpses shown as models, statues never), but before IsoRim's own look work, so a figure you take costs IsoRim nothing. |
| `People.FigureRace`, `People.Hidden`, `Corpses.ShownAsModel` gates | none needed | The drawer is only asked when IsoRim would draw a figure. |
| Skipping IsoRim's hooks, face drawers, wounds, ring note, blob shadow | same by default | A figure you take gets nothing from IsoRim: no blob shadow, no hooks, no held or carried things, as with the prefix. |
| Other mods' attachments (gene parts, decals, gear) lost on your figure | `ctx.RaiseFigureHooks(position, yaw, headPlace, bodyPlace)` | Optional. Their attachments are drawn in your head and body placings. `ctx.ReadFigureBoxes` gives IsoRim's head and body boxes to fit yours. |
| Selection ring placed by IsoRim's body | `ctx.NoteSelectionBox(...)` | Optional. Without it the ring goes round IsoRim's placing, which costs the look work. |

## 2. Placing

| Today | Api 62 |
|---|---|
| `People.GibbetOf`, `Corpses.Bones/Skin/RestHeight`, `Glass.HolderOf/EntryFor`, `Poses.Seated/Seat/SeatUnder/SleeperHeight`, `Carried.LiftOf`, `BodyAnims.ReadCrawl`, `Crawl.ChestLift`, `HoldingChains.Sway`, `Hashing.Unit` (corpse yaw) | `ctx.ReadPlacement(options, out FigurePlacement p)`, or `Api.ReadFigurePlacement(...)` outside a drawer: `Situation`, `Position`, `Yaw`, `Holder`, `Seat`, `Skin`, `Bones`, `Lift`, `ChestLift`, `HeadPlace`, `BodyPlace` and more |
| `Animals.Track` with your own stride and facing | `FigurePlacementOptions.Stride` and `Face`. The tracker steps once a frame, and the first read of the frame decides. |
| `FigureWeapons.AimOf` | `p.Aiming`, `p.AimGame`, `p.AimDrawn` |
| `FigureWeapons.PitchOf` | `Api.AimPitch(pawn, handsHeight)` |
| `Forms.HeightOf`, `Floors.FlatDelta`, `Ground.IsWater`, `People.ApparelColour`, `People.SizeFor` | `Api.SurfaceHeight`, `Api.DrawnDelta`, `Api.IsDrawnWater`, `Api.ApparelColour`, `Api.FigureSizeFor` |

Reading the placement runs IsoRim's look work for that pawn. If you work out the placement yourself, skip it and that cost goes away.
Placement is for people only; animals and mechs can be stood in for, but the read returns false for them.

## 3. Drawing meshes

| Today | Api 62 |
|---|---|
| `FigureScope.Active/Submit`, `Floors.FadeOfFloor`, writing `Floors.FadeSlot`, `Floors.DrawnMatrix(m, out floor)`, setting `_IsoOwnerFloor`, `IsoGlow.DrawLit`, `HitFlash.On/strength/step`, `Cull.Enabled`, `DynamicModels.DrawParts/Part` | `Api.SubmitFigureMesh(mesh, material, trueSpace, block, flags, submesh, flash)`. IsoRim squashes the mesh, moves it to its floor, sets `_IsoOwnerFloor` and `_IsoFlash` in your block, fades it on a peeked floor, culls it unless `NoCull`, draws the light pass with `LightPass`, follows the outline option with `Outline`, and moves or measures it for `Api.DrawFigure`/`FigureBounds`. Pass `ctx.Flash` as `flash`. |
| `Cull.MeshVisible` | `Api.FigureVisible(localBounds, trueSpace)` |
| `IsoGlow.Use/MarkFor/OwnCell/None`, `R3DMaterials.LitModel/LightModel/OutlineMoving` | `Api.ConfigureFigureMaterial(material, kind, lightPassShader)`. Also registers your material for the peek fade (see section 7). |
| `R3DMaterials.Shaded`, `ModelBaker.OutlineShade`, the tint rounding in `DynamicModels.PartsFor` | `Api.ModelVertexColour(tint, outline)`, `Api.RoundTint(tint)` |
| `ModelLibrary.BuildHull/OutlineWidth`, `View3D.OutlinePush`, `RoofClip.MovingTag`, `DynamicModels.Retire` | `Api.BuildOutlineHull`, `Api.OutlineWidth`, `Api.OutlinePush`, `Api.MovingMeshTag`, `Api.RetireMesh` |
| `IsoRimMod.Settings.*` reads, `SunShadowsSS.WillDraw`, `AmbientOcclusion.Available` | `Api.GetOption(fieldName)`, `Api.ScreenShadingOn` |
| `SpritePixels.Sample/GpuSample/IsSheet` | `Api.ReadSpritePixels(texture, size)` (null for a sprite sheet) |

Your own shaders can rely on the names and bindings in the figure shader contract (Docs/Api.md part 43, `Api.FigureShaderContract` = 1).
Anything else in IsoRim's .glsl files can change.

## 4. Models

| Today | Api 62 |
|---|---|
| `new Model()` and `ModelMaterial` fields, including `runtimeTexture` | `Api.CreateModel(ModelData)`. Run-time textures work on baked and moving models alike. |
| `DynamicModels.PartsFor` (IsoRim's ready meshes of a model) | `Api.ModelParts(model, tint, swatch, list)`. Ask every frame and never keep or destroy them. |
| `DynamicModels.DrawLocal/DrawMatrix` | `Api.DrawModel` (existing) |

## 5. Weapons, worn gear, carried things, muzzle

| Today | Api 62 |
|---|---|
| `WeaponModels.ModelOf/Hold/Draw/On`, `SpriteSolids.DrawSlab/DrawHeld/Thickness`, `R3DTextures.ModelTint/ModelSwatch`, refitting the model to the sprite yourself | `Api.ReadThingModel(weapon, ThingModelUse.Held, out info)` (`Model`, `Tint`, `Swatch`, `OnSprite`, `HandScale`, `BarrelEnd`, `Thrown`) and `Api.DrawHeldThing(weapon, pictureTrueSpace, scale)` |
| `Projectiles.Thrown`, `Projectiles.GunEnd` | `info.Thrown`, `info.BarrelEnd` |
| `Projectiles.FigureTip/FigureTipAt` | `Api.ReportMuzzle(pawn, weapon, tip, centre)`. Same store and same off-screen fallback as today. |
| `Carried.Draw`, `Carried.ItemScale` | `Api.DrawCarried(carrier, at, yaw)` |
| `WornGearDef`, `WornGear.For/ModelFor`, `ModelLibrary.LoadFile`, `ObjLoader.Marked` | `Api.ReadThingModel(apparel, ThingModelUse.Worn, out info)` (`Model`, `Tint`, `Mount`, `MountAnchor`) |
| Harmony postfixes on `WeaponModels.Uses` and `WeaponModels.ArtIsOwn` | `Api.AddWeaponModelRule(Func<ThingDef, bool?>)`. See below. |

**Weapon rules.** A rule is asked inside IsoRim's "does this weapon show its model" check. It runs after IsoRim has found a usable
model for the def and before IsoRim's own binding and art checks. Answer true for the model, false for the sprite, or null to let
IsoRim decide. Answer only for the weapons you mean to change and return null for the rest, so IsoRim keeps its own answer there.
Keep a rule a pure lookup with no game state changes, because it may be asked off the main thread.

Use `Api.HowBound(def)` inside a rule to see how the def's model is bound (values in Docs/Api.md part 43). It is safe to call
from a rule. To keep what two postfixes did:

- **Weapons bound only by their category** (for example grenades): answer true when `Api.HowBound(def)` is a category binding
  and the weapon is one you want.
- **Retextured official weapons that should keep their model:** answer true when your option is on, the def is from an official mod
  (`def.modContentPack.IsOfficialMod`), and `Api.HowBound(def)` says it is bound by its own name, a model rule or its picture.
  Answer false for defs you want shown as sprites. Return null otherwise; IsoRim then applies its own art check.

Dirty the map meshes yourself when an answer changes.

## 6. Baking on the ground

| Today | Api 62 |
|---|---|
| Harmony prefix on `GroundGear.Bake`, `ModelBaker.WritePosed`, `RoofClip.Mark/Tag`, `GroundGear.WornLook`, `ModelBaker.DamageMarks/Ghost` | `Api.AddGroundModelRule(Func<GroundModelContext, GroundModel?>)`. The context gives the `Thing`, IsoRim's model and `WornLook`. Answer a model (`Model`, `Scale`, `Yaw`, `Tint`) and IsoRim bakes it with roof clipping, outline and shadow, or null for IsoRim's own. Blueprints and damage marks stay IsoRim's. |

Models made with `Api.CreateModel` use the model light curve. In tests, a flat garment lit that way looked the same as one baked
through the old internal path, by day, at night and under a roof.

## 7. What changes when you move over

These are fixes. Nothing else should look or behave differently.

- **Gravship flights:** every sub-mesh you submit, and everything drawn through `SubmitFigureMesh` (face patches, wounds), flies
  with the ship. Parts drawn with `Graphics.DrawMesh` directly were left at their ground place before.
- **Peek fade:** a material set up with `ConfigureFigureMaterial`, whose shader reads `_IsoFade` and `_IsoFadeFrom`, is now faded
  on a peeked upper floor. Before, IsoRim only faded its own shaders, so other shaders were drawn solid.

## 8. Not covered by Api 62

- **Camera** (`View3D` private fields, `ApplyCamera`, `TurnCamera`, `HandleLook`, `Pitch` patches and the work after them):
  planned as a later Api version.
- **Patching `ModelBaker.Write` to lift item outline rings out of the ground:** this works round an IsoRim bug. It will be
  fixed in IsoRim itself, after which the patch can go.
- **Patching `ModelLibrary.LoadAll` to restore ModelDefs:** no Api.
- `GroundGear.ChildOnly` and `GroundGear.IsGear` read vanilla data. Use your own checks.
- **IsoRim model files loaded by name** (tools) through `Api.LoadModel`: these keep working.

## 9. Failures

A drawer or rule that throws is removed with one warning, and IsoRim draws as before. Only argument checks throw.
