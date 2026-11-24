; Inno Setup Script for SignerBridge Desktop (Windows 10+ x64, per-user, no admin required)
#define MyAppName "SignerBridge"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "MPFN / SignerBridge"
#define MyAppExeName "SignerBridge.Host.exe"
#define PublishDir "..\publish\win-x64"

[Setup]
AppId={{8F6C2B4E-7D1A-4A5B-9E3C-2D1F8A9C01E2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish\installer
OutputBaseFilename=SignerBridge-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "autostart"; Description: "Iniciar SignerBridge automáticamente con Windows"; GroupDescription: "Opciones:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
; Auto-start with Windows (optional task)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SignerBridge"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: autostart; Flags: uninsdeletevalue
; Custom URI protocol signer-bridge://
Root: HKCU; Subkey: "Software\Classes\signer-bridge"; ValueType: string; ValueName: ""; ValueData: "URL:SignerBridge Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\signer-bridge"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\signer-bridge\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\signer-bridge\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
; Enable Java Access Bridge for ReFirma PDF embedded JRE if present
Filename: "{userprofile}\AppRENIEC\common\jre\jre-8\bin\jabswitch.exe"; Parameters: "-enable"; Flags: runhidden skipifdoesntexist
Filename: "{app}\{#MyAppExeName}"; Description: "Iniciar {#MyAppName} ahora"; Flags: nowait postinstall skipifsilent
