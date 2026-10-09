using System.Text.Json;

namespace AutopilotQuant.Core.Forward;

// One local writer. A complete snapshot is flushed before replacement and publication.
// Corrupt state is a startup failure, never permission to silently reset an account.
public sealed class PaperSessionStore : IDisposable
{
    private readonly string _path;
    private readonly FileStream _lease;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
    public string SnapshotPath => _path;
    public string BackupPath => _path + ".bak";
    public bool HasSnapshot => File.Exists(_path);
    public DateTimeOffset? SnapshotLastWriteAt => HasSnapshot ? new DateTimeOffset(File.GetLastWriteTimeUtc(_path)) : null;

    public PaperSessionStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.GetFullPath(Path.Combine(directory, "paper-session.json"));
        _lease = new FileStream(Path.Combine(directory, "paper-session.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public PaperSessionState Load()
    {
        if (!File.Exists(_path)) return new();
        var state = JsonSerializer.Deserialize<PaperSessionState>(File.ReadAllBytes(_path), Json)
            ?? throw new InvalidDataException("Paper session is empty.");
        if (state.Version != 1) throw new InvalidDataException("Unsupported paper session version.");
        if (!Guid.TryParseExact(state.SessionId, "N", out _) || state.Revision is < 0 or long.MaxValue)
            throw new InvalidDataException("Invalid paper session identity or revision.");
        return state;
    }

    public void Save(PaperSessionState state) => WriteAtomic(_path, state);

    public static void WriteAtomic<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
    }

    public void Dispose() => _lease.Dispose();
}
