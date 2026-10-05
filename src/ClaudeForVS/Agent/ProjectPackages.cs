using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ClaudeForVS.Agent;

/// <summary>Reads the NuGet packages a project file references, without needing Visual Studio's services.</summary>
internal static class ProjectPackages
{
    private const string CentralFile = "Directory.Packages.props";

    /// <summary>
    /// The packages the project references directly: <c>PackageReference</c> items (with versions from
    /// <c>Directory.Packages.props</c> when the project uses central package management) or a <c>packages.config</c>.
    /// Empty if the file can't be read.
    /// </summary>
    public static IReadOnlyList<PackageInfo> Read(string projectPath)
    {
        var packages = new List<PackageInfo>();
        try
        {
            XDocument project = XDocument.Load(projectPath);
            Dictionary<string, string>? central = null;
            foreach (XElement reference in project.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                if (Attribute(reference, "Include") is not string id || id.Length == 0)
                {
                    continue; // an Update item changes a reference declared elsewhere
                }

                string? version = Attribute(reference, "Version") ?? Attribute(reference, "VersionOverride")
                    ?? Child(reference, "Version") ?? Child(reference, "VersionOverride");
                if (version is null)
                {
                    central ??= ReadCentralVersions(projectPath);
                    version = central.TryGetValue(id, out string? fromCentral) ? fromCentral : "(no version)";
                }

                packages.Add(new PackageInfo(id, version));
            }

            string config = Path.Combine(Path.GetDirectoryName(projectPath) ?? string.Empty, "packages.config");
            if (packages.Count == 0 && File.Exists(config))
            {
                foreach (XElement package in XDocument.Load(config).Descendants().Where(e => e.Name.LocalName == "package"))
                {
                    if (Attribute(package, "id") is string id && id.Length > 0)
                    {
                        packages.Add(new PackageInfo(id, Attribute(package, "version") ?? "(no version)"));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return packages.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>The versions in the nearest Directory.Packages.props above the project, if there is one.</summary>
    private static Dictionary<string, string> ReadCentralVersions(string projectPath)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (string? dir = Path.GetDirectoryName(projectPath); dir != null; dir = Path.GetDirectoryName(dir))
            {
                string file = Path.Combine(dir, CentralFile);
                if (!File.Exists(file))
                {
                    continue;
                }

                foreach (XElement item in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
                {
                    if (Attribute(item, "Include") is string id && (Attribute(item, "Version") ?? Child(item, "Version")) is string version)
                    {
                        versions[id] = version;
                    }
                }

                break;
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
        }

        return versions;
    }

    private static string? Attribute(XElement element, string name) => element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    private static string? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
}
