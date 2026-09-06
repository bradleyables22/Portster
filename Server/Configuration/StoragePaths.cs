namespace Portster;

public sealed record StoragePaths(string GlobalProfiles, string? ProjectProfiles, string Audit)
{
    public static StoragePaths FromEnvironment()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("PORTSTER_DATA");
        var root = string.IsNullOrWhiteSpace(configuredRoot) ?
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Portster") : Path.GetFullPath(configuredRoot);
        var project = Environment.GetEnvironmentVariable("PORTSTER_PROJECT");
        return new(Path.Combine(root, "profiles"), string.IsNullOrWhiteSpace(project) ? null :
            Path.Combine(Path.GetFullPath(project), ".portster", "profiles"), Path.Combine(root, "audit"));
    }

    public string Profiles(ProfileScope scope) => scope switch
    {
        ProfileScope.Global => GlobalProfiles,
        ProfileScope.Project => ProjectProfiles ?? throw new PortsterException("PROJECT_SCOPE_UNAVAILABLE", "Set PORTSTER_PROJECT to an explicit project directory before using project profiles."),
        _ => throw new PortsterException("INVALID_SCOPE", "Scope must be Global or Project.")
    };
}
