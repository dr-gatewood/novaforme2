# Nova4Me2 v1.0.0 — raw NTFS recovery for drives Windows calls RAW

First release. Windows x64, self-contained (no .NET install needed). Both executables request Administrator rights (raw disk access).

## Downloads
- **Nova4Me2-v1.0.0-win-x64.zip** — desktop app. Unzip anywhere and run `Nova4Me2.exe`.
- **nova4me2-cli-v1.0.0-win-x64.zip** — single-file command-line tool `nova4me2.exe`.
- **SHA256SUMS.txt** — checksums.
- Optional: install [WinFsp](https://winfsp.dev/rel/) to enable *Mount as drive letter*.

## Highlights
- Reads NTFS directly from sectors (boot sector / backup boot sector / signature scan, MFT, attribute lists, compression, sparse, ADS, hard links, full-MFT scan with deleted files) — a RAW volume in Windows is fine.
- Keeps flaky USB enclosures usable: chunked reads, automatic re-attach after a link drop (even under a new drive number), keepalive, throttling, plus a revertible **Stabilize USB** helper.
- Read-only drive-letter mount through WinFsp; multi-select copy; drag-and-drop into Explorer.
- Forensic image/clone (two-pass bad-sector strategy, SHA-256/MD5, verify, resume, block map).
- Health analysis with a structural score and a **boot-record-fix likelihood** estimate; NVMe/ATA SMART; surface scan.
- Reversible repairs (boot sector from backup, backup boot sector, GPT from backup, $MFT from $MFTMirr) with sector backups and undo.
- Firmware reference (incl. Samsung 970 EVO read-only lock notes), CPU-Z style drive info, TXT/HTML/PDF reports, four themes, F1 context help.

## Notes
- Built by the tagged CI run on a Windows runner.
- The Windows device layer, WinFsp mount and drag-out have not yet been exercised on physical hardware — see the README's limitations section and please report issues.
