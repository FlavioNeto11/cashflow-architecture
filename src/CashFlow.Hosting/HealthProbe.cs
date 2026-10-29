namespace CashFlow.Hosting;

/// <summary>
/// Modo "--healthcheck URL": faz um GET e sai com 0 (2xx) ou 1. Existe porque as imagens aspnet/runtime
/// não trazem curl nem wget; o HEALTHCHECK do Compose chama o próprio binário da aplicação.
/// </summary>
public static class HealthProbe
{
    public const string Switch = "--healthcheck";

    public static bool IsRequested(string[] args) => args.Contains(Switch, StringComparer.Ordinal);

    public static async Task<int> RunAsync(string[] args)
    {
        var index = Array.IndexOf(args, Switch);
        if (index < 0 || index + 1 >= args.Length || !Uri.TryCreate(args[index + 1], UriKind.Absolute, out var url))
        {
            await Console.Error.WriteLineAsync("uso: --healthcheck <url>");
            return 1;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
