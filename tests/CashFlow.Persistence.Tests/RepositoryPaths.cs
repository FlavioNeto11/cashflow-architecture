namespace CashFlow.Persistence.Tests;

internal static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("global.json não encontrado acima de " + AppContext.BaseDirectory);
    }
}
