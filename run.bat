@echo off
setlocal enabledelayedexpansion

REM ================================================================
REM  Kyobo PDF Dumper - APPDOMAIN_MANAGER launcher (.NET Framework)
REM ================================================================

REM Check for Administrator privileges (required to copy DLLs into Program Files)
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [*] Administrator privileges required to copy hook DLLs into Program Files.
    echo [*] Requesting elevation...
    powershell -NoProfile -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

set "KYOBO_INSTALL_DIR=C:\Program Files (x86)\Kyobobook\eLibrary"
set "KYOBO_EXE_NAME=KyoboBook.Ebook.ELibrary.exe"

set "KYOBO_DUMP_PATH=%~dp0dump"

set "HOOK_DLL_DIR=%~dp0KyoboDumper\bin\Release"
set "HOOK_DLL=!HOOK_DLL_DIR!\KyoboDumper.dll"

if not exist "!HOOK_DLL!" goto :no_hook
if not exist "!KYOBO_INSTALL_DIR!\!KYOBO_EXE_NAME!" goto :no_exe
if not exist "!KYOBO_DUMP_PATH!" mkdir "!KYOBO_DUMP_PATH!"

REM CLR looks for the AppDomainManager assembly in the app's base directory.
REM Copy our hook DLLs into the install dir.
copy /Y "!HOOK_DLL_DIR!\KyoboDumper.dll" "!KYOBO_INSTALL_DIR!" >nul
if errorlevel 1 (
    echo [!] Error: Failed to copy KyoboDumper.dll to !KYOBO_INSTALL_DIR!
    pause
    exit /b 1
)
if exist "!HOOK_DLL_DIR!\0Harmony.dll"              copy /Y "!HOOK_DLL_DIR!\0Harmony.dll"              "!KYOBO_INSTALL_DIR!" >nul
if exist "!HOOK_DLL_DIR!\MonoMod.Common.dll"        copy /Y "!HOOK_DLL_DIR!\MonoMod.Common.dll"        "!KYOBO_INSTALL_DIR!" >nul
if exist "!HOOK_DLL_DIR!\MonoMod.Utils.dll"         copy /Y "!HOOK_DLL_DIR!\MonoMod.Utils.dll"         "!KYOBO_INSTALL_DIR!" >nul
if exist "!HOOK_DLL_DIR!\MonoMod.RuntimeDetour.dll" copy /Y "!HOOK_DLL_DIR!\MonoMod.RuntimeDetour.dll" "!KYOBO_INSTALL_DIR!" >nul
if exist "!HOOK_DLL_DIR!\Mono.Cecil.dll"            copy /Y "!HOOK_DLL_DIR!\Mono.Cecil.dll"            "!KYOBO_INSTALL_DIR!" >nul

REM .NET Framework AppDomainManager injection (Framework equivalent of DOTNET_STARTUP_HOOKS).
set "APPDOMAIN_MANAGER_ASM=KyoboDumper, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
set "APPDOMAIN_MANAGER_TYPE=KyoboDumper.HookAppDomainManager"
set "COMPLUS_LoaderOptimization=1"

echo [+] Install dir : !KYOBO_INSTALL_DIR!
echo [+] EXE         : !KYOBO_EXE_NAME!
echo [+] Dump path   : !KYOBO_DUMP_PATH!
echo [+] Hook DLL    : (copied into install dir)
echo [+] ADM asm     : !APPDOMAIN_MANAGER_ASM!
echo [+] ADM type    : !APPDOMAIN_MANAGER_TYPE!
echo.
echo [*] Open a book in the viewer. Extraction is automatic.
echo.

pushd "!KYOBO_INSTALL_DIR!"
start "" "!KYOBO_EXE_NAME!"
popd
goto :end

:no_hook
echo [!] Hook DLL not found: !HOOK_DLL!
echo     Build first:
echo         dotnet build KyoboDumper\KyoboDumper.csproj -c Release
pause
exit /b 1

:no_exe
echo [!] EXE not found: !KYOBO_INSTALL_DIR!\!KYOBO_EXE_NAME!
echo     Edit KYOBO_INSTALL_DIR at top of this bat if install path differs.
pause
exit /b 1

:end
endlocal
