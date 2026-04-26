using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IntegrationTests.Infrastructure;
using NSubstitute;
using Shared.Messaging;
using Shared.Storage;
using Shouldly;

namespace IntegrationTests.Storage;

/// <summary>
/// Integration tests for <c>StorageController</c> that exercise the full ASP.NET Core pipeline
/// (routing, model-binding, validation) by running the WebAPI via <see cref="StorageWebApplicationFactory"/>
/// with <see cref="FlySwattr.NATS.Abstractions.IMessageBus"/> replaced by an NSubstitute mock.
/// </summary>
public sealed class StorageControllerIntegrationTests : IAsyncDisposable
{
    private readonly StorageWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private const string ValidLocalJson = """{"path":"/data/media"}""";
    private const string ValidS3Json = """{"provider":0,"container":"my-bucket","region":"us-east-1","useDefaultCredentials":true}""";

    private static readonly StorageConfigDto SampleDto = new()
    {
        Id = 1,
        Key = "test-storage",
        Method = StorageMethod.PosixLocal,
        Parameters = ValidLocalJson
    };

    public StorageControllerIntegrationTests()
    {
        _factory = new StorageWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // ── POST /api/storage/create ──────────────────────────────────────────────

    [Test]
    public async Task CreateStorage_ValidRequest_Returns200()
    {
        _factory.SetupResponse<StorageCreateRequestMessage>(
            StorageSubjects.CreateStorage,
            new StorageOperationResponseMessage { Success = true });

        var response = await _client.PostAsJsonAsync("/api/storage/create", new
        {
            key = "my-storage",
            method = (int)StorageMethod.PosixLocal,
            parameters = ValidLocalJson
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task CreateStorage_KeyTooShort_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/storage/create", new
        {
            key = "x",          // minimum length is 2
            method = (int)StorageMethod.PosixLocal,
            parameters = ValidLocalJson
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateStorage_KeyFailsRegex_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/storage/create", new
        {
            key = "My_Storage",  // uppercase and underscore not allowed by ^[a-z0-9-]+$
            method = (int)StorageMethod.PosixLocal,
            parameters = ValidLocalJson
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateStorage_MissingKey_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/storage/create", new
        {
            method = (int)StorageMethod.PosixLocal,
            parameters = ValidLocalJson
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateStorage_InvalidParametersJson_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/storage/create", new
        {
            key = "my-storage",
            method = (int)StorageMethod.PosixLocal,
            parameters = "not-valid-json"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateStorage_MissingRequiredParameterField_Returns400()
    {
        // PosixLocal requires "path" — empty object is invalid
        var response = await _client.PostAsJsonAsync("/api/storage/create", new
        {
            key = "my-storage",
            method = (int)StorageMethod.PosixLocal,
            parameters = "{}"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateStorage_BusReturnsNull_Returns503()
    {
        _factory.SetupNullResponse<StorageCreateRequestMessage>(StorageSubjects.CreateStorage);

        var response = await _client.PostAsJsonAsync("/api/storage/create", new
        {
            key = "my-storage",
            method = (int)StorageMethod.PosixLocal,
            parameters = ValidLocalJson
        });

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task CreateStorage_BusReturnsConflict_Returns409()
    {
        _factory.SetupResponse<StorageCreateRequestMessage>(
            StorageSubjects.CreateStorage,
            new StorageOperationResponseMessage { Success = false, ErrorCode = "conflict", ErrorMessage = "Duplicate key." });

        var response = await _client.PostAsJsonAsync("/api/storage/create", new
        {
            key = "my-storage",
            method = (int)StorageMethod.PosixLocal,
            parameters = ValidLocalJson
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task CreateStorage_ObjectStorageParameters_Returns200()
    {
        _factory.SetupResponse<StorageCreateRequestMessage>(
            StorageSubjects.CreateStorage,
            new StorageOperationResponseMessage { Success = true });

        var response = await _client.PostAsJsonAsync("/api/storage/create", new
        {
            key = "s3-storage",
            method = (int)StorageMethod.ObjectStorage,
            parameters = ValidS3Json,
            description = "S3 bucket"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ── PUT /api/storage/update/{key} ─────────────────────────────────────────

    [Test]
    public async Task UpdateStorage_ValidRequest_Returns200WithEntity()
    {
        _factory.SetupResponse<StorageUpdateRequestMessage>(
            StorageSubjects.UpdateStorage,
            new StorageOperationResponseMessage { Success = true, Entity = SampleDto });

        var response = await _client.PutAsJsonAsync("/api/storage/update/test-storage", new
        {
            method = (int)StorageMethod.PosixLocal,
            parameters = ValidLocalJson
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task UpdateStorage_InvalidParametersJson_Returns400()
    {
        var response = await _client.PutAsJsonAsync("/api/storage/update/test-storage", new
        {
            method = (int)StorageMethod.PosixLocal,
            parameters = "not-json"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task UpdateStorage_BusReturnsNull_Returns503()
    {
        _factory.SetupNullResponse<StorageUpdateRequestMessage>(StorageSubjects.UpdateStorage);

        var response = await _client.PutAsJsonAsync("/api/storage/update/test-storage", new
        {
            method = (int)StorageMethod.PosixLocal,
            parameters = ValidLocalJson
        });

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task UpdateStorage_NotFound_Returns404()
    {
        _factory.SetupResponse<StorageUpdateRequestMessage>(
            StorageSubjects.UpdateStorage,
            new StorageOperationResponseMessage { Success = false, ErrorCode = "not_found", ErrorMessage = "Key not found." });

        var response = await _client.PutAsJsonAsync("/api/storage/update/unknown-key", new
        {
            method = (int)StorageMethod.PosixLocal,
            parameters = ValidLocalJson
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateStorage_KeyPassedViaRouteNotBody_Returns200()
    {
        _factory.SetupResponse<StorageUpdateRequestMessage>(
            StorageSubjects.UpdateStorage,
            new StorageOperationResponseMessage { Success = true, Entity = SampleDto });

        var response = await _client.PutAsJsonAsync("/api/storage/update/route-key", new
        {
            method = (int)StorageMethod.PosixLocal,
            parameters = ValidLocalJson
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Verify the route key was forwarded to the message bus
        await _factory.MessageBus.Received(1).RequestAsync<StorageUpdateRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.UpdateStorage,
            Arg.Is<StorageUpdateRequestMessage>(m => m.Key == "route-key"),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    // ── GET /api/storage/list ─────────────────────────────────────────────────

    [Test]
    public async Task ListStorage_Returns200WithItems()
    {
        _factory.SetupResponse<StorageListRequestMessage>(
            StorageSubjects.ListStorage,
            new StorageOperationResponseMessage { Success = true, Items = [SampleDto] });

        var response = await _client.GetAsync("/api/storage/list");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("test-storage");
    }

    [Test]
    public async Task ListStorage_NullItems_Returns200WithEmptyArray()
    {
        _factory.SetupResponse<StorageListRequestMessage>(
            StorageSubjects.ListStorage,
            new StorageOperationResponseMessage { Success = true, Items = null });

        var response = await _client.GetAsync("/api/storage/list");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldBe("[]");
    }

    [Test]
    public async Task ListStorage_BusReturnsNull_Returns503()
    {
        _factory.SetupNullResponse<StorageListRequestMessage>(StorageSubjects.ListStorage);

        var response = await _client.GetAsync("/api/storage/list");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    // ── DELETE /api/storage/delete/{key} ──────────────────────────────────────

    [Test]
    public async Task DeleteStorage_ValidKey_Returns204()
    {
        _factory.SetupResponse<StorageDeleteRequestMessage>(
            StorageSubjects.DeleteStorage,
            new StorageOperationResponseMessage { Success = true });

        var response = await _client.DeleteAsync("/api/storage/delete/test-storage");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task DeleteStorage_BusReturnsNull_Returns503()
    {
        _factory.SetupNullResponse<StorageDeleteRequestMessage>(StorageSubjects.DeleteStorage);

        var response = await _client.DeleteAsync("/api/storage/delete/test-storage");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task DeleteStorage_NotFound_Returns404()
    {
        _factory.SetupResponse<StorageDeleteRequestMessage>(
            StorageSubjects.DeleteStorage,
            new StorageOperationResponseMessage { Success = false, ErrorCode = "not_found", ErrorMessage = "Key not found." });

        var response = await _client.DeleteAsync("/api/storage/delete/unknown-key");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteStorage_KeyPassedViaRoute_ForwardedToMessageBus()
    {
        _factory.SetupResponse<StorageDeleteRequestMessage>(
            StorageSubjects.DeleteStorage,
            new StorageOperationResponseMessage { Success = true });

        await _client.DeleteAsync("/api/storage/delete/specific-key");

        await _factory.MessageBus.Received(1).RequestAsync<StorageDeleteRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.DeleteStorage,
            Arg.Is<StorageDeleteRequestMessage>(m => m.Key == "specific-key"),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    // ── GET /api/storage/{key} ────────────────────────────────────────────────

    [Test]
    public async Task GetStorage_ValidKey_Returns200WithEntity()
    {
        _factory.SetupResponse<StorageGetRequestMessage>(
            StorageSubjects.GetStorage,
            new StorageOperationResponseMessage { Success = true, Entity = SampleDto });

        var response = await _client.GetAsync("/api/storage/test-storage");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("test-storage");
    }

    [Test]
    public async Task GetStorage_BusReturnsNull_Returns503()
    {
        _factory.SetupNullResponse<StorageGetRequestMessage>(StorageSubjects.GetStorage);

        var response = await _client.GetAsync("/api/storage/test-storage");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task GetStorage_NotFound_Returns404()
    {
        _factory.SetupResponse<StorageGetRequestMessage>(
            StorageSubjects.GetStorage,
            new StorageOperationResponseMessage { Success = false, ErrorCode = "not_found", ErrorMessage = "Key not found." });

        var response = await _client.GetAsync("/api/storage/unknown-key");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GetStorage_KeyPassedViaRoute_ForwardedToMessageBus()
    {
        _factory.SetupResponse<StorageGetRequestMessage>(
            StorageSubjects.GetStorage,
            new StorageOperationResponseMessage { Success = true, Entity = SampleDto });

        await _client.GetAsync("/api/storage/my-key");

        await _factory.MessageBus.Received(1).RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.GetStorage,
            Arg.Is<StorageGetRequestMessage>(m => m.Key == "my-key"),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetStorage_SuccessButNullEntity_Returns502()
    {
        _factory.SetupResponse<StorageGetRequestMessage>(
            StorageSubjects.GetStorage,
            new StorageOperationResponseMessage { Success = true, Entity = null });

        var response = await _client.GetAsync("/api/storage/test-storage");

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    // ── Route disambiguation: list vs {key} ───────────────────────────────────

    [Test]
    public async Task ListAndGetRoutes_DoNotConflict()
    {
        _factory.SetupResponse<StorageListRequestMessage>(
            StorageSubjects.ListStorage,
            new StorageOperationResponseMessage { Success = true, Items = [SampleDto] });

        _factory.SetupResponse<StorageGetRequestMessage>(
            StorageSubjects.GetStorage,
            new StorageOperationResponseMessage { Success = true, Entity = SampleDto });

        var listResponse = await _client.GetAsync("/api/storage/list");
        var getResponse = await _client.GetAsync("/api/storage/test-storage");

        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // list hits the list subject, not the get subject
        await _factory.MessageBus.Received(1).RequestAsync<StorageListRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.ListStorage, Arg.Any<StorageListRequestMessage>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());

        await _factory.MessageBus.Received(1).RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.GetStorage, Arg.Any<StorageGetRequestMessage>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}
