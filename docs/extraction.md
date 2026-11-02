# Extraction and release ownership

Imported from Seddryck/DubUrl commit `63e5ff87` (the schema extraction is merged), for [issue #1789](https://github.com/Seddryck/DubUrl/issues/1789). Earlier history remains available in that repository.

## Ownership

| Area | Repository |
| --- | --- |
| Core, providers, native adapters, authentication, provider QA and connector pack/catalog publication | DubUrl |
| Schema model, document formats, rendering, exported Schema documentation | DubUrl.Schema |
| Application workflows, CLI, worker, application integration tests, tool distributions and catalog signing host | DubUrl.Tools |

Tools uses package references to the libraries; neither library repository depends on Tools. Core retains the justified `InternalsVisibleTo("DubUrl.Application")` grant for provider-pack lifecycle orchestration. Moving the assembly does not broaden this grant. Tools integration tests use public library APIs; native test hooks remain in provider QA.

Core/provider QA now inherits a library-only authentication hierarchy. CLI consumer contracts live in Tools' QA adapter and reuse the native contracts/fixtures from the pinned `DubUrl.ProviderTesting` package. A friend-assembly grant is restricted to `DubUrl.Tools.ProviderTesting`; no native fixture implementation is duplicated in Tools. `LiveCliTests` provisions a local SQLite fixture by default and can execute against an external provisioned fixture when `DUBURL_TOOLS_QA_URL` is set, with `DUBURL_TOOLS_QA_TABLE` optionally selecting the table. Credentials belong in the test environment, never source or logs. Default CI runs the contract inventory, local SQLite contracts and CLI's isolated provider tests; remote database, OS credential-store, terminal and OAuth acceptance tests require their existing prerequisites.

Provider packs and their official catalog remain Core release assets: their version describes the provider binaries, not the Tools host. Application builds its default metadata from pinned provider packages plus the connector matrix snapshot. Update that snapshot when adopting provider catalog changes. The CLI includes the pinned official v0.55.0 release catalog for installable packs, independently of the v0.56.0 library dependency. Signing orchestration is in `connectors/Signing`; trusted catalog contracts and provider-pack storage remain library APIs.

## Coordinated cutover

1. Merge the companion Core cleanup first. It stops publishing Application, CLI and worker, preserves provider/catalog publication, and adds the missing authentication and reusable provider QA packages. Publish the complete library package set at the pinned `DubUrlVersion` (initially `0.56.0`).
2. Verify every pinned package is available from NuGet.org and the pinned `ConnectorCatalogVersion` catalog is available from Core's GitHub Releases. Candidate validation may use a temporary local NuGet feed; that feed is not a release dependency and is never committed.
3. Configure this repository's `release` environment and NuGet trusted-publishing policies for Application, both tool pointer packages, and their six RID package families. Reuse existing package ownership; do not rename package IDs or run both repositories' publishers for the same IDs. Enable the Codecov OIDC integration if coverage upload is required.
4. Merge Tools only after the dependency packages and publishing configuration are ready. GitVersion seeds Tools at `1.2.0`, above the existing tool/Application `1.1.0` releases. Validate that baseline against NuGet immediately before cutover; advance it if another release has appeared.
5. Confirm a clean restore, package-only smoke tests, and both distribution channels. Update links and consumers. Keep the old release assets available for existing download URLs.

## CI and release contract

The workflow and GitVersion history tests are adapted from [DubUrl.Schema CI](https://github.com/Seddryck/DubUrl.Schema/blob/main/.github/workflows/ci.yml).

| Dimension | Evidence and decision |
| --- | --- |
| Managed build/test | Retain net8.0, net9.0, net10.0 from the original projects; use matching SDKs for framework jobs |
| CLI distribution | Retain 18 framework/runtime archives and two net10.0 Windows installers |
| Worker distribution | Retain six net10.0 self-contained archives; no new worker installer |
| NuGet | Preserve DubUrl-cli, DubUrl.Worker and six RID variants each; publish multi-target DubUrl.Application separately |
| Native smoke | Execute native Windows x64, Linux x64 and both macOS architectures; preserve cross-publication of Windows Arm64 and musl |
| Version | One GitVersion output for all Tools deliverables, independent of library versions; full history and v-prefixed tags |
| Publishing | Main pushes publish NuGet after validation/tag reservation; patch-zero versions also publish GitHub Releases |
| Manual compatibility | Optional published DubUrl/Schema version overrides build/test/package only; no tags or publication |
| Artifacts | Package validators, command smoke tests, exact public asset inventory, SHA256 release manifest, and conflict-safe retries |

Coverage enforcement and upload are separate checks. Application packages reuse tested framework binaries; CLI packages and their archives share published outputs. Worker archive jobs validate their own publish outputs. Release jobs download validated immutable artifacts and do not rebuild them. Existing assets must match hashes on retry; conflicting assets are never overwritten.

Update dependency pins in a normal PR. Manual compatibility runs accept published candidate versions without changing release pins. Version regression tests exercise first release, patch/minor/major increments, tags and feature branches; release guards reject commit drift, version drift and tag collisions.
