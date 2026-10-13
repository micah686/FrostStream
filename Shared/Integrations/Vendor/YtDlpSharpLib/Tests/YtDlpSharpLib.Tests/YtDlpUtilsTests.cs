namespace YtDlpSharpLib.Tests;

public sealed class YtDlpUtilsTests
{
    [Theory]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", false, "a\u29f8b\u29f9c -d_ef'g_h_i_j")]
    [InlineData("  ...  ", false, "_")]
    [InlineData("AT&T demo \u2013 caf\u00e9", true, "AT_T_demo_caf")]
    [InlineData("a  b", true, "a_b")]
    public void SanitizeFilename_AppliesYtDlpCompatibleFilenameRules(
        string value,
        bool restricted,
        string expected)
    {
        Assert.Equal(expected, YtDlpUtils.SanitizeFilename(value, restricted));
        Assert.Equal(expected, YtDlpUtils.Sanitize(value, restricted));
    }
}
