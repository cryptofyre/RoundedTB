# WinUI 3 settings-flyout spike: results

Spike branch `spike/winui3-flyout` (off `feature/taskbar-flyout` @ 8d54121). Measured 2026-11-01 on
Windows on ARM64, Insider 28120, 175% scaling, 3270x2180, dark theme.

## What was built

`RoundedTB.WinUI/` is a separate project that isn't in `RoundedTB.sln`. It targets Windows App SDK
2.6.4 and .NET 10, is unpackaged and self-contained, and builds for x64 and ARM64.

- **Tray icon:** plain `Shell_NotifyIcon` (NOTIFYICON_VERSION_4) on a hidden window, with a
  Settings/Quit context menu. Left-click toggles the flyout. H.NotifyIcon wasn't needed.
- **Flyout window:**
  - `AppWindow` with no border or title bar, a WS_POPUP + tool window, topmost and hidden from
    Alt+Tab. It hides when it loses focus and on Esc.
  - It opens on the monitor under the cursor, using that monitor's DPI and taskbar edge, 12px from
    the edges.
  - The window is sized to the panel and resizes when Advanced expands.
- **Acrylic:** `DesktopAcrylicController` with a custom tint (#1C1C1C, light #EEEEEE), wrapped in a
  `SystemBackdrop` subclass.
- **Slide:** a Composition `Translation` key-frame animation on the panel, masked by an `InsetClip` on
  the window root. The timings and easing are the WPF flyout's: 250 ms in on cubic-bezier(0,0,0,1)
  and 150 ms out. The animation runs on the compositor, not the UI thread.
- **Settings page:**
  - Basic/Dynamic as a toolkit `Segmented`, corner radius and margin `Slider`s, and Link all sides
    with split margins.
  - Segments and Behaviour `ToggleSwitch`es.
  - An Advanced `Expander` holding the per-segment `Segmented`, radius, margins and width, plus the
    remaining options.
  - It reads and writes `%LOCALAPPDATA%\RoundedTB\rtb.json` through the same `Types.Settings`
    shape, with the same "main shape writes all five layouts" rule. It doesn't drive the taskbar.
    "Run at startup" is shown but disabled.
  - Reading the real `rtb.json` and writing it back gives a byte-identical file (ignoring line
    endings).

## The animation question: can the acrylic slide with the content?

This is the main reason to consider WinUI, and the answer is **not in stable Windows App SDK
today**. I tried four approaches:

| Approach | Result |
|---|---|
| Acrylic on a child `ContentIsland` (via `ChildSiteLink`) placed inside the panel | ✗ The island draws **on top of** the XAML controls; nothing is visible but acrylic. |
| `Popup.SystemBackdrop` with the popup constrained to the window | ✗ The backdrop doesn't render. |
| `Popup.SystemBackdrop` with a windowed popup (`ShouldConstrainToRootBounds=false`) | ✗ The acrylic renders but stays fixed to the popup HWND while the content slides. This is the same limitation as WPF. |
| **`ContentExternalBackdropLink`** with its placement visual inside the panel | ✓ **The real acrylic slides and clips with the content.** This API is still `[Experimental]` in 2.x; the test build used WASDK `2.5.4-experimental` (`-p:RtbExperimental=true`). |

So the stable build uses the same compromise as the WPF one. It runs window-level acrylic at rest,
and during the slide swaps it for a 97% tint that moves with the content. The difference is that the
slide is a compositor animation (below). Once `ContentExternalBackdropLink` reaches stable, the
`#if RTB_EXTERNAL_BACKDROP` path in `FlyoutWindow.xaml.cs` is the whole change.

Supporting changes needed along the way:
- `DwmExtendFrameIntoClientArea(-1)`. Without it, the clear parts of the window render black.
- A system `DispatcherQueue`, which `Windows.UI.Composition` needs and WASDK doesn't create.
- Overriding `ToggleSwitchThemeMinWidth`. Otherwise a ToggleSwitch with no On/Off text still takes
  154 px.

## Numbers

Both apps were measured the same way. A small tool clicks the tray icon through UI Automation, from
the overflow, because new tray icons are hidden on this build. It then captures a 100 px-wide strip
of the flyout area every ~8.5 ms and compares each frame with a pre-click baseline.
- "first": the first frame that changed.
- "settle": when 99% of the final area is showing.
- "updates": distinct frames during the slide.
- "max gap": the longest time between visual updates.

The display refreshes at 120 Hz, so ~8.3 ms per frame is the ceiling.

### Memory (flyout closed vs open)

| | Working set | Private bytes |
|---|---|---|
| WPF (running Debug build, includes the taskbar engine), closed | 172 MB | 191 MB |
| WPF, open | 174 MB | 191 MB |
| WinUI, trimmed Release, window created at startup, closed | 131 MB | 96 MB |
| WinUI, same, open | 144 MB | 107 MB |
| WinUI, same, closed again after 3 opens | 146 MB | 107 MB |
| WinUI, `RTB_LAZY=1` (window created on first click), never opened | **90 MB** | **67 MB** |
| WinUI, lazy, after first open | 145 MB | 106 MB |
| WinUI, untrimmed Release, eager, closed | 146 MB | 94 MB |

The brief's WPF figures were 179 MB working set and 191 MB private bytes; I measured 172/191 just
now.

Caveat: the WPF process also runs the whole taskbar engine (UIA, the worker loop, Newtonsoft), so
this isn't apples to apples. A migrated app would add that engine on top of the WinUI numbers. The
engine doesn't depend on the UI framework, so it should cost about the same in either app. The
clear win is private bytes: about 95–107 MB vs 191 MB. Working set gains less, because WinUI maps a
lot of native code (Microsoft.UI.Xaml, dcomp, DWriteCore).

Idle CPU with the flyout closed was 0.16% of one core for WinUI over 30 s. WPF used 3.4%, but that
includes the 10 Hz taskbar loop.

### Open latency and slide smoothness (ARM64)

| Run | first | settle | updates | max gap |
|---|---|---|---|---|
| WPF #1–3 | 33–36 ms | 201–225 ms | 11–12 | 33–49 ms |
| WinUI (trimmed) warm #2–3 | 11–15 ms | 196–210 ms | 18–23 | 16–31 ms |
| WinUI (untrimmed) warm #2–4 | 11–17 ms | 209–210 ms | 21–23 | 17–28 ms |
| WinUI first open after start, eager window | 33–36 ms | 309–345 ms | 22 | 100–126 ms |
| WinUI first open, lazy window | 579 ms (in-app click to first frame) | ~900 ms | n/a | n/a |

In-app timings for warm opens: click to first XAML frame took 14–24 ms, and click to slide finished
took 277–292 ms.

Warm, WinUI shows the panel about 20 ms sooner. It also renders about twice as many distinct frames
during the slide, with the longest stall roughly halved. That's the difference between an animation
on the UI thread (WPF's `CompositionTarget.Rendering` loop) and one on the compositor. The first
open after start has one long stall while XAML lays out for the first time.

### Self-contained size

Each publish folder was zipped with `Compress-Archive` on this machine:

| Build | Folder | Files | Zip |
|---|---|---|---|
| WPF arm64 (release workflow: single file, compressed) | 72.0 MB | 6 | 63.5 MB |
| WPF x64 | 78.3 MB | 7 | 68.2 MB |
| WinUI x64, plain `Microsoft.WindowsAppSDK` reference | 233.4 MB | 522 | 90.3 MB |
| WinUI arm64, AI/ML/Search/Widgets assets excluded | 191.5 MB | 457 | 65.3 MB |
| WinUI x64, same | 174.6 MB | 457 | 66.9 MB |
| **WinUI arm64, excluded + `PublishTrimmed`** | 91.7 MB | 289 | **32.6 MB** |
| **WinUI x64, excluded + `PublishTrimmed`** | 83.0 MB | 289 | **33.0 MB** |

The v3.2.0 release zips are 72 MB (arm64) and 77 MB (x64); the GitHub runner compresses less than
`Compress-Archive` does here.

The metapackage, which the CommunityToolkit also depends on, pulls in onnxruntime, DirectML and
Search, about 60 MB, so their assets are excluded in the csproj. Trimming works for WinUI and is
unavailable for WPF. The trimmed ARM64 build was run and used for all the WinUI measurements above.
The trimmed x64 build also opens and renders correctly under emulation on this machine. A migrated
app would add the taskbar engine and UIAutomation interop on top of these sizes.

## Window frame

The 1px white frame in the first version of the spike wasn't a DWM border. The `OverlappedPresenter`
puts `WS_DLGFRAME` back every time the window is shown, which leaves a 3px non-client frame (window
630 px wide, client 624 px) that gets painted white. Removing the style once isn't enough. The fix is
a window subclass that answers `WM_NCCALCSIZE` with "no non-client area", so the client area always
fills the window.

Keeping the style has a side benefit: DWM still treats the window as framed, so it draws its normal
shadow. That shadow is faint, darkening the background 5–7 levels for ~10 px beyond the edge.

Also checked:
- Light theme, using the `RTB_THEME=light|dark` override.
- Expanding Advanced: the window grows to the available height and the content scrolls.

## Known issues in the spike

- Single-instance handling is just a mutex. There's no Win+F2 hotkey, About page, or startup toggle.
- The first open in lazy mode takes ~0.6 s.

## Recommendation: go, staged (not a big-bang migration now)

- **Go on WinUI 3 as the UI framework.**
  - The flyout is about as smooth as the platform allows: compositor animation, half the stalls,
    first frame in ~15 ms.
  - Private memory is about half.
  - The trimmed package is about half the size of today's zip.
  - The controls (Segmented, Expander, ToggleSwitch, Slider) come with real animated states, so
    none of the hand-made WPF templates are needed.
  - The taskbar engine can move over as-is.
- **But don't migrate *for* the acrylic slide yet.** The one thing WPF can't do (acrylic moving
  with the content) also needs an experimental API in WinUI. Stable WinUI gets the same "tint while
  sliding" compromise, only smoother. Plan to switch to `ContentExternalBackdropLink` once a stable
  Windows App SDK ships it; the code path already exists here.
- **Suggested order:**
  1. Land the event-driven worker (`refactor/phase2-event-driven`), which is where the idle CPU goes.
  2. Port the app shell: tray, hotkey, single instance, startup.
  3. Move the flyout over from this spike.
  4. Ship trimmed builds.
- **Risks:**
  - Trimming warnings from CsWinRT and the Windows SDK projection need a test pass.
  - WASDK self-contained means 289 files instead of a single exe, so no single-file distribution.
  - The toolkit's metapackage dependency means `ExcludeAssets` has to be kept up to date on each
    WASDK bump.

## Reproducing

```
dotnet build RoundedTB.WinUI -p:Platform=ARM64
dotnet publish RoundedTB.WinUI/RoundedTB.WinUI.csproj -c Release -r win-arm64 -p:Platform=ARM64 -p:PublishTrimmed=true
dotnet build RoundedTB.WinUI -p:Platform=ARM64 -p:RtbExperimental=true -p:BaseIntermediateOutputPath=obj-exp\ -p:BaseOutputPath=bin-exp\
```

Click timings are appended to `%TEMP%\RoundedTB.WinUI.timing.log`, and unhandled exceptions go to
`%TEMP%\RoundedTB.WinUI.crash.log`.
