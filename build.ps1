#Requires -Version 5.1
<#
.SYNOPSIS
    Builds, tests, publishes and optionally signs WSL Proxy Relay.

.DESCRIPTION
    1. Restores the local .NET tools and works out the version with GitVersion.
    2. Restores packages in locked mode, so the committed lock files must match.
    3. Builds and tests the solution.
    4. Publishes a self-contained folder for each runtime.
    5. Signs our own binaries with signtool, when a certificate thumbprint is given.
    6. Zips each folder and writes SHA256SUMS.txt next to the zips.

    Output goes to Artifacts/.

.PARAMETER Configuration
    The build configuration. Defaults to Release.

.PARAMETER Runtime
    The runtime identifiers to publish. Defaults to win-x64 and win-arm64.

.PARAMETER SkipTests
    Skips the test run.

.PARAMETER CertificateThumbprint
    The SHA-1 thumbprint of a code signing certificate in the current user's or local machine's
    certificate store. When it's empty, the binaries aren't signed and the script says so.

.PARAMETER TimestampUrl
    An RFC 3161 timestamp server. Required when signing.

.EXAMPLE
    ./build.ps1

.EXAMPLE
    ./build.ps1 -CertificateThumbprint <thumbprint> -TimestampUrl <rfc3161-url>
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64', 'win-arm64'),

    [switch] $SkipTests,

    [string] $CertificateThumbprint = '',

    [string] $TimestampUrl = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# No telemetry from the .NET CLI or the test platform.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$root = $PSScriptRoot
$solution = Join-Path $root 'WSLProxy.slnx'
$appProject = Join-Path $root 'WSLProxy.App/WSLProxy.App.csproj'
$artifacts = Join-Path $root 'Artifacts'
$product = 'WSLProxyRelay'

function Invoke-Native {
    param([string] $File, [string[]] $Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File exited with code $LASTEXITCODE." }
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    if (-not (Test-Path $kits)) { return $null }
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    Get-ChildItem $kits -Directory |
        Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending |
        ForEach-Object { Join-Path $_.FullName "$arch/signtool.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

Write-Host '==> Restoring tools'
Invoke-Native dotnet @('tool', 'restore')

Write-Host '==> Working out the version'
$gitVersionJson = & dotnet tool run dotnet-gitversion -output json
if ($LASTEXITCODE -ne 0) { throw "GitVersion exited with code $LASTEXITCODE." }
$gitVersion = ($gitVersionJson -join "`n") | ConvertFrom-Json
$versionProperties = @(
    "-p:Version=$($gitVersion.SemVer)",
    "-p:AssemblyVersion=$($gitVersion.AssemblySemVer)",
    "-p:FileVersion=$($gitVersion.AssemblySemFileVer)",
    "-p:InformationalVersion=$($gitVersion.InformationalVersion)"
)
Write-Host "    $($gitVersion.FullSemVer)"

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item $artifacts -ItemType Directory | Out-Null

Write-Host '==> Restoring packages (locked mode)'
# WSLProxy.App lists every runtime in RuntimeIdentifiers, so one restore covers each publish below.
Invoke-Native dotnet @('restore', $solution, '--locked-mode')

Write-Host '==> Building'
Invoke-Native dotnet (@('build', $solution, '-c', $Configuration, '--no-restore') + $versionProperties)

if ($SkipTests) {
    Write-Host '==> Skipping tests'
}
else {
    Write-Host '==> Testing'
    Invoke-Native dotnet @('test', '--solution', $solution, '-c', $Configuration, '--no-build')
}

$signTool = $null
if ($CertificateThumbprint) {
    if (-not $TimestampUrl) { throw 'Signing needs -TimestampUrl.' }
    $signTool = Find-SignTool
    if (-not $signTool) { throw 'signtool.exe not found. Install the Windows SDK signing tools.' }
}
else {
    Write-Warning 'No -CertificateThumbprint given: the binaries will not be signed.'
}

foreach ($rid in $Runtime) {
    $publishDir = Join-Path $artifacts "Publish/$rid"
    Write-Host "==> Publishing $rid"
    Invoke-Native dotnet (@('publish', $appProject, '-c', $Configuration, '-r', $rid, '--self-contained', '--no-restore', '-o', $publishDir) + $versionProperties)

    if ($signTool) {
        # Sign only our own binaries. The .NET runtime files already carry Microsoft's signature.
        $ours = Get-ChildItem $publishDir -File |
            Where-Object { $_.Name -eq "$product.exe" -or $_.Name -like 'WSLProxy.*.dll' -or $_.Name -eq "$product.dll" }
        Write-Host "==> Signing $($ours.Count) files for $rid"
        Invoke-Native $signTool (@('sign', '/sha1', $CertificateThumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256') + $ours.FullName)
        Invoke-Native $signTool (@('verify', '/pa') + $ours.FullName)
    }

    $zip = Join-Path $artifacts "$product-$($gitVersion.SemVer)-$rid.zip"
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip
}

Write-Host '==> Writing SHA256SUMS.txt'
# Hash with .NET directly: Get-FileHash isn't always loadable when Windows PowerShell is started from another shell.
$sha256 = [Security.Cryptography.SHA256]::Create()
$sums = Get-ChildItem $artifacts -File -Filter '*.zip' | Sort-Object Name | ForEach-Object {
    $stream = [IO.File]::OpenRead($_.FullName)
    try { $hash = -join ($sha256.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) }
    finally { $stream.Dispose() }
    '{0}  {1}' -f $hash, $_.Name
}
$sha256.Dispose()
# sha256sum format with LF line endings, so `sha256sum -c` works on Linux too.
[IO.File]::WriteAllText((Join-Path $artifacts 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"))
Get-Content (Join-Path $artifacts 'SHA256SUMS.txt')

Write-Host "==> Done: $artifacts"
