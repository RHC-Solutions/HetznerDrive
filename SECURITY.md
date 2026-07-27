# Security Policy

## Supported versions

HetznerDrive is developed on a single release line. Only the latest release receives fixes; please
update before reporting an issue.

| Version | Supported |
| ------- | --------- |
| latest release | :white_check_mark: |
| anything older | :x: |

## Reporting a vulnerability

Report privately via [GitHub Security Advisories](https://github.com/RHC-Solutions/HetznerDrive/security/advisories/new),
or by email to security@rhcsolutions.com. Please do not open a public issue for a vulnerability.

Include what you did, what happened, and the version (**About → Version**). A proof of concept
helps. You can expect an acknowledgement within a few business days and an assessment shortly after;
if a fix is warranted it ships in the next release and the advisory credits you unless you'd rather
it didn't.

## How secrets are handled

Worth knowing before you report — these are deliberate design points, not oversights:

- **Storage Box passwords, SSH key passphrases and S3 secret keys** are stored in
  `%LOCALAPPDATA%\HetznerDrive\credentials.dat`, encrypted with Windows DPAPI at `CurrentUser` scope
  plus an app-specific entropy value. They are decryptable only by the same Windows account on the
  same machine, which is also why they are excluded from settings export.
- **Secrets reach rclone through environment variables** on the child process, never through a
  command line (visible in the process list) and never through an `rclone.conf` on disk.
- **`RcloneObscure` is obfuscation, not encryption.** rclone requires password config values in this
  format and the key is compiled into the public rclone binary, so anyone holding the obscured value
  can recover the password. The security boundary is DPAPI on the credential store; the obscure step
  exists solely because rclone will not accept plaintext. Reports that the transform is reversible
  describe expected behaviour.
- **Share links** (Object Storage only) are presigned S3 URLs valid for 7 days. Anyone holding the
  URL can read that object for its lifetime — that is what makes it shareable.
- **SMB connections** are registered with `WNetAddConnection2`. Windows permits only one credential
  set per server, so if a session to the same host already exists it is reused rather than replaced.

## Out of scope

- Vulnerabilities in rclone, WinFsp, or the Hetzner services themselves — please report those
  upstream.
- The reversibility of rclone's `obscure` encoding (see above).
- Local attacks by a user who already has the Windows account's privileges; DPAPI at `CurrentUser`
  scope does not defend against code running as that user.
