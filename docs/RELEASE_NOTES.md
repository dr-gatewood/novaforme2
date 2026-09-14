# Nova4Me2 v1.0.3

## Fixed
- Progress bars still threw `'Shimmer' name cannot be found in the name scope of ControlTemplate` on load (error toast at start-up, visible in the log). The shimmer animation now runs on the element itself instead of through a template-scoped storyboard target, so no name lookup is involved.

Everything from v1.0.2 (non-blocking disk protection watcher, once-per-disk mount guard, title-bar drive selector, fast reconnect enumeration, deferred SMART collection, recovery workstation mode) is included.

## Downloads
- `Nova4Me2-v1.0.3-win-x64.zip` — desktop app (unzip, run `Nova4Me2.exe`).
- `nova4me2-cli-v1.0.3-win-x64.zip` — command-line tool.
- `SHA256SUMS.txt`.
