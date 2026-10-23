using System.Collections.Concurrent;
using AutomateCert.Server.Data;
using Microsoft.Extensions.Caching.Memory;

namespace AutomateCert.Server;

/// <summary>
/// Short-lived cache of the "is this signed-in user still valid?" check that runs on every request.
/// Entries live 30 s and are removed immediately when an admin disables a user, changes a role or resets a password.
/// </summary>
public static class PrincipalCache
{
    private static readonly ConcurrentDictionary<Guid, (DateTime Until, bool Disabled, string Role)> Items = new();

    public static bool TryGet(Guid id, out bool disabled, out string role)
    {
        if (Items.TryGetValue(id, out var e) && e.Until > DateTime.UtcNow) { disabled = e.Disabled; role = e.Role; return true; }
        disabled = false; role = "";
        return false;
    }

    public static void Set(Guid id, bool disabled, string role) => Items[id] = (DateTime.UtcNow.AddSeconds(30), disabled, role);
    public static void Invalidate(Guid id) => Items.TryRemove(id, out _);
}

/// <summary>Caches successful GET results of expensive read endpoints per tenant for a few seconds.</summary>
public sealed class ShortCache(int seconds) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var cache = http.RequestServices.GetRequiredService<IMemoryCache>();
        var tenant = http.RequestServices.GetRequiredService<TenantContext>().TenantId;
        var key = $"sc:{tenant}:{http.Request.Path}{http.Request.QueryString}";
        if (cache.TryGetValue(key, out var hit)) return Results.Ok(hit);
        var result = await next(ctx);
        if (result is IValueHttpResult v && result is IStatusCodeHttpResult { StatusCode: 200 or null } && v.Value is not null)
            cache.Set(key, v.Value, TimeSpan.FromSeconds(seconds));
        return result;
    }
}
