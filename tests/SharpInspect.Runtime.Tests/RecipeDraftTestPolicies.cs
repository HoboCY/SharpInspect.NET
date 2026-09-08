using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;

namespace SharpInspect.Runtime.Tests;

internal static class RecipeDraftTestPolicies
{
    // Draft authoring is an explicit policy choice for a new development store.
    // The persisted pre-Draft Development policy retains its original hash.
    internal static AuthorizationPolicy Authoring { get; } = new(
        "draft-development", "draft-development-2026-09-v1",
        AuthorizationPolicy.Development.RoleBundles.ToDictionary(pair => pair.Key,
            pair => pair.Key is HumanRoleBundle.Technician or HumanRoleBundle.Administrator
                ? pair.Value.Append(Permission.EditRecipeDraft)
                : pair.Value.AsEnumerable()),
        AuthorizationPolicy.Development.StepUpPermissions);
}
