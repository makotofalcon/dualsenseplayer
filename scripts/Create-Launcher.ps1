[CmdletBinding()]
param(
    [string]$AppDirectory = '',
    [string]$DesktopDirectory = ''
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'WindowsのPowerShellで実行してください。'
}
$required = @('DualSensePlayer.exe', 'DualSensePlayer.Supervisor.exe', 'tools/ffmpeg/ffmpeg.exe', 'tools/ffmpeg/ffprobe.exe')
if (-not $AppDirectory) {
    $base = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DualSensePlayer'
    $installations = Get-ChildItem -LiteralPath $base -Directory -Filter 'install-*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending
    foreach ($installation in $installations) {
        $candidate = Join-Path $installation.FullName 'app'
        $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $candidate $_) -PathType Leaf) })
        if ($missing.Count -eq 0) { $AppDirectory = $candidate; break }
    }
}
if (-not $AppDirectory) {
    throw 'インストール済みのアプリが見つかりません。先に前回のセットアップを完了してください。'
}
$AppDirectory = (Resolve-Path -LiteralPath $AppDirectory).Path
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $AppDirectory $name) -PathType Leaf)) {
        throw ('起動に必要なファイルがありません: ' + $name)
    }
}
if (-not $DesktopDirectory) { $DesktopDirectory = [Environment]::GetFolderPath('DesktopDirectory') }
if (-not $DesktopDirectory) { throw 'デスクトップの保存先を取得できませんでした。' }
New-Item -ItemType Directory -Path $DesktopDirectory -Force | Out-Null
$path = Join-Path $DesktopDirectory 'DualSense Player.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($path)
$shortcut.TargetPath = Join-Path $AppDirectory 'DualSensePlayer.Supervisor.exe'
$shortcut.Arguments = ''
$shortcut.WorkingDirectory = $AppDirectory
$shortcut.IconLocation = Join-Path $AppDirectory 'DualSensePlayer.exe'
$shortcut.Description = 'DualSense 展示プレーヤーを起動'
$shortcut.WindowStyle = 1
$shortcut.Save()
Write-Host ('起動用ファイルを作成しました: ' + $path) -ForegroundColor Green
Write-Host '次回からはこのファイルをダブルクリックし、アプリで「再生 / 再開」を押してください。'
