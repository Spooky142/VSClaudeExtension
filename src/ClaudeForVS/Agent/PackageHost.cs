using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>A NuGet package a project references.</summary>
internal sealed record PackageInfo(string Id, string Version);

/// <summary>A project in the open solution and the NuGet packages it references.</summary>
/// <param name="Path">The project file; null if Visual Studio doesn't say.</param>
internal sealed record PackageProject(string Name, string? Path, IReadOnlyList<PackageInfo> Packages);

/// <summary>NuGet as Visual Studio drives it, so a package change restores like one made in the Manage NuGet Packages window.</summary>
internal interface IPackageHost
{
    /// <summary>Every project in the open solution that can hold packages, with its packages.</summary>
    Task<IReadOnlyList<PackageProject>> GetProjectsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Adds a package to a project, or moves it to <paramref name="version"/> if it's already there. A null version means
    /// the latest stable release. Throws <see cref="ToolInputException"/> with NuGet's reason if it can't.
    /// </summary>
    Task InstallAsync(string project, string packageId, string? version, CancellationToken cancellationToken);

    Task UninstallAsync(string project, string packageId, CancellationToken cancellationToken);
}

/// <summary>Where the engines find NuGet in Visual Studio. Null outside Visual Studio (e.g. in tests).</summary>
internal static class PackageHostRegistry
{
    public static IPackageHost? Current { get; set; }
}

/// <summary>What a package tool was asked to do.</summary>
internal sealed record PackageRequest(string Project, string PackageId, string? Version);

/// <summary>The package tools Claude can call, shared by both engines: reading input, listing and carrying out the changes.</summary>
internal static class PackageTools
{
    /// <summary>The key kept in the "always allow" rules for changing packages.</summary>
    public const string RuleKey = "manage_packages";

    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(@"^[0-9A-Za-z][0-9A-Za-z.+-]*$", RegexOptions.Compiled);

    private static IPackageHost Host =>
        PackageHostRegistry.Current ?? throw new ToolInputException("NuGet packages can only be managed from Visual Studio with a solution open.");

    public static async Task<string> ListAsync(IReadOnlyDictionary<string, JsonElement> input, string repoRoot, CancellationToken cancellationToken)
    {
        string project = Text(input, "project");
        IReadOnlyList<PackageProject> projects = await Host.GetProjectsAsync(cancellationToken).ConfigureAwait(false);
        return Format(project.Length == 0 ? projects : [Find(projects, project)], repoRoot);
    }

    /// <summary>Reads the input of add_package or remove_package. <paramref name="withVersion"/> is false for removals.</summary>
    public static PackageRequest Parse(IReadOnlyDictionary<string, JsonElement> input, bool withVersion)
    {
        string project = Text(input, "project").Trim();
        string id = Text(input, "package").Trim();
        string version = withVersion ? Text(input, "version").Trim() : string.Empty;
        if (project.Length == 0)
        {
            throw new ToolInputException("`project` must be the name of a project in the solution (see list_packages).");
        }

        if (!IdPattern.IsMatch(id))
        {
            throw new ToolInputException("`package` must be a NuGet package ID such as \"Newtonsoft.Json\".");
        }

        if (version.Length > 0 && !VersionPattern.IsMatch(version))
        {
            throw new ToolInputException("`version` must be a NuGet version such as \"13.0.3\" or \"2.0.0-beta.1\"; leave it out for the latest stable release.");
        }

        return new PackageRequest(project, id, version.Length == 0 ? null : version);
    }

    /// <summary>The approval card's title.</summary>
    public static string Title(PackageRequest request, bool remove) => remove
        ? $"Remove NuGet package {request.PackageId} from {request.Project}"
        : $"Add NuGet package {request.PackageId}{(request.Version is null ? " (latest)" : " " + request.Version)} to {request.Project}";

    public static async Task<string> AddAsync(PackageRequest request, CancellationToken cancellationToken)
    {
        IPackageHost host = Host;
        PackageProject project = Find(await host.GetProjectsAsync(cancellationToken).ConfigureAwait(false), request.Project);
        PackageInfo? before = project.Packages.FirstOrDefault(p => p.Id.Equals(request.PackageId, StringComparison.OrdinalIgnoreCase));
        if (before != null && request.Version != null && before.Version.Equals(request.Version, StringComparison.OrdinalIgnoreCase))
        {
            return $"{before.Id} {before.Version} is already in {project.Name}.";
        }

        await host.InstallAsync(project.Name, request.PackageId, request.Version, cancellationToken).ConfigureAwait(false);

        // Ask Visual Studio what it ended up with, rather than assuming.
        PackageProject after = Find(await host.GetProjectsAsync(cancellationToken).ConfigureAwait(false), project.Name);
        PackageInfo? now = after.Packages.FirstOrDefault(p => p.Id.Equals(request.PackageId, StringComparison.OrdinalIgnoreCase));
        if (now is null)
        {
            throw new ToolInputException($"NuGet reported no error, but {request.PackageId} isn't in {project.Name} afterwards. Check the Package Manager output in Visual Studio.");
        }

        string verb = before is null ? "Added" : before.Version.Equals(now.Version, StringComparison.OrdinalIgnoreCase) ? "Kept" : "Changed";
        string from = before != null && verb == "Changed" ? $" (was {before.Version})" : string.Empty;
        string mismatch = request.Version != null && !now.Version.Equals(request.Version, StringComparison.OrdinalIgnoreCase)
            ? $" Note: you asked for {request.Version}, but Visual Studio reports {now.Version}."
            : string.Empty;
        return $"{verb} {now.Id} {now.Version} in {project.Name}{from}. Visual Studio restores the project's packages; run build_solution or get_diagnostics to check it still builds.{mismatch}";
    }

    public static async Task<string> RemoveAsync(PackageRequest request, CancellationToken cancellationToken)
    {
        IPackageHost host = Host;
        PackageProject project = Find(await host.GetProjectsAsync(cancellationToken).ConfigureAwait(false), request.Project);
        PackageInfo? existing = project.Packages.FirstOrDefault(p => p.Id.Equals(request.PackageId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolInputException($"{project.Name} doesn't reference {request.PackageId}. Its packages: {PackageList(project)}.");
        await host.UninstallAsync(project.Name, existing.Id, cancellationToken).ConfigureAwait(false);
        return $"Removed {existing.Id} {existing.Version} from {project.Name}. Run build_solution or get_diagnostics to check nothing still needs it.";
    }

    /// <summary>The projects with their packages, one project per paragraph.</summary>
    public static string Format(IReadOnlyList<PackageProject> projects, string repoRoot)
    {
        if (projects.Count == 0)
        {
            return "No projects that can hold NuGet packages are open in Visual Studio.";
        }

        var sb = new StringBuilder();
        foreach (PackageProject project in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            string file = project.Path is null ? string.Empty : $" ({Relative(repoRoot, project.Path)})";
            sb.Append($"{project.Name}{file}:\n");
            if (project.Packages.Count == 0)
            {
                sb.Append("  (no packages)\n");
            }

            foreach (PackageInfo package in project.Packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append($"  {package.Id} {package.Version}\n");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static PackageProject Find(IReadOnlyList<PackageProject> projects, string name)
    {
        PackageProject? match = projects.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault(p => p.Path != null && Path.GetFileNameWithoutExtension(p.Path).Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ToolInputException(
            $"No project named \"{name}\" is open in Visual Studio. Projects: {(projects.Count == 0 ? "(none)" : string.Join(", ", projects.Select(p => p.Name)))}.");
    }

    private static string PackageList(PackageProject project) =>
        project.Packages.Count == 0 ? "(none)" : string.Join(", ", project.Packages.Select(p => p.Id));

    private static string Relative(string repoRoot, string fullPath)
    {
        string root = repoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(root.Length) : fullPath).Replace('\\', '/');
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> input, string name) =>
        input.TryGetValue(name, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;
}
