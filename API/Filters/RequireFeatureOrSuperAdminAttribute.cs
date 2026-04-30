using API.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace API.Filters;

/// <summary>
/// Action filter that gates a controller (or action) behind a FeatureFlag.
/// Superadmins (JWT claim isSuperAdmin=true) always pass through so they can test
/// hidden features before flipping the flag on for the customer.
/// Everyone else (including the regular admin and unauthenticated kiosk callers)
/// gets a 404 when the flag is off — keeps the feature completely invisible.
/// Apply with: [RequireFeatureOrSuperAdmin("Notifications")]
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireFeatureOrSuperAdminAttribute : Attribute, IAsyncActionFilter
{
    private readonly string _featureKey;

    public RequireFeatureOrSuperAdminAttribute(string featureKey)
    {
        _featureKey = featureKey;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.User?.HasClaim("isSuperAdmin", "true") == true)
        {
            await next();
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Key == _featureKey);
        if (flag?.Enabled == true)
        {
            await next();
            return;
        }

        context.Result = new NotFoundResult();
    }
}
