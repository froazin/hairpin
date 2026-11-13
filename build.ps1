#Requires -Version 5.1
<#
.SYNOPSIS
    Builds, tests, publishes and optionally signs WSL Proxy Relay.

.DESCRIPTION
    1. Restores the local .NET tools and works out the version with GitVersion.
    2. Restores packages in locked mode, so the committed lock files must match.
    3. Builds and tests the solution.
    4. Publishes a framework-dependent folder for each runtime. It needs the .NET 10 Desktop Runtime on the machine.
    5. Signs our own binaries with signtool, when a certificate thumbprint is given.
    6. Zips each folder and writes SHA256SUMS.txt next to the zips.
    7. Writes GitVersion's variables to version.json, for CI.

    With -Msix, step 6 builds a signed MSIX bundle instead of the zips: it packs each signed folder with
    Packaging/AppxManifest.xml and logos drawn from Assets/wslproxy-logo.svg at every size Windows asks for, indexed
    with makepri, bundles the packages into one .msixbundle, signs the bundle and writes SHA256SUMS.txt for it. MSIX
    mode needs the Windows SDK's makeappx.exe, makepri.exe and signtool.exe, and a code signing certificate whose
    subject becomes the package's publisher.

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

# Reads Assets/wslproxy-logo.svg for New-LogoPng. It understands only what the logo uses: a viewBox, linear gradients
# in user space, and rect (with rx) and circle, each filled or stroked with a gradient or a #RRGGBB colour. It fails on
# anything else, so a change to the logo can't be drawn wrongly without anyone noticing.
function Read-LogoSvg {
    param([string] $Path)
    [xml]$svg = [IO.File]::ReadAllText($Path)
    $root = $svg.DocumentElement
    if ($root.LocalName -ne 'svg') { throw "$Path isn't an SVG." }
    $box = @($root.GetAttribute('viewBox') -split '[\s,]+' | Where-Object { $_ } | ForEach-Object { [double]$_ })
    if ($box.Count -ne 4) { throw "$Path needs a viewBox of four numbers." }

    $gradients = @{}
    $shapes = New-Object Collections.Generic.List[object]
    $allowed = @{
        rect   = @('x', 'y', 'width', 'height', 'rx', 'fill', 'stroke', 'stroke-width')
        circle = @('cx', 'cy', 'r', 'fill', 'stroke', 'stroke-width')
    }
    foreach ($node in $root.SelectNodes('//*')) {
        $kind = $node.LocalName
        if ($kind -in 'svg', 'defs', 'stop') { continue }
        if ($kind -eq 'linearGradient') {
            if ($node.GetAttribute('gradientUnits') -ne 'userSpaceOnUse') { throw 'The logo can only use userSpaceOnUse gradients.' }
            $stops = @(foreach ($stop in $node.ChildNodes) {
                    if ($stop.LocalName -eq 'stop') {
                        [pscustomobject]@{ Offset = [double]$stop.GetAttribute('offset'); Colour = $stop.GetAttribute('stop-color') }
                    }
                })
            if ($stops.Count -lt 2) { throw "The logo's gradient $($node.GetAttribute('id')) needs at least two stops." }
            $gradients[$node.GetAttribute('id')] = [pscustomobject]@{
                X1    = [double]$node.GetAttribute('x1')
                Y1    = [double]$node.GetAttribute('y1')
                X2    = [double]$node.GetAttribute('x2')
                Y2    = [double]$node.GetAttribute('y2')
                Stops = $stops
            }
            continue
        }
        if (-not $allowed.ContainsKey($kind)) { throw "$Path uses a $kind element, which build.ps1 can't draw." }
        $values = @{}
        foreach ($attribute in $node.Attributes) {
            if ($allowed[$kind] -notcontains $attribute.LocalName) {
                throw "$Path uses $($attribute.LocalName) on a $kind, which build.ps1 can't draw."
            }
            $values[$attribute.LocalName] = $attribute.Value
        }
        # SVG fills a shape that names no fill in black. The logo always names one, so a missing fill is a mistake.
        if (-not $values.ContainsKey('fill')) { throw "A $kind in $Path has no fill." }
        $shapes.Add([pscustomobject]@{ Kind = $kind; Values = $values })
    }
    [pscustomobject]@{ MinX = $box[0]; MinY = $box[1]; Width = $box[2]; Height = $box[3]; Gradients = $gradients; Shapes = $shapes }
}

# A brush for an SVG paint: none (null), a #RRGGBB colour or url(#gradient).
function New-LogoBrush {
    param($Logo, [string] $Paint)
    if (-not $Paint -or $Paint -eq 'none') { return $null }
    if ($Paint -match '^#[0-9A-Fa-f]{6}$') { return New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml($Paint)) }
    if ($Paint -notmatch '^url\(#(?<id>[^)]+)\)$' -or -not $Logo.Gradients.ContainsKey($Matches['id'])) {
        throw "The logo uses a paint build.ps1 can't draw: $Paint."
    }
    $gradient = $Logo.Gradients[$Matches['id']]
    $stops = $gradient.Stops
    $start = New-Object Drawing.PointF([single]$gradient.X1, [single]$gradient.Y1)
    $end = New-Object Drawing.PointF([single]$gradient.X2, [single]$gradient.Y2)
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush($start, $end,
        [Drawing.ColorTranslator]::FromHtml($stops[0].Colour), [Drawing.ColorTranslator]::FromHtml($stops[-1].Colour))
    $blend = New-Object Drawing.Drawing2D.ColorBlend($stops.Count)
    $blend.Colors = [Drawing.Color[]]@($stops | ForEach-Object { [Drawing.ColorTranslator]::FromHtml($_.Colour) })
    $blend.Positions = [single[]]@($stops | ForEach-Object { [single]$_.Offset })
    $brush.InterpolationColors = $blend
    return $brush
}

# Writes a square, transparent PNG of the logo, $Size pixels across, centred and filling $Fill of the square's width.
function New-LogoPng {
    param($Logo, [int] $Size, [double] $Fill, [string] $Destination)
    $bitmap = New-Object Drawing.Bitmap($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        # Draw in the SVG's own units: fit the view box into $Fill of the square, centred.
        $scale = [Math]::Min($Size * $Fill / $Logo.Width, $Size * $Fill / $Logo.Height)
        $graphics.TranslateTransform([single](($Size - $Logo.Width * $scale) / 2), [single](($Size - $Logo.Height * $scale) / 2))
        $graphics.ScaleTransform([single]$scale, [single]$scale)
        $graphics.TranslateTransform([single](-$Logo.MinX), [single](-$Logo.MinY))

        foreach ($shape in $Logo.Shapes) {
            $v = $shape.Values
            $path = New-Object Drawing.Drawing2D.GraphicsPath
            try {
                if ($shape.Kind -eq 'circle') {
                    $r = [double]$v['r']
                    $path.AddEllipse([single]([double]$v['cx'] - $r), [single]([double]$v['cy'] - $r), [single](2 * $r), [single](2 * $r))
                }
                else {
                    $x = [double]$v['x']; $y = [double]$v['y']; $w = [double]$v['width']; $h = [double]$v['height']
                    $rx = 0.0
                    if ($v.ContainsKey('rx')) { $rx = [double]$v['rx'] }
                    $d = 2 * [Math]::Min($rx, [Math]::Min($w, $h) / 2)
                    if ($d -gt 0) {
                        $path.AddArc([single]$x, [single]$y, [single]$d, [single]$d, 180, 90)
                        $path.AddArc([single]($x + $w - $d), [single]$y, [single]$d, [single]$d, 270, 90)
                        $path.AddArc([single]($x + $w - $d), [single]($y + $h - $d), [single]$d, [single]$d, 0, 90)
                        $path.AddArc([single]$x, [single]($y + $h - $d), [single]$d, [single]$d, 90, 90)
                        $path.CloseFigure()
                    }
                    else {
                        $path.AddRectangle((New-Object Drawing.RectangleF([single]$x, [single]$y, [single]$w, [single]$h)))
                    }
                }

                $fillBrush = New-LogoBrush $Logo $v['fill']
                if ($fillBrush) {
                    try { $graphics.FillPath($fillBrush, $path) } finally { $fillBrush.Dispose() }
                }
                $strokeBrush = New-LogoBrush $Logo $v['stroke']
                if ($strokeBrush) {
                    $width = 1.0
                    if ($v.ContainsKey('stroke-width')) { $width = [double]$v['stroke-width'] }
                    $pen = New-Object Drawing.Pen($strokeBrush, [single]$width)
                    try { $graphics.DrawPath($pen, $path) } finally { $pen.Dispose(); $strokeBrush.Dispose() }
                }
            }
            finally { $path.Dispose() }
        }
        $bitmap.Save($Destination, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
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
    $makePri = Find-SdkTool 'makepri.exe'
    if (-not $makePri) { throw 'makepri.exe not found. Install the Windows SDK, which includes the MSIX packaging tools.' }

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
    # The logos sit in Images under their own root, so makepri indexes only them, not the published files, whose
    # culture folders it would read as language qualifiers.
    $logosRoot = Join-Path $msixDir 'Logos'
    $logosDir = Join-Path $logosRoot 'Images'
    New-Item $packagesDir, $logosDir -ItemType Directory | Out-Null

    Write-Host '==> Drawing the MSIX logos'
    # Windows picks the file that matches each place it shows the logo, by scale or by target size, so every one is
    # drawn at its own size from the SVG rather than stretched. The unplated target sizes are what Start, search and
    # the taskbar show. The manifest names each logo without a qualifier, and resources.pri maps the name to the files.
    $logo = Read-LogoSvg (Join-Path $root 'Assets/wslproxy-logo.svg')
    $scales = @{ 100 = 1.0; 125 = 1.25; 150 = 1.5; 200 = 2.0; 400 = 4.0 }
    # Each logo's size at 100% scale, and how much of the square's width the logo fills. The logo is twice as wide as
    # it's tall, so it fills the width of the small logos and leaves the medium tile its margin.
    $logos = @(
        @{ Name = 'Square44x44Logo'; Size = 44; Fill = 0.94 },
        @{ Name = 'Square150x150Logo'; Size = 150; Fill = 0.66 },
        @{ Name = 'StoreLogo'; Size = 50; Fill = 0.94 }
    )
    foreach ($entry in $logos) {
        foreach ($scale in $scales.Keys) {
            $size = [int][Math]::Round($entry.Size * $scales[$scale], [MidpointRounding]::AwayFromZero)
            New-LogoPng $logo $size $entry.Fill (Join-Path $logosDir "$($entry.Name).scale-$scale.png")
        }
    }
    foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
        foreach ($suffix in '', '_altform-unplated') {
            New-LogoPng $logo $size 0.94 (Join-Path $logosDir "Square44x44Logo.targetsize-$size$suffix.png")
        }
    }
    $priConfig = Join-Path $msixDir 'priconfig.xml'
    Invoke-Native $makePri @('createconfig', '/cf', $priConfig, '/dq', 'en-GB', '/pv', '10.0.0', '/o')
}

foreach ($rid in $Runtime) {
    $publishDir = Join-Path $artifacts "Publish/$rid"
    Write-Host "==> Publishing $rid"
    Invoke-Native dotnet (@('publish', $appProject, '-c', $Configuration, '-r', $rid, '--no-self-contained', '--no-restore', '-o', $publishDir) + $versionProperties)

    if ($signTool) {
        # Sign only our own binaries. The folder holds no .NET runtime, and its Microsoft.Extensions package DLLs already
        # carry Microsoft's signature.
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
        # Index the logos from their own root, so the paths in resources.pri match Images\ in the package.
        Invoke-Native $makePri @('new', '/pr', $logosRoot, '/cf', $priConfig, '/mn', (Join-Path $stagingDir 'AppxManifest.xml'),
            '/of', (Join-Path $stagingDir 'resources.pri'), '/o')
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
