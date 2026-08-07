using System.Text.Json;
using Aware.Application;
using Aware.Domain;

namespace Aware.Infrastructure;

/// <summary>
/// Offline-first local persistence: one JSON file per room in the app's local
/// folder. Nothing leaves the device (07-DATA-PRIVACY defaults).
/// </summary>
public sealed class JsonRoomRepository : IRoomRepository
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<JsonRoomRepository> _log;
    private readonly string _root;
    private Dictionary<RoomId, SpatialRoom>? _cache;

    public JsonRoomRepository(ILogger<JsonRoomRepository> log)
    {
        _log = log;
        _root = Path.Combine(
            Windows.Storage.ApplicationData.Current.LocalFolder.Path,
            "spatial");
    }

    public async Task<IReadOnlyList<SpatialRoom>> GetRoomsAsync(CancellationToken ct)
    {
        var rooms = await LoadAsync(ct);
        return rooms.Values.ToArray();
    }

    public async Task<SpatialRoom?> GetRoomAsync(RoomId id, CancellationToken ct)
    {
        var rooms = await LoadAsync(ct);
        return rooms.GetValueOrDefault(id);
    }

    public async Task SaveRoomAsync(SpatialRoom room, CancellationToken ct)
    {
        var rooms = await LoadAsync(ct);

        await _gate.WaitAsync(ct);
        try
        {
            rooms[room.Id] = room;
            Directory.CreateDirectory(_root);
            await File.WriteAllTextAsync(
                PathFor(room.Id),
                JsonSerializer.Serialize(room, SpatialJsonContext.Default.SpatialRoom),
                ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteRoomAsync(RoomId id, CancellationToken ct)
    {
        var rooms = await LoadAsync(ct);

        await _gate.WaitAsync(ct);
        try
        {
            rooms.Remove(id);
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _cache = null;
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<RoomId, SpatialRoom>> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is not null) return _cache;

            var rooms = new Dictionary<RoomId, SpatialRoom>();

            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, ct);
                        var room = JsonSerializer.Deserialize(json, SpatialJsonContext.Default.SpatialRoom);
                        if (room is not null) rooms[room.Id] = room;
                    }
                    catch (Exception ex)
                    {
                        // A corrupt room file must not take the app down; the seed
                        // below restores a usable model.
                        _log.LogWarning(ex, "Could not read stored room {File}.", file);
                    }
                }
            }

            if (rooms.Count == 0)
            {
                var seeded = SampleGarageFactory.Create();
                rooms[seeded.Id] = seeded;
                Directory.CreateDirectory(_root);
                await File.WriteAllTextAsync(
                    PathFor(seeded.Id),
                    JsonSerializer.Serialize(seeded, SpatialJsonContext.Default.SpatialRoom),
                    ct);
            }

            return _cache = rooms;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathFor(RoomId id) =>
        Path.Combine(_root, $"{Sanitize(id.Value)}.json");

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
