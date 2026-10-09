# Changelog

## Unreleased

Fixes the MOD not working at all on a fresh install of current Resonite (verified on `2026.9.18.82`),
makes every setting the README documents actually work, and adds 8K output.

### Added

- **`CUBEMAP_SIZE = Auto`, now the default.** The cubemap face resolution, not `OUTPUT_WIDTH`, is
  what determines sharpness: the output frame is resampled from the cubemap, so a 2048 face cannot
  fill an 8K frame with real detail. `Auto` derives the smallest face resolution that keeps up with
  the output size, taking the projection and the stereo layout into account. At the default
  6144x3072 it picks 2048, which is exactly the previous default, so nothing changes unless you
  raise the resolution. Faces are rendered only as large as needed (in steps of 256, e.g. 2816 px
  at 8192x4096) and stored in a power-of-two cubemap, as Unity requires. It caps at 4096
  (~400 MB of VRAM), enough for 8K.
- **The achieved frame rate is now measured and logged once**, about ten seconds after the Spout
  sender is created: `Spout output running at 59.9 fps (8192x4096, cubemap 4096px/face, rendered at 2816px)`.
  Whether a given resolution is usable depends entirely on the GPU and the world, so this is the
  only honest way to answer it.
- `OUTPUT_WIDTH` / `OUTPUT_HEIGHT` are clamped to 16384, the Direct3D 11 texture limit, with a
  warning. Beyond it both `CreateSender` and the render texture failed silently.

### Performance

Measured on an RTX 3060 in the local home: ~88 fps at the default 6144x3072, ~60 fps at 8192x4096
VR180 and ~48 fps at 8192x8192 360.

- VR180 and fisheye only render the front half of the four side faces (the back face was already
  skipped), cutting the rendered area from five faces to three.
- The capture camera no longer also renders the whole scene to the screen every frame; it only
  renders when the MOD asks it to.
- The vertical flip for Spout is done in the projection pass instead of an extra full-resolution
  blit through a temporary texture.
- A second cubemap (with a depth buffer) that was always allocated but only used by the unused
  `RenderToCubemap` path is no longer created. At 4096 px/face that saves about 800 MB of VRAM.

### Fixed

- **VR180 / fisheye: lit surfaces at the sides of the frame rendered black.** Only the part of the
  cube faces that the front hemisphere needs is rendered, and that was done by shrinking
  `camera.rect`, which Unity's deferred lighting does not handle. In lit worlds, walls, floors and
  ceilings outside the central 90 degrees came out black while unlit objects still appeared. The
  partial faces are now rendered into a smaller texture and copied into place. 360 output was not
  affected.
- **Spout senders were never destroyed, leaking VRAM on every resolution change.** The bundled
  `KlakSpout_send.dll` uses the KlakSpout v1 API, whose render event ignores its ID, so the
  "dispose" event did nothing. Each change of `OUTPUT_WIDTH` / `OUTPUT_HEIGHT` leaked the whole
  shared texture (128 MB at 8192x4096), and turning `SPOUT_ENABLE` off left a frozen `VRCam`
  source in OBS instead of removing it. Senders are now released with `DestroySharedObject`.
- **The renderer patcher was never loaded, so Spout never started.** BepInEx 5 only recognises a
  patcher type that has *both* a `TargetDLLs` property and a `Patch(ref AssemblyDefinition)` method.
  `Patch` was missing, so the type was silently ignored, `KlakSpout_send.dll` was never placed in
  `Renderite.Renderer_Data/Plugins/x86_64/`, and `CreateSender` failed. This only worked on the
  developer's machine because a post-build step copied the same DLL into the game folder — that
  step has been removed so the shipped path is the one that gets tested.
- Changing **OUTPUT_WIDTH / OUTPUT_HEIGHT** from the Dash menu did nothing; the Spout sender is now
  recreated at the new size.
- Changing **CUBEMAP_SIZE** from the Dash menu did nothing; the cubemap and its renderer are now
  rebuilt.
- Toggling **SPOUT_ENABLE** off and on left the old shared texture attached, producing broken
  output. The sender, shared texture and render target are now released and recreated properly.
- No more spurious `Failed to get shared texture pointer` warning on startup — the first few frames
  legitimately have no shared texture yet, and a warning is only emitted if it stays unavailable.
- **`CUBEMAP_SIZE = Ultra` produced a completely black output.** It was 3072, and Unity refuses to
  create a cubemap render texture whose size is not a power of two — silently, with the failure
  only visible in Unity's own log. `Ultra` is now 4096, any size is rounded to the nearest power of
  two with a warning, and a failed cubemap creation is now reported in the log instead of quietly
  rendering black.
- **The troubleshooting steps pointed at a log file that never contained the messages.** The
  renderer used `UnityEngine.Debug.Log`, which only reaches Unity's `Player.log`; BepInEx 5's
  `UnityLogListening` does not forward anything on the Unity 2019.4 build Resonite ships. The
  renderer now logs through BepInEx, so `Renderer/BepInEx/LogOutput.log` really does contain
  `Spout sender created: <width>x<height>` as documented.

### Changed

- Dependencies updated for current Resonite: BepisLoader 1.7.0, and the previously-implicit
  `BepInExResoniteShim`, `BepInExRenderer`, `BepisModSettings` and `BepisLocaleLoader` are now
  declared explicitly. BepisModSettings 1.6+ refuses to load without BepisLocaleLoader, which
  silently removed the whole in-game settings UI.
- The build no longer hardcodes `C:\Program Files (x86)\Steam\...`. `Directory.Build.props`
  locates Resonite via `ResonitePath` / `RESONITE_PATH`, the Steam registry key and
  `libraryfolders.vdf`, so it builds against installs on any drive. Builds still deploy to the Gale
  `360Spout` profile when it exists, and to the Resonite install otherwise; override with
  `-p:ModDeployDir=...`. BepInEx reference assemblies no longer have to exist in the deploy
  directory, and the deploy directory is created if it does not exist yet.
- The camera slot is now cached instead of walking the whole world hierarchy every frame, and the
  host log reports whether the slot was found (listing the world's root slots when it was not).
- The version now lives in one place (`Directory.Build.props`). The `PackTS` build target passes it
  to `tcli`, so it no longer disagreed with `thunderstore.toml` and produced differently-versioned
  packages depending on which command you ran.
- `CHANGELOG.md` and `LICENSE` are now included in the Thunderstore package.

### Documentation

- README: added a settings reference, a troubleshooting section, manual-installation and
  build-from-source instructions, and a note that the MOD is Windows-only (Spout is DirectX-based).
  Corrected `CAMERA_SLOT_NAME` (it is applied immediately, not only at startup), documented the
  `CUBEMAP_SIZE` options and the power-of-two constraint, and added a "Recording in 8K" section.
  Fixed the credit links, which pointed at Google search URLs rather than the repositories.
- CLAUDE.md: documented the BepInEx 5 patcher contract, the release checklist, the dependency
  matrix, and how to verify the Spout output without entering the game.