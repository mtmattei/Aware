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

    private readonly IRoomRepository _rooms;
    private readonly ILogger<PrivacyService> _log;
    private readonly string _path;

    public PrivacyService(IRoomRepository rooms, ILogger<PrivacyService> log)
    {
        _rooms = rooms;
        _log = log;
        _path = Path.Combine(
            Windows.Storage.ApplicationData.Current.LocalFolder.Path,
            "privacy.json");
    }

    public PrivacySettings Current { get; private set; } = Defaults;

    public async Task<PrivacySettings> LoadAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = await File.ReadAllTextAsync(_path, ct);
                Current = JsonSerializer.Deserialize<PrivacySettings>(json, SpatialJson.Options) ?? Defaults;
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
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(settings, SpatialJson.Options), ct);
    }

    public async Task ForgetAllSpatialDataAsync(CancellationToken ct)
    {
        await _rooms.DeleteAllAsync(ct);
        Current = Defaults;
        if (File.Exists(_path)) File.Delete(_path);
    }

    public async Task<string> ExportModelAsync(RoomId id, CancellationToken ct)
    {
        var room = await _rooms.GetRoomAsync(id, ct)
            ?? throw new InvalidOperationException($"Room {id.Value} is not stored on this device.");

        var target = Path.Combine(
            Windows.Storage.ApplicationData.Current.LocalFolder.Path,
            $"{id.Value}-export.json");

        await File.WriteAllTextAsync(
            target,
            JsonSerializer.Serialize(room, SpatialJson.ExportOptions),
            ct);

        return target;
    }
}
