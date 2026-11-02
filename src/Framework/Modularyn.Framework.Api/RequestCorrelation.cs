using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Ingress;

namespace Modularyn.Framework.Api;

/// <summary>
/// The request's correlation is server-issued (TM-G02-053). ASP.NET Core would adopt an inbound <c>traceparent</c> as the
/// request's trace, and a client could then choose the trace that its request's logs, audit records and response
/// correlation carry, or join another request's. <c>AddModularynApi</c> stops the server from extracting inbound trace
/// context and baggage, so every request starts its own trace; only when the request's direct peer is a declared proxy
/// (<c>Modularyn:Ingress:Proxies</c>) does the request continue the proxy's trace. Either way the identifier is diagnostic
/// linkage only and conveys no authority, and no inbound baggage is kept.
/// </summary>
/// <remarks>
/// The correlation is decided once per request, where <see cref="Begin"/> first runs, which <c>UseModularynApi</c> places
/// before the ingress: the peer is judged by the connection's own address before forwarded headers rewrite it, and never by
/// a header. Every later call, such as <c>UseModularynRequests</c>' after the ingress, keeps that decision, and every
/// response header and problem body carries the one identifier it chose.
/// </remarks>
public static class RequestCorrelation
{
    /// <summary>The name of the activity a request runs under when it adopts a declared proxy's trace or leaves a peer's.</summary>
    public const string ActivityName = "Modularyn.Http.Request";

    private const int MaxTraceStateLength = 512;

    // The request's decided correlation. The key is private, so only this class reads or writes the decision.
    private static readonly object Decision = new();

    /// <summary>
    /// Places the request under its correlation, once: a declared proxy's trace, adopted from its <c>traceparent</c>;
    /// otherwise the server's own, leaving any trace context the peer supplied. Returns what restores the ambient activity
    /// when disposed, or null when there is nothing to change or the request's correlation was already decided. Call it at
    /// the start of the request, before forwarded headers rewrite the peer address.
    /// </summary>
    public static IDisposable? Begin(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.ContainsKey(Decision)) return null;
        var inbound = Activity.Current;
        IDisposable? restore = null;
        if (FromDeclaredProxy(context))
        {
            if (TryReadParent(context.Request.Headers, out var parent) && inbound?.TraceId != parent.TraceId) restore = Start(inbound, parent);
        }
        // A request activity with no parent was started by this server; one with a parent continued the peer's trace.
        else if (inbound?.ParentId is not null) restore = Start(inbound, default);
        context.Items[Decision] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        return restore;
    }

    /// <summary>
    /// The correlation a response carries: the one the request's first <see cref="Begin"/> decided, or, for a request that
    /// never began one, the current trace or the server's own request identifier.
    /// </summary>
    public static string Current(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(Decision, out var decided) && decided is string correlation ? correlation
            : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    }

    private static IDisposable Start(Activity? inbound, ActivityContext parent)
    {
        Activity.Current = null;
        var activity = new Activity(ActivityName);
        activity.SetIdFormat(ActivityIdFormat.W3C);
        if (parent != default)
        {
            activity.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
            activity.TraceStateString = parent.TraceState;
        }
        activity.Start();
        return new Restore(activity, inbound);
    }

    private static bool TryReadParent(IHeaderDictionary headers, out ActivityContext parent)
    {
        parent = default;
        if (headers.TraceParent is not [{ } traceParent]) return false;
        var traceState = headers.TraceState is [{ Length: <= MaxTraceStateLength } state] ? state : null;
        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out parent);
    }

    private static bool FromDeclaredProxy(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } peer) return false;
        var proxies = context.RequestServices?.GetService<IOptions<IngressOptions>>()?.Value.Proxies;
        if (proxies is not { Count: > 0 }) return false;
        var address = (peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer).ToString();
        return proxies.Contains(address, StringComparer.Ordinal);
    }

    private sealed class Restore(Activity activity, Activity? inbound) : IDisposable
    {
        public void Dispose()
        {
            activity.Stop();
            Activity.Current = inbound;
        }
    }
}

/// <summary>
/// The server's trace-context propagator: it injects outbound context as the default propagator does, and extracts
/// nothing from inbound requests, so ASP.NET Core starts every request's trace itself.
/// </summary>
internal sealed class ServerIssuedPropagator(DistributedContextPropagator outbound) : DistributedContextPropagator
{
    public override IReadOnlyCollection<string> Fields => outbound.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) => outbound.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
    {
        traceId = null;
        traceState = null;
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) => null;
}
