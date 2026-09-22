using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FakeSqlCompare;

public sealed class SavedEndpoint
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Auth { get; set; } = "windows";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class SavedProject : ObservableObject
{
    private string _name = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public SavedEndpoint Source { get; set; } = new();
    public SavedEndpoint Target { get; set; } = new();

    public static string MakeName(ConnectionProfile source, ConnectionProfile target)
    {
        static string One(ConnectionProfile p)
            => string.IsNullOrWhiteSpace(p.Database) ? p.Server.Trim() : $"{p.Server.Trim()}.{p.Database.Trim()}";
        return $"{One(source)}  →  {One(target)}";
    }
}

public sealed class ProjectState
{
    public Guid? LastProjectId { get; set; }
    public SavedEndpoint? LastSource { get; set; }
    public SavedEndpoint? LastTarget { get; set; }
    public List<SavedProject> Projects { get; set; } = [];
}

public static class ProjectStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FakeSqlCompare.conn.v1");
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FakeSqlCompare", "projects.json");

    public static ProjectState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new ProjectState();
            var state = JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(FilePath), Json);
            return state ?? new ProjectState();
        }
        catch
        {
            return new ProjectState();
        }
    }

    public static void Save(ProjectState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Json));
    }

    public static SavedEndpoint Capture(ConnectionProfile profile) => new()
    {
        Server = profile.Server,
        Database = profile.Database,
        Auth = profile.Auth,
        User = profile.User,
        Password = Protect(profile.Password)
    };

    public static void Apply(ConnectionProfile profile, SavedEndpoint endpoint)
    {
        profile.Server = endpoint.Server ?? "";
        profile.Auth = string.IsNullOrWhiteSpace(endpoint.Auth) ? "windows" : endpoint.Auth;
        profile.User = endpoint.User ?? "";
        profile.Password = Unprotect(endpoint.Password);
        profile.Database = endpoint.Database ?? "";
        profile.Tested = false;
    }

    public static void Fill(SavedProject project, ConnectionProfile source, ConnectionProfile target, string? alias = null)
    {
        if (!string.IsNullOrWhiteSpace(alias))
            project.Name = alias.Trim();
        else if (string.IsNullOrWhiteSpace(project.Name))
            project.Name = SavedProject.MakeName(source, target);
        project.SavedAt = DateTime.Now;
        project.Source = Capture(source);
        project.Target = Capture(target);
    }

    private static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return "";
        }
    }

    private static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
