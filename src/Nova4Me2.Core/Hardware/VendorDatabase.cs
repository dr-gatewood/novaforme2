namespace Nova4Me2.Core.Hardware;

public sealed class VendorInfo
{
    public string Name { get; init; } = "";
    public string Key { get; init; } = "";
    public string BrandColor { get; init; } = "#5B8DEF";
    public string ToolName { get; init; } = "";
    public string ToolUrl { get; init; } = "";
    public string SupportUrl { get; init; } = "";
    public string Note { get; init; } = "";
}

public sealed class ModelInfo
{
    public string Family { get; init; } = "";
    public string FormFactor { get; init; } = "";
    public string Interface { get; init; } = "";
    public string Controller { get; init; } = "";
    public string Nand { get; init; } = "";
    public string Dram { get; init; } = "";
    public string SeqRead { get; init; } = "";
    public string SeqWrite { get; init; } = "";
    public string Endurance { get; init; } = "";
    public string Warranty { get; init; } = "";
    public string[] KnownFirmware { get; init; } = Array.Empty<string>();
    public string LatestKnownFirmware => KnownFirmware.Length > 0 ? KnownFirmware[^1] : "";
    public string KnownIssues { get; init; } = "";
}

/// <summary>Best-effort identification of vendor and product family from the model string, plus a small spec/firmware table.</summary>
public static class VendorDatabase
{
    public static readonly VendorInfo Unknown = new() { Name = "Unknown", Key = "generic", BrandColor = "#7A8699", ToolName = "Vendor SSD utility", ToolUrl = "", SupportUrl = "" };

    private static readonly VendorInfo[] Vendors =
    {
        new() { Name = "Samsung", Key = "samsung", BrandColor = "#1428A0", ToolName = "Samsung Magician", ToolUrl = "https://semiconductor.samsung.com/consumer-storage/magician/", SupportUrl = "https://semiconductor.samsung.com/consumer-storage/support/", Note = "Magician performs firmware updates only when the drive is attached natively (SATA/NVMe), not through USB." },
        new() { Name = "Crucial (Micron)", Key = "crucial", BrandColor = "#0072CE", ToolName = "Crucial Storage Executive", ToolUrl = "https://www.crucial.com/support/storage-executive", SupportUrl = "https://www.crucial.com/support" },
        new() { Name = "Micron", Key = "micron", BrandColor = "#0072CE", ToolName = "Micron Storage Executive", ToolUrl = "https://www.micron.com/products/storage/ssd/storage-executive", SupportUrl = "https://www.micron.com/support" },
        new() { Name = "Kingston", Key = "kingston", BrandColor = "#E4002B", ToolName = "Kingston SSD Manager", ToolUrl = "https://www.kingston.com/en/support/technical/ssdmanager", SupportUrl = "https://www.kingston.com/en/support" },
        new() { Name = "Western Digital", Key = "wd", BrandColor = "#0F2D62", ToolName = "WD Dashboard", ToolUrl = "https://support-en.wd.com/app/products/product-detailweb/p/1002", SupportUrl = "https://support-en.wd.com/" },
        new() { Name = "SanDisk", Key = "sandisk", BrandColor = "#E31937", ToolName = "SanDisk Dashboard", ToolUrl = "https://www.sandisk.com/support", SupportUrl = "https://www.sandisk.com/support" },
        new() { Name = "Seagate", Key = "seagate", BrandColor = "#6EBE49", ToolName = "SeaTools", ToolUrl = "https://www.seagate.com/support/downloads/seatools/", SupportUrl = "https://www.seagate.com/support/" },
        new() { Name = "Intel / Solidigm", Key = "intel", BrandColor = "#0071C5", ToolName = "Solidigm Storage Tool", ToolUrl = "https://www.solidigm.com/support-page/drivers-downloads.html", SupportUrl = "https://www.solidigm.com/support-page.html" },
        new() { Name = "SK hynix", Key = "skhynix", BrandColor = "#E8562D", ToolName = "SK hynix Drive Manager", ToolUrl = "https://ssd.skhynix.com/download/", SupportUrl = "https://ssd.skhynix.com/" },
        new() { Name = "Kioxia / Toshiba", Key = "kioxia", BrandColor = "#DA291C", ToolName = "Kioxia SSD Utility", ToolUrl = "https://personal.kioxia.com/en-emea/support/ssd-utility.html", SupportUrl = "https://personal.kioxia.com/en-emea/support.html" },
        new() { Name = "Sabrent", Key = "sabrent", BrandColor = "#111111", ToolName = "Sabrent Rocket Control Panel", ToolUrl = "https://sabrent.com/pages/downloads", SupportUrl = "https://sabrent.com/pages/support" },
        new() { Name = "ADATA / XPG", Key = "adata", BrandColor = "#D71920", ToolName = "ADATA SSD ToolBox", ToolUrl = "https://www.adata.com/en/support/downloads/", SupportUrl = "https://www.adata.com/en/support/" },
        new() { Name = "Corsair", Key = "corsair", BrandColor = "#F2C800", ToolName = "Corsair SSD Toolbox", ToolUrl = "https://www.corsair.com/us/en/downloads", SupportUrl = "https://help.corsair.com/" },
        new() { Name = "PNY", Key = "pny", BrandColor = "#D50032", ToolName = "PNY SSD Toolbox", ToolUrl = "https://www.pny.com/support", SupportUrl = "https://www.pny.com/support" },
        new() { Name = "Lexar", Key = "lexar", BrandColor = "#0E4C92", ToolName = "Lexar SSD Dash", ToolUrl = "https://www.lexar.com/support/downloads/", SupportUrl = "https://www.lexar.com/support/" },
        new() { Name = "Transcend", Key = "transcend", BrandColor = "#C8102E", ToolName = "Transcend SSD Scope", ToolUrl = "https://www.transcend-info.com/Support/Software-10", SupportUrl = "https://www.transcend-info.com/Support" },
        new() { Name = "TeamGroup", Key = "team", BrandColor = "#E60012", ToolName = "T-Force SSD Toolbox", ToolUrl = "https://www.teamgroupinc.com/en/support/download.php", SupportUrl = "https://www.teamgroupinc.com/en/support/" },
        new() { Name = "Silicon Power", Key = "sp", BrandColor = "#0067B1", ToolName = "SP Toolbox", ToolUrl = "https://www.silicon-power.com/web/download-toolbox", SupportUrl = "https://www.silicon-power.com/web/support" },
        new() { Name = "Patriot", Key = "patriot", BrandColor = "#D71920", ToolName = "Patriot Toolbox", ToolUrl = "https://www.patriotmemory.com/pages/downloads", SupportUrl = "https://www.patriotmemory.com/pages/support" },
        new() { Name = "Phison (OEM)", Key = "phison", BrandColor = "#0C4DA2", ToolName = "OEM tool", ToolUrl = "", SupportUrl = "" },
    };

    public static VendorInfo Identify(string model, string vendor = "")
    {
        string m = (vendor + " " + model).ToUpperInvariant();
        if (m.Contains("SAMSUNG") || m.Contains("MZ-") || m.Contains("MZV") || m.Contains("MZ7") || m.Contains("MZN") || m.Contains("MZ9")) return V("samsung");
        if (m.Contains("CRUCIAL") || System.Text.RegularExpressions.Regex.IsMatch(m, @"\bCT\d+")) return V("crucial");
        if (m.Contains("MICRON")) return V("micron");
        if (m.Contains("KINGSTON") || m.Contains("SA400") || m.Contains("SNV2") || m.Contains("SNVS") || m.Contains("SKC") || m.Contains("SFYR") || m.Contains("SA2000") || m.Contains("SUV")) return V("kingston");
        if (m.Contains("WDC") || m.Contains("WD_BLACK") || m.Contains("WD BLUE") || m.Contains("WD GREEN") || m.Contains("WD RED") || m.StartsWith("WD") || m.Contains(" WD") || m.Contains("WDS")) return V("wd");
        if (m.Contains("SANDISK") || m.Contains("SDSSD")) return V("sandisk");
        if (m.Contains("SEAGATE") || m.Contains("FIRECUDA") || m.Contains("BARRACUDA") || m.Contains("IRONWOLF") || System.Text.RegularExpressions.Regex.IsMatch(m, @"\bST\d+")) return V("seagate");
        if (m.Contains("INTEL") || m.Contains("SOLIDIGM") || m.Contains("SSDPE") || m.Contains("SSDSC")) return V("intel");
        if (m.Contains("HYNIX") || m.Contains("SHGP") || m.Contains("HFS") || m.Contains("PLATINUM P") || m.Contains("GOLD P")) return V("skhynix");
        if (m.Contains("KIOXIA") || m.Contains("TOSHIBA") || m.Contains("EXCERIA") || m.Contains("KBG") || m.Contains("KXG") || m.Contains("THNS")) return V("kioxia");
        if (m.Contains("SABRENT")) return V("sabrent");
        if (m.Contains("ADATA") || m.Contains("XPG") || m.Contains("GAMMIX")) return V("adata");
        if (m.Contains("CORSAIR") || m.Contains("FORCE MP")) return V("corsair");
        if (m.Contains("PNY") || m.Contains("CS900") || m.Contains("CS3030") || m.Contains("XLR8")) return V("pny");
        if (m.Contains("LEXAR")) return V("lexar");
        if (m.Contains("TRANSCEND") || m.Contains("TS") && m.Contains("MTE")) return V("transcend");
        if (m.Contains("TEAM") || m.Contains("T-FORCE") || m.Contains("TM8")) return V("team");
        if (m.Contains("SILICON POWER") || m.Contains("SPCC")) return V("sp");
        if (m.Contains("PATRIOT")) return V("patriot");
        if (m.Contains("PHISON")) return V("phison");
        return Unknown;
    }

    private static VendorInfo V(string key) => Vendors.First(v => v.Key == key);

    private static readonly (string Prefix, ModelInfo Info)[] Models =
    {
        ("MZ-V7E", new ModelInfo { Family = "Samsung 970 EVO", FormFactor = "M.2 2280", Interface = "PCIe 3.0 x4, NVMe 1.3", Controller = "Samsung Phoenix", Nand = "Samsung 64-layer V-NAND 3-bit MLC (TLC)", Dram = "512 MB LPDDR4 (500 GB)", SeqRead = "3,400 MB/s", SeqWrite = "2,300 MB/s (500 GB)", Endurance = "300 TBW (500 GB)", Warranty = "5 years", KnownFirmware = new[] { "1B2QEXE7", "2B2QEXE7" }, KnownIssues = "Some 970 EVO units lock into a read-only / \"0 MB\" state after unsafe power loss; firmware 2B2QEXE7 improved robustness. If the drive reports read-only mode, data can only be copied off." }),
        ("MZ-V7S", new ModelInfo { Family = "Samsung 970 EVO Plus", FormFactor = "M.2 2280", Interface = "PCIe 3.0 x4, NVMe 1.3", Controller = "Samsung Phoenix (later: Elpis)", Nand = "Samsung 9x-layer V-NAND TLC", Dram = "512 MB–1 GB LPDDR4", SeqRead = "3,500 MB/s", SeqWrite = "3,200 MB/s", Endurance = "300 TBW (500 GB)", Warranty = "5 years", KnownFirmware = new[] { "1B2QEXM7", "2B2QEXM7", "3B2QEXM7", "4B2QEXM7" } }),
        ("MZ-V7P", new ModelInfo { Family = "Samsung 970 PRO", FormFactor = "M.2 2280", Interface = "PCIe 3.0 x4, NVMe 1.3", Controller = "Samsung Phoenix", Nand = "Samsung 64-layer V-NAND 2-bit MLC", Dram = "512 MB–1 GB LPDDR4", SeqRead = "3,500 MB/s", SeqWrite = "2,700 MB/s", Endurance = "600 TBW (512 GB)", Warranty = "5 years", KnownFirmware = new[] { "1B2QEXP7" } }),
        ("MZ-V8P", new ModelInfo { Family = "Samsung 980 PRO", FormFactor = "M.2 2280", Interface = "PCIe 4.0 x4, NVMe 1.3c", Controller = "Samsung Elpis", Nand = "Samsung 128-layer V-NAND TLC", Dram = "512 MB–2 GB LPDDR4", SeqRead = "7,000 MB/s", SeqWrite = "5,000 MB/s", Endurance = "600 TBW (1 TB)", Warranty = "5 years", KnownFirmware = new[] { "1B2QGXA7", "2B2QGXA7", "3B2QGXA7", "4B2QGXA7", "5B2QGXA7" }, KnownIssues = "Firmware before 5B2QGXA7 could put the drive in read-only mode (rapid wear-out bug). Update immediately if older." }),
        ("MZ-V8V", new ModelInfo { Family = "Samsung 980", FormFactor = "M.2 2280", Interface = "PCIe 3.0 x4, NVMe 1.4", Controller = "Samsung Pablo", Nand = "Samsung 128-layer V-NAND TLC", Dram = "DRAM-less (HMB)", SeqRead = "3,500 MB/s", SeqWrite = "3,000 MB/s", Endurance = "300 TBW (500 GB)", Warranty = "5 years", KnownFirmware = new[] { "1B4QFXO7", "2B4QFXO7", "3B4QFXO7" } }),
        ("MZ-V9P", new ModelInfo { Family = "Samsung 990 PRO", FormFactor = "M.2 2280", Interface = "PCIe 4.0 x4, NVMe 2.0", Controller = "Samsung Pascal", Nand = "Samsung 176-layer V-NAND TLC", Dram = "1–4 GB LPDDR4", SeqRead = "7,450 MB/s", SeqWrite = "6,900 MB/s", Endurance = "600 TBW (1 TB)", Warranty = "5 years", KnownFirmware = new[] { "0B2QJXD7", "1B2QJXD7", "3B2QJXD7", "4B2QJXD7" }, KnownIssues = "Early firmware (0B2QJXD7) showed rapid health-percentage drops; fixed in 1B2QJXD7+." }),
        ("MZ-V9E", new ModelInfo { Family = "Samsung 990 EVO", FormFactor = "M.2 2280", Interface = "PCIe 4.0 x4 / 5.0 x2, NVMe 2.0", Controller = "Samsung Piccolo", Nand = "Samsung V-NAND TLC", Dram = "DRAM-less (HMB)", SeqRead = "5,000 MB/s", SeqWrite = "4,200 MB/s", Endurance = "600 TBW (1 TB)", Warranty = "5 years" }),
        ("MZ-V6E", new ModelInfo { Family = "Samsung 960 EVO", FormFactor = "M.2 2280", Interface = "PCIe 3.0 x4, NVMe 1.2", Controller = "Samsung Polaris", Nand = "Samsung 48-layer V-NAND TLC", SeqRead = "3,200 MB/s", SeqWrite = "1,800 MB/s", Warranty = "3 years", KnownFirmware = new[] { "2B7QCXE7", "3B7QCXE7" } }),
        ("MZ-V6P", new ModelInfo { Family = "Samsung 960 PRO", FormFactor = "M.2 2280", Interface = "PCIe 3.0 x4, NVMe 1.2", Controller = "Samsung Polaris", Nand = "Samsung 48-layer V-NAND MLC", Warranty = "5 years", KnownFirmware = new[] { "1B6QCXP7", "2B6QCXP7" } }),
        ("MZ-77E", new ModelInfo { Family = "Samsung 870 EVO", FormFactor = "2.5\" SATA", Interface = "SATA 6 Gb/s", Controller = "Samsung MKX", Nand = "Samsung 128-layer V-NAND TLC", SeqRead = "560 MB/s", SeqWrite = "530 MB/s", Endurance = "300 TBW (500 GB)", Warranty = "5 years", KnownFirmware = new[] { "SVT01B6Q", "SVT02B6Q" }, KnownIssues = "Early production batches showed uncorrectable errors; firmware SVT02B6Q and RMA programmes addressed it." }),
        ("MZ-76E", new ModelInfo { Family = "Samsung 860 EVO", FormFactor = "2.5\" SATA", Interface = "SATA 6 Gb/s", Controller = "Samsung MJX", Nand = "Samsung 64-layer V-NAND TLC", SeqRead = "550 MB/s", SeqWrite = "520 MB/s", Warranty = "5 years", KnownFirmware = new[] { "RVT01B6Q", "RVT02B6Q", "RVT03B6Q", "RVT04B6Q" } }),
        ("MZ-N6E", new ModelInfo { Family = "Samsung 860 EVO M.2", FormFactor = "M.2 2280 SATA", Interface = "SATA 6 Gb/s", Controller = "Samsung MJX", Nand = "Samsung 64-layer V-NAND TLC", Warranty = "5 years" }),
        ("MZ-75E", new ModelInfo { Family = "Samsung 850 EVO", FormFactor = "2.5\" SATA", Interface = "SATA 6 Gb/s", Controller = "Samsung MGX/MHX", Nand = "Samsung 32/48-layer V-NAND TLC", Warranty = "5 years", KnownFirmware = new[] { "EMT01B6Q", "EMT02B6Q", "EMT03B6Q", "EMT04B6Q" } }),
        ("MZ-VLB", new ModelInfo { Family = "Samsung PM981 (OEM)", FormFactor = "M.2 2280", Interface = "PCIe 3.0 x4, NVMe 1.3", Controller = "Samsung Phoenix", Nand = "Samsung 64-layer V-NAND TLC", KnownIssues = "OEM drive: firmware comes from the PC vendor, not Samsung Magician." }),
        ("MZ-VL2", new ModelInfo { Family = "Samsung PM9A1 (OEM)", FormFactor = "M.2 2280", Interface = "PCIe 4.0 x4, NVMe 1.3", Controller = "Samsung Elpis", KnownIssues = "OEM drive: firmware comes from the PC vendor." }),
        ("CT", new ModelInfo { Family = "Crucial SSD (see model code: P3/P3 Plus/P5/P5 Plus NVMe, MX500/BX500 SATA)", KnownIssues = "Firmware via Crucial Storage Executive." }),
        ("WDS", new ModelInfo { Family = "WD Blue / Green / Black SSD (see suffix: SN550 = 2B0C, SN570 = 3B0C, SN770 = 3B0E, SN850X = 3B0E)", KnownIssues = "Firmware via WD Dashboard." }),
        ("SNV2S", new ModelInfo { Family = "Kingston NV2", FormFactor = "M.2 2280", Interface = "PCIe 4.0 x4, NVMe", Dram = "DRAM-less (HMB)", KnownIssues = "Firmware via Kingston SSD Manager." }),
        ("SKC3000", new ModelInfo { Family = "Kingston KC3000", FormFactor = "M.2 2280", Interface = "PCIe 4.0 x4, NVMe 1.4", Controller = "Phison E18", Nand = "Micron 176-layer TLC" }),
        ("SA400", new ModelInfo { Family = "Kingston A400", FormFactor = "2.5\" SATA", Interface = "SATA 6 Gb/s", Controller = "Phison S11 / Silicon Motion", Dram = "DRAM-less" }),
    };

    public static ModelInfo? Lookup(string model)
    {
        string m = model.ToUpperInvariant().Replace(" ", "");
        foreach (var (p, info) in Models)
            if (m.Contains(p.Replace(" ", "").ToUpperInvariant())) return info;
        return null;
    }

    /// <summary>Human readable capacity from a Samsung style model code (MZ-V7E500 → 500 GB, MZ-V7E1T0 → 1 TB).</summary>
    public static string CapacityFromModelCode(string model)
    {
        var mm = System.Text.RegularExpressions.Regex.Match(model.ToUpperInvariant(), @"MZ-[A-Z0-9]{3}(\d+)(T?)(\d?)");
        if (!mm.Success) return "";
        if (mm.Groups[2].Value == "T") return $"{mm.Groups[1].Value}.{(mm.Groups[3].Value.Length > 0 ? mm.Groups[3].Value : "0")} TB".Replace(".0 TB", " TB");
        return mm.Groups[1].Value + " GB";
    }
}
