"""Check extraction boundaries and release contracts without building applications."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET
import yaml

ROOT = Path(__file__).resolve().parents[1]

class Boundaries(unittest.TestCase):
    def workflow(self):
        with (ROOT / ".github/workflows/ci.yml").open(encoding="utf-8") as stream:
            return yaml.safe_load(stream)

    def test_project_references_stay_inside_tools(self):
        for project in ROOT.rglob("*.csproj"):
            if any(p in project.parts for p in ("obj", "bin", ".artifacts", ".worktrees")):
                continue
            for ref in ET.parse(project).iter("ProjectReference"):
                target = (project.parent / ref.attrib["Include"].replace("\\", "/")).resolve()
                self.assertTrue(target.is_relative_to(ROOT), str(project))
                self.assertTrue(target.is_file(), str(target))

    def test_library_package_dependencies_are_pinned(self):
        for project in ROOT.rglob("*.csproj"):
            if any(p in project.parts for p in ("obj", "bin", ".artifacts", ".worktrees")):
                continue
            for ref in ET.parse(project).iter("PackageReference"):
                name = ref.attrib.get("Include", "")
                if name == "DubUrl" or name.startswith("DubUrl."):
                    self.assertIn(ref.attrib["Version"], ("[$(DubUrlVersion)]", "[$(SchemaPackageVersion)]"), str(project))

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

if __name__ == "__main__":
    unittest.main()
