# Sinaxxr.FFME.Windows

[![NuGet version](https://img.shields.io/nuget/v/Sinaxxr.FFME.Windows.svg)](https://www.nuget.org/packages/Sinaxxr.FFME.Windows)
[![NuGet downloads](https://img.shields.io/nuget/dt/Sinaxxr.FFME.Windows.svg)](https://www.nuget.org/packages/Sinaxxr.FFME.Windows)

A fork of [unosquare/ffmediaelement](https://github.com/unosquare/ffmediaelement) — the advanced WPF MediaElement alternative powered by FFmpeg. This fork adds **FFmpeg 8 support** and a set of **lifecycle stability fixes**.

![ffmeplay](https://github.com/unosquare/ffmediaelement/raw/master/Support/ffmeplay.png)

## About This Fork

This fork tracks upstream FFME 7.0.361 with the following additions:

- **FFmpeg 8 / FFmpeg.AutoGen 8.0.0.1** — patched container/decoder code for the FFmpeg 8 API breakages (display matrix lookup via `av_packet_side_data_get`, `pkt_size` → `opaque`, `SwsFlags` enum, etc.).
- **Dedicated decode/read worker threads** — `PacketReadingWorker` and `FrameDecodingWorker` are pinned to dedicated threads instead of the .NET ThreadPool, eliminating audio stutter caused by ThreadPool dispatch starvation under sustained load.
- **DirectSoundPlayer audio output** — replaces the legacy WaveOut path that exhibited reliability issues during fast open/close cycles.
- **Seek-to-zero close/open workaround** — FFME carries a sticky "ended" flag across `Close`/`Open` cycles that prevents fresh playback; this fork issues a `Seek(TimeSpan.Zero)` between `Open` and `Play` to clear it.

The package targets **net8.0-windows** only (upstream's net48 target has been dropped).

If you don't need any of the above, the upstream package at [FFME.Windows](https://www.nuget.org/packages/FFME.Windows) is the better choice.

## Quick Usage Guide for WPF Apps

### Get Started

1. Open Visual Studio and create a new WPF Application.

   **Target Framework must be net8.0-windows or above.**

2. Install the NuGet package:
   ```bash
   PM> Install-Package Sinaxxr.FFME.Windows
   ```
   Or via the dotnet CLI:
   ```bash
   dotnet add package Sinaxxr.FFME.Windows
   ```

3. Acquire the FFmpeg 8 shared binaries (x64).

   Download a build from [BtbN/FFmpeg-Builds releases](https://github.com/BtbN/FFmpeg-Builds/releases) — the `ffmpeg-master-latest-win64-gpl-shared.zip` (or any tagged 8.x release) works. Extract the contents of the `bin` folder to a well-known location, e.g. `c:\ffmpeg\x64`.

   The folder should contain:
   - `avcodec-62.dll`
   - `avdevice-62.dll`
   - `avfilter-11.dll`
   - `avformat-62.dll`
   - `avutil-60.dll`
   - `swresample-6.dll`
   - `swscale-9.dll`
   - `ffmpeg.exe`, `ffplay.exe`, `ffprobe.exe` (optional executables bundled in the same zip)

   Note: BtbN's FFmpeg 8 builds are configured `--disable-postproc`, so there is no `postproc-*.dll`. FFME does not require it.

4. In your application's startup code (e.g. `App.xaml.cs` constructor or the `Main` method), set the FFmpeg directory before any media is opened:

   ```csharp
   Unosquare.FFME.Library.FFmpegDirectory = @"c:\ffmpeg\x64";
   ```

   Then use the FFME `MediaElement` control as you would any other WPF control.

### Example

In your main window (e.g. `MainWindow.xaml`):

* Add the namespace:
  ```xml
  xmlns:ffme="clr-namespace:Unosquare.FFME;assembly=ffme.win"
  ```

* Add the FFME control:
  ```xml
  <ffme:MediaElement x:Name="Media" Background="Gray" LoadedBehavior="Play" UnloadedBehavior="Manual" />
  ```

* Play files or streams by calling the asynchronous `Open` method:
  ```csharp
  await Media.Open(new Uri(@"c:\your-file-here"));
  ```

* Close the media:
  ```csharp
  await Media.Close();
  ```

#### Additional Usage Notes
- The `Unosquare.FFME.Windows.Sample` project in this repository provides usage examples for plenty of features. Use it as your main reference.
- Original API documentation is hosted upstream at [unosquare.github.io/ffmediaelement](http://unosquare.github.io/ffmediaelement/api/Unosquare.FFME.html).

## Features Overview
FFME is an advanced and close drop-in replacement for [Microsoft's WPF MediaElement Control](https://msdn.microsoft.com/en-us/library/system.windows.controls.mediaelement(v=vs.110).aspx). While the standard MediaElement uses DirectX (DirectShow) for media playback, FFME uses [FFmpeg](http://ffmpeg.org/) to read and decode audio and video. This means that for those of you who want to support stuff like HLS playback, or just don't want to go through the hassle of installing codecs on client machines, using FFME *might* just be the answer.

FFME provides multiple improvements over the standard MediaElement such as:
- Fast media seeking and frame-by-frame seeking.
- Properties such as Position, Balance, SpeedRatio, IsMuted, and Volume are all Dependency Properties.
- Additional and extended media events. Extracting (and modifying) video, audio and subtitle frames is very easy.
- Easily apply FFmpeg video and audio filtergraphs.
- Extract media metadata and specs of a media stream (title, album, bit rate, codecs, FPS, etc).
- Apply volume, balance and speed ratio to media playback.
- MediaState actually works on this control. The standard WPF MediaElement is severely lacking in this area.
- Ability to pick media streams contained in a file or a URL.
- Specify input and codec parameters.
- Opt-in hardware decoding acceleration via devices or via codecs.
- Capture stream packets, audio, video and subtitle frames.
- Change raw video, audio and subtitle data upon rendering.
- Perform custom stream reading and stream recording.

*... all in a single MediaElement control*

FFME also supports opening capture devices. See example URLs below and [issue #48](https://github.com/unosquare/ffmediaelement/issues/48)
```
device://dshow/?audio=Microphone (Vengeance 2100):video=MS Webcam 4000
device://gdigrab?title=Command Prompt
device://gdigrab?desktop
```

If you'd like audio to not change pitch while changing the SpeedRatio property, you'll need the `SoundTouch.dll` library v2.1.1 available on the same directory as the FFmpeg binaries. You can get the [SoundTouch library here](https://www.surina.net/soundtouch/).

### About how it works

First off, let's review a few concepts. A `packet` is a group of bytes read from the input. All `packets` are of a specific `MediaType` (Audio, Video, Subtitle, Data), and contain some timing information and most importantly compressed data. Packets are sent to a `Codec` and in turn, the codec produces `Frames`. Please note that producing 1 `frame` does not always take exactly 1 `packet`. A `packet` may contain many `frames` but also a `frame` may require several `packets` for the decoder to build it. `Frames` will contain timing informattion and the raw, uncompressed data. Now, you may think you can use `frames` and show pixels on the screen or send samples to the sound card. We are close, but we still need to do some additional processing. Turns out different `Codecs` will produce different uncompressed data formats. For example, some video codecs will output pixel data in ARGB, some others in RGB, and some other in YUV420. Therefore, we will need to `Convert` these `frames` into something all hardware can use natively. I call these converted frames, `MediaBlocks`. These `MediaBlocks` will contain uncompressed data in standard Audio and Video formats that all hardware is able to receive.

The process described above is implemented in 3 different layers:
- The `MediaContainer` wraps an input stream. This layer keeps track of a `MediaComponentSet` which is nothing more than a collecttion of `MediaComponent` objects. Each `MediaComponent` holds `packet` **caching**, `frame` **decoding**, and `block` **conversion** logic. It provides the following important functionality:
  - We call `Open` to open the input stream and detect the different stream components. This also determines the codecs to use.
  - We call `Read` to read the next available packet and store it in its corresponding component (audio, video, subtitle, data, etc)
  - We call `Decode` to read the following packet from the queue that each of the components hold, and return a set of frames.
  - Finally, we call `Convert` to turn a given `frame` into a `MediaBlock`.
- The `MediaEngine` wraps a `MediaContainer` and it is responsible for executing commands to control the input stream (Play, Pause, Stop, Seek, etc.) while keeping keeping 3 background workers.
  - The `PacketReadingWroker` is designed to continuously read packets from the `MediaContainer`. It will read packets when it needs them and it will pause if it does not need them. This is determined by how much data is in the cache. It will try to keep approximately 1 second of media packets at all times.
  - The `FrameDecodingWroker` gets the packets that the `PacketReadingWorker` writes and decodes them into frames. It then converts those frames into `blocks` and writes them to a `MediaBlockBuffer`. This block buffer can then be read by something else (the following worker described here) so its contents can be rendered.
  - Finally, the `BlockRenderingWorker` reads blocks form the `MediaBlockBuffer`s and sends those blocks to a plat-from specific `IMediaRenderer`.
- At the highest level, we have a `MediaElement`. It wraps a `MediaEngine` and it contains platform-specific implementation of methods to perform stuff like audio rendering, video rendering, subtitle rendering, and property synchronization between the `MediaEngine` and itself.

A high-level diagram is provided as additional reference below.
![arch-michelob-2.0](https://github.com/unosquare/ffmediaelement/raw/master/Support/arch-michelob-2.0.png)

## Building from Source

1. Clone this repository and make sure you have the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or above installed.
2. Download the FFmpeg 8 shared binaries (see the soname list under *Get Started* above).
3. Extract them to a well-known folder (e.g. `c:\ffmpeg\x64`).
4. Open `Unosquare.FFME.sln` in Visual Studio 2022 (or newer) and set `Unosquare.FFME.Windows.Sample` as the startup project.
5. In `App.xaml.cs`, locate the line `Library.FFmpegDirectory = @"c:\ffmpeg";` and replace the path so it points to the folder where you extracted the FFmpeg DLLs.
6. Click `Start` to run. You should see a sample media player. Click on the `Open` icon at the bottom right and enter a URL or path to a media file.
7. The compiled assembly is `ffme.win.dll`.

### ffmeplay.exe Sample Application

The source code for this project contains a very capable media player (`FFME.Windows.Sample`) covering most of the use cases for the `FFME` control. If you are just checking things out, here is a quick set of shortcut keys that `ffmeplay` accepts.

| Shortcut Key | Function Description |
| --- | --- |
| G | Example of toggling subtitle color |
| Left | Seek 1 frame to the left |
| Right | Seek 1 frame to the right |
| + / Volume Up | Increase Audio Volume |
| - / Volume Down | Decrease Audio Volume |
| M / Volume Mute | Mute Audio |
| Up | Increase playback Speed |
| Down | Decrease playback speed |
| A | Cycle Through Audio Streams |
| S | Cycle Through Subtitle Streams |
| Q | Cycle Through Video Streams |
| C | Cycle Through Closed Caption Channels |
| R | Reset Changes |
| Y / H | Contrast: Increase / Decrease |
| U / J | Brightness: Increase / Decrease |
| I / K | Saturation: Increase / Decrease |
| E | Example of cycling through audio filters |
| T | Capture Screenshot to `desktop/ffplay` folder |
| W | Start/Stop recording packets (no transcoding) into a transport stream to `desktop/ffplay` folder. |
| Double-click | Enter fullscreen |
| Escape | Exit fullscreen |
| Mouse Wheel Up / Down | Zoom: In / Out |

## Thanks
*In no particular order*

- To **Mario Di Vece, Unosquare, and the original FFME contributors** for building the project this fork is based on. See the [upstream repository](https://github.com/unosquare/ffmediaelement) for the full history.
- To [zgabi](https://github.com/zgabi/ffmediaelement) for the initial FFmpeg 8 patch this fork builds on.
- To the [FFmpeg team](http://ffmpeg.org/) for making the Swiss Army Knife of media. I encourage you to donate to them.
- To the [NAudio](https://github.com/naudio/NAudio) team for making the best audio library out there for .NET.
- To Ruslan Balanukhin for his FFmpeg interop bindings generator tool: [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen).
- To Martin Bohme for his [tutorial](http://dranger.com/ffmpeg/) on creating a video player with FFmpeg.
- To Barry Mieny for his beautiful [FFmpeg logo](http://barrymieny.deviantart.com/art/isabi4-for-Windows-105473723).

## Similar Projects
- [Meta Vlc](https://github.com/higankanshi/Meta.Vlc)
- [Microsoft FFmpeg Interop](https://github.com/Microsoft/FFmpegInterop)
- [WPF-MediaKit](https://github.com/Sascha-L/WPF-MediaKit)
- [LibVLC.NET](https://libvlcnet.codeplex.com/)
- [Microsoft Player Framework](http://playerframework.codeplex.com/)

## License
- This fork retains the original [LICENSE](LICENSE). Please refer to that file for the full terms.
- Note: distribution of FFmpeg binaries is subject to FFmpeg's own licensing (LGPL or GPL depending on build configuration). This fork's NuGet package does not include FFmpeg binaries — they must be provided separately at runtime.