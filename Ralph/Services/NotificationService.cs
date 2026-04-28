using System.Text;
using System.Text.Json.Nodes;
using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// Ralph 세션 종료 시점에 webhook으로 결과 알림을 전송합니다.
/// 우선순위:
///   1. workflow.notifications.onComplete / onFailure (tasks.json 설정)
///   2. RALPH_WEBHOOK_URL 환경변수 (전역 fallback)
/// 둘 다 없으면 noop.
///
/// 페이로드 포맷은 hostname으로 자동 감지하거나 notifications.format으로 명시 지정.
/// generic = Ralph 구조화 JSON, slack = {text, blocks}, discord = {content, embeds}.
/// </summary>
public class NotificationService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task NotifyAsync(
        bool success,
        string sessionId,
        int totalTasks,
        int completedTasks,
        int failedTasks,
        double durationSec,
        double estimatedCostUsd,
        NotificationSettings? settings,
        RalphLogger? logger = null,
        CancellationToken ct = default)
    {
        var url = success
            ? settings?.OnComplete ?? Environment.GetEnvironmentVariable("RALPH_WEBHOOK_URL")
            : settings?.OnFailure ?? settings?.OnComplete ?? Environment.GetEnvironmentVariable("RALPH_WEBHOOK_URL");

        if (string.IsNullOrWhiteSpace(url))
            return; // 설정 없음 — 조용히 종료

        var format = DetectFormat(url, settings?.Format);
        var ctx = new NotificationContext(
            success, sessionId, totalTasks, completedTasks, failedTasks,
            durationSec, estimatedCostUsd, Environment.MachineName);
        var payload = BuildPayload(format, ctx);

        try
        {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(url, content, ct);
            if (response.IsSuccessStatusCode)
            {
                logger?.Info($"Notification sent: {url} (format: {format}, status: {(int)response.StatusCode})");
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger?.Warn($"Notification webhook returned {(int)response.StatusCode} (format: {format}): {body.Trim()}");
                AnsiConsole.MarkupLine(
                    $"[yellow]알림 webhook 응답: {(int)response.StatusCode} (format: {format})[/]");
            }
        }
        catch (Exception ex)
        {
            logger?.Warn($"Notification webhook failed: {ex.Message}");
            AnsiConsole.MarkupLine($"[yellow]알림 전송 실패: {Markup.Escape(ex.Message)}[/]");
        }
    }

    /// <summary>
    /// 명시 format이 있으면 그 값을 lowercase로 반환. 없으면 URL hostname으로 추정.
    /// 알 수 없는 호스트는 "generic".
    /// </summary>
    public static string DetectFormat(string url, string? explicitFormat)
    {
        if (!string.IsNullOrWhiteSpace(explicitFormat))
        {
            var ef = explicitFormat.Trim().ToLowerInvariant();
            if (ef is "slack" or "discord" or "generic") return ef;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var host = u.Host.ToLowerInvariant();
            if (host == "hooks.slack.com" || host.EndsWith(".slack.com"))
                return "slack";
            if (host == "discord.com" || host.EndsWith(".discord.com")
                || host == "discordapp.com" || host.EndsWith(".discordapp.com"))
                return "discord";
        }
        return "generic";
    }

    public static JsonObject BuildPayload(string format, NotificationContext ctx)
    {
        return format switch
        {
            "slack" => BuildSlack(ctx),
            "discord" => BuildDiscord(ctx),
            _ => BuildGeneric(ctx),
        };
    }

    private static JsonObject BuildGeneric(NotificationContext c) => new()
    {
        ["event"] = c.Success ? "session_complete" : "session_failed",
        ["session"] = c.SessionId,
        ["success"] = c.Success,
        ["totalTasks"] = c.TotalTasks,
        ["completedTasks"] = c.CompletedTasks,
        ["failedTasks"] = c.FailedTasks,
        ["durationSec"] = c.DurationSec,
        ["estimatedCostUsd"] = c.EstimatedCostUsd,
        ["host"] = c.Host,
        ["timestamp"] = DateTime.UtcNow.ToString("o"),
    };

    private static JsonObject BuildSlack(NotificationContext c)
    {
        var emoji = c.Success ? ":white_check_mark:" : ":x:";
        var status = c.Success ? "complete" : "failed";
        var summary = $"{emoji} Ralph session {status} — {c.CompletedTasks}/{c.TotalTasks} tasks · {FormatDuration(c.DurationSec)} · ${c.EstimatedCostUsd:F2}";

        var details = $"*session* `{c.SessionId}` · *host* `{c.Host}`";

        return new JsonObject
        {
            // text는 fallback notification text (Slack mobile push 등에서 사용됨)
            ["text"] = summary,
            ["blocks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "section",
                    ["text"] = new JsonObject
                    {
                        ["type"] = "mrkdwn",
                        ["text"] = $"*{summary}*\n{details}",
                    },
                },
            },
        };
    }

    private static JsonObject BuildDiscord(NotificationContext c)
    {
        var emoji = c.Success ? "✅" : "❌";
        var status = c.Success ? "complete" : "failed";
        var summary = $"{emoji} Ralph session {status}";
        var description = $"**{c.CompletedTasks}/{c.TotalTasks}** tasks · **{FormatDuration(c.DurationSec)}** · **${c.EstimatedCostUsd:F2}**";

        return new JsonObject
        {
            ["content"] = $"{emoji} Ralph session `{c.SessionId}` {status} — {c.CompletedTasks}/{c.TotalTasks} tasks · {FormatDuration(c.DurationSec)} · ${c.EstimatedCostUsd:F2}",
            ["embeds"] = new JsonArray
            {
                new JsonObject
                {
                    ["title"] = summary,
                    ["description"] = description,
                    ["color"] = c.Success ? 3066993 /* green */ : 15158332 /* red */,
                    ["footer"] = new JsonObject { ["text"] = $"session: {c.SessionId} · host: {c.Host}" },
                    ["timestamp"] = DateTime.UtcNow.ToString("o"),
                },
            },
        };
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:F0}s";
        var totalMin = seconds / 60;
        if (totalMin < 60) return $"{(int)totalMin}m {(int)(seconds % 60)}s";
        var hours = (int)(totalMin / 60);
        var mins = (int)(totalMin % 60);
        return $"{hours}h {mins}m";
    }
}

public sealed record NotificationContext(
    bool Success,
    string SessionId,
    int TotalTasks,
    int CompletedTasks,
    int FailedTasks,
    double DurationSec,
    double EstimatedCostUsd,
    string Host);
