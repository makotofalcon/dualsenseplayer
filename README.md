# DualSense 展示プレーヤー

Windows 11 x64で、4ch動画の映像・通常音声・USB DualSense 2台の振動を同期再生するアプリです。複数の動画をプレイリストに配置すると、上から順に連続再生し、最後から先頭へ戻ります。C# / .NET 10 LTS / WPFで実装しています。

動画の論理ch1/2を通常音声へ、ch3/4を各DualSenseのUSB出力ch3/4へ送ります。DualSenseのch1/2は常に無音です。通常音声のミュート、台ごとの強度・ミュート・時刻補正、振動全停止を独立して操作できます。

この版は**実機検収前**です。Linux上でWindows向けビルドと共通処理・実デコードの自動テストを実施しています。WPF画面、WindowsのWASAPI/HID、実際の振動、同期精度、24時間運転はWindowsと展示機材で確認する必要があります。検証範囲は [VALIDATION.md](docs/VALIDATION.md) を参照してください。

## PowerShellでGitから取得して起動

Windows 11 x64のPowerShell 5.1以降に、次をまとめて貼り付けて実行してください。公開リポジトリからGitで取得し、必要なソフトの準備・ビルド・起動まで行います。

```powershell
& {
    $ErrorActionPreference = 'Stop'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $url = 'https://raw.githubusercontent.com/makotofalcon/dualsenseplayer/codex/powershell-setup/scripts/Install-FromGitHub.ps1'
    $code = (Invoke-WebRequest -UseBasicParsing -Uri $url).Content
    & ([scriptblock]::Create($code))
}
```

GitがなければMinGitを、必要な.NET SDKがなければ10.0.403をユーザーフォルダーへ展開します。FFmpegも自動取得し、固定チェックサムで検証します。初回の取得量は約450MBとNuGet依存分です。管理者権限や実行ポリシーの変更は不要です。取得したソースとアプリは `%LOCALAPPDATA%\DualSensePlayer` に保存し、デスクトップに「DualSense Player」を作成します。再実行では別のフォルダーに導入し、動画・機器の設定を保持します。

起動後は下の手順4から、表示先・通常音声・DualSenseの出力先を設定してください。チャンネル確認用など6本の試験動画も `test-media/` に展開されます。

Gitで取得済みなら、リポジトリ内の `scripts/Install-Windows.ps1` を実行できます。`-NoLaunch` は導入後の起動を省略する検証用オプションです。

この導入方法はGyanのFFmpeg 7.1.1 essentials（GPLビルド）を利用します。下記ZIP版のFFmpeg 7.1.5最小LGPLビルドとは配布構成が異なります。原本ZIP、ライセンス、ソース入手先を保持します。詳しくは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。

## ビルド済みZIPで起動

1. `artifacts/DualSensePlayer-win-x64.zip` をWindows PCへコピーし、フォルダー全体を展開します。
2. データ通信対応USBケーブルで標準DualSenseを接続します。初回は1台ずつ登録してください。
3. `DualSensePlayer.Supervisor.exe` を起動します。同梱版は.NETランタイム・FFmpegを含み、インストールや管理者権限を必要としません。
4. 管理画面で表示先、通常音声出力、コントローラー2枠を選択し、25%の強度で左右テストを行います。
5. `test-media/channel-check.mov` を手動再生し、通常L → 通常R → 両台の左振動 → 両台の右振動の順に分離していることを確認します。停止後にch対応の確認済みを保存します。
6. 「動画を追加…」で本番動画を複数選択し、一覧の「上へ」「下へ」で再生順を決めます。動画やフォルダーのドラッグ＆ドロップ、フォルダー単位の追加にも対応します。
7. 一覧から各動画を選び、対象4chトラックとch対応を指定します。「全動画を検証・先読み」の後、「再生 / 再開」を押すと、A → B → C → Aの順に繰り返します。「選択した動画から再生」で途中の動画から開始できます。

初回のチャンネル確認のため、未確認の素材も手動再生できます。自動再生は、全動画のch対応が確認済み・自動再生有効・展示全停止が解除されている場合だけ開始します。再生順と動画ごとの設定を保存でき、従来の単一動画の設定も1本のプレイリストへ自動移行します。停止・正常終了の意思を優先するため、正常終了後に無人運転を再開するときは一度手動で再生してください。

詳しい操作、接続図、復帰手順は [OPERATIONS.md](docs/OPERATIONS.md) にあります。

## 対応素材

- ローカルMOV / MP4、H.264、1920×1080以下、60fps以下のCFR、10分以内。
- 48kHz・4ch PCM 16/24bitまたはAAC-LC。複数の対応トラックはスタッフが選択します。
- デコード順を表示し、論理chへの対応を動画ごとに明示指定します。音声PTS、開始差、AACのskip samplesを扱い、各映像の表示尺を合計した時間をプレイリストの1周とします。
- 対応範囲内で解像度・fps・PCM/AACが異なる動画を混在できます。同じ動画の複数配置にも対応します。
- 2ch素材、破損ファイル、未対応コーデック・VFRは開始前に拒否します。

音声波形は全動画分をRAMへ読み込みます。1本10分・4chのfloat PCMは約461MBです。プレイリストは最大100本、PCMの合計は2GiB以内（約46分）とし、超過した場合は先読み前に知らせます。別途、映像バッファとアプリのメモリが必要です。現在と次の動画を有限キューへ先読みし、動画の切り替えでも共通時計と音声出力を継続します。信号の自動生成・EQ・正規化・クロスフェードは行いません。

プレイリストは追加の要望に対応した機能です。初期仕様書 `docs/SPECIFICATION-v1.0.md` は原文のまま保存しています。

## 開発

.NET SDKは `global.json`、NuGet依存は各 `packages.lock.json` で固定しています。

```powershell
dotnet restore DualSensePlayer.sln --locked-mode
dotnet build DualSensePlayer.sln -c Release --no-restore
dotnet test tests/DualSensePlayer.Tests -c Release --no-build --no-restore
dotnet run --project src/DualSensePlayer.App
```

開発時はFFmpeg 7.1系の `ffmpeg` / `ffprobe` をPATHへ追加するか、アプリ出力フォルダーの `tools/ffmpeg/` に配置します。メディアテストと試験動画生成には、H.264エンコーダー `libx264`、AACエンコーダー、lavfiを備えた開発用FFmpegが必要です。配布用FFmpegは再生に必要な機能だけをビルドしています。

このクラウド環境では `source scripts/env-cloud.sh` の後に上記のrestore/build/testを実行できます。WPFアプリの起動はWindowsで行ってください。

ソース内蔵の長いPowerShellコードが必要な場合は、`python3 scripts/generate-paste-installer.py` で生成できます。生成側はLinuxの `libbrotlienc` を利用します。通常の導入には冒頭のGit経由の手順を使ってください。

```bash
python3 scripts/generate-test-media.py
# Linux + MinGW-w64 / nasm / makeでWindows用LGPLデコーダーを作成
bash scripts/build-ffmpeg-windows.sh
python3 scripts/publish.py --ffmpeg artifacts/ffmpeg-win64
```

配布スクリプトは既存の出力フォルダーを上書きしません。再作成するときは既存パッケージを別の場所へ移してください。FFmpegのソース、ビルド設定、ライセンスをパッケージへ同梱します。

構成と同期方式は [ARCHITECTURE.md](docs/ARCHITECTURE.md)、採用ライブラリは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。
