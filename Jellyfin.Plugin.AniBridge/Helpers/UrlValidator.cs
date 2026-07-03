using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.AniBridge.Services;

namespace Jellyfin.Plugin.AniBridge.Helpers;

/// <summary>
/// Validates URLs to prevent SSRF attacks by ensuring requests only go to hosts a registered
/// <see cref="StreamingSiteService"/> declares via <see cref="StreamingSiteService.AllowedHosts"/>,
/// and are never local/private-network addresses. Adding a new site adapter automatically
/// extends the allowlist — nothing to update here.
/// </summary>
public static class UrlValidator
{
    /// <summary>
    /// Validates that a URL belongs to a host one of the registered site services allows,
    /// and is not a local/private network address.
    /// </summary>
    public static bool IsValidUrl(string url, IEnumerable<StreamingSiteService> services)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();

        if (IsLocalOrPrivateHost(host))
        {
            return false;
        }

        foreach (var service in services)
        {
            if (!service.AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var isHttps = uri.Scheme == Uri.UriSchemeHttps;
            var isAllowedHttp = uri.Scheme == Uri.UriSchemeHttp && service.AllowInsecureHttp;

            if (isHttps || isAllowedHttp)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates a URL and throws if invalid.
    /// </summary>
    public static void EnsureValidUrl(string url, IEnumerable<StreamingSiteService> services, string paramName = "url")
    {
        if (!IsValidUrl(url, services))
        {
            throw new ArgumentException("Invalid URL. Only pages from enabled streaming sites are accepted.", paramName);
        }
    }

    /// <summary>
    /// Detects the source site identifier from a URL by matching its host against each
    /// registered service's <see cref="StreamingSiteService.AllowedHosts"/>. Falls back to
    /// the first registered service if no host matches.
    /// </summary>
    public static string DetectSource(string url, IEnumerable<StreamingSiteService> services)
    {
        var serviceList = services as IList<StreamingSiteService> ?? services.ToList();

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            var match = serviceList.FirstOrDefault(s => s.AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase));
            if (match != null)
            {
                return match.SourceName;
            }
        }

        return serviceList.FirstOrDefault()?.SourceName ?? string.Empty;
    }

    private static bool IsLocalOrPrivateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("[::1]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".example", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPrivateIpv4(address),
            AddressFamily.InterNetworkV6 => address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6Teredo,
            _ => false,
        };
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 => bytes[1] >= 16 && bytes[1] < 32,
            192 => bytes[1] == 168,
            100 => bytes[1] >= 64 && bytes[1] <= 127,
            _ => false,
        };
    }
}
