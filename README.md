<div align="center">

# FileHub

**One storage API for .NET. Local disk, in-memory, S3, OCI, FTP — swap the driver, keep the code.**

[![CI](https://github.com/gustavorviana/FileHub/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/gustavorviana/FileHub/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FileHub.svg)](https://www.nuget.org/packages/FileHub)
[![NuGet Downloads](https://img.shields.io/nuget/dt/FileHub.svg)](https://www.nuget.org/packages/FileHub)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.txt)
[![.NET](https://img.shields.io/badge/.NET-netstandard2.0%20%7C%20net8.0-512BD4)](#install)

[Quick start](#quick-start) ·
[Drivers](#drivers) ·
[Packages](#packages) ·
[Why FileHub?](#why-filehub) ·
[Documentation](#documentation) ·
[Contributing](#contributing)

</div>

> **Heads up:** FileHub is a young library. The API is stabilizing but rough edges are likely — bug reports, feedback, and PRs are very welcome on the [issue tracker](https://github.com/gustavorviana/FileHub/issues).

---

## What you get

- **One API, many backends** — `IFileHub` → `DirectoryEntry` → `FileEntry`. Write the service once, run it against disk, memory, or the cloud.
- **Sync + async on the same types** — async is the source of truth; sync delegates. Every call has a `CancellationToken` sibling.
- **Sandboxed by default** — every hub has a root. `..`, absolute paths, and symlink escapes are rejected at the boundary.
- **Read-only on demand** — `dir.AsReadOnly()` / `file.AsReadOnly()` wraps anything and blocks writes at runtime.
- **Nested paths everywhere** — `"a/b/c.txt"` works on every driver. `/` and `\` interchangeable. Trailing separators tolerated. Cloud drivers resolve the whole path in a single API call.
- **DI-ready** — `AddFileHub` / `AddNamedFileHubs` with lifetime + `IServiceProvider` support for tenant scoping.
- **Zero external deps in core** — multi-targets `netstandard2.0;net8.0`.

---

## Quick start

```bash
dotnet add package FileHub
```

```csharp
using FileHub.Local;

var hub  = new LocalFileHub(@"C:\data");        // or: new MemoryFileHub();
var file = hub.Root.CreateFile("hello.txt");    // anywhere under the sandbox root
file.SetText("hi");

Console.WriteLine(file.ReadAllText());
```

Nested paths, streams, async — all on the same types:

```csharp
// Nested path auto-creates intermediate directories
hub.Root.CreateFile("reports/2026/q1.pdf").SetBytes(bytes);

// Streams
using var write = hub.Root.CreateFile("big.log").GetWriteStream();
using var read  = hub.Root.OpenFile("big.log").GetReadStream();

// Async with cancellation
var entry = await hub.Root.CreateFileAsync("data.json", ct);
await entry.SetTextAsync("{}", cancellationToken: ct);

// async listing on net8.0
await foreach (var f in hub.Root.GetFilesAsync("*.log"))
    Console.WriteLine(f.Name);
```

Sandbox + read-only wrapping:

```csharp
hub.Root.CreateFile(@"..\escape.txt");     // FileHubException — sandbox enforced

var ro = hub.Root.OpenDirectory("config").AsReadOnly();
ro.OpenFile("settings.json").ReadAllText();// OK
ro.CreateFile("new.txt");                  // FileHubException
```

Full walkthrough: **[Quick Start wiki](FileHub.wiki/Quick-Start.md)**.

---

## Drivers

| Driver | Package | Backend | Notes |
|---|---|---|---|
| **Local** | `FileHub` (core) | `System.IO.File` | Sandboxed to a root path. |
| **Memory** | `FileHub` (core) | In-process | Zero-disk, zero-setup. Great for tests. |
| **Amazon S3** | `FileHub.AmazonS3` | AWS S3 General Purpose buckets | Lazy stubs, single-PUT writes. Directory buckets not supported. |
| **Oracle OCI** | `FileHub.OracleObjectStorage` | OCI Object Storage | Cost-optimised: single-request directory ops. |
| **FTP** | `FileHub.Ftp` | FTP via FluentFTP | Plain FTP server. |
| **Custom** | your assembly | anything | Implement two abstract classes — see [Custom drivers](FileHub.wiki/Custom-Drivers.md). |

---

## Packages

Every published NuGet package:

| Package | Purpose | Depends on |
|---|---|---|
| [`FileHub`](https://www.nuget.org/packages/FileHub) | Core: `IFileHub`, `DirectoryEntry`, `FileEntry` + Local & Memory drivers | — (zero external deps) |
| [`FileHub.DependencyInjection`](https://www.nuget.org/packages/FileHub.DependencyInjection) | `AddFileHub` / `AddNamedFileHubs` — MS.Extensions.DI integration | `FileHub` |
| [`FileHub.AmazonS3`](https://www.nuget.org/packages/FileHub.AmazonS3) | Amazon S3 driver | `FileHub` |
| [`FileHub.OracleObjectStorage`](https://www.nuget.org/packages/FileHub.OracleObjectStorage) | Oracle OCI Object Storage driver | `FileHub` |
| [`FileHub.Ftp`](https://www.nuget.org/packages/FileHub.Ftp) | FTP driver (FluentFTP) | `FileHub` |

```bash
dotnet add package FileHub                        # core: Local + Memory
dotnet add package FileHub.DependencyInjection    # MS.Extensions.DI integration
dotnet add package FileHub.AmazonS3               # AWS S3
dotnet add package FileHub.OracleObjectStorage    # OCI Object Storage
dotnet add package FileHub.Ftp                    # FTP
```

---

## Why FileHub?

Most storage abstractions for .NET try to become infrastructure frameworks. They often:

- leak provider-specific concepts into every layer,
- mix unrelated concerns (queues, messaging, locks),
- depend on static factories or pre-DI APIs,
- make provider swapping painful in practice,
- struggle with modern object-storage semantics.

FileHub focuses on one thing: **a clean, modern storage abstraction for .NET**.

- DI-first
- async-first (sync delegates to async)
- sandboxed by default
- provider-agnostic
- minimal API surface — three types: `IFileHub` → `DirectoryEntry` → `FileEntry`
- designed for modern object storage (single-PUT writes, presigned URLs, lazy stubs, dirty-tracked metadata)

The boundary is drawn once. A service written against `IFileHub` / `DirectoryEntry` runs against disk, memory, or the cloud — backend becomes a constructor detail.

**Before — coupled to a backend:**

```csharp
public class ReportService
{
    public void Save(string body)
        => File.WriteAllText(@"C:\reports\latest.txt", body); // disk-only, no tests, no sandbox
}
```

**After — backend-agnostic:**

```csharp
public class ReportService(IFileHub hub)
{
    public void Save(string body)
        => hub.Root.CreateFile("latest.txt").SetText(body);
}

// Unit test
new ReportService(new MemoryFileHub()).Save("hi");

// Local dev
new ReportService(new LocalFileHub(@"C:\reports")).Save("hi");

// Prod
new ReportService(await AmazonS3FileHub.CreateAsync(
        AmazonS3HubOptions.FromProfile("my-bucket", profile: "prod", region: "us-east-1", rootPath: "reports/2026")))
    .Save("hi");
```

Same service. Three backends. Zero edits.

---

## Dependency injection

```csharp
services.AddFileHub(new LocalFileHub(@"C:\data"));
```

Named hubs for multi-tenant / multi-backend setups:

```csharp
services.AddNamedFileHubs(b => b
    .Register("reports", new MemoryFileHub())
    .Register(
        "tenant",
        sp => new LocalFileHub($@"C:\tenants\{sp.GetRequiredService<ITenantContext>().Id}"),
        ServiceLifetime.Scoped));
```

Inject `INamedFileHubs` and call `GetByName("tenant")`. Details: [Dependency injection wiki](FileHub.wiki/Dependency-Injection.md).

---

## Documentation

Full docs live in the [wiki](FileHub.wiki/Home.md).

| Topic | Link |
|---|---|
| Quick start | [Quick Start](FileHub.wiki/Quick-Start.md) |
| API reference | [`IFileHub`, `DirectoryEntry`, `FileEntry`, exceptions](FileHub.wiki/API.md) |
| Usage patterns | [Sync/async, streams, pagination](FileHub.wiki/Usage.md) |
| Security | [Sandbox + read-only](FileHub.wiki/Security.md) |
| Dependency injection | [`AddFileHub`, named hubs, tenant scoping](FileHub.wiki/Dependency-Injection.md) |
| Custom drivers | [Plug in a new backend](FileHub.wiki/Custom-Drivers.md) |
| Testing | [Swap in `MemoryFileHub`](FileHub.wiki/Testing.md) |
| Driver: Local | [Disk](FileHub.wiki/Driver-Local.md) |
| Driver: Memory | [In-process](FileHub.wiki/Driver-Memory.md) |
| Driver: Amazon S3 | [AWS S3](FileHub.wiki/Driver-Amazon-S3.md) |
| Driver: Oracle OCI | [OCI Object Storage](FileHub.wiki/Driver-Oracle-Object-Storage.md) |
| Driver: FTP | [FTP](FileHub.wiki/Driver-Ftp.md) |

---

## Contributing

Issues, discussions, and PRs are welcome.

- Found a bug or rough edge? Open an [issue](https://github.com/gustavorviana/FileHub/issues).
- Adding a driver? Start from [Custom drivers](FileHub.wiki/Custom-Drivers.md).
- Submitting a PR? Please include tests — the suite runs against every driver via a shared contract.

## License

MIT — see [LICENSE.txt](LICENSE.txt).
