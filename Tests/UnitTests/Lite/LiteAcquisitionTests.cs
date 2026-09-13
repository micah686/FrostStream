using System.Net;
using System.IO.Hashing;
using System.Text;
using DataBridge.Lite;
using FluentStorage.Storage;
using FrostStream.Lite.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shared.Application;
using Shared.Auth;
using Shouldly;
using TUnit.Core;
using YtDlpSharpLib.Options;

namespace UnitTests.Lite;

public sealed class LiteAcquisitionTests
{
    [Test]
    public void Disabled_Pot_Leaves_YtDlp_Options_Unchanged()
    {
        var provider = Provider(new LitePotOptions(), new StubHttpHandler(HttpStatusCode.OK));
        var source = new YtDlpOptions();
        provider.Enabled.ShouldBeFalse();
        provider.Apply(source).ShouldBeSameAs(source);
    }

    [Test]
    public void Enabled_Pot_Appends_Direct_Loopback_And_Plugin_Options()
    {
        var provider = Provider(new LitePotOptions
        {
            Enabled = true,
            ProviderUrl = "http://pot-provider:4416",
            ProxyBaseUrl = "http://127.0.0.1:5098/api/internal/pot/",
            PluginDirectory = "/synthetic/plugins"
        }, new StubHttpHandler(HttpStatusCode.OK));

        var applied = provider.Apply(new YtDlpOptions());

        applied.Extractor.ExtractorArgs.ShouldContain("youtubepot-bgutilhttp:base_url=http://127.0.0.1:5098/api/internal/pot");
        applied.General.PluginDirs.ShouldContain("/synthetic/plugins");
        applied.General.PluginDirs.ShouldContain("default");
    }

    [Test]
    public async Task Enabled_Pot_Reports_An_Actionable_Unavailable_Error()
    {
        var provider = Provider(new LitePotOptions
        {
            Enabled = true,
            ProviderUrl = "http://pot-provider:4416"
        }, new StubHttpHandler(HttpStatusCode.ServiceUnavailable));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => provider.EnsureAvailableAsync());
        exception.Message.ShouldContain("LitePot:ProviderUrl");
        exception.Message.ShouldContain("disable LitePot:Enabled");
    }

    [Test]
    public async Task Enabled_Pot_Health_And_Proxy_Calls_Go_Directly_To_The_Provider()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "provider-response");
        var provider = Provider(new LitePotOptions
        {
            Enabled = true,
            ProviderUrl = "http://pot-provider:4416/base/"
        }, handler);

        await provider.EnsureAvailableAsync();
        var response = await provider.ForwardAsync(HttpMethod.Post, "get_pot?phase=11",
            new MemoryStream(Encoding.UTF8.GetBytes("request")), "application/json");

        handler.Requests.Select(x => x.PathAndQuery).ShouldBe(["/base/ping", "/base/get_pot?phase=11"]);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Encoding.UTF8.GetString(response.Body).ShouldBe("provider-response");
    }

    [Test]
    public async Task Download_Controller_Passes_The_Fixed_Owner_To_Durable_Ingress()
    {
        var ingress = Substitute.For<ILiteDownloadIngress>();
        string? owner = null;
        ingress.AcceptAsync(Arg.Any<LiteDownloadSubmission>(), Arg.Do<string>(value => owner = value), Arg.Any<CancellationToken>())
            .Returns(new DurableWorkReceipt(DurableWorkDisposition.Accepted, Guid.NewGuid(), "download:test"));
        var controller = new DownloadsController(ingress, new FixedCurrentOwner());

        var result = await controller.Create(new LiteDownloadSubmission { SourceUrl = "https://example.test/video" }, CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();
        owner.ShouldBe(AuthConstants.SingleUserSubject);
    }

    [Test]
    public async Task Artifact_Reconciliation_Rejects_A_Partial_Existing_Object()
    {
        var bytes = Encoding.UTF8.GetBytes("complete artifact");
        var hash = Convert.ToHexStringLower(XxHash128.Hash(bytes));
        var storage = Substitute.For<IStore>();
        storage.ObjectExists("media.bin", Arg.Any<CancellationToken>()).Returns(true);
        storage.GetObjectLength("media.bin", -1, Arg.Any<CancellationToken>()).Returns(bytes.Length - 1L);

        (await LiteArtifactReconciler.MatchesAsync(
            storage, "media.bin", hash, bytes.Length, CancellationToken.None)).ShouldBeFalse();
        await storage.DidNotReceive().OpenRead(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static LitePotProvider Provider(LitePotOptions options, HttpMessageHandler handler)
        => new(new HttpClient(handler), Options.Create(options), NullLogger<LitePotProvider>.Instance);

    private sealed class StubHttpHandler(HttpStatusCode statusCode, string responseBody = "{}") : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
