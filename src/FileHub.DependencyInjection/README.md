# FileHub.DependencyInjection

[![NuGet](https://img.shields.io/nuget/v/FileHub.DependencyInjection.svg)](https://www.nuget.org/packages/FileHub.DependencyInjection)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt)

`Microsoft.Extensions.DependencyInjection` integration for [FileHub](https://www.nuget.org/packages/FileHub) — register one or many `IFileHub` instances on `IServiceCollection` and resolve them by name at runtime, with first-class lifetime support.

> **Heads up:** FileHub is a young library. The API is stabilizing but rough edges are likely — bug reports, feedback, and PRs are very welcome on the [issue tracker](https://github.com/gustavorviana/FileHub/issues).

## Install

```bash
dotnet add package FileHub.DependencyInjection
```

## Single hub

```csharp
using FileHub.Local;

services.AddFileHub<IFileHub>(_ => new LocalFileHub(@"C:\data"));
```

## Named hubs (tenants, multi-backend)

```csharp
services.AddNamedFileHubs(b => b
    .Register("reports", new MemoryFileHub())
    .Register(
        "tenant",
        sp => new LocalFileHub($@"C:\tenants\{sp.GetRequiredService<ITenantContext>().Id}"),
        ServiceLifetime.Scoped));
```

```csharp
public class ReportService(INamedFileHubs hubs)
{
    private readonly IFileHub _tenant = hubs.GetByName("tenant");
}
```

Lifetimes (`Singleton`, `Scoped`, `Transient`) are honored per registration. Scoped registrations let you resolve per-request services (e.g. a tenant context) inside the factory.

## Documentation

See [Dependency Injection](https://github.com/gustavorviana/FileHub/wiki/Dependency-Injection) on the wiki.

## License

MIT — see [LICENSE.txt](https://github.com/gustavorviana/FileHub/blob/main/LICENSE.txt).
