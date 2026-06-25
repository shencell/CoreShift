# CoreShift

A zero-overhead Windows tray widget that toggles the CPU **Maximum processor state**
between **100% (Coding Mode — Turbo Boost ON)** and **99% (Gaming Mode — Turbo Boost OFF)**
with a single click. Built on .NET 8 / WPF with on-demand XAML rendering.

> Setting the maximum processor state to **99%** is the well-known trick that disables
> Intel/AMD Turbo Boost — lower temperatures, less fan noise, steadier clocks. **100%**
> restores full turbo for compiling and heavy workloads.

---

## Highlights

- **Pure background process.** No window on launch — only a system-tray icon. The widget
  is rendered on demand and dismissed when it loses focus, so RAM/CPU use stays near zero.
- **Frameless, elegant UI.** Dark theme (`#121212`) with a green accent (`#1B5E20`) that
  fades in smoothly on hover. Crisp vector icons (`<Path>`), no external images.
- **Live state.** Reads the real setting via `powercfg` before showing, so the icon always
  matches reality: **PowerShell** glyph at 100%, **Controller** glyph at 99%.
- **Auto-start.** Self-registers under `HKCU\…\Run` on first launch; toggle it any time from
  the tray menu.
- **Small.** Framework-dependent single-file build, well under 5 MB.

---

## Architecture

```
src/CoreShift/
  App.xaml(.cs)            Tray icon, single-instance guard, on-demand show
  MainWindow.xaml(.cs)     Frameless widget, hover animation, dismiss-on-deactivate
  ViewModels/MainViewModel Current state + INotifyPropertyChanged for DataTriggers
  Services/PowerService    powercfg query/set (silent: CreateNoWindow, no shell)
  Services/StartupService  HKCU\…\Run registration
  Services/TrayPositioner  Bottom-right geometry via SystemParameters.WorkArea
installer/CoreShift.iss     Inno Setup script (+ .NET runtime check)
build/                      generate-icon.ps1, publish.ps1, build-installer.ps1
```

| Phase | What happens |
|------|---------------|
| 1. Init | Launch hidden, tray icon registers, app goes passive. |
| 2. Check | Left-click → `powercfg -q … PROCTHROTTLEMAX` read silently. |
| 3. Render | Window positioned bottom-right via `WorkArea`, shown with the matching icon. |
| 4. Toggle | Button flips 100%↔99% via `powercfg`; DataTrigger swaps the icon; window hides on focus loss. |

---

## Build

Prerequisites: **.NET 8 SDK** and (for the installer) **Inno Setup 6**.

```powershell
# one-shot: icon + single-file exe + installer
build\build-installer.ps1
```

Or step by step:

```powershell
powershell.exe -ExecutionPolicy Bypass -File build\generate-icon.ps1   # -> Assets\CoreShift.ico
build\publish.ps1                                                       # -> dist\CoreShift.exe
iscc installer\CoreShift.iss                                           # -> installer_output\CoreShift-Setup-1.0.0.exe
```

Tooling install (if needed):

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
```

---

## Design notes / trade-offs

- **Why framework-dependent (not trimmed self-contained)?** WPF does **not** support
  IL trimming or NativeAOT, so a *self-contained* WPF build is ~150 MB regardless. To honor
  the "< 5 MB" goal, CoreShift ships **framework-dependent**: a tiny single-file app that uses
  the shared **.NET 8 Desktop Runtime**. The installer detects a missing runtime and offers to
  download/install it automatically, so end-users still get a one-click experience.

- **Permissions.** The app runs `asInvoker` (no UAC prompt at boot). Changing the active power
  scheme normally succeeds for the current user; if a hardened policy blocks it, `powercfg`
  returns an error and CoreShift shows it in the widget — relaunch elevated in that case.

- **PROCTHROTTLEMAX** is a hidden setting by default; `powercfg` can still read and write it
  without un-hiding it. Both AC and DC indices are written, then `SCHEME_CURRENT` is reactivated.

- **Memory footprint.** Measured on Windows 11:
  - *Working set* (what Task Manager shows): ~78 MB right after launch, but the app calls
    `EmptyWorkingSet` on startup-idle and after every dismissal, so the **resting value in the
    tray is ~1–3 MB**. Pages fault back in instantly on the next show.
  - *Private bytes* (the real committed cost): **~32 MB** — the inherent floor of the .NET +
    WPF runtime. GC mode (`ConcurrentGarbageCollection`) and `InvariantGlobalization` were
    A/B-measured as neutral for this number; they're kept for fewer background threads and a
    smaller/faster startup.
  - If single-digit-MB *private* memory is a hard requirement, that means leaving WPF: a WinForms
    popup lands ~12–18 MB, and a pure Win32/Rust implementation ~1–5 MB — at the cost of WPF's
    DirectX-accelerated animations and vector crispness.
