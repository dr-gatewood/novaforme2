using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Util;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Nova4Me2.App.Services;

/// <summary>
/// OLE data object that offers "virtual files" (CFSTR_FILEDESCRIPTORW + CFSTR_FILECONTENTS as IStream) so a selection can be
/// dragged from the app straight into Explorer. Explorer pulls each file's contents lazily through our NTFS reader.
/// Implements IDataObjectAsyncCapability so the drop happens on a background thread and the app stays responsive.
/// </summary>
[ComVisible(true)]
public sealed class VirtualFileDataObject : ComIDataObject, IDataObjectAsyncCapability
{
    private const int S_OK = 0, DV_E_FORMATETC = unchecked((int)0x80040064), DV_E_TYMED = unchecked((int)0x80040069), OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003), E_NOTIMPL = unchecked((int)0x80004001);
    private const int FD_ATTRIBUTES = 0x4, FD_CREATETIME = 0x8, FD_ACCESSTIME = 0x10, FD_WRITESTIME = 0x20, FD_FILESIZE = 0x40, FD_PROGRESSUI = 0x4000, FD_UNICODE = unchecked((int)0x8000);
    private readonly short _cfDescriptor = (short)DataFormats.GetDataFormat("FileGroupDescriptorW").Id;
    private readonly short _cfContents = (short)DataFormats.GetDataFormat("FileContents").Id;
    private readonly short _cfPreferred = (short)DataFormats.GetDataFormat("Preferred DropEffect").Id;
    private readonly List<Extractor.PlannedItem> _items;
    private readonly NtfsVolume _vol;
    private readonly Dictionary<short, byte[]> _extra = new();
    private bool _async, _inOperation;

    public int Count => _items.Count;
    public long TotalBytes => _items.Where(i => !i.Entry.IsDirectory).Sum(i => i.Entry.Size);

    public VirtualFileDataObject(IDirectorySource src, IEnumerable<NtfsEntry> roots, int maxItems = 50000)
    {
        _vol = src.Volume;
        var ex = new Extractor(src);
        _items = new List<Extractor.PlannedItem>();
        foreach (var it in ex.Plan(roots, new CopyOptions { SkipMetaFiles = true, SkipReparsePoints = true }))
        {
            _items.Add(it);
            if (_items.Count >= maxItems) break;
        }
    }

    // ---- IDataObject ----
    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat == _cfDescriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = ToHGlobal(BuildDescriptor());
            return;
        }
        if (format.cfFormat == _cfContents && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
        {
            int idx = format.lindex;
            if (idx < 0 || idx >= _items.Count) throw new COMException("bad lindex", DV_E_FORMATETC);
            var e = _items[idx].Entry;
            var stream = e.IsDirectory ? (Stream)new MemoryStream() : _vol.OpenFile(e);
            medium.tymed = TYMED.TYMED_ISTREAM;
            medium.unionmember = Marshal.GetComInterfaceForObject(new ComStream(stream), typeof(IStream));
            return;
        }
        if (format.cfFormat == _cfPreferred && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = ToHGlobal(BitConverter.GetBytes(1)); // DROPEFFECT_COPY
            return;
        }
        if (_extra.TryGetValue(format.cfFormat, out var bytes) && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = ToHGlobal(bytes);
            return;
        }
        throw new COMException("format not supported", DV_E_FORMATETC);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("not supported", DV_E_TYMED);

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == _cfDescriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) return S_OK;
        if (format.cfFormat == _cfContents && (format.tymed & TYMED.TYMED_ISTREAM) != 0) return S_OK;
        if (format.cfFormat == _cfPreferred && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) return S_OK;
        if (_extra.ContainsKey(format.cfFormat)) return S_OK;
        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; formatOut.ptd = IntPtr.Zero; return 0x00040130; /* DATA_S_SAMEFORMATETC */ }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        // Explorer stores "Performed DropEffect", "DropDescription" etc. Keep the bytes so later GetData calls succeed.
        try
        {
            if (medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero)
            {
                IntPtr p = GlobalLock(medium.unionmember);
                try
                {
                    int size = (int)GlobalSize(medium.unionmember);
                    var b = new byte[size];
                    Marshal.Copy(p, b, 0, size);
                    _extra[formatIn.cfFormat] = b;
                }
                finally { GlobalUnlock(medium.unionmember); }
            }
        }
        catch { }
        if (release) ReleaseStgMedium(ref medium);
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException("not supported", E_NOTIMPL);
        var list = new List<FORMATETC>
        {
            new() { cfFormat = _cfDescriptor, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
            new() { cfFormat = _cfContents, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_ISTREAM },
            new() { cfFormat = _cfPreferred, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
        };
        foreach (var k in _extra.Keys) list.Add(new FORMATETC { cfFormat = k, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL });
        return new FormatEnumerator(list);
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) { connection = 0; return OLE_E_ADVISENOTSUPPORTED; }
    public void DUnadvise(int connection) => throw new COMException("not supported", OLE_E_ADVISENOTSUPPORTED);
    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) { enumAdvise = null; return OLE_E_ADVISENOTSUPPORTED; }

    // ---- IDataObjectAsyncCapability ----
    public void SetAsyncMode(int fDoOpAsync) => _async = fDoOpAsync != 0;
    public void GetAsyncMode(out int pfIsOpAsync) => pfIsOpAsync = _async ? -1 : 0;
    public void StartOperation(IBindCtx? pbcReserved) => _inOperation = true;
    public void InOperation(out int pfInAsyncOp) => pfInAsyncOp = _inOperation ? -1 : 0;
    public void EndOperation(int hResult, IBindCtx? pbcReserved, uint dwEffects) { _inOperation = false; Log.Info($"Drag-out copy finished (hr=0x{hResult:X8}, {_items.Count} items)."); }

    // ---- helpers ----
    private byte[] BuildDescriptor()
    {
        const int fdSize = 592;
        var b = new byte[4 + fdSize * _items.Count];
        Bin.PutU32(b, 0, (uint)_items.Count);
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var e = it.Entry;
            int o = 4 + i * fdSize;
            uint flags = (uint)(FD_ATTRIBUTES | FD_UNICODE | FD_PROGRESSUI | FD_WRITESTIME | FD_CREATETIME);
            uint attrs = e.IsDirectory ? 0x10u : 0x80u;
            if (!e.IsDirectory) flags |= FD_FILESIZE;
            Bin.PutU32(b, o, flags);
            Bin.PutU32(b, o + 44, attrs);
            Bin.PutU64(b, o + 48, Ft(e.Created));
            Bin.PutU64(b, o + 56, Ft(e.Accessed));
            Bin.PutU64(b, o + 64, Ft(e.Modified));
            ulong size = e.IsDirectory ? 0 : (ulong)Math.Max(0, e.Size);
            Bin.PutU32(b, o + 72, (uint)(size >> 32));
            Bin.PutU32(b, o + 76, (uint)(size & 0xFFFFFFFF));
            string name = it.RelativePath.Replace('/', '\\');
            if (name.Length > 259) name = name[..259];
            var chars = System.Text.Encoding.Unicode.GetBytes(name);
            Array.Copy(chars, 0, b, o + 80, chars.Length);
        }
        return b;
    }

    private static ulong Ft(DateTime t) { try { return t <= DateTime.MinValue ? 0 : (ulong)t.ToFileTimeUtc(); } catch { return 0; } }

    private static IntPtr ToHGlobal(byte[] data)
    {
        IntPtr h = GlobalAlloc(0x0042 /* GMEM_MOVEABLE | GMEM_ZEROINIT */, (UIntPtr)data.Length);
        IntPtr p = GlobalLock(h);
        try { Marshal.Copy(data, 0, p, data.Length); } finally { GlobalUnlock(h); }
        return h;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr h);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    private sealed class FormatEnumerator : IEnumFORMATETC
    {
        private readonly List<FORMATETC> _f;
        private int _i;
        public FormatEnumerator(List<FORMATETC> f) { _f = f; }
        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            int n = 0;
            while (n < celt && _i < _f.Count) rgelt[n++] = _f[_i++];
            if (pceltFetched != null && pceltFetched.Length > 0) pceltFetched[0] = n;
            return n == celt ? 0 : 1;
        }
        public int Skip(int celt) { _i = Math.Min(_f.Count, _i + celt); return 0; }
        public int Reset() { _i = 0; return 0; }
        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(_f) { _i = _i };
    }
}

[ComImport, Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    void SetAsyncMode([In] int fDoOpAsync);
    void GetAsyncMode([Out] out int pfIsOpAsync);
    void StartOperation([In] IBindCtx? pbcReserved);
    void InOperation([Out] out int pfInAsyncOp);
    void EndOperation([In] int hResult, [In] IBindCtx? pbcReserved, [In] uint dwEffects);
}

/// <summary>Minimal read-only IStream over a .NET Stream (what Explorer uses to pull FileContents).</summary>
[ComVisible(true)]
public sealed class ComStream : IStream
{
    private readonly Stream _s;
    private readonly object _lock = new();
    public ComStream(Stream s) { _s = s; }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        int total = 0;
        lock (_lock)
        {
            while (total < cb) { int n = _s.Read(pv, total, cb - total); if (n <= 0) break; total += n; }
        }
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, total);
    }

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        long pos;
        lock (_lock) pos = _s.Seek(dlibMove, (SeekOrigin)dwOrigin);
        if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, pos);
    }

    public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new System.Runtime.InteropServices.ComTypes.STATSTG { type = 2 /* STGTY_STREAM */, cbSize = _s.Length, grfMode = 0 };
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("read-only", unchecked((int)0x80030005) /* STG_E_ACCESSDENIED */);
    public void SetSize(long libNewSize) => throw new COMException("read-only", unchecked((int)0x80030005));
    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        var buf = new byte[1 << 20];
        long read = 0, written = 0;
        while (read < cb)
        {
            int want = (int)Math.Min(buf.Length, cb - read);
            int n;
            lock (_lock) n = _s.Read(buf, 0, want);
            if (n <= 0) break;
            read += n;
            var w = Marshal.AllocHGlobal(8);
            try { pstm.Write(buf, n, w); written += Marshal.ReadInt64(w); } finally { Marshal.FreeHGlobal(w); }
        }
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, read);
        if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, written);
    }
    public void Commit(int grfCommitFlags) { }
    public void Revert() { }
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("not supported", unchecked((int)0x80030021));
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("not supported", unchecked((int)0x80030021));
    public void Clone(out IStream ppstm) => throw new COMException("not supported", unchecked((int)0x80004001));
}
