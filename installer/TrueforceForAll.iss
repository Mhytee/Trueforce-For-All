; Inno Setup script for Trueforce For All.
;
; What it does:
;   1. Detects SimHub via its Inno-Setup registry key, with fallbacks.
;      No SimHub -> error dialog with download link, abort.
;   2. Drops 5 plugin files into SimHub's install folder.
;   3. If USBPcap isn't installed, runs the bundled USBPcapSetup silently,
;      then registers NonStandardHWIDs so the kernel filter sees USB 3.0
;      ports too (matters because users plug wheels into anything).
;
; Compile with: iscc.exe TrueforceForAll.iss
; (Inno Setup 6+ — older versions don't have RegGetSubkeyNames.)

#define AppName       "Trueforce For All"
#define AppPublisher  "Mhytee"
#define AppURL        "https://github.com/Mhytee/TrueforceForAll"
#define AppVersion    GetEnv("TRUEFORCEFORALL_VERSION")
#if AppVersion == ""
  #define AppVersion "0.1.0-dev"
#endif

; Layout: this .iss lives in `installer/`, plugin build outputs in `src/.../bin/Release/`,
; and the bundled USBPcap setup lives in `installer/vendor/`.
#define PluginBin      "..\src\TrueforceForAll.Plugin\bin\Release\net48"
#define HelperPublish  "..\src\TrueforceForAll.LoopbackHelper\bin\Release\net8.0\win-x64\publish"
#define UsbPcapSetup   "vendor\USBPcapSetup.exe"

[Setup]
; AppId is what registers our uninstall entry. Don't change once published.
AppId={{8A6F3B22-1D5E-4C9A-9F1B-7E3D5A2C4F11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases

; The "default" dir is irrelevant because GetSimHubInstallDir overrides it
; before the wizard runs. Keep it so Inno is happy.
DefaultDirName={code:GetSimHubInstallDir}
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no

OutputDir=output
OutputBaseFilename=TrueforceForAll-Setup
Compression=lzma2
SolidCompression=yes

; Plugin DLLs go under Program Files (x86); needs admin.
PrivilegesRequired=admin
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible

; LICENSE in repo root; license page lets users read GPL-2.0 before installing.
LicenseFile=..\LICENSE
SetupIconFile=

[Files]
; Our own files — always overwrite on upgrade.
Source: "{#PluginBin}\User.TrueforceForAll.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PluginBin}\TrueforceForAll.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#HelperPublish}\TrueforceForAll.LoopbackHelper.exe"; DestDir: "{app}"; Flags: ignoreversion

; Shared deps — install if missing, but never remove on uninstall (SimHub
; or other plugins may be using them).
Source: "{#PluginBin}\HidSharp.dll";          DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall
Source: "{#PluginBin}\NAudio.dll";            DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall
Source: "{#PluginBin}\NAudio.Core.dll";       DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall
Source: "{#PluginBin}\NAudio.Wasapi.dll";     DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall
Source: "{#PluginBin}\NAudio.WinMM.dll";      DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall
Source: "{#PluginBin}\NAudio.Asio.dll";       DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall
Source: "{#PluginBin}\NAudio.Midi.dll";       DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall
Source: "{#PluginBin}\NAudio.WinForms.dll";   DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall

; Bundled USBPcap installer (runs only if USBPcap not already installed).
Source: "{#UsbPcapSetup}"; DestDir: "{tmp}"; DestName: "USBPcapSetup.exe"; Flags: deleteafterinstall

; License redistribution for the bundled USBPcap.
Source: "USBPcap-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall

[Run]
; Chained USBPcap install — silent, only if not already present. /S is the
; NSIS silent-install flag USBPcap's own installer accepts.
Filename: "{tmp}\USBPcapSetup.exe"; \
    Parameters: "/S"; \
    StatusMsg: "Installing USBPcap (USB capture driver, required for FFB pass-through)..."; \
    Check: NeedsUSBPcap

; After USBPcap install, register NonStandardHWIDs so USB 3.0 ports are
; also captured. This is the documented USB 3.0 enable step from
; USBPcapCMD's --help. Idempotent and harmless to re-run.
Filename: "{code:GetUSBPcapCmdPath}"; \
    Parameters: "-I"; \
    StatusMsg: "Configuring USBPcap for USB 3.0 ports..."; \
    Flags: runhidden; \
    Check: HasUSBPcapCmd

[Code]
const
  // SimHub's Inno Setup AppId. Stable across SimHub versions.
  SimHubAppId      = '{019253FE-5A17-42BE-A6B8-D71A729FA5DE}_is1';
  UninstRoot32     = 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\';
  // USBPcap kernel-mode filter driver service.
  UsbPcapServiceKey = 'SYSTEM\CurrentControlSet\Services\USBPcap';

var
  CachedSimHubDir: string;

function StripTrailingSlash(S: string): string;
begin
  if (S <> '') and (S[Length(S)] = '\') then
    Result := Copy(S, 1, Length(S) - 1)
  else
    Result := S;
end;

function FindSimHubInRegistryByDisplayName: string;
var
  Names: TArrayOfString;
  i: Integer;
  DisplayName, InstallLoc: string;
begin
  Result := '';
  if not RegGetSubkeyNames(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall', Names) then
    Exit;
  for i := 0 to GetArrayLength(Names) - 1 do
  begin
    if RegQueryStringValue(HKLM, UninstRoot32 + Names[i], 'DisplayName', DisplayName) then
    begin
      // Match "SimHub version 9.11.11" but not "SimHub USBD480..." or
      // "Romainrob's SimHub Collection..." which have different layouts.
      if (Pos('SimHub version', DisplayName) = 1) then
      begin
        if RegQueryStringValue(HKLM, UninstRoot32 + Names[i], 'InstallLocation', InstallLoc) and
           (InstallLoc <> '') and DirExists(InstallLoc) then
        begin
          Result := StripTrailingSlash(InstallLoc);
          Exit;
        end;
      end;
    end;
  end;
end;

function FindSimHubInstallDir: string;
var
  InstallLoc, DefaultPath: string;
begin
  // 1. Direct lookup by stable AppId.
  if RegQueryStringValue(HKLM, UninstRoot32 + SimHubAppId, 'InstallLocation', InstallLoc) and
     (InstallLoc <> '') and DirExists(InstallLoc) then
  begin
    Result := StripTrailingSlash(InstallLoc);
    Exit;
  end;

  // 2. Scan uninstall keys for a "SimHub version ..." entry (handles future
  //    SimHub installer changes that reissue the AppId).
  Result := FindSimHubInRegistryByDisplayName;
  if Result <> '' then Exit;

  // 3. Default install path.
  DefaultPath := ExpandConstant('{commonpf32}') + '\SimHub';
  if FileExists(DefaultPath + '\SimHubWPF.exe') then
  begin
    Result := DefaultPath;
    Exit;
  end;

  Result := '';
end;

function GetSimHubInstallDir(Param: string): string;
begin
  if CachedSimHubDir = '' then
    CachedSimHubDir := FindSimHubInstallDir;
  Result := CachedSimHubDir;
end;

function InitializeSetup: Boolean;
var
  Dir: string;
  ResponseCode: Integer;
begin
  Dir := FindSimHubInstallDir;
  if Dir = '' then
  begin
    if MsgBox(
        'SimHub doesn''t appear to be installed on this PC.' + #13#10 + #13#10 +
        'Trueforce For All is a plugin for SimHub — it needs SimHub installed first.' + #13#10 + #13#10 +
        'Click OK to open the SimHub download page, then re-run this installer once SimHub is installed.',
        mbInformation, MB_OKCANCEL) = IDOK then
    begin
      ShellExec('open', 'https://www.simhubdash.com/', '', '', SW_SHOWNORMAL, ewNoWait, ResponseCode);
    end;
    Result := False;
    Exit;
  end;
  CachedSimHubDir := Dir;
  Result := True;
end;

function FindUsbPcapCmdPath: string;
var
  Candidates: array[0..1] of string;
  i: Integer;
begin
  Candidates[0] := ExpandConstant('{commonpf}')   + '\USBPcap\USBPcapCMD.exe';
  Candidates[1] := ExpandConstant('{commonpf32}') + '\USBPcap\USBPcapCMD.exe';
  for i := 0 to 1 do
    if FileExists(Candidates[i]) then
    begin
      Result := Candidates[i];
      Exit;
    end;
  Result := '';
end;

function IsUSBPcapInstalled: Boolean;
begin
  // Either the kernel service is registered or the user-mode CLI is on disk.
  Result := RegKeyExists(HKLM, UsbPcapServiceKey) or (FindUsbPcapCmdPath <> '');
end;

function NeedsUSBPcap: Boolean;
begin
  Result := not IsUSBPcapInstalled;
end;

function GetUSBPcapCmdPath(Param: string): string;
begin
  // Re-probe at run time in case USBPcap was just installed in the previous
  // [Run] step and only now exists on disk.
  Result := FindUsbPcapCmdPath;
end;

function HasUSBPcapCmd: Boolean;
begin
  // Used as the Check: for the -I post-install step. We only run -I if
  // USBPcapCMD.exe is on disk by now (either pre-existing or just installed).
  Result := FindUsbPcapCmdPath <> '';
end;
