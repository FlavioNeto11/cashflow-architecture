using System.Text;
using CashFlow.Transactions.Api.Http;

namespace CashFlow.Transactions.Api.Tests.Domain;

public sealed class RequestHasherTests
{
    private const string Route = "/api/v1/merchants/00000000-0000-0000-0000-000000000001/transactions";

    private static string Hash(string json, string method = "POST", string route = Route) =>
        RequestHasher.Compute(method, route, Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Hash_IsSha256Hex64Chars()
    {
        var hash = Hash("""{"type":"CREDIT","amount":"100.00"}""");

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Hash_IgnoresKeyOrderAndWhitespace()
    {
        var a = Hash("""{"type":"CREDIT","amount":"100.00","description":"x"}""");
        var b = Hash("""{ "description" : "x", "amount":"100.00",   "type":"CREDIT" }""");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Hash_IgnoresKeyOrderInNestedObjects()
    {
        Assert.Equal(Hash("""{"a":{"x":1,"y":2},"b":[{"q":1,"r":2}]}"""), Hash("""{"b":[{"r":2,"q":1}],"a":{"y":2,"x":1}}"""));
    }

    [Fact]
    public void Hash_ChangesWhenAnyValueChanges()
    {
        var original = Hash("""{"type":"CREDIT","amount":"100.00"}""");

        Assert.NotEqual(original, Hash("""{"type":"CREDIT","amount":"100.01"}"""));
        Assert.NotEqual(original, Hash("""{"type":"DEBIT","amount":"100.00"}"""));
        Assert.NotEqual(original, Hash("""{"type":"CREDIT","amount":"100.00","description":"x"}"""));
    }

    [Fact]
    public void Hash_ChangesWithRouteAndMethod()
    {
        const string body = """{"type":"CREDIT","amount":"100.00"}""";

        Assert.NotEqual(Hash(body), Hash(body, route: Route + "/x/reversal"));
        Assert.NotEqual(Hash(body), Hash(body, method: "PUT"));
    }

    [Fact]
    public void Hash_EmptyBodyEqualsEmptyObject() =>
        Assert.Equal(Hash("{}"), Hash("   "));

    [Fact]
    public void Canonicalize_DuplicateProperty_Throws() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => RequestHasher.Canonicalize("""{"a":1,"a":2}"""u8));
}
