using System.Diagnostics;

namespace NodeRegistryModule
{
   
    public class LanIpConfig
    {
        /// <summary>
        /// 设置指定网卡为静态 IP
        /// </summary>
        /// <param name="adapterName">网卡名称（如 "以太网"、"Wi-Fi"）</param>
        /// <param name="ipAddress">要设置的 IP 地址</param>
        /// <param name="subnetMask">子网掩码（通常是 255.255.255.0）</param>
        /// <param name="gateway">默认网关</param>
        /// <param name="dns1">首选 DNS</param>
        /// <param name="dns2">备用 DNS</param>
        public static async Task<bool> SetStaticIPAsync(
            string ipAddress,
            string adapterName = "以太网",
            string subnetMask = "255.255.255.0",
            string gateway = "",
            string dns1 = "8.8.8.8",
            string dns2 = "114.114.114.114")
        {
            try
            {
                Console.WriteLine($"正在设置网卡 [{adapterName}] 为静态 IP: {ipAddress}");

                var script = $@"
                $adapter = Get-NetAdapter -Name '{adapterName}' -ErrorAction Stop
                New-NetIPAddress -InterfaceIndex $adapter.ifIndex `
                    -IPAddress '{ipAddress}' `
                    -PrefixLength 24 `
                    -DefaultGateway '{gateway}' | Out-Null

                Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex `
                    -ServerAddresses @('{dns1}','{dns2}') | Out-Null

                Write-Host '静态 IP 设置成功' -ForegroundColor Green
            ";

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        Verb = "runas"   // 请求管理员权限
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    Console.WriteLine($"✓ 网卡 [{adapterName}] 已设置为静态 IP: {ipAddress}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"✗ 设置失败: {error}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"设置静态 IP 时发生异常: {ex.Message}");
                return false;
            }
        }
    }
}
