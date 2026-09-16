namespace Nova4Me2.Core.Analysis;

public enum Severity { Info, Good, Warning, Error, Critical }

public enum RepairKind
{
    None,
    RestoreBootSectorFromBackup,
    RestoreBackupBootSectorFromPrimary,
    RestoreGptFromBackup,
    RestoreMftFromMirror,
    ConvertDynamicToBasic,
    ConnectDirectly,
    RunChkdskAfterImaging,
    RebuildBcd,
    VendorFirmwareTool,
    CopyDataOffNow
}

public sealed class Finding
{
    public Severity Severity { get; init; }
    public string Area { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public RepairKind Repair { get; init; } = RepairKind.None;
    public string? Advice { get; init; }
    public bool IsFixableHere => Repair is RepairKind.RestoreBootSectorFromBackup or RepairKind.RestoreBackupBootSectorFromPrimary or RepairKind.RestoreGptFromBackup or RepairKind.RestoreMftFromMirror or RepairKind.ConvertDynamicToBasic;
    public override string ToString() => $"[{Severity}] {Area}: {Title} — {Detail}";
}

public static class RepairKindText
{
    public static string Title(RepairKind k) => k switch
    {
        RepairKind.RestoreBootSectorFromBackup => "Restore NTFS boot sector from its backup copy",
        RepairKind.RestoreBackupBootSectorFromPrimary => "Rewrite the backup NTFS boot sector",
        RepairKind.RestoreGptFromBackup => "Rebuild primary GPT from the backup GPT",
        RepairKind.RestoreMftFromMirror => "Restore $MFT system records from $MFTMirr",
        RepairKind.ConvertDynamicToBasic => "Convert dynamic (LDM) simple volume to a basic partition",
        RepairKind.ConnectDirectly => "Connect the SSD directly to an M.2/NVMe slot",
        RepairKind.RunChkdskAfterImaging => "Run CHKDSK only after imaging the drive",
        RepairKind.RebuildBcd => "Rebuild Windows boot files (bcdboot) from WinRE",
        RepairKind.VendorFirmwareTool => "Check firmware with the vendor's tool",
        RepairKind.CopyDataOffNow => "Copy data off the drive now",
        _ => ""
    };

    public static string Explain(RepairKind k) => k switch
    {
        RepairKind.RestoreBootSectorFromBackup => "NTFS keeps an exact copy of the boot sector in the last sector of the volume. Copying it back over the damaged first sector is the standard fix used by chkdsk/TestDisk when the primary is unreadable or zeroed. Nova4Me2 saves the original sector first so the change can be undone.",
        RepairKind.RestoreBackupBootSectorFromPrimary => "The primary boot sector is fine but its backup copy is not; rewriting the backup makes future recovery easier. Harmless, and undoable.",
        RepairKind.RestoreGptFromBackup => "GPT stores a second header and partition array at the end of the disk. Rebuilding the primary copy from it (with corrected LBAs and CRCs) restores the partition list Windows needs to find the volume.",
        RepairKind.RestoreMftFromMirror => "$MFTMirr holds copies of the first four MFT records ($MFT, $MFTMirr, $LogFile, $Volume). If record 0 is unreadable Windows reports RAW; restoring it from the mirror is what chkdsk does.",
        RepairKind.ConvertDynamicToBasic => "The disk is a Windows dynamic disk: the NTFS volume lives inside an LDM partition that only the Logical Disk Manager understands, so a foreign or damaged LDM database (or another Windows edition) leaves the volume invisible even though it is intact. Because this simple volume occupies its partition exactly, the disk can be made basic by rewriting only the partition table: on GPT the LDM data entry becomes a Basic data entry and the LDM metadata entry is removed (both GPT copies, CRCs recomputed); on MBR the type byte 0x42 becomes 0x07. The NTFS volume itself is not touched. Windows then mounts it as an ordinary disk. Undo restores the dynamic layout.",
        RepairKind.ConnectDirectly => "USB NVMe enclosures often expose 4096-byte logical sectors while the volume was formatted with 512-byte sectors, or drop the link under sustained load. Both make Windows report RAW even though the file system is intact. A direct PCIe/M.2 connection removes the bridge from the equation.",
        RepairKind.RunChkdskAfterImaging => "CHKDSK writes to the drive and can make things worse on failing hardware. Only run it against a clone or after all wanted files are copied off.",
        RepairKind.RebuildBcd => "With intact NTFS structures the boot failure usually lives in the boot manager / BCD store. From WinRE: bcdboot X:\\Windows /s S: /f UEFI (X: = Windows volume, S: = EFI System Partition), then bootrec /rebuildbcd.",
        RepairKind.VendorFirmwareTool => "Some SSD firmware versions have known bugs that put the drive in a read-only or degraded state after power loss. The vendor tool (e.g. Samsung Magician) checks and installs firmware; it needs the drive attached natively (not USB).",
        RepairKind.CopyDataOffNow => "Every extra minute on a failing drive risks more loss. Copy the important folders first, then image the whole disk, and only then experiment with repairs.",
        _ => ""
    };
}
