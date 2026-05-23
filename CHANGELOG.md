# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.1] - 2026-05-23

### Changed
- Embedded proper version metadata (product, description, company, version) in
  the executable. Improves the file's Properties details and reduces some
  antivirus heuristic false positives for the unsigned binary.

## [1.3.0] - 2026-05-23

### Added
- A custom minimal gamepad icon, embedded in the executable and used for the
  tray icon (green when active, grey when paused). README banner/logo images.

## [1.2.0] - 2026-05-23

### Added
- **About** dialog with version and a link to the GitHub project.
- **Debug logging** option in Settings. By default the log stays minimal
  (start, mode changes, exit); enable this for verbose diagnostics.

### Fixed
- **Open Log File** now opens the log in Notepad. Previously it tried to launch
  the `.log` file directly, which failed with "the system cannot find the path
  specified" when no app was associated with `.log`.

## [1.1.0] - 2026-05-23

### Changed
- Replaced the 1-second polling loop with an **event-driven** architecture: a
  message-only window listens for device interface arrival/removal notifications
  (`WM_DEVICECHANGE` via `RegisterDeviceNotification`). Idle CPU usage is now zero;
  the app only does work when a controller is connected or disconnected.
- A short debounce coalesces bursts of device messages and gives XInput time to
  recognise a freshly powered controller.

### Removed
- The configurable poll interval setting (no longer applicable).

## [1.0.0] - 2026-05-23

### Added
- Automatic switching of the Windows 11 Xbox full screen experience based on
  Xbox controller connection state.
- XInput-based controller detection (wired, wireless dongle, Bluetooth).
- Monitor-relative Xbox mode detection that works on any resolution and
  multi-monitor setup.
- System tray interface with pause/resume.
- Settings dialog: enable-on-connect, disable-on-disconnect, start with Windows,
  and configurable poll interval.
- Activity logging to `%AppData%\AutoXboxMode\app.log`.
