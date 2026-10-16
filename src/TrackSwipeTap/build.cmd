@echo off
rem Builds TrackSwipeTap.dll (loaded into Explorer for the taskbar options) for ARM64 and x64 into
rem artifacts\tap\arm64 and artifacts\tap\x64. Needs the Visual Studio C++ build tools and the Windows 11 SDK.
rem build.ps1 runs this before publishing; each TrackSwipe.exe embeds the DLL for its own architecture.
setlocal
cd /d "%~dp0"
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
set "VCVARS=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat"
if not exist "%VCVARS%" set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" (set HOST=arm64) else (set HOST=amd64)
call :build arm64 || exit /b 1
call :build x64 || exit /b 1
exit /b 0

:build
setlocal
if "%1"=="x64" (set TARGET=amd64) else (set TARGET=%1)
if "%HOST%"=="%TARGET%" (set VCARCH=%TARGET%) else (set VCARCH=%HOST%_%TARGET%)
call "%VCVARS%" %VCARCH% >nul || exit /b 1
set OUT=..\..\artifacts\tap\%1
if not exist %OUT%\obj mkdir %OUT%\obj
cl /nologo /std:c++20 /EHsc /O2 /MT /W4 /permissive- /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /Fo%OUT%\obj\ ^
   /LD TrackSwipeTap.cpp /Fe:%OUT%\TrackSwipeTap.dll /link /DEF:TrackSwipeTap.def ole32.lib oleaut32.lib user32.lib gdi32.lib shell32.lib windowsapp.lib || exit /b 1
del %OUT%\*.exp %OUT%\*.lib 2>nul
exit /b 0
