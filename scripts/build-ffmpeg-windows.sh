#!/usr/bin/env bash
set -euo pipefail
# Linux cross-build. Requires MinGW-w64 C compiler, binutils, nasm, make, curl, Python 3.
# Only LGPL components; no external codec libraries or network protocols are enabled.
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
build_root="${DUALSENSE_BUILD_ROOT:-$repo_dir/artifacts/ffmpeg-build}"
prefix="$repo_dir/artifacts/ffmpeg-win64"
mkdir -p "$build_root" "$prefix"
source_archive="$build_root/ffmpeg-7.1.5-source.tar.gz"
source_sha=83220fe0807da10df19714176f02ec3d3812ef0e055f69e940d01b3ad1379cdb
if [ ! -f "$source_archive" ]; then
  curl --fail --show-error --location https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/3a0867c2bfda4a4d4309ca1a8cbdc6175e67f587 --output "$source_archive"
fi
printf '%s  %s\n' "$source_sha" "$source_archive" | sha256sum --check
source_dir="$build_root/FFmpeg-3a0867c2bfda4a4d4309ca1a8cbdc6175e67f587"
if [ ! -d "$source_dir" ]; then tar -xzf "$source_archive" -C "$build_root"; fi
if [ -n "${DUALSENSE_CROSS_ROOT:-}" ]; then export PATH="$DUALSENSE_CROSS_ROOT/bin:$PATH"; fi
compiler="${DUALSENSE_CROSS_CC:-x86_64-w64-mingw32-gcc}"
if ! command -v x86_64-w64-mingw32-gcc >/dev/null; then
  mkdir -p "$build_root/toolchain"
  ln -sf "$(command -v "$compiler")" "$build_root/toolchain/x86_64-w64-mingw32-gcc"
  export PATH="$build_root/toolchain:$PATH"
fi
mkdir -p "$build_root/objects"
cd "$build_root/objects"
"$source_dir/configure" --prefix="$prefix" --target-os=mingw32 --arch=x86_64 \
  --enable-cross-compile --cross-prefix=x86_64-w64-mingw32- --cc="$compiler" \
  --disable-autodetect --disable-gpl --disable-nonfree --disable-everything \
  --enable-shared --disable-static --enable-w32threads --disable-pthreads \
  --disable-network --disable-doc --disable-debug --disable-avdevice --disable-postproc \
  --enable-ffmpeg --enable-ffprobe --enable-avcodec --enable-avformat --enable-avfilter \
  --enable-swresample --enable-swscale \
  --enable-decoder=h264,aac,pcm_s16le,pcm_s24le \
  --enable-encoder=pcm_f32le,rawvideo --enable-demuxer=mov --enable-muxer=pcm_f32le,rawvideo \
  --enable-parser=h264,aac --enable-protocol=file,pipe \
  --enable-filter=aresample,atrim,asetpts,aformat,format,scale,null,anull \
  --extra-ldflags=-static-libgcc
make -j "${DUALSENSE_BUILD_JOBS:-4}"
make install
mkdir -p "$prefix/source" "$prefix/licenses"
cp "$source_archive" "$prefix/source/"
cp "$repo_dir/scripts/build-ffmpeg-windows.sh" "$prefix/source/"
cp "$source_dir/COPYING.LGPLv2.1" "$prefix/licenses/"
cp config.h config_components.h ffbuild/config.mak "$prefix/source/"
printf '%s\n' 'FFmpeg n7.1.5, commit 3a0867c2bfda4a4d4309ca1a8cbdc6175e67f587. Unmodified source and reproducible build script are included.' > "$prefix/source/README.txt"
