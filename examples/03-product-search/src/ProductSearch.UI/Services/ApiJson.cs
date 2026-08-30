using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProductSearch.UI.Services;

/// <summary>Shared JSON options for API calls.</summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>Non-success HTTP response from the API.</summary>
public sealed class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ApiException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
