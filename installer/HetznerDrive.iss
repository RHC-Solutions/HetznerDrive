; Inno Setup script for HetznerDrive.
; Build the app first with scripts\publish.ps1, then compile this with ISCC.exe
; (Inno Setup 6+, https://jrsoftware.org/isdl.php). scripts\build-installer.ps1 does both.

#define AppName "HetznerDrive"
#define AppVersion "0.3.0"
; Must match ServiceControl.ServiceName.
#define ServiceName "HetznerDrive"
#define AppPublisher "RHC Solutions"
#define AppPublisherUrl "https://rhcsolutions.com/"
#define PublishDir "..\src\HetznerDrive.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
#define WinFspMsi "..\third_party\winfsp\winfsp.msi"
#define AppIcon "..\src\HetznerDrive.App\Assets\hetznerdrive.ico"

[Setup]
AppId={{7A1E4C93-2D6B-4F58-9C31-HETZNERDRIVE1}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppPublisherUrl}
AppSupportURL={#AppPublisherUrl}
AppUpdatesURL=https://github.com/RHC-Solutions/HetznerDrive/releases
AppCopyright=© RHC Solutions. https://rhcsolutions.com/
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=.\output
OutputBaseFilename=HetznerDrive-Setup-{#AppVersion}
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\HetznerDrive.exe
Compression=lzma2
SolidCompression=yes
; Per-user install (no elevation) keeps rclone mounts in the user session; WinFsp still needs admin.
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#WinFspMsi}"; DestDir: "{tmp}"; DestName: "winfsp.msi"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\HetznerDrive.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\HetznerDrive.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
; Install WinFsp silently only if it is not already present (see IsWinFspInstalled below).
; WinFsp is only needed for drive-letter mounts; on-demand folders work without it.
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\winfsp.msi"" /qn /norestart"; \
  StatusMsg: "Installing WinFsp (required to mount drive letters)..."; \
  Flags: waituntilterminated; Check: not IsWinFspInstalled
; Offer to launch the app after install.
Filename: "{app}\HetznerDrive.exe"; Description: "Launch HetznerDrive"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; The Windows service is registered by the app on demand, not by this installer, but it points at
; an exe inside {app}. Leaving it behind after uninstall would give Windows a service that fails to
; start forever, so stop and delete it here. Both are no-ops when it was never installed; exit codes
; are ignored deliberately, since a per-user uninstall has no rights to touch services at all.
Filename: "sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"

[Code]
function IsWinFspInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\WinFsp')
         or RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\WinFsp');
end;
