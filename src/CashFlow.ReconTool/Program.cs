using CashFlow.Messaging;

namespace CashFlow.ReconTool;

/// <summary>
/// recon-tool (F4, ADR-0002 item 7): execução sob demanda, sem servidor. Configuração por variáveis de ambiente com a
/// convenção do .NET ("ConnectionStrings:Source" =&gt; "ConnectionStrings__Source"). Classe com namespace (não top-level)
/// para não colidir com os "Program" globais dos outros executáveis referenciados pelos testes.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        return await ReconCli.RunAsync(args, RabbitMqOptions.FromEnvironment, Console.Out, Console.Error, cancellation.Token);
    }
}
