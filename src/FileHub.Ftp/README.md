# FileHub.Ftp

[![NuGet](https://img.shields.io/nuget/v/FileHub.Ftp.svg)](https://www.nuget.org/packages/FileHub.Ftp)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt)

FTP / FTPS driver for [FileHub](https://www.nuget.org/packages/FileHub), built on [FluentFTP](https://github.com/robinrodricks/FluentFTP). Exposes any FTP server as an `IFileHub` with the same API as the Local and Memory drivers.

> **Heads up:** FileHub is a young library. The API is stabilizing but rough edges are likely — bug reports, feedback, and PRs are very welcome on the [issue tracker](https://github.com/gustavorviana/FileHub/issues).

## Install

```bash
dotnet add package FileHub.Ftp
```

## Quick start

```csharp
using FileHub.Ftp;

using var hub = FtpFileHub.Connect(
    host:     "ftp.example.com",
    user:     "svc",
    password: "s3cret",
    rootPath: "/uploads/2026");

hub.Root.CreateFile("report.pdf").SetBytes(bytes);
```

Always `using` or register as a singleton — the hub owns the FTP control connection.

## Factories

- `Connect(host, port?, user?, password?, rootPath?, pathMode?)` — fresh connection with inline credentials.
- `FromCredentials(host, port, NetworkCredential, rootPath?, pathMode?)` — credentials from a secret store / DI.
- `FromClient(AsyncFtpClient, ownsClient?, rootPath?, pathMode?)` — reuse an externally-configured FluentFTP client.

Each has a sync and async variant.

## Features

- Atomic rename inside the same connection (`RNFR` / `RNTO`).
- Lazy connect with idle-timeout reconnect.
- Stream-based read/write — files don't have to fit in memory.
- Sandboxed by `rootPath`; paths outside the root are rejected.

## Documentation

Full driver reference on the [wiki](https://github.com/gustavorviana/FileHub/wiki/Driver-Ftp).

## License

MIT — see [LICENSE.txt](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt).
