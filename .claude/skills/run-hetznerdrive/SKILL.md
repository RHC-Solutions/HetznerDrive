---
name: run-hetznerdrive
description: Build, launch, run, screenshot, and drive the HetznerDrive WPF tray app on Windows. Use when asked to run or start HetznerDrive, screenshot its window, verify a UI change (menus, activity log, mapping list), check which rclone mount flags a mapping actually produces, or build the Release installer.
---

# Run HetznerDrive

WPF tray app (.NET 8, Windows-only) that supervises one `rclone mount` child process per
drive-letter mapping, and talks to Hetzner directly for on-demand folders. There is no headless mode
and no CLI surface, so it is driven through `.claude/skills/run-hetznerdrive/driver.ps1` — a
PowerShell harness over UI Automation that reads the real window, plus process inspection for the
functional checks.

All paths below are relative to the repo root.

> **This driver has real side effects.** Launching the app mounts the user's actual Hetzner storage
> using the credentials in `%LOCALAPPDATA%\HetznerDrive\credentials.dat`, and `stop` unmounts a drive
> they may be using. There is no test fixture. Prefer the read-only commands (`health`, `args`,
> `rows`, `rowmenu`, `shot`) over `stop`/`launch` when the app is already running.

## Prerequisites

- .NET 8 SDK
- `scripts\fetch-deps.ps1` — downloads `rclone.exe` and `winfsp.msi`; neither is committed
- [WinFsp](https://winfsp.dev) — required for drive-letter mounts only; on-demand folders work
  without it
- Inno Setup 6 — only to build the installer: `winget install JRSoftware.InnoSetup`

## Build and test

```powershell
dotnet build HetznerDrive.slnx
dotnet test src/HetznerDrive.Tests
```

Release build + installer (writes `installer\output\HetznerDrive-Setup-<version>.exe`):

```powershell
& .\scripts\build-installer.ps1
```

Version lives in three files and must stay in sync — `src/HetznerDrive.App/HetznerDrive.App.csproj`
(`<Version>`), `installer/HetznerDrive.iss` (`#define AppVersion`), and
`src/HetznerDrive.App/app.manifest` (`assemblyIdentity version`, `X.Y.Z.0`). `scripts\release.ps1
-Version X.Y.Z` bumps all three, but it also commits, tags, and publishes a GitHub release — do not
run it just to get a build.

## Run (agent path)

```powershell
$d = '.\.claude\skills\run-hetznerdrive\driver.ps1'

& $d health                     # process, exe path, version, rclone, drive, real log errors
& $d args                       # the live rclone mount command line
& $d rows                       # mapping rows with their cell values
& $d rowmenu                    # select row 0, open its context menu, LIST THE ITEMS
& $d traymenu                   # best-effort; see Gotchas
& $d tree -Filter 'Button'      # UIA element dump, regex-filtered
& $d shot                       # screenshot -> %TEMP%\hetznerdrive-driver\window.png
& $d stop                       # stop app + rclone, release the drive letter
& $d launch -Build installed    # or -Build release / -Build debug
```

Commands that report on a drive letter take `-Drive` (default `H`), because the letter is
per-mapping: `& $d health -Drive Z`.

`rowmenu` is the primary way to verify menu changes. It reads the menu's actual items and their
enabled state through UI Automation, so it does not depend on a screenshot landing on top.

`args` is the strongest functional check for anything touching mount behaviour: rclone exits on an
unknown flag, so a live process proves it accepted every flag `RcloneRunner.BuildMountArguments`
emitted. **This matters more here than in a single-backend app** — the flag set is protocol-specific,
and `--s3-chunk-size` against an SFTP remote or `--sftp-concurrency` against S3 is exactly the class
of mistake that only shows up at runtime. Check that the flags match the protocol in the mapping's
`Protocol` column.

`shot` uses `PrintWindow(PW_RENDERFULLCONTENT)`, so it captures the window **even when fully
covered** and never steals focus. Read the PNG afterwards — a black frame means it never rendered.

## Headless XAML check

Run this after **any** XAML edit, before bothering with the UI:

```powershell
$exe = 'src\HetznerDrive.App\bin\Release\net8.0-windows10.0.19041.0\HetznerDrive.exe'
$out = "$env:TEMP\hetznerdrive-selftest.txt"
$p = Start-Process $exe -ArgumentList '--selftest', $out -PassThru
$p.WaitForExit(60000); $p.ExitCode      # 0 = all windows loaded
Get-Content $out
```

`--selftest` constructs every window, calls `ApplyTemplate()` to force the visual tree, writes a
per-window report and exits 0/1. It exists because a green build proves nothing about XAML:
`StaticResource` keys and merged dictionaries resolve at *load* time, so a renamed brush compiles
fine and then throws the first time a dialog opens. It runs before the single-instance mutex, so it
works while the app is already open, and it never calls `AppController.Initialize()` — no mounts, no
registry writes.

Do not try to reproduce this from a PowerShell host. Relative pack URIs (`/Themes/Theme.xaml`)
resolve against the entry assembly, which is `pwsh`, so the merged dictionaries fail to load and
you spend the afternoon fighting `Application.ResourceAssembly` instead.

## Verifying protocol selection

The Auto benchmark is the part most worth checking after a change, and it leaves a trail in the
activity log and in `%LOCALAPPDATA%\HetznerDrive\mappings.json`:

- `mappings.json` → `ResolvedProtocol` and `ProtocolMeasuredUtc` record the last winner. Delete
  those two fields (or set `AlwaysReBenchmark` in `settings.json`) to force a re-measurement.
- The log carries one `Protocol probe — …` line per candidate, then `Selected … as the fastest
  protocol`. A protocol that was skipped says why.
- **Test speeds now** in the mapping dialog runs the same measurement without saving, and is the
  quickest way to exercise it.

No test file should survive a run. The selector writes
`.hetznerdrive-speedtest-<guid>.tmp` into the mapping root and deletes it in a `finally`, including
after a failed protocol — if one is left behind on the Storage Box, that is a bug.

## Verifying service mode

The service is a separate process, so the driver's `health`/`args` commands do not see it. Use:

```powershell
sc.exe query HetznerDrive                                  # installed? running?
sc.exe qc HetznerDrive                                     # binPath, LocalSystem, auto-start
Get-Content "$env:ProgramData\HetznerDrive\logs\*.log" -Tail 40
Get-CimInstance Win32_Process -Filter "Name='rclone.exe'" | Select-Object CommandLine
```

An rclone process whose parent is the service mounts to a **folder path**, never a drive letter --
seeing `H:` in a service-owned command line means the mount-target guard was bypassed and the mount
is invisible to interactive users.

Configuration lives in `%ProgramData%\HetznerDrive\service-mappings.json`. The directory is ACL'd to
SYSTEM + Administrators, so read it from an elevated shell. Editing that file directly is a
legitimate way to test the reconciler: the service watches the directory and converges within the
debounce window, with a five-minute sweep as a backstop.

Both mutating paths (`Settings -> Windows service`, and saving a serviced mapping) relaunch the app
elevated via `--service-apply <publish|install|uninstall>`. That switch is scriptable, and it is far
easier to drive than the dialog:

```powershell
Start-Process .\HetznerDrive.exe -ArgumentList '--service-apply','publish' -Verb RunAs -Wait
```

Exit code 3 means the elevated copy could not read the user's DPAPI credentials -- almost always
because UAC was approved as a different administrator account.

## Run (human path)

`dotnet run --project src/HetznerDrive.App` opens the window and blocks. Useless for verification;
use the driver.

## Gotchas

Most of these are WPF/UIA facts inherited from the sibling WasabiDrive app and re-verified here only
where noted.

- **Single-instance mutex.** A second instance shows a message box and exits immediately. `launch`
  throws rather than silently doing nothing — run `stop` first.
- **Kill order matters.** Killing `HetznerDrive.exe` alone orphans its `rclone.exe` child, so the
  drive letter stays mounted and the next launch fails with *"drive is already in use by another
  volume"*. Always stop the supervisor first, then rclone (what `stop` does).
- **WPF GridView rows are `ControlType.DataItem`, not `ListItem`.** Searching for `ListItem` returns
  zero results. Their `Name` is the view-model type name
  (`HetznerDrive.App.ViewModels.MappingViewModel`); the visible values are child `Text` elements.
- **Context menus are separate top-level windows.** A WPF `ContextMenu` (and the tray menu) is its
  own popup window, *not* a descendant of the app window — search from the desktop root, then filter
  by `ProcessId` or you collect the menu bar of every running app.
- **`BoundingRectangle` can be `±Infinity`** for collapsed/offscreen elements; an unguarded `[int]`
  cast throws *"Value was either too large or too small for an Int32"*.
- **`SetForegroundWindow` is refused** for a process that does not already own the foreground, so a
  `CopyFromScreen` shot silently captures whatever *is* on top. This is why `shot` uses
  `PrintWindow`; `-Focus` opts into the unreliable path.
- **Synthetic mouse clicks are unreliable and rude.** `rowmenu` uses `SelectionItemPattern.Select()`
  + `SetFocus()` + `Shift+F10` instead — no cursor movement, and immune to DPI scaling.
- **Tray icon is not reachable via UI Automation.** Windows 11 keeps third-party notification icons
  in the "Show hidden icons" overflow flyout, which is absent from the UIA tree until a human opens
  it. Verify the tray menu by hand against `TrayIconFactory.Create`.
- **Do not grep the log for `Error`.** Stored file names can contain the word. Match the severity
  field: `'\s(ERROR|CRITICAL)\s+:'`.
- **`VerboseLogging: true`** in `settings.json` runs rclone at `DEBUG`, which turns the activity log
  into a firehose. That is the setting to toggle when testing log UI behaviour.
- **Cache location is per-mapping, not global.** `settings.json → DefaultCache.CacheDir` only applies
  to *new* mappings. Confirm with `args`, not with the Settings dialog.
- **On-demand folders never start rclone**, so `args` reports nothing for them. Verify those through
  the folder itself and the activity log.
- **A serviced mapping refuses to mount from the app.** That is deliberate -- two rclone processes
  racing for one mountpoint is worse than a clear refusal. Untick the service option to mount it
  interactively.
- **A directory mountpoint must not exist before mounting.** WinFsp creates it and removes it on
  unmount, so `Directory.Exists` is exactly the readiness check. A leftover folder from a hard kill
  will block the next mount until it is deleted.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `launch` throws "Already running" | An instance holds the mutex. `& $d stop` first. |
| Mount fails, *"drive is already in use"* | Orphaned `rclone.exe` still owns the letter. `& $d stop`. |
| `rows` returns nothing | No mappings configured, or you searched for `ListItem` instead of `DataItem`. |
| `rowmenu` prints "(none found)" | The row never took keyboard focus. Ensure the window is not minimised. |
| Menu dump lists File/Edit/View/Terminal | You forgot the `ProcessId` filter — that is VS Code's menu bar. |
| Mount fails with base64 / "couldn't decrypt password" | The `pass` value was not run through `RcloneObscure.Obscure`. |
| SFTP copy dies partway with checksum or connection errors | Concurrency exceeded Hetzner's ~10-session cap; check `ConcurrencyFor` and the `--transfers`/`--checkers` in `args`. |
| SMB mapping reports port 445 unreachable | SMB is off for that box in the Hetzner console, or the ISP blocks 445. Both are real, not app bugs. |
| `ISCC.exe not found` | Inno Setup installs to `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`. `build-installer.ps1` already probes there. |
| Build warns "No signing certificate configured" | Expected — builds are unsigned unless `HETZNERDRIVE_SIGN_*` is set. |
