namespace YtDlpSharpLib.Tests;

public sealed class GeneratorTests
{
    [Fact]
    public async Task FixtureGeneration_ProducesExpectedSurface()
    {
        var repoRoot = FindRepoRoot();
        var fixture = Path.Combine(repoRoot, "Tests", "YtDlpSharpLib.Tests", "Fixtures", "yt-dlp-help.txt");
        var outputDir = CreateTempDirectory();

        try
        {
            var generate = await RunGeneratorAsync(repoRoot, "--help-file", fixture, "--output-dir", outputDir);
            AssertProcessSucceeded(generate);

            var general = await File.ReadAllTextAsync(Path.Combine(outputDir, "YtDlpGeneralOptions.g.cs"));
            Assert.Contains("public IReadOnlyList<string> ConfigLocations", general);
            Assert.Contains("ValueTokenCount = 2", general);
            Assert.Contains("public IReadOnlyList<IReadOnlyList<string>> Alias", general);

            var postProcessing = await File.ReadAllTextAsync(Path.Combine(outputDir, "YtDlpPostProcessingOptions.g.cs"));
            Assert.Contains("public AudioConversionFormat? AudioFormat", postProcessing);
            Assert.Contains("public VideoContainer? RemuxVideo", postProcessing);
            Assert.Contains("public VideoRecodeFormat? RecodeVideo", postProcessing);
            Assert.Contains("public SubtitleFormat? ConvertSubs", postProcessing);
            Assert.Contains("public enum YtDlpPostProcessingFixup", postProcessing);
            Assert.Contains("[YtDlpEnumValue(\"detect_or_warn\")]", postProcessing);

            var videoFormat = await File.ReadAllTextAsync(Path.Combine(outputDir, "YtDlpVideoFormatOptions.g.cs"));
            Assert.Contains("public DownloadMergeFormat? MergeOutputFormat", videoFormat);

            var subtitle = await File.ReadAllTextAsync(Path.Combine(outputDir, "YtDlpSubtitleOptions.g.cs"));
            Assert.Contains("public SubtitleFormat? SubFormat", subtitle);

            var root = await File.ReadAllTextAsync(Path.Combine(outputDir, "YtDlpOptions.Generated.g.cs"));
            Assert.Contains("public YtDlpSponsorBlockOptions SponsorBlock", root);
            Assert.Contains("public YtDlpDeprecatedOptions Deprecated", root);
            Assert.True(File.Exists(Path.Combine(outputDir, "YtDlpSponsorBlockOptions.g.cs")));

            var deprecated = await File.ReadAllTextAsync(Path.Combine(outputDir, "YtDlpDeprecatedOptions.g.cs"));
            Assert.Contains("public string? MatchTitle", deprecated);
            Assert.Contains("[Obsolete(\"Use VideoSelection.MatchFilters instead.\")]", deprecated);
            Assert.Contains("[YtDlpArgument(\"--autonumber-size\"", deprecated);

            var check = await RunGeneratorAsync(repoRoot, "--help-file", fixture, "--output-dir", outputDir, "--check");
            AssertProcessSucceeded(check);
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Check_FailsWhenGeneratedFilesDiffer()
    {
        var repoRoot = FindRepoRoot();
        var fixture = Path.Combine(repoRoot, "Tests", "YtDlpSharpLib.Tests", "Fixtures", "yt-dlp-help.txt");
        var outputDir = CreateTempDirectory();

        try
        {
            var generate = await RunGeneratorAsync(repoRoot, "--help-file", fixture, "--output-dir", outputDir);
            AssertProcessSucceeded(generate);

            await File.AppendAllTextAsync(Path.Combine(outputDir, "YtDlpNetworkOptions.g.cs"), "// stale edit");

            var check = await RunGeneratorAsync(repoRoot, "--help-file", fixture, "--output-dir", outputDir, "--check");
            Assert.NotEqual(0, check.ExitCode);
            Assert.Contains("Changed generated files", check.StandardError);
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunGeneratorAsync(string repoRoot, params string[] arguments)
    {
        var runHome = CreateTempDirectory();
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "dotnet";
            process.StartInfo.ArgumentList.Add("run");
            process.StartInfo.ArgumentList.Add("GenerateOptions.cs");
            process.StartInfo.ArgumentList.Add("--");
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.WorkingDirectory = repoRoot;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.Environment["XDG_DATA_HOME"] = Path.Combine(runHome, "xdg-data");
            process.StartInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(runHome, "dotnet-home");
            process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
            process.StartInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => process.WaitForExit(milliseconds: 30_000));

            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("GenerateOptions.cs did not finish within 30 seconds.");
            }

            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            Directory.Delete(runHome, recursive: true);
        }
    }

    private static void AssertProcessSucceeded(ProcessResult result) =>
        Assert.True(
            result.ExitCode == 0,
            $"Exit code: {result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GenerateOptions.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "YtDlpSharpLib.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
