using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace OneHistoryStudio.Connection;

/// <summary>同一产品 EXE 的受控提权模式；不接受命令文本或任意文件路径。</summary>
public static class LanMachineHelper
{
    private const string CertificateSubject = "CN=OneHistoryStudio LAN";
    private const string CertificateMarkerOid = "1.3.6.1.4.1.59072.273.1";
    private const string AppId = "{9B092BA7-A8ED-4A22-91F3-86EE0B746273}";

    public static int Run(
        string mode,
        string encodedPayload,
        string applicationRoot,
        string productVersion)
    {
        var expectedAction = mode.Equals("--lan-remove", StringComparison.OrdinalIgnoreCase)
            ? "remove"
            : "install";
        var payloads = new LanMachinePayloadStore(applicationRoot, productVersion);
        LanMachineOperation operation;
        try
        {
            operation = payloads.Decode(encodedPayload, expectedAction);
            if (!IsAdministrator())
                throw new InvalidOperationException("LAN helper 必须经 UAC 以管理员身份运行");
            payloads.ConsumeNonce(operation);
        }
        catch (Exception)
        {
            return 65;
        }

        LanMachineResult result;
        try
        {
            result = operation.Action == "remove"
                ? Remove(operation)
                : Install(operation);
        }
        catch (Exception ex)
        {
            result = new LanMachineResult(false, $"LAN 机器配置失败: {ex.Message}");
        }

        try
        {
            payloads.WriteResult(operation.Nonce, result);
            return result.Success ? 0 : 1;
        }
        catch
        {
            return 74;
        }
    }

    private static LanMachineResult Install(LanMachineOperation operation)
    {
        EnsurePrivateAdapter(operation.BindAddress);
        var certificate = operation.Action == "install"
                          && operation.PreviousBindAddress?.Equals(
                              operation.BindAddress, StringComparison.OrdinalIgnoreCase) == true
            ? FindOwnedCertificate(operation.PreviousCertificateStoreThumbprint)
              ?? CreateCertificate(operation.BindAddress)
            : CreateCertificate(operation.BindAddress);
        var createdCertificate = !certificate.Thumbprint.Equals(
            operation.PreviousCertificateStoreThumbprint, StringComparison.OrdinalIgnoreCase);
        var thumbprint = certificate.Thumbprint;
        var replacesOwnedEndpoint = operation.PreviousBindAddress?.Equals(
                                        operation.BindAddress,
                                        StringComparison.OrdinalIgnoreCase) == true
                                    && operation.PreviousPort == operation.Port;
        var endpointConfigured = false;
        try
        {
            ConfigureEndpoint(
                operation.BindAddress,
                operation.Port,
                thumbprint,
                replacesOwnedEndpoint);
            endpointConfigured = true;
            ConfigureFirewall(operation.Port);

            if (operation.PreviousBindAddress != null
                && operation.PreviousPort != null
                && (!operation.PreviousBindAddress.Equals(
                        operation.BindAddress, StringComparison.OrdinalIgnoreCase)
                    || operation.PreviousPort != operation.Port))
            {
                RemoveEndpoint(operation.PreviousBindAddress, operation.PreviousPort.Value);
                RemoveFirewall(operation.PreviousPort.Value);
            }

            RemoveOwnedCertificate(operation.PreviousCertificateStoreThumbprint, except: thumbprint);
            return new LanMachineResult(
                true,
                operation.Action == "rotate" ? "LAN 证书已轮换" : "LAN HTTPS 与 Private 防火墙规则已配置",
                Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)),
                certificate.NotAfter.ToUniversalTime(),
                "private-rule-installed",
                thumbprint);
        }
        catch
        {
            if (endpointConfigured || replacesOwnedEndpoint)
                RemoveEndpoint(operation.BindAddress, operation.Port);
            RemoveFirewall(operation.Port);
            if (createdCertificate)
                RemoveOwnedCertificate(thumbprint, except: null);
            RestorePrevious(operation);
            throw;
        }
        finally
        {
            certificate.Dispose();
        }
    }

    private static LanMachineResult Remove(LanMachineOperation operation)
    {
        if (operation.PreviousBindAddress == null || operation.PreviousPort == null)
        {
            return new LanMachineResult(
                true,
                "LAN 机器配置原本未启用，无需移除",
                FirewallStatus: "not-configured");
        }

        try
        {
            RemoveEndpoint(operation.PreviousBindAddress, operation.PreviousPort.Value);
            RemoveFirewall(operation.PreviousPort.Value);
            RemoveOwnedCertificate(operation.PreviousCertificateStoreThumbprint, except: null);
            return new LanMachineResult(
                true,
                "LAN HTTPS、URLACL 与本版本 Private 防火墙规则已移除",
                FirewallStatus: "not-configured");
        }
        catch
        {
            RestorePrevious(operation);
            throw;
        }
    }

    private static X509Certificate2 CreateCertificate(string bindAddress)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            CertificateSubject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        request.CertificateExtensions.Add(new X509Extension(
            new Oid(CertificateMarkerOid), [0x05, 0x00], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse(bindAddress));
        san.AddDnsName(Environment.MachineName);
        request.CertificateExtensions.Add(san.Build());

        using var temporary = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(2));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var persisted = new X509Certificate2(
            temporary.Export(X509ContentType.Pfx, password),
            password,
            X509KeyStorageFlags.MachineKeySet
            | X509KeyStorageFlags.PersistKeySet
            | X509KeyStorageFlags.Exportable);
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);
        return persisted;
    }

    private static void ConfigureEndpoint(
        string bind,
        int port,
        string thumbprint,
        bool replaceOwnedEndpoint)
    {
        var endpoint = $"{bind}:{port}";
        var url = $"https://{bind}:{port}/";
        var user = WindowsIdentity.GetCurrent().Name
                   ?? throw new InvalidOperationException("无法确定当前 Windows 用户");

        if (replaceOwnedEndpoint)
        {
            EnsureNetsh([
                "http", "update", "sslcert", $"ipport={endpoint}",
                $"certhash={thumbprint}", $"appid={AppId}", "certstorename=MY",
            ]);
            RunNetsh(["http", "delete", "urlacl", $"url={url}"], allowFailure: true);
        }
        else
        {
            // 新端点只允许 add；如已被其他应用占用则失败，绝不先删或覆盖外部绑定。
            var sslAdded = false;
            try
            {
                EnsureNetsh([
                    "http", "add", "sslcert", $"ipport={endpoint}",
                    $"certhash={thumbprint}", $"appid={AppId}", "certstorename=MY",
                ]);
                sslAdded = true;
                EnsureNetsh(["http", "add", "urlacl", $"url={url}", $"user={user}"]);
                return;
            }
            catch
            {
                if (sslAdded)
                {
                    RunNetsh([
                        "http", "delete", "sslcert", $"ipport={endpoint}",
                    ], allowFailure: true);
                }
                throw;
            }
        }

        EnsureNetsh(["http", "add", "urlacl", $"url={url}", $"user={user}"]);
    }

    private static void ConfigureFirewall(int port)
    {
        RunNetsh([
            "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule(port)}",
        ], allowFailure: true);
        EnsureNetsh([
            "advfirewall", "firewall", "add", "rule", $"name={FirewallRule(port)}",
            "dir=in", "action=allow", "protocol=TCP", $"localport={port}",
            "profile=private", "enable=yes", $"program={Environment.ProcessPath}",
        ]);
    }

    private static void RemoveEndpoint(string bind, int port)
    {
        RunNetsh([
            "http", "delete", "sslcert", $"ipport={bind}:{port}",
        ], allowFailure: true);
        RunNetsh([
            "http", "delete", "urlacl", $"url=https://{bind}:{port}/",
        ], allowFailure: true);
    }

    private static void RemoveFirewall(int port)
        => RunNetsh([
            "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule(port)}",
        ], allowFailure: true);

    private static void RestorePrevious(LanMachineOperation operation)
    {
        if (operation.PreviousBindAddress == null
            || operation.PreviousPort == null
            || string.IsNullOrWhiteSpace(operation.PreviousCertificateStoreThumbprint))
            return;
        try
        {
            ConfigureEndpoint(
                operation.PreviousBindAddress,
                operation.PreviousPort.Value,
                operation.PreviousCertificateStoreThumbprint,
                replaceOwnedEndpoint: true);
            ConfigureFirewall(operation.PreviousPort.Value);
        }
        catch
        {
            // 原异常仍由调用方返回；人工验收会再次核对机器状态。
        }
    }

    private static void RemoveOwnedCertificate(string? thumbprint, string? except)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)
            || thumbprint.Equals(except, StringComparison.OrdinalIgnoreCase))
            return;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in store.Certificates.Find(
                     X509FindType.FindByThumbprint, thumbprint, validOnly: false))
        {
            using (certificate)
            {
                if (certificate.Subject.Equals(CertificateSubject, StringComparison.OrdinalIgnoreCase)
                    && certificate.Extensions.Cast<X509Extension>()
                        .Any(extension => extension.Oid?.Value == CertificateMarkerOid))
                    store.Remove(certificate);
            }
        }
    }

    private static X509Certificate2? FindOwnedCertificate(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return null;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        foreach (var certificate in store.Certificates.Find(
                     X509FindType.FindByThumbprint, thumbprint, validOnly: false))
        {
            using (certificate)
            {
                if (certificate.Subject.Equals(CertificateSubject, StringComparison.OrdinalIgnoreCase)
                    && certificate.Extensions.Cast<X509Extension>()
                        .Any(extension => extension.Oid?.Value == CertificateMarkerOid)
                    && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30))
                    return new X509Certificate2(certificate);
            }
        }
        return null;
    }

    private static void EnsurePrivateAdapter(string bindAddress)
    {
        var expected = IPAddress.Parse(bindAddress);
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(item => item.OperationalStatus == OperationalStatus.Up
                                    && item.GetIPProperties().UnicastAddresses
                                        .Any(address => address.Address.Equals(expected)));
        if (adapter == null)
            throw new InvalidOperationException("选择的 Private IPv4 不属于当前活动网卡");

        var category = ReadNetworkCategory(adapter.GetIPProperties().GetIPv4Properties()?.Index);
        if (!category.Equals("Private", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"所选网卡当前网络类别为 {category}；仅允许 Private，程序不会自动修改网络类别");
    }

    private static string ReadNetworkCategory(int? interfaceIndex)
    {
        if (interfaceIndex == null)
            return "Unknown";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(
            $"(Get-NetConnectionProfile -InterfaceIndex {interfaceIndex.Value} -ErrorAction Stop).NetworkCategory");
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("无法查询 Windows 网络类别");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("查询 Windows 网络类别失败: " + error.Trim());
        return output.Trim();
    }

    private static NetshResult RunNetsh(IReadOnlyList<string> arguments, bool allowFailure)
    {
        var start = new ProcessStartInfo("netsh.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("无法启动 netsh.exe");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var result = new NetshResult(process.ExitCode == 0, (output + "\n" + error).Trim());
        if (!allowFailure && !result.Success)
            throw new InvalidOperationException("netsh 执行失败: " + result.Output);
        return result;
    }

    private static void EnsureNetsh(IReadOnlyList<string> arguments)
        => _ = RunNetsh(arguments, allowFailure: false);

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string FirewallRule(int port) => $"OneHistoryStudio LAN {port}";

    private sealed record NetshResult(bool Success, string Output);
}
