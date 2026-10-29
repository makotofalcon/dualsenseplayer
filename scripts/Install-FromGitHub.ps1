[CmdletBinding()]
param(
    [ValidatePattern('^$|^[0-9a-fA-F]{40}$')]
    [string]$Revision = '',
    [switch]$NoLaunch
)

# ASCII-only bootstrap so Windows PowerShell 5.1 can read it from HTTPS.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Run this installer in PowerShell 5.1 or later on Windows 11 x64.'
}
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$base = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DualSensePlayer'
$cache = Join-Path $base 'downloads'
New-Item -ItemType Directory -Path $cache -Force | Out-Null

$gitCommand = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($gitCommand) {
    $git = $gitCommand.Source
} else {
    $gitRoot = Join-Path $base 'mingit-2.56.0.4'
    $git = Join-Path $gitRoot 'cmd/git.exe'
    if (-not (Test-Path -LiteralPath $git)) {
        $archive = Join-Path $cache 'MinGit-2.56.0.4-64-bit.zip'
        $hash = 'e3bf78351fe777fba47ddee5950082f8a98bb0f02604fdc7ba3ee2cb68ffa877'
        if (-not (Test-Path -LiteralPath $archive)) {
            $partial = $archive + '.partial-' + [guid]::NewGuid().ToString('N')
            try {
                Write-Host 'Downloading portable Git...'
                Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.4/MinGit-2.56.0.4-64-bit.zip' -OutFile $partial
                if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $hash) { throw 'Git download checksum mismatch.' }
                Move-Item -LiteralPath $partial -Destination $archive
            } finally {
                if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial }
            }
        }
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $hash) { throw ('Git cache checksum mismatch: ' + $archive) }
        if (Test-Path -LiteralPath $gitRoot) { throw ('Incomplete Git installation. Move this directory aside and retry: ' + $gitRoot) }
        $staging = $gitRoot + '.staging-' + [guid]::NewGuid().ToString('N')
        Expand-Archive -LiteralPath $archive -DestinationPath $staging
        Move-Item -LiteralPath $staging -Destination $gitRoot
    }
}
& $git --version
if ($LASTEXITCODE -ne 0) { throw 'Git could not start.' }

$source = Join-Path $base ('source-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
$repository = 'https://github.com/makotofalcon/dualsenseplayer.git'
Write-Host 'Cloning DualSensePlayer...'
& $git -c core.longpaths=true clone --depth 1 --single-branch --branch codex/powershell-setup $repository $source
if ($LASTEXITCODE -ne 0) { throw 'Git clone failed. Check the connection and the error above.' }
if ($Revision) {
    $head = & $git -C $source rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the downloaded revision.' }
    if ($head.Trim() -ne $Revision) {
        & $git -C $source fetch --depth 1 origin $Revision
        if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the requested revision.' }
        & $git -C $source checkout --detach $Revision
        if ($LASTEXITCODE -ne 0) { throw 'Could not check out the requested revision.' }
    }
    $head = & $git -C $source rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head.Trim() -ne $Revision) { throw 'Downloaded revision does not match.' }
}
$setup = [IO.File]::ReadAllText((Join-Path $source 'scripts/Install-Windows.ps1'), [Text.Encoding]::UTF8)
& ([scriptblock]::Create($setup)) -SourceRoot $source -NoLaunch:$NoLaunch
