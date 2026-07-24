using System.Diagnostics;

public class WatchDog
{
    private readonly string _coreExePath;
    private Process? _coreProcess;
    private bool _isWatchDogPaused = false;
    private readonly CancellationTokenSource _cts = new();

    public WatchDog(string internalDir, string exeName = "MyAPICore.exe")
    {
        _coreExePath = Path.Combine(internalDir, exeName);
    }

    // ====================== 公开方法 ======================

    public void Pause()
    {
        _isWatchDogPaused = true;
        Console.WriteLine("[看门狗] 已暂停自动重启（进入更新模式）");
    }

    public void Resume()
    {
        _isWatchDogPaused = false;
        Console.WriteLine("[看门狗] 已恢复自动重启");
    }

    public void Start(Process? initialProcess = null)
    {
        // 优先级1：外部传入的进程（Main中首次启动时传入）
        if (initialProcess != null)
        {
            _coreProcess = initialProcess;
            Console.WriteLine($"[看门狗] 已关联外部传入的主程序进程 (PID: {initialProcess.Id})");
        }
        // 优先级2：系统中已经存在的同名进程（优先接管）
        else
        {
            var existingProcesses = Process.GetProcessesByName("MyAPICore");
            if (existingProcesses.Length > 0)
            {
                // 优先取第一个（通常是最早启动的）
                _coreProcess = existingProcesses[0];
                Console.WriteLine($"[看门狗] 已接管系统中已存在的 MyAPICore 进程 (PID: {_coreProcess.Id})");
            }
            else
            {
                Console.WriteLine("[看门狗] 未找到已存在的 MyAPICore 进程，将在首次循环中启动");
            }
        }

        // 启动监控循环
        Task.Run(() => WatchDogLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    // ====================== 内部循环（保持你原来的逻辑） ======================
    private async Task WatchDogLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_isWatchDogPaused)
                {
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }

                if (_coreProcess == null || _coreProcess.HasExited)
                {
                    Console.WriteLine("[看门狗] 主程序已退出，准备自动重启...");
                    await StartCoreProcessAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[看门狗] 检查异常: {ex.Message}");
            }

            await Task.Delay(3000, cancellationToken);
        }
    }
    private async Task StartCoreProcessAsync()
    {
        if (!File.Exists(_coreExePath))
        {
            Console.WriteLine($"错误：未找到主程序 {_coreExePath}");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _coreExePath,
            WorkingDirectory = Path.GetDirectoryName(_coreExePath)!,
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        _coreProcess = Process.Start(startInfo);
        Console.WriteLine($"[看门狗] 主程序已启动 (PID: {_coreProcess?.Id})");
    }
    // ====================== 供 Update 模块使用的两个方法 ======================
    public async Task CloseCurrentCoreAsync()
    {
        Pause();

        var processes = Process.GetProcessesByName("MyAPICore");
        foreach (var proc in processes)
        {
            try
            {
                if (!proc.HasExited)
                {
                    Console.WriteLine($"正在终止旧进程 (PID: {proc.Id})...");
                    proc.Kill();
                    await proc.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"终止进程异常: {ex.Message}");
            }
        }

        await Task.Delay(1500);
    }

    public async Task StartNewCoreAsync()
    {
        await StartCoreProcessAsync();
        await Task.Delay(2000);
        Resume();
    }
}
