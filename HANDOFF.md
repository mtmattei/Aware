# HANDOFF — Aware spatial room fingerprint (Phase 1 build)
Updated: 2026-08-07

## Where we are

Built the Aware app from the briefs in
`OneDrive - Uno Platform\Desktop\unOS\AI-builds\aware\Aware\docs` (10 briefs, read cold).
Scaffolded a fresh Uno Recommended app at `C:\Users\Platform006\Aware` and implemented
**Phase 1 (prototype parity)** from `10-ROADMAP.md` end to end: seeded garage, progressive
reconstruction, orbit, hit testing, three lenses, measurements, object-attached project,
local persistence, simulated recognition, accessible object list, reduced motion, plus the
correction and first-launch flows from `02-UX-FLOWS.md`.

The starter code under `src/Aware` in the briefs was used as a design reference, not merged
verbatim — it binds an `SKCanvasElement.Draw` event and `SKCanvasElementDrawEventArgs` that
do not exist (the real API is `RenderOverride(SKCanvas, Size)`), draws a fixed four faces
regardless of yaw, and hit-tests axis-aligned bounding boxes.

## Last verified state

- **Build:** all four TFMs, **0 warnings / 0 errors**. Uno.Sdk 6.6.42 / .NET 10.0.302.
  `net10.0-desktop` (Release), `net10.0-android`, `net10.0-ios`, `net10.0-browserwasm`.
- **WebAssembly runtime: verified in Chrome.** `SKCanvasElement` renders the full model,
  recognition and progressive assembly run, navigation resolves the `Room` route, hit testing
  and the tray work, `LocalSettings` persists the onboarding flag across reloads, and the
  bundled serif resolves.
- **Android runtime: verified on a physical Pixel 8 (Android 17).** Full model renders, touch
  selection and the tray work, drag-to-orbit is correctly distinguished from a tap and
  preserves selection, the correction sheet and its soft keyboard behave, and the drawn
  evidence markers render. iOS remains **build-verified only** — no Mac or device.
- **Desktop runtime:** verified by launching the Release exe and driving it with synthesized input,
  captured via `PrintWindow(PW_RENDERFULLCONTENT)`. Confirmed: progressive assembly
  (shell → furniture, message and geometry in step); orbit drag + inertia; keyboard orbit and
  Home reset; tap-to-select with sage selection material; empty-space tap clears; lens switch
  preserving camera and selection; Measure guides with correct cm labels and an explicitly
  unmeasured axis; compare-fit (76 × 125 through a 244 × 213 opening → 88 cm clearance);
  Memories pins distinguishing observed from inferred by shape; accessible object list
  mirroring selection; correction sheet; and an exclusion surviving a full app restart with
  the object's ID and geometry intact.
- **App MCP:** not available this session — the project `.mcp.json` only loads from the
  session root, and this session was rooted in the briefs folder. A session started in
  `C:\Users\Platform006\Aware` would get `uno_app_start` and the peer tools.
- **Tests:** `tests/Aware.Tests`, **108 tests**, all passing, ~4 s. Covers the Projection, Hit
  testing, Recognition and Actions blocks of `tests/TEST-CHECKLIST.md` and
  `08-ACCESSIBILITY-TESTS.md`, plus `PersistenceTests` round-tripping the model through the
  source-generated context. Verified the suite bites twice over: reversing the hit-buffer walk
  in `SpatialRoomRenderer.HitTest` fails `ClosestObjectWinsWhenTwoOverlap`, and restoring the
  collapsed `comparison?.Confidence ?? room.Confidence` fails
  `StandingInADifferentRoomDoesNotLowerModelConfidence` (both mutations reverted).
- **Real sensors: working on the Pixel 8.** `AndroidFingerprintProvider` reads the magnetic
  vector, ambient light, air pressure and a hash of the Wi-Fi neighbourhood; `FingerprintMatcher`
  scores it against the stored room. Against the seeded garage — whose fingerprint was fictional —
  the device correctly refused to confirm a room it is not in. That measurement was right; the
  way it was *displayed* was the bug, now fixed (see "Two confidences" below). **The device has
  not been re-tested since.**
- **Git:** `main`, working tree clean. Latest: `aeadc56`.

## Recognition-only rooms — in progress (2026-08-07)

`SPEC.md` step 7 was the ask; steps 1, 2, 4, 5 and 7 are **built and verified**, leaving
3 (locator tests — done), 6 (room list) and 8 (unmodelled viewport).

A user can now set up the room they are standing in: **Add this place** names it, stores an
ambient fingerprint and no geometry, and on relaunch `IRoomLocator` scores one reading
against every stored room and opens the best match. The sample garage is only the fallback.

Desktop acceptance test **passes** (`AWARE_SIMULATE_SENSORS`):

| mode | result |
|---|---|
| `1`, fresh | garage, `98% confident · not linked to a place yet`, both buttons |
| create "Place A" | opens it, `No model yet · you are here · 4 signals match` |
| restart, `1` | **Place A opens automatically** |
| restart, `elsewhere` | Place A does *not* open; garage fallback + Add this place |

Pixel 8 got steps 1/2/7 only — created a real "Kitchen" reading
`No model yet · you are here · 3 signals match`. **The locator commit never reached the
device** (it disconnected mid-deploy), so on the phone a relaunch still opens the garage.
Redeploy `net10.0-android` to finish that.

**Not built, and visible:** an unmodelled room's viewport is empty rather than drawing the
floor plate, and Measure/Memories are still enabled on a room with nothing to measure. Both
are step 8.

**Left on desktop:** a "Place A" room in LocalState from the acceptance run.

## Next actions (in order)

1. **Redeploy Android** so the phone gets the locator, then confirm the Kitchen opens by
   itself: carry the phone out of that room, relaunch, confirm it does *not* open, walk back,
   confirm it does. This is the one thing desktop simulation cannot stand in for.
2. **SPEC step 8** — floor plate for an unmodelled room, disabled lenses with help text.
   Currently an empty viewport, which reads as broken rather than as "shape unknown".
3. **SPEC step 6** — the room list, so more than one real room is reachable.
4. **Confirm the link flow on the Pixel 8 with real sensors.** All four states are verified on
   desktop through the simulated provider (below), so this is confirmation against real
   hardware, not first contact. Delete the app's stored room first or reinstall: a room saved
   before `e1c49a5` still carries the old invented fingerprint and will keep reporting a
   contradiction, which looks like the fix did not take.
5. Decide the portrait framing. On the Pixel the model fills 88% of the width but only ~24%
   of the height, because a 1.6:1 projected room cannot fill a 1:2.2 viewport. With the tray
   open it reads well (the gaps above and below the model are near-equal); with nothing
   selected the lower band is empty, reserved for the tray. If that reads as too sparse, the
   real fix is a lower camera elevation on narrow viewports, which makes the room taller in
   projection — not cropping.
6. **Bluetooth is the one unread signal.** `BluetoothFeatureHash` is still empty — a BLE scan
   needs an async callback and the `BLUETOOTH_SCAN` permission, which is why it was left out of
   the first pass. It would add a fifth signal to the match.
7. **Real sensors, geometry half.** Camera pose and depth need ARCore, and the .NET binding
   story is the unknown — likely a binding project. The Pixel 8 supports the ARCore Depth API
   through motion stereo despite having no ToF, so hardware is not the blocker.

## Two confidences (2026-08-07, commit e1c49a5)

The seeded-room question from the last handoff turned out to be a modelling bug wearing a
product question's clothes. "98% confident" describes the **geometry**; the fingerprint
comparison answers whether the device is **standing in the place**. Recognition wrote the
second into the first, so a correct sensor reading rendered as a collapsed model.

They are separate now. The percentage is always model confidence; location reads as clauses
after it (`RecognitionMessages.Settled`):

| situation | line |
|---|---|
| no sensors | `98% confident · stable room model` |
| unlinked | `98% confident · not linked to a place yet` |
| match | `98% confident · you are here · 4 signals match` |
| mismatch | `98% confident · you are somewhere else · 4 signals disagree` |

`SpatialRoom.Fingerprint` is optional and the seeded garage ships `null` — its old fingerprint
was invented, so scoring against it manufactured a contradiction out of fiction. `LinkedTo` /
`Unlinked` bind and release a place without touching geometry or IDs. **Migration: none.**
`WhenWritingNull` means an unlinked room writes no key, and rooms saved earlier load as
already-linked rooms, which is what they are.

**The harness was never broken — the coordinates were.** `tools/Send-Input.ps1` takes
**client** coordinates. `tools/Capture-Window.ps1` captures the **whole window**, frame
included, so anything measured off a screenshot is in window space and runs ~31px low and 8px
right. Every "input isn't landing" symptom was clicks missing their target. Convert before
sending: `client = image − (8, 31)` at 96 DPI, or read the true offset from
`ClientToScreen(hwnd, 0,0) − GetWindowRect.topleft`. `tools/Diagnose-Input.ps1` prints
aim-versus-landing and settles it in one run. The earlier UIPI theory in this file was wrong.

## Simulated sensors (2026-08-07, commit 713bf3f)

`AWARE_SIMULATE_SENSORS` opts a sensorless platform into a stand-in reading, so the link and
recognition flows are drivable without a phone. Unset — the default — desktop and the browser
still report no sensors, which keeps the capability-tier promise honest.

```powershell
$env:AWARE_SIMULATE_SENSORS = "1"          # a consistent place; link here and it matches
$env:AWARE_SIMULATE_SENSORS = "elsewhere"  # a different place; the contradiction path
```

Verified end to end on desktop, all four states, **model confidence holding at 98% in every
one including the mismatch** — the regression the split exists to prevent, now proven at
runtime rather than only in a unit test. The link survived a full process restart with exactly
the reading it took.

Same pass fixed a layout defect no unit test could see: the lens selector's
`Margin="20,152,20,0"` was an absolute offset assuming the title block's exact height, so the
Link button rendered *underneath* the pills. The top-left column now flows in one stack.

## Decided this session (previously open)

- **Eyebrow letterspacing: built.** `Presentation/TrackedText.cs` lays a string out one
  `TextBlock` per character in a horizontal `StackPanel`. `TextBlock.CharacterSpacing` is a
  silent no-op on the Uno Skia text stack. Word gaps use a fixed-width `Border`, not a space
  character — a `TextBlock` holding only whitespace is trimmed to zero width, and U+00A0 does
  not survive it either. The full string is the automation name; the per-character blocks are
  `AccessibilityView.Raw` so a screen reader never spells it out.
- **`samples/garage-room.json`: corrected.** Rewritten in its existing readable schema with
  values measured off the geometry, all seven objects, opening dimensions and stages, plus a
  header noting it is a summary and that the app persists the full graph elsewhere.
- **Git: initialised**, `main`, Phase 1 committed.

## Cross-platform pass — what it caught

Five real defects that desktop alone would never have surfaced:

- **`System.Text.Json` reflection would have broken WebAssembly under trimming.** Six IL2026
  warnings on the WASM build. Persistence worked in Debug and would have failed in a published
  app. Now goes through source-generated `SpatialJsonContext` / `SpatialJsonExportContext`, and
  the derived `Centroid` / `IsComplete` properties are `[JsonIgnore]`d so they stop being
  written to disk.
- **The serif was a system font name.** `Georgia` resolves on Windows and macOS and nowhere
  else, so Android and the web fell back to the default sans silently. Now ships
  `Assets/Fonts/Gelasio-Regular.ttf` — a true static TTF (magic `00 01 00 00`, internal family
  `Gelasio`), metrically compatible with Georgia so nothing reflowed.
- **Evidence markers rendered as tofu on WebAssembly.** They were geometric glyphs
  (U+25CF / U+25CB); the Roboto that Material ships is latin-subset and has none. Now drawn as
  a `Border` — filled circle, hollow circle, filled square — so they carry no font dependency
  and still distinguish observed / inferred / corrected without relying on colour.
- **The Android soft keyboard covered the field it was editing.** The page declared
  `SafeArea.Insets="VisibleBounds"`, which does not include the keyboard, so the bottom-anchored
  correction sheet stayed put and the IME sat on top of its own text box. Now
  `"VisibleBounds,SoftInput"`.
- **The transient object label collided with the correction sheet** at phone width, showing
  through the tray. It is hidden while the tray is in correction mode, where the object is
  already named by the field being edited.

## Open questions

- Tray placement diverges from the brief on wide viewports: it is bottom-**left** and capped at
  430 px so it never covers the selected object. The brief's reference viewport is 390 × 844,
  where it fills the width as specified. Confirm the desktop treatment.

## Relaunch

```powershell
cd C:\Users\Platform006\Aware
dotnet build Aware/Aware.csproj -f net10.0-desktop -c Release

# Android on a device. JAVA_HOME on this machine points at a JDK 11 that is
# no longer installed, so the Android tooling needs it overridden per-shell.
$env:JAVA_HOME = "C:\Program Files\Microsoft\jdk-17.0.16.8-hotspot"
dotnet build Aware/Aware.csproj -f net10.0-android -c Debug -t:Run
adb exec-out screencap -p > shot.png     # adb input tap/swipe drives it

# WebAssembly, serves on http://localhost:5000/
dotnet run --project Aware/Aware.csproj -f net10.0-browserwasm -c Debug

# Preferred: start a session rooted here so .mcp.json registers the uno-app server,
# then launch with uno_app_start instead of the exe.
Start-Process Aware\bin\Release\net10.0-desktop\Aware.exe

# Verification helpers written this session:
. .\tools\Send-Input.ps1        # Send-AwareClick / Send-AwareDrag (mouse_event; SetCursorPos
                                # moves are invisible to Uno Skia apps)
.\tools\Capture-Window.ps1 -ProcessId <pid> -OutputPath shot.png
```

Reset to the pristine seeded room by deleting
`%LOCALAPPDATA%\Aware\com.companyname.aware\LocalState\spatial\room-garage-001.json`;
the repository re-seeds on next launch. The onboarding flag lives in `LocalSettings`
under `aware.onboarding.seen`.

**Do this reset on every device that ran a build before `e1c49a5`**, including the Pixel 8.
Rooms saved earlier carry the invented fingerprint and stay linked to a place that never
existed, so they keep reporting a mismatch and the fix looks like it did not land. The desktop
copy has been reset already; the pre-reset file is kept beside it as
`room-garage-001.json.pre-unlink.bak` and can be deleted.
