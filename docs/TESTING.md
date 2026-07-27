# Manual smoke tests

`dotnet test src\HetznerDrive.Tests` covers the pure logic — protocol resolution, rclone argument
construction, the `obscure` encoding, path/key mapping and the sync reconciler. Everything below
needs a real Hetzner account and cannot be automated here.

You need a Storage Box for §1–§5 and an Object Storage bucket for §6. Both are cheap to create and
delete for a test pass.

## 1. Protocol selection

| # | Step | Expected |
|---|------|----------|
| 1.1 | Add a Storage Box mapping, leave protocol on **Auto**, click **Test speeds now** | Each of SFTP/SMB/WebDAV reports either a MiB/s figure or a reason it was skipped; a winner is named |
| 1.2 | Disable SMB for the box in the Hetzner console, re-run | SMB reports `port 445 unreachable`; the other two still measure |
| 1.3 | Block port 445 outbound in Windows Firewall, re-run | Same as 1.2 — proves the probe distinguishes reachability from speed |
| 1.4 | Enter an SSH key and clear the password, re-run | SMB and WebDAV report *needs the account password*; SFTP still measures |
| 1.5 | Mount, unmount, mount again | The second mount does **not** re-run the benchmark (check the activity log) |
| 1.6 | Tick **Re-measure on every mount** in Settings, remount | The benchmark runs again |
| 1.7 | Watch the Storage Box during a run | No `.hetznerdrive-speedtest-*.tmp` file is left behind, including after a failed protocol |

## 2. Per-protocol correctness

Run §3 and §4 once for **each** of SFTP, SMB and WebDAV by pinning the protocol explicitly.

| # | Step | Expected |
|---|------|----------|
| 2.1 | Mount with protocol pinned to SFTP | Mounts; activity log shows `Mounting over Sftp` |
| 2.2 | Same for SMB | Mounts (requires SMB enabled in the console) |
| 2.3 | Same for WebDAV | Mounts |
| 2.4 | Sub-account (`u123456-sub1`) over SMB | Connects to the share named after the sub-account, not `backup` |
| 2.5 | Set the SSH port to 22 and mount over SFTP | Transfers work; checksum warnings in the log are expected (no shell on 22) |

## 3. Drive-letter mode

| # | Step | Expected |
|---|------|----------|
| 3.1 | Mount a drive-letter mapping | `H:` appears in Explorer as a network drive |
| 3.2 | Copy a large file (≥1 GB) in | Completes; the file is visible in the Hetzner console/over SSH |
| 3.3 | Copy a folder of ~1000 small files in | Completes without connection errors — this is what the SFTP concurrency clamp protects |
| 3.4 | Delete a file | Deleted remotely; no `$RECYCLE.BIN` folder appears on the remote |
| 3.5 | Create then delete an empty folder | Both reflected remotely |
| 3.6 | Rename a folder with contents | Renamed remotely, contents intact |
| 3.7 | Unmount | Drive letter disappears; rclone process is gone from Task Manager |

## 4. Files On-Demand folder

| # | Step | Expected |
|---|------|----------|
| 4.1 | Mount an on-demand mapping | Folder appears with placeholders; a pinned entry appears in Explorer's sidebar |
| 4.2 | Check the **Status** column | Cloud-only icons on unopened files |
| 4.3 | Open a file | Hydrates and opens; status becomes locally-available |
| 4.4 | Right-click → **Always keep on this device** | Stays hydrated after a dehydration sweep |
| 4.5 | Right-click → **Free up space** | Returns to cloud-only, disk space released |
| 4.6 | Edit a hydrated file and save | Uploaded within ~2 s (see the activity log) |
| 4.7 | Drop in a new folder of files | All uploaded; no upload loop afterwards |
| 4.8 | Delete a folder locally | Cascade-deleted remotely |
| 4.9 | Rename a folder locally | Cascade-renamed remotely |
| 4.10 | Add a file remotely, wait 5 min | A placeholder appears locally |
| 4.11 | Delete a file remotely (leave it cloud-only locally), wait 5 min | The placeholder disappears |
| 4.12 | Edit a file both locally and remotely | Local wins; the activity log records a conflict |
| 4.13 | Delete the mapping | Sidebar entry is removed |

## 5. Explorer integration

| # | Step | Expected |
|---|------|----------|
| 5.1 | Right-click a file inside a mapping | The **HetznerDrive** submenu appears |
| 5.2 | Right-click a file outside any mapping | The submenu does **not** appear |
| 5.3 | **Copy remote path** on a Storage Box file | `sftp://`, `\\host\share\` or `https://` matching the live protocol |
| 5.4 | **Copy share link** on a Storage Box file | Explains that share links are an Object Storage feature; nothing bogus is copied |

## 6. Object Storage

| # | Step | Expected |
|---|------|----------|
| 6.1 | Add an Object Storage mapping | Protocol UI is hidden; bucket/location/keys are shown instead |
| 6.2 | Mount and copy files both ways | Works |
| 6.3 | **Copy share link** on a file | A presigned URL is copied and opens in a browser |
| 6.4 | Try to select SFTP for it | Not offered in the UI |

## 7. Windows service mode

Needs an administrator account. Every step below asks for UAC approval.

| # | Step | Expected |
|---|------|----------|
| 7.1 | Edit a drive mapping, set **Attach as** to a folder mountpoint, tick *Mount with the Windows service*, Save | Saves; the mapping's Location column shows the folder path |
| 7.2 | Try the same on a Files On-Demand mapping | Rejected in the dialog — Cloud Files needs an interactive session |
| 7.3 | Tick the service option while **Attach as** is a drive letter | Rejected — a letter mounted from session 0 is invisible |
| 7.4 | Point the mountpoint at a folder that already exists | Rejected — WinFsp creates the folder itself |
| 7.5 | **Settings → Windows service → Install**, approve UAC | Status becomes *Installed and running*; the folder appears and is browsable |
| 7.6 | Press **Mount** on that mapping in the main window | Refused with an explanation — the service owns it |
| 7.7 | Sign out and back in | The mountpoint was there throughout (check from another session or via a scheduled task) |
| 7.8 | Reboot without signing in, then check from a remote session or a startup script | The mountpoint exists before any interactive logon |
| 7.9 | Edit the mapping's cache settings, then **Apply changes** | Service restarts and remounts; activity visible in the service log |
| 7.10 | Edit the mapping but *don't* apply | Settings shows *Changes are pending* |
| 7.11 | `sc.exe query HetznerDrive` | Running, `START_TYPE : 2 AUTO_START`, `SERVICE_START_NAME : LocalSystem` |
| 7.12 | Inspect `%ProgramData%\HetznerDrive` permissions | Only SYSTEM and Administrators |
| 7.13 | As a *standard* user, try to read `service-credentials.dat` | Access denied |
| 7.14 | Search the file for the password | Not present in plaintext |
| 7.15 | Kill `rclone.exe` for a serviced mount | Remounted automatically (immediately via the restart budget, or within 5 minutes by the sweep) |
| 7.16 | Stop the service | Mountpoints disappear |
| 7.17 | **Uninstall** from Settings | Service gone from `sc query`; `%ProgramData%\HetznerDrive` config files removed; mapping still listed in the app |
| 7.18 | Approve a UAC prompt as a *different* administrator | Clear message that credentials could not be read, rather than a silently empty publish |
| 7.19 | Install the service, then uninstall the app itself | The service is removed too (the installer's UninstallRun) |

## 8. App lifecycle

| # | Step | Expected |
|---|------|----------|
| 8.1 | Enable **Start at login**, sign out and back in | App starts in the tray; auto-mount mappings mount without a window |
| 8.2 | Close the window | Hides to tray |
| 8.3 | Tray → Exit | All mappings unmount; process exits |
| 8.4 | Export settings, delete a mapping, import | Mapping returns; credentials must be re-entered |
| 8.5 | Inspect `%LOCALAPPDATA%\HetznerDrive\mappings.json` | Contains no password, key or secret |
| 8.6 | Inspect `credentials.dat` in a hex editor | No plaintext secret |
| 8.7 | Run **About → Check for updates** | Reports up to date, or offers the newer release |
