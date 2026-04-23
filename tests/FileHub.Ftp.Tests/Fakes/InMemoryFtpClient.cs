using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileHub.Ftp.Internal;

namespace FileHub.Ftp.Tests.Fakes;

/// <summary>
/// Deterministic in-memory <see cref="IFtpClient"/> implementation that lets
/// the FTP driver logic be unit-tested without contacting an FTP server.
/// Models a real hierarchical filesystem (the FTP server view) with files
/// and nested directories.
/// </summary>
/// <remarks>
/// Two construction modes:
/// <list type="bullet">
///   <item>Stand-alone: <c>new InMemoryFtpClient()</c> — each instance owns
///   its tree and has a distinct <see cref="ConnectionScope"/>. Matches the
///   "two independent FTP servers" scenario, where the driver must fall
///   back to stream copy across hubs.</item>
///   <item>Server-backed: <see cref="InServer"/> — the client borrows a tree
///   from a shared <see cref="InMemoryFtpServer"/>, and all clients in the
///   same server share their <see cref="ConnectionScope"/>. Cross-directory
///   moves use the server-side rename path.</item>
/// </list>
/// </remarks>
internal sealed class InMemoryFtpClient : IFtpClient
{
    private readonly InMemoryFtpServer _server;
    private bool _disposed;
    private bool _connected;
    private int _connectInvocationCount;

    public object ConnectionScope => _server;

    public int ConnectInvocationCount => _connectInvocationCount;
    public bool IsConnected => _connected;

    public InMemoryFtpClient()
    {
        _server = new InMemoryFtpServer();
    }

    private InMemoryFtpClient(InMemoryFtpServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    internal static InMemoryFtpClient InServer(InMemoryFtpServer server) => new(server);

    /// <summary>Direct access to the server tree for test assertions.</summary>
    internal InMemoryFtpServer Server => _server;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _connectInvocationCount);
        _connected = true;
        return Task.CompletedTask;
    }

    public Task<FtpItemInfo> StatAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfReady(cancellationToken);
        var node = _server.Find(path);
        if (node == null) return Task.FromResult<FtpItemInfo?>(null)!;
        return Task.FromResult<FtpItemInfo?>(node.ToInfo(path))!;
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfReady(cancellationToken);
        var node = _server.Find(path);
        return Task.FromResult(node is { IsDirectory: false });
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfReady(cancellationToken);
        if (path == "/") return Task.FromResult(true);
        var node = _server.Find(path);
        return Task.FromResult(node is { IsDirectory: true });
    }

    public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken = default)
    {
        ThrowIfReady(cancellationToken);
        var node = _server.Find(path);
        if (node == null || node.IsDirectory)
            throw new FileNotFoundException($"FTP path \"{path}\" was not found.");

        if (offset < 0 || offset > node.Body.LongLength)
            throw new IOException($"Invalid offset {offset} against length {node.Body.LongLength}.");

        var slice = new byte[node.Body.LongLength - offset];
        Array.Copy(node.Body, offset, slice, 0, slice.LongLength);
        return Task.FromResult<Stream>(new MemoryStream(slice, writable: false));
    }

    public Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfReady(cancellationToken);
        var parentPath = ParentPath(path);
        var name = LeafName(path);
        var parent = _server.Find(parentPath);
        if (parent == null || !parent.IsDirectory)
            throw new FileNotFoundException($"Parent directory \"{parentPath}\" not found for write.");

        var capture = new CapturingStream(bytes =>
        {
            parent.Children[name] = InMemoryFtpNode.NewFile(bytes);
        });
        return Task.FromResult<Stream>(capture);
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfReady(cancellationToken);
        var parent = _server.Find(ParentPath(path));
        var name = LeafName(path);
        if (parent == null || !parent.Children.TryGetValue(name, out var node) || node.IsDirectory)
            throw new FileNotFoundException($"FTP file \"{path}\" was not found.");

        parent.Children.Remove(name);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfReady(cancellationToken);
        if (path == "/") throw new IOException("Cannot delete the FTP root directory.");

        var parent = _server.Find(ParentPath(path));
        var name = LeafName(path);
        if (parent == null || !parent.Children.TryGetValue(name, out var node) || !node.IsDirectory)
            throw new FileNotFoundException($"FTP directory \"{path}\" was not found.");

        parent.Children.Remove(name);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken = default)
    {
        ThrowIfReady(cancellationToken);

        var fromParent = _server.Find(ParentPath(fromPath));
        var fromName = LeafName(fromPath);
        if (fromParent == null || !fromParent.Children.TryGetValue(fromName, out var node))
            throw new FileNotFoundException($"FTP path \"{fromPath}\" was not found.");

        var toParent = _server.Find(ParentPath(toPath));
        var toName = LeafName(toPath);
        if (toParent == null || !toParent.IsDirectory)
            throw new FileNotFoundException($"Destination parent \"{ParentPath(toPath)}\" not found.");

        fromParent.Children.Remove(fromName);
        toParent.Children[toName] = node;
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        ThrowIfReady(cancellationToken);
        if (path == "/") return Task.CompletedTask;

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = _server.Root;

        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (current.Children.TryGetValue(seg, out var child))
            {
                if (!child.IsDirectory)
                    throw new IOException($"Path \"{string.Join('/', segments[..(i + 1)])}\" is a file, cannot create directory.");
                current = child;
                continue;
            }

            if (!recursive && i < segments.Length - 1)
                throw new FileNotFoundException($"Intermediate directory \"{seg}\" not found.");

            var dir = InMemoryFtpNode.NewDirectory();
            current.Children[seg] = dir;
            current = dir;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FtpItemInfo>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfReady(cancellationToken);

        var node = path == "/" ? _server.Root : _server.Find(path);
        if (node == null || !node.IsDirectory)
            throw new FileNotFoundException($"FTP directory \"{path}\" was not found.");

        var basePath = path == "/" ? string.Empty : path.TrimEnd('/');
        var items = node.Children
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => kvp.Value.ToInfo($"{basePath}/{kvp.Key}"))
            .ToArray();
        return Task.FromResult<IReadOnlyList<FtpItemInfo>>(items);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;
    }

    private static string ParentPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        if (idx <= 0) return "/";
        return trimmed.Substring(0, idx);
    }

    private static string LeafName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed : trimmed.Substring(idx + 1);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InMemoryFtpClient));
    }

    private void ThrowIfReady(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!_connected)
            throw new InvalidOperationException("FTP client used before ConnectAsync.");
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// MemoryStream that runs a callback with the buffered content when
    /// disposed — emulates how FluentFTP commits the upload when the data
    /// channel is closed.
    /// </summary>
    private sealed class CapturingStream : MemoryStream
    {
        private readonly Action<byte[]> _onCommit;
        private bool _committed;

        public CapturingStream(Action<byte[]> onCommit)
        {
            _onCommit = onCommit;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _committed = true;
                _onCommit(ToArray());
            }
            base.Dispose(disposing);
        }
    }
}

internal sealed class InMemoryFtpServer
{
    public InMemoryFtpNode Root { get; } = InMemoryFtpNode.NewDirectory();

    /// <summary>
    /// Walks <paramref name="path"/> from the root and returns the matching
    /// node, or <c>null</c> if any segment is missing.
    /// </summary>
    public InMemoryFtpNode? Find(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return Root;
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = Root;
        foreach (var seg in segments)
        {
            if (!current.Children.TryGetValue(seg, out var child)) return null;
            current = child;
        }
        return current;
    }
}

internal sealed class InMemoryFtpNode
{
    public bool IsDirectory { get; private set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public DateTime ModifiedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Dictionary<string, InMemoryFtpNode> Children { get; }
        = new(StringComparer.Ordinal);

    public static InMemoryFtpNode NewFile(byte[] body)
    {
        var now = DateTime.UtcNow;
        return new InMemoryFtpNode
        {
            IsDirectory = false,
            Body = body ?? Array.Empty<byte>(),
            ModifiedUtc = now,
            CreatedUtc = now
        };
    }

    public static InMemoryFtpNode NewDirectory()
    {
        var now = DateTime.UtcNow;
        return new InMemoryFtpNode
        {
            IsDirectory = true,
            ModifiedUtc = now,
            CreatedUtc = now
        };
    }

    public FtpItemInfo ToInfo(string fullPath)
    {
        var leaf = fullPath.TrimEnd('/');
        var idx = leaf.LastIndexOf('/');
        var name = idx < 0 ? leaf : leaf.Substring(idx + 1);
        return new FtpItemInfo
        {
            FullPath = fullPath,
            Name = name,
            IsDirectory = IsDirectory,
            Size = IsDirectory ? 0 : Body.LongLength,
            ModifiedUtc = ModifiedUtc,
            CreatedUtc = CreatedUtc
        };
    }
}
