using System;
using enki.Localization;
using Xunit;

namespace enki.Tests.Localization;

[Collection("LocalizationMutatingTests")]
public class RelativeTimeFormatterTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Format_WalksThroughEachBucketAndPluralizes()
    {
        var original = LocalizationService.Instance.CurrentCulture.Name;
        try
        {
            LocalizationService.Instance.SetLanguage("en-US");

            Assert.Equal("just now", RelativeTimeFormatter.Format(Now.AddSeconds(-30), Now));
            Assert.Equal("5 min ago", RelativeTimeFormatter.Format(Now.AddMinutes(-5), Now));
            Assert.Equal("1 hour ago", RelativeTimeFormatter.Format(Now.AddHours(-1), Now));
            Assert.Equal("3 hours ago", RelativeTimeFormatter.Format(Now.AddHours(-3), Now));
            Assert.Equal("1 day ago", RelativeTimeFormatter.Format(Now.AddDays(-1), Now));
            Assert.Equal("2 days ago", RelativeTimeFormatter.Format(Now.AddDays(-2), Now));
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(original);
        }
    }

    [Fact]
    public void Format_OlderThanAWeek_FallsBackToAFormattedDateInsteadOfAgoText()
    {
        var original = LocalizationService.Instance.CurrentCulture.Name;
        try
        {
            LocalizationService.Instance.SetLanguage("en-US");

            var result = RelativeTimeFormatter.Format(Now.AddDays(-8), Now);

            Assert.DoesNotContain("ago", result);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(original);
        }
    }
}
