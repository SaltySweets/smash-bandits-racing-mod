using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Start Smash Bandits Racing")]
[assembly: System.Reflection.AssemblyDescription("Launches the registered local game copy.")]

[ComImport]
[Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments,
        uint options,
        out uint processId);
}

[ComImport]
[Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal class ApplicationActivationManager
{
}

internal static class Launcher
{
    private const string PackageFullName = "Hutch.SmashBanditsRacing_1.1.1.10_x86__zm2j5gar6w84m";
    private const string AppUserModelId = "Hutch.SmashBanditsRacing_zm2j5gar6w84m!App";
    private const int InsufficientBuffer = 122;
    private const uint SuppressWindowsErrorDialog = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(
        string packageFullName, ref uint pathLength, StringBuilder path);

    [STAThread]
    private static int Main()
    {
        string gamePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Editable Game");
        if (!MatchesRegisteredPath(gamePath) || !File.Exists(Path.Combine(gamePath, "Template.exe")))
        {
            return ShowError("当前目录需要重新注册游戏副本。请将启动器和 Editable Game 文件夹放在注册时的目录。");
        }

        try
        {
            var activation = (IApplicationActivationManager)new ApplicationActivationManager();
            uint processId;
            int result = activation.ActivateApplication(
                AppUserModelId, null, SuppressWindowsErrorDialog, out processId);
            if (result < 0)
            {
                return ShowError("Windows 无法启动本地游戏。错误代码 0x" + result.ToString("X8"));
            }

            Console.WriteLine(processId);
            return 0;
        }
        catch (COMException error)
        {
            return ShowError("Windows 无法启动本地游戏。错误代码 0x" + error.ErrorCode.ToString("X8"));
        }
    }

    private static bool MatchesRegisteredPath(string gamePath)
    {
        uint length = 0;
        if (GetPackagePathByFullName(PackageFullName, ref length, null) != InsufficientBuffer)
        {
            return false;
        }

        var registeredPath = new StringBuilder((int)length);
        if (GetPackagePathByFullName(PackageFullName, ref length, registeredPath) != 0)
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(gamePath).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(registeredPath.ToString()).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static int ShowError(string message)
    {
        MessageBox.Show(message, "Smash Bandits Racing", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
    }
}
