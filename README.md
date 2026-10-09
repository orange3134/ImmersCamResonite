# ImmersCamResonite

This is a MOD for recording **VR180** video in Resonite. (It also includes 360-degree video support as a bonus.)

> **Windows only.** The output goes out over Spout, which is built on DirectX shared textures and
> has no Linux equivalent.

## Installation

1.  Install this MOD from **Thunderstore** using a Mod Manager (Gale, r2modman, …).
    All required dependencies — BepisLoader, RenderiteHook, **BepInExRenderer**, BepisModSettings
    and BepisLocaleLoader — are pulled in automatically.
2.  Launch Resonite via your Mod Manager.

### Manual installation

Resonite runs as **two processes**, and this MOD has a part in each of them, so the package has two
roots:

| Folder in the package | Copy to |
|---|---|
| `plugins/` | `<Resonite>/BepInEx/plugins/` |
| `Renderer/` | `<Resonite>/Renderer/` |

The `Renderer/BepInEx/patchers/Reso360Spout2.Patcher/` part is **not optional**: it is what places
`KlakSpout_send.dll` into `Renderer/Renderite.Renderer_Data/Plugins/x86_64/` before Unity starts,
which is the only way Spout gets a valid D3D11 device. Without it the MOD loads but never produces
a Spout sender.

Remember to enable BepisLoader itself (`enable=true` in `hookfxr.ini`, or the `--hookfxr-enable`
launch argument) if you are not using a Mod Manager.

## How to Use

This MOD outputs video via **Spout**. You can achieve VR180 recording by receiving the Spout signal in **OBS** and recording it there.
*This guide assumes OBS is already installed on your system.*

### OBS Configuration

1.  Install the **Spout2 plugin** for OBS. ([Link to GitHub](https://github.com/Off-World-Live/obs-spout2-plugin))
2.  Add a **Spout2 Capture** source to your scene.
3.  Set your **Canvas Resolution** to match the MOD's `OUTPUT_WIDTH` x `OUTPUT_HEIGHT` — **6144x3072** by default. At least 6K/3K is recommended for high-quality results; see [Recording in 8K](#recording-in-8k) if you want to go further.
4.  Configure your **Output Settings** as follows:
      * **Video Encoder:** NVIDIA NVENC HEVC
      * **Rate Control:** CBR
      * **Bitrate:** 200,000 Kbps
      * **Preset:** P3 Fast
      * **Multi-pass Mode:** 1 Pass

### Resonite Configuration

1.  Copy and paste the following URL into Resonite and save the spawned folder to your inventory:
    `resrec:///U-orange/R-283474B975465A2BF6F0B3A29F9E46515262C40B601429CDE229D603954869C1`
2.  Open the folder in your inventory and spawn the **SkyCamera by orange V1.10 VR180 System**.
3.  Click the **blue circle** to enter the camera anchor. Use the left/right sticks and triggers to control the camera.
4.  You can adjust camera settings via the **Dash Menu** using **BepisModSettings**. This allows you to toggle the camera on/off, switch between 360/VR180, change resolution, and adjust the **IPD**. All of these apply immediately, without restarting the game. Changing the resolution recreates the Spout sender; the OBS Spout2 source picks up the new size by itself. Turning the camera off removes the `VRCam` sender, and OBS reconnects when you turn it back on.

### Shooting Tips

  * **Keep the camera steady:** Avoid moving the camera during recording. If you must move it, do so very slowly. Sudden camera movements in VR180 can cause severe motion sickness.
  * **Maintain a level horizon:** Unless you have a specific artistic reason, keep the camera’s roll and pitch horizontal. It is highly recommended to enable the **PitchLock** feature on the SkyCamera.
  * **Adjusting IPD:** You can control the sense of depth using the IPD setting. A wider IPD increases the 3D effect but makes the world look smaller. A narrower IPD reduces the depth effect and makes the world feel larger. Be careful: if the subject is too close to the camera at a high IPD, the parallax may become too intense, making the video uncomfortable to watch.

-----

## Settings

Everything below is editable from the **Dash Menu → Settings** (via BepisModSettings) and applies
immediately. The values are also persisted in `<Resonite>/BepInEx/config/net.orange.Reso360Spout2.cfg`.

| Setting | Default | What it does |
|---|---|---|
| `CAMERA_SLOT_NAME` | `#Camera` | Name of the world slot the camera follows. Searched again as soon as you change it. |
| `SPOUT_ENABLE` | `true` | Turns the Spout output (and the extra rendering cost) on and off. |
| `PROJECTION_TYPE` | `Equirectangular_180` | `Equirectangular_360` / `Equirectangular_180` / `FishEye_Circumference` / `FishEye_Diagonal`. |
| `RENDER_IN_STEREO` | `true` | Stereo output. 180 is laid out side-by-side, 360 is laid out over-under. |
| `STEREO_SEPARATION` | `0.065` | IPD in metres. See *Shooting Tips* above. |
| `OUTPUT_WIDTH` / `OUTPUT_HEIGHT` | `6144` / `3072` | Size of the Spout texture. Both eyes share it, so in VR180 each eye gets half the width. Capped at 16384, the Direct3D 11 texture limit. |
| `CUBEMAP_SIZE` | `Auto` | Resolution of each cubemap face — this, not `OUTPUT_WIDTH`, is what actually drives sharpness. `Auto` picks the smallest face resolution that does not lose detail at your output size. Fixed values are `Low` 512 / `Mid` 1024 / `High` 2048 / `Ultra` 4096; Unity requires a power of two, so anything else is rounded. |
| `NEAR_CLIP` / `FAR_CLIP` | `0.01` / `3000` | Camera clipping planes. |
| `HIDE_LOCAL` | `true` | Hides local-user-only content (your own UI, the Dash menu) from the recording. |

Note that `OUTPUT_WIDTH` / `OUTPUT_HEIGHT` are the dimensions of the **whole** frame. For VR180
side-by-side, `6144x3072` gives each eye `3072x3072`. For 360 over-under you usually want a square
frame such as `4096x4096` so each eye gets a 2:1 equirectangular image.

### Recording in 8K

Set `OUTPUT_WIDTH` / `OUTPUT_HEIGHT` and leave `CUBEMAP_SIZE` on `Auto`:

| | Frame | Per eye | Faces rendered by `Auto` |
|---|---|---|---|
| VR180 (side-by-side) | `8192` x `4096` | 4096x4096 | 2816 |
| 360 (over-under) | `8192` x `8192` | 8192x4096 | 2816 |

Then set the same value as your **OBS Canvas Resolution**.

Raising `OUTPUT_WIDTH` on its own does **not** give you more detail — the frame is resampled from
the cubemap, so a 2048 face cannot fill an 8K frame with real detail. That is the mistake `Auto`
exists to prevent. The rule it applies is that one cube face spans 90° and is coarsest at its
centre, where it resolves `face / 114.6` pixels per degree; `Auto` renders each face at the
smallest size (in steps of 256) that keeps that at or above what the output needs, and stores it
in the next power-of-two cubemap (Unity requires one). It caps itself at 4096 (about 400 MB of VRAM
for the six faces), which is enough for 8K but not for 16K — beyond that, set `CUBEMAP_SIZE` yourself.

VR180 and fisheye only look forward, so the back face is skipped and the four side faces are only
rendered on their front half.

**Cost.** Measured on an RTX 3060 in Resonite's local home, which is a light scene — a busy world
will be considerably slower:

| Setting | Frame rate |
|---|---|
| 6144x3072 VR180, faces 2048 | ~88 fps |
| 8192x4096 VR180, faces 2816 | ~60 fps |
| 8192x8192 360, faces 2816 | ~48 fps |

The MOD measures this for you: about ten seconds after the Spout sender is created it logs a line
like `Spout output running at 59.9 fps (8192x4096, cubemap 4096px/face, rendered at 2816px)` to
`<Resonite>/Renderer/BepInEx/LogOutput.log`. Check it after changing resolution — this is the only
reliable way to know whether a setting is usable on your machine.

Note that 8K HEVC is beyond what many players and NVENC presets handle comfortably; check that your
target platform accepts the resolution before committing to a long recording.

## Troubleshooting

* **No `VRCam` source shows up in OBS.**
  Check `<Resonite>/Renderer/BepInEx/LogOutput.log`. You should see
  `Reso360Spout2.Patcher` copying `KlakSpout_send.dll`, followed by
  `Spout sender created: <width>x<height>`. If the patcher line is missing, `BepInExRenderer` or
  `RenderiteHook` is not installed and the renderer half of the MOD never runs.
* **Settings in the Dash menu do nothing.**
  Make sure **BepisModSettings** *and* **BepisLocaleLoader** are both installed — since
  BepisModSettings 1.6 it refuses to load without the locale loader.
* **The `VRCam` source is there but completely black.**
  The renderer log reports it if a cubemap could not be created (`Failed to create a … cubemap
  render texture`). Also check `NEAR_CLIP` — anything closer than it is clipped away, so a large
  near plane renders nothing.
* **The camera does not move.**
  The host log (`<Resonite>/BepInEx/LogOutput.log`) reports whether the camera slot was found and
  lists the root slots of the focused world. The slot name is configurable via `CAMERA_SLOT_NAME`.
* **Nothing loads at all.**
  BepisLoader must be enabled (`enable=true` in `hookfxr.ini`). Also note that BepInEx caches its
  plugin scan in `<Resonite>/BepInEx/cache/` — if you add mods by hand, delete that cache.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Resonite must be installed; the
build locates it automatically from the Steam registry and `libraryfolders.vdf`, including
libraries on other drives.

Building the renderer half also needs the renderer's BepInEx 5 (the `BepInExRenderer`
dependency) to be installed, because it is referenced from the game folder. Close Resonite first —
the build deploys the MOD, and a running game holds the DLLs open.

```powershell
dotnet build -c Release
```

This also deploys the MOD: into the Gale profile `360Spout` if it exists, otherwise straight into
the Resonite install. To override either path:

```powershell
# build against a Resonite install the auto-detection cannot find
dotnet build -c Release -p:ResonitePath="D:\Games\Resonite"
```

```powershell
# deploy into a different Mod Manager profile
dotnet build -c Release -p:ModDeployDir="$env:APPDATA\com.kesomannen.gale\resonite\profiles\MyProfile"
```

`ModDeployDir` only changes where the built files are *copied*; the reference assemblies still
come from the Resonite install that `ResonitePath` points at.

To produce the Thunderstore package (lands in `build/`):

```powershell
dotnet build -c Release -t:PackTS
```

Developer notes — the two-process architecture, the KlakSpout/Unity native plugin constraints and
how to verify the output without a headset — are in [CLAUDE.md](CLAUDE.md).

-----

## Credits

  * This MOD was ported to Bepis based on **kokoa's Reso360Spout** ([GitHub](https://github.com/rassi0429/Reso360Spout)).
  * The Spout implementation was referenced from **Zozokasu's ResoniteSpout** ([GitHub](https://github.com/Zozokasu/ResoniteSpout)).