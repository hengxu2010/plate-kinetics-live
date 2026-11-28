# Builds the program, its installer and portable zip into dist\ beside this script:
#   dist\PlateKineticsLive-Setup.exe            the BMG Omega program (src\PlateKineticsLive.csproj, web files from web\)
#   dist\<name>-portable.zip             the same program without installer and service: unzip, run <name>.exe
#   dist\PlateKineticsLive-offline.zip   with -Offline: the installers built and Microsoft's installers of the .NET
#                                        Framework 4.8 and the WebView2 Runtime (from redist\), for PCs without internet
# An installer carries its program as an embedded zip and its settings (name, exe, version, port); src\Setup\Setup.cs
# is the source of the installer. The program's files: the exe, the WebView2 DLLs (vendor\webview2), web\ (the
# page and the shell), Help\ (every file of help\: the manuals), the licence texts (LICENSE.txt, NOTICE.txt and
# THIRD-PARTY-NOTICES.txt from help\, when there, and the WebView2 SDK's licence).
# Nothing is downloaded: everything targets .NET Framework 4.8 (part of Windows) and is restored from an empty folder;
# -Offline only packs what is in redist\ already (checked: Microsoft's valid signature).
# Work files go to build\ (obj, bin, payload of each program); dist\ only gets the installers and zips.
# The C# sources are ASCII only (Chinese texts as \u escapes): the build names a file that is not.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1                         the program and its installer
#   ... -Version 2026.11.23.1          the version Windows' list of apps shows (default: the build time, yyyy.M.d.HHmm)
#   ... -Offline [-RedistDir <folder>]  also the offline package (needs ndp48-x86-x64-allos-*.exe and
#                                      MicrosoftEdgeWebView2RuntimeInstallerX64.exe in redist\)
# Tests: the installer of a ready program folder, no program is built:
#   ... -Name PlateKineticsLive-InstallerTest -ProgramDir <folder with PlateKineticsLive-InstallerTest.exe> -WebDir <web files> -Port 47899
param(
    [string]$Version = (Get-Date -Format 'yyyy.M.d.HHmm'),
    [string]$AnalysisVersion = '',
    [string[]]$Only = @(),
    [string]$OutDir = (Join-Path $PSScriptRoot 'dist'),
    [string]$WorkDir = (Join-Path $PSScriptRoot 'build'),
    [string]$Name = '',
    [string]$ProgramDir = '',
    [string]$WebDir = '',
    [int]$Port = 0,
    [string]$Mode = '',
    [switch]$Offline,
    [string]$RedistDir = (Join-Path $PSScriptRoot 'redist'),
    [string]$HelpDir = (Join-Path $PSScriptRoot 'help')
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$src = Join-Path $PSScriptRoot 'src'
$vendor = Join-Path $PSScriptRoot 'vendor\webview2'
$dlls = 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll'
$webTypes = '.html', '.js', '.css'                # the page and the shell: every such file of the web folder
$licences = 'LICENSE.txt', 'NOTICE.txt', 'THIRD-PARTY-NOTICES.txt'   # beside the exe too, when help\ has them
$programs = @(
    @{ Name = 'PlateKineticsLive'; Project = (Join-Path $src 'PlateKineticsLive.csproj'); Web = (Join-Path $PSScriptRoot 'web'); Port = 47815; Mode = 'live'; WebOnly = @() },
    @{ Name = 'PlateKineticsAnalysis'; Project = (Join-Path $src 'PlateKineticsAnalysis.csproj'); Web = (Join-Path $PSScriptRoot 'web'); Port = 0; Mode = 'analysis'; WebOnly = @('PlateKineticsAnalysis.html') }
)
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$WorkDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDir)
$RedistDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RedistDir)
$HelpDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($HelpDir)
$feed = Join-Path $WorkDir 'empty-feed'        # the only package source: nothing in it, so nothing can be downloaded
New-Item -ItemType Directory -Force $feed, $OutDir | Out-Null

foreach ($file in Get-ChildItem $src -Recurse -Filter '*.cs') {
    $at = [Array]::FindIndex([IO.File]::ReadAllBytes($file.FullName), [Predicate[byte]] { param($b) $b -gt 127 })
    if ($at -ge 0) { Write-Warning ('not ASCII (byte ' + $at + '): ' + $file.FullName + ' - write Chinese texts as \u escapes') }
}

# the offline package's redistributables: there and signed by Microsoft, checked before anything is built
$redist = @()
if ($Offline) {
    $net = Get-ChildItem $RedistDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'ndp48*-x86-x64-allos-*.exe' } | Sort-Object Name | Select-Object -Last 1
    $wv2 = Get-ChildItem $RedistDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'MicrosoftEdgeWebView2RuntimeInstaller*X64*.exe' } | Sort-Object Name | Select-Object -Last 1
    if (-not $net -or -not $wv2) {
        throw ('-Offline needs Microsoft''s installers in ' + $RedistDir + ' (nothing is downloaded): ' +
               'ndp48-x86-x64-allos-enu.exe (.NET Framework 4.8, offline installer: https://dotnet.microsoft.com/download/dotnet-framework/net48) and ' +
               'MicrosoftEdgeWebView2RuntimeInstallerX64.exe (WebView2 Runtime, Evergreen Standalone Installer x64: https://developer.microsoft.com/microsoft-edge/webview2/)' +
               '; missing: ' + ((@($(if (-not $net) { 'ndp48-x86-x64-allos-*.exe' }), $(if (-not $wv2) { 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe' })) | Where-Object { $_ }) -join ', '))
    }
    foreach ($file in $net, $wv2) {
        $signature = Get-AuthenticodeSignature $file.FullName
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            throw ('not a valid Microsoft signature: ' + $file.FullName + ' (' + $signature.Status + ', ' + $signature.SignerCertificate.Subject + ')')
        }
        '{0}: signed by Microsoft, {1:N1} MB' -f $file.Name, ($file.Length / 1MB) | Out-Host
    }
    $redist = @($net, $wv2)
}

# the version as a file version (four numbers up to 65535), when it is one
$fileVersion = $null
$parsed = $null
if ([version]::TryParse($Version, [ref]$parsed) -and @($parsed.Major, $parsed.Minor, $parsed.Build, $parsed.Revision | Where-Object { $_ -gt 65535 }).Count -eq 0) {
    $fileVersion = $parsed.ToString()
}

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw ('dotnet ' + $Arguments[0] + ' failed (exit code ' + $LASTEXITCODE + ')') }
}

# Restores (from the empty folder) and builds one project, its obj\ and bin\ under $Work; returns the bin folder.
function Build-Project([string]$Project, [string]$Work, [string[]]$Properties) {
    $obj = Join-Path $Work 'obj'
    $bin = Join-Path $Work 'bin'
    $common = @('-nologo', '-v:minimal', "-p:BaseIntermediateOutputPath=$obj/") + $Properties
    Invoke-Dotnet (@('restore', $Project, '--source', $feed) + $common)
    Invoke-Dotnet (@('build', $Project, '--no-restore', '-c', 'Release', "-p:OutDir=$bin/") + $common)
    return $bin
}

# A zip of a folder's files, entries with '/' between folders whatever this PowerShell's .NET would write; prefix: a
# folder the entries go into ('' for none).
function New-Zip([string]$Zip, [string]$Folder, [string]$Prefix) {
    if (Test-Path $Zip) { Remove-Item $Zip -Force }
    $stream = [IO.File]::Open($Zip, [IO.FileMode]::CreateNew)
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $Folder -Recurse -File | Sort-Object FullName) {
            $entry = $Prefix + $file.FullName.Substring($Folder.Length + 1).Replace('\', '/')
            $level = $(if ($file.Extension -eq '.exe' -and $file.Length -gt 20MB) { [IO.Compression.CompressionLevel]::NoCompression } else { [IO.Compression.CompressionLevel]::Optimal })
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, $level)
        }
    } finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

# The installer of one program: the payload zip and the settings, then Setup.csproj with both embedded.
function New-Installer([string]$AppName, [string]$Exe, [string]$Web, [int]$AppPort, [string]$AppMode = '', [string[]]$WebOnly = @(), [string]$AppVersion = '') {
    $ver = if ($AppVersion) { $AppVersion } else { $Version }
    $fv = $null
    $p = $null
    if ([version]::TryParse($ver, [ref]$p) -and @($p.Major, $p.Minor, $p.Build, $p.Revision | Where-Object { $_ -gt 65535 }).Count -eq 0) {
        $fv = $p.ToString()
    }
    $work = Join-Path $WorkDir $AppName
    $payload = Join-Path $work 'payload'
    if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $payload 'web') | Out-Null
    Copy-Item $Exe $payload
    if (Test-Path ($Exe + '.config')) { Copy-Item ($Exe + '.config') $payload }
    foreach ($dll in $dlls) { Copy-Item (Join-Path $vendor $dll) $payload }
    Copy-Item (Join-Path $vendor 'LICENSE-WebView2.txt') $payload
    $pages = @(Get-ChildItem $Web -File | Where-Object { $webTypes -contains $_.Extension.ToLower() })
    if ($WebOnly.Count -gt 0) { $pages = @($pages | Where-Object { $WebOnly -contains $_.Name }) }
    if (-not ($pages | Where-Object { $_.Name -eq 'PlateKineticsAnalysis.html' })) { throw ('no PlateKineticsAnalysis.html in ' + $Web) }
    foreach ($file in $pages) { Copy-Item $file.FullName (Join-Path $payload 'web') }
    ('{0}: web\{1}' -f $AppName, (($pages | ForEach-Object Name) -join ', web\')) | Out-Host

    # the manuals (Live builds) and licence texts beside the exe
    $help = @()
    if (Test-Path (Join-Path $HelpDir 'index.html')) {
        if ($AppMode -ne 'analysis') {
            $help = @(Get-ChildItem $HelpDir -Recurse -File)
            foreach ($file in $help) {
                $to = Join-Path (Join-Path $payload 'Help') $file.FullName.Substring($HelpDir.Length + 1)
                New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
                Copy-Item $file.FullName $to
            }
        }
        foreach ($licence in $licences) {
            if (Test-Path (Join-Path $HelpDir $licence)) { Copy-Item (Join-Path $HelpDir $licence) $payload }
            else { Write-Warning ('no ' + $licence + ' in ' + $HelpDir + ': the program is shipped without it') }
        }
    } elseif ($AppMode -ne 'analysis') { Write-Warning ('no ' + (Join-Path $HelpDir 'index.html') + ': the program is shipped without its manual') }
    ('{0}: Help\ {1} files; beside the exe: {2}' -f $AppName, $help.Count, ((Get-ChildItem $payload -File -Filter '*.txt' | ForEach-Object Name) -join ', ')) | Out-Host

    $zip = Join-Path $work 'payload.zip'
    New-Zip $zip $payload ''

    # the portable version: the same files in a folder of the program's name, as a zip (no installer, no service)
    New-Zip (Join-Path $OutDir ($AppName + '-portable.zip')) $payload ($AppName + '/')

    $settings = Join-Path $work 'setup.settings'
    $text = 'name=' + $AppName + "`r`n" + 'exe=' + (Split-Path $Exe -Leaf) + "`r`n" + 'version=' + $ver + "`r`n" + 'port=' + $AppPort + "`r`n"
    if ($AppMode) { $text += 'mode=' + $AppMode + "`r`n" }
    [IO.File]::WriteAllText($settings, $text, (New-Object Text.UTF8Encoding $false))

    $properties = @("-p:SetupName=$AppName", "-p:SetupPayload=$zip", "-p:SetupSettings=$settings", "-p:SetupVersion=$ver")
    if ($fv) { $properties += "-p:SetupFileVersion=$fv" }
    $bin = Build-Project (Join-Path $src 'Setup\Setup.csproj') (Join-Path $work 'setup') $properties
    $setup = Join-Path $OutDir ($AppName + '-Setup.exe')
    Copy-Item (Join-Path $bin ($AppName + '-Setup.exe')) $setup -Force
    return $setup
}

$built = @()
if ($Name) {
    if (-not $ProgramDir -or -not $WebDir) { throw '-Name needs -ProgramDir and -WebDir' }
    $exe = Join-Path $ProgramDir ($Name + '.exe')
    if (-not (Test-Path $exe)) { throw ('not found: ' + $exe) }
    $built += New-Installer $Name $exe $WebDir $Port $Mode
} else {
    foreach ($program in $programs) {
        if ($Only.Count -gt 0 -and $Only -notcontains $program.Name) { continue }
        if (-not (Test-Path $program.Project)) { throw ('not found: ' + $program.Project + ' (the project file of the program)') }
        $appVer = if ($program.Mode -eq 'analysis' -and $AnalysisVersion) { $AnalysisVersion } else { $Version }
        $bin = Build-Project $program.Project (Join-Path (Join-Path $WorkDir $program.Name) 'program') @("-p:SAAControlVersion=$appVer")
        $exe = Join-Path $bin ($program.Name + '.exe')
        if (-not (Test-Path $exe)) { throw ('the build made no ' + $exe) }
        '{0}  sha256 {1}' -f $exe, (Get-FileHash $exe -Algorithm SHA256).Hash.Substring(0, 16).ToLower()
        $built += New-Installer $program.Name $exe $program.Web $program.Port $program.Mode $program.WebOnly $appVer
    }
}

# the offline package: the installers built now, Microsoft's redistributables in redist\, a page of how to use it
if ($Offline) {
    $pack = Join-Path $WorkDir 'offline\PlateKineticsLive-offline'
    if (Test-Path (Split-Path $pack)) { Remove-Item (Split-Path $pack) -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $pack 'redist') | Out-Null
    foreach ($setup in $built) { Copy-Item $setup $pack }
    foreach ($file in $redist) { Copy-Item $file.FullName (Join-Path $pack 'redist') }
    Copy-Item (Join-Path $src 'Setup\offline-README.txt') (Join-Path $pack 'README.txt')
    $offlineZip = Join-Path $OutDir 'PlateKineticsLive-offline.zip'
    New-Zip $offlineZip (Split-Path $pack) ''
    $built += $offlineZip
}

foreach ($setup in @($built) + @(Get-ChildItem $OutDir -Filter '*-portable.zip' | ForEach-Object FullName)) {
    $item = Get-Item $setup
    '{0}  {1:N1} MB ({2:N0} bytes)  sha256 {3}  version {4}' -f $item.FullName, ($item.Length / 1MB), $item.Length,
        (Get-FileHash $setup -Algorithm SHA256).Hash.Substring(0, 16).ToLower(), $Version
}
