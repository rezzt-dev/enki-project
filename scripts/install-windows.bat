@echo off
rem ============================================================================
rem  Enki - instalador para Windows
rem
rem  Compila Enki (Release, autocontenido) y lo instala o actualiza por usuario,
rem  reutilizando la instalacion existente para no duplicarla:
rem    1. La registrada por el instalador Inno Setup de Enki (HKCU o HKLM).
rem    2. %LOCALAPPDATA%\Programs\Enki  /  %ProgramFiles%\Enki  si ya existe.
rem    3. Si no hay ninguna: %LOCALAPPDATA%\Programs\Enki (sin permisos de admin).
rem
rem  Los datos del usuario (%AppData%\Enki) nunca se tocan.
rem
rem  Uso: scripts\install-windows.bat [opciones]
rem    /rid <RID>   Runtime de destino: win-x64 (defecto) o win-arm64
rem    /skipbuild    Reutiliza la ultima publicacion en publish\<RID>
rem    /uninstall    Elimina la aplicacion (conserva los datos del usuario)
rem    /verbose      Muestra la salida completa de dotnet
rem    /nocolor      Desactiva los colores
rem    /?            Muestra esta ayuda
rem ============================================================================
setlocal EnableExtensions EnableDelayedExpansion

rem --- Relanzar en un cmd hijo: asi "exit" nunca cierra la consola del usuario --
if not defined ENKI_INSTALL_CHILD (
  set "ENKI_INSTALL_CHILD=1"
  cmd /d /c ""%~f0" %*"
  set "RC=!errorlevel!"
  echo !CMDCMDLINE! | find /i " /c " >nul && (echo. & pause)
  exit /b !RC!
)

rem --- Valores por defecto ---------------------------------------------------
set "REPO=%~dp0.."
for %%I in ("%REPO%") do set "REPO=%%~fI"
set "CSPROJ=%REPO%\enki\src\enki\enki.csproj"
set "PROPS=%REPO%\enki\Directory.Build.props"
set "UNINST_KEY=Software\Microsoft\Windows\CurrentVersion\Uninstall\{6C6B4B2E-6E9B-4E3E-9B8A-6C6C7B7C8E3A}_is1"
set "RID=win-x64"
set "SKIP_BUILD=0" & set "UNINSTALL=0" & set "VERBOSE=0" & set "COLOR=1"
set "MIN_FREE_MB=1024"
set "STEP=0" & set "TOTAL=0"

rem --- Argumentos ------------------------------------------------------------
:args
if "%~1"=="" goto :args_done
if /i "%~1"=="/rid"       (set "RID=%~2" & shift & shift & goto :args)
if /i "%~1"=="/skipbuild" (set "SKIP_BUILD=1" & shift & goto :args)
if /i "%~1"=="/uninstall" (set "UNINSTALL=1" & shift & goto :args)
if /i "%~1"=="/verbose"   (set "VERBOSE=1" & shift & goto :args)
if /i "%~1"=="/nocolor"   (set "COLOR=0" & shift & goto :args)
if "%~1"=="/?"            goto :help
set "HC=0"
for /f "delims=" %%l in ('findstr /b /c:"rem " "%~f0"') do (
  set "l=%%l"
  if "!l:~4,4!"=="====" (set /a HC+=1) else if !HC! EQU 1 echo !l:~4!
)
exit /b 0

:banner "Instalacion - version %VERSION% - %RID%"

rem 1 -- Requisitos --------------------------------------------------------------
call :step "Comprobando requisitos"
where dotnet >nul 2>&1 || call :die "No se encuentra 'dotnet'. Instala el SDK de .NET 8 o superior: https://dotnet.microsoft.com/download"
set "DOTNET_VER=" & set "DOTNET_MAJOR=0"
for /f "delims=" %%v in ('dotnet --version 2^>nul') do if not defined DOTNET_VER set "DOTNET_VER=%%v"
for /f "delims=." %%m in ("!DOTNET_VER!") do set "DOTNET_MAJOR=%%m"
if !DOTNET_MAJOR! LSS 8 call :die "Se requiere el SDK de .NET 8 o superior (detectado: !DOTNET_VER!)."
call :ok "SDK de .NET !DOTNET_VER!"
if not exist "%CSPROJ%" call :die "No se encuentra el proyecto: %CSPROJ%"
call :ok "Proyecto localizado"

set "DRIVE=%INSTALL_DIR:~0,1%"
set "FREE_MB="
for /f %%f in ('powershell -NoProfile -Command "[math]::Floor((Get-PSDrive -Name '%DRIVE%').Free/1MB)" 2^>nul') do set "FREE_MB=%%f"
if defined FREE_MB (
  if !FREE_MB! LSS %MIN_FREE_MB% call :die "Espacio libre insuficiente en %DRIVE%: !FREE_MB! MB (se necesitan %MIN_FREE_MB% MB)."
  call :ok "Espacio libre en %DRIVE%: !FREE_MB! MB"
)

rem Comprobar permisos de escritura en el destino (Program Files requiere admin)
md "%INSTALL_DIR%" >nul 2>&1
(echo.>"%INSTALL_DIR%\.write-test") 2>nul || call :die "Sin permisos de escritura en %INSTALL_DIR%. Ejecuta el script como administrador."
del /q "%INSTALL_DIR%\.write-test" >nul 2>&1

set "UPDATING=0"
if exist "%INSTALL_DIR%\enki.exe" (
  set "UPDATING=1"
  if defined INNO_SCOPE (call :info "Instalacion existente (instalador, !INNO_SCOPE!) en %INSTALL_DIR% - se actualizara") else call :info "Instalacion existente en %INSTALL_DIR% - se actualizara"
) else (
  call :info "Instalacion nueva en %INSTALL_DIR%"
)

rem 2 -- Compilacion ------------------------------------------------------------
call :step "Compilando Enki"
if "%SKIP_BUILD%"=="1" (
  if not exist "%REPO%\publish\%RID%\enki.exe" call :die "/skipbuild: no existe %REPO%\publish\%RID%\enki.exe"
  robocopy "%REPO%\publish\%RID%" "%STAGE%" /E /NFL /NDL /NJH /NJS /NP >nul
  if errorlevel 8 call :die "No se pudo copiar la publicacion previa."
  call :ok "Reutilizando publish\%RID%"
) else (
  call :info "dotnet publish (%RID%, Release) - puede tardar unos minutos..."
  if "%VERBOSE%"=="1" (
    dotnet publish "%CSPROJ%" -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o "%STAGE%" -nologo -v:minimal
  ) else (
    dotnet publish "%CSPROJ%" -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o "%STAGE%" -nologo -v:minimal >"%LOG%" 2>&1
  )
  if errorlevel 1 call :die "La compilacion ha fallado." showlog
  call :elapsed
  call :ok "Compilacion completada (!ELAPSED!)"
)

rem 3 -- Verificacion ------------------------------------------------------------
call :step "Verificando el paquete"
if not exist "%STAGE%\enki.exe" call :die "La publicacion no contiene enki.exe."
call :ok "Ejecutable enki.exe"
if not exist "%STAGE%\polar.properties" call :die "Falta polar.properties (configura enki\local.properties o SUPABASE_URL/SUPABASE_ANON_KEY)."
call :ok "Configuracion publica de Polar"
if exist "%STAGE%\local.properties" del /q "%STAGE%\local.properties"
call :ok "Sin credenciales locales en el paquete"

rem 4 -- Instalacion --------------------------------------------------------------
call :step "Instalando"
call :stop_enki
if "%UPDATING%"=="1" (
  robocopy "%INSTALL_DIR%" "%BACKUP%" /MIR /NFL /NDL /NJH /NJS /NP >nul
  if errorlevel 8 call :die "No se pudo crear la copia de seguridad previa."
  call :ok "Copia de seguridad de la version anterior"
)
robocopy "%STAGE%" "%INSTALL_DIR%" /MIR /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
  call :warn "Fallo la copia; restaurando la version anterior..."
  if "%UPDATING%"=="1" robocopy "%BACKUP%" "%INSTALL_DIR%" /MIR /NFL /NDL /NJH /NJS /NP >nul
  call :die "No se pudo instalar en %INSTALL_DIR%."
)
call :ok "Archivos instalados en %INSTALL_DIR%"

rem 5 -- Integracion -------------------------------------------------------------
call :step "Integrando con Windows"
set "LNK_USER=%AppData%\Microsoft\Windows\Start Menu\Programs\Enki.lnk"
set "LNK_ALL=%ProgramData%\Microsoft\Windows\Start Menu\Programs\Enki\Enki.lnk"
if exist "%LNK_USER%" (
  call :info "Acceso directo del menu Inicio existente conservado"
) else if exist "%LNK_ALL%" (
  call :info "Acceso directo del menu Inicio existente conservado"
) else (
  set "LNK=%LNK_USER%"
  powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut($env:LNK); $s.TargetPath=$env:INSTALL_DIR+'\enki.exe'; $s.WorkingDirectory=$env:INSTALL_DIR; $s.Description='Enki'; $s.Save()" >nul 2>&1
  if exist "%LNK_USER%" (call :ok "Acceso directo creado en el menu Inicio") else call :warn "No se pudo crear el acceso directo del menu Inicio."
)

rem 6 -- Limpieza ----------------------------------------------------------------
call :step "Finalizando"
call :ok "Instalacion completada"
call :elapsed
echo.
echo %GRN%%B%OK Enki %VERSION% instalado correctamente%R% en !ELAPSED!
echo.
echo   %B%Ejecutar%R%    menu Inicio ^> Enki   %D%(o %INSTALL_DIR%\enki.exe)%R%
echo   %B%Instalado%R%   %INSTALL_DIR%
echo   %B%Datos%R%       %%AppData%%\Enki %D%(sin modificar)%R%
echo   %B%Quitar%R%      scripts\install-windows.bat /uninstall
echo.
call :cleanup
exit /b 0

rem ============================================================================
rem  DESINSTALACION
rem ============================================================================
:uninstall
set "TOTAL=2"
call :banner "Desinstalacion"
if not exist "%INSTALL_DIR%\enki.exe" call :die "No hay ninguna instalacion en %INSTALL_DIR%."
if defined INNO_SCOPE call :die "Esta instalacion la gestiona el instalador de Enki: desinstalala desde Configuracion, Aplicaciones."

call :step "Cerrando Enki"
call :stop_enki

call :step "Eliminando archivos"
rd /s /q "%INSTALL_DIR%" 2>nul
if exist "%INSTALL_DIR%" call :die "No se pudo eliminar %INSTALL_DIR% (hay archivos en uso)."
call :ok "Aplicacion: %INSTALL_DIR%"
if exist "%AppData%\Microsoft\Windows\Start Menu\Programs\Enki.lnk" (
  del /q "%AppData%\Microsoft\Windows\Start Menu\Programs\Enki.lnk"
  call :ok "Acceso directo del menu Inicio"
)
echo.
echo %GRN%%B%OK Enki se ha desinstalado.%R%
echo   Tus datos se conservan en %B%%%AppData%%\Enki%R%
echo.
call :cleanup
exit /b 0

rem ============================================================================
rem  SUBRUTINAS
rem ============================================================================
:help
for /f "skip=2 tokens=1,* delims=:" %%a in ('findstr /b /c:"rem " "%~f0"') do (
  if "%%a"=="rem" echo %%b
)
exit /b 0

:banner
echo.
echo   %B%Enki%R% - %~1
echo   %D%----------------------------------------------%R%
exit /b 0

:step
set /a STEP+=1
echo.
echo %CYN%%B%[%STEP%/%TOTAL%]%R% %B%%~1%R%
exit /b 0

:ok
echo   %GRN%OK%R% %~1
exit /b 0

:info
echo   %D%-%R%  %~1
exit /b 0

:warn
echo   %YEL%AVISO%R% %~1
exit /b 0

:die
echo.
echo %RED%%B%ERROR%R% %RED%%~1%R%
if /i "%~2"=="showlog" if exist "%LOG%" (
  echo.
  echo %B%Ultimas lineas del registro:%R%
  powershell -NoProfile -Command "Get-Content -LiteralPath $env:LOG -Tail 25 | ForEach-Object { '    ' + $_ }"
  copy /y "%LOG%" "%TEMP%\enki-install.log" >nul 2>&1 && echo. && echo   Registro completo: %TEMP%\enki-install.log
)
call :cleanup
exit 1

:stop_enki
tasklist /fi "imagename eq enki.exe" 2>nul | find /i "enki.exe" >nul
if errorlevel 1 (
  call :info "Enki no estaba en ejecucion"
  exit /b 0
)
call :warn "Enki esta en ejecucion; cerrandolo para continuar..."
taskkill /im enki.exe >nul 2>&1
timeout /t 3 /nobreak >nul
tasklist /fi "imagename eq enki.exe" 2>nul | find /i "enki.exe" >nul
if not errorlevel 1 taskkill /f /im enki.exe >nul 2>&1
call :ok "Enki cerrado"
exit /b 0

:now
set "t=%TIME: =0%"
set /a NOW=(1!t:~0,2!-100)*3600+(1!t:~3,2!-100)*60+(1!t:~6,2!-100)
exit /b 0

:elapsed
call :now
set /a SECS=NOW-T0
if !SECS! LSS 0 set /a SECS+=86400
set /a MM=SECS/60, SS=SECS%%60
if !SS! LSS 10 (set "SS=0!SS!")
set "ELAPSED=!MM!m !SS!s"
exit /b 0

:cleanup
if exist "%WORK%" rd /s /q "%WORK%" >nul 2>&1
exit /b 0
