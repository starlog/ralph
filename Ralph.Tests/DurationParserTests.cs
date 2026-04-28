using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

public class DurationParserTests
{
    [Theory]
    [InlineData("90", 90)]
    [InlineData("1800", 1800)]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("1h", 3600)]
    [InlineData("2h", 7200)]
    [InlineData("  30m  ", 1800)]    // whitespace
    [InlineData("30M", 1800)]        // uppercase
    [InlineData("1H", 3600)]
    public void Parses_valid_durations(string input, int expected)
    {
        Assert.True(DurationParser.TryParseSeconds(input, out var s));
        Assert.Equal(expected, s);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]               // 양수 아님
    [InlineData("-30")]             // 음수
    [InlineData("30x")]             // unknown unit
    [InlineData("m30")]             // unit 앞에
    [InlineData("30 m")]            // 공백 있음 (단위와 숫자 사이)
    [InlineData("3.5h")]             // 소수 미지원
    public void Rejects_invalid(string? input)
    {
        Assert.False(DurationParser.TryParseSeconds(input, out var s));
        Assert.Equal(0, s);
    }
}
