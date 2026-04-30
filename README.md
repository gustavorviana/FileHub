# FileHub

[![CI](https://github.com/gustavorviana/FileHub/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/gustavorviana/FileHub/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FileHub.svg)](https://www.nuget.org/packages/FileHub)
[![NuGet Downloads](https://img.shields.io/nuget/dt/FileHub.svg)](https://www.nuget.org/packages/FileHub)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.txt)

> **Heads up:** FileHub is a young library. The API is stabilizing but rough edges are likely — bug reports, feedback, and PRs are very welcome on the [issue tracker](https://github.com/gustavorviana/FileHub/issues).

A .NET storage abstraction. One API (`IFileHub` → `FileDirectory` → `FileEntry`) across local disk, in-memory, and cloud object storage — swap the driver, keep the code.

```csharp
using FileHub.Local;

var hub = new LocalFileHub(@"C:\data");        // or: new MemoryFileHub();
var file = hub.Root.CreateFile("hello.txt");   // anywhere under the sandbox root
file.SetText("hi");

Console.WriteLine(file.ReadAllText());
```

## Why

Most .NET apps end up with `System.IO.File` scattered across services, or coupled to a cloud SDK whose types leak into every layer. When the storage backend has to change — "move uploads to S3", "keep tests off the disk", "sandbox per-tenant data" — the refactor hurts because the boundary was never drawn.

FileHub draws it once. A service like `ReportService(IFileHub hub)` is written once, unit-tested against memory, run locally against disk, and deployed to the cloud against an object store — without a single line changing in the service.

## What's in the box

- **Drivers**: `FileHub.Local` (disk), `FileHub.Memory` (in-process), `FileHub.AmazonS3` (S3), `FileHub.OracleObjectStorage` (OCI), `FileHub.Ftp` (FTP server). Custom drivers implement two abstract classes.
- **Sync + async** on the same types. Async is the source of truth; sync delegates.
- **Sandboxed by default** — every hub has a root. `..`, absolute paths, and symlink escapes are rejected.
- **Read-only on demand** — `dir.AsReadOnly()` / `file.AsReadOnly()` wraps anything and blocks writes at runtime.
- **DI integration** — `FileHub.DependencyInjection` ships `AddFileHub` / `AddNamedFileHubs` with lifetime + `IServiceProvider` support for tenant scoping.
- **Nested paths** — every method that takes a `name` (`CreateFile`/`OpenFile`, `CreateDirectory`/`OpenDirectory`, `FileExists`, `DirectoryExists`, `Delete`) accepts subpaths like `"a/b/c.txt"` and tolerates a trailing `/` or `\`. `/` and `\` are interchangeable as separators. `DirectoryPathMode.Direct` (default on cloud drivers) collapses nested directory creation to a single API call.
- **Zero external deps in core**. Multi-targets `netstandard2.0;net8.0`.

## Install

```bash
dotnet add package FileHub
dotnet add package FileHub.DependencyInjection    # optional
dotnet add package FileHub.AmazonS3                # optional
dotnet add package FileHub.OracleObjectStorage    # optional
dotnet add package FileHub.Ftp                    # optional
```

## Named hubs (tenant, multi-backend)

```csharp
services.AddNamedFileHubs(b => b
    .Register("reports", new MemoryFileHub())
    .Register(
        "tenant",
        sp => new LocalFileHub($@"C:\tenants\{sp.GetRequiredService<ITenantContext>().Id}"),
        ServiceLifetime.Scoped));
```

Inject `INamedFileHubs` and call `GetByName("tenant")`.

## Documentation

Full docs live in the [wiki](FileHub.wiki/Home.md):

- [Quick Start](FileHub.wiki/Quick-Start.md) — install, hubs, files, directories
- [API reference](FileHub.wiki/API.md) — `IFileHub`, `FileDirectory`, `FileEntry`, exceptions
- [Drivers](FileHub.wiki/Driver-Local.md) — [Local](FileHub.wiki/Driver-Local.md), [Memory](FileHub.wiki/Driver-Memory.md), [OCI](FileHub.wiki/Driver-Oracle-Object-Storage.md), [FTP](FileHub.wiki/Driver-Ftp.md)
- [Usage patterns](FileHub.wiki/Usage.md) — sync/async, streams, pagination
- [Security](FileHub.wiki/Security.md) — sandbox and read-only mode
- [Dependency injection](FileHub.wiki/Dependency-Injection.md) · [Custom drivers](FileHub.wiki/Custom-Drivers.md) · [Testing](FileHub.wiki/Testing.md)

## License

MIT — see [LICENSE.txt](LICENSE.txt).
