# Nova4Me2 v1.0.2

Second round of fixes from real-hardware testing with a RAW Samsung 970 EVO in a flapping USB enclosure.

## Fixed
- **"Take disk offline + read-only" appeared to do nothing.** Opening the disk handle blocked while Windows was busy probing the disk (in the log, all queued clicks completed together two minutes later). Protection is now handed to a background watcher that retries with short, abandonable attempts on every device-arrival event and every few seconds until the attribute lands; the dialog closes immediately and the Drives view shows the status.
- The mount-guard dialog reappeared on every click of a "not responding" disk; it is now asked once per disk.
- A progress-bar animation threw `'ShimmerMove' name cannot be found` on every progress bar, producing error toasts. Fixed.
- Reconnects and the drive selector now use a fast identity-only enumeration with tight time budgets instead of the full volume-probing scan, so a disk that is only up for a few seconds can still be re-attached.
- SMART / identify pass-through no longer delays opening a drive (collected in the background, 5 s per query).

## Added
- **Drive selector in the title bar**: pick any connected drive (or an image file) from any view; every function works on the selected drive.
- **Recovery workstation mode** (Stabilize USB dialog): Windows SAN policy *OfflineAll* so every newly discovered disk arrives offline and is never mounted or probed until brought online by hand. Reversible.

## Downloads
- `Nova4Me2-v1.0.2-win-x64.zip` — desktop app (unzip, run `Nova4Me2.exe`).
- `nova4me2-cli-v1.0.2-win-x64.zip` — command-line tool.
- `SHA256SUMS.txt`.
