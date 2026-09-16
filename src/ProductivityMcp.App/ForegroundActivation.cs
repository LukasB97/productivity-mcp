using System.Runtime.InteropServices;

namespace ProductivityMcp.App;

internal static partial class ForegroundActivation
{
    public static void AllowProcess(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = AllowSetForegroundWindow(processId);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
