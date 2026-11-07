# Static model API: local verification

Consumer and harness: [IsoRim Material and LOD](https://github.com/muziyue707-sys/IsoRim-MaterialLOD). Its [validation report](https://github.com/muziyue707-sys/IsoRim-MaterialLOD/blob/main/VALIDATION.md), [same-camera captures and frame intervals](https://github.com/muziyue707-sys/IsoRim-MaterialLOD/blob/main/review/comparison.md), and [fault-injection/runtime assertions](https://github.com/muziyue707-sys/IsoRim-MaterialLOD/blob/main/review/lod-acceptance.txt) accompany this proposal.

Tested 2026-11-07 on RimWorld 1.7.5000 rev1275 / Waylith / Windows, based on upstream `4cc784a669ef049ba081296ff94f50bf9cc4dd9d`. Candidate DLL SHA256: `9CC4D7678413BAAEB9DE707F82F1A9F47E959C49C3962426B5E107571E0AA959`. No DLL is included in this PR. The API version is assigned at merge.

The addon has no Harmony reference or internal access. The separate isolated harness inspects the native section mesh to verify complete normal/tangent arrays, model cache identity, and absence of model-loader/writer patches. It tests detached snapshots, invalid basis rejection and deliberately throwing callbacks. Each failing callback runs once, is disabled, and falls back to native rendering.

Ordinary building acceptance includes near/mid/far replacement and return, threshold jitter, selection, HP/rotation save/reload, despawn cleanup, daylight changes, indoor torch, fog and roof cover/restoration. Comparison runs load the same saved map and camera with original upstream, unused candidate API, and candidate plus consumer. Frame intervals are reported, not claimed as GPU time or FPS gains. Formal configs/saves (648 files) and original Workshop mods (3040 files) retained their hashes in every run.

Tests do not accept dynamic models, IsoRimZ floors, full Mod stacks, large-scene performance or long-term saves. Existing ambient Workshop metadata errors occur in baseline too and are documented. The consumer test mod must never be enabled in a real save.

Reproduce using the consumer's `scripts/Build.ps1`, `scripts/Launch.ps1`, and `tests/` (Windows paths must be adapted). Production additions and their failure/ownership contracts are documented in [StaticModelExtensions.md](StaticModelExtensions.md).
