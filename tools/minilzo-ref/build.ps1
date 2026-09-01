# Builds minilzo_ref.exe, the upstream reference harness used by the interop tests.
# Compiles the UNMODIFIED upstream minilzo.c vendored under third_party/minilzo together
# with minilzo_ref.c, using the MSVC toolchain located via vswhere.
#
# On Linux and macOS use build.sh instead.

$ErrorActionPreference = 'Stop'

$upstream = (Resolve-Path (Join-Path $PSScriptRoot '..\..\third_party\minilzo')).Path
$binDir = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $binDir | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found; install Visual Studio with the C++ workload.' }
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'No Visual Studio installation with the C++ toolchain was found.' }

$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
$src1 = Join-Path $upstream 'minilzo.c'
$src2 = Join-Path $PSScriptRoot 'minilzo_ref.c'
$exe = Join-Path $binDir 'minilzo_ref.exe'

# Run cl inside the vcvars64 environment.
$command = "call `"$vcvars`" >nul && cl /nologo /O2 /W3 /I`"$upstream`" `"$src1`" `"$src2`" /Fe`"$exe`" /Fo`"$binDir\\`""
cmd /c $command
if ($LASTEXITCODE -ne 0) { throw "cl failed with exit code $LASTEXITCODE" }
Write-Host "Built $exe"
