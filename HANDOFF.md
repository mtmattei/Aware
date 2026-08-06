# HANDOFF — Aware spatial room fingerprint (Phase 1 build)
Updated: 2026-08-06

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

- **Build:** pass. `net10.0-desktop`, Release, **0 warnings**. Uno.Sdk 6.6.42 / .NET 10.0.302.
  Android/iOS/WASM TFMs are in the csproj but were not built or run.
- **Runtime:** verified by launching the Release exe and driving it with synthesized input,
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
- **Git:** not a repository yet. Nothing committed.

## Next actions (in order)

1. Stand up `Aware.Tests` and automate `tests/TEST-CHECKLIST.md`. The projection, hit-testing
   and recognition cases are all pure functions of `Math3D`, `SpatialRoomRenderer.HitTest` and
   `RenderSnapshotFactory` — cheap to cover, and currently zero coverage.
2. Run the other three TFMs. Android and WebAssembly have never been compiled here, and the
   serif font resource (`Georgia`) will not resolve on either — bundle a static instance.
3. Phase 2 capture: implement `ISpatialCaptureAdapter` for one real tier. The boundary and the
   simulation tier exist; nothing native sits behind them.

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

## Open questions

- Tray placement diverges from the brief on wide viewports: it is bottom-**left** and capped at
  430 px so it never covers the selected object. The brief's reference viewport is 390 × 844,
  where it fills the width as specified. Confirm the desktop treatment.

## Relaunch

```powershell
cd C:\Users\Platform006\Aware
dotnet build Aware/Aware.csproj -f net10.0-desktop -c Release

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
