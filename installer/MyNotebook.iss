; Inno Setup Script for My Notebook (WinUI 3)
; Self-contained, unpackaged win-x64. Installs the published folder.
; PrivilegesRequired=lowest so it can be installed into any test directory and
; run in PORTABLE mode (the app writes its Data\ folder next to the exe).

#define MyAppName "My Notebook"
#define MyAppVersion "1.7.1"
#define MyAppPublisher "Aung Ko Ko"
#define MyAppExeName "MyNotebook.App.exe"

; Paths are relative to this .iss file (E:\Notebook\installer\).
#define SourcePath  "..\src\MyNotebook.App\bin\publish\win-x64"
#define SeedDb      "..\db\notebook-seed.db"
#define SampleData  "..\data\attachments"
#define AppIcon     "..\src\MyNotebook.App\Assets\AppIcon.ico"

[Setup]
AppId={{B7A4F2D1-3C6E-4A91-9F2D-7E5C1A8B40C2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=Local-first notebook with OCR-searchable screenshot threads (WinUI 3)
DefaultDirName={autopf}\MyNotebook
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=Output
OutputBaseFilename=MyNotebook_Setup_v{#MyAppVersion}
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
VersionInfoVersion={#MyAppVersion}
; Always SHOW the "Select Destination" page (so you can still install a separate
; portable instance elsewhere), but PRE-FILL it with the last folder you installed
; to. UsePreviousAppDir=yes makes Inno remember the previous path per AppId.
UsePreviousAppDir=yes
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
; Entire self-contained publish output (exe + WinUI runtime + native DLLs + Assets).
Source: "{#SourcePath}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Sample data so the app opens pre-populated (10 notes + 10 OCR'd screenshots).
; onlyifdoesntexist => never clobbers real data on reinstall.
Source: "{#SeedDb}";      DestDir: "{app}\Data";                      DestName: "notebook.db"; Flags: onlyifdoesntexist
Source: "{#SampleData}\*"; DestDir: "{app}\Data\attachments";         Flags: onlyifdoesntexist recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";            Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}";  Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";      Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove the app folder on uninstall. NOTE: in portable mode this also removes the
; user's Data\ (it lives under {app}); acceptable for a test build.
Type: filesandordirs; Name: "{app}"
