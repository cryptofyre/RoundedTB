# RoundedTB

Add margins, rounded corners and segments to your Windows 11 taskbar.

![RoundedTB in dynamic mode](https://user-images.githubusercontent.com/31840547/134795141-76349eaf-12da-40f8-b2a0-d7b7c268d152.png)

## Download

Grab the latest build from the [releases page](https://github.com/cryptofyre/RoundedTB/releases/latest).

| File | For |
|---|---|
| `RoundedTB_<version>_win-x64.zip` | Most PCs (Intel / AMD) |
| `RoundedTB_<version>_win-arm64.zip` | Windows on Arm (Snapdragon etc.) |

Builds are self-contained, so no .NET runtime is needed. Extract the whole `RoundedTB` folder and run `RoundedTB.exe` — the exe won't start without the DLLs next to it. The binary is unsigned, so SmartScreen will warn on first run.

The RoundedTB on the Microsoft Store is the [original project](https://github.com/RoundedTB/RoundedTB), not this fork.

## What's different in this fork

- **Works on current Insider builds.** Dynamic mode masks the Start and Search buttons correctly again after the taskbar's layout changes.
- **Redraws on display changes.** Changing resolution, scaling or the work area rebuilds the taskbar mask instead of leaving it stale.
- **Native ARM64 builds** alongside x64.
- **Removes the Insider build watermark** (Advanced → Insider watermark). A C# port of [UWD2](https://github.com/machineonamission/uwd2) by Melody: it resolves `CDesktopWatermark::s_DesktopBuildPaint` from Microsoft's symbols and returns early from it in the running Explorer, on both x64 and ARM64. Re-applies itself if Explorer restarts, and never touches the Activate Windows watermark. Needs the internet once per build to fetch symbols.
- Plus the stability fixes from [Gniang's fork](https://github.com/Gniang/RoundedTB): .NET 10, a self-healing background worker, atomic settings saves and migration of old configs.

Settings live in `%LOCALAPPDATA%\RoundedTB\rtb.json`.

## Known issues

- **Windows auto-hide taskbar** isn't supported.
- **Windows 10** hasn't been tested and probably doesn't work.
- **Rounded corners aren't antialiased.** This is a Windows limitation ([torchgm/RoundedTB#4](https://github.com/torchgm/RoundedTB/issues/4)).
- **The top of the taskbar isn't rounded.** Raising the top margin helps, but the value that looks right can change after a restart.
- **Dynamic mode may not cut off the left side on first run** if you've never changed the taskbar alignment. Switch alignment to Left and back to Center to fix it ([torchgm/RoundedTB#98](https://github.com/torchgm/RoundedTB/issues/98)).
- **Dynamic mode can stay applied after RoundedTB exits.** Restarting Explorer or signing out clears it.
- **TranslucentTB** works with the compatibility option enabled, but may flicker when the taskbar updates.

If anything breaks badly, press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Esc</kbd>, end RoundedTB and restart Windows Explorer. RoundedTB makes no permanent changes (apart from running on startup if you turn that on), so a restart always clears it.

## Building

Requires the .NET 10 SDK.

```
dotnet build RoundedTB/RoundedTB.csproj -c Release
```

To produce a release-style build for one architecture:

```
dotnet publish RoundedTB/RoundedTB.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

Releases are built by GitHub Actions: pushing a `v*` tag publishes a release with x64 and ARM64 zips, and a `canary*` tag publishes a pre-release.

## Credits

RoundedTB was created by [torchgm](https://github.com/torchgm) — see the [original project](https://github.com/RoundedTB/RoundedTB). This fork builds on the maintenance work in [Gniang/RoundedTB](https://github.com/Gniang/RoundedTB).
