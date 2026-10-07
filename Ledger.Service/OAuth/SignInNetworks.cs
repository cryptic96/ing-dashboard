using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Ledger.Service.OAuth;

/// <summary>
/// Network ranges in CIDR notation and the membership test the sign-in pages rely on. Both ends of a range belong to it, and an
/// IPv4 address that arrives wrapped in an IPv6 address is treated as the IPv4 address it is.
/// </summary>
public static class SignInNetworks
{
    /// <summary>The IPv4 range Anthropic's connectors call from; it must never be allowed to sign in.</summary>
    public const string AnthropicIpv4Range = "160.79.104.0/21";

    /// <summary>The IPv6 range Anthropic's connectors call from; it must never be allowed to sign in.</summary>
    public const string AnthropicIpv6Range = "2607:6bc0::/48";

    /// <summary>A parsed range: its first address with the host bits cleared, and the number of leading bits that are fixed.</summary>
    /// <param name="Address">The first address of the range.</param>
    /// <param name="PrefixLength">The number of fixed leading bits.</param>
    public readonly record struct Network(IPAddress Address, int PrefixLength);

    /// <summary>Parses a range such as 192.0.2.0/24 or 2001:db8::/32. A bare address is a range of one address.</summary>
    /// <param name="range">The text to parse.</param>
    /// <param name="network">The parsed range when parsing succeeded.</param>
    public static bool TryParse(string? range, out Network network)
    {
        network = default;

        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        var text = range.Trim();
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? text : text[..slash];

        if (!IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }

        var wasMapped = address.IsIPv4MappedToIPv6;
        address = Normalise(address);
        var maxBits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxBits;

        if (slash >= 0)
        {
            if (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix))
            {
                return false;
            }

            if (wasMapped)
            {
                prefix -= 96;
            }
        }

        if (prefix < 0 || prefix > maxBits)
        {
            return false;
        }

        network = new Network(Mask(address, prefix), prefix);

        return true;
    }

    /// <summary>Returns whether the address lies inside the range, both ends included.</summary>
    public static bool Contains(Network network, IPAddress address)
    {
        var candidate = Normalise(address);

        return candidate.AddressFamily == network.Address.AddressFamily
            && Mask(candidate, network.PrefixLength).Equals(network.Address);
    }

    /// <summary>Returns whether the address lies inside at least one of the ranges. A range that cannot be parsed admits nobody.</summary>
    public static bool Contains(IReadOnlyList<string> ranges, IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        foreach (var range in ranges)
        {
            if (TryParse(range, out var network) && Contains(network, address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether two ranges share at least one address.</summary>
    public static bool Overlaps(Network first, Network second)
    {
        return Contains(first, second.Address) || Contains(second, first.Address);
    }

    private static IPAddress Normalise(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static IPAddress Mask(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();

        for (var index = 0; index < bytes.Length; index++)
        {
            var bitsInByte = Math.Clamp(prefixLength - (index * 8), 0, 8);
            var mask = bitsInByte == 0 ? 0 : 0xFF << (8 - bitsInByte);
            bytes[index] &= (byte)mask;
        }

        return new IPAddress(bytes);
    }
}
