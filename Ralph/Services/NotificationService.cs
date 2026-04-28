using System.Net.Http.Json;
using System.Text.Json;
using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

/// <summary>
/// Ralph 세션 종료 시점에 webhook으로 결과 알림을 전송합니다.
/// 우선순위:
///   1. workflow.notifications.onComplete / onFailure (tasks.json 설정)
///   2. RALPH_WEBHOOK_URL 환경변수 (전역 fallback)
/// 둘 다 없으면 noop.
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

        var payload = new
        {
            @event = success ? "session_complete" : "session_failed",
            session = sessionId,
            success,
            totalTasks,
            completedTasks,
            failedTasks,
            durationSec,
            estimatedCostUsd,
            host = Environment.MachineName,
            timestamp = DateTime.UtcNow.ToString("o"),
        };

        try
        {
            var response = await HttpClient.PostAsJsonAsync(url, payload, ct);
            if (response.IsSuccessStatusCode)
            {
                logger?.Info($"Notification sent: {url} (status: {(int)response.StatusCode})");
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger?.Warn($"Notification webhook returned {(int)response.StatusCode}: {body.Trim()}");
                AnsiConsole.MarkupLine($"[yellow]알림 webhook 응답: {(int)response.StatusCode}[/]");
            }
        }
        catch (Exception ex)
        {
            logger?.Warn($"Notification webhook failed: {ex.Message}");
            AnsiConsole.MarkupLine($"[yellow]알림 전송 실패: {Markup.Escape(ex.Message)}[/]");
        }
    }
}
