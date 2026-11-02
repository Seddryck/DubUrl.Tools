# SDK and runtime audit

The Tools CI workflow installs at most one SDK per job. Target frameworks in a package do not each require their own SDK.

| Jobs | SDK | Additional runtimes | Reason |
| --- | --- | --- | --- |
| Version calculation and release-tag reservation | 8.0.416 | None | GitVersion.Tool runs on .NET 8 and its installation needs an SDK |
| Framework build/test matrix | Matching 8, 9 or 10 SDK, one per row | None | Explicitly validates the selected SDK/framework; each SDK includes its matching runtime |
| Linux host tests | 10.0.403 | None | Compiles and tests net10.0 |
| CLI and worker NuGet packaging | 10.0.403 | 8.0 and 9.0 if absent | SDK 10 compiles all three TFMs; the catalog generator executables still target and run on net8.0/net9.0 during packing |
| Application library packaging | 10.0.403 | None | Restores package metadata and packs transferred tested binaries without rebuilding or executing older targets |
| Worker self-contained distributions | 10.0.403 | None | Publishes net10.0 for each RID; the archived executable carries its runtime |
| CLI archives/installers | None | Matching framework runtime only for native smoke rows | Archiving, icon validation and Inno Setup use existing publish outputs; cross-architecture/musl rows perform no managed execution |
| NuGet publication | 10.0.403 | None | Uses dotnet nuget push; tool installation/smoke checks select the net10.0 payload |
| Coverage, catalog download, release-asset validation and GitHub Release publication | None | None | Uses existing artifacts with PowerShell, native tools or the Codecov action |

`build/Ensure-DotNetRuntimes.ps1` reuses installed stable Microsoft.NETCore.App runtimes and downloads only missing runtime components using Microsoft's dotnet-install script. Packaging keeps runtimes alongside its selected SDK; archive-only jobs use a private runner runtime root if installation is necessary. SDK selection is left to the explicit global.json selectors. ASP.NET runtimes, SDK version lines and preview runtimes do not satisfy a stable Microsoft.NETCore.App requirement.

The audit also supplied explicit SDK setup for release-tag reservation, which invokes GitVersion independently of the initial version job.

Validation: 9 workflow/boundary tests, 8 runtime-planning scenarios, actionlint and PowerShell syntax checks. Local packaging uses SDK 10.0.403; SDK 8 and SDK 9 are not installed on the validation host. The catalog runtimeconfig files explicitly require Microsoft.NETCore.App 8.0/9.0. No machine-wide SDK/runtime installation was performed locally; existing runtimes were reused. Full application/provider suites were not rerun for this workflow-only change.

The CLI and worker package locks were also regenerated in an isolated package cache using NuGet.org after Core 0.56.0 publication. This replaces candidate-package hashes with the published package hashes while retaining locked-mode validation and all dependency versions. Both locked restores pass.

Packaging verification: CLI and worker each produced all seven expected packages with SDK 10.0.403 and passed the existing package-content validator. Both locked restores and the net10.0 solution build passed after refreshing the public package hashes.
