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
#define AppURL        "https://github.com/Mhytee/Trueforce-For-All"
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

; Restart Manager safety net for the upgrade case where SimHub is holding
; User.TrueforceForAll.dll. Default on Inno Setup 6.x but set explicitly so
; the behavior is documented and survives Inno upgrades. The custom
; InitializeSetup check below catches the same case earlier with a
; friendlier dialog; this catches anything that slips through (e.g. SimHub
; launched between the check and the [Files] step).
CloseApplications=yes
RestartApplications=yes

; LICENSE in repo root; license page lets users read GPL-2.0 before installing.
LicenseFile=..\LICENSE
SetupIconFile=

; Remove pre-rebrand plugin files. Pre-1.0 builds shipped under the old
; "SimHubTrueforce" name; if a user upgrades from one of those, both old
; and new DLLs would coexist in SimHub's folder and SimHub would load both,
; showing two identically-functioning plugins in the UI. Strip the old set
; before [Files] copies the new one in.
[InstallDelete]
Type: files; Name: "{app}\User.SimHubTrueforce.dll"
Type: files; Name: "{app}\SimHubTrueforce.Core.dll"
Type: files; Name: "{app}\SimHubTrueforce.LoopbackHelper.exe"

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

; Bundled USBPcap installer. Kept on disk under {app}\vendor so the plugin
; can re-run it from its settings panel if USBPcap goes missing later (user
; uninstall, driver corruption). Also used by the chained [Run] install
; step below when USBPcap isn't already present at install time.
Source: "{#UsbPcapSetup}"; DestDir: "{app}\vendor"; DestName: "USBPcapSetup.exe"; Flags: ignoreversion

; PowerShell helper that registers our plugin in SimHub's
; PluginsActivation.json so it's enabled and pinned to the sidebar on
; first launch. Idempotent: a no-op when the user has already set their
; own choice (preserves disable/hide on upgrade installs).
Source: "RegisterPlugin.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall

; License redistribution for the bundled USBPcap.
Source: "USBPcap-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall

[Run]
; Chained USBPcap install — silent, only if not already present. /S is the
; NSIS silent-install flag USBPcap's own installer accepts.
Filename: "{app}\vendor\USBPcapSetup.exe"; \
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

; Auto-register the plugin in SimHub's PluginsActivation.json so a fresh
; install lands enabled + sidebar-visible without the user having to
; click "Add/remove feature". The PowerShell script is idempotent: it
; respects an existing entry, so a user who previously disabled or hid
; the plugin won't get their choice overridden on upgrade.
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\RegisterPlugin.ps1"" -ConfigPath ""{app}\PluginsData\PluginsActivation.json"""; \
    StatusMsg: "Registering Trueforce For All with SimHub..."; \
    Flags: runhidden waituntilterminated

; Postinstall checkbox on the Finished page: launch SimHub when the user
; clicks Finish (default checked).
;
; We launch via "cmd /c start" rather than executing SimHubWPF.exe
; directly. Direct CreateProcess from the elevated installer with
; runasoriginaluser was producing a headless SimHub process (alive in
; Task Manager, no visible window) in 0.1.0-localtest8. cmd /c start
; spawns through the shell's normal launch path which avoids that.
;
; If the user has SimHub's "Start minimized" preference enabled,
; SimHub will minimize itself to the taskbar after launch regardless
; of how we start it. An earlier revision shipped a PowerShell shim
; that polled for the window and called ShowWindow + SetForegroundWindow
; to overrule the minimize, but SimHub re-applies its preference late
; in WPF init and would re-minimize after our restore. The Finished
; page text (set in CurPageChanged below) tells the user to look at
; the taskbar in that case.
Filename: "{cmd}"; \
    Parameters: "/c start """" /D ""{app}"" ""{app}\SimHubWPF.exe"""; \
    Description: "Launch SimHub now"; \
    Flags: postinstall nowait skipifsilent runasoriginaluser

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

// True if a process named SimHubWPF.exe is currently running. Uses
// `tasklist` (always available since XP) rather than WMI to keep the check
// running on machines where the WMI service is disabled. The /NH flag
// strips the header row; /FI filters by image name.
function IsSimHubRunning: Boolean;
var
  ResultCode: Integer;
  TmpFile: string;
  Lines: TArrayOfString;
  i: Integer;
begin
  Result := False;
  TmpFile := ExpandConstant('{tmp}\tasklist_simhub.txt');
  if not Exec(ExpandConstant('{cmd}'),
              '/c tasklist /FI "IMAGENAME eq SimHubWPF.exe" /NH > "' + TmpFile + '"',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;
  if not LoadStringsFromFile(TmpFile, Lines) then Exit;
  for i := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Pos('SimHubWPF.exe', Lines[i]) > 0 then
    begin
      Result := True;
      Break;
    end;
  end;
  DeleteFile(TmpFile);
end;

// OnClick handler for the "Open download page" button in the
// SimHub-missing dialog. Top-level so the OnClick assignment can take
// its address. Doesn't set ModalResult — the dialog stays open so the
// user can install SimHub and then click Retry without re-starting.
procedure OpenSimHubPageBtnClick(Sender: TObject);
var
  ResponseCode: Integer;
begin
  ShellExec('open', 'https://www.simhubdash.com/', '', '', SW_SHOWNORMAL, ewNoWait, ResponseCode);
end;

// OnClick handler for the "Browse..." button. Opens a folder picker so a
// user with SimHub installed at a non-standard location (portable zip,
// custom drive, missing registry entries) can point us at it directly.
// Validates that SimHubWPF.exe is in the chosen folder before accepting.
// On success, sets CachedSimHubDir and closes the dialog with mrOk so
// InitializeSetup's detection loop skips the registry probes.
procedure BrowseSimHubBtnClick(Sender: TObject);
var
  Selected: string;
begin
  Selected := '';
  if BrowseForFolder('Select your SimHub install folder (the one containing SimHubWPF.exe)', Selected, False) then
  begin
    if FileExists(Selected + '\SimHubWPF.exe') then
    begin
      CachedSimHubDir := StripTrailingSlash(Selected);
      // Don't try to set ModalResult here. PascalScript in Inno 6.7+
      // doesn't expose TForm.ModalResult on a generic parent-walked
      // form reference, and the cast paths we tried fail to compile.
      // Instead, populating CachedSimHubDir is enough: InitializeSetup
      // breaks out of its detection loop on the first non-empty
      // CachedSimHubDir after the dialog returns, so the user clicks
      // Retry once and the install proceeds with the picked folder.
      MsgBox(
        'SimHub install folder accepted:' + #13#10 + Selected + #13#10 + #13#10 +
        'Click "Retry" to continue with this folder.',
        mbInformation, MB_OK);
    end else
    begin
      MsgBox(
        'SimHubWPF.exe was not found in that folder.' + #13#10 + #13#10 +
        'Please pick the folder that contains SimHubWPF.exe (the SimHub install root, not a subfolder).',
        mbError, MB_OK);
    end;
  end;
end;

// Modal "SimHub required" dialog with three labeled buttons. Returns
// mrOk when the user clicks Retry (re-check detection), mrCancel to
// abort the installer. "Open download page" doesn't close the dialog.
// We use a custom form because standard Windows MsgBox can't rename the
// Yes/No/Cancel buttons.
function ShowSimHubMissingDialog: Integer;
var
  Form: TSetupForm;
  Body: TNewStaticText;
  OpenBtn, BrowseBtn, RetryBtn, CancelBtn: TNewButton;
  ButtonW: Integer;
begin
  // Match the CodeClasses.iss example shipped with Inno: 4-arg
  // CreateCustomForm (width, height, flip-for-RTL, allow-resize).
  // Direct TSetupForm.Create(nil) misses the resource template and
  // crashes at runtime with "resource TSetupForm not found".
  Form := CreateCustomForm(ScaleX(580), ScaleY(240), False, True);
  try
    Form.Caption := 'Trueforce For All — SimHub required';

    Body := TNewStaticText.Create(Form);
    Body.Parent := Form;
    Body.Left := ScaleX(20);
    Body.Top := ScaleY(20);
    Body.Width := Form.ClientWidth - ScaleX(2 * 20);
    Body.Height := ScaleY(140);
    Body.AutoSize := False;
    Body.WordWrap := True;
    Body.Caption :=
      'SimHub doesn''t appear to be installed on this PC.' + #13#10 + #13#10 +
      'Trueforce For All is a SimHub plugin, so SimHub has to be installed first. ' +
      'Click "Open download page" to get SimHub from simhubdash.com, install it, ' +
      'then click "Retry" to continue.' + #13#10 + #13#10 +
      'If SimHub is already installed but in a non-standard location (portable ' +
      'install, custom folder), click "Browse..." to point us at it directly.';

    OpenBtn := TNewButton.Create(Form);
    OpenBtn.Parent := Form;
    OpenBtn.Caption := 'Open download page';
    OpenBtn.Top := Form.ClientHeight - ScaleY(23 + 10);
    OpenBtn.Height := ScaleY(23);
    OpenBtn.OnClick := @OpenSimHubPageBtnClick;

    BrowseBtn := TNewButton.Create(Form);
    BrowseBtn.Parent := Form;
    BrowseBtn.Caption := 'Browse...';
    BrowseBtn.Top := Form.ClientHeight - ScaleY(23 + 10);
    BrowseBtn.Height := ScaleY(23);
    BrowseBtn.OnClick := @BrowseSimHubBtnClick;

    RetryBtn := TNewButton.Create(Form);
    RetryBtn.Parent := Form;
    RetryBtn.Caption := 'Retry';
    RetryBtn.Top := Form.ClientHeight - ScaleY(23 + 10);
    RetryBtn.Height := ScaleY(23);
    RetryBtn.ModalResult := mrOk;
    RetryBtn.Default := True;

    CancelBtn := TNewButton.Create(Form);
    CancelBtn.Parent := Form;
    CancelBtn.Caption := 'Cancel';
    CancelBtn.Top := Form.ClientHeight - ScaleY(23 + 10);
    CancelBtn.Height := ScaleY(23);
    CancelBtn.ModalResult := mrCancel;
    CancelBtn.Cancel := True;

    // Width all four buttons to fit the longest caption. Keep this on one
    // line: Inno's tokenizer treats a leading "[" on a fresh line as a
    // section tag even inside [Code], so splitting the array literal at
    // the bracket triggers "Invalid section tag."
    ButtonW := Form.CalculateButtonWidth([OpenBtn.Caption, BrowseBtn.Caption, RetryBtn.Caption, CancelBtn.Caption]);
    OpenBtn.Width   := ButtonW;
    BrowseBtn.Width := ButtonW;
    RetryBtn.Width  := ButtonW;
    CancelBtn.Width := ButtonW;

    // Open + Browse on the left; Retry + Cancel right-aligned with a gap.
    OpenBtn.Left   := ScaleX(10);
    BrowseBtn.Left := OpenBtn.Left + ButtonW + ScaleX(6);
    CancelBtn.Left := Form.ClientWidth - ScaleX(10) - ButtonW;
    RetryBtn.Left  := CancelBtn.Left - ScaleX(6) - ButtonW;

    Result := Form.ShowModal();
  finally
    Form.Free();
  end;
end;

function InitializeSetup: Boolean;
var
  Dir: string;
begin
  // Find SimHub. If not present, loop with the custom "SimHub required"
  // dialog so the user can install SimHub and resume in-place instead of
  // restarting our installer.
  while True do
  begin
    Dir := FindSimHubInstallDir;
    if Dir <> '' then
    begin
      CachedSimHubDir := Dir;
      Break;
    end;
    if ShowSimHubMissingDialog = mrCancel then
    begin
      Result := False;
      Exit;
    end;
    // mrOk (Retry): the user might have used Browse... to populate
    // CachedSimHubDir manually. Accept that without re-probing so a
    // valid hand-picked folder isn't overridden by a failing registry
    // scan on the next loop iteration.
    if CachedSimHubDir <> '' then Break;
  end;

  // Block install while SimHub is running: it holds the plugin DLL open,
  // and pushing through anyway leaves either a stale install or a Restart
  // Manager prompt later in the wizard. Loop until SimHub is closed or
  // the user cancels. SimHub minimizes to the system tray by default —
  // closing the window doesn't actually exit it; the user has to
  // right-click the tray icon and pick Exit.
  while IsSimHubRunning do
  begin
    if MsgBox(
        'SimHub is currently running.' + #13#10 + #13#10 +
        'The plugin DLL is loaded into SimHub''s process and can''t be ' +
        'replaced while SimHub holds it open. Please close SimHub before ' +
        'continuing.' + #13#10 + #13#10 +
        'SimHub usually minimizes to the system tray when its window is ' +
        'closed. To fully exit, right-click the SimHub icon in the tray ' +
        '(near the clock) and pick Exit.' + #13#10 + #13#10 +
        'Click Retry once SimHub is closed, or Cancel to abort the install.',
        mbConfirmation, MB_RETRYCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  end;

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

// Override the wizard's Finished page so the user has a clear, plugin-
// specific next-steps list instead of a generic "Setup is complete".
// SimHub doesn't auto-enable plugins after a fresh DLL drop, and new
// users won't know to add it to the sidebar.
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
  begin
    WizardForm.FinishedHeadingLabel.Caption :=
      'Trueforce For All is installed';
    WizardForm.FinishedLabel.Caption :=
      'Make sure Logitech G HUB is closed before launching SimHub. G HUB claims the wheel''s HID interface and will block this plugin.' + #13#10 + #13#10 +
      'Trueforce For All has been enabled and pinned to SimHub''s sidebar automatically. Click "Launch SimHub now" below, then drive a supported game and tune via the plugin''s settings panel.' + #13#10 + #13#10 +
      'If you have "Start minimized" enabled in SimHub, the window will open minimized to the taskbar. Click the SimHub icon in the taskbar to bring it up.' + #13#10 + #13#10 +
      'If the plugin doesn''t appear in the sidebar, click "Add/remove feature" at the bottom left of the SimHub window and enable Trueforce For All from the list.';
  end;
end;
