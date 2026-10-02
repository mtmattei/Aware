# HANDOFF — Aware recognition-only rooms (SPEC complete, walk test outstanding)
Updated: 2026-10-02

## Capture: pipeline verified, ARCore adapter rewritten and not yet compiled

**The app can now observe real geometry.** `ISpatialCaptureAdapter.CaptureAsync`
had been declared since the first build and never once called; there is now an
ARCore adapter behind it, an assembler folding observations into the room, and a
`Scan this room` button. 4 TFMs clean, 135 tests.

**The pipeline is runtime-verified on desktop** via `AWARE_SIMULATE_CAPTURE=1`
(the counterpart to `AWARE_SIMULATE_SENSORS`; unset, the app still reports the
Simulation tier and hides the scan). Driven against a recognition-only room:
the label switches between `Scan this room` and `Scan again`, the recognition line
counts live (`Scanning this room · 18 surfaces · 3 objects`), the model assembles
progressively, Measure and Memories come alive mid-scan, and the room persists
under its **original id and name** — `Place A`, `modelVersion` 0 → 4, 362 bytes →
11521 — reloading modelled after a restart. That chain is SPEC's untested
recognition-only-becomes-modelled transition, now proven.

```powershell
$env:AWARE_SIMULATE_CAPTURE = "1"
Start-Process Aware\bin\Release\net10.0-desktop\Aware.exe
```

**What remains unverified is the ARCore adapter itself** — zero device time. The
assembler, view model, persistence and rendering beneath it are all proven, so a
failure on the phone is almost certainly in `ArCoreCaptureAdapter`, not below it.

```powershell
$env:JAVA_HOME = "C:\Program Files\Microsoft\jdk-17.0.16.8-hotspot"
dotnet build Aware/Aware.csproj -f net10.0-android -c Debug -t:Run   # Debug ONLY
```

What to check on the phone, in order — each step can fail independently, and
everything downstream of step 3 is already proven on desktop:

1. **Does `Scan this room` appear at all?** It is bound to `CanCapture`, which is
   false unless `ArCoreApk.CheckAvailability` reports supported. Absent means
   ARCore is not installed or the Play Services for AR app is missing.
2. **Camera prompt on first scan.** Asked at scan time, not launch.
3. **Does the recognition line count surfaces?** It reads
   `Scanning this room · N surfaces · M objects` while streaming. Zero surfaces
   means planes are not being found — move slowly, and note ARCore needs texture
   and light, so a dark kitchen at midnight is close to a worst case.
4. **Does the model draw?** Observations are applied as they arrive, so shapes
   should appear progressively rather than at the end.
5. **Does it persist?** Force-close, reopen, confirm the room still has its shape
   and that Measure and Memories are now live rather than dead.
6. `adb shell run-as com.companyname.aware cat files/spatial/<room>.json` — the
   `shell` array should have real extents and the room should keep its original id.

**The GL texture failure is addressed, uncompiled.** The previous adapter called
`GLES20.GlGenTextures` after an `await`, which resumes on the UI thread, where Uno's
Skia renderer holds no GL context — texture id 0, every frame dropped, a 45 s scan
that observes nothing. Each scan now runs on its own long-running thread that
creates a 1×1 pbuffer EGL context, creates the external texture inside it, and
keeps `Resume`, `Update` and `Pause` on that thread, handing observations back
over a `Channel`. **This was written in a cloud session with no Android SDK and
has never been compiled.** First thing on the next device session:

```powershell
dotnet build Aware/Aware.csproj -f net10.0-android -c Debug
```

Then scan. The failure modes now look different from before:

- `The ARCore scan failed.` with *No offscreen GL context could be created* →
  EGL refused a pbuffer config on this GPU. Try dropping `EglAlphaSize` from the
  config attributes, or use the renderer's config via `EglGetConfigs`.
- *The camera texture could not be created (GL error 0x502)* → the context was
  created but not made current; `0x502` is `GL_INVALID_OPERATION`.
- Scan runs 45 s and `Scanning this room · 0 surfaces` the whole time, no error
  → the context is fine and ARCore is simply not finding planes (light, texture,
  moving too fast). That is the only remaining "observes nothing" case.
- The scan stops at once with `The scan stopped early` → `Session.Resume()`
  threw; the exception is in logcat under `The ARCore scan failed.`

One assumption remains unmeasured: that `Session.Update()` is happy with the
session having been *created* on the UI thread (in `GetCapabilitiesAsync`) and
*driven* on the scan thread. Not checked against the ARCore docs (blocked from
the cloud session). If `Resume` or `Update` complains about the calling thread,
move `EnsureSession` onto the scan thread too.

## Privacy controls: built, pause verified, the rest not click-verified

`07-DATA-PRIVACY` specifies export and erasure and `PrivacyService` always
implemented both — nothing could reach either. There is now a **Privacy** sheet
(top right): pause awareness, export this room, forget everything. **Room deletion**
sits in the room list, on every room except the one open.

**Only `AwarenessPaused` is a control.** The other three `PrivacySettings` are
honoured by no code — no cloud exists, no image is ever kept, nothing listens — so
they are stated as facts in the sheet rather than offered as switches that would
move and change nothing. Do not "finish" them by wiring toggles; wire the
behaviour first or leave them stated.

**Verified:** paused reads `100% confident · awareness paused`, no place clause,
`Scan` disabled, `Add this place` absent. Worth knowing *how* that was checked,
because the obvious gate was wrong: writing `privacy.json` with
`"awarenessPaused":true` and launching with `AWARE_SIMULATE_SENSORS=1` caught that
gating the fingerprint read stops the locator but **not** recognition —
`SensorRoomRecognitionService` takes its own reading when handed null, so the app
still said "you are here · 4 signals match" while claiming to be paused.

**Not verified:** export, delete, forget-everything. Built, no unit test possible
(`PrivacyService` needs `Windows.Storage`), and not click-verified — see below.
Read-through on 2026-10-02 found three things worth knowing before clicking:

- **Fixed: forget-everything left exports behind.** Exports live beside
  `privacy.json`, outside `spatial/`, so `DeleteAllAsync` never saw them — the
  originals went and the full copies stayed. `ForgetAllSpatialDataAsync` now
  deletes every `*-export.json` too.
- **Open: forget-everything re-seeds the garage on the next repository call.**
  `JsonRoomRepository.LoadAsync` seeds the sample whenever the store is empty, so
  after "Everything Aware knew has been erased" and the note "Nothing is stored on
  this device", opening the room list will show `Your Garage` again. The line is not
  false — the sample is fiction, not something Aware knew — but the note is. Product
  call: either return to the pristine first-launch state explicitly (re-seed and open
  the garage, drop the "nothing is stored" note), or stop seeding after a forget.
  Recommend the first; it is what a fresh install looks like.
- **Open: export never leaves the app sandbox.** It writes to `LocalFolder`, which on
  Android only `run-as` can read. "Export" that the user cannot reach is a share-sheet
  job (`Intent.ActionSend` with a `FileProvider` URI) — Android UI work, not started.

## Not compiled in the cloud session

Three files were changed on 2026-10-02 without a toolchain that can compile them —
the cloud container has no Android workload and no Uno desktop build. Each was
syntax-checked with Roslyn and nothing more:

- `Platforms/Android/ArCoreCaptureAdapter.cs` — the GL-context rewrite above.
- `Presentation/SpatialRoomViewModel.cs` — three `CanLinkPlace` sites and the Link
  command guard now go through `PlaceLinking.CanLink`; the private
  `SampleGarageFactoryId` copy is gone.
- `Infrastructure/PrivacyService.cs` — the export cleanup.

Build all four TFMs before anything else; the pure layers under them are covered
(131 tests pass, the 10 `HitTestingTests` failures are Linux lacking `libSkiaSharp`).

## The input harness is unreliable again

`tools/Send-Input.ps1` silently misses whenever the app is not foreground, and
`SetForegroundWindow` is refused to a background process. Two new symptoms seen
this session, both of which look like "the app ignored my click":

- **A minimized window still screenshots normally.** `PrintWindow` captures it, so
  the capture looks right while every click lands at screen (−32000, −32000).
  Check `IsIconic` before believing a capture.
- Landing drifted by a few hundred px with the window restored and stationary.
  `tools/Diagnose-Input.ps1` prints aim-vs-landing; error (0,0) means input is fine.

`uno_app_start` **now works** for the Debug desktop build (the renderer lifecycle
fix in `1f9babc` was what killed it before), so peer actions are the better route —
but the DevServer opened **Hot Design** over the app, which per the project
gotchas kills screenshots and the Skia canvas for the rest of the process. Exit it
before driving, or launch Release and fix the foreground problem.

## Do this first: the walk test

Everything is deployed on the Pixel and **the positive half is verified**. Standing
in the kitchen, two consecutive force-stop relaunches scored **0.996** and **0.997**
and opened the kitchen. What is still unproven is the negative half — whether
another room now fails to match. That is the one thing that needs legs.

1. Kitchen → force-close Aware (swipe away, not just background) → reopen.
   Expect `kitchen`. The fingerprint is read only at launch, so resuming proves nothing.
2. Walk to another room, wait ~10 s, force-close → reopen. Expect **Your Garage**.
3. Walk back, force-close → reopen. Expect `kitchen`.

**Every launch now logs why**, so tune against numbers rather than impressions:

```powershell
adb logcat -c        # then force-close and reopen the app
adb logcat -d | Select-String 'Locate:'
```

It prints each room's score, `recognized`/`rejected`, and which signals agreed.

**Re-capture any room stored before commit `f6acd7c`.** A fingerprint taken with the
old power-saving scan holds 2 devices; comparing it against the 4 a low-latency scan
now finds scores 0.5 on overlap alone, which looks exactly like the bug. The current
`kitchen` was captured after the change and is fine.

## What the fingerprint actually turned out to be

Every one of these was measured on a Pixel 8, not reasoned about, and each replaced
a plausible-sounding assumption that was wrong.

- **Wi-Fi is dead on Android 13+ and contributed nothing while holding 0.45 of the
  weight.** `getScanResults()` is refused without a location permission the app
  deliberately does not hold. Its hash was also a single blob over the strongest
  BSSIDs — the same set in every room of one home, so it separated buildings, not
  rooms, and restoring it would have raised scores rather than discriminated.
- **Magnetic direction is device-frame** — it describes how the phone is held. Two
  readings in one kitchen were 29 µT apart as vectors, 0.7 µT as magnitudes. Now
  magnitude only.
- **Magnetic magnitude is barely usable either.** That kitchen has read 46.2, 45.6,
  46.5, 34.5 and 21.4 µT — a within-room spread as wide as the gap to the garage
  (21.3). At weight 0.30 it *disagreed with the room the phone was standing in* and
  dragged the score to 0.666, a margin of 0.066 over the bar. Now a nudge at 0.10.
- **Pressure drifts with weather, not height.** Same room, 1015.0 hPa morning and
  1011.6 that night — 3.4 hPa against a 1.5 hPa "one storey" tolerance. Widened to 6,
  weight 0.10. Detecting a floor change is deliberately given up.
- **Bluetooth carries the feature, at 0.70.** `BLUETOOTH_SCAN` with
  `neverForLocation` genuinely needs no location permission.
  - **Names, not addresses** — BLE addresses are resolvable-private and rotate every
    few minutes (07-DATA-PRIVACY specified names all along).
  - **Per-device hashes compared by overlap, not one hash over the set** — a blob
    hash gave the same kitchen two different fingerprints four minutes apart, because
    advertisers sleep and wake between scans.
  - **`ScanMode.LowLatency`** — Android's default duty-cycles the radio and found
    only 2 devices here; low latency finds 4, which is the difference between a set
    that can afford to lose one and one that cannot.

**The remaining fragility is physical, not fixable in software:** four named BLE
advertisers is thin. In a room with none, the fingerprint falls back to signals that
were measured not to discriminate, and recognition will not work there.

## Read this first

**The Pixel now launches and the feature works on real sensors.** Standing in the
kitchen, the app opens the recognition-only room by itself reading
`No model yet · you are here · 3 signals match`, draws the floor plate, and keeps
Measure and Memories dead. That is SPEC steps 4, 5, 6 and 8 confirmed on hardware.

**A crash blocked all of this and is fixed (`1f9babc`).** `SpatialRoomView.OnUnloaded`
disposed the renderer — freeing every native Skia paint, font, path and shader — while
the view was still alive and about to be reloaded. On Android the splash screen swaps
content under the page, so the first frame drew through freed handles: **SIGSEGV at
address 0 in libSkiaSharp**, dead before anything rendered. The same bug is what
crashed desktop with a `0xc0000005` AV when the room list was a route. The routed list
did not cause it, it only exposed it.

**Data loss, caused by this session:** deploying `-c Release` to the Pixel forced an
uninstall/reinstall (different signing key) and **wiped app data at 22:05**, destroying
the `Kitchen` room from the earlier session. A new `kitchen` was created at 22:06 and
works. **Do not deploy Release to a device that holds rooms you care about.**

## Where we are

**Every SPEC step is now built.** Step 6 (the room list) landed this session, which
was the last one outstanding, plus a defect fix that step 6 exposed. The remaining
work on this feature is not code: it is the one test that needs a human to walk
between two physical rooms.

`SPEC.md` steps 1–8 are implemented; step 9 (full runtime pass) is done on desktop
and **half done on the Pixel** — the app is deployed and the sensor half is unverified.

## Last verified state

- **Build:** all four TFMs, **0 warnings / 0 errors**. Uno.Sdk 6.6.42 / .NET 10.0.302.
- **Tests:** `tests/Aware.Tests`, **141 total; 131 pass on Linux, all expected on Windows**. `FieldMeasuredMatchTests` is built
  from readings actually taken on the device and pins both directions: another room
  must not match the kitchen, and the kitchen must still match itself across a 29 µT
  reorientation and from a step across the room.
- **Desktop runtime (Release exe + `tools/`):** room list opens over the live viewport,
  both rooms listed with correct reason lines (`No model yet`, `7 objects · not linked
  to a place`), the open room carries the sage marker, and switching to an unmodelled
  room works — Measure and Memories go dead, the floor plate draws, the note appears,
  no relaunch needed.
- **Android (Pixel 8, Debug, 22:07):** launches and stays up. Locator opens the
  recognition-only room automatically against real sensors; floor plate draws; Measure
  and Memories dead; `Rooms` and `Add this place` both offered (Android has sensors,
  desktop hides Add). Stored rooms: `kitchen` (recognition-only) and the seeded garage.
- **Git:** `main`, clean. `f6acd7c` (scan mode + weights), `0dd5284` (set overlap),
  `d8fdc6c` (matcher rebuild), `1f9babc` (renderer lifecycle), `375a0f9` (step 6),
  `29e883d` (unmodelled state on room switch).

## Fixed this session

**`ApplyModelledState` ran only on load.** `OpenAsync` — the path "Add this place"
uses to open the room it just created — never recomputed it, so a new recognition-only
room opened with Measure and Memories live over a bare floor and no explanatory note
until relaunch. Step 8 was invisible on the exact path that creates the rooms step 8
exists for, which is how the Kitchen on the Pixel was made. Commit `29e883d`;
confirmed at runtime through the room-switch path.

## Deviation from SPEC (step 6)

The spec specifies the room list as a **route with its own page**. Built that way
first; it does not survive contact with the renderer:

- Returning from the list remounts the page owning `SKCanvasElement`, and pushing the
  next room's snapshot into the remounted canvas kills the process with a native
  **access violation (0xc0000005 in coreclr)**.
- Back navigation silently did nothing: `Shell` is an `ExtendedSplashScreen`
  `UserControl` with no `Frame` and no `Region.Attached`, so a sibling route has no
  back stack to return through.

It is now an **overlay on the room page** — the same deviation already taken for the
naming sheet and the first-launch card, and it keeps the canvas mounted. If the room
list ever needs to be a real destination, the shell needs a proper navigator host
first, and the canvas lifecycle across page swaps has to be solved before that.

## Next actions (in order)

1. **The walk test** — see the top of this file. Only the negative half is unproven.
2. **Exercise the room list on the phone**, where "Add this place" is actually visible
   (desktop hides it: no sensors). Create a second real room from the list and confirm
   both appear with sensible reason lines.
3. **Verify capture on hardware** — see the top of this file. Built, never run.
4. **Objects are not classified, by design.** ARCore reports surfaces, not
   "workbench", so a scan produces shell geometry and no object candidates. The
   correction flow already lets a user name things; whether that is the shipped
   answer or a classifier follows is a product decision, not a bug.
5. **Decided: the sample garage is not linkable.** SPEC unresolved question #1,
   answered as SPEC recommended. `PlaceLinking.CanLink` is the one rule, used by the
   recognition service and the view model (load, room switch, and inside the Link
   command as a guard). With live sensors the sample now settles on
   `98% confident · stable room model` instead of inviting a link. Pinned by
   `PlaceLinkingTests`. The view-model side is not compiled here — see "Not compiled
   in the cloud session" below.
6. **Revisit the 0.6 bar once there are three or more real rooms.** Untunable on two.
   The earlier ~0.83 cross-room figure is obsolete: it came from the old matcher.
7. Portrait framing decision (unchanged from earlier handoffs).

## Known-bad, do not re-derive

- **`uno_app_start` cannot set environment variables**, so `AWARE_SIMULATE_SENSORS`
  cannot be used through the App MCP — the app inherits the DevServer's environment.
  Use the Release exe + `tools/Send-Input.ps1` for simulated-sensor runs.
- **Debug desktop build via `uno_app_start` exits immediately** on this machine
  (Release exe runs fine). Not diagnosed; unrelated to app code.
- **Deploying `-c Release` to a device wipes its app data** (different signing key →
  uninstall/reinstall). This destroyed a real room once already. Stay on Debug.
- **Wi-Fi scanning is dead on Android 13+ and cannot be fixed without location.**
  `NEARBY_WIFI_DEVICES` is granted and `getScanResults()` is still refused:
  `SecurityException: UID ... has no location permission`. Adding
  `usesPermissionFlags="neverForLocation"` was tried and measured and does **not**
  lift that gate — do not retry it. The only ways back are holding
  `ACCESS_FINE_LOCATION` at runtime (rejected: it breaks the location-free promise)
  or leaving Wi-Fi out. It is left declared and inert, documented in the manifest.
- Even if it worked, the Wi-Fi hash is a **set hash of the strongest BSSIDs**, which
  is the same set in every room of a small home. It separates buildings, not rooms.
  Room-level Wi-Fi needs per-AP RSSI vectors, which is a different design.
- **`Send-AwareClick` silently misses when the app is not foreground.**
  `tools/Diagnose-Input.ps1` prints aim-vs-landing and settles it in one run: error
  (0,0) means input is fine. Client = image − (8, 31) at 96 DPI, confirmed again.
- The uno-navigation skill's `ResultDataViewMap` example is **wrong for 7.2.3**: the
  real type takes 3 type arguments (no input type). Input data goes through
  `Data: new DataMap<T>()`, which does work.

## Relaunch

```powershell
cd C:\Users\Platform006\Aware
dotnet build Aware/Aware.csproj -f net10.0-desktop -c Release
Start-Process Aware\bin\Release\net10.0-desktop\Aware.exe

$env:JAVA_HOME = "C:\Program Files\Microsoft\jdk-17.0.16.8-hotspot"
dotnet build Aware/Aware.csproj -f net10.0-android -c Debug -t:Run

. .\tools\Send-Input.ps1        # Send-AwareClick / Send-AwareDrag (client coords)
.\tools\Capture-Window.ps1 -ProcessId <pid> -OutputPath shot.png
.\tools\Diagnose-Input.ps1 -ProcessId <pid> -X <x> -Y <y>
```

Reset a device to the pristine seeded room by deleting its
`spatial/room-garage-001.json`; the repository re-seeds on next launch.
