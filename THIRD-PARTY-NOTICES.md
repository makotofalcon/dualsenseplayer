# 採用コンポーネント

| コンポーネント | 固定版 / 用途 | ライセンス |
|---|---|---|
| .NET SDK | 10.0.403 / ビルド | MITほか、SDK配布条件 |
| .NET Runtime / Windows Desktop | 10.0.14 / 配布物の実行 | MITほか。`licenses/` のLICENSEと第三者通知を参照 |
| NAudio.Core / NAudio.Wasapi | 2.2.1 / Core AudioとWASAPIのCOMラッパー | MIT。`licenses/NAudio-LICENSE.txt` |
| FFmpeg | n7.1.5、commit `3a0867c2bfda4a4d4309ca1a8cbdc6175e67f587` / MOV・MP4、H.264、PCM、AAC | LGPL-2.1-or-later構成。GPL・nonfree・外部コーデックを無効化した共有ライブラリ |
| xUnit / runner | 2.9.3 / 3.1.1 / 開発テストのみ | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 / 開発テストのみ | MIT |
| MinGW-w64 / GCC runtime | FFmpegのWindowsクロスビルド | `licenses/MinGW-w64-copyright.txt` と `licenses/GCC-runtime-copyright.txt`（ランタイム例外を含む） |

配布用FFmpegは原本のソースアーカイブ、ビルドスクリプト、configure結果、LGPL本文を `tools/ffmpeg/source/` と `tools/ffmpeg/licenses/` に含めます。DLLを置き換え可能な共有ライブラリ構成です。入力のデコードとraw PCM/BGRA出力に必要な機能だけを有効化し、ネットワーク入出力を含みません。FFmpegの商標・コーデックに関する配布条件は配布地域と用途に応じて確認してください。

## PowerShellで導入する場合

`scripts/Install-Windows.ps1` はGyan Doshiの **FFmpeg 7.1.1 essentials（GPLビルド）** をGitHubから取得します。上の最小LGPLビルドとは異なる配布構成です。ダウンロードは固定SHA-256で検証し、原本ZIP、配布元のライセンスとREADME、ソース入手先を保持します。この方法で作成したフォルダーを再配布する場合は、そのFFmpegビルドの配布条件に従ってください。

- バイナリ: https://github.com/GyanD/codexffmpeg/releases/tag/7.1.1
- FFmpeg原本ソース: https://github.com/FFmpeg/FFmpeg/tree/n7.1.1
- SHA-256: `04861d3339c5ebe38b56c19a15cf2c0cc97f5de4fa8910e4d47e5e6404e4a2d4`

Gitが未導入の場合、`scripts/Install-FromGitHub.ps1` はGit for WindowsのMinGit 2.56.0.4をユーザーフォルダーへ展開します。配布物は改変せず、同梱のライセンスを保持します。Gitと関連コンポーネントのライセンス・ソースは https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.4 を参照してください。

- MinGit ZIP SHA-256: `e3bf78351fe777fba47ddee5950082f8a98bb0f02604fdc7ba3ee2cb68ffa877`

## 最小LGPLビルドの出典

ソースアーカイブSHA-256:

```
83220fe0807da10df19714176f02ec3d3812ef0e055f69e940d01b3ad1379cdb
```

参考にしたプロトコル資料:

- [Jellybit/DualSense-Haptic-Designer](https://github.com/Jellybit/DualSense-Haptic-Designer)、参照commit `e4653e82cd7320263b88eaaaf3a9a8036524c6a1`。USB音声4chの経路。
- [SDL HIDAPI PS5 driver](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c)。USBレポートと音声ハプティクス/ランブル選択ビット。
- [Microsoft IAudioClock::GetPosition](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getposition)。機器時計とQPC。

これらの資料はSony公式のWindows向けAPI保証を意味しません。開発用Linux FFmpegのlibx264は試験素材の生成だけに使用し、そのバイナリをWindowsパッケージへ含めません。
