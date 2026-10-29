using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CashFlow.Transactions.Api.Tests.Support;

/// <summary>Identidade de desenvolvimento enviada por cabeçalhos X-Dev-* (esquema DevelopmentHeaders).</summary>
public sealed record Identity(string? Sub, string? ClientId, Guid? MerchantId, string? Scope)
{
    public const string ReadWrite = "transactions.write transactions.read";

    public static Identity For(Guid merchantId, string scope = ReadWrite) => new("user-test", "client-test", merchantId, scope);
}

public static class ApiClient
{
    public static string TransactionsUrl(Guid merchantId) => $"/api/v1/merchants/{merchantId}/transactions";

    public static Task<HttpResponseMessage> PostTransactionAsync(
        HttpClient client, Guid merchantId, string? json, string? key, Identity? identity = null) =>
        SendAsync(client, HttpMethod.Post, TransactionsUrl(merchantId), json, key, identity ?? Identity.For(merchantId));

    public static Task<HttpResponseMessage> PostReversalAsync(
        HttpClient client, Guid merchantId, Guid id, string? json, string? key, Identity? identity = null) =>
        SendAsync(client, HttpMethod.Post, $"{TransactionsUrl(merchantId)}/{id}/reversal", json, key, identity ?? Identity.For(merchantId));

    /// <summary>POST com o corpo em bytes crus (permite enviar UTF-8 inválido, que StringContent não produz).</summary>
    public static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string url, byte[] body, string key, Identity identity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        AddIfNotNull(request, "X-Dev-Sub", identity.Sub);
        AddIfNotNull(request, "X-Dev-Client-Id", identity.ClientId);
        AddIfNotNull(request, "X-Dev-Merchant-Id", identity.MerchantId?.ToString());
        AddIfNotNull(request, "X-Dev-Scope", identity.Scope);
        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string url, Identity? identity) =>
        SendAsync(client, HttpMethod.Get, url, null, null, identity);

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string? json, string? key, Identity? identity)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        if (identity is not null)
        {
            AddIfNotNull(request, "X-Dev-Sub", identity.Sub);
            AddIfNotNull(request, "X-Dev-Client-Id", identity.ClientId);
            AddIfNotNull(request, "X-Dev-Merchant-Id", identity.MerchantId?.ToString());
            AddIfNotNull(request, "X-Dev-Scope", identity.Scope);
        }

        return await client.SendAsync(request);
    }

    public static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    public static string CreateJson(string type, string amount, string? extra = null) =>
        $$"""{"type":"{{type}}","amount":"{{amount}}"{{(extra is null ? "" : "," + extra)}}}""";

    public static bool SameJson(string a, string b) => JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));

    public static string NewKey() => Guid.NewGuid().ToString();

    private static void AddIfNotNull(HttpRequestMessage request, string name, string? value)
    {
        if (value is not null)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
