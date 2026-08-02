using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SE2SW.Worker;

internal sealed class CadProcessOwnership
{
    private readonly string _processName;
    private readonly HashSet<int> _before;

    private CadProcessOwnership(string processName)
    {
        _processName = processName;
        _before = GetProcessIds(processName);
    }

    public int OwnedProcessId { get; private set; }
    public bool OwnsInstance => OwnedProcessId > 0;

    public static CadProcessOwnership Capture(string processName) => new(processName);

    public void Resolve(long windowHandle)
    {
        var after = GetProcessIds(_processName);
        var newProcessIds = after.Except(_before).ToArray();
        if (windowHandle != 0)
        {
            _ = GetWindowThreadProcessId(new IntPtr(windowHandle), out var windowProcessId);
            if (windowProcessId > 0 && !_before.Contains((int)windowProcessId) && after.Contains((int)windowProcessId))
            {
                OwnedProcessId = (int)windowProcessId;
                return;
            }
        }

        if (newProcessIds.Length == 1)
            OwnedProcessId = newProcessIds[0];
    }

    public bool WaitForOwnedExit(TimeSpan timeout)
    {
        if (!OwnsInstance)
            return true;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using var process = Process.GetProcessById(OwnedProcessId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            Thread.Sleep(200);
        }
        return false;
    }

    private static HashSet<int> GetProcessIds(string processName)
    {
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
                result.Add(process.Id);
        }
        return result;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
