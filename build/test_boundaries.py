"""Check extraction boundaries and release contracts without building applications."""
from pathlib import Path
import unittest
import defusedxml.ElementTree as ET
import yaml

ROOT = Path(__file__).resolve().parents[1]

class Boundaries(unittest.TestCase):
    def workflow(self):
        with (ROOT / ".github/workflows/ci.yml").open(encoding="utf-8") as stream:
            return yaml.safe_load(stream)

    def test_project_references_stay_inside_tools(self):
        for project in ROOT.rglob("*.csproj"):
            if any(p in project.relative_to(ROOT).parts for p in ("obj", "bin", ".artifacts", ".worktrees")):
                continue
            for ref in ET.parse(project).iter("ProjectReference"):
                target = (project.parent / ref.attrib["Include"].replace("\\", "/")).resolve()
                self.assertTrue(target.is_relative_to(ROOT), str(project))
                self.assertTrue(target.is_file(), str(target))

    def test_library_package_dependencies_are_pinned(self):
        for project in ROOT.rglob("*.csproj"):
            if any(p in project.relative_to(ROOT).parts for p in ("obj", "bin", ".artifacts", ".worktrees")):
                continue
            for ref in ET.parse(project).iter("PackageReference"):
                name = ref.attrib.get("Include", "")
                if name == "DubUrl" or name.startswith("DubUrl."):
                    versions = (("[$(DubUrlProviderTestingVersion)]",) if name == "DubUrl.ProviderTesting"
                                else ("[$(DubUrlVersion)]", "[$(SchemaPackageVersion)]"))
                    self.assertIn(ref.attrib["Version"], versions, str(project))

    def test_job_dependencies_exist(self):
        jobs = self.workflow()["jobs"]
        for name, job in jobs.items():
            needs = job.get("needs", [])
            for dep in ([needs] if isinstance(needs, str) else needs):
                self.assertIn(dep, jobs, name)

    def test_no_pr_or_manual_publication(self):
        jobs = self.workflow()["jobs"]
        for name in ("reserve-release", "publish-application", "publish-cli-nuget", "publish-worker-nuget", "github-release"):
            condition = jobs[name]["if"]
            self.assertIn("github.event_name == 'push'", condition)
            self.assertIn("github.ref == 'refs/heads/main'", condition)
        self.assertIn("needs.version.outputs.patch == '0'", jobs["github-release"]["if"])

    def test_preserves_both_tool_distribution_matrices(self):
        jobs = self.workflow()["jobs"]
        cli = jobs["package-cli-distributions"]["strategy"]["matrix"]["include"]
        worker = jobs["package-worker-distributions"]["strategy"]["matrix"]["include"]
        self.assertEqual(len(cli), 18)
        self.assertEqual(sum(bool(row["installer"]) for row in cli), 2)
        self.assertEqual(len(worker), 6)
        self.assertEqual({row["runtime"] for row in worker}, {row["runtime"] for row in cli})
        for job in ("publish-application", "publish-cli-nuget", "publish-worker-nuget"):
            self.assertIn(job, jobs["github-release"]["needs"])

    def test_gitversion_sdk_is_installed_before_tool_installation(self):
        for job in ("version", "reserve-release"):
            with self.subTest(job=job):
                steps = self.workflow()["jobs"][job]["steps"]
                installation = next(i for i, step in enumerate(steps)
                                    if step.get("uses") == "actions/setup-dotnet@v6"
                                    and str(step.get("with", {}).get("dotnet-version")) == "8.0.416")
                gitversion = next(i for i, step in enumerate(steps)
                                  if step.get("uses", "").startswith("gittools/actions/gitversion/setup@"))
                self.assertLess(installation, gitversion, "GitVersion requires the selected SDK first.")

    def test_jobs_install_at_most_one_sdk(self):
        for job, configuration in self.workflow()["jobs"].items():
            steps = [step for step in configuration.get("steps", [])
                     if step.get("uses") == "actions/setup-dotnet@v6"]
            self.assertLessEqual(len(steps), 1, job)
            for step in steps:
                versions = str(step["with"]["dotnet-version"]).splitlines()
                self.assertEqual(len(versions), 1, job)

    def test_tool_packing_keeps_runtime_requirements_separate(self):
        for job in ("package-cli-nuget", "package-worker-nuget"):
            steps = self.workflow()["jobs"][job]["steps"]
            sdk = next(step for step in steps if step.get("uses") == "actions/setup-dotnet@v6")
            self.assertEqual(sdk["with"]["dotnet-version"], "10.0.403")
            self.assertTrue(any("Ensure-DotNetRuntimes.ps1 -Channels '8.0,9.0'" in step.get("run", "")
                                for step in steps), job)

    def test_cli_archives_need_only_the_native_test_runtime(self):
        steps = self.workflow()["jobs"]["package-cli-distributions"]["steps"]
        self.assertFalse(any(step.get("uses") == "actions/setup-dotnet@v6" for step in steps))
        runtime = next(step for step in steps if "Ensure-DotNetRuntimes.ps1" in step.get("run", ""))
        self.assertEqual(runtime["if"], "matrix.native")
        self.assertIn("matrix.framework", runtime["run"])
        self.assertIn("-RuntimeOnly", runtime["run"])

if __name__ == "__main__":
    unittest.main()
