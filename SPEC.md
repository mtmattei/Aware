# SPEC — Recognition-only rooms

Status: written 2026-08-07, not implemented.
Implement in a **fresh session** that reads this cold, plus `CLAUDE.md`, `HANDOFF.md`,
and the ten briefs in
`OneDrive - Uno Platform\Desktop\unOS\AI-builds\aware\Aware\docs`.

## The problem

A user cannot set up the room they are standing in. There is no room-creation path
anywhere in the app: `IRoomRepository` can get, save and delete, but only
`SampleGarageFactory` ever constructs a room. `ISpatialCaptureAdapter.CaptureAsync`
is declared and **never called** — `SpatialRoomViewModel` uses only
`GetCapabilitiesAsync`. `02-UX-FLOWS.md` step 2 offers *"Choose `Try sample garage`
or `Scan a room`"*; the Scan branch was never built, and it needs Phase 2 capture.

Meanwhile `LinkedTo` (commit `e1c49a5`) lets the **sample garage** be tied to a real
place. Link it in a kitchen and the app says "you are here" while drawing a garage.
Recognition is honest; the geometry is fiction. That incoherence is the immediate
thing to fix, and it does not need ARCore.

## The idea

Separate **knowing where you are** from **knowing what the room looks like**. A
recognition-only room has a name and an ambient fingerprint and no geometry at all.
Aware recognizes it on return and says so, and says plainly that it has no model yet.

This is `10-ROADMAP.md` Phase 3's "repeated-visit room matching" decoupled from
Phase 2 capture. Every part it needs already exists and is tested: `FingerprintMatcher`,
`IRoomFingerprintProvider`, `JsonRoomRepository`, `RecognitionMessages`.

It also completes the two-confidences split. Model confidence and place confidence
became separate numbers in `e1c49a5`; a recognition-only room is simply the case where
the first does not exist yet and the second does.

---

## Architecture Brief

### Pattern choice

**MVVM (`CommunityToolkit.Mvvm`), not MVUX.** `~/.claude/rules/uno-scaffolding.md`
defaults to MVUX for async/reactive app state, but existing project architecture wins
and must not change mid-project: this app is MVVM throughout
(`SpatialRoomViewModel : ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`),
and the `UnoFeatures` set has `Mvvm` in and `MVUX` out. Introducing MVUX for one screen
would mean two state idioms and a second binding style in one app.

**XAML binding: `{Binding}`, not `x:Bind`.** The scaffolding rule prefers `x:Bind` for
non-MVUX pages, but `SpatialRoomPage.xaml` is `{Binding}` end to end. Existing project
code wins for local conventions (evidence hierarchy). Do not mix within a page.

### State model

A room becomes two independent halves, both optional in principle and never conflated:

| half | carried by | means |
|---|---|---|
| geometry | `Shell`, `Objects`, `Confidence` | what the room looks like |
| place | `Fingerprint` | which physical place it is |

`SpatialRoom.Fingerprint` is already nullable. Add the mirror:

```csharp
/// <summary>False for a recognition-only room: known place, no model yet.</summary>
[JsonIgnore]
public bool IsModelled => Shell.Count > 0 || Objects.Count > 0;
```

Do **not** add an enum or a `RoomKind` discriminator. The two booleans
(`IsLinkedToPlace`, `IsModelled`) describe every combination that can exist, and a
discriminator would drift out of sync with the data it claims to describe.

`Confidence` on an unmodelled room is meaningless and must never be rendered as a
percentage. `RecognitionMessages.Settled` gains an `isModelled` parameter and a new
leading clause:

| modelled | linked | line |
|---|---|---|
| yes | no sensors | `98% confident · stable room model` |
| yes | unlinked | `98% confident · not linked to a place yet` |
| yes | match | `98% confident · you are here · 3 signals match` |
| yes | mismatch | `98% confident · you are somewhere else · 3 signals disagree` |
| **no** | **match** | `No model yet · you are here · 3 signals match` |
| **no** | **mismatch** | `No model yet · you are somewhere else` |
| **no** | **no sensors** | `No model yet · nothing to recognize it by` |

### Which room opens

This is the feature. On launch, take one reading, score it against **every** stored
room, and open the best match at or above `FingerprintComparison.IsRecognized`
(0.6). Ties and misses fall back to the most recently opened room, then the sample.

New service, Application layer, pure and testable:

```csharp
public interface IRoomLocator
{
    /// <summary>Best-matching stored room for a live reading, or null if none clears the bar.</summary>
    RoomMatch? Locate(IReadOnlyList<SpatialRoom> rooms, FingerprintReading live);
}

public sealed record RoomMatch(SpatialRoom Room, FingerprintComparison Comparison);
```

Keep it a pure function of `(rooms, reading)` for the same reason `FingerprintMatcher`
is: the "which room am I in" rule is the one most worth testing and the one most likely
to be got subtly wrong.

### Repository

`IRoomRepository` already has `GetRoomsAsync`/`SaveRoomAsync`/`DeleteRoomAsync` and
`JsonRoomRepository` already writes one file per room. **No repository change is
needed** — verify this rather than assuming it, since the seeding path currently
special-cases the sample room id.

Add a factory beside `SampleGarageFactory`:

```csharp
public static SpatialRoom CreateUnmodelled(string name, RoomFingerprint fingerprint)
```

New `RoomId` from a GUID. `Confidence: 0f`, `ModelVersion: 0`, empty `Shell`/`Objects`/
`Projects`. Never reuse `SampleGarageFactory.GarageId`.

### Navigation

Region-based, routes registered centrally in `App.RegisterRoutes`, no code-behind
navigation (`~/.claude/rules/uno-scaffolding.md`). Today there is one nested route,
`Room`, `IsDefault: true`. Add:

```csharp
new RouteMap("Rooms", View: views.FindByViewModel<RoomListViewModel>()),
```

`Room` stays the default so the app still opens straight into a model — the room *is*
the interface (`01-PRODUCT-BRIEF.md`). The list is a destination, not a gate.

Room creation is a **dialog**, not a page: one text field and a confirm. Follow
`uno-navigation`'s dialog route pattern rather than hand-rolling a `ContentDialog` —
and note the runtime gotcha in `uno-scaffolding.md`: an implicit
`Style TargetType="ContentDialog"` never reaches a `ContentDialog` *subclass*.

### Services and dependencies

No new packages. No new DI lifetimes beyond one `AddSingleton<IRoomLocator, RoomLocator>()`.

### Data flow

```
launch
  → IRoomFingerprintProvider.ReadAsync            (once, shared)
  → IRoomRepository.GetRoomsAsync
  → IRoomLocator.Locate(rooms, reading)
  → open matched room, else last opened, else sample
  → SensorRoomRecognitionService.RecognizeAsync   (reuses the same reading)
```

**The reading is taken once per launch and shared.** Locating and then recognizing
must not read the sensors twice: two readings seconds apart can disagree, and the room
that was located would then fail to recognize itself. Thread the reading through, or
have `SensorRoomRecognitionService` accept an optional pre-taken reading.

### Platform constraints

- Desktop and WebAssembly have no sensors. Recognition-only rooms cannot be created
  there without `AWARE_SIMULATE_SENSORS` — **hide the create affordance when
  `IRoomFingerprintProvider.IsAvailable` is false** rather than offering an action that
  cannot produce a usable room.
- Android needs `NEARBY_WIFI_DEVICES`, asked once per install (`94b199d`). A user who
  declined still gets magnetometer, light and pressure; the matcher renormalizes and
  three signals were verified sufficient on a Pixel 8.
- iOS remains build-verified only. No Mac, no device.

### Testing and validation

Unit tests in `tests/Aware.Tests` (files are compiled in, not project-referenced —
add each new source file to the `<Compile Include>` list in `Aware.Tests.csproj`):

- `RoomLocatorTests` — picks the best match; returns null when nothing clears 0.6;
  ignores rooms with no fingerprint; deterministic on a tie.
- `RecognitionMessagesTests` — every row of the table above.
- `PersistenceTests` — an unmodelled room round-trips with empty collections and no
  geometry keys; `IsModelled` is not serialized.
- `UnmodelledRoomFactoryTests` — unique ids, never the sample id.

Runtime verification is in the Interaction Brief.

---

## Design Brief

### Visual direction

Nothing new. This feature must look like it was always there, so it reuses the existing
tokens exactly: `AwareEyebrowTextStyle`, `AwarePlaceTitleTextStyle`,
`AwareSupportTextStyle`, `AwareQuietButtonStyle`, `AwarePillRadius`,
`AwareSurfaceBrush`, `AwareHairlineBrush`, `AwareModelRearBrush`.

Do not introduce new colors, type sizes, or a second card style. If something seems to
need one, it is a sign the layout is wrong, not the palette.

### The empty model

A recognition-only room's viewport is the hard design problem: there is nothing to draw
and the room is supposed to be the interface.

**Draw the floor plate only** — the same ground plane the seeded room stands on, at the
same camera, with no walls and no objects. It reads as a room whose shape is not known
yet rather than as a broken render, it keeps orbit meaningful, and it costs nothing:
`SpatialRoomRenderer` already draws the plate.

Do **not** use a spinner, a dashed outline, or an illustration. `02-UX-FLOWS.md` is
explicit: no empty spinner, and artifacts before explanatory text.

Under it, one line of `AwareSupportTextStyle`: *"No model yet. Aware recognizes this
place but has not mapped it."*

### Layout

The top-left column already flows in one stack (commit `713bf3f`) — eyebrow, title,
recognition line, progress, optional Link button, lens pills. Room switching joins it
as a quiet button under the title, matching the `Objects`/`Motion` treatment
top-right. Do not add a nav bar.

The lens pills stay visible on an unmodelled room but Measure and Memories are
**disabled, not hidden** — the lenses are a fixed triad (`01-PRODUCT-BRIEF.md`:
"Explore, Measure and Memories are lenses, not separate apps") and hiding them would
teach the wrong model of the app.

### Room list

A plain vertical list in the existing surface treatment. Per row: room name
(`AwarePlaceTitleTextStyle` one step down), and one support line that is the *reason*
it is listed — `Recognized here now`, `3 objects · not linked to a place`,
`No model yet`. Rows are **44 px minimum**, matching the accessible object list
already built (`08-ACCESSIBILITY-TESTS.md`).

The currently-open room is marked with the sage selection material already used for a
selected object. No checkmarks, no chevrons.

### Responsive

The list is the only new surface that needs it. Phone: full width, one column.
Desktop: cap at 430 px and align left, matching the tray's existing desktop treatment.
Use `utu:Responsive` rather than a hand-rolled `VisualStateManager`.

---

## Interaction Brief

### Flow — create the room you are in

1. From the room list, **Add this place**. Hidden entirely where
   `IRoomFingerprintProvider.IsAvailable` is false.
2. A dialog: one text field, "What is this room called?", confirm and cancel.
   Confirm is disabled while the field is empty or whitespace.
3. On confirm the app takes a reading, creates the room, saves, and opens it.
4. The room opens on the floor plate, reading `No model yet · you are here · N signals
   match`.

The reading is taken **at confirm**, not at dialog open — the user may walk while
naming it, and the place they mean is where they are when they commit.

### Flow — return to a known room

Launch → one reading → best match opens automatically → `No model yet · you are here ·
N signals match`. No prompt, no picker. Being recognized without asking is the product.

### Flow — an unrecognized place

No stored room clears 0.6. Open the most recently opened room, showing
`… · you are somewhere else`, with **Add this place** offered inline in the recognition
column rather than only in the list. This is the moment a user most wants to create a
room, and making them find the list would waste it.

### States

- **Empty** (no rooms at all, sensors present): the room list is the first screen, with
  a single primary action. Never a blank list.
- **Empty** (no rooms, no sensors): the sample garage, exactly as today. The app is
  still fully usable; it simply cannot learn a place.
- **Loading**: recognition already paces itself in four stages; an unmodelled room has
  no geometry to assemble, so collapse to two — *"Reading this place"* then settled.
  Do not replay furniture stages for a room with no furniture.
- **Error**: a reading that throws degrades to no-signal and the room opens unlinked,
  never a failure dialog. Match `SensorRoomRecognitionService`'s existing behaviour.
- **Duplicate place**: creating a room whose fingerprint already matches an existing
  one at ≥ 0.6 warns — *"This looks like <name>. Add it anyway?"* — and allows it.
  Warn, do not block: two rooms can legitimately share a signature.

### Feedback

`HapticKind.Confirm` on create, matching the correction flow. Status message on create:
*"<name> is linked to this place. Aware will recognize it from here on."*

### Animation

Reuse the existing crossfade. No new curves. Reduced motion collapses to the 150 ms
crossfade already implemented, and the room-switch transition must respect it.

### Accessibility

- Room list rows: `AutomationProperties.Name` = name plus its support line, so a screen
  reader gets the reason without needing the visual grouping.
- The recognition line already carries `LiveSetting="Polite"` — an automatic room switch
  must announce, or a blind user has no idea the room changed.
- Disabled lens buttons need `AutomationProperties.HelpText` explaining *why*
  ("Measure needs a model of this room").
- Dialog field needs a label, not just placeholder text.

### Runtime verification

Desktop, via the simulated provider — this is why it exists (`713bf3f`):

```powershell
$env:AWARE_SIMULATE_SENSORS = "1"          # place A
$env:AWARE_SIMULATE_SENSORS = "elsewhere"  # place B
```

1. Mode `1`, create "Place A" → opens, `No model yet · you are here`.
2. Relaunch in mode `1` → Place A opens automatically.
3. Relaunch in mode `elsewhere` → Place A does **not** open; unrecognized state with
   **Add this place** offered.
4. Create "Place B" in mode `elsewhere`, relaunch alternating modes → the correct room
   opens each time. **This is the acceptance test.**
5. Mode unset → no create affordance, sample garage as today, `stable room model`.

Drive with `tools/Send-Input.ps1` + `tools/Capture-Window.ps1`. **Send-Input takes
client coordinates; Capture-Window captures the whole window** — subtract (8, 31) at
96 DPI or run `tools/Diagnose-Input.ps1`. This cost an hour on 2026-08-07.

Android, on the Pixel 8: create a room in one physical room, carry the phone to another,
relaunch, confirm it does not open. Then create the second room and confirm both are
recognized. Launch with `am start -n com.companyname.aware/crc64069453ae4e9f0b37.MainActivity`
— **not** `monkey`, which injects a random event after launching.

---

## Implementation Plan

1. **Domain.** `IsModelled`, `CreateUnmodelled` factory, tests. No UI.
2. **Messages.** `RecognitionMessages.Settled` gains `isModelled`; table above; tests.
   Existing rows must not change — pin them first.
3. **Locator.** `IRoomLocator` + `RoomLocator` + `RoomLocatorTests`. Pure, no UI.
4. **Single reading.** Thread one reading through locate → recognize. Verify the
   sensors are read once per launch, not twice.
5. **Startup selection.** Open the located room. Verify on desktop with the simulated
   provider, steps 1–3 above.
6. **Room list.** `RoomListViewModel` + page + route. Sage marker, 44 px rows.
7. **Create dialog.** Field, validation, duplicate warning, haptic, status message.
8. **Unmodelled viewport.** Floor plate, disabled lenses with help text, support line.
9. **Full runtime pass.** Desktop acceptance test (step 4), then the Pixel.
10. **Handoff.** Update `HANDOFF.md`, `CHECKLIST.md` deviations, commit per step.

Commit at each step. Do not batch — step 5 is the one most likely to need rework, and
it should not drag the list UI back with it.

## Unresolved Questions

- **Does the sample garage stay linkable?** Once real rooms exist, "Link to this place"
  on a fictional garage is the incoherence this spec set out to fix. Options: drop the
  Link button from the sample only; keep it as a demo affordance; or delete the sample
  once the user has a real room. **Recommend dropping it from the sample** — the button
  exists to link a room to a place, and the sample is not a place. Decide before step 7.
- **What happens to a recognition-only room when Phase 2 capture lands?** The intent is
  that geometry fills in beneath an existing `RoomId`, so a room keeps its identity and
  history across becoming modelled. Nothing here should make that harder — but it is
  untested until Phase 2 exists.
- **Is 0.6 the right bar for auto-opening a room?** It is `IsRecognized`'s existing
  threshold, borrowed rather than chosen. Wrongly opening the wrong room is worse than
  opening none, so the auto-open bar may want to be higher than the display bar. Needs
  real multi-room data on the Pixel before tuning.
- **Bluetooth remains unread.** `BluetoothFeatureHash` is still empty. A fifth signal
  would sharpen every match here, and matters more with several rooms than with one.
- **How many rooms before the list needs search or grouping?** Assume under ten. Revisit
  if that stops being true.

## Pinned versions

Uno.Sdk `6.6.42` (`global.json`), .NET `10.0.302`, TFMs
`net10.0-desktop;net10.0-android;net10.0-ios;net10.0-browserwasm`. Test-only packages in
`Directory.Packages.props`; the app takes everything from the Uno.Sdk implicit set.
SkiaSharp is pinned to `3.119.2` to match what Uno.Sdk 6.6.42 bundles — a managed/native
mismatch is the first thing to suspect on a renderer crash. **Do not bump any of these
as part of this feature.**
