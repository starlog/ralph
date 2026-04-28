using Ralph.Services;
using Xunit;

namespace Ralph.Tests;

public class NotificationServiceTests
{
    private static NotificationContext SampleCtx(bool success = true) =>
        new(success, "20260428-094200", 12, 12, 0, 2730.0, 3.21, "test-host");

    // ─── DetectFormat ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://hooks.slack.com/services/T123/B456/abcdef", "slack")]
    [InlineData("https://team.slack.com/api/webhooks/...", "slack")]
    [InlineData("https://discord.com/api/webhooks/123/abc", "discord")]
    [InlineData("https://discordapp.com/api/webhooks/123/abc", "discord")]
    [InlineData("https://canary.discord.com/api/webhooks/...", "discord")]
    [InlineData("https://example.com/hook", "generic")]
    [InlineData("https://api.example.com/webhook", "generic")]
    [InlineData("not-a-url", "generic")]
    public void DetectFormat_by_hostname(string url, string expected)
    {
        Assert.Equal(expected, NotificationService.DetectFormat(url, null));
    }

    [Theory]
    [InlineData("slack")]
    [InlineData("Slack")]   // case-insensitive
    [InlineData("DISCORD")]
    [InlineData("generic")]
    public void DetectFormat_explicit_override_wins(string explicitFormat)
    {
        // hostname이 slack인 URL이지만 explicit이 우선 적용
        var detected = NotificationService.DetectFormat("https://hooks.slack.com/x", explicitFormat);
        Assert.Equal(explicitFormat.ToLowerInvariant(), detected);
    }

    [Theory]
    [InlineData("invalid-format-name")]
    [InlineData("")]
    [InlineData("  ")]
    public void DetectFormat_invalid_explicit_falls_back_to_hostname_detection(string explicitFormat)
    {
        // explicit이 알 수 없는 값이면 무시 → hostname 감지 적용
        var detected = NotificationService.DetectFormat("https://hooks.slack.com/x", explicitFormat);
        Assert.Equal("slack", detected);
    }

    // ─── BuildPayload — generic ──────────────────────────────────────────────

    [Fact]
    public void Generic_payload_includes_event_and_stats()
    {
        var p = NotificationService.BuildPayload("generic", SampleCtx());
        Assert.Equal("session_complete", p["event"]!.GetValue<string>());
        Assert.Equal("20260428-094200", p["session"]!.GetValue<string>());
        Assert.True(p["success"]!.GetValue<bool>());
        Assert.Equal(12, p["totalTasks"]!.GetValue<int>());
        Assert.Equal(3.21, p["estimatedCostUsd"]!.GetValue<double>(), 4);
    }

    [Fact]
    public void Generic_failure_uses_session_failed_event()
    {
        var p = NotificationService.BuildPayload("generic", SampleCtx(success: false));
        Assert.Equal("session_failed", p["event"]!.GetValue<string>());
        Assert.False(p["success"]!.GetValue<bool>());
    }

    // ─── BuildPayload — Slack ────────────────────────────────────────────────

    [Fact]
    public void Slack_payload_has_text_and_blocks()
    {
        var p = NotificationService.BuildPayload("slack", SampleCtx());

        var text = p["text"]?.GetValue<string>();
        Assert.NotNull(text);
        Assert.Contains("Ralph session", text);
        Assert.Contains("12/12", text);

        var blocks = p["blocks"];
        Assert.NotNull(blocks);
        Assert.True(blocks!.AsArray().Count > 0, "blocks 배열이 비어있음");
        var first = blocks.AsArray()[0]!.AsObject();
        Assert.Equal("section", first["type"]!.GetValue<string>());
        Assert.Equal("mrkdwn", first["text"]!["type"]!.GetValue<string>());
    }

    // ─── BuildPayload — Discord ──────────────────────────────────────────────

    [Fact]
    public void Discord_payload_has_content_and_embeds_with_color()
    {
        var p = NotificationService.BuildPayload("discord", SampleCtx());

        var content = p["content"]?.GetValue<string>();
        Assert.NotNull(content);
        Assert.Contains("Ralph session", content);

        var embeds = p["embeds"];
        Assert.NotNull(embeds);
        var first = embeds!.AsArray()[0]!.AsObject();
        Assert.Equal(3066993, first["color"]!.GetValue<int>()); // green
        Assert.Contains("12/12", first["description"]!.GetValue<string>());
    }

    [Fact]
    public void Discord_failure_uses_red_color()
    {
        var p = NotificationService.BuildPayload("discord", SampleCtx(success: false));
        var first = p["embeds"]!.AsArray()[0]!.AsObject();
        Assert.Equal(15158332, first["color"]!.GetValue<int>()); // red
    }

    // ─── 통합: payload는 valid JSON으로 직렬화되어야 함 ─────────────────────

    [Theory]
    [InlineData("generic")]
    [InlineData("slack")]
    [InlineData("discord")]
    public void Payload_serializes_to_valid_json(string format)
    {
        var p = NotificationService.BuildPayload(format, SampleCtx());
        var json = p.ToJsonString();

        // 다시 파싱되는지 (round-trip)
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
    }
}
