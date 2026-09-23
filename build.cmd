@echo off
rem Builds Bestie.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No installs needed.
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo couldn't find the .NET Framework C# compiler. is this windows?
  exit /b 1
)

rem System.Speech (text-to-speech) isn't next to csc; it's in the WPF subfolder (or the GAC)
for %%d in ("%CSC%") do set SPEECH=%%~dpdWPF\System.Speech.dll
if not exist "%SPEECH%" set SPEECH=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/r:%SPEECH%" /r:System.Security.dll
set OPTS=/nologo /codepage:65001 /target:winexe /optimize+

rem a running Bestie.exe can't be overwritten; opening it for append fails if it's in use
if exist Bestie.exe (
  2>nul (call ) >> "Bestie.exe" || (
    echo Bestie is running from this folder. say goodbye to it first ^(right-click it, "say goodbye"^).
    exit /b 1
  )
)

"%CSC%" %OPTS% %REFS% /out:Bestie.exe src\*.cs || exit /b 1

rem second pass: bake a portrait of a creature into the exe icon
if not exist bestie.ico start "" /wait "%~dp0Bestie.exe" --icon "%~dp0bestie.ico"
if exist bestie.ico "%CSC%" %OPTS% %REFS% /win32icon:bestie.ico /out:Bestie.exe src\*.cs || exit /b 1

echo.
echo built Bestie.exe. double-click it to hatch your creature.
