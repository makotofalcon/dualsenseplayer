# 試験動画

`test-media.zip` は `scripts/generate-test-media.py` で生成した6本の試験動画です。ユーザーの本番素材は含みません。Gitから導入するWindows版で、Pythonを追加せずにチャンネル・同期・周回の確認を行うために同梱しています。

| ファイル | 尺 | 内容 |
|---|---|---|
| channel-check.mov / .mp4 | 8秒 | 通常L → 通常R → 振動L → 振動R |
| sync-check.mov / .mp4 | 30秒 | 毎秒のパルスと白フラッシュ |
| loop-check.mov / .mp4 | 30秒 | 連続波形による周回確認 |

映像は640×360、30fps、H.264です。音声は48kHz・4chで、MOVはPCM24、MP4はAAC-LCです。通常音声の振幅は0.25、振動信号は0.6とし、アプリ側の強度設定をさらに適用します。

再生成にはlibx264・AAC・lavfi・drawtextを備えた開発用FFmpegとPythonを使います。

```bash
python3 scripts/generate-test-media.py --archive assets/test-media.zip
```

生成に使用したFFmpeg実行ファイル自体はこのZIPに含めません。実機試験は `docs/VALIDATION.md` を参照してください。
