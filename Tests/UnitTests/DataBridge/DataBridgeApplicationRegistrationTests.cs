using System.Security.Claims;
using DataBridge;
using DataBridge.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shared.Application;
using Shared.Auth;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;

namespace UnitTests.DataBridge;

public sealed class DataBridgeApplicationRegistrationTests
{
    [Test]
    public void Reusable_Module_Registers_Application_Without_Starting_Executable()
    {
        var services = new ServiceCollection();

        services.AddDataBridgeApplicationOperations();

        var descriptor = services.Single(service => service.ServiceType == typeof(IUserNoteApplication));
        descriptor.ImplementationType.ShouldBe(typeof(UserNoteApplication));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Test]
    public void Reusable_Module_Registers_Durable_Ingress_And_Bounded_Local_Progress()
    {
        var services = new ServiceCollection();

        services.AddDataBridgeApplicationOperations();

        services.Single(service => service.ServiceType == typeof(IDownloadWorkflowIngress))
            .Lifetime.ShouldBe(ServiceLifetime.Scoped);
        services.Single(service => service.ServiceType == typeof(IDownloadWorkflowStarter))
            .Lifetime.ShouldBe(ServiceLifetime.Singleton);
        services.Single(service => service.ServiceType == typeof(ILocalProgressHub<,>))
            .ImplementationType.ShouldBe(typeof(BoundedLocalProgressHub<,>));
    }

    [Test]
    public void Fixed_Current_Owner_Uses_Established_Single_User_Subject()
    {
        ICurrentOwner owner = new FixedCurrentOwner();

        owner.Subject.ShouldBe(AuthConstants.SingleUserSubject);
    }

    [Test]
    public void Http_Current_Owner_Uses_Authenticated_Subject()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthConstants.SubjectClaim, "full-owner")],
                authenticationType: "test"))
        };
        ICurrentOwner owner = new HttpCurrentOwner(new HttpContextAccessor { HttpContext = context });

        owner.Subject.ShouldBe("full-owner");
    }
}
