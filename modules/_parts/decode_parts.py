#!/usr/bin/env python3
"""Decode zlib+base64 payloads into Settings module sources."""
import base64, zlib, pathlib

ROOT = pathlib.Path(__file__).resolve().parent
OUT = ROOT.parent

def decode(name: str, dest: pathlib.Path) -> None:
    raw = (ROOT / name).read_text().strip()
    data = zlib.decompress(base64.b64decode(raw))
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_bytes(data)
    print(f"{dest} ({len(data)} bytes)")

if __name__ == "__main__":
    decode("SettingsViewModel.cs.zlib.b64", OUT / "WinOptimizer.Settings" / "SettingsViewModel.cs")
    decode("SettingsPage.xaml.zlib.b64", OUT / "WinOptimizer.Settings" / "Views" / "SettingsPage.xaml")
    decode("MainWindowPages.xaml.zlib.b64", OUT.parent / "Views" / "MainWindowPages.xaml")
