#!/usr/bin/env python3
"""Build an offline win-x64 folder and ZIP; requires a prepared LGPL FFmpeg build."""
import argparse
import hashlib
from pathlib import Path
import shutil
import subprocess
import zipfile


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--ffmpeg", type=Path, required=True, help="FFmpeg prefix containing bin/, licenses/, source/")
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    target = repo / "artifacts" / "DualSensePlayer-win-x64"
    if target.exists():
        raise SystemExit(f"Output already exists: {target}. Move it aside before publishing a new package.")
    for required in ("bin/ffmpeg.exe", "bin/ffprobe.exe", "licenses/COPYING.LGPLv2.1", "source/ffmpeg-7.1.5-source.tar.gz"):
        if not (args.ffmpeg / required).is_file():
            raise SystemExit(f"Missing required decoder distribution file: {required}")
    for project in ("DualSensePlayer.App", "DualSensePlayer.Supervisor"):
        subprocess.run([args.dotnet, "publish", str(repo / "src" / project), "-c", "Release", "-r", "win-x64", "--self-contained", "true",
                        "--no-restore", "-p:PublishSingleFile=false", "-p:PublishReadyToRun=false", "-o", str(target)], check=True)
    decoder = target / "tools" / "ffmpeg"
    shutil.copytree(args.ffmpeg / "bin", decoder)
    shutil.copytree(args.ffmpeg / "licenses", decoder / "licenses")
    shutil.copytree(args.ffmpeg / "source", decoder / "source")
    shutil.copytree(repo / "docs", target / "docs")
    shutil.copy2(repo / "README.md", target / "README.md")
    shutil.copy2(repo / "THIRD-PARTY-NOTICES.md", target / "THIRD-PARTY-NOTICES.md")
    shutil.copytree(repo / "licenses", target / "licenses")
    for report in ("validation.txt", "validation-minimal-decoder.txt"):
        if (repo / "artifacts" / report).is_file():
            shutil.copy2(repo / "artifacts" / report, target / report)
    if (repo / "artifacts" / "test-media").exists():
        shutil.copytree(repo / "artifacts" / "test-media", target / "test-media")
    (target / "START-HERE.txt").write_text("DualSensePlayer.Supervisor.exe を起動してください。操作手順: docs/OPERATIONS.md\nWindows 11 x64 / USB DualSense 2台。初回は25%で左右を確認してください。\n", encoding="utf-8-sig")
    paths = sorted(p for p in target.rglob("*") if p.is_file())
    manifest = []
    for path in paths:
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        manifest.append(f"{digest}  {path.relative_to(target).as_posix()}")
    (target / "SHA256SUMS.txt").write_text("\n".join(manifest) + "\n", encoding="utf-8")
    archive_path = target.with_suffix(".zip")
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in target.rglob("*"):
            if path.is_file(): archive.write(path, Path(target.name) / path.relative_to(target))
    print(archive_path)


if __name__ == "__main__":
    main()
