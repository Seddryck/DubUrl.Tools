# DubUrl.Tools

The `duburl` CLI and `duburl-worker` persistent JSON-RPC host share the transport-neutral `DubUrl.Application` library.
Database/provider capabilities come from [DubUrl](https://github.com/Seddryck/DubUrl); schema capabilities come from [DubUrl.Schema](https://github.com/Seddryck/DubUrl.Schema).

```sh
dotnet tool install --global DubUrl-cli
dotnet tool install --global DubUrl.Worker
duburl --help
duburl-worker --version
```

Both tools are distributed through NuGet.org and [GitHub Releases](https://github.com/Seddryck/DubUrl.Tools/releases).
CLI archives cover .NET 8, 9, and 10 on Windows x64/Arm64, Linux x64/glibc and musl, and macOS x64/Arm64. Windows installers target .NET 10.
Worker self-contained archives target .NET 10 on the same six runtimes. CLI and worker retain their existing pointer and RID-specific .NET tool packages.

See [CLI documentation](docs/_docs/cli.md), [worker documentation](DubUrl.Worker/README.md), and [the extraction and release guide](docs/extraction.md).

## Develop

```sh
dotnet restore DubUrl.Tools.sln
dotnet build DubUrl.Tools.sln -c Release -f net10.0 --no-restore
dotnet test DubUrl.Application.Testing -c Release -f net10.0 --no-build --no-restore
dotnet test DubUrl.Cli.Testing -c Release -f net10.0 --no-build --no-restore
dotnet test DubUrl.Worker.Testing -c Release -f net10.0 --no-build --no-restore
```

No sibling checkout is required. Library package versions are pinned in `Directory.Build.props`. `DubUrl.Application` remains a separately published library for other hosts.
