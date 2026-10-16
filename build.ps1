# Builds TrackSwipe as two standalone native executables in dist\: TrackSwipe-arm64.exe and TrackSwipe-x64.exe.
# Needs the .NET 8 SDK and the Visual Studio C++ build tools (for the native linker), and runs the tests first.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# The native AOT linker locates the C++ tools through vswhere.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $installer) { $env:PATH = "$installer;$env:PATH" }

dotnet test tests\TrackSwipe.Tests -c Release --nologo -v q
if ($LASTEXITCODE) { throw 'Tests failed.' }

# The taskbar options' DLL, embedded in each executable.
cmd /c src\TrackSwipeTap\build.cmd
if ($LASTEXITCODE) { throw 'Building TrackSwipeTap.dll failed.' }

New-Item -ItemType Directory -Force dist | Out-Null
foreach ($arch in 'arm64', 'x64') {
    $out = "artifacts\$arch"
    dotnet publish src\TrackSwipe -c Release -r "win-$arch" -o $out --nologo -v q
    if ($LASTEXITCODE) { throw "Publishing $arch failed." }
    Copy-Item "$out\TrackSwipe.exe" "dist\TrackSwipe-$arch.exe" -Force
}
Get-ChildItem dist\*.exe | ForEach-Object { '{0}  {1:0.0} MB' -f $_.Name, ($_.Length / 1MB) }
