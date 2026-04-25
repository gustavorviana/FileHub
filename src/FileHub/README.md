# FileHub

[![NuGet](https://img.shields.io/nuget/v/FileHub.svg)](https://www.nuget.org/packages/FileHub)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt)

Core package for **FileHub** — a .NET storage abstraction. One API (`IFileHub` → `FileDirectory` → `FileEntry`) over local disk, in-memory, and cloud object storage. Swap the driver, keep the code.

> **Heads up:** FileHub is a young library. The API is stabilizing but rough edges are likely — bug reports, feedback, and PRs are very welcome on the [issue tracker](https://github.com/gustavorviana/FileHub/issues).

## Install

```bash
dotnet add package FileHub
```

## What's in this package

- The `IFileHub`, `FileDirectory`, `FileEntry` types and exceptions.
- **Local driver** (`FileHub.Local.LocalFileHub`) — wraps `System.IO`.
- **Memory driver** (`FileHub.Memory.MemoryFileHub`) — in-process, no I/O. Great for tests.
- Sync + async on the same types. Async is the source of truth; sync delegates.
- Sandboxed by default — every hub has a root. `..`, absolute paths and symlink escapes are rejected.
- `AsReadOnly()` wrappers for files and directories.
- Multi-targets `netstandard2.0;net8.0`. Zero external dependencies.

## Quick start

```csharp
using FileHub.Local;

var hub = new LocalFileHub(@"C:\data");        // or: new MemoryFileHub();
var file = hub.Root.CreateFile("hello.txt");
file.SetText("hi");

Console.WriteLine(file.ReadAllText());
```

## Other drivers

| Backend | Package |
|---|---|
| Amazon S3 | [`FileHub.AmazonS3`](https://www.nuget.org/packages/FileHub.AmazonS3) |
| Oracle Cloud Object Storage | [`FileHub.OracleObjectStorage`](https://www.nuget.org/packages/FileHub.OracleObjectStorage) |
| FTP / FTPS | [`FileHub.Ftp`](https://www.nuget.org/packages/FileHub.Ftp) |
| DI integration | [`FileHub.DependencyInjection`](https://www.nuget.org/packages/FileHub.DependencyInjection) |

## Documentation

Full docs and driver guides live in the [project wiki](https://github.com/gustavorviana/FileHub/wiki).

## License

MIT — see [LICENSE.txt](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt).
