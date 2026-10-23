; AutomateCert Server - customer installer (Inno Setup 6)
; Build: installer\build-installers.ps1
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
AppId={{5D6464F0-C93B-4C54-9368-7D8DCA41B8F4}
AppName=AutomateCert Server
AppVersion={#AppVersion}
AppVerName=AutomateCert Server {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppPublisherURL}
AppCopyright=Copyright (C) {#AppPublisher}
DefaultDirName={commonpf64}\AutomateCert\Server
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
OutputBaseFilename=AutomateCertServer-Setup-{#AppVersion}
SetupIconFile=..\src\AutomateCert.Server\wwwroot\img\favicon.ico
UninstallDisplayIcon={app}\AutomateCert.Server.exe
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
UninstallDisplayName=AutomateCert Server
SetupLogging=yes
CloseApplications=no

[Files]
Source: "..\artifacts\server\*"; DestDir: "{tmp}\pkg\server"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "..\deploy\server\Install-AutomateCertServer.ps1"; DestDir: "{tmp}\pkg"; Flags: ignoreversion deleteafterinstall
Source: "..\deploy\server\Uninstall-AutomateCertServer.ps1"; DestDir: "{app}"; Flags: ignoreversion

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-AutomateCertServer.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAutomateCertServer"

[Code]
var
  HostPage, SqlPage, AdminPage, SmtpPage: TInputQueryWizardPage;
  OptPage: TInputOptionWizardPage;
  IsUpgrade, ConfigOk: Boolean;
  ConsoleUrl: String;

function JsonEscape(S: String): String;
var I: Integer; C: Char;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if C = '\' then Result := Result + '\\'
    else if C = '"' then Result := Result + '\"'
    else if Ord(C) < 32 then Result := Result + ' '
    else Result := Result + C;
  end;
end;

function J(Name, Value: String): String;
begin
  Result := '"' + Name + '":"' + JsonEscape(Value) + '"';
end;

function B(Name: String; Value: Boolean): String;
begin
  if Value then Result := '"' + Name + '":true' else Result := '"' + Name + '":false';
end;

procedure InitializeWizard;
begin
  IsUpgrade := FileExists(ExpandConstant('{commonpf64}\AutomateCert\Server\appsettings.Production.json'));

  HostPage := CreateInputQueryPage(wpWelcome, 'Console address', 'How will users and servers reach AutomateCert?',
    'Enter the DNS name for this server. AutomateCert uses a matching certificate from the Local Computer store or the thumbprint you enter; ' +
    'otherwise it creates a self-signed certificate (agents pin it). AutomateCert can renew its own certificate later.');
  HostPage.Add('Host name (e.g. automatecert.contoso.com):', False);
  HostPage.Add('HTTPS port:', False);
  HostPage.Add('Certificate thumbprint (optional):', False);
  HostPage.Values[0] := LowerCase(GetComputerNameString);
  HostPage.Values[1] := '443';

  OptPage := CreateInputOptionPage(HostPage.ID, 'Options', 'What should Setup take care of?', 'Recommended options are pre-selected.', False, False);
  OptPage.Add('Install SQL Server 2022 Express if no SQL Server is reachable (internet required, about 10 minutes)');
  OptPage.Add('Install the AutomateCert agent on this server (manages the console certificate and local services)');
  OptPage.Add('Open the HTTPS port in Windows Firewall (Domain and Private profiles)');
  OptPage.Add('Require two-step verification for every user');
  OptPage.Values[0] := True; OptPage.Values[1] := True; OptPage.Values[2] := True; OptPage.Values[3] := False;

  SqlPage := CreateInputQueryPage(OptPage.ID, 'Database', 'Microsoft SQL Server',
    'Leave "." to use (or install) SQL Server on this machine. Leave the SQL login empty for Windows authentication (the account running Setup must be sysadmin).');
  SqlPage.Add('SQL Server instance (e.g. SQL01 or .\SQLEXPRESS):', False);
  SqlPage.Add('Database name:', False);
  SqlPage.Add('SQL login (optional - SQL authentication):', False);
  SqlPage.Add('SQL password:', True);
  SqlPage.Values[0] := '.';
  SqlPage.Values[1] := 'AutomateCert';

  AdminPage := CreateInputQueryPage(SqlPage.ID, 'Organization and administrator', 'The first account that signs in',
    'Free Let''s Encrypt issuers are added automatically. Add more users and roles after signing in.');
  AdminPage.Add('Organization name:', False);
  AdminPage.Add('Administrator name:', False);
  AdminPage.Add('Administrator email:', False);
  AdminPage.Add('Password (at least 12 characters):', True);
  AdminPage.Add('Confirm password:', True);

  SmtpPage := CreateInputQueryPage(AdminPage.ID, 'Email alerts (optional)', 'SMTP server for expiry alerts and sign-in codes',
    'Leave the server empty to configure later. Provide a user unless your mail relay accepts this server''s IP without login.');
  SmtpPage.Add('SMTP server:', False);
  SmtpPage.Add('Port (587 STARTTLS, 465 SSL):', False);
  SmtpPage.Add('From address:', False);
  SmtpPage.Add('SMTP user (optional):', False);
  SmtpPage.Add('SMTP password:', True);
  SmtpPage.Add('Alert recipients (comma-separated, optional):', False);
  SmtpPage.Values[1] := '587';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := IsUpgrade and ((PageID = HostPage.ID) or (PageID = OptPage.ID) or (PageID = SqlPage.ID) or (PageID = AdminPage.ID) or (PageID = SmtpPage.ID));
end;

function Fail(Msg: String): Boolean;
begin
  MsgBox(Msg, mbError, MB_OK);
  Result := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var P: Integer;
begin
  Result := True;
  if CurPageID = HostPage.ID then
  begin
    if (Trim(HostPage.Values[0]) = '') or (Pos('.', HostPage.Values[0]) = 0) then begin Result := Fail('Enter a fully qualified host name, for example automatecert.contoso.com.'); exit; end;
    P := StrToIntDef(Trim(HostPage.Values[1]), 0);
    if (P < 1) or (P > 65535) then begin Result := Fail('Enter a valid HTTPS port (1-65535).'); exit; end;
  end
  else if CurPageID = SqlPage.ID then
  begin
    if Trim(SqlPage.Values[0]) = '' then begin Result := Fail('Enter the SQL Server instance.'); exit; end;
    if Trim(SqlPage.Values[1]) = '' then begin Result := Fail('Enter a database name.'); exit; end;
    if (Trim(SqlPage.Values[2]) <> '') and (SqlPage.Values[3] = '') then begin Result := Fail('Enter the SQL password.'); exit; end;
  end
  else if CurPageID = AdminPage.ID then
  begin
    if Trim(AdminPage.Values[0]) = '' then begin Result := Fail('Enter the organization name.'); exit; end;
    if Pos('@', AdminPage.Values[2]) = 0 then begin Result := Fail('Enter a valid administrator email.'); exit; end;
    if Length(AdminPage.Values[3]) < 12 then begin Result := Fail('The password must be at least 12 characters.'); exit; end;
    if AdminPage.Values[3] <> AdminPage.Values[4] then begin Result := Fail('The passwords do not match.'); exit; end;
  end
  else if CurPageID = SmtpPage.ID then
  begin
    if (Trim(SmtpPage.Values[0]) <> '') and (Pos('@', SmtpPage.Values[2]) = 0) then begin Result := Fail('Enter the From address for alert emails.'); exit; end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Lines: TArrayOfString;
  F, Params, Auth, Tls, Port: String;
  RC: Integer;
begin
  if CurStep <> ssPostInstall then exit;
  F := ExpandConstant('{tmp}\answers.json');
  SetArrayLength(Lines, 1);
  if IsUpgrade then
    Lines[0] := '{"Upgrade":true}'
  else
  begin
    if Trim(SqlPage.Values[2]) = '' then Auth := 'Windows' else Auth := 'Sql';
    if Trim(SmtpPage.Values[1]) = '465' then Tls := 'false' else Tls := 'true';
    Port := Trim(SmtpPage.Values[1]);
    if Port = '' then Port := '587';
    Lines[0] := '{' +
      J('PublicHostName', Trim(HostPage.Values[0])) + ',' + J('HttpsPort', Trim(HostPage.Values[1])) + ',' +
      J('CertificateThumbprint', Trim(HostPage.Values[2])) + ',"SelfSignedCertificate":true,' +
      B('InstallSqlExpress', OptPage.Values[0]) + ',' + B('InstallLocalAgent', OptPage.Values[1]) + ',' +
      B('OpenFirewall', OptPage.Values[2]) + ',' + B('RequireMfa', OptPage.Values[3]) + ',' +
      J('SqlServer', Trim(SqlPage.Values[0])) + ',' + J('SqlDatabase', Trim(SqlPage.Values[1])) + ',' + J('SqlAuth', Auth) + ',' +
      J('SqlUser', Trim(SqlPage.Values[2])) + ',' + J('SqlPassword', SqlPage.Values[3]) + ',' +
      J('OrganizationName', Trim(AdminPage.Values[0])) + ',' + J('AdminName', Trim(AdminPage.Values[1])) + ',' +
      J('AdminEmail', Trim(AdminPage.Values[2])) + ',' + J('AdminPassword', AdminPage.Values[3]) + ',' +
      J('SmtpHost', Trim(SmtpPage.Values[0])) + ',' + J('SmtpPort', Port) + ',"SmtpStartTls":' + Tls + ',' +
      J('SmtpFrom', Trim(SmtpPage.Values[2])) + ',' + J('SmtpUser', Trim(SmtpPage.Values[3])) + ',' +
      J('SmtpPassword', SmtpPage.Values[4]) + ',' + J('AlertEmails', Trim(SmtpPage.Values[5])) + '}';
    if Trim(HostPage.Values[1]) = '443' then ConsoleUrl := 'https://' + Trim(HostPage.Values[0])
    else ConsoleUrl := 'https://' + Trim(HostPage.Values[0]) + ':' + Trim(HostPage.Values[1]);
  end;
  SaveStringsToUTF8File(F, Lines, False);
  WizardForm.StatusLabel.Caption := 'Configuring AutomateCert: SQL, service, certificate, database, administrator and agent...';
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\pkg\Install-AutomateCertServer.ps1') + '" -AnswerFile "' + F + '"';
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, ExpandConstant('{tmp}\pkg'), SW_HIDE, ewWaitUntilTerminated, RC) and (RC = 0) then
    ConfigOk := True
  else
  begin
    DeleteFile(F);
    SuppressibleMsgBox('AutomateCert files were installed, but configuration did not complete (code ' + IntToStr(RC) + ').' + #13#10#13#10 +
      'See the newest log in C:\ProgramData\AutomateCert\Server\install-*.log, fix the cause (usually SQL access or the certificate) and run Setup again.', mbError, MB_OK, IDOK);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
  begin
    if not ConfigOk then WizardForm.FinishedLabel.Caption := 'Configuration did not complete. Check the log in C:\ProgramData\AutomateCert\Server and run Setup again.'
    else if IsUpgrade then WizardForm.FinishedLabel.Caption := 'AutomateCert was upgraded. Configuration, keys and data were kept.'
    else WizardForm.FinishedLabel.Caption := 'AutomateCert is running.' + #13#10#13#10 + 'Open ' + ConsoleUrl + ' and sign in as ' + Trim(AdminPage.Values[2]) + '.' + #13#10#13#10 +
      'Free Let''s Encrypt is ready. Next: add your DNS provider and connect your servers.';
  end;
end;
