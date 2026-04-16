using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Internal;

namespace FileHub.OracleObjectStorage
{
    public class OracleObjectStorageDirectory : FileDirectory
    {
        private const string DirectoryContentType = "application/x-directory";

        private readonly IOciSession _session;
        private readonly OracleObjectStorageDirectory _parent;
        private readonly string _prefix;
        private readonly string _rootPrefix;
        private DateTime _creationTimeUtc;
        private DateTime _lastWriteTimeUtc;
        private bool _metadataLoaded;

        public override string Path => OciPathUtil.DisplayPath(_prefix);
        public override FileDirectory Parent => _parent;

        public override DateTime CreationTimeUtc
        {
            get { EnsureMetadataLoaded(); return _creationTimeUtc; }
        }

        public override DateTime LastWriteTimeUtc
        {
            get { EnsureMetadataLoaded(); return _lastWriteTimeUtc; }
        }

        internal IOciSession SessionInternal => _session;
        internal string PrefixInternal => _prefix;
        internal string RootPrefixInternal => _rootPrefix;

        /// <summary>Constructor used for the root directory of a FileHub.</summary>
        internal OracleObjectStorageDirectory(IOciSession session, string rootPrefix)
            : base(GetDisplayName(rootPrefix), rootPath: rootPrefix)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _prefix = rootPrefix ?? string.Empty;
            _rootPrefix = _prefix;
            _parent = null;

            EnsureMarkerIfNeeded();
        }

        /// <summary>Constructor used for child directories.</summary>
        internal OracleObjectStorageDirectory(OracleObjectStorageDirectory parent, string name)
            : base(name, rootPath: parent?.RootPrefixInternal)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _session = parent._session;
            _rootPrefix = parent._rootPrefix;
            _prefix = OciPathUtil.CombinePrefix(parent._prefix, name);
        }

        private static string GetDisplayName(string rootPrefix)
        {
            if (string.IsNullOrEmpty(rootPrefix))
                return "/";
            return OciPathUtil.GetLeafName(rootPrefix);
        }

        private void EnsureMarkerIfNeeded()
        {
            if (string.IsNullOrEmpty(_prefix)) return;
            try
            {
                var head = _session.Client.HeadObjectAsync(_prefix).GetAwaiter().GetResult();
                _creationTimeUtc = head.LastModified ?? default;
                _lastWriteTimeUtc = _creationTimeUtc;
                _metadataLoaded = true;
            }
            catch (FileNotFoundException)
            {
                PutMarker().GetAwaiter().GetResult();
                _creationTimeUtc = DateTime.UtcNow;
                _lastWriteTimeUtc = _creationTimeUtc;
                _metadataLoaded = true;
            }
        }

        private async Task PutMarker(CancellationToken cancellationToken = default)
        {
            using var empty = new MemoryStream();
            await _session.Client.PutObjectAsync(
                _prefix, empty, contentLength: 0, contentType: DirectoryContentType, opcMeta: null, cancellationToken).ConfigureAwait(false);
        }

        private void EnsureMetadataLoaded()
        {
            if (_metadataLoaded) return;
            if (string.IsNullOrEmpty(_prefix))
            {
                _creationTimeUtc = DateTime.MinValue;
                _lastWriteTimeUtc = _creationTimeUtc;
                _metadataLoaded = true;
                return;
            }

            try
            {
                var head = _session.Client.HeadObjectAsync(_prefix).GetAwaiter().GetResult();
                _creationTimeUtc = head.LastModified ?? default;
                _lastWriteTimeUtc = _creationTimeUtc;
            }
            catch (FileNotFoundException)
            {
                _creationTimeUtc = default;
                _lastWriteTimeUtc = default;
            }
            _metadataLoaded = true;
        }

        // === Existence ===

        public override bool Exists() => ExistsAsync().GetAwaiter().GetResult();

        public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_prefix))
                return await AnyObjectUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);

            try
            {
                await _session.Client.HeadObjectAsync(_prefix, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (FileNotFoundException)
            {
                return await AnyObjectUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);
            }
        }

        // === File operations ===

        public override FileEntry CreateFile(string name) => CreateFileAsync(name).GetAwaiter().GetResult();

        public override async Task<FileEntry> CreateFileAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var objectName = OciPathUtil.ResolveSafeObjectName(_rootPrefix, _prefix, name);
            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(objectName, empty, 0, null, null, cancellationToken).ConfigureAwait(false);
            }
            return new OracleObjectStorageFile(this, name, 0, DateTime.UtcNow);
        }

        public override bool TryOpenFile(string name, out FileEntry file)
        {
            file = TryOpenFileCoreAsync(name).GetAwaiter().GetResult();
            return file != null;
        }

        private async Task<FileEntry> TryOpenFileCoreAsync(string name, CancellationToken cancellationToken = default)
        {
            try
            {
                OciPathUtil.ValidateName(name);
            }
            catch (ArgumentException)
            {
                return null;
            }

            var objectName = OciPathUtil.CombineObjectName(_prefix, name);
            try
            {
                var head = await _session.Client.HeadObjectAsync(objectName, cancellationToken).ConfigureAwait(false);
                return new OracleObjectStorageFile(this, name, head.ContentLength ?? 0, head.LastModified);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        public override IEnumerable<FileEntry> GetFiles(string searchPattern = "*")
        {
            var regex = OciPathUtil.BuildSearchPatternRegex(searchPattern);
            string start = null;
            do
            {
                var page = _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, start: start).GetAwaiter().GetResult();
                foreach (var obj in page.Objects)
                {
                    if (!IsChildFile(obj.Name, out var leaf)) continue;
                    if (!regex.IsMatch(leaf)) continue;
                    yield return new OracleObjectStorageFile(this, leaf, obj.Size ?? 0, obj.TimeCreated);
                }
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));
        }

#if NET8_0_OR_GREATER
        public override async IAsyncEnumerable<FileEntry> GetFilesAsync(
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var regex = OciPathUtil.BuildSearchPatternRegex(searchPattern);
            string start = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, start: start, cancellationToken).ConfigureAwait(false);
                foreach (var obj in page.Objects)
                {
                    if (!IsChildFile(obj.Name, out var leaf)) continue;
                    if (!regex.IsMatch(leaf)) continue;
                    yield return new OracleObjectStorageFile(this, leaf, obj.Size ?? 0, obj.TimeCreated);
                }
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));
        }
#endif

        // === Directory operations ===

        public override FileDirectory CreateDirectory(string name) => CreateDirectoryAsync(name).GetAwaiter().GetResult();

        public override async Task<FileDirectory> CreateDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            OciPathUtil.ResolveSafeChildPrefix(_rootPrefix, _prefix, name);
            var childPrefix = OciPathUtil.CombinePrefix(_prefix, name);

            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(childPrefix, empty, 0, DirectoryContentType, null, cancellationToken).ConfigureAwait(false);
            }
            return new OracleObjectStorageDirectory(this, name);
        }

        public override bool TryOpenDirectory(string name, out FileDirectory directory)
        {
            directory = TryOpenDirectoryCoreAsync(name).GetAwaiter().GetResult();
            return directory != null;
        }

        private async Task<FileDirectory> TryOpenDirectoryCoreAsync(string name, CancellationToken cancellationToken = default)
        {
            try
            {
                OciPathUtil.ValidateName(name);
            }
            catch (ArgumentException)
            {
                return null;
            }

            var childPrefix = OciPathUtil.CombinePrefix(_prefix, name);

            try
            {
                await _session.Client.HeadObjectAsync(childPrefix, cancellationToken).ConfigureAwait(false);
                return new OracleObjectStorageDirectory(this, name);
            }
            catch (FileNotFoundException)
            {
                if (await AnyObjectUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false))
                    return new OracleObjectStorageDirectory(this, name);
                return null;
            }
        }

        public override IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*")
        {
            var regex = OciPathUtil.BuildSearchPatternRegex(searchPattern);
            string start = null;
            do
            {
                var page = _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, start: start).GetAwaiter().GetResult();
                foreach (var childPrefix in page.Prefixes)
                {
                    var leaf = OciPathUtil.GetLeafName(childPrefix);
                    if (!regex.IsMatch(leaf)) continue;
                    yield return new OracleObjectStorageDirectory(this, leaf);
                }
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));
        }

#if NET8_0_OR_GREATER
        public override async IAsyncEnumerable<FileDirectory> GetDirectoriesAsync(
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var regex = OciPathUtil.BuildSearchPatternRegex(searchPattern);
            string start = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, start: start, cancellationToken).ConfigureAwait(false);
                foreach (var childPrefix in page.Prefixes)
                {
                    var leaf = OciPathUtil.GetLeafName(childPrefix);
                    if (!regex.IsMatch(leaf)) continue;
                    yield return new OracleObjectStorageDirectory(this, leaf);
                }
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));
        }
#endif

        // === Common ===

        public override bool ItemExists(string name) => ItemExistsAsync(name).GetAwaiter().GetResult();

        public override async Task<bool> ItemExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            try
            {
                OciPathUtil.ValidateName(name);
            }
            catch (ArgumentException)
            {
                return false;
            }

            var objectName = OciPathUtil.CombineObjectName(_prefix, name);
            try
            {
                await _session.Client.HeadObjectAsync(objectName, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (FileNotFoundException)
            {
                // fall through to directory probe
            }

            var childPrefix = OciPathUtil.CombinePrefix(_prefix, name);
            try
            {
                await _session.Client.HeadObjectAsync(childPrefix, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (FileNotFoundException)
            {
                return await AnyObjectUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false);
            }
        }

        public override void Delete() => DeleteAsync().GetAwaiter().GetResult();

        public override async Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (_prefix == _rootPrefix)
                throw new NotSupportedException("Cannot delete the root directory of the FileHub.");

            await DeleteAllUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);
        }

        public override void Delete(string name) => DeleteAsync(name).GetAwaiter().GetResult();

        public override async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            OciPathUtil.ValidateName(name);

            var objectName = OciPathUtil.CombineObjectName(_prefix, name);
            try
            {
                await _session.Client.DeleteObjectAsync(objectName, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (FileNotFoundException)
            {
                // fall through to directory delete attempt
            }

            var childPrefix = OciPathUtil.CombinePrefix(_prefix, name);
            if (await AnyObjectUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false))
            {
                await DeleteAllUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new FileNotFoundException($"The item \"{name}\" was not found under \"{Path}\".");
        }

        public override FileDirectory Rename(string newName) => RenameAsync(newName).GetAwaiter().GetResult();

        public override async Task<FileDirectory> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (_parent == null)
                throw new NotSupportedException("Cannot rename the root directory.");

            OciPathUtil.ValidateName(newName);
            var destinationPrefix = OciPathUtil.CombinePrefix(_parent._prefix, newName);
            await CopyAllObjectsAsync(_prefix, destinationPrefix, cancellationToken).ConfigureAwait(false);
            await DeleteAllUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);
            return new OracleObjectStorageDirectory(_parent, newName);
        }

        public override FileDirectory MoveTo(FileDirectory directory, string name)
            => MoveToAsync(directory, name).GetAwaiter().GetResult();

        public override async Task<FileDirectory> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var newDir = await CopyToAsync(directory, name, cancellationToken).ConfigureAwait(false);
            await DeleteAllUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);
            return newDir;
        }

        public override FileDirectory CopyTo(FileDirectory directory, string name)
            => CopyToAsync(directory, name).GetAwaiter().GetResult();

        public override async Task<FileDirectory> CopyToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            if (directory is OracleObjectStorageDirectory ociDir
                && ReferenceEquals(ociDir._session, _session))
            {
                OciPathUtil.ValidateName(name);
                var destinationPrefix = OciPathUtil.CombinePrefix(ociDir._prefix, name);
                await CopyAllObjectsAsync(_prefix, destinationPrefix, cancellationToken).ConfigureAwait(false);
                return new OracleObjectStorageDirectory(ociDir, name);
            }

            var newDir = await directory.CreateDirectoryAsync(name, cancellationToken).ConfigureAwait(false);
            CopyContentsGeneric(this, newDir);
            return newDir;
        }

        public override void SetLastWriteTime(DateTime date) => SetLastWriteTimeAsync(date).GetAwaiter().GetResult();

        public override async Task SetLastWriteTimeAsync(DateTime date, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (string.IsNullOrEmpty(_prefix)) return;

            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OracleObjectStorageFile.ChangedAtTag] = date.ToUniversalTime().ToString("O")
            };

            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(_prefix, empty, 0, DirectoryContentType, meta, cancellationToken).ConfigureAwait(false);
            }

            if (DateTime.TryParse(meta[OracleObjectStorageFile.ChangedAtTag], null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                _lastWriteTimeUtc = parsed;
        }

        // === Helpers ===

        private bool IsChildFile(string objectName, out string leaf)
        {
            leaf = null;
            if (!objectName.StartsWith(_prefix, StringComparison.Ordinal)) return false;
            if (objectName.Length == _prefix.Length) return false; // own marker
            var rest = objectName.Substring(_prefix.Length);
            if (rest.EndsWith("/", StringComparison.Ordinal)) return false; // subdir marker
            if (rest.IndexOf('/') >= 0) return false; // nested deeper
            leaf = rest;
            return true;
        }

        private async Task<bool> AnyObjectUnderPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            var page = await _session.Client.ListObjectsAsync(prefix, delimiter: null, limit: 1, start: null, cancellationToken).ConfigureAwait(false);
            return page.Objects.Count > 0;
        }

        private async Task DeleteAllUnderPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            string start = null;
            do
            {
                var page = await _session.Client.ListObjectsAsync(prefix, delimiter: null, limit: null, start: start, cancellationToken).ConfigureAwait(false);
                foreach (var obj in page.Objects)
                    await _session.Client.DeleteObjectAsync(obj.Name, cancellationToken).ConfigureAwait(false);
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));

            if (!string.IsNullOrEmpty(prefix))
            {
                try
                {
                    await _session.Client.DeleteObjectAsync(prefix, cancellationToken).ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    // no marker to delete
                }
            }
        }

        private async Task CopyAllObjectsAsync(string sourcePrefix, string destinationPrefix, CancellationToken cancellationToken)
        {
            string start = null;
            do
            {
                var page = await _session.Client.ListObjectsAsync(sourcePrefix, delimiter: null, limit: null, start: start, cancellationToken).ConfigureAwait(false);
                foreach (var obj in page.Objects)
                {
                    var destName = destinationPrefix + obj.Name.Substring(sourcePrefix.Length);
                    await _session.Client.CopyObjectAsync(obj.Name, destName, cancellationToken).ConfigureAwait(false);
                }
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));

            // Make sure the destination marker exists so empty-dir renames still surface.
            try
            {
                await _session.Client.HeadObjectAsync(destinationPrefix, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                using (var empty = new MemoryStream())
                {
                    await _session.Client.PutObjectAsync(destinationPrefix, empty, 0, DirectoryContentType, null, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static void CopyContentsGeneric(FileDirectory source, FileDirectory destination)
        {
            foreach (var file in source.GetFiles())
                file.CopyTo(destination, file.Name);

            foreach (var subDir in source.GetDirectories())
            {
                var newSubDir = destination.CreateDirectory(subDir.Name);
                CopyContentsGeneric(subDir, newSubDir);
            }
        }
    }
}
