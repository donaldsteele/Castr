using Castr.Core.Security;

namespace Castr.Core.Trust;

/// <summary>
/// A JSON-file-backed trust store. Loads eagerly at construction and persists synchronously after every
/// mutation — trust decisions must never be lost to a crash between "the user clicked trust" and "the
/// file actually saved."
/// </summary>
public sealed class FileTrustStore : ITrustStore
{
    private readonly string _path;
    private readonly InMemoryTrustStore _inner = new();

    public FileTrustStore(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            foreach (var entry in TrustStoreJsonCodec.Decode(File.ReadAllText(path)))
                _inner.Upsert(entry);
        }
    }

    public TrustEntry? Find(PublicKeyId publicKeyId) => _inner.Find(publicKeyId);

    public IReadOnlyList<TrustEntry> All() => _inner.All();

    public void Upsert(TrustEntry entry)
    {
        _inner.Upsert(entry);
        Save();
    }

    public void Remove(PublicKeyId publicKeyId)
    {
        _inner.Remove(publicKeyId);
        Save();
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Write to a temp file then move into place so a crash mid-write can't corrupt the trust store.
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, TrustStoreJsonCodec.Encode(_inner.All()));
        File.Move(tempPath, _path, overwrite: true);
    }
}
