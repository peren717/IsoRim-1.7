# Building IsoRim for RimWorld 1.7

Requires the .NET 10 SDK and five reference assemblies:

| Assembly | Source |
|---|---|
| `Assembly-CSharp.dll`, `UnityStubs.dll`, `Waylith.dll`, `Brutal.Vulkan.dll` | A RimWorld 1.7 installation, or the compile-only [RimRef package](https://github.com/krafs/RimRef). |
| `0Harmony.dll` | The **1.7** assembly from the [official Harmony mod release](https://github.com/pardeike/HarmonyRimWorld/releases). |

When the mod is inside a RimWorld 1.7 installation with Harmony in its usual Steam Workshop location, the project keeps
using those locations by default. For another installation or downloaded references, pass the paths explicitly:

```powershell
dotnet build Source/IsoRim.csproj "-p:RimWorldDir=C:\path\to\game-or-reference-folder" "-p:HarmonyPath=C:\path\to\1.7\Assemblies\0Harmony.dll"
```

`RimWorldDir` is the directory containing the four game DLLs, not the extracted package's root. No reference DLLs are
copied to the mod output. The default output is `1.7/Assemblies/IsoRim.dll`; use `-p:OutputPath=bin/validation` to build
under `Source/bin/validation` without replacing the packaged DLL.

## Compile without installing the game

RimRef contains signatures rather than executable game code. It is sufficient to compile all IsoRim source, but cannot
run the game or verify its rendered output. The following pinned versions were used for the complete-wall change:

- [Krafs.Rimworld.Ref 1.7.4984-unstable](https://www.nuget.org/packages/Krafs.Rimworld.Ref/1.7.4984-unstable).
- [Harmony mod v3.0.1.0](https://github.com/pardeike/HarmonyRimWorld/releases/tag/v3.0.1.0), using `HarmonyMod/1.7/Assemblies/0Harmony.dll`.

This PowerShell example downloads only build dependencies into a new temporary directory and checks the Harmony
archive against the release's published SHA-256 before extracting it:

```powershell
$dependencies = Join-Path ([System.IO.Path]::GetTempPath()) ("isorim-build-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $dependencies | Out-Null

$rimref = Join-Path $dependencies "rimref.nupkg"
Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/krafs.rimworld.ref/1.7.4984-unstable/krafs.rimworld.ref.1.7.4984-unstable.nupkg" -OutFile $rimref
Expand-Archive -LiteralPath $rimref -DestinationPath (Join-Path $dependencies "rimref")

$release = "https://github.com/pardeike/HarmonyRimWorld/releases/download/v3.0.1.0"
$harmony = Join-Path $dependencies "HarmonyMod.zip"
Invoke-WebRequest "$release/HarmonyMod.zip" -OutFile $harmony
$publishedHash = (Invoke-WebRequest "$release/HarmonyMod.zip.sha256").Content
if ($publishedHash -is [byte[]]) { $publishedHash = [System.Text.Encoding]::UTF8.GetString($publishedHash) }
if ((Get-FileHash -LiteralPath $harmony -Algorithm SHA256).Hash -ne ($publishedHash -split '\s+')[0]) {
    throw "Harmony archive checksum mismatch."
}
Expand-Archive -LiteralPath $harmony -DestinationPath (Join-Path $dependencies "harmony")

$gameReferences = Join-Path $dependencies "rimref/ref/net10.0"
$harmonyAssembly = Join-Path $dependencies "harmony/HarmonyMod/1.7/Assemblies/0Harmony.dll"
dotnet build Source/IsoRim.csproj "-p:RimWorldDir=$gameReferences" "-p:HarmonyPath=$harmonyAssembly" -p:OutputPath=bin/validation
```

The actual RimWorld 1.7 engine is available through Steam's `unstable` branch, as described in
[Ludeon's announcement](https://ludeon.com/blog/2026/10/announcing-the-waylith-engine-and-update-1-7/).
