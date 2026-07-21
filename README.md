# CoreShift

**v1.1.0** · A zero-overhead Windows tray widget that shifts your laptop between a **quiet/cool**
and a **full-performance** profile in one click — controlling both the **CPU** and the **NVIDIA
GPU**. Built on .NET 8 / WPF with on-demand XAML rendering.

- **CPU** — toggles *Maximum processor state* between **100% (Coding Mode — Turbo Boost ON)** and
  **99% (Gaming Mode — Turbo Boost OFF)**.
- **GPU** — **BASE / LIGHT / HEAVY** power presets for the NVIDIA dGPU via NVML.

> Setting the maximum processor state to **99%** is the well-known trick that disables
> Intel/AMD Turbo Boost — lower temperatures, less fan noise, steadier clocks. **100%**
> restores full turbo for compiling and heavy workloads.

📄 **[Full technical specification →](docs/SPECIFICATION.md)**

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
- **GPU power presets** *(v1.1)*. BASE / LIGHT / HEAVY pills cap the NVIDIA dGPU via NVML, with an
  automatic locked-clock fallback for laptop vBIOSes that refuse power limits — and **no UAC prompt
  after the first setup**. Coexists with Armoury Crate. [Details ↓](#gpu-presets)

---

## Architecture

```
src/CoreShift/
  App.xaml(.cs)            Entry point + CLI verbs, tray icon, single-instance guard
  MainWindow.xaml(.cs)     Frameless widget, hover animations, dismiss-on-deactivate
  ViewModels/MainViewModel All observable state + INotifyPropertyChanged for DataTriggers
  Services/PowerService    CPU: powercfg query/set (silent: CreateNoWindow, no shell)
  Services/GpuPowerService GPU: NVML bindings, presets, elevated-helper handshake
  Services/StartupService  HKCU\…\Run registration
  Services/TrayPositioner  Bottom-right geometry via SystemParameters.WorkArea
  Services/MemoryUtil      Working-set trimming on idle
installer/CoreShift.iss     Inno Setup script (+ .NET runtime check, uninstall cleanup)
build/                      generate-icon.ps1, publish.ps1, build-installer.ps1
```

| Phase | What happens |
|------|---------------|
| 1. Init | Launch hidden, tray icon registers, app goes passive. |
| 2. Check | Left-click → CPU read via `powercfg -q … PROCTHROTTLEMAX` **and** GPU read via NVML. |
| 3. Render | Window positioned bottom-right via `WorkArea`, shown with the matching icon. |
| 4. Act | Tile flips CPU 100%↔99%; GPU pills apply a preset. DataTriggers re-render instantly. |
| 5. Dismiss | On focus loss: hide, collect, and trim the working set back to the OS. |

One executable, three roles — `CoreShift.exe` (tray), `--gpu-apply` (elevated helper),
`--gpu-install` (one-time task registration). See the [specification](docs/SPECIFICATION.md#31-process-model).

---

## GPU presets

| Pill | Effect |
|---|---|
| **BASE** | Removes CoreShift's cap — power limit back to vBIOS max, clocks unlocked. Armoury Crate governs. |
| **LIGHT** | ~40–60 W envelope. Default 50 W, or 990 MHz locked-clock fallback. |
| **HEAVY** | ~60–80 W envelope. Default 70 W, or 1402 MHz locked-clock fallback. |

The software power limit is tried first; laptop vBIOSes that reject it
(`NVML_ERROR_NOT_SUPPORTED`) automatically fall back to locked GPU clocks (the `nvidia-smi -lgc`
API). Tune the values in `%LOCALAPPDATA%\CoreShift\gpu-config.json`:

```json
{ "LightWatts": 50, "HeavyWatts": 70, "LightMaxMhz": 990, "HeavyMaxMhz": 1402 }
```

**Elevation:** NVML *reads* need no rights, so live data always shows. *Writes* do — so the first
preset you apply registers a scheduled task (**one UAC prompt, once ever**); after that, switching
modes never prompts again. No ASUS ACPI/WMI paths are touched, so this coexists with Armoury
Crate. Both GPU knobs reset on reboot, so the applied preset is tracked per boot session.

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
iscc installer\CoreShift.iss                                           # -> installer_output\CoreShift-Setup-1.1.0.exe
```

Tooling install (if needed):

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
```

> **Releasing a new version:** bump `<Version>`/`<FileVersion>`/`<AssemblyVersion>` in
> `src/CoreShift/CoreShift.csproj` **and** `MyAppVersion` in `installer/CoreShift.iss` — they are
> independent and must be kept in sync.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| GPU row shows an NVML error | No NVIDIA dGPU, or the driver's `nvml.dll` couldn't be found. CPU toggling is unaffected. |
| `…vBIOS refuses the change` | Normal on laptops — CoreShift automatically falls back to locked clocks. |
| `GPU helper did not respond` | The `CoreShift GPU Limit` scheduled task is blocked or was deleted. Delete it in Task Scheduler and apply a preset again to re-register. |
| UAC prompt appears when applying a GPU preset | Expected **once ever** (task registration). If it repeats, the task isn't persisting — check Task Scheduler policy. |
| GPU preset resets after reboot | By design. Both NVML knobs are volatile; re-apply after boot. |
| CPU toggle reports an error | A policy is blocking `powercfg` writes to the active scheme; relaunch CoreShift elevated. |

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

- **Why a scheduled task for the GPU?** NVML *reads* need no elevation but *writes* do. Making the
  whole app elevated would mean a UAC prompt on every boot. Instead CoreShift registers a task at
  `RunLevel HIGHEST` once (one prompt, ever) and starts it on demand — Windows lets a task's own
  creator do that without prompting again. The UI and the elevated helper coordinate through a
  nonce written to `%LOCALAPPDATA%\CoreShift`, not `HKCU`, because an elevated task process isn't
  guaranteed to see the same user registry hive.

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
