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
| Calling the internal `SleeperBlanket.Draw(pawn, bed, at, yaw, size, ground)` by reflection for the blanket in a head-only bed | `ctx.DrawSleeperBlanket()` (Api 67, Docs/Api.md part 46; gate on `IsoRim.Api.Version >= 67`) | No arguments: IsoRim passes its own bed, position, yaw, size and bed height for this frame, so the blanket sits exactly where it does on IsoRim's own figure. It draws only when `FigurePlacement.HeadOnlyInBed` is true, at most once per drawer call, and flashes with the figure when hit. Returns true when drawn or waiting for its build budget. |
| The same reflection call with your own placing: `at` moved along the bed so the blanket starts at your figure's neck, `size` times a factor to fit a bigger figure | `Api.DrawSleeperBlanket(pawn, bed, at, yaw, size, ground, start, scale)` (Api 68, Docs/Api.md part 47) | Pass the unmoved `at` (the head's middle), your neck's distance from it toward the feet in cells as `start`, and the factor as `scale`. The blanket is the one the old call drew with `at` moved by `start - 0.14 * size * scale` and size `size * scale` (the same build, rounding aside). `start` and `scale` -1: exactly `SleeperBlanket.Draw` with the same arguments. Like the internal method it draws for any bed it is given (call it only when the bed hides the body), with the same cache, build budget and flash; it also returns false, drawing nothing, while the 3D view is not drawn this frame, for a size not above 0 and off the main thread. Each start/scale pair gets its own cache entry. |

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
| removing your materials from `IsoGlow`'s marks or `FadeFloors`' copies yourself | `Api.ReleaseFigureMaterial(material)` (version 64) when a configured material is no longer used: IsoRim forgets it and destroys its light pass and faded copies. |
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

- **Camera:** covered by Api 66 (Docs/Api.md part 45) and Api 68 (part 47), see "Camera (Api 66)" and "View switch, keys and
  constants (Api 68)" below.
- **Patching `ModelBaker.Write` to lift item outline rings out of the ground:** this works round an IsoRim bug. It will be
  fixed in IsoRim itself, after which the patch can go.
- **Patching `ModelLibrary.LoadAll` to restore ModelDefs:** no Api.
- `GroundGear.ChildOnly` and `GroundGear.IsGear` read vanilla data. Use your own checks.
- **IsoRim model files loaded by name** (tools) through `Api.LoadModel`: these keep working.

### Camera (Api 66)

A free or scripted camera that today writes `View3D`'s private fields, patches `ApplyCamera`, `TurnCamera`, the `Pitch` getter
or `HandleLook`, and calls `Cull.SetCamera`, `R3DMaterials.SetView`, `UprightCards.SetCamera`, `Sky.Draw` or sets `_IsoEye`
itself moves over like this (gate on `IsoRim.Api.Version >= 66`):

| Today | With Api 66 |
|---|---|
| A `TurnCamera` prefix that reads input and moves the eye | A handler on `Api.BeforeViewUpdate` (same moment: after IsoRim's own view keys, before the camera is set). |
| An `ApplyCamera` prefix with its own copy of the perspective branch, the private-field writes (`eye`, `fwd`, `up`, `right`, `orthoSize`, `aspect`, `tanHalf`, `camDist`, `nearNow`, `persp`, `cams`, `camerasOverridden`, `haveFrame`), the `FieldOfView` / `Altitude` setters, `LightingMatrix`, and the calls after it | `Api.PushViewPose(owner, () => ViewPose.FromEye(eye, yaw, pitch, fov))` once when the camera starts. IsoRim derives all of it from the pose. With `FocusDistance`, `Near`, `Far` left 0 the numbers are the usual free-camera ones (focus distance = distance to the ground along the line of sight within 10-150, near 0.08-1 by height, far = focus distance + view distance + 90); the matrices and fields come out bit-identical to that copy. |
| `yawNow` / `yawSet` writes and a `Pitch` getter prefix (pitch beyond IsoRim's range) | The pose's `Yaw` and `Pitch` (perspective -89..89); `View3D.Pitch`, `View3D.Yaw`, `Api.ViewPitch`, `Api.ViewYaw` and everything built on them report it. |
| Writing the settings `pitch` / `isoPitch` every frame so inlined readers see the camera's pitch | Not needed: nothing reads the settings for the camera any more. The options pitch slider shows the player's own pitch while the pose holds the view. |
| A `HandleLook` prefix and clearing `lookDragging` | `ViewPoseOptions.BlockPlayerCamera` (default true): IsoRim's look drag, turn, tilt and reset keys wait while the pose holds the view. |
| Relying on `Turning` being true so nothing is rebuilt while flying | `ViewPoseOptions.CountsAsTurning` (default true): the same, by rule. One rebuild of the facing-built parts after the camera gives the view back, as before. |
| On leaving: writing `freeYaw` so IsoRim's camera keeps facing the same way, and restoring the saved pitch | `Api.SetPlayerView(yaw, null)` and then dispose the handle. Nothing to restore: the player's settings were never touched. |
| Leaving when first person starts | Optional now: a pushed pose wins over first person, which resumes when the pose goes. |
| Reading `View3D.EyePosition`, `View3D.ViewDistance` | `Api.CurrentViewPose` (`Eye`, `Far`, `FocusDistance`, `Near`). |

### View switch, keys and constants (Api 68)

Gate on `IsoRim.Api.Version >= 68` (Docs/Api.md part 47):

| Today | With Api 68 |
|---|---|
| Reading `View3D.Active` | `Api.View3DActive` (same value). |
| Writing `View3D.Active = true` / `false` | `Api.View3DActive = true` / `false`: the same switch the player's corner button sets, the rebuild and state following on the next frame as before. Differences: nothing happens off the main thread, outside a game being played, during a gravship cutscene that holds the camera, or when set false while an addon keeps the view on (writing the field did it anyway). |
| Reading `View3D.ControlsLocked` | `Api.ControlsLocked` (same value). |
| Calling `FirstPerson.Leave(null)` (after checking `FirstPerson.On`) | `Api.LeaveFirstPerson()`: the same call, true if first person was on. Off the main thread it does nothing and answers false. |
| Calling `View3D.BindingHeld(def, unboundHeld)` | `Api.KeyHeld(def, unboundHeld)` (same answer: mouse buttons count; false while a window's search box has focus; an unbound binding answers `unboundHeld`). |
| Reading `View3D.DefaultTanHalf` | `Api.PerspectiveTanHalf` (same value, a const). |

Stays in the camera mod: its own patches on the game's camera driver, input, cursor and jumps (dolly, zoom, pointer locking or
warping, glides), keeping the game camera ahead of the eye, and remembering places. A camera that also wants a picture of its
view without the interface uses `Api.RequestSceneImage` (part 45).

## 9. Failures

A drawer or rule that throws is removed with one warning, and IsoRim draws as before. Only argument checks throw.
