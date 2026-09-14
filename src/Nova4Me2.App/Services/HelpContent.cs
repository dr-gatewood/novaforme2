namespace Nova4Me2.App.Services;

/// <summary>Context-sensitive help (F1). Lightweight markup: "# " heading, "- " bullet, blank line = paragraph break.</summary>
public static class HelpContent
{
    public static string For(string view) => Pages.TryGetValue(view, out var s) ? s : Pages["Drives"];

    public static readonly Dictionary<string, string> Pages = new()
    {
        ["Drives"] = @"# Drives
This is where you pick the disk to work on. Nova4Me2 talks to the disk sector by sector, so it does not matter that Windows shows the volume as RAW.

- Click a drive card to open it. The app reads the partition table, finds NTFS volumes (also via the backup boot sector or a signature scan) and queries SMART where the interface allows.
- Drives marked SYSTEM are the disk Windows is running from. They can be browsed but never used as a clone target.
- Open image file… lets you work on a raw .img/.dd image instead of the physical drive: the safest way to recover from a failing SSD is to image it first (Clone view) and then work from the image.
- Stabilize USB applies the standard Windows tweaks that stop enclosures from being suspended or re-probed (selective suspend, device power management, automount, disk time-out). Everything it changes is recorded and can be reverted from the same dialog.

# Keeping Windows out of the way
When a RAW or unresponsive disk is connected, Nova4Me2 offers to take it **offline and read-only** in Windows. An offline disk is ignored by the mount manager (no more mount attempts, no ""directory is invalid"" errors, no Explorer probing) while raw reads keep working. ""Bring back online"" reverses it. Every request the tool sends to a drive has a hard time-out, so a hung bridge can never freeze the app.

# Connection status
The dot in the status bar shows the link state. If the enclosure drops off the bus, the app waits for it to come back (up to the reconnect time-out in Settings) and resumes the read that was in flight. Reads are chunked and throttled if you ask for it, which many flaky bridges appreciate.

# Why RAW?
Windows reports RAW when its NTFS driver refuses the volume: a damaged boot sector or $MFT record, a dirty volume it cannot repair, I/O errors — or a sector-size mismatch introduced by the USB bridge (very common with NVMe enclosures). The Health view tells you which one applies.",
        ["Browse"] = @"# Browse
A file explorer for the recovered volume.

- Double-click folders to enter them, use the breadcrumb bar to jump back, and the filter box to narrow the list.
- Select any number of files and folders (Ctrl / Shift click) and either press Copy to… or drag them straight into an Explorer window or onto the desktop. The drag copies the real file contents; Explorer shows its normal progress dialog.
- Mode: Directory index is the fast, exact listing NTFS keeps for each folder. Rebuild from MFT walks every file record instead; use it when folders show errors or come up empty, and to list deleted files (they appear greyed out — their data may already be overwritten).
- Show system files reveals $MFT, $LogFile and friends. Alternate data streams and encrypted files are marked in the Attributes column.
- Mount as drive letter (needs WinFsp) exposes the whole volume read-only as a normal Windows drive so any program can open files from it.

# Keyboard
Enter opens a folder, Backspace goes up, Ctrl+A selects all, Ctrl+C copies the selection to the default destination, F5 refreshes.",
        ["Recover"] = @"# Recover
The queue of copy jobs. Jobs run one after another because the source drive is the bottleneck.

- Each job shows an animated progress bar, the file being read, throughput and an estimate of the remaining time.
- Files with unreadable sectors are still written; the unreadable parts are zero-filled and the file is listed as partial in the job report.
- EFS-encrypted files are copied as-is (ciphertext). They can only be opened with the original Windows user's certificate.
- When a job finishes you can export a TXT, HTML or PDF report listing everything that was copied and every problem.
- Cancel stops after the current file; nothing that was already written is removed.",
        ["Clone"] = @"# Clone / Image
Bit-for-bit copies of the whole disk or a single partition, forensic style.

- To image file: creates a raw .img that any tool can open (and that Nova4Me2 itself can browse). Pick a destination with more free space than the source.
- To another drive: writes directly to a second physical disk. Everything on the target is overwritten; the app refuses the system disk and asks you to type CONFIRM.
- Two-pass mode copies everything readable first and only then retries the unreadable chunks sector by sector, which is the ddrescue strategy for failing media.
- SHA-256 (and optionally MD5) of the data as read are computed on the fly; Verify target re-reads the copy and compares. Unreadable ranges are written to a .badsectors.txt log next to the image.
- Imaging can be resumed after a cancellation or a crash.
- The block map shows every region of the source: green readable, amber slow, red unreadable, blue is where the head is now.",
        ["Health"] = @"# Health
A non-destructive check of the disk and its NTFS structures, plus the drive's own SMART data when the interface exposes it.

- Structural score (0–100) summarises partition table, boot sectors, $MFT / $MFTMirr, dirty flag and a sample of directory indexes.
- Boot record fix likelihood is the app's estimate of whether rewriting the boot record would actually change anything. High values come with a concrete, reversible fix in the Repair view; low values mean the problem lies elsewhere (USB bridge sector translation, boot manager / BCD, or the drive itself).
- Surface scan reads the disk (quick = a sample, full = every sector) and maps unreadable and slow areas.
- SMART / NVMe health is available for natively attached drives; most USB bridges do not forward these commands.
- Reports: save everything as TXT, HTML or PDF.",
        ["Repair"] = @"# Repair
Only the well-understood, reversible fixes are automated. Every write is preceded by a backup of the exact sectors being replaced; Undo puts them back.

- Restore boot sector from backup: NTFS keeps a copy of the boot sector at the end of the volume; this copies it over a damaged first sector.
- Rebuild GPT from backup: reconstructs the primary partition table from the copy at the end of the disk.
- Restore $MFT records from $MFTMirr: replaces damaged system records 0–3 with the mirror copies.
- Writes fail on a drive that has locked itself read-only (the Health view shows the NVMe read-only flag when it can see it). In that case only copying data off is possible.

# Manual fixes
For boot-manager problems (Windows still will not boot after the volume mounts) the app prints the WinRE commands (bcdboot / bootrec) with the right drive letters filled in.",
        ["Firmware"] = @"# Firmware
Shows the drive's firmware revision, the versions known for this model and the vendor's update tool.

- Nova4Me2 does not flash firmware: vendor tools have the signed images and the drive must usually be attached natively (not via USB) for an update.
- Known issues for the model are listed when the app has them; for example several Samsung NVMe generations had firmware that could lock the drive read-only after unsafe power loss.
- Update firmware only after your data is safely copied off.",
        ["Info"] = @"# Drive info
CPU-Z style identification of the drive: vendor, model family, capacity, interface as seen by Windows versus the drive's native interface, sector sizes, NVMe controller details and the SMART health log.

- The product picture is drawn from the model data; drop a PNG named after the model (e.g. MZ-V7E500.png) or the vendor (samsung.png) into the assets folder to show a real photo.
- Export the page as a TXT / HTML / PDF report.",
        ["Settings"] = @"# Settings
- Theme: four looks; the change applies instantly.
- Reconnect time-out: how long to wait for a vanished USB drive before giving up on a read.
- Chunk size and throttle: smaller chunks and a throughput cap are gentler on failing drives and flaky bridges.
- Keepalive: a tiny read every few seconds keeps some enclosures from going to sleep.
- Copy defaults: destination folder, verification, zero-fill of unreadable sectors, alternate data streams, timestamps and what to do when a file already exists.",
    };
}
