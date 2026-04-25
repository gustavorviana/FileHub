# FileHub.OracleObjectStorage

[![NuGet](https://img.shields.io/nuget/v/FileHub.OracleObjectStorage.svg)](https://www.nuget.org/packages/FileHub.OracleObjectStorage)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt)

Oracle Cloud Object Storage driver for [FileHub](https://www.nuget.org/packages/FileHub) — exposes any OCI Object Storage bucket as an `IFileHub` with the same API as the Local and Memory drivers.

> **Heads up:** FileHub is a young library. The API is stabilizing but rough edges are likely — bug reports, feedback, and PRs are very welcome on the [issue tracker](https://github.com/gustavorviana/FileHub/issues).

## Install

```bash
dotnet add package FileHub.OracleObjectStorage
```

## Quick start

```csharp
using FileHub.OracleObjectStorage;

using var hub = OracleObjectStorageFileHub.FromConfigFile(
    rootPath:   "archive/2026",
    bucketName: "reports");

hub.Root.CreateFile("q1.pdf").SetBytes(bytes);
```

Always `using` or register as a singleton — the hub owns the SDK HTTP client by default.

## Factories

- `FromConfigFile(...)` — reads `~/.oci/config` (or a custom path).
- `FromProvider(...)` — explicit `IAuthenticationDetailsProvider` (instance principals, resource principals, custom).
- `FromClient(...)` — reuse an existing `ObjectStorageClient`. Caller owns it.

Each has a sync and async variant.

## Features

- Server-side `Rename` / `CopyTo` / `MoveTo` (same-region and cross-region).
- Pre-authenticated read URLs (`IUrlAccessible`).
- Mutable, dirty-tracked metadata applied on the next write/copy.
- Lazy stubs — `OpenFile(name, createIfNotExists: true)` and nested-path `CreateFile("a/b/c.txt")` minimize round-trips.

## Documentation

Full driver reference on the [wiki](https://github.com/gustavorviana/FileHub/wiki/Driver-Oracle-Object-Storage).

## License

MIT — see [LICENSE.txt](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt).
