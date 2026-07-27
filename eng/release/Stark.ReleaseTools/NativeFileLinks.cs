using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Stark.ReleaseTools;

internal static class NativeFileLinks
{
    public static void CreateHardLink(string path, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkWindows(path, target, IntPtr.Zero))
            {
                throw new IOException($"Could not create hard link '{path}' to '{target}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }

            return;
        }

        if (CreateHardLinkUnix(target, path) != 0)
        {
            throw new IOException($"Could not create hard link '{path}' to '{target}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string fileName, string existingFileName, IntPtr securityAttributes);
}
