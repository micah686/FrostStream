using IntegrationTests.Infrastructure;
using Shared.Auth;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;

namespace IntegrationTests.Auth;

/// <summary>
/// End-to-end verification of the Axis 1 API-surface authorization model (B_Axis1.MD) against a real
/// OpenFGA store. Drives the production <see cref="OpenFgaProvisioner"/>, <see cref="OpenFgaAuthorizer"/>,
/// <see cref="OpenFgaTupleWriter"/>, and <see cref="OpenFgaBundleManagementService"/> — covering the
/// pieces the in-process (single-user, allow-all) tests cannot: the seeded model, the
/// <c>grantee from bundle</c> tuple-to-userset <c>invoke</c> check, the <c>/read</c> all-tuples paging,
/// and runtime bundle compose/grant.
/// </summary>
public class OpenFgaAxis1FlowTests
{
    private static readonly OpenFgaStackFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static OpenFgaAxis1FlowTests()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Before(Test)]
    public async Task SetupAsync()
    {
        await Gate.WaitAsync();
        await Fixture.InitializeAsync();
    }

    [After(Test)]
    public void Release() => Gate.Release();

    [Test]
    public async Task Provisioner_Seeds_Every_Endpoint_Into_AllBundle_Granted_To_Admins()
    {
        var result = await Fixture.Management.ListBundlesAsync(CancellationToken.None);
        result.Status.ShouldBe(BundleOpStatus.Ok);

        var all = result.Value!.FirstOrDefault(b => b.Id == AuthConstants.AllBundle);
        all.ShouldNotBeNull();
        all!.SystemOwned.ShouldBeTrue();
        // The :all guard bundle must contain every code-defined endpoint (the registry auto-includes them).
        all.Endpoints.Count.ShouldBe(EndpointCatalog.Endpoints.Count);
        all.Grants.ShouldContain(g => g.Type == "group" && g.Id == OpenFgaStackFixture.AdminGroup);

        // A seeded baseline bundle is present and system-owned.
        var downloading = result.Value!.FirstOrDefault(b => b.Id == Bundles.Downloading);
        downloading.ShouldNotBeNull();
        downloading!.SystemOwned.ShouldBeTrue();
        downloading.Endpoints.ShouldContain(EndpointIds.DownloadsCreate);
    }

    [Test]
    public async Task Bootstrap_Owner_Subject_Can_Invoke_Everything()
    {
        // Seeded directly with the :all bundle, no group membership required.
        (await Fixture.CanInvokeAsync(OpenFgaStackFixture.OwnerSubject, EndpointIds.DownloadsCreate)).ShouldBeTrue();
        (await Fixture.CanInvokeAsync(OpenFgaStackFixture.OwnerSubject, EndpointIds.ManagementCatalog)).ShouldBeTrue();
    }

    [Test]
    public async Task Admin_Group_Member_Can_Invoke_Any_Endpoint()
    {
        const string subject = "axis1-admin";
        await Fixture.TupleWriter.SyncUserGroupsAsync(subject, [OpenFgaStackFixture.AdminGroup]);

        (await Fixture.CanInvokeAsync(subject, EndpointIds.DownloadsCreate)).ShouldBeTrue();
        (await Fixture.CanInvokeAsync(subject, EndpointIds.StorageList)).ShouldBeTrue();
        (await Fixture.CanInvokeAsync(subject, EndpointIds.ManagementBundlesCreate)).ShouldBeTrue();
    }

    [Test]
    public async Task Unprivileged_User_Is_Denied()
    {
        (await Fixture.CanInvokeAsync("axis1-nobody", EndpointIds.DownloadsCreate)).ShouldBeFalse();
        (await Fixture.CanInvokeAsync("axis1-nobody", EndpointIds.MetadataList)).ShouldBeFalse();
    }

    [Test]
    public async Task Seeded_Bundle_Grant_Scopes_Access_To_That_Bundle_Only()
    {
        const string group = "axis1-downloaders";
        const string subject = "axis1-downloader-user";

        // Grant the seeded "downloading" bundle to a group (grants may target system bundles).
        var grant = await Fixture.Management.GrantAsync(Bundles.Downloading, "group", group, CancellationToken.None);
        grant.Status.ShouldBe(BundleOpStatus.Ok);

        await Fixture.TupleWriter.SyncUserGroupsAsync(subject, [group]);

        (await Fixture.CanInvokeAsync(subject, EndpointIds.DownloadsCreate)).ShouldBeTrue();
        (await Fixture.CanInvokeAsync(subject, EndpointIds.DownloadsAudio)).ShouldBeTrue();
        // Not in the downloading bundle -> denied.
        (await Fixture.CanInvokeAsync(subject, EndpointIds.StorageList)).ShouldBeFalse();
    }

    [Test]
    public async Task System_Owned_Bundles_Are_ReadOnly_To_Runtime_Management()
    {
        (await Fixture.Management.CreateBundleAsync(Bundles.Storage, [EndpointIds.StorageList], CancellationToken.None))
            .Status.ShouldBe(BundleOpStatus.Forbidden);

        (await Fixture.Management.SetBundleEndpointsAsync(Bundles.Storage, [EndpointIds.StorageGet], CancellationToken.None))
            .Status.ShouldBe(BundleOpStatus.Forbidden);

        (await Fixture.Management.DeleteBundleAsync(Bundles.Storage, CancellationToken.None))
            .Status.ShouldBe(BundleOpStatus.Forbidden);

        // The guard above must not have mutated the seeded bundle.
        var storage = (await Fixture.Management.GetBundleAsync(Bundles.Storage, CancellationToken.None)).Value;
        storage.ShouldNotBeNull();
        storage!.Endpoints.ShouldContain(EndpointIds.StorageList);
    }

    [Test]
    public async Task Runtime_Bundle_Rejects_Unknown_Endpoints()
    {
        (await Fixture.Management.CreateBundleAsync("user.bad", ["not.a.real.endpoint"], CancellationToken.None))
            .Status.ShouldBe(BundleOpStatus.Validation);

        // Empty membership is also rejected.
        (await Fixture.Management.CreateBundleAsync("user.empty", [], CancellationToken.None))
            .Status.ShouldBe(BundleOpStatus.Validation);
    }

    [Test]
    public async Task User_Composed_Bundle_Full_Lifecycle_And_Grant()
    {
        const string bundle = "user.axis1-mix";
        const string group = "axis1-mixers";
        const string subject = "axis1-mixer-user";

        // Compose from the catalog.
        (await Fixture.Management.CreateBundleAsync(bundle, [EndpointIds.StorageList], CancellationToken.None))
            .Status.ShouldBe(BundleOpStatus.Ok);

        // Grant it and confirm scoped access through a real invoke check.
        (await Fixture.Management.GrantAsync(bundle, "group", group, CancellationToken.None))
            .Status.ShouldBe(BundleOpStatus.Ok);
        await Fixture.TupleWriter.SyncUserGroupsAsync(subject, [group]);

        (await Fixture.CanInvokeAsync(subject, EndpointIds.StorageList)).ShouldBeTrue();
        (await Fixture.CanInvokeAsync(subject, EndpointIds.DownloadsCreate)).ShouldBeFalse();

        // Extend membership; the new endpoint becomes invokable.
        (await Fixture.Management.SetBundleEndpointsAsync(bundle, [EndpointIds.StorageList, EndpointIds.StorageGet], CancellationToken.None))
            .Status.ShouldBe(BundleOpStatus.Ok);
        (await Fixture.CanInvokeAsync(subject, EndpointIds.StorageGet)).ShouldBeTrue();

        // Revoke removes access.
        (await Fixture.Management.RevokeAsync(bundle, "group", group, CancellationToken.None))
            .Status.ShouldBe(BundleOpStatus.Ok);
        (await Fixture.CanInvokeAsync(subject, EndpointIds.StorageList)).ShouldBeFalse();

        // Delete tears the bundle down.
        (await Fixture.Management.DeleteBundleAsync(bundle, CancellationToken.None))
            .Status.ShouldBe(BundleOpStatus.Ok);
        (await Fixture.Management.GetBundleAsync(bundle, CancellationToken.None))
            .Status.ShouldBe(BundleOpStatus.NotFound);
    }
}
