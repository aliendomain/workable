namespace Workable;

internal static class WorkableHttpRouteBinding
{
    public static bool TryParseAction(string value, out WorkAction action)
        => Enum.TryParse(value, ignoreCase: true, out action);

    public static bool TryParseWorkflowAction(string value, out WorkflowAction action)
        => Enum.TryParse(value, ignoreCase: true, out action);
}
