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
    7. Writes GitVersion's variables to version.json, for CI.

    With -Msix, step 6 builds a signed MSIX bundle instead of the zips: it packs each signed folder with
    Packaging/AppxManifest.xml and logos made from Assets/wslproxy-logo.ico, bundles the packages into one
    .msixbundle, signs the bundle and writes SHA256SUMS.txt for it. MSIX mode needs the Windows SDK's makeappx.exe and
    signtool.exe, and a code signing certificate whose subject becomes the package's publisher.

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

.PARAMETER Msix
    Builds a signed MSIX bundle, WSLProxyRelay-<version>.msixbundle, instead of the zips. Needs
    -CertificateThumbprint and -TimestampUrl, because Windows won't install an unsigned package.

.PARAMETER PublisherDisplayName
    The publisher name the MSIX package shows. Defaults to the certificate subject's common name.

.EXAMPLE
    ./build.ps1

.EXAMPLE
    ./build.ps1 -CertificateThumbprint <thumbprint> -TimestampUrl <rfc3161-url>

.EXAMPLE
    ./build.ps1 -Msix -CertificateThumbprint <thumbprint> -TimestampUrl <rfc3161-url>
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64', 'win-arm64'),

    [switch] $SkipTests,

    [string] $CertificateThumbprint = '',

    [string] $TimestampUrl = '',

    [switch] $Msix,

    [string] $PublisherDisplayName = ''
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

# Finds a Windows SDK tool, such as signtool.exe or makeappx.exe: on the PATH, or else in the newest Windows SDK for
# this machine's architecture.
function Find-SdkTool {
    param([string] $Name)
    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    if (-not (Test-Path $kits)) { return $null }
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    Get-ChildItem $kits -Directory |
        Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending |
        ForEach-Object { Join-Path $_.FullName "$arch/$Name" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

# Finds a certificate by thumbprint in the current user's, then the local machine's, personal store. Returns it with the
# store it was in, because signtool only looks in the local machine's store when it's given /sm.
function Find-Certificate {
    param([string] $Thumbprint)
    foreach ($location in 'CurrentUser', 'LocalMachine') {
        $store = New-Object Security.Cryptography.X509Certificates.X509Store('My', $location)
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        try {
            $found = $store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $Thumbprint, $false)
            if ($found.Count -gt 0) { return [pscustomobject]@{ Certificate = $found[0]; Location = $location } }
        }
        finally { $store.Close() }
    }
    return $null
}

function Test-CodeSigningCertificate {
    param([Security.Cryptography.X509Certificates.X509Certificate2] $Certificate)
    $codeSigning = '1.3.6.1.5.5.7.3.3'
    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -ne '2.5.29.37') { continue }
        $usage = New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($extension, $extension.Critical)
        foreach ($oid in $usage.EnhancedKeyUsages) {
            if ($oid.Value -eq $codeSigning) { return $true }
        }
    }
    return $false
}

# Writes a square PNG logo from an .ico file. It scales the smallest frame that's at least as big as the logo, so it
# only ever scales down, or the biggest frame when none is.
function New-LogoPng {
    param([string] $IconPath, [int] $Size, [string] $Destination)
    $bytes = [IO.File]::ReadAllBytes($IconPath)
    $frames = for ($i = 0; $i -lt [BitConverter]::ToUInt16($bytes, 4); $i++) {
        $entry = 6 + 16 * $i
        [pscustomobject]@{
            # A width of 0 means 256.
            Width  = if ($bytes[$entry] -eq 0) { 256 } else { [int]$bytes[$entry] }
            Bits   = [int][BitConverter]::ToUInt16($bytes, $entry + 6)
            Length = [int][BitConverter]::ToUInt32($bytes, $entry + 8)
            Offset = [int][BitConverter]::ToUInt32($bytes, $entry + 12)
        }
    }
    $byQuality = @{ Expression = 'Bits'; Descending = $true }
    $frame = $frames | Where-Object { $_.Width -ge $Size } |
        Sort-Object @{ Expression = 'Width'; Ascending = $true }, $byQuality | Select-Object -First 1
    if (-not $frame) {
        $frame = $frames | Sort-Object @{ Expression = 'Width'; Descending = $true }, $byQuality | Select-Object -First 1
    }

    $stream = $null; $icon = $null; $source = $null; $target = $null; $graphics = $null; $attributes = $null
    try {
        # A frame is either a PNG or a bitmap, which System.Drawing.Icon reads.
        if ($bytes[$frame.Offset] -eq 0x89 -and $bytes[$frame.Offset + 1] -eq 0x50) {
            $stream = New-Object IO.MemoryStream($bytes, $frame.Offset, $frame.Length, $false)
            $source = New-Object Drawing.Bitmap($stream)
        }
        else {
            $icon = New-Object Drawing.Icon($IconPath, $frame.Width, $frame.Width)
            $source = $icon.ToBitmap()
        }
        $target = New-Object Drawing.Bitmap($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($target)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
        # Mirroring at the edges stops the bicubic filter blending in transparent pixels from outside the image.
        $attributes = New-Object Drawing.Imaging.ImageAttributes
        $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage($source, (New-Object Drawing.Rectangle(0, 0, $Size, $Size)), 0, 0, $source.Width, $source.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
        $target.Save($Destination, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        foreach ($disposable in $attributes, $graphics, $target, $source, $icon, $stream) {
            if ($disposable) { $disposable.Dispose() }
        }
    }
}

# Fills the placeholders in double braces in the MSIX manifest template, escaping each value for XML.
function New-AppxManifest {
    param([string] $Template, [hashtable] $Values)
    $manifest = $Template
    foreach ($name in $Values.Keys) {
        $manifest = $manifest.Replace("{{$name}}", [Security.SecurityElement]::Escape([string]$Values[$name]))
    }
    $left = [regex]::Match($manifest, '\{\{\w+\}\}')
    if ($left.Success) { throw "Packaging/AppxManifest.xml has a placeholder build.ps1 doesn't fill: $($left.Value)." }
    # Check it's still well-formed.
    [xml]$manifest | Out-Null
    return $manifest
}

if ($Msix -and -not ($CertificateThumbprint -and $TimestampUrl)) {
    throw '-Msix needs -CertificateThumbprint and -TimestampUrl: Windows only installs a signed MSIX package.'
}

$signTool = $null
$signArguments = @()
$verifySignatures = $true
if ($CertificateThumbprint) {
    if (-not $TimestampUrl) { throw 'Signing needs -TimestampUrl.' }
    $signTool = Find-SdkTool 'signtool.exe'
    if (-not $signTool) { throw 'signtool.exe not found. Install the Windows SDK signing tools.' }

    # Thumbprints are often pasted with spaces. Check and sign with the same value.
    $CertificateThumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
    $found = Find-Certificate $CertificateThumbprint
    if (-not $found) {
        throw 'No certificate with that thumbprint in the CurrentUser\My or LocalMachine\My certificate store.'
    }
    $certificate = $found.Certificate
    $signArguments = @('/sha1', $CertificateThumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
    if ($found.Location -eq 'LocalMachine') { $signArguments = @('/sm') + $signArguments }

    # signtool verify /pa needs a chain to a trusted root. A self-signed test certificate shouldn't be made a trusted
    # root just for that, so its signatures aren't verified here. Windows checks an MSIX package's signature against
    # Trusted People when it installs it.
    if ($certificate.Subject -eq $certificate.Issuer) {
        $verifySignatures = $false
        Write-Warning 'The certificate is self-signed, so signtool verify is skipped. Use it for testing only.'
    }
}
else {
    Write-Warning 'No -CertificateThumbprint given: the binaries will not be signed.'
}

if ($Msix) {
    $makeAppx = Find-SdkTool 'makeappx.exe'
    if (-not $makeAppx) { throw 'makeappx.exe not found. Install the Windows SDK, which includes the MSIX packaging tools.' }

    if (-not (Test-CodeSigningCertificate $certificate)) {
        throw 'The certificate has no code signing enhanced key usage (1.3.6.1.5.5.7.3.3), so it cannot sign an MSIX package.'
    }
    if (-not $certificate.HasPrivateKey) { throw 'The certificate has no private key here, so it cannot sign.' }

    # The manifest's publisher must equal the certificate's subject exactly, or signtool refuses to sign the bundle.
    $publisher = $certificate.Subject
    if (-not $PublisherDisplayName) {
        $PublisherDisplayName = $certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    }
    if (-not $PublisherDisplayName) { throw 'The certificate subject has no common name: give -PublisherDisplayName.' }

    $manifestTemplate = [IO.File]::ReadAllText((Join-Path $root 'Packaging/AppxManifest.xml'))
    Add-Type -AssemblyName System.Drawing
    Write-Host "==> Building an MSIX bundle published by $publisher"
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

# CI reads this to decide whether a release is due.
[IO.File]::WriteAllText((Join-Path $artifacts 'version.json'), ($gitVersionJson -join "`n"))

Write-Host '==> Restoring packages (locked mode)'
# WSLProxy.App lists every runtime in RuntimeIdentifiers, so one restore covers each publish below.
Invoke-Native dotnet @('restore', $solution, '--locked-mode')

Write-Host '==> Building'
Invoke-Native dotnet (@('build', $solution, '-c', $Configuration, '--no-restore') + $versionProperties)

if ($SkipTests) {
    Write-Host '==> Skipping tests'
}
elseif (-not (Get-ChildItem -Path $root -Filter '*.Tests.csproj' -Recurse -Depth 1)) {
    # dotnet test fails when a solution has no test projects.
    Write-Warning 'The solution has no test projects yet, so no tests ran.'
}
else {
    Write-Host '==> Testing'
    Invoke-Native dotnet @('test', '--solution', $solution, '-c', $Configuration, '--no-build')
}

if ($Msix) {
    # Package versions have four parts.
    $packageVersion = '{0}.{1}.{2}.0' -f $gitVersion.Major, $gitVersion.Minor, $gitVersion.Patch
    $msixDir = Join-Path $artifacts 'Msix'
    $packagesDir = Join-Path $msixDir 'Packages'
    $logosDir = Join-Path $msixDir 'Images'
    New-Item $packagesDir, $logosDir -ItemType Directory | Out-Null

    Write-Host '==> Making the MSIX logos'
    # The sizes Packaging/AppxManifest.xml names.
    $logos = @{ 'Square44x44Logo.png' = 44; 'Square150x150Logo.png' = 150; 'StoreLogo.png' = 50 }
    foreach ($logo in $logos.Keys) {
        New-LogoPng -IconPath (Join-Path $root 'Assets/wslproxy-logo.ico') -Size $logos[$logo] -Destination (Join-Path $logosDir $logo)
    }
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
        Invoke-Native $signTool (@('sign') + $signArguments + $ours.FullName)
        if ($verifySignatures) { Invoke-Native $signTool (@('verify', '/pa') + $ours.FullName) }
    }

    if ($Msix) {
        # The package holds the signed publish output, the filled manifest and the logos.
        $stagingDir = Join-Path $msixDir "Staging/$rid"
        Write-Host "==> Packing the MSIX package for $rid"
        New-Item $stagingDir -ItemType Directory | Out-Null
        Copy-Item (Join-Path $publishDir '*') $stagingDir -Recurse
        Copy-Item $logosDir $stagingDir -Recurse
        $manifest = New-AppxManifest $manifestTemplate @{
            Publisher            = $publisher
            PublisherDisplayName = $PublisherDisplayName
            Version              = $packageVersion
            Architecture         = $rid -replace '^win-', ''
        }
        [IO.File]::WriteAllText((Join-Path $stagingDir 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))
        $package = Join-Path $packagesDir "$product-$($gitVersion.SemVer)-$rid.msix"
        Invoke-Native $makeAppx @('pack', '/d', $stagingDir, '/p', $package, '/o')
    }
    else {
        $zip = Join-Path $artifacts "$product-$($gitVersion.SemVer)-$rid.zip"
        Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip
    }
}

if ($Msix) {
    # Only the bundle is signed: Windows checks the bundle's signature, which covers the packages inside it.
    $bundle = Join-Path $artifacts "$product-$($gitVersion.SemVer).msixbundle"
    Write-Host '==> Bundling the MSIX packages'
    Invoke-Native $makeAppx @('bundle', '/d', $packagesDir, '/p', $bundle, '/bv', $packageVersion, '/o')
    Write-Host '==> Signing the MSIX bundle'
    Invoke-Native $signTool (@('sign') + $signArguments + $bundle)
    if ($verifySignatures) { Invoke-Native $signTool @('verify', '/pa', $bundle) }
}

Write-Host '==> Writing SHA256SUMS.txt'
# Hash with .NET directly: Get-FileHash isn't always loadable when Windows PowerShell is started from another shell.
$sha256 = [Security.Cryptography.SHA256]::Create()
$packageFilter = if ($Msix) { '*.msixbundle' } else { '*.zip' }
$sums = Get-ChildItem $artifacts -File -Filter $packageFilter | Sort-Object Name | ForEach-Object {
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
