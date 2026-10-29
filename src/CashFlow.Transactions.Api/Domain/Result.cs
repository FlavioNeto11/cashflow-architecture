namespace CashFlow.Transactions.Api.Domain;

/// <summary>Erros de validação por campo (vira 422 com "errors" no problem+json).</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool Any => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }

        list.Add(message);
    }

    public void AddRange(IReadOnlyDictionary<string, string[]> other)
    {
        foreach (var (field, messages) in other)
        {
            foreach (var message in messages)
            {
                Add(field, message);
            }
        }
    }

    public Dictionary<string, string[]> ToDictionary() => _errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
}

/// <summary>Resultado de uma operação de domínio: valor ou erros de validação (sem exceções para fluxo).</summary>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T? value, Dictionary<string, string[]>? errors)
    {
        _value = value;
        Errors = errors;
    }

    public bool IsSuccess => Errors is null;

    public Dictionary<string, string[]>? Errors { get; }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Resultado com erros de validação.");

    public static Result<T> Ok(T value) => new(value, null);

    public static Result<T> Fail(ValidationErrors errors) => new(default, errors.ToDictionary());

    public static Result<T> Fail(string field, string message)
    {
        var errors = new ValidationErrors();
        errors.Add(field, message);
        return Fail(errors);
    }
}
