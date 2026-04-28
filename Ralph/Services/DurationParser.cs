using System.Globalization;

namespace Ralph.Services;

/// <summary>
/// "30m" / "1h" / "90s" / "1800" 같은 duration 문자열을 초로 파싱합니다.
/// 단순 정수만 있으면 그대로 초로 해석. 음수/0/잘못된 형식은 false 반환.
/// </summary>
public static class DurationParser
{
    public static bool TryParseSeconds(string? input, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim().ToLowerInvariant();
        if (s.Length == 0) return false;
        // 내부 공백 거부 ("30 m" → false). Trim 이후 internal whitespace는 의도치 않은 입력.
        foreach (var c in s) if (char.IsWhiteSpace(c)) return false;

        var lastChar = s[^1];
        int multiplier;
        string numPart;
        if (char.IsDigit(lastChar))
        {
            multiplier = 1;
            numPart = s;
        }
        else
        {
            multiplier = lastChar switch
            {
                's' => 1,
                'm' => 60,
                'h' => 3600,
                _ => 0,
            };
            if (multiplier == 0) return false;
            numPart = s[..^1];
        }

        if (!int.TryParse(numPart,
                NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var n) || n <= 0)
            return false;

        var total = (long)n * multiplier;
        if (total <= 0 || total > int.MaxValue) return false;
        seconds = (int)total;
        return true;
    }
}
