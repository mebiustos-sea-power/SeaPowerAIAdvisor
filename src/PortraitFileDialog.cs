using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor;

// The Windows dialog runs on an STA thread; all Noesis and Unity work stays on the game thread.
internal static class PortraitFileDialog
{
    internal static Task<string?> OpenAsync(string title, string filterLabel)
    {
        var owner = GetActiveWindow();
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Open(owner, title, filterLabel)); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "Advisor portrait selection" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static string? Open(IntPtr owner, string title, string filterLabel)
    {
        const int capacity = 32768;
        var file = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            Marshal.WriteInt16(file, 0);
            var options = new OpenFileName
            {
                Size = Marshal.SizeOf(typeof(OpenFileName)), Owner = owner,
                Filter = filterLabel + "\0*.png;*.jpg;*.jpeg\0\0", FilterIndex = 1,
                File = file, MaxFile = capacity, Title = title,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                // Explorer, existing file/path, no current-directory change, hide read-only, no recent-file link.
                Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008 | 0x00000004 | 0x02000000
            };
            if (GetOpenFileName(options))
                return Marshal.PtrToStringUni(file) ?? throw new InvalidOperationException("The portrait selection has no path.");
            var error = CommDlgExtendedError();
            if (error != 0) throw new Win32Exception((int)error, "Portrait file dialog failed (0x" + error.ToString("X") + ").");
            return null;
        }
        finally { Marshal.FreeHGlobal(file); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class OpenFileName
    {
        public int Size = 0;
        public IntPtr Owner = IntPtr.Zero, Instance = IntPtr.Zero;
        public string? Filter = null;
        public IntPtr CustomFilter = IntPtr.Zero;
        public int MaxCustomFilter = 0, FilterIndex = 0;
        public IntPtr File = IntPtr.Zero;
        public int MaxFile = 0;
        public IntPtr FileTitle = IntPtr.Zero;
        public int MaxFileTitle = 0;
        public string? InitialDirectory = null, Title = null;
        public int Flags = 0;
        public short FileOffset = 0, FileExtension = 0;
        public string? DefaultExtension = null;
        public IntPtr CustomData = IntPtr.Zero, Hook = IntPtr.Zero, TemplateName = IntPtr.Zero, Reserved = IntPtr.Zero;
        public int ReservedValue = 0, ExtendedFlags = 0;
    }

    [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName([In, Out] OpenFileName options);
    [DllImport("comdlg32.dll", ExactSpelling = true)]
    private static extern uint CommDlgExtendedError();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetActiveWindow();
}
