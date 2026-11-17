@echo off
setlocal DisableDelayedExpansion

set "TOOLS=%~dp0build\windows-tools"
if exist "%TOOLS%\dotnet\dotnet.exe" (
    set "DOTNET_ROOT=%TOOLS%\dotnet"
    set "PATH=%TOOLS%\dotnet;%PATH%"
    if not defined DOTNET_CLI_HOME set "DOTNET_CLI_HOME=%TOOLS%\cli-home"
    if not defined NUGET_PACKAGES set "NUGET_PACKAGES=%TOOLS%\nuget"
)

if not defined GODOT set "GODOT=%TOOLS%\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"

powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0run-windows.ps1" %*
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
    echo.
    echo World could not start. See the error above and the Windows setup in README.md.
    pause
)
exit /b %RESULT%
