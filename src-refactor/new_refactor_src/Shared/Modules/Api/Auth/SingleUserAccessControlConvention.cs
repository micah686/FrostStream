using Microsoft.AspNetCore.Mvc.ApplicationModels;
using WebAPI.Features.Management.Controllers;

namespace WebAPI.Auth;

/// <summary>
/// Removes the multi-user access-control surface from MVC discovery when FrostStream is running in
/// single-user mode. This keeps the API unavailable rather than merely denying its actions.
/// </summary>
public sealed class SingleUserAccessControlConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        var accessControlController = application.Controllers.FirstOrDefault(controller =>
            controller.ControllerType.AsType() == typeof(AccessControlController));

        if (accessControlController is not null)
        {
            application.Controllers.Remove(accessControlController);
        }
    }
}
