[CmdletBinding()]
param(
    [string]$SourceRoot = '',
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Windows 11 x64 の PowerShell 5.1以降で実行してください。'
}
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

if (-not $SourceRoot) {
    if (-not $PSScriptRoot) { throw 'SourceRootにGitで取得したフォルダーを指定してください。' }
    $SourceRoot = Split-Path -Parent $PSScriptRoot
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
foreach ($name in @('global.json', 'src/DualSensePlayer.App/DualSensePlayer.App.csproj', 'src/DualSensePlayer.Supervisor/DualSensePlayer.Supervisor.csproj')) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot $name))) { throw ('ソースが見つかりません: ' + $name) }
}

function Get-VerifiedFile($Uri, $Path, $Hash, $Algorithm) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host ('取得中: ' + [IO.Path]::GetFileName($Path))
        $partial = $Path + '.partial-' + [guid]::NewGuid().ToString('N')
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $partial
            if ((Get-FileHash -LiteralPath $partial -Algorithm $Algorithm).Hash -ne $Hash) {
                throw ('ダウンロードの検証に失敗しました: ' + $Uri)
            }
            Move-Item -LiteralPath $partial -Destination $Path
        } finally {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial }
        }
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ne $Hash) {
        throw ('キャッシュの検証に失敗しました。このファイルを削除して再実行してください: ' + $Path)
    }
}

$base = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DualSensePlayer'
$cache = Join-Path $base 'downloads'
$install = Join-Path $base ('install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $cache, $install -Force | Out-Null
$sdk = Join-Path $base 'dotnet-10.0.403'
$dotnet = Join-Path $sdk 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $existing = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existing) {
        $installedSdks = & $existing.Source --list-sdks
        if ($LASTEXITCODE -eq 0 -and ($installedSdks -match '^10\.0\.403 \[')) { $dotnet = $existing.Source }
    }
}
if (-not (Test-Path -LiteralPath $dotnet)) {
    $sdkZip = Join-Path $cache 'dotnet-sdk-10.0.403-win-x64.zip'
    Get-VerifiedFile 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip' $sdkZip '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48' 'SHA512'
    if (Test-Path -LiteralPath $sdk) { throw ('SDKの展開先が不完全です。別名に移動して再実行してください: ' + $sdk) }
    $staging = $sdk + '.staging-' + [guid]::NewGuid().ToString('N')
    Write-Host '.NET SDKを展開しています。数分かかる場合があります。'
    Expand-Archive -LiteralPath $sdkZip -DestinationPath $staging
    Move-Item -LiteralPath $staging -Destination $sdk
}

Push-Location $SourceRoot
try {
    $sdkVersion = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdkVersion.Trim() -ne '10.0.403') { throw '必要な.NET SDK 10.0.403を実行できませんでした。' }
    $output = Join-Path $install 'app'
    foreach ($project in @('DualSensePlayer.App', 'DualSensePlayer.Supervisor')) {
        $projectFile = Join-Path $SourceRoot ('src/' + $project + '/' + $project + '.csproj')
        Write-Host ('ビルドしています: ' + $project)
        & $dotnet restore $projectFile -r win-x64 --locked-mode --nologo
        if ($LASTEXITCODE -ne 0) { throw 'NuGetの依存関係を取得できませんでした。ネット接続と上のエラーを確認してください。' }
        & $dotnet publish $projectFile -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $output --nologo
        if ($LASTEXITCODE -ne 0) { throw ('ビルドに失敗しました: ' + $project) }
    }

    $ffmpegZip = Join-Path $cache 'ffmpeg-7.1.1-essentials_build.zip'
    Get-VerifiedFile 'https://github.com/GyanD/codexffmpeg/releases/download/7.1.1/ffmpeg-7.1.1-essentials_build.zip' $ffmpegZip '04861D3339C5EBE38B56C19A15CF2C0CC97F5DE4FA8910E4D47E5E6404E4A2D4' 'SHA256'
    $decoder = Join-Path $install 'decoder'
    Expand-Archive -LiteralPath $ffmpegZip -DestinationPath $decoder
    $ffmpegRoot = Join-Path $decoder 'ffmpeg-7.1.1-essentials_build'
    $tools = Join-Path $output 'tools/ffmpeg'
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
        Copy-Item -LiteralPath (Join-Path $ffmpegRoot ('bin/' + $name)) -Destination $tools
    }
    Get-ChildItem -LiteralPath $ffmpegRoot -File | Copy-Item -Destination $tools
    $utf8 = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $tools 'SOURCE.txt'), "FFmpeg 7.1.1 essentials build by Gyan Doshi (GPL).`r`nBinary: https://github.com/GyanD/codexffmpeg/releases/tag/7.1.1`r`nFFmpeg source: https://github.com/FFmpeg/FFmpeg/tree/n7.1.1`r`n", $utf8)

    foreach ($directory in @('docs', 'licenses')) {
        Copy-Item -LiteralPath (Join-Path $SourceRoot $directory) -Destination (Join-Path $output $directory) -Recurse
    }
    foreach ($name in @('README.md', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $SourceRoot $name) -Destination $output
    }
    Expand-Archive -LiteralPath (Join-Path $SourceRoot 'assets/test-media.zip') -DestinationPath $output
    [IO.File]::WriteAllText((Join-Path $output 'SOURCE-CHECKOUT.txt'), $SourceRoot + "`r`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $output 'START-HERE.txt'), "DualSensePlayer.Supervisor.exe を起動してください。`r`n操作手順: docs/OPERATIONS.md`r`nこの導入版のFFmpeg: Gyan 7.1.1 essentials (GPL)。tools/ffmpeg/ のライセンスを参照してください。`r`n", $utf8)

    $exe = Join-Path $output 'DualSensePlayer.Supervisor.exe'
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    if ($desktop -and (Test-Path -LiteralPath $desktop)) {
        try {
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut((Join-Path $desktop 'DualSense Player.lnk'))
            $shortcut.TargetPath = $exe
            $shortcut.WorkingDirectory = $output
            $shortcut.IconLocation = Join-Path $output 'DualSensePlayer.exe'
            $shortcut.Save()
        } catch { Write-Warning ('ショートカットを作れませんでした。次の場所から起動できます: ' + $exe) }
    }
    Write-Host ('セットアップ完了: ' + $output) -ForegroundColor Green
    Write-Host '「動画を追加…」→順序と4ch設定を確認→「全動画を検証・先読み」→「再生 / 再開」'
    if (-not $NoLaunch) { Start-Process -FilePath $exe -WorkingDirectory $output }
} finally { Pop-Location }
