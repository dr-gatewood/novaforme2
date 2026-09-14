# Nova4Me2 v1.0.1

Fixes from the first real-world run against a RAW Samsung 970 EVO in a USB enclosure.

## Fixed
- **The app could freeze when the enclosure was plugged in.** Windows kept trying to auto-mount the RAW volume and held the disk busy; several app paths waited on that. All device requests (reads, writes, IOCTLs, SMART pass-through) now use overlapped I/O with a hard time-out and cancellation, drive/volume probing during enumeration runs on throwaway threads with a time budget, and closing a device never blocks the UI thread. Nothing the app does depends on Windows mounting anything.
- Application icon: the taskbar/window/EXE icon now matches the in-app Nova4Me2 logo.

## Added
- **Mount guard.** When a disk with a RAW or unresponsive volume is connected, the app offers to take it offline and read-only in Windows (mount manager ignores it; raw reads keep working) or to disable automount. Both are reversible from the Drives view; "Don't ask again" is available in Settings.
- Drives view shows Windows' disk state (online/offline, writable/read-only, automount) with **Keep Windows off this disk** and **Bring back online** buttons; disks that Windows is holding busy are shown as "not responding" instead of stalling the list.
- Automatic drive-list refresh on device arrival/removal.
- CLI: `nova4me2 protect <N> [--online] [--status]`.
- Settings: per-request I/O time-out.

## Downloads
- `Nova4Me2-v1.0.1-win-x64.zip` — desktop app (unzip, run `Nova4Me2.exe`).
- `nova4me2-cli-v1.0.1-win-x64.zip` — command-line tool.
- `SHA256SUMS.txt`.
