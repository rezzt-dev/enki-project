using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="SupabaseConfig.NormalizeAndValidateHttpsUrl"/> — the pure validation the
/// static constructor applies to <c>SUPABASE_URL</c> from <c>local.properties</c>. The static constructor
/// itself can't be re-run per test case (it only runs once per process, from whatever
/// <c>local.properties</c> happens to exist on this machine), so this exercises the extracted pure function
/// directly instead (docs/analisis-proyecto/roadmap.md, Fase 4): a non-<c>https://</c> URL must be treated
/// as unconfigured rather than silently degrading Polar's REST/Realtime traffic to plain text.</summary>
public class SupabaseConfigTests
{
    [Fact]
    public void NormalizeAndValidateHttpsUrl_HttpsUrl_IsAcceptedAndTrailingSlashTrimmed()
    {
        var result = SupabaseConfig.NormalizeAndValidateHttpsUrl("https://project.supabase.co/");

        Assert.Equal("https://project.supabase.co", result);
    }

    [Fact]
    public void NormalizeAndValidateHttpsUrl_HttpUrl_IsRejected()
    {
        var result = SupabaseConfig.NormalizeAndValidateHttpsUrl("http://project.supabase.co");

        Assert.Null(result);
    }

    [Fact]
    public void NormalizeAndValidateHttpsUrl_NoScheme_IsRejected()
    {
        var result = SupabaseConfig.NormalizeAndValidateHttpsUrl("project.supabase.co");

        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeAndValidateHttpsUrl_MissingValue_ReturnsNull(string? rawUrl)
    {
        Assert.Null(SupabaseConfig.NormalizeAndValidateHttpsUrl(rawUrl));
    }
}
