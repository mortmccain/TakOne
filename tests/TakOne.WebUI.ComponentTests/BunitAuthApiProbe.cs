using Bunit;
using Bunit.TestDoubles;

namespace TakOne.WebUI.ComponentTests.SmokeTests;

/// <summary>
/// Compile-only smoke test that probes bUnit's authorization API surface
/// in v2.14 so the helper can use the right method names.
/// </summary>
public class BunitAuthApiProbe
{
    public static void Probe()
    {
        using var ctx = new BunitContext();
        // bUnit v2.14: AddAuthorization (renamed from AddTestAuthorization
        // in 1.x). Returns BunitAuthorizationContext (renamed from
        // TestAuthorizationContext) which has SetAuthorized/SetRoles.
        var authCtx = ctx.AddAuthorization();
        authCtx.SetAuthorized("alice", AuthorizationState.Authorized);
        authCtx.SetRoles("Admin");
    }
}
