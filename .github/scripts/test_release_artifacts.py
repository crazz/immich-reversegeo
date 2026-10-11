import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("release_artifacts", Path(__file__).with_name("release-artifacts.py"))
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
SHA = "a" * 40


class ReleaseArtifactsTests(unittest.TestCase):
    def test_branch_without_version_is_explicitly_a_dev_build(self):
        metadata = release.build_metadata("", "branch", "master", SHA, "refs/heads/master")
        self.assertEqual(metadata["version"], "0.0.0-dev")
        self.assertEqual(metadata["informationalVersion"], "0.0.0-dev+sha." + SHA)

    def test_release_tag_and_prerelease_metadata(self):
        self.assertEqual(release.build_metadata("", "tag", "v1.2.3", SHA, "refs/tags/v1.2.3")["version"], "1.2.3")
        self.assertEqual(release.build_metadata("v1.2.3-rc.1+build", "branch", "candidate", SHA, "refs/heads/candidate")["informationalVersion"], "1.2.3-rc.1+build.sha." + SHA)

    def test_invalid_versions_and_source_identity_are_rejected(self):
        for version in ["1.2", "01.2.3", "1.2.3-01", "1.2.3-rc.01", "1.2.3-", "1.2.3+", "1.2.3;echo bad", "65535.0.0"]:
            with self.subTest(version=version), self.assertRaises(ValueError):
                release.build_metadata(version, "branch", "candidate", SHA, "refs/heads/candidate")
        with self.assertRaises(ValueError):
            release.build_metadata("1.2.3", "branch", "master", "abc", "refs/heads/master")
        with self.assertRaises(ValueError):
            release.build_metadata("1.2.4", "tag", "v1.2.3", SHA, "refs/tags/v1.2.3")

    def packages(self, directory, embedded=None, omit=None, database=None):
        metadata = release.build_metadata("1.2.3", "branch", "candidate", SHA, "refs/heads/candidate")
        app, docs = directory / "app.zip", directory / "docs.zip"
        with zipfile.ZipFile(app, "w") as archive:
            for name in release.APP_FILES:
                if name == omit:
                    continue
                value = json.dumps(embedded or metadata).encode() if name == "release-build.json" else b"fixture"
                if name.endswith(".db"):
                    value = database or b"SQLite format 3\x00fixture"
                archive.writestr(name, value)
        with zipfile.ZipFile(docs, "w") as archive:
            for name in release.DOC_FILES:
                archive.writestr(name, b"fixture")
        return metadata, app, docs

    def test_complete_packages_produce_checksums_and_source_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            metadata, app, docs = self.packages(directory)
            artifacts = release.verify_packages(metadata, app, docs, directory / "verification")
            self.assertEqual(len(artifacts), 2)
            self.assertTrue(all(len(item["sha256"]) == 64 for item in artifacts))
            manifest = json.loads((directory / "verification/release-manifest.json").read_text())
            self.assertEqual(manifest["sourceCommit"], SHA)
            self.assertIn(artifacts[0]["sha256"], (directory / "verification/SHA256SUMS").read_text())

    def test_stale_package_missing_data_and_lfs_pointers_fail_verification(self):
        for mode in ["stale", "missing", "lfs"]:
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as directory:
                directory = Path(directory)
                kwargs = {"embedded": {"sourceCommit": "b" * 40}} if mode == "stale" else {"omit": "bundled-data/iso3166.json"} if mode == "missing" else {"database": b"version https://git-lfs.github.com/spec/v1"}
                metadata, app, docs = self.packages(directory, **kwargs)
                with self.assertRaises(ValueError):
                    release.verify_packages(metadata, app, docs, directory / "verification")

    def test_unsafe_or_empty_archive_members_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.zip"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("../outside", b"bad")
            with self.assertRaises(ValueError):
                release.verify_archive(path, [])
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("index.html", b"")
            with self.assertRaises(ValueError):
                release.verify_archive(path, ["index.html"])


if __name__ == "__main__":
    unittest.main()
