@echo off
setlocal enabledelayedexpansion
REM ============================================================================
REM  Run the chibil test suite inside the MSVC developer environment.
REM
REM  The tests shell out to cl.exe / link.exe (MSVC /clr) for the interop and
REM  end-to-end scenarios, so they only pass when the VC tools are on PATH.
REM  This locates Visual Studio via vswhere, initializes vcvars for the given
REM  architecture (default x64), then runs `dotnet test`.
REM
REM  Usage:
REM    run-tests.cmd                 - run all tests (x64)
REM    run-tests.cmd x86             - run all tests (x86)
REM    run-tests.cmd x64 --filter X  - pass extra args through to dotnet test
REM ============================================================================

set ARCH=x64
if /I "%~1"=="x64" ( set ARCH=x64& shift )
if /I "%~1"=="x86" ( set ARCH=x86& shift )
if /I "%~1"=="arm64" ( set ARCH=arm64& shift )

REM Accumulate any remaining args to forward to `dotnet test`.
REM (%* ignores shift, so build the passthrough list explicitly.)
set "REST="
:argloop
if "%~1"=="" goto argdone
set "REST=!REST! %1"
shift
goto argloop
:argdone

set "vswherePath=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%vswherePath%" (
    echo ERROR: vswhere.exe not found. Install Visual Studio with C++ tools.
    exit /b 1
)

set toolsComponent=Microsoft.VisualStudio.Component.VC.Tools.x86.x64
if /I "%ARCH%"=="arm64" set toolsComponent=Microsoft.VisualStudio.Component.VC.Tools.ARM64

for /f "tokens=*" %%i in (
    '"%vswherePath%" -latest -prerelease -products * -requires %toolsComponent% -property installationPath'
) do set "vsBase=%%i"

if "%vsBase%"=="" (
    echo ERROR: No Visual Studio install with the required VC tools was found.
    exit /b 1
)

call "%vsBase%\VC\Auxiliary\Build\vcvarsall.bat" %ARCH% >nul 2>&1
if errorlevel 1 (
    echo ERROR: vcvarsall.bat failed for %ARCH%.
    exit /b 1
)

where cl >nul 2>&1 || ( echo ERROR: cl.exe not on PATH after vcvars.& exit /b 1 )
where link >nul 2>&1 || ( echo ERROR: link.exe not on PATH after vcvars.& exit /b 1 )

dotnet test "%~dp0tests\Chibil.Tests\Chibil.Tests.csproj" --nologo%REST%
exit /b %errorlevel%
