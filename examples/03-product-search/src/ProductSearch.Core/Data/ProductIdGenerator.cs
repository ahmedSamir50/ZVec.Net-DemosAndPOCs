using System.Security.Cryptography;
using System.Text;

namespace ProductSearch.Core.Data;

public static class ProductIdGenerator
{
    /// <summary>Fixed demo namespace for deterministic UUID v5 ids.</summary>
    public static readonly Guid Namespace = Guid.Parse("a3f2c8d1-5e4b-4f9a-9c2d-1b7e6f0a8d3c");

    public static Guid FromCatalogId(string catalogId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        return CreateVersion5(Namespace, $"myntra:{catalogId.Trim()}");
    }

    public static string StringFromCatalogId(string catalogId)
        => FromCatalogId(catalogId).ToString();

    private static Guid CreateVersion5(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        SwapByteOrder(namespaceBytes);

        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var data = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(data, 0);
        nameBytes.CopyTo(data, namespaceBytes.Length);

        var hash = SHA1.HashData(data);
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static void SwapByteOrder(byte[] guid)
    {
        static void Swap(byte[] g, int a, int b)
        {
            (g[a], g[b]) = (g[b], g[a]);
        }

        Swap(guid, 0, 3);
        Swap(guid, 1, 2);
        Swap(guid, 4, 5);
        Swap(guid, 6, 7);
    }
}
