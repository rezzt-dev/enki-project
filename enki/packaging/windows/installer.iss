; Instalador de Enki para Windows (Inno Setup 6).
;
; Envuelve el resultado de `dotnet publish -r win-x64 --self-contained` (ver
; enki/packaging/windows/build-windows-package.ps1) en un instalador con acceso directo de menú
; inicio/escritorio, desinstalador registrado en "Aplicaciones y características" e icono ya tomado
; del propio enki.exe (enki.csproj fija <ApplicationIcon>, no hace falta repetirlo aquí).
;
; Compilar (ISCC viene preinstalado en los runners windows-latest de GitHub Actions, y con la
; instalación normal de Inno Setup en un PC de desarrollo):
;
;   iscc enki\packaging\windows\installer.iss ^
;     /DMyAppVersion=0.1.0 ^
;     /DSourceDir=publish\win-x64 ^
;     /DOutputDir=dist\windows
;
; MyAppVersion, SourceDir y OutputDir tienen valores por defecto pensados para ejecutar el script a
; mano desde la raíz del repo tras un `dotnet publish` normal; en CI se sobrescriben explícitamente
; para que la versión del instalador coincida siempre con el tag de la release.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\..\dist\windows"
#endif

#define MyAppName "Enki"
#define MyAppPublisher "rezztdev"
#define MyAppExeName "enki.exe"
; GUID fijo del producto Enki — no lo regeneres entre releases: Inno Setup lo usa para reconocer que
; una instalación futura es una actualización de esta y no una app distinta, y para que el
; desinstalador de una versión anterior se ofrezca a limpiarse solo.
#define MyAppId "{{6C6B4B2E-6E9B-4E3E-9B8A-6C6C7B7C8E3A}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/rezzt-dev/enki-project
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=enki-setup-{#MyAppVersion}-win-x64
SetupIconFile=..\..\src\enki\assets\icon\enki.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; "x64" en vez de "x64compatible": el identificador moderno solo lo entienden Inno Setup 6.3+, y no
; hay control sobre qué versión trae preinstalada el runner de CI en el momento de cada release.
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
LicenseFile=..\..\..\LICENSE
; Enki guarda sus datos en %AppData%\Enki (ver services/AppDataDirectory.cs) — la desinstalación no
; toca esa carpeta a propósito, igual que un desinstalador de Windows normal no borra los documentos
; del usuario; se deja como rastro deliberado, no como fuga.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Recursivo: incluye el ejecutable single-file, enki.pdb y las librerías nativas sueltas de
; Avalonia/Skia/HarfBuzz/PDFium (ver empaquetado-y-distribucion.md — no se pueden embeber en el
; single-file con las opciones actuales del proyecto, hay que distribuir la carpeta completa).
; local.properties (credenciales locales de Supabase del entorno de quien compila) se excluye a
; propósito: nunca debe viajar dentro de un instalador.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "local.properties"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
