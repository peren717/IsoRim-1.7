# Static model extension API (proposed; version assigned at merge)

An addon can supply a surface for an immutable textured building model and choose a visual replacement when IsoRim prints that building. IsoRim owns placement, section assembly and upload. No texture loaders, material defs, asset formats, LOD thresholds, update loops or shaders are added to IsoRim.

Consumer: the independent IsoRim Material and LOD addon. Existing LoadModel and WorldToViewport are reused. AddModelState selects gameplay states; using that for camera detail would compete with another addon's state names. This selector runs after gameplay model resolution, without changing that state or its shadow model.

The candidate keeps base Api.Version 60. The maintainer assigns and increments the version at merge. The consumer verifies the exact candidate DLL hash as well as its base API version; stock API 60 is insufficient.

## Contract

Main thread only. No save data. All additions are optional.

| Member | Meaning |
| --- | --- |
| ReadStaticModel(handle) | Detached snapshot: positions, UVs, material indices/names and texture paths in triangle-corner order. Null for unsupported geometry. Startup use, not per-frame. |
| RegisterStaticSurface(handle, normals, tangents, factory) | Register once. Validates and copies unit orthogonal bases with tangent w +/-1. Wrong arrays and duplicate registration throw. |
| StaticMaterialContext | Value type: handle, slot, original shared material. Do not mutate Original. Return null/original or a cached distinct replacement. Do not share a replacement across different originals. |
| ConfigureStaticMaterial(material, supportsFloorFade = false) | Native own-cell lighting and static texture culling configuration. Optional per-material floor-fade opt-in; its shader must implement `_IsoFade` and `_IsoFadeFrom`. Call before first print. Does not make arbitrary shaders compatible. |
| AddStaticModelSelector / RemoveStaticModelSelector | First non-null wins in registration order. No recursion. Duplicate registration throws. Invalid result/exception disables that registration with one warning. |
| StaticModelContext | Value type: Thing, base handle. LocalToMap(point) returns raw map-row coordinates in true units; LocalToWorld(point) additionally applies the current floor offset. Project world points using existing Api.WorldToViewport. |
| ReadStaticModelView(map, row) | Value snapshot: Visible, Floor, ViewedFloor, Offset. Uses the current renderer layout, including peek/fade floors. Offset is true-space (not height-compressed). Off-map/inactive/void rows are invisible. No frustum or roof-occlusion test. Cache raw geometry; refresh this row state as the view changes. |
| InvalidateStaticModel(thing) | Dirty its Things section including the realtime-only building-body layer. Caller owns throttling. No immediate rebuild. |
| StaticModelExtensionsActive | Models are drawn and this path is available, including ground and upper floors. |

```csharp
// Once after content load. ReadBasis and MyMaterials belong to the addon.
ModelHandle model = Api.LoadModel(content, "Models/crate.obj");
StaticModelGeometry imported = Api.ReadStaticModel(model);
ReadBasis(imported, out Vector3[] normals, out Vector4[] tangents);
Api.RegisterStaticSurface(model, normals, tangents, context =>
    MyMaterials.Get(context.Slot, context.Original));
// MyMaterials creates/caches a separate material and calls ConfigureStaticMaterial once.
Api.AddStaticModelSelector(context =>
    context.Model == model ? MyVisualPolicy.Select(context) : null);
// On an actual policy change: Api.InvalidateStaticModel(thing).
```

## Scope, failures and cost

Static spawned building textured faces only, including ground and upper floors. No moving draws, figures, door leaves, ghosts, damage overlays, beam/plant/fire/glowing/runtime-texture/vertex-color geometry. Generated copies (merged, cut, aged) do not inherit registrations. Surface scale must be positive. Decals retain precedence. Unsupported paths fall back individually; enabling floors never globally disables these APIs.

The renderer still owns hidden section suppression, floor placement, floor fade, native shadows and clipping tags. A selector may be called when hidden geometry is rebuilt for ordinary game reasons; consumers should use ReadStaticModelView to avoid unnecessary camera work. Faded/peeked floors are visible. The independent addon owns lower-floor detail policy and hidden-row scheduling; neither is imposed by the host API.

Custom shaders belong to the consumer. IsoRim's internal shader attributes, lighting uniforms and clipping tags are **not a stable public shader ABI**. ConfigureStaticMaterial is explicit opt-in, not a compatibility guarantee. A renderer shader change may require the consumer to adapt, rebuild and revalidate. The maintainer will document/version the final API on merge; this proposal leaves Api.Version and file organization unchanged.

Original geometry still controls gameplay, layout, attachment points and shadow selection. Replacements must preserve origin, units and footprint; the addon validates alignment. Basis data is in true model space; print placement transforms it. UV1 remains owned by IsoRim clipping tags. There is no tangent generation or image-convention conversion here.

Throwing selectors are removed. Throwing/unusable material factories are disabled. Each produces one warning and falls back to native rendering. Already baked custom faces remain until their normal section rebuild. Buffer misalignment restores the original material with at most one warning, never a red error. Registration still validates caller arguments synchronously; consumers must catch malformed asset data before opting it in. The reference consumer catches asset rejection and logs a warning once per rejected definition/model; host version mismatch logs one warning and disables the consumer.

No registrations: first-line exits for selector and surface/upload paths, no save writes or new patches. Contexts are value types and selector arrays change only on registration/removal. Geometry storage and material caches grow on first use as native assembly does. Finalization submits the existing normal/tangent lists on section rebuild. There is no host update loop.

ModelLibrary.LoadFile normalizes cache keys: XML paths with mixed separators and Api.LoadModel previously produced separate objects for the same file. The regular existing FinalizeMesh bridge uploads attributes. No new Harmony target is added; the addon does not reference Harmony or reflect into IsoRim.

See StaticModelExtensionsValidation.md for local verification and limits. No compiled DLL is included in this PR.
