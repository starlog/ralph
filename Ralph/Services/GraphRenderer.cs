using Ralph.Models;
using Spectre.Console;

namespace Ralph.Services;

public class GraphRenderer
{
    private const int BoxWidth = 18;
    private const int BoxSpacing = 1;
    private const int MaxBoxesPerRow = 6;

    private readonly TaskManager _tm;
    private readonly List<List<string>> _layers;

    public GraphRenderer(TaskManager tm)
    {
        _tm = tm;
        _layers = tm.ComputeTopologicalLayers();
    }

    public void RenderToConsole()
    {
        var total = _tm.Data.Tasks.Count;
        var done = _tm.Data.Tasks.Count(t => t.Done);
        var pending = total - done;

        AnsiConsole.MarkupLine(
            $"[bold]Ralph Task Graph[/] ({total} tasks, [green]{done} done[/], [yellow]{pending} pending[/])");
        AnsiConsole.MarkupLine(new string('\u2550', 80)); // ══════

        if (_layers.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No tasks found.[/]");
            RenderFooter();
            return;
        }

        // Detect sequential chains and merge them
        var rendered = new HashSet<int>();
        for (var i = 0; i < _layers.Count; i++)
        {
            if (rendered.Contains(i)) continue;

            // Check for sequential chain: consecutive single-task layers
            var chainStart = i;
            var chainEnd = i;
            while (chainEnd + 1 < _layers.Count
                   && _layers[chainEnd].Count == 1
                   && _layers[chainEnd + 1].Count == 1
                   && IsDirectDependency(_layers[chainEnd][0], _layers[chainEnd + 1][0]))
            {
                chainEnd++;
            }

            if (chainEnd > chainStart && _layers[chainStart].Count == 1)
            {
                // Render sequential chain horizontally
                var chainIds = new List<string>();
                for (var j = chainStart; j <= chainEnd; j++)
                {
                    chainIds.Add(_layers[j][0]);
                    rendered.Add(j);
                }
                RenderSequentialChain(chainIds, chainStart + 1, chainEnd + 1);

                // Connector to next layer
                if (chainEnd + 1 < _layers.Count)
                {
                    var nextLayerSize = _layers[chainEnd + 1].Count;
                    if (nextLayerSize == 1)
                        RenderSingleConnector(GetLayerWidth(1));
                    else
                        RenderDivergingConnectors(1, nextLayerSize);
                }
            }
            else
            {
                rendered.Add(i);
                var layer = _layers[i];
                RenderBoxRow(layer, i + 1);

                // Connectors to next layer
                if (i + 1 < _layers.Count)
                {
                    var nextIdx = i + 1;
                    while (nextIdx < _layers.Count && rendered.Contains(nextIdx))
                        nextIdx++;

                    if (nextIdx < _layers.Count)
                    {
                        var currentCount = layer.Count;
                        var nextCount = _layers[nextIdx].Count;

                        if (currentCount == 1 && nextCount == 1)
                            RenderSingleConnector(GetLayerWidth(1));
                        else if (currentCount > 1 && nextCount == 1)
                            RenderConvergingConnectors(currentCount);
                        else if (currentCount == 1 && nextCount > 1)
                            RenderDivergingConnectors(1, nextCount);
                        else
                            RenderParallelConnectors(Math.Max(currentCount, nextCount));
                    }
                }
            }
        }

        RenderFooter();
    }

    private void RenderBoxRow(List<string> taskIds, int layerNum)
    {
        var count = Math.Min(taskIds.Count, MaxBoxesPerRow);
        var label = count > 1 ? $"\u00d7{count} parallel" : $"\u00d71";
        AnsiConsole.MarkupLine($"  [bold cyan]Layer {layerNum}[/] [dim]{label}[/]");

        var boxes = taskIds.Take(MaxBoxesPerRow).Select(id => BuildBox(id)).ToList();
        var overflow = taskIds.Count > MaxBoxesPerRow;

        // Render boxes line by line (each box is 4 lines)
        for (var line = 0; line < 4; line++)
        {
            var sb = new System.Text.StringBuilder("  ");
            for (var b = 0; b < boxes.Count; b++)
            {
                if (b > 0) sb.Append(new string(' ', BoxSpacing));
                sb.Append(boxes[b][line]);
            }
            AnsiConsole.MarkupLine(sb.ToString());
        }

        if (overflow)
            AnsiConsole.MarkupLine($"  [dim]... +{taskIds.Count - MaxBoxesPerRow} more tasks[/]");
    }

    private void RenderSequentialChain(List<string> taskIds, int layerStart, int layerEnd)
    {
        var label = layerStart == layerEnd
            ? $"Layer {layerStart}"
            : $"Layers {layerStart}-{layerEnd}";

        AnsiConsole.MarkupLine(
            $"  [bold cyan]{label}[/] [dim]sequential chain \u00d7{taskIds.Count}[/]");

        // Box width for chain = 16 (slightly narrower to fit arrows)
        const int chainBoxWidth = 16;
        const string arrow = " \u2500\u25ba ";

        // Check how many fit in ~80 cols
        var perBox = chainBoxWidth + arrow.Length;
        var maxPerLine = Math.Max(1, (78 - 2) / perBox);

        for (var chunk = 0; chunk < taskIds.Count; chunk += maxPerLine)
        {
            var slice = taskIds.Skip(chunk).Take(maxPerLine).ToList();
            var chainBoxes = slice.Select(id => BuildBox(id, chainBoxWidth)).ToList();

            for (var line = 0; line < 4; line++)
            {
                var sb = new System.Text.StringBuilder("  ");
                for (var b = 0; b < chainBoxes.Count; b++)
                {
                    if (b > 0)
                    {
                        // Arrow only on middle lines (line 1 or 2)
                        sb.Append(line == 1 || line == 2 ? " [yellow]\u2500\u25ba[/] " : "    ");
                    }
                    sb.Append(chainBoxes[b][line]);
                }
                AnsiConsole.MarkupLine(sb.ToString());
            }

            // If more chunks follow, render continuation
            if (chunk + maxPerLine < taskIds.Count)
            {
                AnsiConsole.MarkupLine("  [dim]     \u2502[/]");
            }
        }
    }

    /// <summary>단일 수직 커넥터</summary>
    private void RenderSingleConnector(int totalWidth)
    {
        var pad = totalWidth / 2 + 1;
        AnsiConsole.MarkupLine($"{new string(' ', pad)}[dim]\u2502[/]");
    }

    /// <summary>병렬 수직 커넥터 (N개)</summary>
    private void RenderParallelConnectors(int count)
    {
        count = Math.Min(count, MaxBoxesPerRow);
        var sb = new System.Text.StringBuilder("  ");
        for (var i = 0; i < count; i++)
        {
            var mid = BoxWidth / 2;
            sb.Append(new string(' ', mid));
            sb.Append('\u2502'); // │
            sb.Append(new string(' ', BoxWidth - mid - 1 + BoxSpacing));
        }
        AnsiConsole.MarkupLine($"[dim]{sb}[/]");
    }

    /// <summary>수렴 커넥터: N개 → 1개</summary>
    private void RenderConvergingConnectors(int sourceCount)
    {
        sourceCount = Math.Min(sourceCount, MaxBoxesPerRow);
        var cellWidth = BoxWidth + BoxSpacing;
        var totalWidth = cellWidth * sourceCount - BoxSpacing;
        var mid = totalWidth / 2;

        // Line 1: └──┴──┬──┴──┘
        var sb = new System.Text.StringBuilder("  ");
        for (var i = 0; i < totalWidth; i++)
        {
            if (i == 0)
                sb.Append('\u2514'); // └
            else if (i == totalWidth - 1)
                sb.Append('\u2518'); // ┘
            else if (i == mid)
                sb.Append('\u252C'); // ┬
            else
            {
                // Check if this position is a box center
                var isBoxCenter = false;
                for (var b = 0; b < sourceCount; b++)
                {
                    var boxCenter = b * cellWidth + BoxWidth / 2;
                    if (i == boxCenter) { isBoxCenter = true; break; }
                }
                sb.Append(isBoxCenter ? '\u2534' : '\u2500'); // ┴ or ─
            }
        }
        AnsiConsole.MarkupLine($"[dim]{sb}[/]");

        // Line 2: center │
        AnsiConsole.MarkupLine($"[dim]{new string(' ', mid + 2)}\u2502[/]");
    }

    /// <summary>발산 커넥터: 1개 → N개</summary>
    private void RenderDivergingConnectors(int sourceCount, int targetCount)
    {
        targetCount = Math.Min(targetCount, MaxBoxesPerRow);
        var cellWidth = BoxWidth + BoxSpacing;
        var totalWidth = cellWidth * targetCount - BoxSpacing;
        var mid = totalWidth / 2;

        // Center │ from source
        AnsiConsole.MarkupLine($"[dim]{new string(' ', mid + 2)}\u2502[/]");

        // Line: ┌──┬──┴──┬──┐
        var sb = new System.Text.StringBuilder("  ");
        for (var i = 0; i < totalWidth; i++)
        {
            if (i == 0)
                sb.Append('\u250C'); // ┌
            else if (i == totalWidth - 1)
                sb.Append('\u2510'); // ┐
            else if (i == mid)
                sb.Append('\u2534'); // ┴
            else
            {
                var isBoxCenter = false;
                for (var b = 0; b < targetCount; b++)
                {
                    var boxCenter = b * cellWidth + BoxWidth / 2;
                    if (i == boxCenter) { isBoxCenter = true; break; }
                }
                sb.Append(isBoxCenter ? '\u252C' : '\u2500'); // ┬ or ─
            }
        }
        AnsiConsole.MarkupLine($"[dim]{sb}[/]");
    }

    private void RenderFooter()
    {
        AnsiConsole.MarkupLine(new string('\u2550', 80));
        AnsiConsole.MarkupLine(
            "[dim]Legend:[/] [green][[\u2713]][/] done  [[ ]] pending  [dim]\u2502[/] parallel  [yellow]\u2500\u25ba[/] sequential chain");
    }

    /// <summary>박스 4줄 생성 (Spectre Markup 포함)</summary>
    private string[] BuildBox(string taskId, int width = BoxWidth)
    {
        var task = _tm.GetTask(taskId);
        var done = task?.Done ?? false;
        var status = done ? "[green][\u2713][/]" : "[ ]";
        var statusRaw = done ? "[v]" : "[ ]";
        var category = task?.Category ?? task?.Phase ?? "";

        var innerWidth = width - 2; // minus borders
        var idDisplay = Truncate($"{statusRaw} {taskId}", innerWidth);
        var catDisplay = Truncate($"    {category}", innerWidth);

        // Markup versions
        var idMarkup = done
            ? $"[green]{Markup.Escape(Truncate($"[\u2713] {taskId}", innerWidth))}[/]"
            : $"{Markup.Escape(Truncate("[ ] " + taskId, innerWidth))}";
        var catMarkup = $"[dim]{Markup.Escape(catDisplay)}[/]";

        var top = $"\u250C{new string('\u2500', innerWidth)}\u2510";
        var idLine = $"\u2502{idMarkup}{PadMarkup(idDisplay, innerWidth)}\u2502";
        var catLine = $"\u2502{catMarkup}{PadMarkup(catDisplay, innerWidth)}\u2502";
        var connectorPos = innerWidth / 2;
        var bottom = $"\u2514{new string('\u2500', connectorPos)}\u252C{new string('\u2500', innerWidth - connectorPos - 1)}\u2518";

        return [top, idLine, catLine, bottom];
    }

    /// <summary>Markup을 포함한 문자열의 패딩 계산 (실제 표시 너비 기준)</summary>
    private static string PadMarkup(string rawText, int totalWidth)
    {
        var visibleLen = rawText.Length;
        var pad = totalWidth - visibleLen;
        return pad > 0 ? new string(' ', pad) : "";
    }

    private static string Truncate(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        return maxLen > 2 ? text[..(maxLen - 2)] + ".." : text[..maxLen];
    }

    private static int GetLayerWidth(int count)
    {
        return (BoxWidth + BoxSpacing) * count - BoxSpacing;
    }

    /// <summary>taskA가 taskB의 직접 의존성인지 확인</summary>
    private bool IsDirectDependency(string taskA, string taskB)
    {
        var taskBItem = _tm.GetTask(taskB);
        return taskBItem?.DependsOn is { Count: > 0 } && taskBItem.DependsOn.Contains(taskA);
    }
}
