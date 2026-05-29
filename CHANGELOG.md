# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.5-beta] - 2026-05-23

### Changed
- **Privacy:** the diagnostic log no longer records arbitrary window titles. Only
  the Xbox shell window (titled exactly "Xbox") is named; any other window that
  merely contains "Xbox" has its title redacted, while its size and monitor are
  still logged for detection diagnosis. No file paths, usernames, account data, or
  device serial numbers are ever logged.
- **Log size:** the activity log is now trimmed to its most recent 128 KB when it
  passes 512 KB (instead of being wiped), so the startup diagnostics and recent
  events survive. The expensive WMI device query runs only once at startup and only
  when Debug logging is enabled, so it does not affect normal performance.

## [1.3.4-beta] - 2026-05-23

### Added
- **Rich diagnostic logging** for remote troubleshooting. With Debug logging
  enabled, the log now records a startup snapshot (app and OS version and build,
  monitors and their bounds, per-slot XInput controller state with subtype and
  wired/wireless, HID game-controller device names via WMI, Xbox mode state, and
  every window matching "Xbox" with its rect and monitor bounds) plus per-event
  detail on each controller change and during mode switching. This makes logs sent
  by users on other devices and controllers enough to diagnose issues remotely.

## [1.3.3-beta] - 2026-05-23

### Fixed
- **Handheld support (ROG Ally and similar).** Detection no longer relies on the
  controller count reaching zero. Handhelds expose a built-in XInput gamepad that
  is always present, so that count never hit zero and the app never reacted.
  Switching is now based on the count rising above the baseline of always-present
  controllers, which also keeps multi-controller desktop setups correct: removing
  one of several controllers no longer exits Xbox mode, only returning all the way
  to the baseline does.
- **Respect the Windows "Restart for better performance" prompt.** On handhelds,
  entering Xbox mode makes Windows show a prompt (Restart / Start now / Stay on
  desktop) and wait for the user. The app now sends `Win+F11` once and waits
  patiently, instead of sending a second keystroke or relaunching the Xbox app,
  which could toggle the mode back off right after the user accepted or override a
  deliberate "Stay on desktop" choice.

### Changed
- Controller connect/disconnect transitions are always written to the activity log
  now (with the before/after counts and baseline), so issues are easier to diagnose
  without enabling Debug logging.

## [1.3.2] - 2026-05-23

### Added
- Embedded an application manifest (asInvoker execution level, declared
  Windows 10/11 support, system DPI awareness). Improves high-DPI rendering and
  adds another legitimacy signal to reduce antivirus heuristic flags.
- README: link to enabling the Xbox full screen experience (and a community
  guide for PCs that don't have it yet), plus a Disclaimer section.

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
