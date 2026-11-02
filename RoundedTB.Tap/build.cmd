@echo off
rem Builds RoundedTB.Tap.dll for one architecture: build.cmd arm64|x64 [outdir]
setlocal
set ARCH=%1
set OUT=%~2
if "%OUT%"=="" set OUT=%~dp0bin\%ARCH%
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set VS=%%i
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" (set HOST=arm64) else (set HOST=amd64)
if /i "%HOST%"=="%ARCH%" (set TARGET=%ARCH%) else (set TARGET=%HOST%_%ARCH%)
if /i "%HOST%"=="amd64" if /i "%ARCH%"=="x64" set TARGET=amd64
call "%VS%\VC\Auxiliary\Build\vcvarsall.bat" %TARGET% >nul || exit /b 1
if not exist "%OUT%" mkdir "%OUT%"
cl /nologo /LD /O2 /EHsc /std:c++20 /permissive- /W3 /DNOMINMAX /DWIN32_LEAN_AND_MEAN "%~dp0Tap.cpp" /Fo"%OUT%\\" /Fe"%OUT%\RoundedTB.Tap.dll" /link /DEF:"%~dp0Tap.def" WindowsApp.lib ole32.lib oleaut32.lib user32.lib || exit /b 1
del "%OUT%\RoundedTB.Tap.lib" "%OUT%\RoundedTB.Tap.exp" "%OUT%\*.obj" 2>nul
