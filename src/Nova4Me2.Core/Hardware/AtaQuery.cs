using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Nova4Me2.Core.Devices.Windows;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Hardware;

public sealed class AtaSmartAttribute
{
    public byte Id { get; init; }
    public ushort Flags { get; init; }
    public byte Current { get; init; }
    public byte Worst { get; init; }
    public ulong Raw { get; init; }
    public byte Threshold { get; set; }
    public bool PreFail => (Flags & 1) != 0;
    public bool Failing => Threshold > 0 && Current <= Threshold;
    public string Name => AtaQuery.AttributeName(Id);
}

public sealed class AtaSmartData
{
    public List<AtaSmartAttribute> Attributes { get; } = new();
    public byte[] Raw { get; init; } = Array.Empty<byte>();
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Firmware { get; init; } = "";
    public bool IsSsd { get; init; }
    public bool AnyFailing => Attributes.Any(a => a.Failing && a.PreFail);
    public AtaSmartAttribute? this[byte id] => Attributes.FirstOrDefault(a => a.Id == id);
    public double? TemperatureCelsius { get { var t = this[194] ?? this[190]; return t == null ? null : (double)(t.Raw & 0xFF); } }
}

/// <summary>ATA pass-through (IDENTIFY DEVICE, SMART READ DATA / THRESHOLDS) for SATA drives, incl. many USB-SATA bridges (SAT).</summary>
[SupportedOSPlatform("windows")]
public static class AtaQuery
{
    private const ushort ATA_FLAGS_DRDY_REQUIRED = 1, ATA_FLAGS_DATA_IN = 2;

    public static AtaSmartData? Smart(SafeFileHandle h, out string error)
    {
        var id = PassThrough(h, 0xEC, 0, 0, 0, 0, out error);
        var data = PassThrough(h, 0xB0, 0xD0, 0x4F, 0xC2, 1, out error);
        if (data == null) return null;
        var thr = PassThrough(h, 0xB0, 0xD1, 0x4F, 0xC2, 1, out _);
        string model = "", serial = "", fw = ""; bool ssd = false;
        if (id != null)
        {
            model = Swapped(id, 27 * 2, 40); serial = Swapped(id, 10 * 2, 20); fw = Swapped(id, 23 * 2, 8);
            ssd = Bin.U16(id, 217 * 2) == 1;
        }
        var s = new AtaSmartData { Raw = data, Model = model, Serial = serial, Firmware = fw, IsSsd = ssd };
        for (int i = 0; i < 30; i++)
        {
            int o = 2 + i * 12;
            byte aid = data[o];
            if (aid == 0) continue;
            ulong raw = 0;
            for (int k = 0; k < 6; k++) raw |= (ulong)data[o + 5 + k] << (8 * k);
            var a = new AtaSmartAttribute { Id = aid, Flags = Bin.U16(data, o + 1), Current = data[o + 3], Worst = data[o + 4], Raw = raw };
            if (thr != null) for (int j = 0; j < 30; j++) { int t = 2 + j * 12; if (thr[t] == aid) { a.Threshold = thr[t + 1]; break; } }
            s.Attributes.Add(a);
        }
        return s;
    }

    private static string Swapped(byte[] b, int off, int len)
    {
        var c = new char[len];
        for (int i = 0; i + 1 < len; i += 2) { c[i] = (char)b[off + i + 1]; c[i + 1] = (char)b[off + i]; }
        return new string(c).Trim().Replace("\0", "");
    }

    private static byte[]? PassThrough(SafeFileHandle h, byte command, byte features, byte lbaMid, byte lbaHigh, byte count, out string error)
    {
        const int hdr = 40; // sizeof(ATA_PASS_THROUGH_EX) on x64
        var buf = new byte[hdr + 512];
        Bin.PutU16(buf, 0, hdr);
        Bin.PutU16(buf, 2, (ushort)(ATA_FLAGS_DATA_IN | ATA_FLAGS_DRDY_REQUIRED));
        Bin.PutU32(buf, 8, 512);   // DataTransferLength
        Bin.PutU32(buf, 12, 15);   // TimeOutValue
        Bin.PutU64(buf, 24, hdr);  // DataBufferOffset (ULONG_PTR, x64)
        // CurrentTaskFile at offset 32 (PreviousTaskFile at 24 is only 8 bytes on x86; on x64 layout: Previous @ 24? no: Previous @ 32-8?)
        // Layout x64: Length(2) AtaFlags(2) PathId TargetId Lun Reserved(1) DataTransferLength(4) TimeOutValue(4) ReservedAsUlong(4) DataBufferOffset(8) PreviousTaskFile(8) CurrentTaskFile(8) = 40
        int cur = 32;
        buf[cur + 0] = features; buf[cur + 1] = count; buf[cur + 2] = 1; buf[cur + 3] = lbaMid; buf[cur + 4] = lbaHigh; buf[cur + 5] = 0xA0; buf[cur + 6] = command;
        var o = NativeMethods.Ioctl(h, NativeMethods.IOCTL_ATA_PASS_THROUGH, buf, buf.Length, out int err);
        if (o == null || o.Length < hdr + 512) { error = o == null ? NativeMethods.ErrorText(err) : "short reply"; return null; }
        error = "";
        // Status register in CurrentTaskFile[6] of the returned header; ERR bit 0 means failure.
        if ((o[cur + 6] & 0x01) != 0) { error = "ATA command returned error"; return null; }
        return o.AsSpan(hdr, 512).ToArray();
    }

    public static string AttributeName(byte id) => id switch
    {
        1 => "Raw read error rate", 2 => "Throughput performance", 3 => "Spin-up time", 4 => "Start/stop count", 5 => "Reallocated sectors count", 7 => "Seek error rate", 8 => "Seek time performance",
        9 => "Power-on hours", 10 => "Spin retry count", 11 => "Calibration retry count", 12 => "Power cycle count", 168 => "SATA PHY error count", 170 => "Available reserved space", 171 => "Program fail count",
        172 => "Erase fail count", 173 => "Wear leveling count", 174 => "Unexpected power loss count", 175 => "Power loss protection failure", 177 => "Wear leveling count", 179 => "Used reserved block count",
        180 => "Unused reserved block count", 181 => "Program fail count", 182 => "Erase fail count", 183 => "SATA downshift / runtime bad block", 184 => "End-to-end error", 187 => "Uncorrectable errors",
        188 => "Command timeout", 189 => "High fly writes", 190 => "Airflow temperature", 191 => "G-sense error rate", 192 => "Power-off retract count", 193 => "Load cycle count", 194 => "Temperature",
        195 => "Hardware ECC recovered", 196 => "Reallocation event count", 197 => "Current pending sectors", 198 => "Offline uncorrectable", 199 => "UDMA CRC error count", 200 => "Multi-zone error rate",
        201 => "Soft read error rate", 202 => "Data address mark errors", 220 => "Disk shift", 230 => "Drive life protection status", 231 => "SSD life left", 232 => "Available reserved space", 233 => "Media wearout indicator",
        234 => "Average/max erase count", 235 => "Good block count", 240 => "Head flying hours", 241 => "Total LBAs written", 242 => "Total LBAs read", 244 => "Average erase count", 245 => "Max erase count", 246 => "Total host sector writes",
        249 => "NAND writes (1GiB)", 250 => "Read error retry rate", 254 => "Free fall protection", _ => $"Vendor attribute {id}"
    };
}
