using Bunit;
using Bunit.TestDoubles;

namespace TakOne.WebUI.ComponentTests.SmokeTests;

/// <summary>
/// Compile-only smoke test that probes bUnit's authorization API surface
/// in v1.36 so the helper can use the right method names.
/// </summary>
public class BunitAuthApiProbe
{
    public void Probe()
    {
        using var ctx = new TestContext();
        // bUnit v1.36: AddTestAuthorization (not AddAuthorization). Returns
        // TestAuthorizationContext which has SetAuthorized/SetRoles.
        var authCtx = ctx.AddTestAuthorization();
        authCtx.SetAuthorized("alice", AuthorizationState.Authorized);
        authCtx.SetRoles("Admin");
    }
}
