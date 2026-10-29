# Cloud development startup

Use `/workspace/dualsenseplayer`, the existing isolated checkout. Do not create a Git worktree unless the user explicitly requests one. Preserve existing source files and user changes. The repository began without commits; the user authorized publishing the implementation on `codex/powershell-setup` for installation through Git. Do not reset or clean the checkout.

The application targets Windows 11 x64. This Linux cloud environment supports editing, Windows cross-builds, and the portable core/FFmpeg integration tests. WPF, WASAPI, HID, actual DualSense vibration, physical synchronization, and endurance checks require Windows and the exhibition hardware.

Activate the retained SDK and NuGet cache in every shell that needs dotnet:

```bash
cd /workspace/dualsenseplayer
source scripts/env-cloud.sh
dotnet --version
ffmpeg -version
ffprobe -version
```

Expected SDK: 10.0.403. The base cloud image provides a development FFmpeg with libx264, AAC, and lavfi. No application credentials or database services are required. No long-running application service needs to start on Linux.

When dependencies need refreshing, run `bash scripts/setup-cloud.sh`. It verifies the SDK SHA-512, uses locked NuGet restore, builds the Windows projects, and runs the portable/integration tests, including playlist transitions, seeking, channel mapping, and migration of single-video settings. The latest executed count is recorded in `artifacts/validation.txt`. Do not modify lockfiles to work around restore errors. Keep TLS, package integrity, and source-archive checksum verification enabled.

For a normal code change:

```bash
dotnet build DualSensePlayer.sln -c Release --no-restore
dotnet test tests/DualSensePlayer.Tests -c Release --no-build --no-restore
```

If the executor sandbox reports `NuGet-Migrations` shared-mutex creation failure, run the same build/test operation through the supported sandbox approval mechanism. This was a sandbox IPC restriction, not missing GitHub credentials or a reason to disable verification.

The tested Windows package is `artifacts/DualSensePlayer-win-x64.zip`; extract it on Windows and run `DualSensePlayer.Supervisor.exe`. It contains the .NET runtime, a minimal LGPL FFmpeg build with corresponding source, six four-channel test videos, licenses, and staff instructions. Read `docs/OPERATIONS.md` and `docs/VALIDATION.md` for device registration and outstanding hardware acceptance checks.

The public Git installation path is `scripts/Install-FromGitHub.ps1`, followed by `scripts/Install-Windows.ps1`. It installs portable MinGit if necessary, uses locked restore and SDK 10.0.403, fetches a pinned Gyan FFmpeg 7.1.1 GPL build, and expands `assets/test-media.zip`. Use `-NoLaunch` for Windows installation checks without starting the hardware application. This path has a different FFmpeg distribution from the minimal LGPL ZIP package.

To regenerate media: `python3 scripts/generate-test-media.py`. The retained Windows FFmpeg prefix is `artifacts/ffmpeg-win64`; its build instructions are `scripts/build-ffmpeg-windows.sh`. The local cross-compiler is under `/workspace/.tools/mingw/usr`; pass `DUALSENSE_CROSS_ROOT=/workspace/.tools/mingw/usr`, `DUALSENSE_CROSS_CC=x86_64-w64-mingw32-gcc-win32`, and `DUALSENSE_BUILD_ROOT=/workspace/.cache/dualsense-ffmpeg` to reuse the verified source and build outputs. This is optional for ordinary C# changes.

After a successful build, package with `python3 scripts/publish.py --ffmpeg artifacts/ffmpeg-win64`. Preserve the existing package by moving it aside first; the publisher refuses to overwrite an existing output directory. Do not claim physical vibration or synchronization is validated by a successful cloud build. No environment publication or new-task restoration has been verified by these commands.
