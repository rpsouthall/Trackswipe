<#
.SYNOPSIS
    Publishes Anchor and runs the UI smoke tests against the published exe.

.DESCRIPTION
    The UI tests (tests\Anchor.UITests) launch the real Anchor.exe and drive it through UI
    Automation, so they need a built copy of the app and an interactive desktop — a signed-in
    session with a visible desktop. They will not pass over a locked screen, in a headless CI
    agent, or while the screen saver is up.

    Each test runs Anchor against a throwaway ANCHOR_DATA_DIR under %Temp%, so your own dock's
    items, position and settings are never touched.

    The tests skip themselves (rather than fail) if ANCHOR_UITESTS/ANCHOR_EXE aren't set, which
    is why they are driven from here instead of a plain `dotnet test`.

.PARAMETER Configuration
    Build configuration to publish and test. Defaults to Debug.

.PARAMETER Platform
    x64 (default) or ARM64. Must match the machine you're running on — these launch the app.

.PARAMETER Filter
    Optional xUnit filter expression, e.g. -Filter "FullyQualifiedName~Settings".

.EXAMPLE
    pwsh scripts/run-ui-tests.ps1
    pwsh scripts/run-ui-tests.ps1 -Configuration Release -Filter "FullyQualifiedName~gear"
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [string]$Filter
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "src\Anchor\Anchor.csproj"
$testProject = Join-Path $repoRoot "tests\Anchor.UITests\Anchor.UITests.csproj"
$publishDir = Join-Path $repoRoot "publish\Anchor-uitests-$Platform"

Write-Host "Publishing Anchor ($Configuration, $Platform) for the UI tests..."
dotnet publish $appProject -c $Configuration -p:Platform=$Platform --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $publishDir "Anchor.exe"
if (-not (Test-Path $exe)) { throw "Anchor.exe not found at $exe" }

$env:ANCHOR_UITESTS = "1"
$env:ANCHOR_EXE = $exe

Write-Host ""
Write-Host "Running UI smoke tests against $exe"
Write-Host "(these open real windows — leave the desktop alone while they run)"
Write-Host ""

$testArgs = @($testProject, "-c", $Configuration, "-p:Platform=$Platform")
if ($Filter) { $testArgs += @("--filter", $Filter) }

dotnet test @testArgs
$testExit = $LASTEXITCODE

Remove-Item Env:\ANCHOR_UITESTS -ErrorAction SilentlyContinue
Remove-Item Env:\ANCHOR_EXE -ErrorAction SilentlyContinue

if ($testExit -ne 0) { throw "UI tests failed (exit code $testExit)" }
Write-Host ""
Write-Host "UI smoke tests passed."
