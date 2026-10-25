# Changelog

## 0.1.0 (first public release, not yet published)

First release. What it contains:

- **Setup** with two profiles. *Recommended* (14 settings): less promotion, ad profiling and nagging, nothing you would
  miss. *Maximum performance* (24 settings and 7 unticked extras): the same plus fewer optional background functions and
  the desktop power settings, with the cost of each named. Every setting is documented by Microsoft or was checked
  read-only on a Windows build; community-only settings are never in a profile.
- **All settings**: about a hundred Windows settings with what each writes, its risk and what is known about it. Machine
  policies apply on Pro and higher only; Windows Home shows them as not applicable.
- **Telemetry**: the official levers only in the profiles (Required diagnostic data, advertising ID, tailored
  experiences, feedback prompts, activity history, error reporting, update peer sharing); the telemetry service and the
  data-collection tasks, which Microsoft does not document as a control, are unticked extras.
- **Apps** (winget), **Configurations** (allowlisted app settings), **Profiles** (save a PC, run it on another),
  **Activation** (read the licence state; install and activate a key you own), **Activity** (history with exact undo).
- **NVIDIA**: driver lookup, trimmed install with NVCleanstall, your profiles backed up and put back, and Mesura's own
  driver profile (three documented settings: G-SYNC for full screen and windowed, and maximum-performance power mode on desktops only).
- English and Russian interface; a command line that previews by default.

Known limits: not signed; checked on one Windows 11 25H2 Pro PC with read-only previews; never applied to a clean PC,
Windows Home or Windows 10 by the author; the NVIDIA step has never run on a GeForce card. See the README.
