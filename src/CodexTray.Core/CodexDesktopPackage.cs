using System.Runtime.InteropServices;
using System.Text;

namespace CodexTray.Core;

internal static class CodexDesktopPackage
{
    // The ChatGPT desktop app with Codex keeps this package identity.
    // ChatGPT Classic uses a different family and does not bundle Codex.
    private const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    private const int InsufficientBuffer = 122;

    internal static IEnumerable<string> RuntimePaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        // Ask Windows for the current user's registered package locations.
        foreach (var name in PackageNames().OrderByDescending(PackageVersion))
        {
            uint pathLength = 0;
            if (GetPackagePathByFullName(name, ref pathLength, null) != InsufficientBuffer ||
                pathLength is 0 or > 32_768)
            {
                continue;
            }

            var path = new StringBuilder((int)pathLength);
            if (GetPackagePathByFullName(name, ref pathLength, path) == 0)
            {
                yield return Path.Combine(path.ToString(), "app", "resources", "codex.exe");
            }
        }
    }

    private static string[] PackageNames()
    {
        // Retry if a package update changes the required buffer sizes.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            uint count = 0;
            uint length = 0;
            if (GetPackagesByPackageFamily(PackageFamily, ref count, null, ref length, IntPtr.Zero) != InsufficientBuffer ||
                count is 0 or > 256 || length is 0 or > 65_536)
            {
                return [];
            }

            var names = new IntPtr[count];
            var buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            try
            {
                var result = GetPackagesByPackageFamily(PackageFamily, ref count, names, ref length, buffer);
                if (result == InsufficientBuffer)
                {
                    continue;
                }
                if (result != 0)
                {
                    return [];
                }

                return names.Take((int)count)
                    .Select(pointer => Marshal.PtrToStringUni(pointer))
                    .OfType<string>()
                    .Where(name => name.Split('_') is [_, _, _, "", _]) // Ignore resource packages.
                    .ToArray();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    private static Version PackageVersion(string name) =>
        Version.TryParse(name.Split('_')[1], out var version) ? version : new Version();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count,
        [Out] IntPtr[]? names, ref uint bufferLength, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint pathLength,
        [Out] StringBuilder? path);
}
