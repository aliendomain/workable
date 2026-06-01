namespace Workable.Tests;

internal sealed class SqlServerCliTestWorkspace : IDisposable
{
    private SqlServerCliTestWorkspace(string root)
    {
        this.Root = root;
    }

    public string Root { get; }

    public static SqlServerCliTestWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "WorkableSqlServerCliTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new SqlServerCliTestWorkspace(root);
    }

    public string WriteProject(string relativePath)
    {
        return this.WriteFile(relativePath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
    }

    public string WriteSolution(string content)
    {
        return this.WriteFile("Workable.slnx", content);
    }

    public string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(this.Root, relativePath);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Could not resolve directory for '{path}'.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(this.Root))
        {
            Directory.Delete(this.Root, recursive: true);
        }
    }
}
