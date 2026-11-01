using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;

namespace Modularyn.Framework.Events;

/// <summary>The unit owns the effect transaction; the explicit principal is used at every further authorization check.</summary>
public sealed record EventExecution(EfCoreUnitOfWork Work, ExecutionPrincipal Principal, EventEnvelope Event);
public sealed record EventHandler(string Kind, int SchemaVersion, Func<EventExecution, CancellationToken, Task> Handle);

public sealed class EventConsumer
{
    private readonly IReadOnlyDictionary<(string Kind, int Version), EventHandler> handlers;
    public EventConsumer(string name, string permission, IEnumerable<EventHandler> handlers, EventOrdering ordering = EventOrdering.MonotonicSnapshot)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128) throw new ArgumentException("Name the consumer.", nameof(name));
        if (!PermissionNames.IsValid(permission)) throw new ArgumentException("Declare the consumer permission.", nameof(permission));
        if (!Enum.IsDefined(ordering)) throw new ArgumentOutOfRangeException(nameof(ordering));
        Name = name;
        Permission = permission;
        Ordering = ordering;
        var entries = handlers.ToArray();
        if (entries.Any(handler => string.IsNullOrWhiteSpace(handler.Kind) || handler.Kind.Length > 128
            || handler.SchemaVersion < 1 || handler.Handle is null))
            throw new ArgumentException("Every handler needs a bounded kind, positive version and implementation.", nameof(handlers));
        this.handlers = entries.ToDictionary(handler => (handler.Kind, handler.SchemaVersion));
    }
    public string Name { get; }
    public string Permission { get; }
    public EventOrdering Ordering { get; }
    internal EventHandler? Find(InboxEvent row) => handlers.GetValueOrDefault((row.Kind, row.SchemaVersion));
}

/// <summary>Optional audit, diagnostics and crash-observation seam. No payload or policy is imposed here.</summary>
public interface IEventObserver
{
    Task ObserveAsync(EventCheckpoint checkpoint, Guid eventId, CancellationToken cancellationToken);
}

/// <summary>An observation seam can stop processing without recording a handler failure, as on process interruption.</summary>
public sealed class EventInterruptedException() : Exception("Event processing was interrupted at a durable boundary.");
