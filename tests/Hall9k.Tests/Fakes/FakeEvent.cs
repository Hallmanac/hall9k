using JasperFx.Events;

namespace Hall9k.Tests.Fakes;

/// <summary>Lightweight IEvent wrapper for unit testing Marten projections without a database.</summary>
public sealed class FakeEvent<T>(T data) : IEvent<T> where T : notnull
{
    public T Data { get; } = data;

    object IEvent.Data => Data;

    public Guid Id { get; set; } = Guid.NewGuid();
    public long Version { get; set; }
    public long Sequence { get; set; }
    public Type EventType => typeof(T);
    public string EventTypeName { get; set; } = typeof(T).Name;
    public string DotNetTypeName { get; set; } = typeof(T).AssemblyQualifiedName!;
    public Guid StreamId { get; set; } = Guid.NewGuid();
    public string? StreamKey { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string TenantId { get; set; } = string.Empty;
    public string? CausationId { get; set; } = string.Empty;
    public string? CorrelationId { get; set; } = string.Empty;
    public Dictionary<string, object>? Headers { get; set; }
    public bool IsArchived { get; set; }
    public string? AggregateTypeName { get; set; } = string.Empty;
    public string? UserName { get; set; } = string.Empty;
    public bool IsSkipped { get; set; }

    public void SetHeader(string key, object value)
    {
        Headers ??= new Dictionary<string, object>();
        Headers[key] = value;
    }

    public object? GetHeader(string key) => Headers?.GetValueOrDefault(key);

    public Func<object, object> CreateAggregateIdentitySource<TId>() => _ => default(TId)!;
}
