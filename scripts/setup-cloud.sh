#!/usr/bin/env bash
set -euo pipefail
cd /workspace/dualsenseplayer
mkdir -p /workspace/.tools/dotnet /workspace/.cache/dotnet-home /workspace/.cache/nuget /workspace/.cache/dualsenseplayer
if [ ! -x /workspace/.tools/dotnet/dotnet ] || [ "$(/workspace/.tools/dotnet/dotnet --version)" != 10.0.403 ]; then
  archive=/workspace/.cache/dualsenseplayer/dotnet-sdk-10.0.403-linux-x64.tar.gz
  curl --fail --show-error --location https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-linux-x64.tar.gz --output "$archive"
  printf '%s  %s\n' b7b3d8461cf98b07d2552ce9199702fa3ad010ac2671a5f8cd5136f7534e50f6855ba7bbb02b11494563f1d04156276a31cda1103b900e5eecd0d9473dc7b9c5 "$archive" | sha512sum --check
  tar -xzf "$archive" -C /workspace/.tools/dotnet
fi
source scripts/env-cloud.sh
command -v ffmpeg >/dev/null
command -v ffprobe >/dev/null
dotnet restore DualSensePlayer.sln --locked-mode --nologo
dotnet build DualSensePlayer.sln -c Release --no-restore --nologo
dotnet test tests/DualSensePlayer.Tests -c Release --no-build --no-restore --nologo
