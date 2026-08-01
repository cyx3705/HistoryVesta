namespace GitHubConnection;

internal static class GitHubRuntime
{
    public static GitHubConnectionService CreateService()
    {
        var resolver = new RepositoryPathResolver();
        return new GitHubConnectionService(resolver.Resolve);
    }
}
