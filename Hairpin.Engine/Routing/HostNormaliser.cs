using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

namespace Hairpin.Engine.Routing;

/// <summary>Normalises and classifies hosts. Pure and thread-safe.</summary>
/// <remarks>
/// Rules, applied in order:
/// <list type="number">
/// <item>Reject empty hosts and anything with control characters, spaces or <c>% @ [ ] : / \ ? # *</c>. IPv6 isn't supported.</item>
/// <item>Strip one trailing dot, then convert international names with <see cref="IdnMapping.GetAscii(string)"/> and lowercase with the invariant culture.
/// Conversion comes first so full-width digits and dots can't slip past the numeric check.</item>
/// <item>If the last label is numeric (all decimal digits, or a <c>0x</c> prefix), the whole host must be a strict dotted quad: four decimal
/// octets 0 to 255 with no leading zeros. Anything else, such as <c>127.1</c>, <c>2130706433</c>, <c>0x7f.0.0.1</c> or <c>010.0.0.1</c>, is
/// rejected, because Windows' resolver would read it as an address the matcher didn't see. This follows the WHATWG URL "ends in a number" rule.</item>
/// <item>Each name label is 1 to 63 characters of letters, digits, hyphens and underscores, and doesn't start or end with a hyphen. The whole name is at most 253 characters.</item>
/// </list>
/// </remarks>
public static class HostNormaliser
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = false };

    /// <summary>Normalises <paramref name="host"/>, or returns false with a short reason when it isn't acceptable.</summary>
    public static bool TryNormalise(string host, [NotNullWhen(true)] out NormalisedHost? result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        if (string.IsNullOrEmpty(host))
        {
            error = "The host is empty.";
            return false;
        }

        if (host.Length > 254)
        {
            error = "The host is too long.";
            return false;
        }

        foreach (var c in host)
        {
            if (c is '[' or ']' or ':')
            {
                error = "IPv6 addresses aren't supported.";
                return false;
            }

            if (c <= ' ' || c == '\u007f' || c is '%' or '@' or '/' or '\\' or '?' or '#' or '*' || char.IsControl(c) || char.IsWhiteSpace(c))
            {
                error = "The host contains a character that isn't allowed.";
                return false;
            }
        }

        var trimmed = host.EndsWith('.') ? host[..^1] : host;
        if (trimmed.Length == 0 || trimmed.StartsWith('.') || trimmed.EndsWith('.') || trimmed.Contains("..", StringComparison.Ordinal))
        {
            error = "The host has an empty label.";
            return false;
        }

        string ascii;
        try
        {
            ascii = Idn.GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            error = "The host isn't a valid name.";
            return false;
        }

        if (ascii.Length > 253)
        {
            error = "The host is too long.";
            return false;
        }

        var labels = ascii.Split('.');
        if (IsNumericLabel(labels[^1]))
        {
            if (TryParseStrictIPv4(ascii, out var address))
            {
                result = new NormalisedHost(ascii, HostKind.IPv4, address);
                error = null;
                return true;
            }

            error = "A numeric host must be a plain dotted IPv4 address, such as 10.1.2.3.";
            return false;
        }

        foreach (var label in labels)
        {
            if (!IsValidLabel(label))
            {
                error = "The host isn't a valid name.";
                return false;
            }
        }

        result = new NormalisedHost(ascii, HostKind.Name, null);
        error = null;
        return true;
    }

    /// <summary>Parses a strict dotted quad: four decimal octets 0 to 255, no leading zeros, no other characters.</summary>
    internal static bool TryParseStrictIPv4(string value, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            var octet = 0;
            foreach (var c in part)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }

                octet = (octet * 10) + (c - '0');
            }

            if (octet > 255)
            {
                return false;
            }

            bytes[i] = (byte)octet;
        }

        address = new IPAddress(bytes);
        return true;
    }

    /// <summary>Converts an IPv4 address to a host-order integer, for range checks.</summary>
    internal static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out var written) || written != 4)
        {
            throw new ArgumentException("Not an IPv4 address.", nameof(address));
        }

        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static bool IsNumericLabel(string label)
    {
        if (label.Length == 0)
        {
            return false;
        }

        if (label.StartsWith("0x", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var c in label)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidLabel(string label)
    {
        if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
        {
            return false;
        }

        foreach (var c in label)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
