"""Check extension discovery/registration with Sea Power's Mono, without starting the game."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
ORDERS = (
    "extension-main-api", "extension-api-main", "main-extension-api",
    "main-api-extension", "api-extension-main", "api-main-extension", "disabled",
)


def host_path(path):
    value = str(path)
    if os.name == "nt":
        return value
    if value.startswith("/mnt/") and len(value) > 7 and value[6] == "/":
        return value[5].upper() + ":\\" + value[7:].replace("/", "\\")
    raise ValueError("A Windows-mounted repository path is required: " + value)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", help="Override SeaPowerDir from Directory.Build.local.props.")
    parser.add_argument("--anchorchain-dir", help="Override AnchorChainDir from Directory.Build.local.props.")
    args = parser.parse_args()
    properties = ROOT / "Directory.Build.local.props"
    config = {element.tag: element.text for element in ET.parse(properties).getroot().iter()
              if element.text and element.text.strip()} if properties.exists() else {}
    game = args.game_dir or config.get("SeaPowerDir")
    anchorchain = args.anchorchain_dir or config.get("AnchorChainDir")
    if not game or not anchorchain:
        parser.error("Configure Directory.Build.local.props or pass both directory overrides.")
    powershell = shutil.which("powershell.exe")
    if powershell is None:
        powershell = str(Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe") if os.name == "nt" else "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
    for order in ORDERS:
        # A fresh process ensures an earlier check cannot accidentally preload the SDK.
        result = subprocess.run([powershell, "-NoProfile", "-File", host_path(ROOT / "tools/test_extension_loading.ps1"),
                                 "-GameDirectory", game, "-AnchorChainDirectory", anchorchain, "-Order", order],
                                capture_output=True, timeout=45)
        sys.stdout.write(result.stdout.decode("utf-8-sig", errors="replace"))
        sys.stderr.write(result.stderr.decode("utf-8-sig", errors="replace"))
        if result.returncode:
            return result.returncode
    print("PASS all 6 discovery orders and inactive-host registration")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
