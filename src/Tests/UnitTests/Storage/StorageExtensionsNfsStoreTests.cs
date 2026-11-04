using FluentStorage;
using FluentStorage.Storage;
using Shouldly;
using StorageExtensions.Nfs;
using TUnit.Core;

namespace UnitTests.Storage;

public class StorageExtensionsNfsStoreTests
{
    [Test]
    public async Task Store_Exercises_File_And_Directory_Operations_Against_A_Fake_Export()
    {
        var fileSystem = new MemoryNfsFileSystem();
        using var store = new NfsStore(new NfsOptions
        {
            Host = "fake",
            Export = "/export",
            BasePath = "library"
        }, _ => fileSystem);

        (await store.IsFileSystem()).ShouldBeTrue();
        (await store.IsSeekable()).ShouldBeTrue();
        (await store.GetClient()).ShouldBe(fileSystem);

        await store.CreateDirectory("", force: false);
        await store.CreateDirectory("albums/2024", force: true);
        await store.CreateDirectory("albums/2024", force: true);
        await Should.ThrowAsync<IOException>(() => store.CreateDirectory("albums/2024", force: false));
        (await store.DirectoryExists("albums/2024")).ShouldBeTrue();
        (await store.DirectoryExists("absent")).ShouldBeFalse();

        await store.SetObject("albums/2024/song.txt", new MemoryStream("abcdefghij"u8.ToArray()), null, append: false);
        (await store.ObjectExists("albums/2024/song.txt")).ShouldBeTrue();
        (await store.ObjectExists("absent")).ShouldBeFalse();
        (await store.GetObjectInfo("albums/2024/song.txt"))!.Size.ShouldBe(10);
        (await store.GetObjectInfo("absent")).ShouldBeNull();
        (await store.GetObjectLength("albums/2024/song.txt")).ShouldBe(10);
        (await store.GetObjectLength("absent", 23)).ShouldBe(23);

        await using (var range = await store.OpenRange("albums/2024/song.txt", 3, 4))
        using (var reader = new StreamReader(range))
        {
            (await reader.ReadToEndAsync()).ShouldBe("defg");
        }

        await store.SetObject("albums/2024/song.txt", new MemoryStream("!"u8.ToArray()), null, append: true);
        (await store.GetText("albums/2024/song.txt")).ShouldBe("abcdefghij!");
        var listing = await store.ListObjects(new StorageListOptions { FolderPath = "albums", Recurse = false });
        listing.ShouldContain(item => item.Name == "2024" && item.IsFolder);

        (await store.MoveObject("albums/2024/song.txt", "albums/2024/moved.txt", false)).ShouldBeTrue();
        (await store.MoveObject("albums/2024/moved.txt", "albums/2024/moved.txt", false)).ShouldBeFalse();
        (await store.MoveObject("absent", "albums/2024/nope.txt", true)).ShouldBeFalse();
        await store.MoveDirectory("albums/2024", "albums/renamed");
        (await store.ObjectExists("albums/renamed/moved.txt")).ShouldBeTrue();
        await store.DeleteDirectory("albums", recursive: true);
        (await store.DirectoryExists("albums")).ShouldBeFalse();
        await store.DeleteObject("absent");
        using var unrooted = new NfsStore(new NfsOptions { Host = "fake", Export = "/export" }, _ => fileSystem);
        await Should.ThrowAsync<InvalidOperationException>(() => unrooted.DeleteDirectory("", recursive: false));
    }

    private sealed class MemoryNfsFileSystem : INfsFileSystem
    {
        private sealed record Entry(bool Directory, byte[] Contents, DateTime Modified);

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal)
        {
            [""] = new Entry(true, [], DateTime.UtcNow)
        };

        public object Client => this;

        public FileAttributes GetAttributes(string path)
        {
            if (!_entries.TryGetValue(Normalize(path), out var entry))
            {
                throw new FileNotFoundException(path);
            }

            return entry.Directory ? FileAttributes.Directory : FileAttributes.Normal;
        }

        public IEnumerable<string> GetFileSystemEntries(string path)
        {
            var prefix = Normalize(path);
            return _entries.Keys.Where(key => key.Length > prefix.Length &&
                key.StartsWith(prefix.Length == 0 ? "" : prefix + "/", StringComparison.Ordinal) &&
                key[prefix.Length..].TrimStart('/').IndexOf('/') < 0).ToArray();
        }

        public long GetFileLength(string path) => GetEntry(path).Contents.LongLength;
        public DateTime GetLastWriteTimeUtc(string path) => GetEntry(path).Modified;

        public Stream OpenFile(string path, FileMode mode, FileAccess access)
        {
            var key = Normalize(path);
            var exists = _entries.TryGetValue(key, out var entry);
            if (mode == FileMode.Open && !exists)
            {
                throw new FileNotFoundException(path);
            }

            var initial = (mode is FileMode.Append or FileMode.Open) && exists ? entry!.Contents : [];
            return new CommitStream(initial, bytes => _entries[key] = new Entry(false, bytes, DateTime.UtcNow))
            {
                Position = mode == FileMode.Append ? initial.Length : 0
            };
        }

        public void DeleteFile(string path)
        {
            var key = Normalize(path);
            if (_entries.TryGetValue(key, out var entry) && !entry.Directory)
            {
                _entries.Remove(key);
            }
        }

        public void DeleteDirectory(string path)
        {
            var key = Normalize(path);
            if (_entries.Keys.Any(candidate => candidate.Length > key.Length && candidate.StartsWith(key + "/", StringComparison.Ordinal)))
            {
                throw new IOException("Directory not empty");
            }

            _entries.Remove(key);
        }

        public void CreateDirectory(string path)
        {
            var key = Normalize(path);
            if (!_entries.ContainsKey(Parent(key)))
            {
                throw new DirectoryNotFoundException(Parent(key));
            }

            _entries.Add(key, new Entry(true, [], DateTime.UtcNow));
        }

        public void MoveDirectory(string source, string destination) => MoveTree(source, destination);

        public void MoveFile(string source, string destination, bool overwrite)
        {
            var oldKey = Normalize(source);
            var newKey = Normalize(destination);
            if (!_entries.TryGetValue(oldKey, out var value))
            {
                throw new FileNotFoundException(source);
            }

            if (!overwrite && _entries.ContainsKey(newKey))
            {
                throw new IOException("Destination exists");
            }

            _entries.Remove(oldKey);
            _entries[newKey] = value;
        }

        private void MoveTree(string source, string destination)
        {
            var oldKey = Normalize(source);
            var newKey = Normalize(destination);
            var moving = _entries.Where(pair => pair.Key == oldKey || pair.Key.StartsWith(oldKey + "/", StringComparison.Ordinal)).ToArray();
            foreach (var (key, _) in moving)
            {
                _entries.Remove(key);
            }

            foreach (var (key, value) in moving)
            {
                _entries[newKey + key[oldKey.Length..]] = value;
            }
        }

        private Entry GetEntry(string path) => _entries[Normalize(path)];
        private static string Normalize(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        public void Dispose() { }

        private sealed class CommitStream : MemoryStream
        {
            private readonly Action<byte[]> _commit;

            public CommitStream(byte[] initial, Action<byte[]> commit)
            {
                _commit = commit;
                Write(initial);
                Position = 0;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _commit(ToArray());
                }

                base.Dispose(disposing);
            }
        }
    }
}
