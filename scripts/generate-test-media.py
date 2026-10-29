#!/usr/bin/env python3
"""Generate single-file four-channel acceptance fixtures; intermediate WAVs are temporary."""
import argparse
import math
from pathlib import Path
import struct
import subprocess
import tempfile
import wave
import zipfile


def create(output: Path, scenario: str, duration: int, ffmpeg: str) -> None:
    with tempfile.TemporaryDirectory(prefix="dualsense-fixture-") as temporary:
        pcm = Path(temporary) / "four-channels.wav"
        with wave.open(str(pcm), "wb") as audio:
            audio.setnchannels(4)
            audio.setsampwidth(2)
            audio.setframerate(48000)
            for start in range(0, duration * 48000, 4800):
                block = bytearray()
                for sample in range(start, min(start + 4800, duration * 48000)):
                    t = sample / 48000
                    values = []
                    for channel, frequency in enumerate((440, 550, 110, 180)):
                        if scenario == "channel-check":
                            phase = t - channel * 2 - .25
                            active = 0 <= phase < 1.5
                            envelope = max(0, min(1, phase / .01, (1.5 - phase) / .01)) if active else 0
                        elif scenario == "sync-check":
                            phase = t % 1
                            active = t >= 1 and phase < .1
                            envelope = max(0, min(1, phase / .005, (.1 - phase) / .005)) if active else 0
                        else:
                            envelope = 1
                        amplitude = .25 if channel < 2 else .6
                        values.append(round(32767 * amplitude * envelope * math.sin(2 * math.pi * frequency * t)))
                    block += struct.pack("<4h", *values)
                audio.writeframesraw(block)
        font = Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
        font_option = f"fontfile={font}:" if font.exists() else ""
        video_filter = (
            "drawbox=x=0:y=0:w=iw:h=ih:color=white:t=fill:enable='gte(t,1)*lt(mod(t,1),0.1)'"
            if scenario in ("sync-check", "loop-check") else
            ",".join(f"drawtext={font_option}text='CH {channel + 1}':fontsize=64:fontcolor=white:x=(w-tw)/2:y=(h-th)/2:enable='between(t,{channel * 2},{channel * 2 + 2})'" for channel in range(4))
        )
        for extension, codec in (("mov", "pcm_s24le"), ("mp4", "aac")):
            target = output / f"{scenario}.{extension}"
            subprocess.run([
                ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", f"color=c=0x101820:s=640x360:r=30:d={duration}",
                "-i", str(pcm), "-map", "0:v:0", "-map", "1:a:0", "-vf", video_filter,
                "-c:v", "libx264", "-threads", "2", "-preset", "fast", "-pix_fmt", "yuv420p",
                "-c:a", codec, "-channel_layout", "quad", *(["-b:a", "512k"] if codec == "aac" else []),
                "-movflags", "+faststart", str(target)
            ], check=True)
            print(target)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=Path("artifacts/test-media"))
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--archive", type=Path, help="Also package the six fixtures for the Windows Git installer")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    create(args.output, "channel-check", 8, args.ffmpeg)
    create(args.output, "sync-check", 30, args.ffmpeg)
    create(args.output, "loop-check", 30, args.ffmpeg)
    if args.archive:
        args.archive.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(args.archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for scenario in ("channel-check", "loop-check", "sync-check"):
                for extension in ("mov", "mp4"):
                    name = f"{scenario}.{extension}"
                    archive.write(args.output / name, "test-media/" + name)
        print(args.archive)


if __name__ == "__main__":
    main()
