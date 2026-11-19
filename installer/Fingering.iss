; Installer Fingering (Inno Setup 6). Developer: illustricious.
; Build:  powershell -File ..\publish.ps1 -Installer      (membuat exe slim + memanggil ISCC)
; Hasil:  ..\..\Fingering-Setup-<versi>.exe (di folder induk "SOURCE CODE !!")
; - Dipasang di Program Files (perlu admin), folder program HANYA berisi Fingering.exe (satu berkas, tanpa runtime di dalamnya).
; - Uninstaller disimpan di ProgramData supaya folder program tetap satu berkas.
; - .NET 10 Runtime dipasang otomatis (dari installer resmi Microsoft yang dibawa Setup) bila belum ada di PC.
; - Data TIDAK di folder program: database terenkripsi ada di %APPDATA%\illustricious\Fingering dan tidak ikut terhapus saat update.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PayloadExe
  #define PayloadExe "..\artifacts\publish-slim-win-x64\Fingering.exe"
#endif
#define MyAppName "Fingering"
#define MyAppPublisher "illustricious"
#define MyAppExe "Fingering.exe"

[Setup]
AppId={{6C3B9E52-8A1F-4D27-B5E4-F1A07D93C2B8}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (c) {#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallFilesDir={commonappdata}\illustricious\Fingering\uninstall
OutputDir=..\..
OutputBaseFilename=Fingering-Setup-{#MyAppVersion}
SetupIconFile=..\src\Fingering.App\Assets\fingering.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadExe}"; DestDir: "{app}"; DestName: "{#MyAppExe}"; Flags: ignoreversion
Source: "prereq\dotnet-runtime-win-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsDotNetRuntime

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\dotnet-runtime-win-x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing .NET 10 Runtime (one time)..."; Flags: waituntilterminated; Check: NeedsDotNetRuntime
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DataSubDir = '\illustricious\Fingering';
  FxKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App';

// Apakah .NET Runtime 10.x (x64) sudah terpasang? Dibaca dari registri yang ditulis installer runtime.
function NeedsDotNetRuntime: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := True;
  if RegGetValueNames(HKLM64, FxKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 3) = '10.' then
      begin
        Result := False;
        Exit;
      end;
end;

// Saat uninstall, tanya apakah data (database terenkripsi + vault + backup otomatis) ikut dihapus. Bawaan: TIDAK (data aman).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}') + DataSubDir;
    if DirExists(DataDir) then
    begin
      if MsgBox('Delete the Fingering data too?' + #13#10 + #13#10 + DataDir + #13#10 + #13#10 +
                'It holds the encrypted database, the key vault and automatic backups. Without them (and the recovery code) the data cannot be restored. ' +
                'Choose No to keep it (recommended if you plan to reinstall).',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
