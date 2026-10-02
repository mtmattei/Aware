using System.Text.Json;
using Aware.Application;
using Aware.Domain;

namespace Aware.Infrastructure;

/// <summary>
/// The controls listed in 07-DATA-PRIVACY, backed by local storage only.
/// Defaults are the privacy-preserving ones: nothing paused, nothing synced,
/// no imagery retained.
/// </summary>
public sealed class PrivacyService : IPrivacyService
{
    private static readonly PrivacySettings Defaults = new(
        AwarenessPaused: false,
        CloudSyncEnabled: false,
        RetainImagery: false,
        AcousticSensingEnabled: true);

    /// <summary>
    /// Exports land beside the settings file rather than under the room store,
    /// so a room deleted from the list does not take its export with it. The
    /// pattern is what lets "forget everything" find them again.
    /// </summary>
    private const string ExportPattern = "*-export.json";

    private readonly IRoomRepository _rooms;
    private readonly ILogger<PrivacyService> _log;
    private readonly string _folder;
    private readonly string _path;

    public PrivacyService(IRoomRepository rooms, ILogger<PrivacyService> log)
    {
        _rooms = rooms;
        _log = log;
        _folder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        _path = Path.Combine(_folder, "privacy.json");
    }

    public PrivacySettings Current { get; private set; } = Defaults;

    public async Task<PrivacySettings> LoadAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = await File.ReadAllTextAsync(_path, ct);
                Current = JsonSerializer.Deserialize(json, SpatialJsonContext.Default.PrivacySettings) ?? Defaults;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read privacy settings; falling back to defaults.");
            Current = Defaults;
        }

        return Current;
    }

    public async Task SaveAsync(PrivacySettings settings, CancellationToken ct)
    {
        Current = settings;
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(settings, SpatialJsonContext.Default.PrivacySettings), ct);
    }

    public async Task ForgetAllSpatialDataAsync(CancellationToken ct)
    {
        await _rooms.DeleteAllAsync(ct);
        Current = Defaults;
        if (File.Exists(_path)) File.Delete(_path);

        // An export is a full copy of a room, so "forget everything" that left
        // them behind would have erased the originals and kept the copies.
        foreach (var export in Directory.EnumerateFiles(_folder, ExportPattern))
            File.Delete(export);
    }

    public async Task<string> ExportModelAsync(RoomId id, CancellationToken ct)
    {
        var room = await _rooms.GetRoomAsync(id, ct)
            ?? throw new InvalidOperationException($"Room {id.Value} is not stored on this device.");

        var target = Path.Combine(_folder, $"{id.Value}-export.json");

        await File.WriteAllTextAsync(
            target,
            JsonSerializer.Serialize(room, SpatialJsonExportContext.Default.SpatialRoom),
            ct);

        return target;
    }
}
