namespace Workable;

public interface IDotNetWorkOriginProvider
{
    WorkOrigin CreateOrigin(string description);
}
