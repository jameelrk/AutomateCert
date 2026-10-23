; AutomateCert Agent - installer (Inno Setup 6)
; Interactive: run and paste the console URL + token from Servers > Connect servers.
; Silent (GPO / SCCM / Intune / RMM): AutomateCertAgent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /SERVERURL="https://automatecert.contoso.com" /TOKEN="<fleet token>" [/PIN=1]
#ifndef AppPublisher
  #define AppPublisher "AutomateCert contributors"
#endif
#ifndef AppPublisherURL
  #define AppPublisherURL "https://example.com"
#endif
#ifndef AppVersion
  #define AppVersion "1.5.0"
#endif

[Setup]
AppId={{FF882669-CAD0-4C72-9714-61F3E4004E59}
AppName=AutomateCert Agent
AppVersion={#AppVersion}
AppVerName=AutomateCert Agent {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppPublisherURL}
DefaultDirName={commonpf64}\AutomateCert\Agent
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
OutputBaseFilename=AutomateCertAgent-Setup-{#AppVersion}
SetupIconFile=..\src\AutomateCert.Server\wwwroot\img\favicon.ico
UninstallDisplayIcon={app}\AutomateCert.Agent.exe
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
UninstallDisplayName=AutomateCert Agent
SetupLogging=yes
CloseApplications=no

[Files]
Source: "..\artifacts\agent\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop AutomateCertAgent"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "{sys}\sc.exe"; Parameters: "delete AutomateCertAgent"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAgent"

[Code]
var
  ConnPage: TInputQueryWizardPage;
  PinBox: TNewCheckBox;
  ConfigOk: Boolean;

function ParamOr(Name, Def: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '|' + Def + '}');
end;

function ValidUrl(S: String): Boolean;
begin
  Result := (Pos('https://', LowerCase(Trim(S))) = 1) and (Length(Trim(S)) > 10);
end;

function ValidToken(S: String): Boolean;
begin
  Result := Length(Trim(S)) = 64;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if WizardSilent and (not ValidUrl(ParamOr('SERVERURL', '')) or not ValidToken(ParamOr('TOKEN', ''))) then
  begin
    Log('Silent install requires /SERVERURL="https://..." and /TOKEN="<64-character token>".');
    SuppressibleMsgBox('Silent install requires /SERVERURL="https://..." and /TOKEN="<64-character token>".', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure InitializeWizard;
begin
  ConnPage := CreateInputQueryPage(wpWelcome, 'Connect to AutomateCert', 'Console address and enrollment token', 'Copy both values from the AutomateCert console: Servers > Connect servers.');
  ConnPage.Add('AutomateCert console URL (https://...):', False);
  ConnPage.Add('Enrollment token:', False);
  ConnPage.Values[0] := ParamOr('SERVERURL', '');
  ConnPage.Values[1] := ParamOr('TOKEN', '');
  PinBox := TNewCheckBox.Create(ConnPage);
  PinBox.Parent := ConnPage.Surface;
  PinBox.Top := ConnPage.Edits[1].Top + ConnPage.Edits[1].Height + ScaleY(18);
  PinBox.Width := ConnPage.SurfaceWidth;
  PinBox.Height := ScaleY(20);
  PinBox.Caption := 'Pin the console certificate (use when servers do not trust it, e.g. self-signed)';
  PinBox.Checked := ParamOr('PIN', '0') = '1';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = ConnPage.ID then
  begin
    if not ValidUrl(ConnPage.Values[0]) then begin MsgBox('Enter the console URL, starting with https://', mbError, MB_OK); Result := False; exit; end;
    if not ValidToken(ConnPage.Values[1]) then begin MsgBox('The enrollment token must be 64 characters. Copy it again from the console.', mbError, MB_OK); Result := False; exit; end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var RC: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop AutomateCertAgent', '', SW_HIDE, ewWaitUntilTerminated, RC);
  Sleep(3000);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Params: String; RC: Integer;
begin
  if CurStep <> ssPostInstall then exit;
  WizardForm.StatusLabel.Caption := 'Enrolling this server with AutomateCert...';
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Install-AutomateCertAgent.ps1') + '"' +
    ' -ServerUrl "' + Trim(ConnPage.Values[0]) + '" -EnrollmentToken "' + Trim(ConnPage.Values[1]) + '"';
  if PinBox.Checked then Params := Params + ' -PinServerCertificate';
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, RC) and (RC = 0) then ConfigOk := True
  else
  begin
    Log('Agent enrollment failed, exit code ' + IntToStr(RC));
    SuppressibleMsgBox('The agent was installed but did not enroll (code ' + IntToStr(RC) + ').' + #13#10 +
      'Check that the console URL is reachable from this server and the token is valid, then run Setup again.' + #13#10 +
      'Details: Event Viewer > Windows Logs > Application (source AutomateCert.Agent).', mbError, MB_OK, IDOK);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and ConfigOk then WizardForm.FinishedLabel.Caption := 'This server is connected to AutomateCert and appears under Servers in the console.';
end;
