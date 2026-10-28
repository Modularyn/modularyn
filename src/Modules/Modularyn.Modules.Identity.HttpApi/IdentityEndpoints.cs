using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Api;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;

namespace Modularyn.Modules.Identity.HttpApi;

/// <summary>
/// The identity module's HTTP endpoints. Responses are never cached, carry stable codes rather than descriptions, and never
/// contain a password, session token or verifier. State changes need the ingress's antiforgery token.
/// </summary>
public static class IdentityEndpoints
{
    public const string SignInPath = "/identity/sign-in";
    public const string SignOutPath = "/identity/sign-out";
    public const string AntiforgeryPath = "/identity/antiforgery";
    public const string CurrentUserPath = "/identity/current-user";

    private static readonly JsonSerializerOptions Reading = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions Writing = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    /// <summary>The routes these endpoints occupy, for the module manifest.</summary>
    public static IReadOnlyList<ModuleRoute> Routes { get; } = Array.AsReadOnly(
    [
        new ModuleRoute("POST", SignInPath), new ModuleRoute("POST", SignOutPath), new ModuleRoute("GET", AntiforgeryPath), new ModuleRoute("GET", CurrentUserPath)
    ]);

    public static RouteGroupBuilder MapModularynIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("");
        group.MapPost(SignInPath, SignInAsync).AllowAnonymous().WithApiContract(FrameworkApi.Endpoint("SignIn"));
        group.MapPost(SignOutPath, SignOutAsync).AllowAnonymous().WithApiContract(FrameworkApi.Endpoint("SignOut"));
        group.MapGet(AntiforgeryPath, IssueAntiforgery).AllowAnonymous().WithApiContract(FrameworkApi.Endpoint("GetAntiforgery"));
        // This session probe is reachable anonymously and returns 401 unless SEC-03 supplies a current principal.
        group.MapGet(CurrentUserPath, CurrentUser).AllowAnonymous().WithApiContract(FrameworkApi.Endpoint("GetCurrentUser"));
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });
        return group;
    }

    private static async Task<IResult> SignInAsync(HttpContext context, ISignInService signIn, ISessionService sessions, PublicOrigins origins)
    {
        if (!context.Request.HasJsonContentType()) return Code("unsupported_media_type", StatusCodes.Status415UnsupportedMediaType);
        SignInBody? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<SignInBody>(context.Request.Body, Reading, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Code("invalid_request", StatusCodes.Status400BadRequest);
        }
        if (body is not { UserName: { } userName, Password: { } password }) return Code("invalid_request", StatusCodes.Status400BadRequest);

        var source = context.RequestServices.GetRequiredService<ClientSourceObserver>().SourceOf(context.Connection);
        var result = await signIn.SignInWithPasswordAsync(new PasswordSignInRequest(userName, new SecretText(password), source), context.RequestAborted)
            .ConfigureAwait(false);
        switch (result.Status)
        {
            case SignInStatus.Succeeded:
                // A session this browser held before is ended, so no value set before sign-in survives beside the new one.
                if (SessionCookie.TryRead(context.Request, out var earlier, out _))
                    await sessions.SignOutAsync(earlier!, context.RequestAborted).ConfigureAwait(false);
                context.Response.Cookies.Append(SessionCookie.Name, result.Session!.Token.Reveal(), SessionCookie.Options());
                var target = body.ReturnUrl is { } requested && origins.IsAllowedRedirect(requested) ? requested : "/";
                return Results.Json(new SignInResponse(target), Writing);
            case SignInStatus.Throttled:
                var seconds = Math.Max(1, (int)Math.Ceiling((result.RetryAt!.Value - TimeProvider(context).GetUtcNow()).TotalSeconds));
                context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return Code("sign_in_throttled", StatusCodes.Status429TooManyRequests);
            default:
                return Code("sign_in_failed", StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, ISessionService sessions)
    {
        if (SessionCookie.TryRead(context.Request, out var token, out _))
            await sessions.SignOutAsync(token!, context.RequestAborted).ConfigureAwait(false);
        context.RequestServices.GetRequiredService<ExecutionPrincipalScope>().End();
        context.Response.Cookies.Delete(SessionCookie.Name, SessionCookie.Options());
        return Results.NoContent();
    }

    private static IResult IssueAntiforgery(HttpContext context, IAntiforgery antiforgery) =>
        Results.Json(new AntiforgeryResponse(antiforgery.GetAndStoreTokens(context).RequestToken!), Writing);

    private static IResult CurrentUser(IExecutionPrincipalAccessor principals) => principals.Current is { } principal
        ? Results.Json(CurrentPrincipalView.Describe(principal), Writing)
        : Code("unauthenticated", StatusCodes.Status401Unauthorized);

    private static IResult Code(string code, int status) => Results.Json(new CodeResponse(code), Writing, statusCode: status);

    private static TimeProvider TimeProvider(HttpContext context) => context.RequestServices.GetService<TimeProvider>() ?? System.TimeProvider.System;

    private sealed class SignInBody
    {
        public string? UserName { get; set; }
        public string? Password { get; set; }
        public string? ReturnUrl { get; set; }

        public override string ToString() => nameof(SignInBody);
    }

    private sealed record SignInResponse(string Redirect);

    private sealed record AntiforgeryResponse(string RequestToken);

    private sealed record CodeResponse(string Code);
}
