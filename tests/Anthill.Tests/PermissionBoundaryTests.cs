using Anthill.Core.Security;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Role-permission boundaries (v1.8.14.5 audit). The mission report is served under
/// <c>read_status</c> (which coordinators hold) but can surface patch proposals, approval state,
/// and autonomy objectives — surfaces gated by admin-only permissions. These tests pin the fact
/// that those permissions are admin-only, which is what the report's sensitive-section stripping
/// relies on: a coordinator lacking <c>read_patches</c> gets a report with those sections empty.
/// </summary>
public class PermissionBoundaryTests
{
    [Theory]
    [InlineData("read_patches")]
    [InlineData("read_approvals")]
    [InlineData("read_objectives")]
    [InlineData("operator_shell")]
    [InlineData("manage_settings")]
    [InlineData("manage_providers")]
    public void SensitivePermissions_AreAdminOnly(string permission)
    {
        Assert.True(UserRoles.RoleAllows(UserRoles.Admin, permission));
        Assert.False(UserRoles.RoleAllows(UserRoles.Coordinator, permission));
        Assert.True(UserRoles.IsAdminOnly(permission));
    }

    [Theory]
    [InlineData("run_mission")]
    [InlineData("read_status")]
    [InlineData("read_events")]
    [InlineData("read_ui_state")]
    public void CoordinatorBaseline_IsAllowedButNotAdminExtras(string permission)
    {
        Assert.True(UserRoles.RoleAllows(UserRoles.Coordinator, permission));
        Assert.False(UserRoles.IsAdminOnly(permission));
    }

    [Fact]
    public void Coordinator_CannotReachAdminReadsGrantedToStatus()
    {
        // read_status is shared, but the report's sensitive sections key off read_patches, which
        // the coordinator does NOT have — so the stripping guard engages for them.
        Assert.True(UserRoles.RoleAllows(UserRoles.Coordinator, "read_status"));
        Assert.False(UserRoles.RoleAllows(UserRoles.Coordinator, "read_patches"));
    }
}
