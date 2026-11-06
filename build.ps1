# Builds TrackSwipe as two standalone native executables in dist\: TrackSwipe-arm64.exe and TrackSwipe-x64.exe,
# and its optional dock as dist\TrackSwipe-Dock-arm64.zip and dist\TrackSwipe-Dock-x64.zip (release assets that
# TrackSwipe downloads the first time the dock is turned on; each TrackSwipe carries the SHA-256 of its own).
# Needs the .NET 8 SDK and the Visual Studio C++ build tools (for the native linker); the dock needs the .NET 10 SDK.
# Runs the tests first.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# The native AOT linker locates the C++ tools through vswhere.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $installer) { $env:PATH = "$installer;$env:PATH" }

dotnet test tests\TrackSwipe.Tests -c Release --nologo -v q
if ($LASTEXITCODE) { throw 'Tests failed.' }
dotnet test dock\tests\Anchor.Tests -p:Platform=ARM64 --nologo -v q
if ($LASTEXITCODE) { throw 'Dock tests failed.' }

# The taskbar options' DLL, embedded in each executable.
cmd /c src\TrackSwipeTap\build.cmd
if ($LASTEXITCODE) { throw 'Building TrackSwipeTap.dll failed.' }

New-Item -ItemType Directory -Force dist | Out-Null

# The dock first: TrackSwipe is built with each archive's hash and size.
$dock = @{}
foreach ($arch in 'arm64', 'x64') {
    $platform = if ($arch -eq 'arm64') { 'ARM64' } else { 'x64' }
    $out = "artifacts\dock-$arch"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    dotnet publish dock\src\Anchor\Anchor.csproj -c Release "-p:Platform=$platform" --self-contained true -o $out --nologo -v q
    if ($LASTEXITCODE) { throw "Publishing the $arch dock failed." }
    $zip = "dist\TrackSwipe-Dock-$arch.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    # Everything but the debugging symbols (Anchor.pdb, about 70 MB): they stay in artifacts\ for reading crash dumps,
    # and aren't needed to run the dock.
    $symbols = "artifacts\dock-$arch-symbols"
    if (Test-Path $symbols) { Remove-Item -Recurse -Force $symbols }
    New-Item -ItemType Directory -Force $symbols | Out-Null
    Get-ChildItem $out -Filter *.pdb | Move-Item -Destination $symbols
    Compress-Archive -Path "$out\*" -DestinationPath $zip -CompressionLevel Optimal
    $dock[$arch] = @{ Hash = (Get-FileHash $zip -Algorithm SHA256).Hash; Bytes = (Get-Item $zip).Length }
}

foreach ($arch in 'arm64', 'x64') {
    $out = "artifacts\$arch"
    dotnet publish src\TrackSwipe -c Release -r "win-$arch" -o $out --nologo -v q `
        "-p:DockSha256Arm64=$($dock['arm64'].Hash)" "-p:DockSha256X64=$($dock['x64'].Hash)" `
        "-p:DockBytesArm64=$($dock['arm64'].Bytes)" "-p:DockBytesX64=$($dock['x64'].Bytes)"
    if ($LASTEXITCODE) { throw "Publishing $arch failed." }
    Copy-Item "$out\TrackSwipe.exe" "dist\TrackSwipe-$arch.exe" -Force
}
Get-ChildItem dist\TrackSwipe-*.exe, dist\TrackSwipe-Dock-*.zip | ForEach-Object { '{0}  {1:0.0} MB' -f $_.Name, ($_.Length / 1MB) }
