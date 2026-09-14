# Nova4Me2 — raw NTFS recovery for drives Windows calls "RAW"

Nova4Me2 is a Windows desktop tool (plus a command-line twin) for the situation where a Windows system
drive — in the original case a **Samsung 970 EVO 500 GB (MZ-V7E500)** that locked itself after a power
cut — no longer boots, shows up as **RAW** in a USB NVMe enclosure, and keeps dropping off the bus.

It does not ask Windows to mount anything. It opens the physical device, reads the partition table and
the NTFS structures **directly from the sectors**, keeps the USB link alive across disconnects, and lets you
copy files out (drag-and-drop, "Copy to…", CLI), make a forensic image or clone, analyse the health of
the disk, apply the few safe boot-record repairs, and export reports.

> Nothing is ever written to the source drive unless you explicitly run a *Repair* action or choose it as a
> *Clone* target — and even repairs back up the sectors they replace so they can be undone.

## What it does

| Area | Details |
|---|---|
| **Read RAW volumes** | Own NTFS reader: boot sector (or its backup copy at the end of the volume, or a signature scan), `$MFT` with fixups, attribute lists, data runs, sparse files, LZNT1 compression, alternate data streams, hard links, directory `$I30` indexes, and a full-MFT scan that rebuilds the tree when indexes are damaged and lists deleted files. |
| **Keep Windows out of the way** | When a RAW or unresponsive disk is connected, the app offers to take it **offline and read-only** in Windows (the mount manager then ignores it: no more mount attempts, no "directory is invalid" errors, no Explorer probing) or to disable automount. Reversible from the Drives view. Every device request has a hard time-out with cancellation, and nothing on the UI thread ever waits for a device or for Windows' mount manager. |
| **Keep the USB link alive** | Chunked, sector-aligned unbuffered reads; failures are classified as *device gone* (wait for the drive to re-enumerate — even under a new `PhysicalDriveN` number — then resume the read) or *localised* (retry in smaller pieces down to one sector, then declare a bad sector). Keepalive reads stop enclosures from idling; optional throttling and chunk size for flaky bridges. **Stabilize USB** applies the standard Windows fixes (selective suspend, per-device power management, automount, disk time-out) and can revert them. |
| **Mount as a drive letter** | Through [WinFsp](https://winfsp.dev/rel/) (free user-mode file-system framework) the volume is exposed **read-only as a normal Windows drive** (e.g. `R:`), so Explorer and any program can open files from it. Windows' own NTFS driver is bypassed entirely. |
| **Copy files** | Multi-select copy with progress, throughput and ETA; files containing unreadable sectors are written with zero-filled gaps and flagged; timestamps/attributes preserved; ADS optional; verify-after-copy optional; drag selections straight into Explorer (virtual files, Explorer copies them lazily on its own thread). |
| **Forensic image / clone** | Whole disk or one partition → raw `.img` file or another physical disk. Two-pass ddrescue-style strategy (copy everything readable first, retry bad chunks sector-by-sector), SHA-256/MD5 of the data as read, optional target verification, resume, unreadable-sector log, animated block map. System disk and source are blocked as targets; typed confirmation required. |
| **Health** | Structural check (partition tables incl. backup GPT, boot sectors, `$MFT`/`$MFTMirr`, dirty flag, directory samples, Windows installation detection), NVMe identify + SMART health log and ATA SMART (when the interface forwards them), quick/full surface scan with latency map, a 0–100 score and a **boot-record-fix likelihood** estimate with the reasoning spelled out. |
| **Repair** | Restore NTFS boot sector from its backup, rewrite the backup boot sector, rebuild the primary GPT from the backup GPT, restore `$MFT` records 0–3 from `$MFTMirr`. Each write is preceded by a JSON backup of the replaced sectors; **Undo** restores them. Manual guidance (bcdboot/bootrec) is generated for boot-manager problems. |
| **Firmware** | Installed revision, known revisions for the model (reference table), known issues (e.g. Samsung drives locking read-only after power loss), NVMe firmware-slot capabilities, and a button to the vendor tool. Nova4Me2 does not flash firmware. |
| **Drive info** | CPU-Z style page: vendor/model family/capacity, interface as seen by Windows vs native, sector sizes, NVMe controller data, SMART, partitions, Windows' view of the volumes, a drawn product picture with the vendor colour badge (drop a PNG in `assets/` for a real photo). |
| **Analysis / Forensics** | Project folders for extractions; alternate-data-stream scan and extraction; signature carving with exact lengths over unallocated space, a volume, a disk or inside one file; steganography/embedded-data analysis (appended payloads, nested files, entropy, PNG/JPEG anomalies, LSB chi-square); Sleuth Kit-style fsstat/istat/icat/ils/ffind/blkstat/blkcat/blkls/fls/mactime/slack/USN-journal tools with CSV and body-file export. |
| **Reports** | Every analysis, hardware page, copy job and clone can be saved as **TXT, HTML or PDF**. |
| **UI** | WPF, four themes (Nova Dark, Midnight, Graphite, Aurora Light), fade/slide transitions, animated progress and gauges, context-sensitive **F1** help for every view, toast notifications, log panel. Requires Administrator (manifest). |

### Why a healthy-looking drive shows as RAW in a USB enclosure

The Health view checks for this explicitly: many NVMe-to-USB bridges present **4096-byte logical sectors**
while the volume was formatted on the native controller with **512-byte sectors** (or vice-versa). Windows'
NTFS driver then refuses the volume even though every structure is intact. Nova4Me2 reads bytes, not
"sectors", so it is unaffected — and it will tell you that the fix is to attach the SSD directly rather than
to rewrite anything.

## Download

Ready-made Windows x64 builds are on the [Releases page](https://github.com/dr-gatewood/novaforme2/releases):

* `Nova4Me2-<version>-win-x64.zip` — desktop app. Unzip anywhere, run `Nova4Me2.exe` (it asks for Administrator rights; raw disk access needs them).
* `nova4me2-cli-<version>-win-x64.zip` — single-file command-line tool.
* `SHA256SUMS.txt` — checksums. No .NET installation is required; the builds are self-contained.

Windows SmartScreen may warn about an unsigned download; the builds are produced by the public GitHub Actions
workflow in this repository, and you can always build from source instead.

## Building

Requirements: .NET 8 SDK on Windows (the desktop app is WPF; the core, CLI and tests also build and run on Linux).

```powershell
git clone https://github.com/dr-gatewood/novaforme2.git
cd novaforme2
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
# → publish\Nova4Me2\Nova4Me2.exe          (desktop app, prompts for elevation)
# → publish\nova4me2-cli\nova4me2.exe      (command line)
```

Or from Visual Studio 2022: open `Nova4Me2.sln`, set `Nova4Me2.App` as start-up project, run (it will ask
for Administrator rights — raw device access needs them).

Optional: install **WinFsp** (https://winfsp.dev/rel/) to enable *Mount as drive letter*.

The GitHub Actions workflow builds everything on Windows, runs the test-suite on Linux against generated
NTFS images, and — when a `v*` tag is pushed — publishes a GitHub Release with the zipped executables:

```powershell
git tag -a v1.0.0 -m "Nova4Me2 v1.0.0"
git push origin v1.0.0
```

## Using the desktop app

1. **Drives** — when the enclosure is plugged in, the app detects it and offers to keep Windows away from it
   (take the disk offline + read-only, or disable automount). Accept the recommended option, then click the
   disk's card (it is marked *RAW* or *OFFLINE*). The app opens it read-only, finds the NTFS volume(s) and reads
   the hardware data. If the link keeps dropping, press **Stabilize USB…**, apply, and re-plug it.
2. **Health → Analyze** — read the verdict. It tells you whether the file system is intact (then it is a
   bridge / boot-manager / hardware problem), or which structure is damaged and whether a one-click repair
   applies. Run a *Quick surface scan* to see whether the media itself is failing. Save the report.
3. **Copy your data first.** **Browse** → select folders/files → **Copy to…** (or drag them into Explorer),
   or **Clone** the whole disk to an `.img` on a healthy drive and work from the image afterwards.
   Use *Rebuild from MFT* if folders come up empty or show errors, and tick *Deleted* to see deleted files.
4. Only then consider **Repair** (each action explains itself and can be undone) or **Firmware**.

Press **F1** anywhere for help about the view you are looking at.

## Command line

```
nova4me2 list                                   drives + Windows' view of their volumes
nova4me2 info 2 --report drive.pdf              hardware / SMART / firmware page
nova4me2 scan 2                                 partitions and NTFS volumes (incl. backup boot sectors)
nova4me2 ls 2 "Users\Alice" --long              list a folder (add --mft-scan --deleted for deleted files)
nova4me2 copy 2 "Users\Alice" "Users\Bob\Pictures" --to D:\Recovered --ads --report copy.html
nova4me2 image 2 D:\970evo.img --verify --report image.html      (two-pass, SHA-256, resumable with --resume)
nova4me2 clone 2 3 --yes                        sector clone onto PhysicalDrive3 (system disk is refused)
nova4me2 health 2 --surface quick --report health.html --report health.pdf
nova4me2 repair 2 boot-sector                   (also: backup-boot-sector | gpt | mft-mirror | undo FILE)
nova4me2 mount 2 R:                             read-only drive letter via WinFsp (Ctrl+C unmounts)
nova4me2 stabilize-usb [--status|--revert]
nova4me2 protect 2                              take disk 2 offline + read-only in Windows (undo: --online)
nova4me2 forensics 2 carve --scope unalloc --extract --project case1     carve deleted files from free space
nova4me2 forensics 2 ads --extract --project case1                       alternate data streams
nova4me2 forensics 2 stego "Users\Alice\photo.jpg" --extract          hidden/appended payloads, LSB check
nova4me2 forensics 2 timeline --csv timeline.csv                         mactime-style MACB timeline
```

`<src>` may be a drive number, `\\.\PhysicalDrive2`, `\\.\E:` or a raw image file. Global options:
`--reconnect-timeout SEC --throttle MB/s --chunk KB --no-keepalive --log FILE --verbose`.

## Safety notes

* The source is opened **read-only** for everything except *Repair* and being a *Clone target*.
* Repairs write one to a few sectors, always after saving the originals to
  `%LocalAppData%\Nova4Me2\sector-backups\`. If the drive has locked itself read-only the write simply
  fails — which is itself the diagnosis (the Health view shows the NVMe read-only flag when it can see it).
* Never run CHKDSK or `bootrec /fixboot` on a failing drive before you have copied or imaged it.
* Drag-and-drop and *Copy to…* give Windows real file contents read through Nova4Me2, so partially unreadable
  files arrive with zero-filled gaps rather than being lost; the job report lists them.

## Disclaimer

Nova4Me2 is a data-recovery tool for drives that are already in trouble. It is provided **as is, without
warranty of any kind** (see `LICENSE`). Reading from a failing drive can hasten its death; copy the data you
care about first, and prefer working from an image. The *Repair* and *Clone-to-drive* features write to a
disk you choose; they back up what they replace and ask for typed confirmation, but the responsibility for
pointing them at the right disk is yours. If the data is irreplaceable and the drive is physically failing,
a professional recovery lab is the safer route.

## Contributing / reporting problems

Issues and pull requests are welcome. When reporting a problem please include the health report
(Health → *Save report…*, HTML or TXT), the drive model, the enclosure/bridge if any, and the log from
`%LocalAppData%\Nova4Me2\nova4me2.log`. Please strip anything private from file listings first.

## Limitations (honest list)

* **EFS-encrypted** files are copied as ciphertext (needs the original user's key). **WOF/CompactOS**
  compressed system files (Windows' newer per-file compression) are not decompressed; their raw stream is
  copied and they are flagged. Ordinary NTFS (LZNT1) compression *is* supported.
* **SMART/NVMe** data is only available when the interface forwards it — most USB bridges do not. Attach the
  SSD to an M.2 slot for full health data. The firmware table is a static reference, not a live feed.
* Firmware is **not** flashed by this tool; use the vendor utility with the drive attached natively.
* The WPF app was written and compiled against the .NET 8 Windows Desktop SDK and the core engine is
  covered by tests against real NTFS images, but the Windows device layer (PhysicalDrive I/O, SMART
  IOCTLs, WinFsp mount, drag-out) needs a real Windows machine with the enclosure attached to be exercised
  end to end — please report anything that misbehaves.

## License

MIT — see `LICENSE`. Third-party components are listed in `THIRD-PARTY-NOTICES.md`.

## Project layout

```
src/Nova4Me2.Core    devices (raw disk, resilient wrapper), partitions, NTFS, recovery, analysis, hardware, reports
src/Nova4Me2.Mount   WinFsp read-only file system
src/Nova4Me2.Cli     nova4me2 command line
src/Nova4Me2.App     WPF desktop application
tests/Nova4Me2.Tests xunit: unit tests + integration tests against NTFS images built with mkfs.ntfs/ntfs-3g
scripts/             build.ps1 / build.sh / make-test-image.sh
assets/              optional product pictures
```

## Tests

`scripts/make-test-image.sh` (Linux, needs `ntfs-3g` and FUSE) creates NTFS images containing nested folders,
600+ files in one directory (multi-block indexes), compressed, sparse, fragmented and Unicode-named files,
an alternate data stream, a hard link, a deleted file, plus GPT variants with damaged primary GPT and a
damaged primary boot sector. `dotnet test` verifies byte-exact recovery of every file, MFT-scan recovery of
the deleted file, backup-GPT/backup-boot-sector fallback, repairs with undo, forensic imaging with simulated
bad sectors, USB drop/reconnect handling, and all three report formats.
