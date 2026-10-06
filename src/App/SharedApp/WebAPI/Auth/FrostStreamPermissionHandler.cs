using Microsoft.AspNetCore.Authorization;
using Shared.Auth;

namespace WebAPI.Auth;

public sealed class FrostStreamPermissionRequirement(string relation, string objectRef) : IAuthorizationRequirement
{
    public string Relation { get; } = relation;

    public string ObjectRef { get; } = objectRef;
}

public sealed class FrostStreamPermissionHandler(IFrostStreamAuthorizer authorizer, ILogger<FrostStreamPermissionHandler> logger)
    : AuthorizationHandler<FrostStreamPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FrostStreamPermissionRequirement requirement)
    {
        var subject = AuthConstants.FindSubject(context.User);
        if (subject is null)
        {
            context.Fail();
            return;
        }

        var decision = await authorizer.CheckAsync(new FrostStreamAuthorizationCheck(
            $"user:{subject}",
            requirement.Relation,
            requirement.ObjectRef));

        if (decision.Allowed)
        {
            context.Succeed(requirement);
            return;
        }

        logger.LogDebug(
            "Authorization denied for user {User}, relation {Relation}, object {Object}: {Reason}",
            subject,
            requirement.Relation,
            requirement.ObjectRef,
            decision.Reason ?? "not permitted");
        context.Fail();
    }
}
