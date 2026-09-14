# Nova4Me2 v1.0.5

## Fixed
- **Bad-sector analysis showed no sign of life** while it walked the MFT of a large image (minutes on a 500 GB clone), so it looked hung. The tab now shows a progress card with the current phase, a pulsing progress bar with a percentage for the MFT indexing and range-mapping steps, elapsed time with an estimate for the current step, and a Stop button. The verdict card explains what is being done while it runs. The CLI prints the same phases and percentages.
- Sleuth Kit → Build cluster map reports its percentage while indexing.

# Nova4Me2 v1.0.4

## Added
- **Bad-sector analysis** (Analysis / Forensics → Bad sectors). Load the `.badsectors.txt` log the imager writes next to an image (or a GNU ddrescue mapfile, or a plain LBA list) and every unreadable range is mapped to where it lands: partition table, unpartitioned gap, non-NTFS partition, free space, file slack, a live file (with the byte range inside the file and the percentage lost), a deleted file, a directory index, or NTFS metadata (for `$MFT` hits, the exact file records that were damaged, by name). A verdict says whether any file was actually affected, with an impact note per file, and the report saves as TXT + CSV into the project. The log is picked up automatically when an image with a log next to it is selected in the drive selector. CLI: `nova4me2 forensics <image> badsectors [LOG] [--csv FILE] [--out FILE]` (exit code 2 when a file or metadata structure was hit).
- The clone-complete toast points to the analysis when unreadable sectors were logged.

# Nova4Me2 v1.0.3

## Added
- **Analysis / Forensics tab** (magnifying glass): named project folders for extractions; alternate-data-stream scanner with content classification; signature-based file carving with exact-length parsing (unallocated space, whole volume/disk, or inside a single file); embedded-data and steganography analysis (appended payloads, nested files, entropy profile, PNG/JPEG container checks, LSB chi-square with LSB-plane export); Sleuth Kit-style tools (fsstat, istat, icat, ils, ffind, blkstat/blkcat with cluster owner map, blkls, fls to CSV/body file, mactime timeline, file slack, $UsnJrnl change journal). Same tools on the CLI under `nova4me2 forensics`.
- **VHD output.** Clone/Image can write the image as a fixed-size VHD (raw image + footer) so Windows Disk Management can attach it (Action → Attach VHD, tick Read-only) and give the recovered volume a normal drive letter. A "Convert existing .img to VHD" button and `nova4me2 vhd <image>` handle images made earlier; `.vhd` files open in Nova4Me2 like raw images.

- Wordmark now reads NoVa4Me2 with the NVMe2 letters in white and the rest in the accent colour.
- Progress bars (Copy/Recover, Clone, surface scan) are rounded rectangles that pulse, carry a light sweep, and grow smoothly between updates.

## Fixed
- Option toggles (Whole disk / One partition / Image file / Another drive, mode switches) are rounded rectangles instead of ovals.
- Progress bars still threw `'Shimmer' name cannot be found in the name scope of ControlTemplate` on load (error toast at start-up, visible in the log). The shimmer animation now runs on the element itself instead of through a template-scoped storyboard target, so no name lookup is involved.

- Health view: the gauge labels were drawn off-centre and overlapped the arc; both gauges now centre their text, the score gauge is labelled "structural health (out of 100)" and the likelihood gauge shows a percentage.

Everything from v1.0.2 (non-blocking disk protection watcher, once-per-disk mount guard, title-bar drive selector, fast reconnect enumeration, deferred SMART collection, recovery workstation mode) is included.

## Downloads
- `Nova4Me2-v1.0.3-win-x64.zip` — desktop app (unzip, run `Nova4Me2.exe`).
- `nova4me2-cli-v1.0.3-win-x64.zip` — command-line tool.
- `SHA256SUMS.txt`.
