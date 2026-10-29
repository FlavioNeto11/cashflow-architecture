namespace CashFlow.Messaging;

/// <summary>
/// Retenção do Inbox (ADR-0002 item 8, HIPÓTESE): 30 d, expurgo em 31 d por processed_at. Os três limiares que dependem
/// do Inbox completo usam a MESMA guarda conservadora de 29 d: obsoleta no worker (x-reason=stale), recusa do redrive
/// e guarda de retenção do reconcile. Quem mudar um muda todos.
/// </summary>
public static class InboxRetention
{
    public const int GuardDays = 29;
}
