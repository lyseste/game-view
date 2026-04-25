<h1 align="center">
  <img src="https://ik.imagekit.io/lyseste/game-view/game-view-logotext-white.png" alt="Game View logo" height="100">
</h1>
<p align="center">
A lightweight Windows viewer for USB video capture devices, designed with video game capture cards in mind. Plug in your device, select it from the list, and the feed appears full-quality in a clean window with optional audio passthrough.
</p>
<p align="center">
Built for low latency and zero bloat. Just run the app and play from your capture card's game feed.
</p>
<br/><br/>

## Features

- **GPU-accelerated rendering** using DXGI and D3D11
- **Audio passthrough** from a capture device to any output, with volume control
- **Configurable resolution** supports 720p / 1080p / 1440p / 4K, and frame rates 30 / 60 / 120, or custom 30-240
- **Real-time overlay** shows FPS and detailed render info

## Download

Grab the latest zip from the [Releases page](https://github.com/lyseste/game-view/releases), extract anywhere, and run `game-view.exe`.

Windows SmartScreen may warn about an unsigned executable - click **More info → Run anyway** to proceed.

## Usage

1. Plug in your capture device.
2. Launch `game-view.exe`. If no device was previously selected, the settings sidebar opens automatically.
3. Pick your device from **Video Device**. Pick audio input/output if you want passthrough.
4. Adjust resolution and frame rate if your device supports more than 1080p60.

Click the icon in the top-right, or press `Esc` at any time to reopen settings.

### Keyboard shortcuts

| Key | Action |
| --- | --- |
| `Esc` | Toggle settings sidebar |
| `F11` | Toggle fullscreen |
| Double-click | Toggle fullscreen |

## Build

Requires **.NET 8 SDK** for Windows.

```powershell
git clone https://github.com/lyseste/game-view.git
cd game-view
dotnet publish -c Release
```

Output lands at `bin\Release\publish\game-view.exe`.
