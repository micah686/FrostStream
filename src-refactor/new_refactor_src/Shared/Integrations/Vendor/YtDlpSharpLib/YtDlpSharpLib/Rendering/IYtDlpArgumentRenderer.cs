using YtDlpSharpLib.Options;

namespace YtDlpSharpLib.Rendering;

/// <summary>
/// Converts a <see cref="YtDlpOptions"/> instance into argv tokens suitable for
/// <c>ProcessStartInfo.ArgumentList</c>.
/// </summary>
public interface IYtDlpArgumentRenderer
{
    /// <summary>
    /// Renders typed options to argv tokens.
    /// </summary>
    /// <param name="options">The strongly typed options to render.</param>
    /// <returns>The argv tokens, in order.</returns>
    IReadOnlyList<string> Render(YtDlpOptions options);
}
