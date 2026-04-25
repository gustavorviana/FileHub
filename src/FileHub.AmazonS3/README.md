# FileHub.AmazonS3

[![NuGet](https://img.shields.io/nuget/v/FileHub.AmazonS3.svg)](https://www.nuget.org/packages/FileHub.AmazonS3)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt)

Amazon S3 driver for [FileHub](https://www.nuget.org/packages/FileHub) — exposes any S3 **General Purpose** bucket as an `IFileHub` with the same API as the Local and Memory drivers.

> **Heads up:** FileHub is a young library. The API is stabilizing but rough edges are likely — bug reports, feedback, and PRs are very welcome on the [issue tracker](https://github.com/gustavorviana/FileHub/issues).

## Install

```bash
dotnet add package FileHub.AmazonS3
```

## Quick start

```csharp
using Amazon.Runtime;
using FileHub.AmazonS3;

using var hub = AmazonS3FileHub.FromCredentials(
    rootPath:    "archive/2026",
    bucketName:  "reports",
    credentials: new BasicAWSCredentials(key, secret),
    region:      "us-east-1");

hub.Root.CreateFile("q1.pdf").SetBytes(bytes);   // 1 PutObject
```

Always `using` or register as a singleton — the hub owns the AWSSDK HTTP client by default.

## Features

- Server-side `CopyObject` for `Rename` / `MoveTo` / `CopyTo` (same and cross-region).
- Multipart uploads with constant memory (`IMultipartUploadable`) and presigned-part flows for direct browser/mobile uploads (`IMultipartUploadSignable`).
- Presigned GET URLs via SigV4 (`IUrlAccessible`).
- Mutable, dirty-tracked metadata (`Content-Type`, `StorageClass`, `ServerSideEncryption`, user-metadata tags) applied automatically on the next write/copy.
- Lazy stubs — `OpenFile(name, createIfNotExists: true)` / `CreateFile("a/b/c.txt")` cost zero server calls until you actually write.

## Not supported

- **S3 Express One Zone (Directory Buckets)** — different auth flow and missing features the driver depends on. Use the AWS SDK directly for that storage tier.

## Documentation

Full driver reference (cost matrix, metadata semantics, multipart contracts) on the [wiki](https://github.com/gustavorviana/FileHub/wiki/Driver-Amazon-S3).

## License

MIT — see [LICENSE.txt](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt).
