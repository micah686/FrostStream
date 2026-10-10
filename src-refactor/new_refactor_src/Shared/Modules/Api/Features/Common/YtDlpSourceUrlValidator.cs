using System.Net;

namespace WebAPI.Features.Common;

internal static class YtDlpSourceUrlValidator
{
    public static bool TryValidate(string? sourceUrl, out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            errorMessage = "Source URL is required.";
            return false;
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            errorMessage = "Source URL must be an absolute URL.";
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            errorMessage = "Source URL must use http or https.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errorMessage = "Source URL must not include credentials.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            errorMessage = "Source URL must include a host.";
            return false;
        }

        if (IsBlockedHost(uri.Host))
        {
            errorMessage = "Source URL host is not allowed.";
            return false;
        }

        errorMessage = "";
        return true;
    }

    private static bool IsBlockedHost(string host)
    {
        var normalized = host.Trim().TrimEnd('.');

        if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(normalized, out var address))
            return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => IsBlockedIPv4(bytes),
            System.Net.Sockets.AddressFamily.InterNetworkV6 => IsBlockedIPv6(bytes),
            _ => true
        };
    }

    private static bool IsBlockedIPv4(byte[] bytes)
    {
        if (bytes.Length != 4)
            return true;

        return bytes[0] == 0
               || bytes[0] == 10
               || bytes[0] == 127
               || bytes[0] == 169 && bytes[1] == 254
               || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
               || bytes[0] == 192 && bytes[1] == 168
               || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
               || bytes[0] >= 224
               || bytes is [255, 255, 255, 255];
    }

    private static bool IsBlockedIPv6(byte[] bytes)
    {
        if (bytes.Length != 16)
            return true;

        return IsAllZero(bytes)
               || bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80
               || (bytes[0] & 0xfe) == 0xfc
               || bytes[0] == 0xff;
    }

    private static bool IsAllZero(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
                return false;
        }

        return true;
    }
}
