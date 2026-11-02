using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Host;

public static class WorkspacePages
{
    public static IResult SignIn(HttpContext context) => Html(context, "Sign in", """
        <h1>Team workspace</h1>
        <form id="sign-in" action="/identity/sign-in" method="post">
          <p><label>User name <input name="userName" autocomplete="username" required maxlength="256"></label></p>
          <p><label>Password <input name="password" type="password" autocomplete="current-password" required maxlength="256"></label></p>
          <p><label>Tenant identifier <input name="tenant" required maxlength="36"></label></p>
          <button type="submit">Sign in</button>
        </form>
        <p id="status" role="status"></p>
        <script src="/assets/sign-in.js" defer></script>
        """);

    public static async Task<IResult> NewNoteAsync(HttpContext context)
    {
        var tenant = WorkspaceEndpoints.Tenant(context);
        var projects = await WorkspaceEndpoints.Service(context).ProjectsAsync(tenant, WorkspaceEndpoints.Principal(context), context.RequestAborted);
        var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
        var options = string.Concat(projects.Select(project => $"<option value=\"{project.Id:D}\">{Encode(project.Name)}</option>"));
        return Html(context, "Create a note", $"""
            <h1>Create a note</h1>
            <form method="post" action="/t/{tenant.TenantId:D}/notes/new">
              <input type="hidden" name="{Encode(tokens.FormFieldName)}" value="{Encode(tokens.RequestToken!)}">
              <input type="hidden" name="operation" value="{Guid.NewGuid():D}">
              <p><label>Project <select name="project" required>{options}</select></label></p>
              <p><label>Note <textarea name="text" required maxlength="16000"></textarea></label></p>
              <button type="submit">Save note</button>
            </form>
            """);
    }

    public static async Task<IResult> CreateNoteAsync(HttpContext context)
    {
        if (context.Request.ContentType?.Split(';')[0].Trim() != ApiMediaTypes.Form) throw new ApiException(ApiCodes.Validation, 415);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form["project"] is not [{ } projectText] || !Guid.TryParseExact(projectText, "D", out var project)
            || form["operation"] is not [{ } operationText] || !Guid.TryParseExact(operationText, "D", out var operation)
            || form["text"] is not [{ } text]) throw new ApiException(ApiCodes.Validation, 400);
        var tenant = WorkspaceEndpoints.Tenant(context);
        var note = await WorkspaceEndpoints.Service(context).CreateNoteAsync(tenant, WorkspaceEndpoints.Principal(context), operation, 1,
            new(project, text), context.RequestAborted);
        return Html(context, "Note saved", $"<h1>Note saved</h1><p>{note.Id:D}</p><a href=\"/t/{tenant.TenantId:D}/notes/new\">Create another note</a>");
    }

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);
    private static IResult Html(HttpContext context, string title, string body)
    {
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Content($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>{Encode(title)}</title></head><body>{body}</body></html>", ApiMediaTypes.Html);
    }
}
