using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoMailChecker.Windows.Pause;

/// <summary>動いているプロセス1つ。Path は権限がなくて調べられなければ null。</summary>
public sealed record ProcessInfo(int Pid, string ExeName, string? Path);

/// <summary>表示中のウィンドウ1つ。</summary>
public sealed record WindowInfo(int Pid, string Title);

/// <summary>停止対象の検知に使う、Windows の現在の状態。偽物に差し替えてテストする。</summary>
public interface ISystemSnapshotSource
{
    IReadOnlyList<ProcessInfo> GetProcesses();

    IReadOnlyList<WindowInfo> GetWindows();

    /// <summary>全画面アプリの使用中、または Direct3D の排他全画面か。</summary>
    bool IsFullscreen();
}

/// <summary>
/// ISystemSnapshotSource の Windows API 実装（外部仕様タブ「Windows API」）。
/// 実行ファイルの場所は新しく見つけたプロセスだけを調べ、プロセスごとに覚えておく（検知の負荷を小さくするため）。
/// </summary>
public sealed class WindowsSystemSnapshot : ISystemSnapshotSource
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int QunsBusy = 2;
    private const int QunsRunningD3DFullScreen = 3;

    private readonly object _gate = new();
    private readonly Dictionary<int, (string Name, string? Path)> _paths = [];

    public IReadOnlyList<ProcessInfo> GetProcesses()
    {
        var processes = Process.GetProcesses();
        try
        {
            lock (_gate)
            {
                var alive = new HashSet<int>();
                var result = new List<ProcessInfo>(processes.Length);
                foreach (var process in processes)
                {
                    int pid;
                    string name;
                    try
                    {
                        pid = process.Id;
                        name = process.ProcessName;
                    }
                    catch (InvalidOperationException)
                    {
                        continue; // 調べている間に終わった
                    }

                    alive.Add(pid);
                    // 同じ PID でも名前が変わっていたら別のプロセス（PID の再利用）なので調べ直す
                    if (!_paths.TryGetValue(pid, out var known) || known.Name != name)
                    {
                        known = (name, QueryImagePath(pid));
                        _paths[pid] = known;
                    }

                    var exeName = known.Path is { } path ? System.IO.Path.GetFileName(path) : name + ".exe";
                    result.Add(new ProcessInfo(pid, exeName, known.Path));
                }

                foreach (var gone in _paths.Keys.Where(pid => !alive.Contains(pid)).ToList())
                {
                    _paths.Remove(gone);
                }

                return result;
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        var windows = new List<WindowInfo>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle))
            {
                return true;
            }

            var length = NativeMethods.GetWindowTextLength(handle);
            if (length <= 0)
            {
                return true;
            }

            var buffer = new char[length + 1];
            var copied = NativeMethods.GetWindowText(handle, buffer, buffer.Length);
            if (copied > 0)
            {
                NativeMethods.GetWindowThreadProcessId(handle, out var pid);
                windows.Add(new WindowInfo((int)pid, new string(buffer, 0, copied)));
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public bool IsFullscreen() =>
        NativeMethods.SHQueryUserNotificationState(out var state) == 0
        && state is QunsBusy or QunsRunningD3DFullScreen;

    private static string? QueryImagePath(int pid)
    {
        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            return null; // システムのプロセスなど、権限がなくて調べられない
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] buffer, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr handle);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr handle, [Out] char[] text, int maxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int state);
    }
}
