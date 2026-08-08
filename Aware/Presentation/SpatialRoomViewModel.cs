using System.Collections.ObjectModel;
using Aware.Application;
using Aware.Domain;
using Aware.Infrastructure;
using Aware.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aware.Presentation;

public enum TrayMode { Actions, Correct }

/// <summary>
/// Owns current room, recognition state, active lens, selection, the action
/// tray and persistence (03-ARCHITECTURE state ownership). It never projects,
/// sorts or hit-tests: that is the renderer's half of the contract.
/// </summary>
public partial class SpatialRoomViewModel : ObservableObject, IDisposable
{
    private const string OnboardingSeenKey = "aware.onboarding.seen";

    private readonly IRoomRepository _rooms;
    private readonly IRoomRecognitionService _recognition;
    private readonly IFingerprintMatcher _matcher;
    private readonly IRoomLocator _locator;
    private readonly IRenderSnapshotFactory _snapshots;
    private readonly IObjectActionResolver _actions;
    private readonly ISpatialCaptureAdapter _capture;
    private readonly IRoomFingerprintProvider _fingerprints;
    private readonly IPrivacyService _privacy;
    private readonly IHapticsService _haptics;
    private readonly IMotionSettings _motion;
    private readonly ILogger<SpatialRoomViewModel> _log;
    private readonly CancellationTokenSource _lifetime = new();

    private SpatialRoom? _room;
    private bool _suppressListSync;

    [ObservableProperty] private SpatialRenderSnapshot? snapshot;
    [ObservableProperty] private SpatialLens activeLens = SpatialLens.Explore;
    [ObservableProperty] private ObjectActionTray? actionTray;
    [ObservableProperty] private string roomName = "Room";
    [ObservableProperty] private string recognitionMessage = "Preparing room model";
    [ObservableProperty] private float recognitionProgress;
    [ObservableProperty] private bool isRoomStable;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private string captureCapability = string.Empty;
    [ObservableProperty] private bool reducedMotion;
    [ObservableProperty] private bool showOnboarding;
    [ObservableProperty] private int onboardingStep;
    [ObservableProperty] private bool isObjectListVisible;

    /// <summary>
    /// True when the device senses a place but this room is not tied to one yet.
    /// Drives the Link affordance; false on desktop and the browser, where there
    /// is nothing to link with.
    /// </summary>
    [ObservableProperty] private bool canLinkPlace;

    /// <summary>
    /// True where the device can sense a place at all. Without it, creating a
    /// room would produce one with no fingerprint — a room that can never be
    /// recognized — so the affordance is hidden rather than offered and failing.
    /// </summary>
    [ObservableProperty] private bool canAddPlace;

    [ObservableProperty] private bool showAddPlace;

    [ObservableProperty] private bool showRoomList;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmAddPlace))]
    private string newPlaceName = string.Empty;

    public bool CanConfirmAddPlace => !string.IsNullOrWhiteSpace(NewPlaceName);

    /// <summary>
    /// Measure and Memories need geometry. They stay visible but disabled rather
    /// than hidden: the triad is fixed (01-PRODUCT-BRIEF, "lenses, not separate
    /// apps"), and hiding two of three would teach the wrong model of the app.
    /// </summary>
    [ObservableProperty] private bool hasModel = true;

    /// <summary>Said once, under the empty floor, rather than on every lens.</summary>
    [ObservableProperty] private string? unmodelledNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrayOpen))]
    [NotifyPropertyChangedFor(nameof(SelectedObjectName))]
    private SpatialObjectId? selectedObjectId;

    [ObservableProperty] private TrayMode trayMode = TrayMode.Actions;
    [ObservableProperty] private string correctionName = string.Empty;
    [ObservableProperty] private string correctionClass = string.Empty;

    public SpatialRoomViewModel(
        IRoomRepository rooms,
        IRoomRecognitionService recognition,
        IFingerprintMatcher matcher,
        IRoomLocator locator,
        IRenderSnapshotFactory snapshots,
        IObjectActionResolver actions,
        ISpatialCaptureAdapter capture,
        IRoomFingerprintProvider fingerprints,
        IPrivacyService privacy,
        IHapticsService haptics,
        IMotionSettings motion,
        ILogger<SpatialRoomViewModel> log)
    {
        _rooms = rooms;
        _recognition = recognition;
        _matcher = matcher;
        _locator = locator;
        _snapshots = snapshots;
        _actions = actions;
        _capture = capture;
        _fingerprints = fingerprints;
        _privacy = privacy;
        _haptics = haptics;
        _motion = motion;
        _log = log;

        reducedMotion = motion.ReducedMotion;
        _motion.Changed += OnMotionChanged;
    }

    public ObservableCollection<SpatialObjectListItem> Objects { get; } = [];

    /// <summary>Every room stored on this device, rebuilt each time the list opens.</summary>
    public ObservableCollection<RoomListItem> RoomList { get; } = [];

    public IReadOnlyList<string> KnownClasses { get; } =
    [
        "workbench", "cabinet", "tool_chest", "storage_shelf",
        "bicycle", "garage_door", "paint_cans", "unknown",
    ];

    public bool IsTrayOpen => SelectedObjectId is not null;

    public string SelectedObjectName =>
        SelectedObjectId is { } id ? _room?.FindObject(id)?.DisplayName ?? string.Empty : string.Empty;

    public string OnboardingTitle => OnboardingStep switch
    {
        0 => "Everything stays on this device",
        1 => "Drag to orbit. Tap an object.",
        _ => "One object, three lenses",
    };

    public string OnboardingBody => OnboardingStep switch
    {
        0 => "Aware builds a spatial model of your room from repeated observations. " +
             "Processing and storage are local by default, and nothing is synced unless you turn it on.",
        1 => "Drag anywhere on the model to orbit it. Tap an object to select it; tap empty space to clear.",
        _ => "Explore, Measure and Memories are three readings of the same object. " +
             "Switching between them never loses your place.",
    };

    public string OnboardingPrimaryLabel => OnboardingStep == 0 ? "Try sample garage" : "Next";

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // Page left before assembly finished.
        }
        catch (Exception ex)
        {
            // A failure here leaves an empty viewport, so it has to be visible
            // in the UI rather than only in the log.
            _log.LogError(ex, "Could not open the room model.");
            RecognitionMessage = "Could not open the room model on this device.";
        }
    }

    private async Task LoadAsync()
    {
        var ct = _lifetime.Token;

        await _privacy.LoadAsync(ct);

        var capabilities = await _capture.GetCapabilitiesAsync(ct);

        // What this device can actually sense, said plainly on first launch.
        CaptureCapability = _fingerprints.IsAvailable
            ? $"{capabilities.Summary} · {_fingerprints.Summary}"
            : capabilities.Summary;

        ShowOnboarding = !HasSeenOnboarding();

        // Seeding still runs, so the sample exists on a fresh install.
        var seeded = await _rooms.GetRoomAsync(SampleGarageFactoryId, ct);
        var rooms = await _rooms.GetRoomsAsync(ct);

        // Which room opens is the whole point: take one reading and let the place
        // the device is standing in choose, rather than always opening the sample.
        // The reading is reused by recognition below — sensing twice seconds apart
        // can disagree, and the room that was located would fail to recognize itself.
        var reading = await ReadPlaceAsync(ct);
        var located = reading is null ? null : _locator.Locate(rooms, reading);

        LogLocatorScores(rooms, reading, located);

        _room = located?.Room ?? seeded ?? rooms.FirstOrDefault();

        if (_room is null)
        {
            RecognitionMessage = "No room model is stored on this device.";
            return;
        }

        RoomName = _room.Name;
        RebuildObjectList();
        Refresh();

        await foreach (var state in _recognition.RecognizeAsync(_room, reading, ct))
        {
            RecognitionMessage = state.Message;
            RecognitionProgress = state.Progress;
            IsRoomStable = state.IsStable;
        }

        // Only offered once recognition has settled, so the reading it would store
        // is the one the user just watched being taken.
        CanLinkPlace = _room is { Fingerprint: null }
                       && _recognition.LastReading is { HasAnySignal: true };

        CanAddPlace = _fingerprints.IsAvailable;
        ApplyModelledState();
    }

    /// <summary>
    /// Why this room opened and not another, as numbers.
    ///
    /// <para>"It keeps opening the garage" is unfalsifiable without the scores:
    /// a room can lose because its signals genuinely disagree or because one
    /// Bluetooth advertiser happened to be asleep during the scan, and those need
    /// opposite fixes. Logged at Information so a `logcat | grep Locate` answers
    /// it on any device, without a debugger attached.</para>
    /// </summary>
    private void LogLocatorScores(
        IReadOnlyList<SpatialRoom> rooms,
        FingerprintReading? reading,
        RoomMatch? located)
    {
        if (reading is not { HasAnySignal: true })
        {
            _log.LogInformation("Locate: no usable reading; opening the fallback room.");
            return;
        }

        foreach (var room in rooms)
        {
            if (room.Fingerprint is not { } stored)
            {
                _log.LogInformation("Locate: {Room} is not linked to a place.", room.Name);
                continue;
            }

            var comparison = _matcher.Compare(stored, reading);

            _log.LogInformation(
                "Locate: {Room} scored {Score:0.000} ({Verdict}) · agree [{Agree}] · disagree [{Disagree}]",
                room.Name,
                comparison.Confidence,
                comparison.IsRecognized ? "recognized" : "rejected",
                string.Join(", ", comparison.Agreements),
                string.Join(", ", comparison.Contradictions));
        }

        _log.LogInformation("Locate: opening {Room}.", located?.Room.Name ?? "the fallback room");
    }

    private void ApplyModelledState()
    {
        HasModel = _room?.IsModelled ?? true;
        UnmodelledNote = HasModel
            ? null
            : "No model yet. Aware recognizes this place but has not mapped it.";

        if (!HasModel && ActiveLens != SpatialLens.Explore)
            ActiveLens = SpatialLens.Explore;
    }

    // ------------------------------------------------------------------
    // Setting up the room you are standing in (SPEC.md, recognition-only rooms)
    // ------------------------------------------------------------------

    [RelayCommand]
    private void BeginAddPlace()
    {
        NewPlaceName = string.Empty;
        ShowAddPlace = true;
    }

    [RelayCommand]
    private void CancelAddPlace() => ShowAddPlace = false;

    /// <summary>
    /// Shows every room stored on this device, and which one the user is standing
    /// in. Reads the launch reading the recognition service already took, so the
    /// list agrees with the line under the room title rather than sensing again
    /// and possibly disagreeing with it.
    /// </summary>
    [RelayCommand]
    private async Task OpenRoomsAsync()
    {
        if (_room is null) return;

        var ct = _lifetime.Token;
        var stored = await _rooms.GetRoomsAsync(ct);
        var reading = _recognition.LastReading;

        RoomList.Clear();

        foreach (var room in stored)
        {
            var recognized = reading is { HasAnySignal: true }
                             && room.Fingerprint is { } stamp
                             && _matcher.Compare(stamp, reading).IsRecognized;

            RoomList.Add(new RoomListItem(
                room.Id,
                room.Name,
                RoomListMessages.Reason(room, recognized),
                room.Id == _room.Id));
        }

        ShowRoomList = true;
    }

    [RelayCommand]
    private void CloseRoomList() => ShowRoomList = false;

    /// <summary>
    /// Switches to a room from the list. Chosen from an overlay rather than a
    /// separate route so the viewport is never unmounted: remounting the page
    /// that owns the Skia canvas and then pushing the next room's snapshot into
    /// it crashes the process natively on Skia desktop.
    /// </summary>
    [RelayCommand]
    private async Task ChooseRoomAsync(RoomListItem? item)
    {
        ShowRoomList = false;

        // Choosing the room already open is a no-op the user can still express;
        // reopening it would replay recognition for no reason.
        if (item is null || item.IsCurrent) return;

        var ct = _lifetime.Token;

        var room = await _rooms.GetRoomAsync(item.Id, ct);
        if (room is null)
        {
            StatusMessage = "That room is no longer stored on this device.";
            return;
        }

        await OpenAsync(room, ct);
    }

    /// <summary>Leaves the list for the naming sheet, which already exists here.</summary>
    [RelayCommand]
    private void AddPlaceFromList()
    {
        ShowRoomList = false;
        BeginAddPlace();
    }

    /// <summary>
    /// Creates a room for the place the device is in right now: a name, an ambient
    /// fingerprint, and no geometry.
    ///
    /// <para>The reading is taken <em>here</em> rather than when the sheet opened,
    /// because the user may have walked while naming it and the place they mean is
    /// where they are when they commit.</para>
    /// </summary>
    [RelayCommand]
    private async Task ConfirmAddPlaceAsync()
    {
        if (!CanConfirmAddPlace) return;

        var ct = _lifetime.Token;
        var name = NewPlaceName.Trim();

        FingerprintReading reading;
        try
        {
            reading = await _fingerprints.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read the place while adding a room.");
            StatusMessage = "Could not read this place. Nothing was saved.";
            return;
        }

        if (!reading.HasAnySignal)
        {
            StatusMessage = "No signals here to recognize this place by. Nothing was saved.";
            return;
        }

        // Warn on a place that already matches a stored room, but allow it: two
        // rooms can legitimately share a signature, and refusing would strand the
        // user with no way to record the second.
        var existing = await _rooms.GetRoomsAsync(ct);
        var clash = existing.FirstOrDefault(r =>
            r.Fingerprint is { } f && _matcher.Compare(f, reading).IsRecognized);

        var room = UnmodelledRoomFactory.Create(name, reading.Fingerprint, DateTimeOffset.Now);
        await _rooms.SaveRoomAsync(room, ct);

        ShowAddPlace = false;
        await OpenAsync(room, ct);

        StatusMessage = clash is null
            ? $"{room.Name} is linked to this place. Aware will recognize it from here on."
            : $"{room.Name} saved. This looks a lot like {clash.Name}, so the two may be hard to tell apart.";

        _haptics.Play(HapticKind.Confirm);
    }

    /// <summary>Switches the open room and replays recognition against it.</summary>
    private async Task OpenAsync(SpatialRoom room, CancellationToken ct)
    {
        _room = room;
        RoomName = room.Name;
        SelectedObjectId = null;
        ActionTray = null;
        TrayMode = TrayMode.Actions;
        ActiveLens = SpatialLens.Explore;

        RebuildObjectList();
        Refresh();

        await foreach (var state in _recognition.RecognizeAsync(room, _recognition.LastReading, ct))
        {
            RecognitionMessage = state.Message;
            RecognitionProgress = state.Progress;
            IsRoomStable = state.IsStable;
        }

        CanLinkPlace = _room is { Fingerprint: null }
                       && _recognition.LastReading is { HasAnySignal: true };

        // The room being switched to may be modelled or not, so the lens state and
        // the empty-floor note have to be recomputed here as well as on load. Adding
        // a place opens a room with no geometry through this path: without it the
        // new room inherits the previous room's HasModel and shows live Measure and
        // Memories buttons over a bare floor until the app is relaunched.
        ApplyModelledState();
    }

    /// <summary>
    /// Ties this model to the place the device is standing in, using the reading
    /// recognition already took. Geometry and every object ID are untouched.
    /// </summary>
    [RelayCommand]
    private async Task LinkPlaceAsync()
    {
        if (_room is null) return;
        if (_recognition.LastReading is not { HasAnySignal: true } reading) return;

        _room = _room.LinkedTo(reading.Fingerprint);
        await _rooms.SaveRoomAsync(_room, _lifetime.Token);

        CanLinkPlace = false;

        // Scored rather than asserted: comparing the reading against itself is a
        // real match, and it words itself the same way every later arrival will.
        var comparison = _matcher.Compare(reading.Fingerprint, reading);
        RecognitionMessage = RecognitionMessages.Settled(_room.Confidence, comparison, canLink: false);

        StatusMessage = $"{_room.Name} is linked to this place. " +
                        "Aware will recognize it from here on, and say so when you are somewhere else.";
        _haptics.Play(HapticKind.Confirm);
    }

    private static RoomId SampleGarageFactoryId => new("room-garage-001");

    /// <summary>
    /// One reading for the whole launch. Failure degrades to "no sensors" rather
    /// than taking startup down, matching SensorRoomRecognitionService.
    /// </summary>
    private async Task<FingerprintReading?> ReadPlaceAsync(CancellationToken ct)
    {
        if (!_fingerprints.IsAvailable) return null;

        try
        {
            var reading = await _fingerprints.ReadAsync(ct);
            return reading.HasAnySignal ? reading : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read the place while choosing a room.");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Lens and selection
    // ------------------------------------------------------------------

    [RelayCommand]
    private void SetLens(string lens)
    {
        if (!Enum.TryParse<SpatialLens>(lens, ignoreCase: true, out var parsed)) return;
        if (parsed == ActiveLens) return;

        // Guarding here as well as in XAML: a disabled button is a hint, not a
        // gate, and the command is reachable from automation.
        if (!HasModel && parsed != SpatialLens.Explore) return;

        // Camera and geometry are untouched; only the annotation layer and the
        // tray content change (06-MOTION-BRIEF).
        ActiveLens = parsed;
        StatusMessage = null;
        ResolveTray();
        Refresh();
    }

    public bool IsLensActive(SpatialLens lens) => ActiveLens == lens;

    /// <summary>Selection from the viewport. Null clears.</summary>
    public void SelectObject(SpatialObjectId? id)
    {
        if (id == SelectedObjectId) return;

        SelectedObjectId = id;
        TrayMode = TrayMode.Actions;
        StatusMessage = null;

        if (id is not null) _haptics.Play(HapticKind.Selection);

        SyncListSelection();
        ResolveTray();
        Refresh();
    }

    /// <summary>Selection from the accessible list. Same act, same identity.</summary>
    [RelayCommand]
    private void SelectFromList(SpatialObjectListItem? item)
    {
        if (_suppressListSync) return;
        SelectObject(item?.Id);
    }

    [RelayCommand]
    private void CloseTray() => SelectObject(null);

    [RelayCommand]
    private void ToggleObjectList() => IsObjectListVisible = !IsObjectListVisible;

    [RelayCommand]
    private void ToggleReducedMotion()
    {
        ReducedMotion = !ReducedMotion;
        _motion.SetUserOverride(ReducedMotion);
        Refresh();
    }

    // ------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------

    [RelayCommand]
    private async Task ExecuteActionAsync(string? actionId)
    {
        if (_room is null || SelectedObjectId is not { } id) return;
        var selected = _room.FindObject(id);
        if (selected is null) return;

        switch (actionId)
        {
            case ObjectActionIds.Measure:
            case ObjectActionIds.MeasureAgain:
                // A lens switch, not a separate screen. Identity is preserved.
                SetLens(nameof(SpatialLens.Measure));
                break;

            case ObjectActionIds.ViewHistory:
                SetLens(nameof(SpatialLens.Memories));
                break;

            case ObjectActionIds.CompareFit:
                var opening = ObjectActionResolver.FindOpening(_room, selected);
                StatusMessage = opening is null
                    ? "No object in this room has saved opening dimensions."
                    : ObjectActionResolver.CompareFit(selected, opening);
                _haptics.Play(HapticKind.Confirm);
                break;

            case ObjectActionIds.ResumeProject:
                var project = _room.FindProject(selected.AttachedProjectId);
                StatusMessage = project is null
                    ? null
                    : $"{project.Name} · {project.Status}. Last touched {project.LastActivityAt:MMM d}.";
                break;

            case ObjectActionIds.AttachProject:
                await AttachProjectAsync(selected);
                break;

            case ObjectActionIds.DetachProject:
                await UpdateObjectAsync(selected with { AttachedProjectId = null });
                StatusMessage = $"Project detached from {selected.DisplayName}.";
                break;

            case ObjectActionIds.Open:
                StatusMessage = $"{selected.DisplayName} has no attached project yet.";
                break;
        }
    }

    private async Task AttachProjectAsync(SpatialObject selected)
    {
        if (_room is null) return;

        var project = _room.Projects.FirstOrDefault();
        if (project is null)
        {
            StatusMessage = "No projects exist on this device yet.";
            return;
        }

        await UpdateObjectAsync(selected with { AttachedProjectId = project.Id });
        StatusMessage = $"{project.Name} attached to {selected.DisplayName}.";
    }

    // ------------------------------------------------------------------
    // Correction (02-UX-FLOWS). Every inferred object can be corrected, and
    // corrections outrank inference from then on.
    // ------------------------------------------------------------------

    [RelayCommand]
    private void BeginCorrection()
    {
        if (_room is null || SelectedObjectId is not { } id) return;
        var selected = _room.FindObject(id);
        if (selected is null) return;

        CorrectionName = selected.DisplayName;
        CorrectionClass = selected.SemanticClass;
        TrayMode = TrayMode.Correct;
    }

    [RelayCommand]
    private void CancelCorrection() => TrayMode = TrayMode.Actions;

    [RelayCommand]
    private async Task SaveCorrectionAsync()
    {
        if (_room is null || SelectedObjectId is not { } id) return;
        var selected = _room.FindObject(id);
        if (selected is null) return;

        var name = string.IsNullOrWhiteSpace(CorrectionName) ? selected.DisplayName : CorrectionName.Trim();
        var semanticClass = string.IsNullOrWhiteSpace(CorrectionClass) ? selected.SemanticClass : CorrectionClass;
        var changedClass = semanticClass != selected.SemanticClass;

        // The ID is untouched: renaming and reclassification must not break
        // identity (03-ARCHITECTURE stable IDs).
        await UpdateObjectAsync(selected with
        {
            DisplayName = name,
            SemanticClass = semanticClass,
            ClassificationEvidence = changedClass || name != selected.DisplayName
                ? EvidenceKind.Corrected
                : selected.ClassificationEvidence,
        });

        TrayMode = TrayMode.Actions;
        StatusMessage = (changedClass, name != selected.DisplayName) switch
        {
            (true, _) => $"Recorded as {name}. Future inference will defer to this.",
            (false, true) => $"Renamed to {name}.",
            _ => "Nothing changed.",
        };
        _haptics.Play(HapticKind.Confirm);
    }

    [RelayCommand]
    private async Task ToggleExclusionAsync()
    {
        if (_room is null || SelectedObjectId is not { } id) return;
        var selected = _room.FindObject(id);
        if (selected is null) return;

        var excluded = !selected.IsExcludedFromSuggestions;
        await UpdateObjectAsync(selected with { IsExcludedFromSuggestions = excluded });

        StatusMessage = excluded
            ? $"{selected.DisplayName} will not be used for suggestions."
            : $"{selected.DisplayName} is back in suggestions.";
    }

    [RelayCommand]
    private async Task ForgetObjectAsync()
    {
        if (_room is null || SelectedObjectId is not { } id) return;
        var selected = _room.FindObject(id);
        if (selected is null) return;

        _room = _room.WithoutObject(id);
        await _rooms.SaveRoomAsync(_room, _lifetime.Token);

        SelectedObjectId = null;
        TrayMode = TrayMode.Actions;
        ActionTray = null;
        StatusMessage = $"{selected.DisplayName} was forgotten. Its geometry is gone from this device.";

        RebuildObjectList();
        Refresh();
        _haptics.Play(HapticKind.Warning);
    }

    private async Task UpdateObjectAsync(SpatialObject updated)
    {
        if (_room is null) return;

        _room = _room.WithObject(updated with { LastObservedAt = updated.LastObservedAt });
        await _rooms.SaveRoomAsync(_room, _lifetime.Token);

        RebuildObjectList();
        SyncListSelection();
        ResolveTray();
        Refresh();
        OnPropertyChanged(nameof(SelectedObjectName));
    }

    // ------------------------------------------------------------------
    // Onboarding
    // ------------------------------------------------------------------

    [RelayCommand]
    private void AdvanceOnboarding()
    {
        if (OnboardingStep >= 2)
        {
            CompleteOnboarding();
            return;
        }

        OnboardingStep++;
        NotifyOnboardingText();
    }

    [RelayCommand]
    private void SkipOnboarding() => CompleteOnboarding();

    private void CompleteOnboarding()
    {
        ShowOnboarding = false;
        try
        {
            Windows.Storage.ApplicationData.Current.LocalSettings.Values[OnboardingSeenKey] = true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not persist the onboarding flag.");
        }
    }

    private static bool HasSeenOnboarding()
    {
        try
        {
            return Windows.Storage.ApplicationData.Current.LocalSettings.Values
                .TryGetValue(OnboardingSeenKey, out var value) && value is true;
        }
        catch
        {
            return false;
        }
    }

    private void NotifyOnboardingText()
    {
        OnPropertyChanged(nameof(OnboardingTitle));
        OnPropertyChanged(nameof(OnboardingBody));
        OnPropertyChanged(nameof(OnboardingPrimaryLabel));
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private void RebuildObjectList()
    {
        if (_room is null) return;

        _suppressListSync = true;
        Objects.Clear();
        foreach (var obj in _room.Objects.OrderByDescending(o => o.Confidence))
            Objects.Add(new SpatialObjectListItem(obj));
        _suppressListSync = false;

        SyncListSelection();
    }

    private void SyncListSelection()
    {
        _suppressListSync = true;
        foreach (var item in Objects)
            item.IsSelected = item.Id == SelectedObjectId;
        _suppressListSync = false;
    }

    private void ResolveTray()
    {
        if (_room is null || SelectedObjectId is not { } id)
        {
            ActionTray = null;
            return;
        }

        var selected = _room.FindObject(id);
        ActionTray = selected is null ? null : _actions.Resolve(_room, selected, ActiveLens);
    }

    private void Refresh()
    {
        if (_room is null) return;
        Snapshot = _snapshots.Create(_room, SelectedObjectId, ActiveLens, ReducedMotion);
    }

    private void OnMotionChanged(object? sender, EventArgs e)
    {
        ReducedMotion = _motion.ReducedMotion;
        Refresh();
    }

    partial void OnOnboardingStepChanged(int value) => NotifyOnboardingText();

    public void Dispose()
    {
        _motion.Changed -= OnMotionChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
