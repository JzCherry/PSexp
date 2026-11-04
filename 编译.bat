@echo off
chcp 65001 >nul
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

rem Pure build script: run the tests, then publish. Output goes to the system temp
rem folder. It never touches an installed file, never uploads, never obfuscates -
rem those belong to the author's private release pipeline.
rem
rem Keep this file 7-bit ASCII. cmd.exe resumes batch parsing from a stored byte
rem offset, and multi-byte characters make that offset drift, so a line can restart
rem mid-way and the rest of it runs as a command.

set "PROJECT=PSdodo\PSdodo.csproj"
set "TEST_PROJECT=tests\PSdodo.Tests\PSdodo.Tests.csproj"
set "BUILD_ROOT=%TEMP%\PSexp-compile"
set "ARTIFACTS_DIR=%BUILD_ROOT%\artifacts"
set "PUBLISH_DIR=%ARTIFACTS_DIR%\publish\PSdodo\release_win-x64"
set "TEST_EXE=%ARTIFACTS_DIR%\bin\PSdodo.Tests\release\PSdodo.Tests.exe"

echo =========================================
echo  PSexp build and test
echo =========================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 goto :dotnet_missing

for /f "tokens=1 delims=." %%V in ('dotnet --version') do set "DOTNET_MAJOR=%%V"
if not defined DOTNET_MAJOR goto :dotnet_missing
if %DOTNET_MAJOR% LSS 10 goto :dotnet_too_old

rem The output name comes from the project file, so it is not written down twice.
call :read_tag AssemblyName ASSEMBLY_NAME
if errorlevel 1 goto :csproj_unreadable
call :read_tag Version APP_VERSION
if errorlevel 1 goto :csproj_unreadable
set "APP_NAME=PSexp.exe"
echo       assembly: %ASSEMBLY_NAME%   finished file: %APP_NAME%

echo.
echo [1/2] Building and running the automated tests...
if exist "%BUILD_ROOT%" rmdir /s /q "%BUILD_ROOT%"
mkdir "%BUILD_ROOT%" >nul 2>nul
if errorlevel 1 goto :build_dir_failed

dotnet build "%TEST_PROJECT%" -c Release --nologo --artifacts-path "%ARTIFACTS_DIR%" -p:PublishSingleFile=false -p:EnableReleaseObfuscation=false -p:PublishTrimmed=false
if errorlevel 1 goto :test_failed
if not exist "%TEST_EXE%" goto :test_failed
"%TEST_EXE%"
if errorlevel 1 goto :test_failed

echo.
echo [2/2] Publishing the Windows x64 framework-dependent single file...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained false --nologo --artifacts-path "%ARTIFACTS_DIR%" -p:PublishDir="%PUBLISH_DIR%/" -p:EnableReleaseObfuscation=false -p:PublishTrimmed=false
if errorlevel 1 goto :publish_failed
if not exist "%PUBLISH_DIR%\%ASSEMBLY_NAME%.exe" goto :output_missing

rem The finished EXE goes next to this script, which is where people look for it.
copy /b /y "%PUBLISH_DIR%\%ASSEMBLY_NAME%.exe" "%~dp0%APP_NAME%" >nul
if errorlevel 1 goto :copy_failed
if not exist "%~dp0%APP_NAME%" goto :copy_failed
for %%F in ("%~dp0%APP_NAME%") do set "FINAL_SIZE=%%~zF"

echo.
echo =========================================
echo  output: %~dp0%APP_NAME%
echo  size: %FINAL_SIZE% bytes
echo  requires the .NET 10 x64 Runtime; runtime is not bundled
echo  intermediate files stay in the temp folder, delete them any time
echo =========================================
exit /b 0

:dotnet_missing
set "ERROR_MESSAGE=dotnet was not found. Building needs the .NET 10 SDK; running the output needs the .NET 10 x64 Runtime."
goto :failed

:dotnet_too_old
set "ERROR_MESSAGE=The dotnet SDK is too old. .NET 10 or newer is required."
goto :failed

:csproj_unreadable
set "ERROR_MESSAGE=Could not read the AssemblyName tag from %PROJECT%."
goto :failed

:build_dir_failed
set "ERROR_MESSAGE=Could not create %BUILD_ROOT% in the system temp folder."
goto :failed

:test_failed
set "ERROR_MESSAGE=The automated tests failed, or the test program was not produced."
goto :failed

:publish_failed
set "ERROR_MESSAGE=Publish failed. See the dotnet output above."
goto :failed

:output_missing
set "ERROR_MESSAGE=Publish did not produce %ASSEMBLY_NAME%.exe."
goto :failed

:copy_failed
set "ERROR_MESSAGE=Build succeeded but the EXE could not be copied next to this script. Close PSexp first, then run again."
goto :failed

:failed
echo.
echo [ERROR] %ERROR_MESSAGE%
if /i not "%PSexpNoPause%"=="1" pause
exit /b 1

rem Read one tag value from the project file. %1=tag name, %2=variable to set.
rem Works regardless of indentation; takes the last matching line.
:read_tag
set "_LINE="
for /f "delims=" %%L in ('findstr /i /c:"<%~1>" "%PROJECT%"') do set "_LINE=%%L"
if not defined _LINE exit /b 1
set "_VAL="
for /f "tokens=2 delims=>" %%V in ("%_LINE%") do set "_VAL=%%V"
if not defined _VAL exit /b 1
for /f "tokens=1 delims=<" %%W in ("%_VAL%") do set "%~2=%%W"
if not defined %~2 exit /b 1
exit /b 0
