# Development setup

How to build, run and test Gelatinarm. Structure: [ARCHITECTURE.md](ARCHITECTURE.md).
Code rules: [CONVENTIONS.md](CONVENTIONS.md).

## Prerequisites

- Windows 10/11 with Visual Studio and the **Universal Windows Platform
  development** workload. Releases are built with Visual Studio 2026 (v18)
  Community.
- Windows SDK **10.0.22621.0** (target); minimum OS 10.0.17763.0.
- The .NET SDK (`dotnet` on the PATH): the build runs XAML Styler through it.
- A Jellyfin server with movies, series with several seasons, music and a
  collection.
- For console testing, an Xbox in Developer Mode.

NuGet packages restore on first build. The project is an old-style UWP `.csproj`
on .NET Native, C# 9 (`LangVersion` 9.0).

## Build

In Visual Studio: open `Gelatinarm.sln`, `Debug` and `x64`, build.

From a command line:

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" ^
  Gelatinarm.sln /t:Build /p:Configuration=Debug /p:Platform=x64 ^
  /nologo /v:minimal /m /p:AppxPackageSigningEnabled=false
```

`/p:AppxPackageSigningEnabled=false` is required: `Gelatinarm_TemporaryKey.pfx`
is not in the repo (`*.pfx` is ignored) and the Store signs the package. Without
it the build fails at signing (APPX0104/APPX0102/APPX0107).

### Editions: Standard and 4K

The same source builds two apps (Xbox makes an app choose between background
music and 4K video; see the root README):

| | Standard (`Debug`, `Release`) | 4K (`Debug4K`, `Release4K`) |
| --- | --- | --- |
| Package identity | `55770patel4prez.Gelatinarm` | same name + `4K`, display name "Gelatinarm 4K" |
| Capability | `backgroundMediaPlayback` | `hevcPlayback` (restricted) |
| Background music | yes | no; the app is closed when a game starts |
| Video | capped at 1920x1080 by the device profile | no cap |
| Display mode | never switched | switched for HDR and, optionally, the video's frame rate |
| Logos | `Assets` | `Assets4K` (the same files with a 4K cuff), packaged under the `Assets` names |

Pick the configuration in Visual Studio, or pass `/p:Configuration=Release4K`
(`/p:Edition=4K` works with any configuration). Both editions install side by
side.

Each edition is its own Store product with its own association file, neither
tracked in git: `Package.StoreAssociation.xml` (standard) and
`Package.4K.StoreAssociation.xml` (4K, a copy with that product's identity name
and reserved name). Without its file the 4K edition packages for sideloading
only. Visual Studio's "Associate App with the Store" rewrites the standard file
and the manifest's identity, so it is not used for the 4K product.

There is one manifest, `Package.appxmanifest`. The 4K build writes
`obj\x64\<Configuration>\Package.4K.appxmanifest` from it (the
`WriteEditionManifest` task in `Gelatinarm.csproj`) and fails if a substitution
does not match exactly once. `EDITION_4K` is defined for the 4K edition
(`XboxDevice.IsFourKEdition`).

**Judge warnings from a rebuild** (`/t:Rebuild`): an incremental build skips
compilation and shows no C# warnings. The baseline is no errors and only
ILT0005 warnings from .NET Native; anything else is new.

Two things a build catches that an editor does not:

- Every `.cs` file is listed in `Gelatinarm.csproj` as a
  `<Compile Include="..." />`; an unlisted file is silently left out. Every
  folder with XAML is named in the `AdditionalFiles` line; a page in a folder
  left out of it is skipped by the XAML rules.
- Types named in `Properties/Default.rd.xml` are matched by string; a renamed or
  deleted one shows only as an ILT warning in a Release build.

### Analyzers

Four packages run at build: `Microsoft.CodeAnalysis.NetAnalyzers`,
`Microsoft.CodeAnalysis.CSharp.CodeStyle`, `SonarAnalyzer.CSharp` and
`Roslynator.Analyzers`. `.editorconfig` sets their severities; any warning
beyond ILT0005 fails the gate. Among them: constant log templates (CA2254),
unnecessary usings (IDE0005), formatting (IDE0055), unused private members,
parameters and assigned values, members that could be static, redundant casts,
discards, parentheses and null checks, defaults passed explicitly, conditions
always true or false, catch blocks that log without the exception, nested
conditionals, and the allocation rules that matter on an Xbox One (constant
arrays built per call, LINQ over indexable collections). Roslynator's rules are
listed one by one; Sonar's default set is on. A rule that conflicts with a
deliberate choice is turned off in `.editorconfig` with the reason beside it,
never suppressed in code. IDE0005 needs the compiler to write a documentation
file, so the project sets `DocumentationFile` and turns off CS1591. The one
`SuppressMessage` is on XAML event handlers with an unused parameter.

`Gelatinarm.Analyzers` (a netstandard2.0 Roslyn project in the solution,
referenced as an analyzer; it ships nothing) holds the project's own rules, the
GEL warnings. They resolve references by symbol across the compilation, XAML
included (the pages are `AdditionalFiles`; the XAML compiler's `.g.cs` counts
`x:Bind` and event wiring as uses). `Descriptors.cs` lists every rule with its
title, message and the finding that made it a rule, grouped by kind (dead code,
XAML, consistency, safety). Options (the same-named state that is deliberate,
the tables the clone check skips) are read from `.editorconfig`, where the
option names its rule. To add a rule: add its descriptor and the check in the
analyzer of its kind, and prove it fires on a seeded defect first. A GEL warning
is fixed, never suppressed; a rule that conflicts with a deliberate choice is
turned off in `.editorconfig` with the reason beside it.

### XAML form

Every XAML file is in XAML Styler's default form (`xstyler`, the local dotnet
tool pinned in `.config/dotnet-tools.json`; the build restores it). The
`CheckXamlStyle` target runs before the XAML compiler and fails the build naming
each file that has strayed; `msbuild Gelatinarm.csproj /t:FormatXaml` (or
`dotnet xstyler -f <file>`) puts it back. `/p:CheckXamlStyle=false` skips the
check on a machine without the SDK.

## Run

- Windows: target Local Machine, F5. An Xbox controller works over USB or
  Bluetooth; keyboard arrows and Space drive the player.
- Xbox: enable Developer Mode, pair Visual Studio with the console, target Remote
  Machine with the console's IP, deploy and run from the Visual Studio window (a
  command-line deploy from a non-interactive session does not bring up the
  remote debugger).

Focus, playback, memory, HDR and codec behaviour are the console's; Windows is
for quick iteration.

## Test

There are no automated tests; changes are tested by hand on a console. The
server log is the second witness for playback: it records every start, progress
report and stop with its position.

Before a release, run through at least:

1. Sign-in: server entry, username/password, Quick Connect, a second user and
   switching profiles, sign-out.
2. Browsing: home screen sections, library selection, each library type, filters
   and sorting (the A-Z column appears only when sorting by name), search,
   favourites.
3. Detail pages: movie, series/season/episode, album, artist, person,
   collection, moving between them and going Back.
4. Playback: start, resume from a saved position, the controller mapping
   ([ARCHITECTURE.md](ARCHITECTURE.md#controller-input)), audio and subtitle
   changes (playback restarts at the same position), next episode, stop, and the
   server's recorded progress.
5. Music: a song and an album, playing on while browsing, right trigger held to
   jump to the player, sign-out while playing (it must stop). A FLAC with more than
   4 MB of embedded cover art must start without a failed first attempt.
6. Settings: change each one, restart the app, check it persisted.
7. Controller only: every screen reachable and escapable with the D-pad and B,
   no focus traps.
8. Memory: on an Xbox One if possible, a long browse through a large library and
   an extended playback session.

For a failure, note the console model, the build configuration, the exact steps
and what the server log shows.

A change to the device profile (`DeviceProfileService`) is tested against the codec
clip set: [CODEC_TESTING.md](CODEC_TESTING.md).

## Release

1. Bump the version in `Package.appxmanifest` past the last Store submission
   (`AssemblyInfo.cs` must match; the build checks).
2. Build the Store upload package as in the root
   [README](../README.md#generating-store-release-unsigned). The package is
   uploaded unsigned; the Store signs it.

## Formatting

`.editorconfig` defines formatting, `.gitattributes` line endings: files are
stored LF and checked out CRLF, except `*.yml`, `*.yaml` and `*.sh` (LF). The
workflow `.github/workflows/formatting.yml` checks both on every pull request
and push to `main`. A branch that shows a one-line change as a whole-file
rewrite has committed CRLF: `git add --renormalize . && git commit`.
