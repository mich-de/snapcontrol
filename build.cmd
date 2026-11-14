@echo off
rem Builds SnapControl with the .NET Framework 4.8.1 C# compiler shipped with Windows (native ARM64 runtime).
rem Produces bin\SnapControl.exe and the installer bin\SnapControl-Setup.exe (which contains the app).
setlocal
set "CSC=%WINDIR%\Microsoft.NET\FrameworkArm64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%~dp0bin" mkdir "%~dp0bin"
rem Optional: factory color profiles in a local "profiles" folder (git-ignored, they are Lenovo/X-Rite files)
rem are built into the executable, so SnapControl can reinstall them even without X-Rite or its backup folder.
set "RES="
for %%f in ("%~dp0profiles\TPLCD_*.icm" "%~dp0profiles\TPLCD_*_Quality.txt") do call set RES=%%RES%% /resource:"%%~ff",profile/%%~nxf
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /langversion:5 /codepage:65001 ^
  /win32manifest:"%~dp0src\app.manifest" /win32icon:"%~dp0src\app.ico" ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  /reference:System.Management.dll /reference:System.Core.dll /reference:Microsoft.CSharp.dll /reference:System.Web.Extensions.dll ^
  %RES% /out:"%~dp0bin\SnapControl.exe" "%~dp0src\SnapControl.cs" "%~dp0src\Ui.cs" "%~dp0src\Device.cs" "%~dp0src\WifiGuard.cs"
if errorlevel 1 exit /b %errorlevel%
copy /y "%~dp0src\SnapControl.exe.config" "%~dp0bin\" >nul
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /langversion:5 /codepage:65001 ^
  /win32manifest:"%~dp0src\setup.manifest" /win32icon:"%~dp0src\app.ico" ^
  /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Core.dll /reference:Microsoft.CSharp.dll ^
  /resource:"%~dp0bin\SnapControl.exe",SnapControl.exe /resource:"%~dp0src\SnapControl.exe.config",SnapControl.exe.config ^
  /out:"%~dp0bin\SnapControl-Setup.exe" "%~dp0src\Setup.cs"
exit /b %errorlevel%
