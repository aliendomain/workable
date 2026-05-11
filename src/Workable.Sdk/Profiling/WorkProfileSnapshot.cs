using System.Text;
using System.Text.Json;

namespace Workable;

public sealed record WorkProfileSnapshot(
    WorkProfileSnapshotNode Root,
    DateTimeOffset StartedAt,
    DateTimeOffset CapturedAt)
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = true,
    };

    public string ToAsciiTree()
    {
        var builder = new StringBuilder();
        var widths = GutterWidths.For(this.Root);
        AppendHeader(builder, widths);
        AppendGutter(builder, this.Root, widths);

        builder.Append(this.Root.Label)
            .AppendLine();

        AppendEntries(builder, this.Root, string.Empty, widths);

        return builder.ToString();
    }

    private static void AppendEntries(
        StringBuilder builder,
        WorkProfileSnapshotNode node,
        string prefix,
        GutterWidths widths)
    {
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            var isLast = i == node.Children.Count - 1;
            var nextPrefix = prefix + (isLast ? "   " : "│  ");

            AppendScope(builder, child, prefix, isLast, widths);

            if (child is { MetricType: not WorkProfileMetricType.MethodScope, Context: not null and not string })
            {
                var childPrefix =
                    (child.MetricType == WorkProfileMetricType.Scope ? nextPrefix : prefix) +
                    (isLast && child.Children.Count == 0 ? "   " : "│  ");
                var jsonPrefix = new string(' ', widths.Total + 1) + childPrefix;
                AppendJsonBlock(builder, child.Context, jsonPrefix);
            }

            AppendEntries(builder, child, nextPrefix, widths);
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

        public static GutterWidths For(WorkProfileSnapshotNode root)
        {
            var widths = new GutterWidths(
                $"{root.TreeMilliseconds}ms".Length,
                $"{root.NodeMilliseconds}ms".Length);
            widths.Include(root);
            return widths;
        }

        private void Include(WorkProfileSnapshotNode node)
        {
            this.TreeMilliseconds = Math.Max(this.TreeMilliseconds, $"{node.TreeMilliseconds}ms".Length);
            this.NodeMilliseconds = Math.Max(this.NodeMilliseconds, $"{node.NodeMilliseconds}ms".Length);

            foreach (var child in node.Children)
            {
                this.Include(child);
            }
        }
    }
}
