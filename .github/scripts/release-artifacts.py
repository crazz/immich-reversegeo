"""Version and verify reviewable release artifacts; never publish them."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import zipfile

SEMVER = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$")
APP_FILES = (
    "ImmichReverseGeo.Web.dll", "ImmichReverseGeo.Web.deps.json",
    "ImmichReverseGeo.Web.runtimeconfig.json", "ImmichReverseGeo.Web.staticwebassets.endpoints.json",
    "release-build.json", "bundled-data/iso3166.json",
    "bundled-data/defaults/city-resolver-profiles.json", "bundled-data/defaults/OVERTURE-ATTRIBUTION.txt",
    "bundled-data/defaults/overture-airports.db", "bundled-data/defaults/overture-country-divisions.db",
)
DOC_FILES = ("index.html", "installation/index.html", "configuration/index.html", "llms.txt", "installation/index.md")


def parse_version(value):
    version = value.removeprefix("v")
    match = SEMVER.fullmatch(version)
    if not match or any(int(part) > 65534 for part in match.groups()[:3]):
        raise ValueError("Version must be SemVer with .NET-compatible numeric components, optionally prefixed by v.")
    if match[4] and any(part.isdigit() and len(part) > 1 and part.startswith("0") for part in match[4].split(".")):
        raise ValueError("Numeric prerelease identifiers cannot have leading zeros.")
    return version


def build_metadata(requested_version, ref_type, ref_name, commit, source_ref):
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Source commit must be a full lowercase Git SHA.")
    tagged_version = parse_version(ref_name) if ref_type == "tag" else None
    version = parse_version(requested_version) if requested_version else tagged_version or "0.0.0-dev"
    if tagged_version and version != tagged_version:
        raise ValueError("Requested version must match the checked-out release tag.")
    information = version + ("." if "+" in version else "+") + "sha." + commit
    return {"version": version, "informationalVersion": information, "sourceCommit": commit, "sourceRef": source_ref}


def verify_archive(path, required):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)) or any(name.startswith("/") or ".." in PurePosixPath(name).parts for name in names):
            raise ValueError(f"Unsafe or duplicate ZIP paths in {path.name}.")
        for name in required:
            if name not in names or archive.getinfo(name).file_size == 0:
                raise ValueError(f"Missing or empty {name} in {path.name}.")
        broken = archive.testzip()
        if broken:
            raise ValueError(f"Corrupt ZIP member {broken} in {path.name}.")


def verify_packages(metadata, app_path, docs_path, output):
    verify_archive(app_path, APP_FILES)
    verify_archive(docs_path, DOC_FILES)
    with zipfile.ZipFile(app_path) as app:
        embedded = json.loads(app.read("release-build.json"))
        if embedded != metadata:
            raise ValueError("Packaged app metadata does not match the candidate source/version.")
        for name in APP_FILES:
            if name.endswith(".db"):
                with app.open(name) as database:
                    if database.read(16) != b"SQLite format 3\x00":
                        raise ValueError(f"Bundled database {name} is not SQLite; check Git LFS hydration.")
    artifacts = []
    for path in (app_path, docs_path):
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        artifacts.append({"name": path.name, "sizeBytes": path.stat().st_size, "sha256": digest.hexdigest()})
    output.mkdir(parents=True, exist_ok=True)
    (output / "release-manifest.json").write_text(json.dumps({**metadata, "artifacts": artifacts}, indent=2) + "\n", encoding="utf-8")
    (output / "SHA256SUMS").write_text("".join(f"{item['sha256']}  {item['name']}\n" for item in artifacts), encoding="utf-8")
    return artifacts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    metadata_parser = commands.add_parser("metadata")
    metadata_parser.add_argument("--output", type=Path, required=True)
    metadata_parser.add_argument("--github-output", type=Path)
    verify_parser = commands.add_parser("verify")
    verify_parser.add_argument("--metadata", type=Path, required=True)
    verify_parser.add_argument("--app", type=Path, required=True)
    verify_parser.add_argument("--docs", type=Path, required=True)
    verify_parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "metadata":
        metadata = build_metadata(os.getenv("REQUESTED_VERSION", "").strip(), os.getenv("RELEASE_REF_TYPE", "branch"),
                                  os.getenv("RELEASE_REF_NAME", ""), os.environ["SOURCE_COMMIT"], os.getenv("SOURCE_REF", ""))
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        if args.github_output:
            with args.github_output.open("a", encoding="utf-8") as output:
                output.write(f"version={metadata['version']}\ninformational_version={metadata['informationalVersion']}\n")
        print(f"Candidate version: {metadata['version']}; source: {metadata['sourceCommit']}")
    else:
        artifacts = verify_packages(json.loads(args.metadata.read_text(encoding="utf-8-sig")), args.app, args.docs, args.output)
        for artifact in artifacts:
            print(f"Verified {artifact['name']}: {artifact['sizeBytes']} bytes; SHA256 {artifact['sha256']}")


if __name__ == "__main__":
    main()
