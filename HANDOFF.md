# HANDOFF — Aware recognition-only rooms (SPEC complete, walk test outstanding)
Updated: 2026-08-07 22:15

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
- **Tests:** `tests/Aware.Tests`, **115 passing** (108 + 7 new `RoomListMessagesTests`).
- **Desktop runtime (Release exe + `tools/`):** room list opens over the live viewport,
  both rooms listed with correct reason lines (`No model yet`, `7 objects · not linked
  to a place`), the open room carries the sage marker, and switching to an unmodelled
  room works — Measure and Memories go dead, the floor plate draws, the note appears,
  no relaunch needed.
- **Android (Pixel 8, Debug, 22:07):** launches and stays up. Locator opens the
  recognition-only room automatically against real sensors; floor plate draws; Measure
  and Memories dead; `Rooms` and `Add this place` both offered (Android has sensors,
  desktop hides Add). Stored rooms: `kitchen` (recognition-only) and the seeded garage.
- **Git:** `main`, clean. `1f9babc` (renderer lifecycle), `375a0f9` (step 6),
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

1. **The walk test. Needs a human. Half of it is already done.** Standing in the
   kitchen, `kitchen` opens by itself — confirmed 22:09. What remains is the negative
   case: carry the phone to another room, relaunch, confirm `kitchen` does *not* open
   (expect the garage fallback plus `you are somewhere else`), then walk back and
   confirm it does. Only the negative half is unproven.
   `am start -n com.companyname.aware/crc64069453ae4e9f0b37.MainActivity` — not `monkey`.
   **The bar may be too generous for this** — see item 4.
2. **Exercise the room list on the phone**, where "Add this place" is actually visible
   (desktop hides it: no sensors). Create a second real room from the list and confirm
   both appear with sensible reason lines.
3. **Decide: does the sample garage stay linkable?** SPEC unresolved question #1 said
   decide before step 7; step 7 shipped without it. It is no longer hypothetical — the
   garage on the Pixel **is** linked to a real place (fingerprint saved 06:54, after the
   unlink fix, so via the Link button). That is exactly the incoherence SPEC opens with.
   The spec recommends dropping Link from the sample.
4. **Reconsider the 0.6 auto-open bar.** Scoring the Pixel's two stored rooms: from the
   kitchen, the garage scores **~0.83** — clears the bar from a different room in the
   same building. Same floor means near-identical pressure, and magnetic *direction*
   agrees more than magnitude disagrees. The Kitchen should still win on max, but SPEC
   unresolved question #3 wanted real multi-room data before tuning; this is it.
5. Bluetooth is still the unread fifth signal (`BluetoothFeatureHash` empty). Both
   stored rooms also have an empty `wifiFeatureHash`, so matching currently runs on
   **three** signals with weights renormalized from 0.55 — worth checking whether
   `NEARBY_WIFI_DEVICES` was declined on the device.
6. Portrait framing decision (unchanged from last handoff).
7. Real geometry needs ARCore; the .NET binding story is the unknown.

## Known-bad, do not re-derive

- **`uno_app_start` cannot set environment variables**, so `AWARE_SIMULATE_SENSORS`
  cannot be used through the App MCP — the app inherits the DevServer's environment.
  Use the Release exe + `tools/Send-Input.ps1` for simulated-sensor runs.
- **Debug desktop build via `uno_app_start` exits immediately** on this machine
  (Release exe runs fine). Not diagnosed; unrelated to app code.
- **Deploying `-c Release` to a device wipes its app data** (different signing key →
  uninstall/reinstall). This destroyed a real room once already. Stay on Debug.
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
