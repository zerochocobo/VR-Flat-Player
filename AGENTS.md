# Working rules for this repository

Short, and every rule here exists because breaking it cost real time. Read the
reason, not just the rule — a rule whose reason no longer holds should be
changed, and one whose reason you have not understood will be worked around.

## 1. Nothing about the development machine may reach a release

**A release must contain only what was built, never what running it produced.**

The player writes state as it runs: window position and size, per-file VR mode,
the mpv log. Those describe the machine that ran it last. During development
that machine is always the developer's, and it is never the user's.

This has already shipped once. A build went out carrying a `1391x1530` window
saved from a 288 dpi development display. On the user's 1080p screen it opened
clamped to full height, and the log said `remembered` for a placement they had
never chosen — so the bug also lied about its own cause.

Concretely:

- Runtime state lives in its **own files** (`window-state.json`,
  `mode-memory.json`, `mpv-last-run.log`), never inside `bridge.config.json`.
  A settings file has to ship, so it cannot simply be deleted before packing;
  a state file can. That separation is the only thing that makes the check
  below possible.
- `tools/publish.ps1` deletes those files from the staging folder, and does it
  **again immediately before zipping**, because anything that starts the player
  in between recreates them.
- `bridge.config.json` is **generated from the compiled defaults** at publish
  time, never copied from a hand-maintained file. The hand-maintained one went
  stale and kept shipping `deadzoneDegrees: 0.6` with no `stickyDegrees` long
  after those defaults changed, which silently disabled a feature in every
  release while the source said it was enabled. A stale config is worse than a
  missing one: it is read in full, so every key it still carries overrides the
  default that replaced it.
- **Do not test by running `dist\VR Flat Player\` directly.** That folder is the
  release. Copy it somewhere first and run the copy. This is how the 1391x1530
  window got in: publish produced a clean folder, then the verification runs
  dirtied it.

  Broken again since, with a worse blast radius. Smoke-testing the staged exe
  with `--source=camera` and switching tracking on left
  `"faceTracking": true, "kind": "camera"` in the staged
  `bridge.config.json` — so that folder would have opened the user's webcam
  the moment it launched. The scrub cannot save you here: settings have to
  ship, so `bridge.config.json` is the one file that is regenerated rather
  than deleted, and running the player rewrites it after that.

  The zip was clean both times, because it is packed before the testing. That
  makes the dirty folder *harder* to notice, not easier — whichever of the two
  the user copies from decides what they get.

## 2. Verify by running it, not by reasoning about it

Claims about behaviour need a measurement. This project has a long list of
things that were confidently wrong on paper:

- "60 fps content drops frames" — measured `time-pos` against wall clock: 0.21x.
  It was genuine slow motion.
- "Capping inference threads frees cores for decode" — measured 362 ms at 2
  threads against 273 ms unrestricted. Throttling made it *hold* the CPU longer.
- "The mode is set correctly, so the picture must be right" — screenshotting the
  player's own output showed every circle in a test grid as a tall ellipse.

Useful instruments, all already in the repo or trivial to rebuild: mpv's
`screenshot-to-file <path> window` over the IPC pipe renders exactly what is on
screen; a generated test frame with a known grid turns "looks wrong" into a
ratio; `tests/VideoFormatTests` runs in seconds and prints measurements as well
as pass/fail.

## 3. The development machine is not representative

This repo has been developed at **288 dpi on a 3840x2160 display**, and reported
against **120 dpi on 1920x1080**. Scaling bugs are invisible here and obvious
there.

Anything involving window size, DPI, minimum sizes or screen fractions must be
reasoned about in both, and the numbers written down. Two real examples:

- A fixed `360x240` logical minimum became 1080 px at 288 dpi and silently
  overrode a 948 px default, so the "square" window opened `1044x948`.
- Half the screen height at 120 dpi with 125% scaling is about 440 px square —
  correct by the rule, useless to watch video in.

## 4. Change one thing, and say what was not verified

Bundling several behaviour changes into one round makes it impossible to tell
which one caused the next report. When something genuinely cannot be verified
here — no camera, no second monitor, no 4K file — **say so explicitly** rather
than presenting a reasoned guess as a tested result.

## 5. Comments carry the reason, not the mechanism

The code says what it does. Comments exist for what cannot be recovered by
reading it: the measurement behind a constant, the alternative that was tried
and failed, the hidden coupling that makes an obvious simplification wrong.
Most non-obvious constants in this codebase have a number and a reason attached;
keep it that way when changing them.

## Build, test, publish

```
dotnet run --project tests/VideoFormatTests/VideoFormatTests.csproj -c Release
powershell -ExecutionPolicy Bypass -File tools/publish.ps1
```

The version lives in exactly one place: `<InformationalVersion>` in
`src/HeadTrackBridge/HeadTrackBridge.csproj`. The folder name, the zip name, the
About box and the console banner all read it from there.
