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
| ConfigureStaticMaterial(material) | Native own-cell lighting and static texture culling contract. Shader must preserve IsoRimTex positions, tags and uniforms. Call before first print. Does not make arbitrary shaders compatible. |
| AddStaticModelSelector / RemoveStaticModelSelector | First non-null wins in registration order. No recursion. Duplicate registration throws. Invalid result/exception disables that registration with one warning. |
| StaticModelContext | Value type: Thing, base handle, LocalToWorld(point) in true map units. Project using existing Api.WorldToViewport. |
| InvalidateStaticModel(thing) | Dirty its Things section including the realtime-only building-body layer. Caller owns throttling. No immediate rebuild. |
| StaticModelExtensionsActive | Models are drawn and this path is available; excludes stacked floors. |

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

Static spawned building textured faces only. No moving draws, figures, door leaves, ghosts, damage overlays, beam/plant/fire/glowing/runtime-texture/vertex-color geometry or stacked floors. Generated copies (merged, cut, aged) do not inherit registrations. Surface scale must be positive. Decals retain precedence.

Original geometry still controls gameplay, layout, attachment points and shadow selection. Replacements must preserve origin, units and footprint; the addon validates alignment. Basis data is in true model space; print placement transforms it. UV1 remains owned by IsoRim clipping tags. There is no tangent generation or image-convention conversion here.

Throwing selectors are removed. Throwing/unusable material factories are disabled. Each produces one warning and falls back to native rendering. Already baked custom faces remain until their normal section rebuild. Buffer misalignment restores the original material.

No registrations: first-line exits for selector and surface/upload paths, no save writes or new patches. Contexts are value types and selector arrays change only on registration/removal. Geometry storage and material caches grow on first use as native assembly does. Finalization submits the existing normal/tangent lists on section rebuild. There is no host update loop.

ModelLibrary.LoadFile normalizes cache keys: XML paths with mixed separators and Api.LoadModel previously produced separate objects for the same file. The regular existing FinalizeMesh bridge uploads attributes. No new Harmony target is added; the addon does not reference Harmony or reflect into IsoRim.

See StaticModelExtensionsValidation.md for local verification and limits. No compiled DLL is included in this PR.
