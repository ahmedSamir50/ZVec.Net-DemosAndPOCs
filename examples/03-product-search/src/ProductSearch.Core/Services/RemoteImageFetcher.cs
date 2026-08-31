using System.Net;
using System.Net.Sockets;

namespace ProductSearch.Core.Services;

public interface IRemoteImageFetcher
{
    Task<Stream> FetchImageAsync(string url, CancellationToken ct = default);
}

/// <summary>SSRF-safe HTTP(S) image fetch for Lens paste-URL search.</summary>
public sealed class RemoteImageFetcher : IRemoteImageFetcher
{
    private const int MaxBytes = 8 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;

    public RemoteImageFetcher(IHttpClientFactory httpClientFactory)
        => _httpClientFactory = httpClientFactory;

    public async Task<Stream> FetchImageAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Image URL must be an absolute http or https address.");
        }

        var host = uri.DnsSafeHost;
        if (IsBlockedHost(host))
            throw new InvalidOperationException("That host is not allowed for image fetch.");

        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(IsPrivateOrLocalAddress))
            throw new InvalidOperationException("URL resolves to a private or local address.");

        var client = _httpClientFactory.CreateClient("remote-image");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"URL did not return an image (content-type: {contentType ?? "unknown"}).");
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        if (buffer.Length > MaxBytes)
            throw new InvalidOperationException("Image exceeds the 8 MB size limit.");

        buffer.Position = 0;
        return buffer;
    }

    private static bool IsBlockedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var ip) && IsPrivateOrLocalAddress(ip);
    }

    private static bool IsPrivateOrLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || bytes[0] == 127
                   || bytes[0] == 0;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
                return true;

            var bytes = address.GetAddressBytes();
            return bytes[0] == 0xFC || bytes[0] == 0xFD;
        }

        return false;
    }
}
