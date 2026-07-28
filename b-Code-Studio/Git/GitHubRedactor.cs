using System.Text.RegularExpressions;

namespace OneHistoryStudio.Git;

public static partial class GitHubRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? "";
        var redacted = PrivateKeyBlock().Replace(value, "[REDACTED PRIVATE KEY]");
        redacted = Token().Replace(redacted, "[REDACTED TOKEN]");
        redacted = SecretAssignment().Replace(redacted, "$1=[REDACTED]");
        redacted = Bearer().Replace(redacted, "$1[REDACTED]");
        return UrlUserInfo().Replace(redacted, "$1[REDACTED]@");
    }

    public static string SanitizeRemoteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var trimmed = Redact(value.Trim());
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        var builder = new UriBuilder(uri)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = "",
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    [GeneratedRegex("-----BEGIN (?:OPENSSH |RSA |EC |DSA )?PRIVATE KEY-----[\\s\\S]*?-----END (?:OPENSSH |RSA |EC |DSA )?PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex("(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,})", RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    [GeneratedRegex("(?i)\\b(token|password|passwd|secret|oauth_token)\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s&;]+)")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex("(?i)(authorization\\s*:\\s*bearer\\s+)[^\\s]+")]
    private static partial Regex Bearer();

    [GeneratedRegex("(?i)(https?://)[^/@\\s]+@")]
    private static partial Regex UrlUserInfo();
}
