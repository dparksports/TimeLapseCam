# TimeLapseCam 📷

A powerful WinUI 3 application for time-lapse recording with AI-powered event detection.

![App Screenshot](app_screenshot.png)

## Features

- **Time-Lapse Recording**: Captures video frames at 1 FPS for efficient storage.
- **Continuous Audio**: Records real-time audio alongside the video.
- **AI Object Detection**: Uses YOLOv8 (ONNX) to detect people, pets, and objects, logging them as events.
- **Sound Detection**: Detects loud noises (e.g., claps) and bookmarks them.
- **Event Review**: Review recordings and instantly jump to detected events.
- **Live Preview**: See what the camera sees in real-time.
- **Camera Selection**: Choose from any available video capture device.

## Installation

1.  **Download**: Get the latest release from the [Releases](https://github.com/dparksports/TimeLapseCam/releases/latest) page.
2.  **Extract**: Unzip the `TimeLapseCam_v1.1.zip` file.
3.  **Run**: Double-click `Launch.cmd` — it will auto-install any missing dependencies (Windows App SDK Runtime, VC++ Redistributable) on first run, then launch the app.

> **Note**: After the first launch installs dependencies, you can run `TimeLapseCam.exe` directly.

## Usage

1.  **Capture Tab**:
    - Select your camera from the dropdown.
    - Click "Start Recording".
    - The app will record video at 1 FPS and full audio.
    - AI events (Person, Dog, Cat) and loud sounds will be logged automatically.

2.  **Review Tab**:
    - Select a recording from the list.
    - Click an event in the timeline to jump to that moment.
    - Play back at normal speed (accelerated video).

## Requirements

- Windows 10 (1809) or Windows 11.
- .NET 8 Runtime.
- A webcam.

## License

Apache 2.0 License. See [LICENSE](LICENSE) for details.
