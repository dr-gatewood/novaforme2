using System.Runtime.InteropServices;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Nova4Me2.App.Services;

/// <summary>
/// Starts an OLE drag operation with our own data object passed straight through (WPF's DragDrop.DoDragDrop would wrap it
/// in a DataObject and hide IDataObjectAsyncCapability from Explorer). Must run on the UI (STA) thread.
/// </summary>
public static class NativeDragDrop
{
    private const int DRAGDROP_S_DROP = 0x00040100, DRAGDROP_S_CANCEL = 0x00040101, DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102, S_OK = 0;
    private const uint MK_LBUTTON = 1, DROPEFFECT_COPY = 1;

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool fEscapePressed, uint grfKeyState);
        [PreserveSig] int GiveFeedback(uint dwEffect);
    }

    [ComVisible(true)]
    private sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag(bool fEscapePressed, uint grfKeyState)
        {
            if (fEscapePressed) return DRAGDROP_S_CANCEL;
            if ((grfKeyState & MK_LBUTTON) == 0) return DRAGDROP_S_DROP;
            return S_OK;
        }
        public int GiveFeedback(uint dwEffect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] ComIDataObject pDataObj, [MarshalAs(UnmanagedType.Interface)] IDropSource pDropSource, uint dwOKEffects, out uint pdwEffect);

    /// <summary>Returns true if the data was dropped (the copy may still be running asynchronously in the target).</summary>
    public static bool Copy(ComIDataObject data)
    {
        int hr = DoDragDrop(data, new DropSource(), DROPEFFECT_COPY, out uint effect);
        return hr == DRAGDROP_S_DROP && effect != 0;
    }
}
