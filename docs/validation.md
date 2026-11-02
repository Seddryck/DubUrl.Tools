# Validation

Validation ran on Windows on 2 November 2026 using SDK 10.0.403 and the installed .NET 8, 9 and 10 runtimes. Tools restored candidate Core 0.56.0 packages from an ignored local NuGet feed, alongside published Schema 0.55.1 packages. There are no cross-repository project references.

## Builds and tests

`dotnet restore DubUrl.Tools.sln` with the isolated candidate-feed configuration, followed by `dotnet build DubUrl.Tools.sln -c Release --no-restore`, passed for all three target frameworks.

For each framework in `net8.0`, `net9.0`, and `net10.0`, the following suites passed:

| Command (append `-c Release -f <framework> --no-build --no-restore`) | Passed | Prerequisite-gated skips |
| --- | --- | --- |
| `dotnet test DubUrl.Application.Testing` | 226 | 0 |
| `dotnet test DubUrl.Cli.Testing` | 312 | 18 |
| `dotnet test DubUrl.Worker.Testing` | 27 | 0 |
| `dotnet test QA/DubUrl.Provider.Testing/DubUrl.Tools.QA.Testing.csproj` | 62 | 120 |

CLI tests use an isolated default connector store. The QA adapter reuses native Core contracts and provisions its own SQLite database; other provider/authentication tests retain their opt-in prerequisites.

- `python -m unittest discover -s build -p test_boundaries.py`: 6 passed.
- `python build/test_gitversion.py --gitversion <GitVersion 6.6.0 executable>`: 6 passed against real temporary Git histories.
- `./build/Test-ReleaseGuards.ps1` and `./build/Test-ReleaseAssets.ps1`: passed.
- actionlint and PowerShell AST parsing: both changed workflows and embedded command scripts passed.

## Packages and runtime smoke

- Packed multi-target `DubUrl.Application` with symbols and validated its frameworks, version and library dependencies.
- `./distribution/build-distribution.ps1 -Mode Pack -Version 1.2.0`: seven CLI packages validated, covering the pointer and all six RID packages with all three TFMs.
- `dotnet pack DubUrl.Worker -c Release --no-restore -p:Version=1.2.0` plus `Test-CliToolPackages.ps1` with worker identity: seven worker packages validated.
- Generated the net10.0/win-x64 CLI archive and self-contained worker archive using the retained distribution scripts.
- Installed both pointer packages into fresh tool directories, ran both version commands, and verified the installed Core assembly is 0.56.0.0.
- The packaged CLI listed all 25 entries from the pinned published 0.55.0 official connector catalog.
- The extracted worker archive passed a persistent stdin/stdout JSON-RPC session: capabilities, schema validation, SQLite rendering and graceful shutdown with exit code zero.

## Not run locally

No Linux/macOS native execution, Windows Arm64 execution, Windows installer compilation, full external-provider/container QA, interactive OAuth/certificate acceptance, native credential-store/terminal opt-ins, NuGet publication or GitHub Release publication was run locally. Existing platform matrices and relevant native archive checks are retained in CI. Publication jobs consume validated artifacts and are gated to main pushes; GitHub Releases additionally require patch zero.

The GitHub `release` environment has been created with a main-only deployment policy. NuGet trusted-publishing policies and publication of the pinned Core packages are prerequisites to merging this repository's extraction PR. See [the cutover guide](extraction.md).

CI follow-up: moved SDK 8.0.416 installation before GitVersion setup. A regression check demonstrated that the original ordering failed and the corrected ordering passed; all six boundary checks and actionlint pass.
