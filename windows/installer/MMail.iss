; MMail for Windows installer (Inno Setup 6).
; Built by the Windows workflow:
;   ISCC /DAppVersion=0.1.42 /DSourceDir=..\out\MMail /DOutputDir=..\out MMail.iss
; Installs for the current user only (no admin prompt), in %LOCALAPPDATA%\Programs\MMail.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\MMail"
#endif
#ifndef OutputDir
  #define OutputDir "..\out"
#endif

[Setup]
AppId={{7C1E5B2A-4D8F-4A6B-9E3C-2F5D8A1B6C40}
AppName=MMail
AppVersion={#AppVersion}
AppPublisher=MMail
AppPublisherURL=https://github.com/brunmul/MMail
DefaultDirName={autopf}\MMail
DefaultGroupName=MMail
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=MMail-Setup
SetupIconFile=..\src\MMail.App\Assets\MMail.ico
UninstallDisplayIcon={app}\MMail.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Updating while MMail runs: close it first, reopen it after.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; A new version replaces the whole app folder (mail and settings live elsewhere, in %LOCALAPPDATA%\MMail).
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{autoprograms}\MMail"; Filename: "{app}\MMail.exe"
Name: "{autodesktop}\MMail"; Filename: "{app}\MMail.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MMail.exe"; Description: "{cm:LaunchProgram,MMail}"; Flags: nowait postinstall skipifsilent
