#!/usr/bin/env python3
"""Generate a complete PowerShell paste installer without publishing source externally.

Run on Linux with libbrotlienc installed (provided by the managed cloud image).
The generated script downloads a SHA-512-verified Microsoft SDK and a SHA-256-
verified Gyan FFmpeg build. It embeds all application source and NuGet lockfiles.
"""
import base64
import ctypes
import ctypes.util
import hashlib
import json
from pathlib import Path
import textwrap

repo = Path(__file__).resolve().parents[1]
paths = sorted(p for p in (repo / "src").rglob("*") if p.is_file()
               and p.suffix in (".cs", ".xaml", ".csproj", ".manifest")
               and not any(part in ("bin", "obj") for part in p.parts))
paths += sorted((repo / "src").glob("*/packages.lock.json"))
paths += [repo / "global.json", repo / "Directory.Build.props"]
contents = {p.relative_to(repo).as_posix(): p.read_text(encoding="utf-8") for p in paths}
raw = json.dumps(contents, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
library = ctypes.util.find_library("brotlienc")
if library is None:
    raise SystemExit("libbrotlienc is required to generate the paste installer.")
encoder = ctypes.CDLL(library).BrotliEncoderCompress
encoder.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_size_t,
                    ctypes.c_void_p, ctypes.POINTER(ctypes.c_size_t), ctypes.c_void_p]
encoder.restype = ctypes.c_int
length = ctypes.c_size_t(len(raw) + 1024)
buffer = ctypes.create_string_buffer(length.value)
if not encoder(11, 22, 1, len(raw), raw, ctypes.byref(length), buffer):
    raise SystemExit("Brotli compression failed")
payload = buffer.raw[:length.value]
template = (repo / "scripts/paste-installer.ps1.in").read_text(encoding="utf-8")
script = template.replace("@@PAYLOAD@@", "\n".join(textwrap.wrap(base64.b64encode(payload).decode(), 120)))
script = script.replace("@@PAYLOAD_SHA256@@", hashlib.sha256(payload).hexdigest())
output = repo / "artifacts/Install-DualSensePlayer.ps1"
output.parent.mkdir(exist_ok=True)
output.write_text(script, encoding="utf-8-sig")  # Windows PowerShell 5.1 reads Japanese correctly.
output.with_suffix(".ps1.sha256").write_text(hashlib.sha256(output.read_bytes()).hexdigest() + "  " + output.name + "\n")
print(f"{output}: {len(contents)} source files, {len(payload)} compressed bytes")
