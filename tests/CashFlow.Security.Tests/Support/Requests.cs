using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CashFlow.Security.Tests.Support;

/// <summary>Requisições com Bearer e Idempotency-Key, sem nenhum cabeçalho X-Dev-* (salvo quando o teste quer prová-los ignorados).</summary>
public static class Requests
{
    public static string TransactionsUrl(Guid merchantId) => $"/api/v1/merchants/{merchantId}/transactions";

    public static string DailyUrl(Guid merchantId, string date) => $"/api/v1/merchants/{merchantId}/consolidations/daily?date={date}";

    public static string CreditJson(string amount = "10.00") => $$"""{"type":"CREDIT","amount":"{{amount}}"}""";

    public static Task<HttpResponseMessage> PostTransactionAsync(HttpClient client, Guid merchantId, string? token, string? json = null, string? key = null) =>
        SendAsync(client, HttpMethod.Post, TransactionsUrl(merchantId), token, json ?? CreditJson(), key ?? Guid.NewGuid().ToString());

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string url, string? token) =>
        SendAsync(client, HttpMethod.Get, url, token, json: null, key: null);

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string? token, string? json, string? key, Action<HttpRequestMessage>? configure = null)
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

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        configure?.Invoke(request);
        return await client.SendAsync(request);
    }

    public static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    public static async Task AssertProblemAsync(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(response);
        Assert.Equal(status, (int?)body["status"]);
    }

    /// <summary>
    /// Cabeçalhos de segurança da borda (security.md 5) e nenhum "Server": vale para TODA resposta do gateway, inclusive as
    /// que nascem antes do roteamento (404 do isolamento de listener, 413/411 da higiene, 429 do rate limit) e as que vêm
    /// da API ou do próprio YARP (502). Auditoria F5, P1-2/P2-1/P2-2.
    /// </summary>
    public static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        var label = $"resposta {(int)response.StatusCode} {response.RequestMessage?.RequestUri?.PathAndQuery}";
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var nosniff) && nosniff.Single() == "nosniff", $"X-Content-Type-Options ausente na {label}");
        Assert.True(response.Headers.TryGetValues("Referrer-Policy", out var referrer) && referrer.Single() == "no-referrer", $"Referrer-Policy ausente na {label}");
        Assert.True(response.Headers.CacheControl?.NoStore, $"Cache-Control: no-store ausente na {label}");
        Assert.False(response.Headers.Contains("Server"), $"Server presente na {label}");
    }
}

/// <summary>Leitura e forja de JWTs para os negativos de T-18 (nunca para produzir tokens válidos).</summary>
public static class Jwt
{
    public static JsonObject Payload(string token) => Decode(token.Split('.')[1]);

    public static JsonObject Header(string token) => Decode(token.Split('.')[0]);

    /// <summary>alg=none com o payload de um token válido e assinatura vazia.</summary>
    public static string WithAlgNone(string token)
    {
        var header = Header(token);
        header["alg"] = "none";
        return $"{Encode(header)}.{token.Split('.')[1]}.";
    }

    /// <summary>HS256 com segredo arbitrário, mantendo kid e payload (ataque de confusão de algoritmo).</summary>
    public static string WithHs256(string token)
    {
        var header = Header(token);
        header["alg"] = "HS256";
        var signingInput = $"{Encode(header)}.{token.Split('.')[1]}";
        var signature = HMACSHA256.HashData(RandomNumberGenerator.GetBytes(32), Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>Payload alterado (merchant_id de outro tenant) com a assinatura original.</summary>
    public static string WithTamperedMerchant(string token, Guid merchantId)
    {
        var parts = token.Split('.');
        var payload = Payload(token);
        payload["merchant_id"] = merchantId.ToString();
        return $"{parts[0]}.{Encode(payload)}.{parts[2]}";
    }

    private static JsonObject Decode(string segment) =>
        (JsonObject)JsonNode.Parse(Base64UrlDecode(segment))!;

    private static string Encode(JsonObject node) =>
        Base64Url(Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = false })));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
