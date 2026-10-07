; Instalador do AtreiaDPS (Inno Setup 6).
; Gerar: build-installer.ps1 (publica o app e compila este script).
;
; O Npcap NÃO vai junto: a licença gratuita dele proíbe redistribuição. O instalador verifica se ele existe e,
; se faltar, oferece abrir a página oficial de download.

#define AppName "AtreiaDPS"
#define AppVersion "1.0.2"
#define AppExe "AtreiaDPS.exe"
#define PublishDir "..\..\release\AtreiaDPS"

[Setup]
AppId={{6B2E4C1A-8F3D-4E7A-9C51-A7E0D2F4B913}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppName}
VersionInfoDescription={#AppName} - instalador
VersionInfoProductName={#AppName}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\..\release
OutputBaseFilename={#AppName}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Assets\AtreiaDPS.ico
CloseApplications=yes

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "LEIA-ME.txt,*.log,settings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "LEIA-ME.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o {#AppName} agora"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\settings.json"
Type: files; Name: "{app}\*.log"

[Code]
function NpcapInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\Npcap\wpcap.dll')) or FileExists(ExpandConstant('{sys}\wpcap.dll'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not NpcapInstalled()) then
  begin
    if MsgBox('O AtreiaDPS precisa do Npcap (driver gratuito de captura de rede, o mesmo do Wireshark), ' +
              'e ele não está instalado neste PC.' + #13#10#13#10 +
              'Abrir a página oficial de download agora?' + #13#10 +
              '(Instale com as opções padrão e depois abra o AtreiaDPS.)',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://npcap.com/#download', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;
