using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using NuGet.VisualStudio;

namespace ClaudeForVS;

/// <summary>
/// <see cref="IPackageHost"/> on Visual Studio's NuGet services, so a change restores and updates the project the way
/// the Manage NuGet Packages window does. The projects are found through the automation model and their packages are
/// read from the project files; NuGet's synchronous calls run on a background thread, as its documentation advises, so
/// Visual Studio stays responsive while it works.
/// </summary>
internal sealed class VsPackageHost(IServiceProvider services) : IPackageHost
{
    private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

    // How long to wait for NuGet's change to show in the project file.
    private static readonly TimeSpan ProjectFileWait = TimeSpan.FromSeconds(8);

    private static readonly string[] ProjectExtensions = [".csproj", ".vbproj", ".fsproj"];

    public async Task<IReadOnlyList<PackageProject>> GetProjectsAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        List<(string Name, string Path)> found = this.ProjectFiles();
        return await Task.Run(() => (IReadOnlyList<PackageProject>)found.Select(p => new PackageProject(p.Name, p.Path, ProjectPackages.Read(p.Path))).ToList(), cancellationToken).ConfigureAwait(false);
    }

    public async Task InstallAsync(string project, string packageId, string? version, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        (Project target, string path, IVsPackageInstaller2 installer, IVsPackageUninstaller uninstaller) = this.Prepare(project);
        string? existing = await VersionOfAsync(path, packageId);

        await RunAsync($"add {packageId}", () => Install(installer, target, packageId, version), cancellationToken);
        string? now = await WaitForAsync(path, packageId, version, cancellationToken);

        // Some project types won't move a package to another version in place: take it out and put it back.
        if (existing != null && version != null && now != null && !string.Equals(now, version, StringComparison.OrdinalIgnoreCase))
        {
            await RunAsync($"remove {packageId}", () => uninstaller.UninstallPackage(target, packageId, removeDependencies: false), cancellationToken);
            try
            {
                await RunAsync($"add {packageId} {version}", () => Install(installer, target, packageId, version), cancellationToken);
            }
            catch (ToolInputException)
            {
                // Put back what was there, so a failed change doesn't leave the project without the package.
                try
                {
                    await RunAsync($"restore {packageId} {existing}", () => Install(installer, target, packageId, existing), CancellationToken.None);
                }
                catch (ToolInputException)
                {
                }

                throw;
            }

            await WaitForAsync(path, packageId, version, cancellationToken);
        }
    }

    public async Task UninstallAsync(string project, string packageId, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        (Project target, string path, _, IVsPackageUninstaller uninstaller) = this.Prepare(project);
        await RunAsync($"remove {packageId}", () => uninstaller.UninstallPackage(target, packageId, removeDependencies: false), cancellationToken);

        // The project file is rewritten a moment after NuGet returns.
        DateTime until = DateTime.UtcNow + ProjectFileWait;
        while (await VersionOfAsync(path, packageId) != null && DateTime.UtcNow < until)
        {
            await Task.Delay(300, cancellationToken);
        }
    }

    private static void Install(IVsPackageInstaller2 installer, Project project, string packageId, string? version)
    {
        // A null source means the sources enabled in the user's NuGet settings.
        if (version is null)
        {
            installer.InstallLatestPackage(null, project, packageId, includePrerelease: false, ignoreDependencies: false);
        }
        else
        {
            installer.InstallPackage(null, project, packageId, version, ignoreDependencies: false);
        }
    }

    /// <summary>Runs one of NuGet's synchronous calls off the UI thread, and turns whatever it throws into a message for Claude.</summary>
    private static async Task RunAsync(string what, Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await Task.Run(action, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NuGet reports its own failures with many exception types (no such package, a conflicting version, no
            // network, a restore error), and each has a message that says why.
            throw new ToolInputException($"NuGet couldn't {what}: {Innermost(ex).Message}");
        }
    }

    private static Exception Innermost(Exception ex)
    {
        while ((ex is AggregateException or TargetInvocationException) && ex.InnerException is Exception inner)
        {
            ex = inner;
        }

        return ex;
    }

    private static Task<string?> VersionOfAsync(string projectPath, string packageId) =>
        Task.Run(() => ProjectPackages.Read(projectPath).FirstOrDefault(p => p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))?.Version);

    /// <summary>The package's version in the project file once NuGet's change has been written (the expected one, when there is one); null if it never shows.</summary>
    private static async Task<string?> WaitForAsync(string projectPath, string packageId, string? expected, CancellationToken cancellationToken)
    {
        DateTime until = DateTime.UtcNow + ProjectFileWait;
        while (true)
        {
            string? version = await VersionOfAsync(projectPath, packageId);
            if ((version != null && (expected is null || string.Equals(version, expected, StringComparison.OrdinalIgnoreCase))) || DateTime.UtcNow >= until)
            {
                return version;
            }

            await Task.Delay(300, cancellationToken);
        }
    }

    private (Project Project, string Path, IVsPackageInstaller2 Installer, IVsPackageUninstaller Uninstaller) Prepare(string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (Project project in this.Projects())
        {
            if (ProjectFile(project) is string path && string.Equals(NameOf(project), name, StringComparison.OrdinalIgnoreCase))
            {
                IComponentModel components = services.GetService(typeof(SComponentModel)) as IComponentModel
                    ?? throw new ToolInputException("Visual Studio's NuGet services aren't available.");
                return (project, path, components.GetService<IVsPackageInstaller2>(), components.GetService<IVsPackageUninstaller>());
            }
        }

        throw new ToolInputException($"No project named \"{name}\" is open in Visual Studio.");
    }

    private List<(string Name, string Path)> ProjectFiles()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var result = new List<(string, string)>();
        foreach (Project project in this.Projects())
        {
            if (ProjectFile(project) is string path)
            {
                result.Add((NameOf(project) ?? Path.GetFileNameWithoutExtension(path), path));
            }
        }

        return result;
    }

    /// <summary>The project's file, if it's a kind that holds NuGet packages.</summary>
    private static string? ProjectFile(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return FullNameOf(project) is string path && ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary>Every project in the solution, looking inside solution folders.</summary>
    private IEnumerable<Project> Projects()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if ((services.GetService(typeof(DTE)) as DTE2)?.Solution?.Projects is not Projects roots)
        {
            yield break;
        }

        foreach (Project root in roots)
        {
            foreach (Project project in Flatten(root))
            {
                yield return project;
            }
        }
    }

    private static IEnumerable<Project> Flatten(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!string.Equals(KindOf(project), SolutionFolderKind, StringComparison.OrdinalIgnoreCase))
        {
            yield return project;
            yield break;
        }

        foreach (ProjectItem item in project.ProjectItems)
        {
            if (item.SubProject is Project sub)
            {
                foreach (Project inner in Flatten(sub))
                {
                    yield return inner;
                }
            }
        }
    }

    // Some project types (e.g. unloaded ones) throw when asked about themselves.
    private static string? NameOf(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return project.Name;
        }
        catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidCastException)
        {
            return null;
        }
    }

    private static string? FullNameOf(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return project.FullName;
        }
        catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidCastException)
        {
            return null;
        }
    }

    private static string? KindOf(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return project.Kind;
        }
        catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidCastException)
        {
            return null;
        }
    }
}

