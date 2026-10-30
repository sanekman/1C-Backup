using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace SPBackup.Server;

public sealed class AgentAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, ServerStore store) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentBearer";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var id = await store.AuthenticateAgentAsync(header[7..], Context.RequestAborted);
        if (id is null) return AuthenticateResult.Fail("Invalid agent credential.");
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id.Value.ToString("D")),
            new("agent_generation", Convert.ToHexString(ServerTokens.Hash(header[7..])))], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public static class ServerAuthentication
{
    public const string Administrator = "Administrator";
    public static void Add(WebApplicationBuilder builder, ServerOptions settings)
    {
        var insecure = builder.Environment.IsDevelopment() && settings.AllowInsecureLoopback;
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "OneCBackup.Server.Session";
                options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = insecure ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.Cookie.Path = settings.PathBase ?? "/";
                options.LoginPath = "/login"; options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = false;
                options.Events.OnValidatePrincipal = async context =>
                {
                    var user = context.Principal;
                    var valid = Guid.TryParse(user?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                        && Guid.TryParse(user?.FindFirstValue("stamp"), out var stamp)
                        && await context.HttpContext.RequestServices.GetRequiredService<ServerStore>().ValidateSessionAsync(id, stamp, context.HttpContext.RequestAborted);
                    if (!valid) context.RejectPrincipal();
                };
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/openapi")) context.Response.StatusCode = 401;
                    else context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            }).AddScheme<AuthenticationSchemeOptions, AgentAuthentication>(AgentAuthentication.SchemeName, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(Administrator, policy => policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireRole(Administrator));
            options.AddPolicy("Agent", policy => policy.AddAuthenticationSchemes(AgentAuthentication.SchemeName).RequireAuthenticatedUser());
        });
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "OneCBackup.Server.Antiforgery";
            options.Cookie.SecurePolicy = insecure ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = settings.PathBase ?? "/";
        });
    }
    public static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    internal static byte[] AgentGeneration(ClaimsPrincipal user) => Convert.FromHexString(user.FindFirstValue("agent_generation")!);
}
