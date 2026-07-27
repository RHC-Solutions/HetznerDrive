# HetznerDrive

Mount a [Hetzner Storage Box](https://www.hetzner.com/storage/storage-box/) or
[Hetzner Object Storage](https://www.hetzner.com/storage/object-storage/) as a native Windows drive
letter or a OneDrive-style **Files On-Demand** folder.

HetznerDrive is a WPF tray app that drives [rclone](https://rclone.org) on top of
[WinFsp](https://winfsp.dev), with a GUI for credentials, multiple mappings, auto-mount at login,
and cache tuning — and it picks the **fastest transfer protocol for your connection automatically**.

## Storage Box or Object Storage?

These are two different Hetzner products and the difference matters:

| | Storage Box | Object Storage |
|---|---|---|
| Host | `uNNNNNN.your-storagebox.de` | `<location>.your-objectstorage.com` |
| Protocols | **SFTP, SMB/CIFS, WebDAV** | **S3** |
| Credentials | account password or SSH key | access key + secret key |
| Billing | fixed size | per GB stored |

**A Storage Box has no S3 endpoint.** If you want S3, you want Object Storage. HetznerDrive
supports both as separate account types and will not let you configure a combination that cannot
work.

## The fastest protocol, by default

New Storage Box mappings default to **Auto**, which does not guess. On first mount HetznerDrive:

1. **Probes each port** — 23 (SFTP), 445 (SMB), 443 (WebDAV) — recording the TCP handshake time.
   A port that does not answer is reported as unreachable rather than silently skipped, which tells
   you the difference between "SMB is slow here" and "your ISP blocks port 445".
2. **Transfers a real payload** over each reachable protocol, 16 MiB up and back down, **one
   protocol at a time** so they aren't competing for the same uplink.
3. **Ranks them by total bytes ÷ total time.** Combining both directions stops a protocol that
   downloads fast but uploads at a crawl from winning on half a story.

The winner is cached for two weeks so remounting and auto-mount at logon don't pay for the
measurement again. You can see the numbers yourself with **Test speeds now** in the mapping dialog,
override the choice at any time, or set **Re-measure on every mount** for a laptop that moves
between very different networks.

Why not just hard-code SFTP? Because which one wins genuinely depends on where you are. SMB can be
the fastest option on a low-latency link and completely unavailable on a coffee-shop network;
WebDAV is often the only one that survives a corporate firewall.

### Per-protocol tuning

Each backend gets flags suited to it rather than one generic set:

- **SFTP** — pipelines 64 outstanding requests inside a single SSH connection. Hetzner caps a
  Storage Box at roughly **10 concurrent SSH sessions**, and going over does not queue: the server
  refuses the extra sessions and rclone reports them as checksum errors partway through a copy. So
  `--transfers` and `--checkers` are clamped to stay inside that budget, and parallel read streams
  (which would each cost another session) are left off.
- **S3** — 16 parallel range reads per file, multipart uploads, and `--use-server-modtime` to skip
  a HEAD per object.
- **WebDAV** — parallel range reads, but no multipart upload (the protocol has none).
- **SMB** — throughput comes from concurrent files; slicing reads into separate streams hurts.

## Download

**[⬇ HetznerDrive-Setup.exe (0.2.0 preview)](https://github.com/RHC-Solutions/HetznerDrive/releases/download/v0.2.0/HetznerDrive-Setup.exe)**
&nbsp;·&nbsp; [All releases](https://github.com/RHC-Solutions/HetznerDrive/releases)

Run the installer; it installs WinFsp automatically if it's missing. Windows 10/11 x64.

> 0.2.0 is a **prerelease**: unsigned, so SmartScreen will warn, and not yet tested against real
> Hetzner hardware. Two things follow from the prerelease flag and are expected, not bugs — the
> `releases/latest/download/…` shortcut does not resolve, and the in-app updater (which queries the
> *latest* release) will not offer it. Both start working with the first stable release, at which
> point the link above becomes
> `releases/latest/download/HetznerDrive-Setup.exe`.

## How it works

```
HetznerDrive.App (WPF tray)
   ├─ drive letter    → HetznerDrive.Core → rclone.exe mount → WinFsp → H:\
   └─ on-demand folder → HetznerDrive.CloudFiles → Windows Cloud Files API → %USERPROFILE%\HetznerDrive\…
```

Drive-letter mappings run one `rclone mount` child process each. Secrets are injected into that
process via environment variables, so the password is never written to disk in plaintext nor placed
on a command line. Passwords are encoded with rclone's `obscure` transform in-process — shelling
out to `rclone obscure` would put the password in the process list, which is exactly what this
avoids.

On-demand folders don't use rclone at all; they talk to Hetzner directly through a protocol-agnostic
client (`IRemoteStorageClient`) with an implementation per protocol:

| Protocol | Implementation | Notes |
|---|---|---|
| SFTP | SSH.NET, 4-connection pool | The pool is what keeps hydration fan-out under Hetzner's session cap |
| SMB | `WNetAddConnection2` + `System.IO` | Uses the Windows redirector, so kernel caching comes free |
| WebDAV | `HttpClient`, raw DAV verbs | `Depth: 1` walk — `Depth: infinity` is optional and often disabled |
| S3 | AWS SDK | Presigned share links, batched deletes, server-side copy |

## Running without anyone logged in

By default mappings mount in your interactive session, which means they disappear at logoff. A
mapping can instead be handed to the **HetznerDrive Windows service**, which runs as LocalSystem and
starts at boot.

Two constraints are not workarounds but consequences of how Windows works, and the app enforces both
rather than letting you discover them as silent failures:

- **Files On-Demand folders can never be serviced.** The Cloud Files API registers a sync root
  inside a user profile and calls back into that user's session. There is no session-0 equivalent.
- **A service mount cannot use a drive letter.** Drive letters are per-logon-session, so `H:`
  mounted from session 0 is simply absent from your namespace. Service mappings therefore use a
  **folder mountpoint**, which lives in the global filesystem namespace and is visible everywhere.

Set it up in **Settings → Windows service → Install**, then tick *Mount with the Windows service* on
a drive mapping. Both steps ask for administrator approval, because registering a service and
writing to `%ProgramData%` require it. The app relaunches only itself elevated for that one step — it
otherwise runs unelevated on purpose, since it mounts into your own session and writes to HKCU.

The service reads its configuration from `%ProgramData%\HetznerDrive\`, watches it for changes, and
converges on it — mounting what is new, unmounting what is gone, remounting what changed. That
design is deliberate: after a crash, a reboot or an rclone process dying, recovery is the same
operation as normal running, with no message history to lose.

**Credential caveat, stated plainly.** The service runs as LocalSystem and cannot read your
DPAPI `CurrentUser` blob, so its copy is re-protected at `LocalMachine` scope. Any process on the
machine can decrypt a LocalMachine blob; what actually keeps other users out is the file ACL, which
is restricted to SYSTEM and Administrators. If that trade-off is not acceptable for your threat
model, do not use service mode.

All config lives under `%LOCALAPPDATA%\HetznerDrive\`:

| File | Contents |
|------|----------|
| `mappings.json` | mappings and the last measured protocol (no secrets) |
| `settings.json` | app settings (cache defaults, benchmark size, start-at-login) |
| `credentials.dat` | passwords and keys, encrypted with Windows DPAPI (per-user) |
| `sync/` | per-mapping sync state for on-demand folders |
| `logs/` | daily activity logs (`hetznerdrive-YYYY-MM-DD.log`, 30-day retention) |

Service mode adds `%ProgramData%\HetznerDrive\` (ACL'd to SYSTEM + Administrators):

| File | Contents |
|------|----------|
| `service-mappings.json` | the mappings the service should mount (no secrets) |
| `service-credentials.dat` | their credentials, DPAPI **LocalMachine** scope |
| `logs/` | service logs; it also writes to the Windows Event Log |

## Prerequisites

- Windows 10/11 x64
- [WinFsp](https://winfsp.dev) — needed only for drive-letter mounts; the installer handles it
- .NET 8 SDK (to build). The published app is self-contained.

## Project layout

```
src/HetznerDrive.Core/       engine + persistence (protocol model, rclone config, benchmark, stores)
src/HetznerDrive.CloudFiles/ Files On-Demand provider + the four storage clients
src/HetznerDrive.App/        WPF tray app (Views, ViewModels, AppController)
src/HetznerDrive.Service/    Windows service host for mounts that must outlive a logon session
src/HetznerDrive.Tests/      xUnit tests
third_party/                 rclone.exe + WinFsp MSI (fetched, not committed)
installer/                   Inno Setup script
scripts/                     fetch-deps.ps1, publish.ps1, build-installer.ps1, release.ps1
```

## Build & run

```powershell
scripts\fetch-deps.ps1                  # download rclone.exe + winfsp.msi (once)
dotnet build HetznerDrive.slnx          # build everything
dotnet test  src\HetznerDrive.Tests     # run unit tests
dotnet run   --project src\HetznerDrive.App
```

`HetznerDrive.exe --selftest [report.txt]` constructs every window headlessly and exits 0 or 1.
XAML resource lookups resolve at load time, not compile time, so this catches the class of breakage
a green build cannot. Worth running in CI.

## Package an installer

```powershell
scripts\publish.ps1          # self-contained win-x64 publish
scripts\build-installer.ps1  # publish + compile installer (needs Inno Setup 6+)
```

## Usage

1. Launch HetznerDrive (it lives in the system tray).
2. **Add…** a mapping. For a Storage Box, enter the username (`u123456`, or a sub-account like
   `u123456-sub1`) and the password; the host and SMB share name are derived and shown as you type.
   For Object Storage, pick a location and enter the bucket, access key and secret key.
3. Leave the protocol on **Auto**, or hit **Test speeds now** to see the comparison first.
4. Click **Mount**.
5. Tick **Auto-mount at login** and enable **Start at login** in Settings to have mappings reappear
   automatically.

## Features

- **Two mapping modes:**
  - **Files On-Demand folder** (default) — a normal folder backed by the **Windows Cloud Files
    API**, just like OneDrive: files show in Explorer as placeholders, download only when opened,
    and support the native **Status** column/overlays and the **"Always keep on this device" /
    "Free up space"** right-click menu. Idle files are freed automatically. **Two-way sync**: local
    edits, new files, deletes and renames (with folder cascades) are pushed back; a periodic pull
    brings remote changes down, with change detection, last-writer-wins conflict logging, and
    retry/backoff. Works over all four protocols, and needs no WinFsp.
  - **Drive letter or folder mountpoint** — a virtual drive (e.g. `H:`) via rclone + WinFsp, or a
    folder such as `C:\HetznerDrive\Backups`.
- **Windows service mode** — see below.
- **Automatic protocol selection** — see above.
- **SSH key support** — use a key instead of the password for SFTP. The UI is explicit that SMB and
  WebDAV are password-only, so a key-only login restricts you to SFTP.
- **Sub-account aware** — a `-sub` account gets its own hostname and its own SMB share name, both
  derived correctly.
- **Cache tuning** — mode, max size, max age, and a configurable cache location.
- **Start at login** — per-user `HKCU\...\Run` key (no administrator rights).
- **Export / Import** — settings and mappings as JSON or XML. Secrets are never exported (they are
  DPAPI-encrypted and bound to the user+machine).
- **In-app updates** — checks GitHub Releases on startup and from **About → Check for updates**.
- **Explorer right-click** — a scoped **HetznerDrive** menu on items inside a mapping: *Copy share
  link* (Object Storage only — a Storage Box has no presigned-URL equivalent, and the app says so
  rather than copying something broken), *Open Hetzner console*, *Copy remote path* (rendered as
  `sftp://`, `\\host\share\`, `https://` or `s3://` to match the protocol in use).

## Notes

- Auto-mount runs in the interactive user session so drive letters are visible in Explorer; use
  service mode for mounts that must exist before sign-in.
- SMB must be enabled per Storage Box in the Hetzner console before Auto can measure it.
- Port 23 is the default for SFTP: Hetzner's port 22 accepts file transfers but has no interactive
  shell, which disables rclone's checksum support.
- `--vfs-cache-mode full` is the default for best app compatibility.

## Testing

See [docs/TESTING.md](docs/TESTING.md) for the manual smoke-test checklist.

## License

[MIT](LICENSE) © [RHC Solutions](https://rhcsolutions.com/).

## Code signing

Release binaries are signed via [SignPath.io](https://signpath.io) so Windows SmartScreen trusts
them. Signing runs in CI on tagged releases — see [docs/SIGNING.md](docs/SIGNING.md).
