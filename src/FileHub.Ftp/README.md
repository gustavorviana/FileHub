# FileHub.Ftp

[![NuGet](https://img.shields.io/nuget/v/FileHub.Ftp.svg)](https://www.nuget.org/packages/FileHub.Ftp)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt)

FTP and FTPS driver for [FileHub](https://www.nuget.org/packages/FileHub), built on [FluentFTP](https://github.com/robinrodricks/FluentFTP). Exposes any FTP server as an `IFileHub` with the same API as the Local and Memory drivers.

> **Heads up:** FileHub is a young library. The API is stabilizing but rough edges are likely — bug reports, feedback, and PRs are very welcome on the [issue tracker](https://github.com/gustavorviana/FileHub/issues).

## Install

```bash
dotnet add package FileHub.Ftp
```

## Quick start

```csharp
using FileHub.Ftp;

using var hub = FtpFileHub.Create(FtpHubOptions.FromCredentials(
    host:     "ftp.example.com",
    user:     "svc",
    password: "s3cret",
    rootPath: "/uploads/2026"));

hub.Root.CreateFile("report.pdf").SetBytes(bytes);
```

Always `using` or register as a singleton — the hub owns the FTP control connection.

## Construction

A single entry point — `FtpFileHub.Create(FtpHubOptions)` (and `CreateAsync`) — so there's no sync/async factory sprawl. All connection, root, and FTPS settings live on `FtpHubOptions`; pick a typed factory:

- `FtpHubOptions.FromCredentials(host, port?, user?, password?, rootPath?, encryption?, certificateValidation?)` — inline credentials (also a `NetworkCredential` overload).
- `FtpHubOptions.FromClient(AsyncFtpClient, ownsClient?, rootPath?)` — reuse an externally-configured FluentFTP client.
- `new FtpHubOptions { ... }` — object initializer for advanced cases.

## FTPS (TLS)

Pass encryption through `FtpHubOptions`:

```csharp
using FileHub.Ftp;
using FluentFTP; // FtpEncryptionMode

using var hub = FtpFileHub.Create(FtpHubOptions.FromCredentials(
    host:       "ftp.example.com",
    user:       "svc",
    password:   "s3cret",
    encryption: FtpEncryptionMode.Explicit)); // AUTH TLS on the normal port; Implicit = port 990
```

- `encryption` takes FluentFTP's `FtpEncryptionMode`: `None` (default, plain FTP), `Explicit` (FTPES, common), `Implicit` (port 990), `Auto`.
- The data channel is encrypted too by default (`FtpHubOptions.DataConnectionEncryption`).
- Certificates are validated by default — an untrusted/invalid one fails the connection. For a self-signed dev server, supply `certificateValidation` (a standard `RemoteCertificateValidationCallback`) returning `true` to accept.

## Features

- Atomic rename inside the same connection (`RNFR` / `RNTO`).
- Lazy connect with idle-timeout reconnect.
- Stream-based read/write — files don't have to fit in memory.
- Sandboxed by `rootPath`; paths outside the root are rejected.

## Documentation

Full driver reference on the [wiki](https://github.com/gustavorviana/FileHub/wiki/Driver-Ftp).

## License

MIT — see [LICENSE.txt](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt).
