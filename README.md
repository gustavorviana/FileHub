# FileHub

A .NET library that abstracts file and directory access through a unified API. Install and it works with local files out of the box — no configuration needed. Other storage backends can be plugged in as custom drivers using the same contract.

## Motivation

Most .NET apps end up tangled with `System.IO.File` calls scattered across services, or coupled to a specific cloud SDK whose types leak into every layer. When the storage backend has to change — "move uploads to S3", "keep unit tests off the disk", "sandbox per-tenant data" — the refactor is painful because the abstraction boundary was never drawn.

FileHub draws that boundary once and holds it:

- **One API, many backends.** Consumer code depends on `IFileHub` / `FileDirectory` / `FileEntry`. Local disk, in-memory, Oracle Object Storage, or any custom driver plug into the exact same contract.
- **No SDK types leak.** Cloud drivers wrap their SDK internally; consumers never see an `ObjectStorageClient` or an SDK-specific exception. Swapping a driver does not ripple through the codebase.
- **Sync and async on the same class.** Modeled on `Stream.Read` / `Stream.ReadAsync` — pick whichever fits the caller, no parallel async interface to maintain.
- **Safe by default.** Every hub has a sandbox root. Path traversal (`../`), symlink escape, and invalid names are rejected before any I/O happens.
- **Testable without mocks.** Swap `LocalFileHub` for `MemoryFileHub` in tests and the same code runs in-process with zero disk I/O. No temp folders to clean up, no fake interfaces to maintain.
- **Zero dependencies in the core.** The base package pulls nothing beyond the BCL. Optional integrations (DI helpers, OCI driver) live in separate packages so you opt in only where needed.
- **Read-only as a wrapper, not a policy.** `AsReadOnly()` turns any directory or file into a runtime-enforced read-only view that propagates to everything it returns.

The goal is that a service like `ReportService(IFileHub hub)` can be written once, unit-tested against memory, run locally against disk, and deployed to the cloud against an object store — without a single line changing in the service itself.

## Features

- **Zero-config for local usage** — `new LocalFileHub(path)` and you're ready
- **Pluggable drivers** — Local and Memory ship in the core; additional backends can be built on the same abstract classes
- **Sync + async** — every operation has both synchronous and asynchronous versions
- **Read-only mode** — `IsReadOnly` property blocks writes via `ThrowIfReadOnly`
- **Sandbox (root)** — every factory receives a root directory; path traversal (`../`) is blocked
- **Full directory CRUD** — create, rename, move, copy, delete, list
- **Multi-target** — `netstandard2.0` + `net8.0`
- **Zero external dependencies** in the core

## Target frameworks

- `netstandard2.0` — broad compatibility; `GetFilesAsync` returns `Task<IEnumerable<T>>`
- `net8.0` — uses native `IAsyncEnumerable<T>` for streaming async listing

## Quick start

### Local (disk)

```csharp
using FileHub.Local;

var hub = new LocalFileHub(@"C:\data");
// Root is the sandbox. Nothing outside it is accessible.

var file = hub.Root.CreateFile("hello.txt");
file.SetText("Hello, world!");

var content = file.ReadAllText();
Console.WriteLine(content);
```

### Memory (unit tests)

```csharp
using FileHub.Memory;

var hub = new MemoryFileHub();
var file = hub.Root.CreateFile("test.txt");
file.SetText("test content");

// Useful as a disk replacement in tests - zero I/O
```

### Async

```csharp
var file = await hub.Root.CreateFileAsync("data.json");
await file.SetTextAsync("{\"key\": \"value\"}");

string content = await file.ReadAllTextAsync();
```

### Enumerating files

```csharp
// Sync
foreach (var file in hub.Root.GetFiles("*.log"))
    Console.WriteLine($"{file.Name}: {file.Length} bytes");

// Async (net8.0 - IAsyncEnumerable)
await foreach (var file in hub.Root.GetFilesAsync("*.log"))
    Console.WriteLine(file.Name);
```

### Subdirectories

```csharp
var logs = hub.Root.CreateDirectory("logs");
var archive = logs.CreateDirectory("2025");

var logFile = archive.CreateFile("app.log");
logFile.SetText("log entry");

// Walk the tree
foreach (var dir in hub.Root.GetDirectories())
    Console.WriteLine(dir.Path);
```

### Copy / move / rename

```csharp
var file = hub.Root.OpenFile("report.pdf");

// Copy within the same directory
var copy = file.CopyTo("report-backup.pdf");

// Move to another directory
var archive = hub.Root.CreateDirectory("archive");
file.MoveTo(archive, "report.pdf");

// Rename
file.Rename("report-final.pdf");
```

### Read-only

```csharp
using FileHub;

var config = hub.Root.OpenDirectory("config");
var readOnlyConfig = config.AsReadOnly();

// Reading works
var settings = readOnlyConfig.OpenFile("settings.json");
var json = settings.ReadAllText();

// Writing throws FileHubException
readOnlyConfig.CreateFile("new.txt");  // throws!
settings.SetText("changed");            // throws!
```

### Sandbox (path traversal protection)

```csharp
var hub = new LocalFileHub(@"C:\data");

hub.Root.CreateFile("file.txt");                  // OK - inside root
hub.Root.OpenDirectory("subfolder");              // OK

hub.Root.OpenDirectory("../../Windows");          // FileHubException!
hub.Root.CreateFile("../escape.txt");             // FileHubException!
```

## Architecture

### Class hierarchy

```
FileSystemEntry (abstract, base)
  |- Path, Name, IsReadOnly, timestamps
  |- Exists / ExistsAsync
  |- IDisposable
  |
  |-- FileEntry (abstract)
  |     |- Extension, Length, Parent
  |     |- GetReadStream / GetWriteStream
  |     |- ReadAllText, SetText, ReadAllBytes, SetBytes, CopyToStream
  |     |- Rename, MoveTo, CopyTo, Delete
  |     |- All methods have async versions
  |     |
  |     |-- LocalFile
  |     |-- MemoryFile
  |
  |-- FileDirectory (abstract)
        |- Parent, RootPath (sandbox)
        |- CreateFile / OpenFile / TryOpenFile / GetFiles
        |- CreateDirectory / OpenDirectory / TryOpenDirectory / GetDirectories
        |- Rename, MoveTo, CopyTo, Delete, DeleteIfExists
        |- All methods have async versions
        |
        |-- LocalDirectory
        |-- MemoryDirectory
```

### Factories

```
IFileHub (interface)
  |- Root : FileDirectory
  |
  |-- LocalFileHub - new LocalFileHub(rootPath)
  |-- MemoryFileHub - new MemoryFileHub()
```

Each factory is an **instance** (not static), so it can hold its own configuration.

### The "Stream-like" pattern

`FileEntry` and `FileDirectory` expose **sync and async** on the same class, just like `Stream.Read` / `Stream.ReadAsync`. Every operation is available in both versions — pick whichever fits your code. There is no separate async interface to learn.

### IsReadOnly

Property on the base `FileSystemEntry` class. Write operations call `ThrowIfReadOnly()` before executing:

```csharp
public override void Delete()
{
    ThrowIfReadOnly();  // throws FileHubException if IsReadOnly == true
    File.Delete(Path);
}
```

To convert a writable directory into read-only, use the `.AsReadOnly()` extension method, which returns a wrapper with the flag enabled. Files and subdirectories returned by the wrapper are also read-only (automatic propagation).

### IUrlAccessible

Optional interface that a file can implement when it is reachable through a URL. Consumer code can check for it with a type test to get a download link without knowing which backend provided the file:

```csharp
var file = hub.Root.OpenFile("report.pdf");

if (file is IUrlAccessible urlFile)
{
    var link = urlFile.GetSignedUrl(TimeSpan.FromMinutes(15));
    // hand this temporary link to the browser
}
```

The Local and Memory drivers included in the core do not implement this interface — files they return live on disk or in memory, not behind a URL.

## Available drivers

### Local (`FileHub.Local`)

Wraps `System.IO.File` / `System.IO.Directory`. Automatically creates the root directory if it does not exist. Supports `~` to resolve relative to `AppDomain.CurrentDomain.BaseDirectory`:

```csharp
var hub = new LocalFileHub("~/app-data");
// = AppDomain.CurrentDomain.BaseDirectory + "/app-data"
```

Or with a custom resolver:

```csharp
var hub = new LocalFileHub("data", path =>
    Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "MyApp", path));
```

#### Symlinks

Symbolic links (and other Windows reparse points — junctions, mount points) are **always ignored** when enumerating directory contents via `GetFiles` / `GetDirectories`. A symlink inside the sandbox can point to anywhere on disk, which would silently defeat the root-path isolation, so the driver skips them entirely.

Detection uses `FileAttributes.ReparsePoint`, which works on every target framework — behavior is identical on `netstandard2.0` and `net8.0`.

### Memory (`FileHub.Memory`)

Stores files and directories in memory using `Dictionary` + `MemoryStream`. Mainly useful for unit tests — swap the real disk for memory without changing a single line of the code that uses FileHub's API:

```csharp
IFileHub hub = environment == "test"
    ? new MemoryFileHub()
    : new LocalFileHub(@"C:\data");

// rest of the code uses hub.Root normally
```

## Project structure

```
src/FileHub/
  FileHub.csproj                (multi-target netstandard2.0;net8.0)
  FileSystemEntry.cs            (abstract base)
  FileEntry.cs                  (abstract - files)
  FileDirectory.cs              (abstract - directories)
  IFileHub.cs                   (factory interface)
  IUrlAccessible.cs             (optional interface for URL-accessible drivers)
  FileHubException.cs
  FileHubExtensions.cs          (AsReadOnly)
  Local/
    LocalFileHub.cs
    LocalDirectory.cs
    LocalFile.cs
  Memory/
    MemoryFileHub.cs
    MemoryDirectory.cs
    MemoryFile.cs
    MemoryFileData.cs           (internal)
    NonDisposableMemoryStream.cs (internal)
```
