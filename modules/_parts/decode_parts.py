#!/usr/bin/env python3
"""Decode zlib+base64 payloads into Settings module sources."""
import base64, zlib, pathlib

ROOT = pathlib.Path(__file__).resolve().parent
OUT = ROOT.parent

def load_joined(prefix: str) -> bytes:
    a = (ROOT / f"{prefix}.a").read_text().strip()
    b = (ROOT / f"{prefix}.b").read_text().strip()
    return zlib.decompress(base64.b64decode(a + b))

def write(dest: pathlib.Path, data: bytes) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_bytes(data)
    print(f"{dest} ({len(data)} bytes)")

if __name__ == "__main__":
    write(OUT / "WinOptimizer.Settings" / "SettingsViewModel.cs",
          load_joined("SettingsViewModel.cs.zlib.b64"))
    print("OK: SettingsViewModel.cs")
    # SettingsPage / MainWindowPages: copy from artifacts if parts missing
    sp_a = ROOT / "SettingsPage.xaml.zlib.b64.a"
    if sp_a.exists():
        write(OUT / "WinOptimizer.Settings" / "Views" / "SettingsPage.xaml",
              load_joined("SettingsPage.xaml.zlib.b64"))
    mwp_a = ROOT / "MainWindowPages.xaml.zlib.b64.a"
    if mwp_a.exists():
        write(OUT.parent / "Views" / "MainWindowPages.xaml",
              load_joined("MainWindowPages.xaml.zlib.b64"))
