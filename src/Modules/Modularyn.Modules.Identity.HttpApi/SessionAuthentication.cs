using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;

[assembly: InternalsVisibleTo("Modularyn.Modules.Identity.HttpApi.Tests")]

namespace Modularyn.Modules.Identity.HttpApi;

/// <summary>The session cookie: host-only through its __Host- prefix, Secure, HttpOnly, SameSite=Strict and without an expiry.</summary>
public static class SessionCookie
{
    public const string Name = "__Host-Modularyn-Session";

    /// <summary>The header naming the delegation grant a request acts under. Its value is the grant identifier.</summary>
    public const string DelegationHeader = "Modularyn-Delegation";

    internal static CookieOptions Options() => new()
    {
        Secure = true,
        HttpOnly = true,
        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
        Path = "/",
        Domain = null,
        IsEssential = true
    };

    /// <summary>
    /// Reads the one session cookie the request carries. False with <paramref name="present"/> set when it carries a malformed
    /// value or more than one, which is refused without reaching the store.
    /// </summary>
    internal static bool TryRead(HttpRequest request, out SessionToken? token, out bool present)
    {
        token = null;
        present = false;
        if (request.Headers.Cookie.Count == 0 || !CookieHeaderValue.TryParseList(request.Headers.Cookie.Select(value => value ?? "").ToArray(), out var cookies)) return false;
        string? value = null;
        foreach (var cookie in cookies)
        {
            if (!string.Equals(cookie.Name.Value, Name, StringComparison.Ordinal)) continue;
            if (present) return false;
            present = true;
            value = cookie.Value.Value;
        }
        return present && SessionToken.TryParse(value, out token);
    }
}

public sealed class SessionAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authenticates a request by its session cookie against the server-side session record, on every request, and resolves a
/// delegation the request names. A request whose delegation does not resolve is not authenticated at all, so it never
/// falls back to the actor's own authority. Challenges and refusals are bare 401 and 403 responses.
/// </summary>
internal sealed class SessionAuthenticationHandler(IOptionsMonitor<SessionAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    ISessionService sessions, IDelegationService delegations) : AuthenticationHandler<SessionAuthenticationOptions>(options, logger, encoder)
{
    internal const string SessionClaim = "modularyn:session";
    internal const string SubjectClaim = "modularyn:subject";
    internal const string DelegationClaim = "modularyn:delegation";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!SessionCookie.TryRead(Request, out var token, out var present))
            return present ? AuthenticateResult.Fail("The session cookie is malformed.") : AuthenticateResult.NoResult();
        var session = await sessions.ValidateAsync(token!, Context.RequestAborted).ConfigureAwait(false);
        if (session is null)
        {
            if (!Response.HasStarted) Response.Cookies.Delete(SessionCookie.Name, SessionCookie.Options());
            return AuthenticateResult.Fail("The session is not active.");
        }
        var principal = session.ToPrincipal();
        if (Request.Headers.TryGetValue(SessionCookie.DelegationHeader, out var references))
        {
            if (references.Count != 1 || !Guid.TryParseExact(references[0], "D", out var grantId))
                return AuthenticateResult.Fail("The delegation reference is malformed.");
            var delegated = await delegations.ResolveAsync(grantId, principal, Context.RequestAborted).ConfigureAwait(false);
            if (delegated is null) return AuthenticateResult.Fail("The delegation does not resolve for this session.");
            principal = delegated;
        }
        // The request's own service scope holds its principal, established once; nothing ambient carries it.
        if (!Context.RequestServices.GetRequiredService<ExecutionPrincipalScope>().TryEstablish(principal))
            return AuthenticateResult.Fail("The request's principal is already established.");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, principal.Actor.Id),
            new(SessionClaim, session.SessionId.ToString("D")),
            new(ClaimTypes.AuthenticationInstant, session.AuthenticatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
        };
        if (principal.Delegation is { } delegation)
        {
            claims.Add(new(SubjectClaim, principal.EffectiveSubject.Id));
            claims.Add(new(DelegationClaim, delegation.GrantId.ToString("D")));
            // Tenancy requires every candidate to name this tenant, so a delegated request reaches no other tenant.
            if (delegation.Tenant is { } tenant) claims.Add(new(TenantClaimTypes.Tenant, TenantIdentifier.Format(tenant)));
        }
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>Reduces a client address to the unit a per-source limit applies to: an IPv4 address, or an IPv6 /64 network.</summary>
internal static class ClientSourceAddress
{
    internal static string From(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}

/// <summary>
/// Tenancy's actor seam (SEC-04): the account behind a request's validated session. A delegated request resolves to its
/// actor, never to the subject it acts for, so tenant membership and host authority are always the actor's own; the
/// delegation adds only its tenant claim and its ceiling.
/// </summary>
internal sealed class SessionTenantActorResolver : ITenantActorResolver
{
    public ActorReference? Resolve(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identity = principal.Identities.FirstOrDefault(item => item.IsAuthenticated
            && string.Equals(item.AuthenticationType, IdentityHttpServiceCollectionExtensions.AuthenticationScheme, StringComparison.Ordinal));
        return identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } account && Guid.TryParseExact(account, "D", out _)
            ? new ActorReference(ActorKind.User, account) : null;
    }
}
