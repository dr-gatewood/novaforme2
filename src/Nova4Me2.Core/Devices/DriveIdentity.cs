using System.Security.Cryptography;

namespace Nova4Me2.Core.Devices;

/// <summary>Enough information to recognise the same physical drive after it re-enumerates on the USB bus.</summary>
public sealed class DriveIdentity
{
    public string Serial { get; init; } = "";
    public string Model { get; init; } = "";
    public long Length { get; init; }
    /// <summary>SHA-256 of the first 64 KiB, used when serial numbers are unavailable through the bridge.</summary>
    public byte[]? HeadSignature { get; init; }

    public static DriveIdentity Capture(IBlockDevice dev, string serial, string model)
    {
        byte[]? sig = null;
        try
        {
            int n = (int)Math.Min(65536, dev.Length);
            var head = dev.ReadBytes(0, n);
            sig = SHA256.HashData(head);
        }
        catch { }
        return new DriveIdentity { Serial = serial, Model = model, Length = dev.Length, HeadSignature = sig };
    }

    public bool Matches(string serial, string model, long length)
    {
        if (length != Length) return false;
        if (Serial.Length > 0 && serial.Length > 0) return string.Equals(Serial, serial, StringComparison.OrdinalIgnoreCase);
        return string.Equals(Model, model, StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesSignature(IBlockDevice dev)
    {
        if (HeadSignature == null) return true;
        try
        {
            int n = (int)Math.Min(65536, dev.Length);
            return SHA256.HashData(dev.ReadBytes(0, n)).AsSpan().SequenceEqual(HeadSignature);
        }
        catch { return false; }
    }

    public override string ToString() => $"{Model} S/N {Serial} ({Length} bytes)";
}
