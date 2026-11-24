"""Build the Thunderstore package zip in dist/.

Usage:  python tools/make_package.py [--no-build] [--readme-only]

The zip holds manifest.json, README.md, CHANGELOG.md, icon.png and the plugin DLL.
The README that goes into the zip is not the file in the repo: every block between
<!-- github-only:start --> and <!-- github-only:end --> is left out. Those blocks are the
download card and the link to the Thunderstore page, which make no sense on that page itself,
and the sections written for GitHub readers (approach, building from source, license).

--readme-only writes just that README to dist/README.thunderstore.md, for pasting into the
package's Edit page on Thunderstore.
"""

import json
import re
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

GITHUB_ONLY = re.compile(
    r"<!-- github-only:start -->.*?<!-- github-only:end -->[ \t]*(?:\r?\n|$)(?:[ \t]*\r?\n)?",
    re.DOTALL,
)


def thunderstore_readme(text: str) -> str:
    """Return the README without its github-only blocks."""
    stripped = GITHUB_ONLY.sub("", text)
    if "<!-- github-only" in stripped:
        sys.exit("README.md has a github-only marker without its partner.")
    if "thunderstore.io" in stripped:
        sys.exit("README.md still links to thunderstore.io outside a github-only block.")
    return stripped


def main() -> None:
    manifest = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8-sig"))
    name = manifest["name"]
    version = manifest["version_number"]
    if len(manifest["description"]) > 250:
        sys.exit("manifest.json description is over Thunderstore's 250 character limit.")

    readme = thunderstore_readme((ROOT / "README.md").read_text(encoding="utf-8"))
    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)

    if "--readme-only" in sys.argv:
        out = dist / "README.thunderstore.md"
        out.write_text(readme, encoding="utf-8", newline="\n")
        print(f"Wrote {out} ({len(readme)} characters).")
        return

    dll = ROOT / "bin" / "Release" / "netstandard2.1" / f"{name}.dll"
    if "--no-build" not in sys.argv:
        subprocess.run(["dotnet", "build", "-c", "Release", "-nologo"], cwd=ROOT, check=True)
    if not dll.is_file():
        sys.exit(f"{dll} is missing. Run without --no-build.")

    out = dist / f"{name}-{version}.zip"
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as package:
        package.write(ROOT / "manifest.json", "manifest.json")
        package.writestr("README.md", readme.encode("utf-8"))
        package.write(ROOT / "CHANGELOG.md", "CHANGELOG.md")
        package.write(ROOT / "icon.png", "icon.png")
        package.write(dll, f"{name}.dll")

    print(f"Wrote {out} ({out.stat().st_size} bytes), version {version}.")


if __name__ == "__main__":
    main()
