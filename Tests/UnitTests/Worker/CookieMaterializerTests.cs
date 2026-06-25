using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shared.Secrets;
using Shouldly;
using TUnit.Core;
using Worker.Services;

namespace UnitTests.Worker;

public sealed class CookieMaterializerTests
{
    private const string ProfilePath = "cookies/users/user-abc/member-cookie";

    [Test]
    public async Task CreateFromPathAsync_With_Blank_Path_Does_Not_Read_Secret_Store()
    {
        var store = Substitute.For<ISecretStore>();

        await using var materializer = await CookieMaterializer.CreateFromPathAsync(
            store,
            secretPath: " ",
            scratchDirectory: Path.GetTempPath(),
            NullLogger.Instance);

        materializer.FilePath.ShouldBeNull();
        await store.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Test]
    public async Task CreateFromPathAsync_Writes_Cookie_Content_To_Temp_File_And_Disposes_It()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"froststream-cookies-{Guid.NewGuid():N}");
        var store = Substitute.For<ISecretStore>();
        store.ReadAsync(ProfilePath, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>
            {
                ["content"] = "# Netscape HTTP Cookie File"
            });

        try
        {
            var materializer = await CookieMaterializer.CreateFromPathAsync(
                store,
                ProfilePath,
                scratch,
                NullLogger.Instance);

            materializer.FilePath.ShouldNotBeNull();
            File.Exists(materializer.FilePath).ShouldBeTrue();
            (await File.ReadAllTextAsync(materializer.FilePath)).ShouldBe("# Netscape HTTP Cookie File");

            await materializer.DisposeAsync();

            File.Exists(materializer.FilePath).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    [Test]
    public async Task CreateFromPathAsync_Throws_When_Secret_Has_No_Content_Field()
    {
        var store = Substitute.For<ISecretStore>();
        store.ReadAsync("cookies/users/user-abc/missing", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            CookieMaterializer.CreateFromPathAsync(
                store,
                "cookies/users/user-abc/missing",
                Path.GetTempPath(),
                NullLogger.Instance));

        exception.Message.ShouldContain("cookies/users/user-abc/missing");
        exception.Message.ShouldContain("content");
    }
}
