# Nova4Me2 v1.0.3

## Added
- **VHD output.** Clone/Image can write the image as a fixed-size VHD (raw image + footer) so Windows Disk Management can attach it (Action → Attach VHD, tick Read-only) and give the recovered volume a normal drive letter. A "Convert existing .img to VHD" button and `nova4me2 vhd <image>` handle images made earlier; `.vhd` files open in Nova4Me2 like raw images.

## Fixed
- Option toggles (Whole disk / One partition / Image file / Another drive, mode switches) are rounded rectangles instead of ovals.
- Progress bars still threw `'Shimmer' name cannot be found in the name scope of ControlTemplate` on load (error toast at start-up, visible in the log). The shimmer animation now runs on the element itself instead of through a template-scoped storyboard target, so no name lookup is involved.

- Health view: the gauge labels were drawn off-centre and overlapped the arc; both gauges now centre their text, the score gauge is labelled "structural health (out of 100)" and the likelihood gauge shows a percentage.

Everything from v1.0.2 (non-blocking disk protection watcher, once-per-disk mount guard, title-bar drive selector, fast reconnect enumeration, deferred SMART collection, recovery workstation mode) is included.

## Downloads
- `Nova4Me2-v1.0.3-win-x64.zip` — desktop app (unzip, run `Nova4Me2.exe`).
- `nova4me2-cli-v1.0.3-win-x64.zip` — command-line tool.
- `SHA256SUMS.txt`.
