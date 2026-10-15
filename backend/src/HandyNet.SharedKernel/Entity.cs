namespace HandyNet.SharedKernel;

/// <summary>Marker for events raised by aggregates. They are persisted to the outbox in the same transaction.</summary>
public interface IDomainEvent;

public abstract class Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public IReadOnlyList<IDomainEvent> PopDomainEvents()
    {
        var events = _domainEvents.ToList();
        _domainEvents.Clear();
        return events;
    }
}

/// <summary>Entities whose changes are written to the append-only audit log.</summary>
public interface IAuditable;
