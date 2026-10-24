using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;

namespace ClaudeForVS.Agent;

/// <summary>One model version, e.g. ("claude-opus-5-6", "Opus 5.6", 5.6).</summary>
internal sealed record ModelVersion(string Id, string Name, Version Version);

/// <summary>
/// The newest model versions seen for each family: on the Claude API (from the Models API) and in Claude Code (from
/// the model its <c>--model opus</c>-style aliases resolved to). Kept in %LOCALAPPDATA%\ClaudeForVS\models.json so
/// the names are right straight after a restart. Everything here is used from the UI thread.
/// </summary>
internal static class ModelCatalog
{
    private static readonly string CatalogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeForVS", "models.json");

    // claude-opus-5-6, claude-opus-5, claude-opus-4-1-20250805, claude-opus-5-5[1m]. The minor version is one or two
    // digits, so it can't be mistaken for a date. Older names (claude-3-5-sonnet-...) don't match and are ignored.
    private static readonly Regex IdPattern = new(
        @"^claude-(?<family>opus|sonnet|haiku)-(?<major>\d+)(?:-(?<minor>\d{1,2}))?(?:-\d{8})?(?:\[[^\]]*\])?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static Saved saved = Load();

    /// <summary>The newest version of each family the Models API listed, as of the last check.</summary>
    public static ModelVersion? NewestApi(ClaudeModel family) => Find(saved.Api, family);

    /// <summary>The model Claude Code last ran for this family's alias, or null before it has run one.</summary>
    public static ModelVersion? ClaudeCodeModel(ClaudeModel family) => Find(saved.ClaudeCode, family);

    /// <summary>A newer version of the family than this extension was built for, if the Models API has listed one.</summary>
    public static ModelVersion? NewerApiModel(ClaudeModel family) =>
        NewestApi(family) is ModelVersion newest && Parse(AgentSettings.ModelId(family)) is ModelVersion built && newest.Version > built.Version
            ? newest
            : null;

    /// <summary>Reads a model ID; null for IDs that aren't an Opus, Sonnet or Haiku in the current naming scheme.</summary>
    public static ModelVersion? Parse(string? id)
    {
        Match match = IdPattern.Match(id?.Trim() ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        string family = match.Groups["family"].Value.ToLowerInvariant();
        int major = int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture);
        int minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        string name = char.ToUpperInvariant(family[0]) + family.Substring(1) + " " + (minor > 0 ? $"{major}.{minor}" : $"{major}");
        return new ModelVersion(id!.Trim(), name, new Version(major, minor));
    }

    public static ClaudeModel? FamilyOf(ModelVersion model) => model.Name.Split(' ')[0] switch
    {
        "Opus" => ClaudeModel.Opus,
        "Sonnet" => ClaudeModel.Sonnet,
        "Haiku" => ClaudeModel.Haiku,
        _ => null,
    };

    /// <summary>The highest version of each family among the given IDs. Dated snapshots of the same version lose to the undated ID.</summary>
    public static Dictionary<ClaudeModel, ModelVersion> PickNewest(IEnumerable<string> ids)
    {
        var newest = new Dictionary<ClaudeModel, ModelVersion>();
        foreach (ModelVersion model in ids.Select(Parse).OfType<ModelVersion>())
        {
            if (FamilyOf(model) is not ClaudeModel family)
            {
                continue;
            }

            if (!newest.TryGetValue(family, out ModelVersion? best)
                || model.Version > best.Version
                || (model.Version == best.Version && model.Id.Length < best.Id.Length))
            {
                newest[family] = model;
            }
        }

        return newest;
    }

    /// <summary>Remembers the model Claude Code reported. Returns true if that changes the name shown for its family.</summary>
    public static bool ObserveClaudeCode(string? id)
    {
        if (Parse(id) is not ModelVersion model || FamilyOf(model) is not ClaudeModel family
            || (ClaudeCodeModel(family) is ModelVersion known && known.Name == model.Name))
        {
            return false;
        }

        var code = new Dictionary<string, string>(saved.ClaudeCode, StringComparer.Ordinal) { [family.ToString()] = model.Id };
        saved = saved with { ClaudeCode = code };
        Save();
        return true;
    }

    /// <summary>When the Models API was last asked, so several chat windows don't all ask.</summary>
    public static DateTime LastApiCheckUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Asks the Models API which models there are. Returns true if the newest version of a family changed.</summary>
    public static async Task<bool> RefreshApiAsync(string apiKey, CancellationToken cancellationToken)
    {
        LastApiCheckUtc = DateTime.UtcNow;
        var ids = new List<string>();
        using (var client = new AnthropicClient { ApiKey = apiKey })
        {
            var page = await client.Models.List(new Anthropic.Models.Models.ModelListParams { Limit = 1000 }, cancellationToken);
            ids.AddRange(page.Items.Select(m => m.ID));
        }

        Dictionary<ClaudeModel, ModelVersion> newest = PickNewest(ids);
        var api = newest.ToDictionary(p => p.Key.ToString(), p => p.Value.Id, StringComparer.Ordinal);
        if (newest.Count == 0 || (api.Count == saved.Api.Count && api.All(p => saved.Api.TryGetValue(p.Key, out string? id) && id == p.Value)))
        {
            return false;
        }

        saved = saved with { Api = api };
        Save();
        return true;
    }

    private static ModelVersion? Find(Dictionary<string, string> ids, ClaudeModel family) =>
        ids.TryGetValue(family.ToString(), out string? id) ? Parse(id) : null;

    private static Saved Load()
    {
        try
        {
            if (File.Exists(CatalogFile) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(CatalogFile)) is { } loaded)
            {
                return new Saved(
                    new Dictionary<string, string>(loaded.Api ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                    new Dictionary<string, string>(loaded.ClaudeCode ?? new Dictionary<string, string>(), StringComparer.Ordinal));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        return new Saved(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
    }

    private static void Save()
    {
        try
        {
            AtomicFile.WriteAllText(CatalogFile, JsonSerializer.Serialize(saved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Model IDs by family name ("Opus", ...).</summary>
    private sealed record Saved(Dictionary<string, string> Api, Dictionary<string, string> ClaudeCode);
}
