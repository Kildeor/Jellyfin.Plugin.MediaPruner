#!/usr/bin/env python3
"""Helpers for the release workflow.

  meta     <version> <changelog-file> <timestamp> <target-abi> <out>
      Write the meta.json shipped inside the plugin zip.
  manifest <version> <changelog-file> <timestamp> <target-abi> <checksum> <source-url>
      Add (or replace) the version in manifest.json, newest first.
"""
import json
import sys
from pathlib import Path

MANIFEST = Path("manifest.json")


def load_plugin():
    data = json.loads(MANIFEST.read_text(encoding="utf-8"))
    return data, data[0]


def read_changelog(path):
    text = Path(path).read_text(encoding="utf-8").strip()
    return text or "No changelog provided."


def cmd_meta(version, changelog_file, timestamp, target_abi, out):
    _, plugin = load_plugin()
    meta = {
        "category": plugin["category"],
        "changelog": read_changelog(changelog_file),
        "description": plugin["description"],
        "guid": plugin["guid"],
        "name": plugin["name"],
        "overview": plugin["overview"],
        "owner": plugin["owner"],
        "targetAbi": target_abi,
        "timestamp": timestamp,
        "version": version,
        "status": "Active",
        "autoUpdate": False,
        "assemblies": [],
    }
    Path(out).write_text(json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def cmd_manifest(version, changelog_file, timestamp, target_abi, checksum, source_url):
    data, plugin = load_plugin()
    entry = {
        "version": version,
        "changelog": read_changelog(changelog_file),
        "targetAbi": target_abi,
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": timestamp,
    }
    versions = [v for v in plugin.get("versions", []) if v.get("version") != version]
    versions.append(entry)
    versions.sort(key=lambda v: tuple(int(p) for p in v["version"].split(".")), reverse=True)
    plugin["versions"] = versions
    MANIFEST.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    commands = {"meta": (cmd_meta, 5), "manifest": (cmd_manifest, 6)}
    if len(sys.argv) < 2 or sys.argv[1] not in commands or len(sys.argv) - 2 != commands[sys.argv[1]][1]:
        sys.exit(__doc__)
    func, _ = commands[sys.argv[1]]
    func(*sys.argv[2:])
