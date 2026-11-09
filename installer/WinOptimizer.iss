; WinOptimizer — Inno Setup 6
;
; Установщик кладёт приложение и, если нужно, сам ставит .NET 8 Desktop Runtime
; (файл redist\windowsdesktop-runtime-win-x64.exe; в CI он скачивается автоматически).
;
; Локальная сборка: build-setup.bat (скачивает runtime, публикует приложение, собирает установщик).
; Вручную: ISCC /DMyAppVersion=0.4.52 installer\WinOptimizer.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.4.0"
#endif
#define MyAppName "WinOptimizer"
#define MyAppPublisher "WinOptimizer Contributors"
#define MyAppURL "https://github.com/reikidod/WinOptimazer"
#define MyAppExeName "WinOptimizer.exe"
#define MyAppId "{{B7E3A1C2-9F4D-4E8A-A6B1-2C5D8E9F0A11}"
#define RuntimeFile "windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=LICENSE.txt
OutputDir=..\dist
; Имя без версии: встроенное обновление ищет в релизе файл с «Setup» в названии
OutputBaseFilename=WinOptimizer-Setup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
WizardImageFile=wizard-large.bmp,wizard-large@2x.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small@2x.bmp
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Установщик ждёт закрытия работающего приложения (имя мьютекса — как в App.xaml.cs)
AppMutex=Global\WinOptimizer.SingleInstance
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoCopyright=Copyright (c) 2026 {#MyAppPublisher}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.InstallingRuntime=Устанавливаю компонент .NET 8 Desktop Runtime (нужен для работы программы)…
english.InstallingRuntime=Installing .NET 8 Desktop Runtime (required by the app)...
russian.KeepData=Оставить настройки и резервные копии твиков (папка %1)?%n%nЕсли позже переустановите программу, настройки сохранятся.
english.KeepData=Keep settings and tweak backups (folder %1)?%n%nThey will be reused if you reinstall later.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
; Runtime распаковывается во временную папку и удаляется после установки; нужен только если его нет в системе
Source: "..\redist\{#RuntimeFile}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsDotNet

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\{#RuntimeFile}"; Parameters: "/install /quiet /norestart"; StatusMsg: "{cm:InstallingRuntime}"; Flags: waituntilterminated; Check: NeedsDotNet
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser
; Тихое обновление из самого приложения: после установки запускаем его снова
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: ShouldRelaunch

[UninstallRun]
; Автозапуск, созданный в настройках приложения, не должен остаться после удаления
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""WinOptimizer"" /f"; Flags: runhidden; RunOnceId: "RemoveAutostartTask"

[Code]
function NeedsDotNet: Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := True;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Versions) then
    for I := 0 to GetArrayLength(Versions) - 1 do
      if Pos('8.', Versions[I]) = 1 then
      begin
        Result := False;
        Exit;
      end;
end;

function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:NORELAUNCH|0}') = '0');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\WinOptimizer');
    if DirExists(DataDir) and (not UninstallSilent) then
      if MsgBox(FmtMessage(CustomMessage('KeepData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDNO then
        DelTree(DataDir, True, True, True);
  end;
end;
