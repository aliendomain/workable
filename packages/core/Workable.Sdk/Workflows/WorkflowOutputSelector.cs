using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workable;

/// <summary>
/// Represents one generated selector against a workflow step's retained output.
/// </summary>
/// <param name="JsonPointer">The generated JSON pointer persisted for replay and recovery. When omitted, the root output value is selected.</param>
public sealed record WorkflowOutputSelector(string? JsonPointer)
{
    /// <summary>
    /// Creates one selector from a typed output-path expression.
    /// </summary>
    public static WorkflowOutputSelector Create<TSource, TResult>(Expression<Func<TSource, TResult>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new WorkflowOutputSelector(Translate(selector));
    }

    private static string? Translate<TSource, TResult>(Expression<Func<TSource, TResult>> selector)
    {
        var segments = new Stack<string>();
        var current = StripConvert(selector.Body);
        while (true)
        {
            current = StripConvert(current);
            switch (current)
            {
                case ParameterExpression:
                    return segments.Count == 0
                        ? null
                        : $"/{string.Join("/", segments.Select(EscapeJsonPointerSegment))}";
                case MemberExpression memberExpression:
                    segments.Push(ResolveJsonPropertyName(memberExpression.Member));
                    current = memberExpression.Expression
                        ?? throw new NotSupportedException("Workflow selector members must be rooted in the source output.");
                    break;
                default:
                    throw new NotSupportedException(
                        $"Workflow selector '{selector}' is not supported. Use a simple property path like 'output => output.Items'.");
            }
        }
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression unary &&
            (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static string ResolveJsonPropertyName(MemberInfo member)
    {
        var attribute = member.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (!string.IsNullOrWhiteSpace(attribute?.Name))
        {
            return attribute.Name;
        }

        return JsonNamingPolicy.CamelCase.ConvertName(member.Name);
    }

    private static string EscapeJsonPointerSegment(string segment)
        => segment
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
}
