# Contributing to RoundedTB

Thanks for helping out. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Open an issue with the **Bug report** template. The things that help most:

- Your Windows version and build (run `winver`), especially on Insider builds, where the taskbar changes often.
- Which mode you use (Basic or Dynamic) and the settings you've changed.
- A screenshot of the taskbar when it goes wrong.
- If RoundedTB crashed, `crash.log` from `%LOCALAPPDATA%\RoundedTB`.

Please search the existing issues first, and check the [known issues](../README.md#known-issues).

## Suggesting a feature

Open an issue with the **Feature request** template and describe what you'd like the taskbar to do, rather than how to build it. A mock-up or screenshot goes a long way.

## Pull requests

For anything bigger than a small fix, open an issue first so we can agree on the approach before you put the time in.

### Building

You'll need Windows 11, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and Visual Studio 2022 or later with the **Desktop development with C++** workload. The C++ part builds `RoundedTB.Tap`, the small native library behind the rounded button highlights. Build for your PC's architecture, `x64` or `ARM64`:

```
dotnet build RoundedTB/RoundedTB.csproj -p:Platform=x64
```

Then run `RoundedTB/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/RoundedTB.exe`. Quit any installed copy of RoundedTB first, since only one can manage the taskbar at a time.

### Guidelines

- Keep pull requests focused on one change.
- Match the style of the code around you. Comment only what the code can't say for itself: why something is done, not what it does.
- Test on the taskbar you have, and say in the pull request what you tested on (Windows build, single or multiple monitors, Basic or Dynamic).
- UI changes should look at home in Windows 11, in both light and dark mode. Include before and after screenshots.
