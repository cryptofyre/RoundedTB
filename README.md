<div align="center">

<img src="RoundedTB/Images/StoreLogo.scale-400.png" width="96" alt="RoundedTB logo">

# RoundedTB

Margins, rounded corners and segments for the Windows 11 taskbar.

[![Latest release](https://img.shields.io/github/v/release/cryptofyre/RoundedTB?label=release)](https://github.com/cryptofyre/RoundedTB/releases/latest)
[![CI](https://github.com/cryptofyre/RoundedTB/actions/workflows/ci.yml/badge.svg)](https://github.com/cryptofyre/RoundedTB/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/cryptofyre/RoundedTB)](LICENSE)

![RoundedTB in dynamic mode](https://user-images.githubusercontent.com/31840547/134795141-76349eaf-12da-40f8-b2a0-d7b7c268d152.png)

</div>

## Install

| | Download | |
|---|---|---|
| **Recommended** | [`RoundedTB.appinstaller`](https://github.com/cryptofyre/RoundedTB/releases/latest/download/RoundedTB.appinstaller) | Installs RoundedTB and keeps it up to date |
| Package | `RoundedTB_<version>.msixbundle` | Installs one version, x64 and ARM64 in one file |
| Portable | `RoundedTB_<version>_win-x64.zip` / `_win-arm64.zip` | Extract the `RoundedTB` folder and run `RoundedTB.exe` |

Everything is on the [releases page](https://github.com/cryptofyre/RoundedTB/releases/latest), signed and self-contained: no .NET or Windows App SDK runtime needed.

The RoundedTB on the Microsoft Store is the [original project](https://github.com/RoundedTB/RoundedTB), not this fork.

## Features

- **Rounded corners and margins** on every taskbar, including secondary monitors.
- **Dynamic mode** splits the taskbar into segments around the apps and the system tray, and can round the button hover highlights to match.
- **WinUI 3 settings flyout** that opens from the tray icon above the taskbar, with acrylic and smooth page transitions.
- **Insider watermark remover** (Advanced → Insider watermark). A C# port of [UWD2](https://github.com/machineonamission/uwd2) by Melody that patches the running Explorer on x64 and ARM64, reapplies itself after Explorer restarts, and never touches the Activate Windows watermark. It fetches Microsoft's symbols once per Windows build.
- **Native ARM64** alongside x64.
- **Works on current Insider builds** and redraws when the resolution, scaling or work area changes.

Settings live in `%LOCALAPPDATA%\RoundedTB\rtb.json`. The packaged app keeps its own copy, seeded from that file on first run.

## Known issues

- **Auto-hide taskbar** isn't supported.
- **Windows 10** hasn't been tested and probably doesn't work.
- **Corners aren't antialiased.** This is a Windows limitation ([torchgm/RoundedTB#4](https://github.com/torchgm/RoundedTB/issues/4)).
- **The top of the taskbar isn't rounded.** Raising the top margin helps, but the value that looks right can change after a restart.
- **Dynamic mode may not cut off the left side on first run** if you've never changed the taskbar alignment. Switch to Left and back to Center to fix it ([torchgm/RoundedTB#98](https://github.com/torchgm/RoundedTB/issues/98)).
- **Dynamic mode can stay applied after RoundedTB exits.** Restarting Explorer or signing out clears it.
- **Rounded highlights** rely on the taskbar's internal layout, so a future Windows build can quietly stop them working.
- **TranslucentTB** works with the compatibility option enabled, but may flicker when the taskbar updates.

If anything breaks badly, press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Esc</kbd>, end RoundedTB and restart Windows Explorer. RoundedTB makes no permanent changes (apart from running on startup if you turn that on), so a restart always clears it.

## Building

Requires the .NET 10 SDK and Visual Studio's C++ build tools (for `RoundedTB.Tap`, the native part of the highlight rounding). Pick the platform, `x64` or `ARM64`:

```
dotnet build RoundedTB/RoundedTB.csproj -c Release -p:Platform=ARM64
```

Unpackaged release build:

```
dotnet publish RoundedTB/RoundedTB.csproj -c Release -r win-arm64 -p:Platform=ARM64 -p:PublishTrimmed=true
```

MSIX package (unsigned; sign it, or register the unpacked layout in Developer Mode, to install it):

```
dotnet publish RoundedTB/RoundedTB.csproj -c Release -r win-arm64 -p:Platform=ARM64 -p:PublishTrimmed=true -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false
```

### Releases

GitHub Actions builds releases from tags: `v*` publishes a release and `canary*` a pre-release. The binaries and the `.msixbundle` are signed with Azure Artifact Signing when these secrets are set in the `release` environment (limit its deployments to `v*` and `canary*` tags):

| Secret | |
|---|---|
| `ARTIFACT_SIGNING_ENDPOINT`, `ARTIFACT_SIGNING_ACCOUNT`, `ARTIFACT_SIGNING_PROFILE` | The signing account and certificate profile |
| `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` | A service principal allowed to sign with that profile |

The certificate subject must match the `Publisher` in `RoundedTB/Package.appxmanifest`. Without the secrets, releases carry only the unsigned zips. Stable releases also attach `RoundedTB.appinstaller`, which installed copies check for updates.

## Credits

RoundedTB was created by [torchgm](https://github.com/torchgm); see the [original project](https://github.com/RoundedTB/RoundedTB). This fork builds on the maintenance work in [Gniang/RoundedTB](https://github.com/Gniang/RoundedTB).
