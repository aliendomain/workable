using System.Text;
using System.Text.Json;

namespace Workable;

/// <summary>
/// Represents a captured worker profile tree.
/// </summary>
/// <param name="Root">The root node of the captured profile tree.</param>
/// <param name="StartedAt">The time the profile capture began.</param>
/// <param name="CapturedAt">The time the profile snapshot was captured.</param>
public sealed record WorkProfileSnapshot(
    WorkProfileSnapshotNode Root,
    DateTimeOffset StartedAt,
    DateTimeOffset CapturedAt)
{
    private const int DefaultMaximumRenderedNodes = 10_000;
    private const int DefaultMaximumRenderedDepth = 256;
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Renders the captured profile tree as an ASCII timeline.
    /// </summary>
    /// <returns>The rendered ASCII tree.</returns>
    public string ToAsciiTree()
        => this.ToAsciiTree(DefaultMaximumRenderedNodes, DefaultMaximumRenderedDepth);

    /// <summary>
    /// Renders the captured profile tree as a bounded ASCII timeline.
    /// </summary>
    /// <param name="maximumNodes">The maximum number of profile nodes, including the root, to render.</param>
    /// <param name="maximumDepth">The maximum child depth to render.</param>
    /// <returns>The rendered ASCII tree.</returns>
    public string ToAsciiTree(int maximumNodes, int maximumDepth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumNodes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDepth, 1);

        var builder = new StringBuilder();
        var entries = BuildRenderEntries(this.Root, maximumNodes, maximumDepth, out var truncated);
        var widths = GutterWidths.For(this.Root, entries);
        AppendHeader(builder, widths);
        AppendGutter(builder, this.Root, widths);

        builder.Append(this.Root.Label)
            .AppendLine();

        foreach (var entry in entries)
        {
            AppendScope(builder, entry.Node, entry.Prefix, entry.IsLast, widths);

            if (entry.Node is { MetricType: not WorkProfileMetricType.MethodScope, Context: not null and not string })
            {
                var nextPrefix = entry.Prefix + (entry.IsLast ? "   " : "│  ");
                var childPrefix =
                    (entry.Node.MetricType == WorkProfileMetricType.Scope ? nextPrefix : entry.Prefix) +
                    (entry.IsLast && entry.Node.Children.Count == 0 ? "   " : "│  ");
                var jsonPrefix = new string(' ', widths.Total + 1) + childPrefix;
                AppendJsonBlock(builder, entry.Node.Context, jsonPrefix);
            }
        }

        if (truncated)
        {
            builder.AppendLine("… profile rendering truncated …");
        }

        return builder.ToString();
    }

    private static IReadOnlyList<RenderEntry> BuildRenderEntries(
        WorkProfileSnapshotNode root,
        int maximumNodes,
        int maximumDepth,
        out bool truncated)
    {
        var entries = new List<RenderEntry>(Math.Min(maximumNodes - 1, root.Children.Count));
        var pending = new Stack<PendingRenderEntry>();
        PushChildren(pending, root, string.Empty, depth: 1);
        truncated = false;
        while (pending.TryPop(out var entry))
        {
            if (entries.Count >= maximumNodes - 1)
            {
                truncated = true;
                break;
            }

            entries.Add(new RenderEntry(entry.Node, entry.Prefix, entry.IsLast));
            if (entry.Node.Children.Count == 0)
            {
                continue;
            }

            if (entry.Depth >= maximumDepth)
            {
                truncated = true;
                continue;
            }

            var nextPrefix = entry.Prefix + (entry.IsLast ? "   " : "│  ");
            PushChildren(pending, entry.Node, nextPrefix, entry.Depth + 1);
        }

        return entries;
    }

    private static void PushChildren(
        Stack<PendingRenderEntry> pending,
        WorkProfileSnapshotNode parent,
        string prefix,
        int depth)
    {
        for (var index = parent.Children.Count - 1; index >= 0; index--)
        {
            pending.Push(new PendingRenderEntry(
                parent.Children[index],
                prefix,
                IsLast: index == parent.Children.Count - 1,
                depth));
        }
    }

    private static void AppendScope(
        StringBuilder builder,
        WorkProfileSnapshotNode node,
        string prefix,
        bool isLast,
        GutterWidths widths)
    {
        AppendGutter(builder, node, widths);

        builder.Append(prefix)
            .Append(isLast ? "└─ " : "├─ ")
            .Append(node.Label)
            .AppendLine();
    }

    private static void AppendGutter(
        StringBuilder builder,
        WorkProfileSnapshotNode node,
        GutterWidths widths)
    {
        if (node.Context is not null and not string)
        {
            builder.Append(new string(' ', widths.Total + 1));
            return;
        }

        builder.Append($"{node.TreeMilliseconds}ms".PadLeft(widths.TreeMilliseconds))
            .Append(" / ")
            .Append($"{node.NodeMilliseconds}ms".PadLeft(widths.NodeMilliseconds))
            .Append(' ');
    }

    private static void AppendHeader(StringBuilder builder, GutterWidths widths)
    {
        builder.Append("Tree".PadLeft(widths.TreeMilliseconds))
            .Append(" / ")
            .Append("Node".PadLeft(widths.NodeMilliseconds))
            .AppendLine();

        builder.Append(new string('-', widths.Total))
            .AppendLine();
    }

    private static void AppendJsonBlock(StringBuilder builder, object data, string prefix)
    {
        var json = JsonSerializer.Serialize(data, JsonSerializerOptions);
        using var reader = new StringReader(json);
        while (reader.ReadLine() is { } line)
        {
            builder.Append(prefix)
                .AppendLine(line);
        }
    }

    private sealed class GutterWidths(int treeMilliseconds, int nodeMilliseconds)
    {
        public int TreeMilliseconds { get; private set; } = treeMilliseconds;

        public int NodeMilliseconds { get; private set; } = nodeMilliseconds;

        public int Total => this.TreeMilliseconds + this.NodeMilliseconds + 3;

        public static GutterWidths For(WorkProfileSnapshotNode root, IReadOnlyList<RenderEntry> entries)
        {
            var widths = new GutterWidths(
                $"{root.TreeMilliseconds}ms".Length,
                $"{root.NodeMilliseconds}ms".Length);
            widths.Include(root);
            foreach (var entry in entries)
            {
                widths.Include(entry.Node);
            }

            return widths;
        }

        private void Include(WorkProfileSnapshotNode node)
        {
            this.TreeMilliseconds = Math.Max(this.TreeMilliseconds, $"{node.TreeMilliseconds}ms".Length);
            this.NodeMilliseconds = Math.Max(this.NodeMilliseconds, $"{node.NodeMilliseconds}ms".Length);

        }
    }

    private readonly record struct PendingRenderEntry(
        WorkProfileSnapshotNode Node,
        string Prefix,
        bool IsLast,
        int Depth);

    private readonly record struct RenderEntry(
        WorkProfileSnapshotNode Node,
        string Prefix,
        bool IsLast);
}
