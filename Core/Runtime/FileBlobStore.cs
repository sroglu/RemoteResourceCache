using System;
using System.IO;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// <see cref="IBlobStore"/> over a directory. Each key maps to one file named by the xxHash3 of the key
    /// (fast non-crypto — filename derivation, not integrity), so arbitrary keys/URLs become collision-resistant,
    /// filesystem-safe names. Writes publish atomically (unique temp file + rename) so a crash or cancelled fetch
    /// never leaves a partial blob at the final path — a present file is therefore always complete, which is what
    /// makes the offline-first read safe. An optional <c>partition</c> scopes the store to a sub-directory of the
    /// root, so several independent caches can share one root without their blobs (or their <see cref="Clear"/>s)
    /// colliding.
    /// </summary>
    public sealed class FileBlobStore : IBlobStore
    {
        private readonly string _root;

        /// <param name="rootDirectory">Directory the blobs live in.</param>
        /// <param name="partition">Optional sub-scope under the root; null/empty = the root itself.</param>
        public FileBlobStore(string rootDirectory, string partition = null)
        {
            _root = string.IsNullOrEmpty(partition) ? rootDirectory : Path.Combine(rootDirectory, partition);
            Directory.CreateDirectory(_root);
        }

        /// <summary>The bare filename (no directory) a key maps to — the disk index reconciles files by this name.</summary>
        public string FileNameFor(string key) => XxHash3.HashToHex(key);

        public bool TryRead(string key, out byte[] bytes)
        {
            string path = PathFor(key);
            if (!File.Exists(path))
            {
                bytes = null;
                return false;
            }
            bytes = File.ReadAllBytes(path);
            return true;
        }

        public void Write(string key, byte[] bytes)
        {
            string path = PathFor(key);
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temp, bytes);
            // Rename over any prior blob: Move won't overwrite, so clear the old file first. The window is
            // harmless because the temp already holds the complete new bytes.
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public bool Exists(string key) => File.Exists(PathFor(key));

        public void Delete(string key)
        {
            string path = PathFor(key);
            if (File.Exists(path)) File.Delete(path);
        }

        public DateTime GetTimestampUtc(string key) => File.GetLastWriteTimeUtc(PathFor(key));

        /// <summary>
        /// Deletes every blob (and any leftover temp write) in this store's directory, leaving foreign files
        /// (e.g. a higher tier's metadata sidecar) alone by only removing files whose name matches the blob
        /// naming shape — the 16-char hex xxHash3 digest — plus its atomic-write temps.
        /// </summary>
        public void Clear()
        {
            foreach (string path in Directory.GetFiles(_root))
            {
                string name = Path.GetFileName(path);
                if (IsBlobName(name) || name.Contains(".tmp-"))
                    File.Delete(path);
            }
        }

        private static bool IsBlobName(string name)
        {
            if (name.Length != 16) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHex) return false;
            }
            return true;
        }

        private string PathFor(string key) => Path.Combine(_root, FileNameFor(key));
    }
}
