"""Exercise the release configuration with real Git histories and GitVersion."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
GITVERSION = None


class GitVersionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.env = {key: value for key, value in os.environ.items()
                    if not key.startswith(("GITHUB_", "BUILD_", "SYSTEM_", "APPVEYOR", "GIT_DIR", "GIT_WORK_TREE"))
                    and key not in ("CI", "TF_BUILD", "TEAMCITY_VERSION")}
        self.git("init", "-b", "main")
        self.git("config", "user.name", "Versioning tests")
        self.git("config", "user.email", "version-tests@example.invalid")
        shutil.copyfile(ROOT / "GitVersion.yml", self.repo / "GitVersion.yml")
        self.git("add", "GitVersion.yml")
        self.git("commit", "-m", "chore: initialize version fixture")

    def git(self, *arguments):
        return subprocess.check_output(["git", *arguments], cwd=self.repo, env=self.env, text=True, stderr=subprocess.STDOUT).strip()

    def commit(self, message):
        self.git("commit", "--allow-empty", "-m", message)

    def version(self):
        output = subprocess.check_output([GITVERSION, "/output", "json", "/nofetch", "/verbosity", "quiet"],
                                         cwd=self.repo, env=self.env, text=True, stderr=subprocess.STDOUT)
        return json.loads(output)["SemVer"]

    def released(self):
        self.git("tag", "v1.2.0")

    def test_fixes_and_maintenance_advance_patch_on_main(self):
        self.released()
        self.commit("fix: preserve schema values")
        self.assertEqual(self.version(), "1.2.1")
        self.git("tag", "v1.2.1")
        self.commit("chore: update schema dependencies")
        self.assertEqual(self.version(), "1.2.2")

    def test_feature_advances_minor_and_next_release_advances_patch(self):
        self.released()
        self.commit("feat: add a schema operation")
        self.assertEqual(self.version(), "1.3.0")
        self.git("tag", "v1.3.0")
        self.commit("fix: handle empty schemas")
        self.assertEqual(self.version(), "1.3.1")

    def test_breaking_change_advances_major(self):
        self.released()
        self.commit("feat!: change the schema contract")
        self.assertEqual(self.version(), "2.0.0")

    def test_explicit_semver_minor_annotation(self):
        self.released()
        self.commit("docs: describe new schema support\n\n+semver: minor")
        self.assertEqual(self.version(), "1.3.0")

    def test_feature_branch_uses_beta_label(self):
        self.released()
        self.git("checkout", "-b", "feat/new-schema")
        self.commit("feat: add new schema support")
        self.assertTrue(self.version().startswith("1.3.0-beta.new-schema."))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gitversion", required=True, help="Path to GitVersion 6.6.0 executable")
    args = parser.parse_args()
    GITVERSION = str(Path(args.gitversion).resolve())
    unittest.main(argv=[__file__])
