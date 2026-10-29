namespace CashFlow.Consolidation.Tests.Support;

/// <summary>Espera ativa com prazo: falha com a descrição da condição se ela não ocorrer a tempo.</summary>
public static class Eventually
{
    public static async Task<T> WaitAsync<T>(Func<Task<T>> probe, Func<T, bool> done, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        T last;
        do
        {
            last = await probe();
            if (done(last))
            {
                return last;
            }

            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail($"Tempo esgotado ({timeout.TotalSeconds:0} s) esperando: {description}. Último valor: {last}");
        return last;
    }
}
