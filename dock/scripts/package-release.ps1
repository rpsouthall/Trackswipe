<#
.SYNOPSIS
    Builds self-contained Release publishes of Anchor and zips them for distribution
    (GitHub Releases, winget, manual download).

.DESCRIPTION
    Produces dist\Anchor-win-<arch>-<version>.zip for each requested architecture, containing the
    published app folder, and prints each zip's SHA256 (needed for the winget InstallerSha256
    entries — see docs/winget-deployment.md).

    WinUI cannot be AnyCPU, so each architecture is a separate publish: the -Architecture switch
    picks which. ARM64 is cross-compiled from an x64 host by the .NET SDK, so both zips can be
    produced on one machine — but only the x64 one can be smoke-tested there.

.PARAMETER Version
    Version string used in the output file names, e.g. "1.0.0". Defaults to the csproj's <Version>.

.PARAMETER Architecture
    Which architectures to publish: x64, arm64, or both (the default).

.PARAMETER FrameworkDependent
    Publish a smaller build that requires the Windows App Runtime to be installed separately
    (~60–80 MB vs ~215 MB self-contained). Off by default.

.PARAMETER CertificateThumbprint
    SHA1 thumbprint of an Authenticode code-signing certificate in the current user's (or the
    machine's) certificate store. When given, Anchor.exe is signed before the zip is built, which
    is what removes the SmartScreen "Windows protected your PC" prompt on first run.

    Anchor's public releases are NOT signed today — there is no certificate — so this is off
    unless you pass it. Everything else about the release is identical either way, so an unsigned
    build stays reproducible.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server used when signing. A timestamped signature keeps verifying after the
    certificate itself expires, which for a downloadable zip matters more than it does for
    software that is reinstalled often.

.PARAMETER SignToolPath
    Full path to signtool.exe. By default the newest one under the installed Windows SDKs is used.

.EXAMPLE
    pwsh scripts/package-release.ps1
    pwsh scripts/package-release.ps1 -Version 1.1.0
    pwsh scripts/package-release.ps1 -Architecture arm64
    pwsh scripts/package-release.ps1 -CertificateThumbprint A1B2C3...
#>
param(
    [string]$Version,
    [ValidateSet("x64", "arm64", "both")]
    [string]$Architecture = "both",
    [switch]$FrameworkDependent,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$SignToolPath
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repoRoot "src\Anchor\Anchor.csproj"

function Resolve-SignTool {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "signtool.exe not found at '$Explicit'." }
        return $Explicit
    }

    if ((Get-Command signtool.exe -ErrorAction SilentlyContinue)) {
        return (Get-Command signtool.exe).Source
    }

    # The Windows SDK installs one signtool per architecture per SDK version; take the newest,
    # preferring the x64 build (the ARM64 one won't run on an x64 host).
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    $candidate = $roots |
        ForEach-Object { Get-ChildItem -Path $_ -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue } |
        Where-Object { $_.FullName -match '\\(x64|x86)\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $candidate) {
        throw "Could not find signtool.exe. Install the Windows SDK or pass -SignToolPath."
    }
    return $candidate.FullName
}

# Signs every executable in a published folder. Only Anchor.exe is Anchor's own code, but the
# self-contained publish also carries the .NET and Windows App SDK native DLLs — those arrive
# already signed by Microsoft, so re-signing them is neither needed nor wanted.
function Invoke-SignPublish {
    param([string]$SignTool, [string]$PublishDir, [string]$Thumbprint, [string]$Timestamp)

    $exe = Join-Path $PublishDir "Anchor.exe"
    if (-not (Test-Path $exe)) { throw "Nothing to sign: '$exe' does not exist." }

    Write-Host "Signing $exe..."
    & $SignTool sign /sha1 $Thumbprint /fd SHA256 /td SHA256 /tr $Timestamp /d "Anchor" $exe
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for '$exe' (exit $LASTEXITCODE)." }

    & $SignTool verify /pa /v $exe | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The signature on '$exe' did not verify." }
}

$signTool = $null
if ($CertificateThumbprint) {
    $signTool = Resolve-SignTool -Explicit $SignToolPath
    Write-Host "Signing with certificate $CertificateThumbprint using $signTool"
}

if (-not $Version) {
    [xml]$xml = Get-Content $csproj
    $Version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { $Version = "1.0.0" }
}

$targets = if ($Architecture -eq "both") { @("x64", "arm64") } else { @($Architecture) }

$distDir = Join-Path $repoRoot "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

$results = @()

foreach ($arch in $targets) {
    # <Platform> is the MSBuild platform name (x64 / ARM64); the RID is derived from it in the
    # csproj, so passing Platform alone is enough to retarget the whole publish.
    $platform = if ($arch -eq "arm64") { "ARM64" } else { "x64" }
    $publishDir = Join-Path $repoRoot "publish\Anchor-win-$arch"
    $zipPath = Join-Path $distDir "Anchor-win-$arch-$Version.zip"

    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

    Write-Host "Publishing Anchor $Version (Release, $arch, $(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }))..."
    $publishArgs = @(
        "publish", $csproj, "-c", "Release", "-p:Platform=$platform", "-o", $publishDir
    )
    if ($FrameworkDependent) {
        $publishArgs += "-p:FrameworkDependent=true", "--self-contained", "false"
    } else {
        $publishArgs += "--self-contained", "true"
    }
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $arch" }

    # Signed before zipping, so the hash printed below is the hash of the archive people actually
    # download — signing afterwards would invalidate every published SHA256.
    if ($signTool) {
        Invoke-SignPublish -SignTool $signTool -PublishDir $publishDir `
                           -Thumbprint $CertificateThumbprint -Timestamp $TimestampUrl
    }

    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Write-Host "Zipping to $zipPath..."
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

    $results += [pscustomobject]@{
        Architecture = $arch
        Zip          = $zipPath
        Signed       = [bool]$signTool
        Sha256       = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
    }
}

Write-Host ""
Write-Host "Done."
foreach ($r in $results) {
    Write-Host "  [$($r.Architecture)] $($r.Zip)"
    Write-Host "           SHA256: $($r.Sha256)"
    Write-Host "           Signed: $(if ($r.Signed) { 'yes' } else { 'no (SmartScreen will warn on first run)' })"
}
Write-Host ""
Write-Host "Next: upload the zips to a GitHub Release, then fill InstallerUrl/InstallerSha256 for"
Write-Host "each architecture in packaging/winget/manifests/.../Anchor.installer.yaml"
Write-Host "(see docs/winget-deployment.md)."
