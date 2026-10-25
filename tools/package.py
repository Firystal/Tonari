"""Build the single distribution tree and ZIP from an explicit source allowlist."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import uuid
import zipfile
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
PACKAGE = DIST / "Tonari"
ARCHIVE = DIST / "Tonari.zip"
SOURCE_FILES = [
    "README.md", "LICENSE", "CONTRIBUTING.md", ".editorconfig", ".gitattributes", ".gitignore", "package.json", "package-lock.json",
    ".github/workflows/ci.yml", ".github/ISSUE_TEMPLATE/bug_report.yml",
    ".github/ISSUE_TEMPLATE/feature_request.yml", ".github/ISSUE_TEMPLATE/config.yml",
    "src/Program.cs", "src/MainWindow.xaml", "src/RoomConfig.cs", "src/ZeroTierSession.cs",
    "src/SetupActions.cs", "src/RuntimeSupport.cs", "src/NetworkDiagnostics.cs",
    "src/GameLauncher.cs", "src/GameFilePreparation.cs", "src/GameTestRole.cs", "src/UI-Roles.md",
    "src/build.ps1", "src/launcher.manifest", "src/app.ico",
    "src/patches/connect_compat.dll", "src/patches/daemon_registry_fix.dll",
    "src/tests/BackendTests.cs", "src/tests/GameLauncherTests.cs", "src/tests/GameFilePreparationTests.cs",
    "src/tests/GameTestRoleTests.cs", "src/tests/ZeroTierReadOnlyTests.cs", "src/tests/UiFlowTests.cs",
    "src/tests/run-tests.ps1", "src/tests/run-game-launcher-tests.ps1", "src/tests/run-preparation-tests.ps1",
    "src/tests/run-test-role-tests.ps1", "src/tests/run-zerotier-readonly-tests.ps1",
    "src/tests/run-ui-tests.ps1", "src/tests/TestPaths.ps1",
    "01-基准机从零建房教程.md", "02-接入机从零加入教程.md",
    "tools/package.py", "tools/deploy.ps1", "tools/render-guides.mjs", "tools/verify-guides.mjs",
]
NATIVE_FILES = [
    "src/patches/connect/build.ps1", "src/patches/connect/connect_core.h",
    "src/patches/connect/connect_probe.c", "src/patches/connect/connect_signature.h",
    "src/patches/connect/connect_targets.h", "src/patches/connect/make-ordinal-fixture.py",
    "src/patches/connect/README.md", "src/patches/connect/test-native.ps1",
    "src/patches/connect/test_core.c", "src/patches/connect/test_native_fixture.c", "src/patches/connect/test_signature.c",
    "src/patches/distribution/build.ps1", "src/patches/distribution/daemon_registry_fix.c",
    "src/patches/distribution/README.md", "src/patches/distribution/registry_signature.h",
    "src/patches/distribution/sha256_local.h", "src/patches/distribution/test_registry_fix.c",
]


def checked(path, boundary):
    path = Path(os.path.abspath(path))
    boundary = Path(os.path.abspath(boundary))
    try:
        relative = path.relative_to(boundary)
    except ValueError as error:
        raise ValueError(f"Path is outside {boundary}: {path}") from error
    current = boundary
    for part in ("", *relative.parts):
        current = current / part
        try:
            attributes = current.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(attributes.st_mode) or getattr(attributes, "st_file_attributes", 0) & 0x400:
            raise ValueError(f"Path cannot contain a link or reparse point: {current}")
    return path


def checked_tree(directory):
    checked(directory, ROOT)
    for parent, directories, files in os.walk(directory, followlinks=False):
        for name in directories + files:
            checked(Path(parent) / name, ROOT)


def digest(path):
    with path.open("rb") as stream:
        result = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
        return result.hexdigest().upper()


def version():
    match = re.search(r'AssemblyFileVersion\("(\d+\.\d+\.\d+)\.\d+"\)', (ROOT / "src/Program.cs").read_text(encoding="utf-8-sig"))
    if not match:
        raise ValueError("Application version was not found in src/Program.cs.")
    return match[1]


def release_readme(staging):
    targets = {
        "01-基准机从零建房教程.md": "基准机教程.html",
        "02-接入机从零加入教程.md": "接入机教程.html",
        "README.md": "README.md",
        "LICENSE": "LICENSE",
    }

    def replace_link(match):
        destination = match[2]
        parsed = urlsplit(destination)
        if parsed.scheme or parsed.netloc or not parsed.path:
            return match[0]
        relative = unquote(parsed.path).removeprefix("./")
        target = targets.get(relative, "source/" + relative)
        if not checked(staging / target, staging).exists():
            raise FileNotFoundError("README link does not exist in the package: " + destination)
        suffix = ("?" + parsed.query if parsed.query else "") + ("#" + parsed.fragment if parsed.fragment else "")
        return match[1] + target + suffix + match[3]

    text = (ROOT / "README.md").read_text(encoding="utf-8")
    text = text.replace("GitHub 上可通过上表阅读 Markdown 版本。", "Markdown 教程与完整源码位于 `source` 目录。")
    text = text.replace("在项目根目录运行：", "进入发布包的 `source` 目录后运行：")
    return re.sub(r"(\[[^\]]+\]\()([^\s)]+)(\))", replace_link, text)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="validate inputs without changing distribution files")
    args = parser.parse_args()
    inputs = SOURCE_FILES + NATIVE_FILES
    if len(set(name.casefold() for name in inputs)) != len(inputs):
        raise ValueError("Duplicate source allowlist entry.")
    for name in inputs:
        if not checked(ROOT / name, ROOT).is_file():
            raise FileNotFoundError(f"Required source file is missing: {name}")
    executable = checked(PACKAGE / "Tonari.exe", ROOT)
    checked(ARCHIVE, ROOT)
    if not executable.is_file():
        raise FileNotFoundError("Build the application with src/build.ps1 first.")
    app_version = version()
    if args.check:
        print(json.dumps({"version": app_version, "source_files": len(inputs), "exe_sha256": digest(executable)}, ensure_ascii=False))
        return

    checked(DIST, ROOT).mkdir(exist_ok=True)
    checked_tree(PACKAGE)
    tag = uuid.uuid4().hex
    staging = checked(DIST / (".package-" + tag), DIST)
    temporary_zip = checked(DIST / (".package-" + tag + ".zip"), DIST)
    staging.mkdir()
    try:
        shutil.copy2(executable, staging / "Tonari.exe")
        for name in inputs:
            destination = checked(staging / "source" / name, staging)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / name, destination)
        shutil.copy2(ROOT / "LICENSE", staging / "LICENSE")
        subprocess.run(["node", str(ROOT / "tools/render-guides.mjs"), str(staging)], cwd=ROOT, check=True)
        (staging / "README.md").write_text(release_readme(staging), encoding="utf-8", newline="\n")
        manifest = [
            {"Path": path.relative_to(staging).as_posix(), "Length": path.stat().st_size, "SHA256": digest(path)}
            for path in sorted(staging.rglob("*")) if path.is_file()
        ]
        (staging / "SHA256-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        with zipfile.ZipFile(temporary_zip, "w", zipfile.ZIP_DEFLATED) as archive:
            for path in sorted(staging.rglob("*")):
                if path.is_file():
                    archive.write(path, "Tonari/" + path.relative_to(staging).as_posix())
        with zipfile.ZipFile(temporary_zip) as archive:
            if archive.testzip() is not None:
                raise ValueError("ZIP CRC validation failed.")
            for item in manifest:
                content = archive.read("Tonari/" + item["Path"])
                if len(content) != item["Length"] or hashlib.sha256(content).hexdigest().upper() != item["SHA256"]:
                    raise ValueError("ZIP content validation failed: " + item["Path"])
        # Only the fixed generated distribution directory is replaced.
        checked_tree(PACKAGE)
        if PACKAGE != ROOT / "dist" / "Tonari":
            raise ValueError("Unexpected package destination.")
        shutil.rmtree(PACKAGE)
        staging.replace(PACKAGE)
        checked(ARCHIVE, DIST)
        temporary_zip.replace(ARCHIVE)
        print(json.dumps({"version": app_version, "files_verified": len(manifest), "exe_sha256": digest(PACKAGE / "Tonari.exe"), "zip_sha256": digest(ARCHIVE)}, ensure_ascii=False))
    finally:
        if staging.exists():
            checked_tree(staging)
            shutil.rmtree(staging)
        if temporary_zip.exists():
            checked(temporary_zip, DIST).unlink()


if __name__ == "__main__":
    main()
