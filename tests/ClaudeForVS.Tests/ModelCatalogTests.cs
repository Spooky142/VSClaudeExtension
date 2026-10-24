using System;
using System.Collections.Generic;
using ClaudeForVS.Agent;
using Xunit;

namespace ClaudeForVS.Tests;

public sealed class ModelCatalogTests
{
    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5", 5, 5)]
    [InlineData("claude-opus-5-6", "Opus 5.6", 5, 6)]
    [InlineData("claude-sonnet-5", "Sonnet 5", 5, 0)]
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5", 4, 5)]
    [InlineData("claude-opus-4-20250514", "Opus 4", 4, 0)]
    [InlineData("claude-opus-5-5[1m]", "Opus 5.5", 5, 5)]
    [InlineData("claude-sonnet-6-10", "Sonnet 6.10", 6, 10)]
    public void ReadsModelIds(string id, string name, int major, int minor)
    {
        ModelVersion? model = ModelCatalog.Parse(id);
        Assert.NotNull(model);
        Assert.Equal(name, model!.Name);
        Assert.Equal(new Version(major, minor), model.Version);
    }

    [Theory]
    [InlineData("claude-3-5-sonnet-20241022")] // the old naming scheme
    [InlineData("claude-fable-5-1")]
    [InlineData("opus")]
    [InlineData("")]
    [InlineData(null)]
    public void IgnoresOtherIds(string? id) => Assert.Null(ModelCatalog.Parse(id));

    [Fact]
    public void PicksTheNewestVersionOfEachFamily()
    {
        Dictionary<ClaudeModel, ModelVersion> newest = ModelCatalog.PickNewest(
        [
            "claude-opus-4-1-20250805", "claude-opus-5-6", "claude-opus-5-5", "claude-opus-5",
            "claude-sonnet-5-5", "claude-sonnet-4-6",
            "claude-fable-5-1", "claude-3-5-haiku-20241022",
        ]);

        Assert.Equal("claude-opus-5-6", newest[ClaudeModel.Opus].Id);
        Assert.Equal("claude-sonnet-5-5", newest[ClaudeModel.Sonnet].Id);
        Assert.False(newest.ContainsKey(ClaudeModel.Haiku));
    }

    [Fact]
    public void PrefersTheUndatedIdOfAVersion()
    {
        Dictionary<ClaudeModel, ModelVersion> newest = ModelCatalog.PickNewest(["claude-haiku-5-5-20260901", "claude-haiku-5-5"]);
        Assert.Equal("claude-haiku-5-5", newest[ClaudeModel.Haiku].Id);
    }

    [Fact]
    public void BuiltInNamesComeFromTheBuiltInIds()
    {
        Assert.Equal("Opus 5.5", AgentSettings.ModelName(ClaudeModel.Opus, ChatEngineKind.Api));
        Assert.Equal("Sonnet 5.5", AgentSettings.ModelName(ClaudeModel.Sonnet, ChatEngineKind.Api));
        Assert.Equal("Haiku 5.5", AgentSettings.ModelName(ClaudeModel.Haiku, ChatEngineKind.Api));
    }

    [Fact]
    public void ClaudeCodeIsGivenAliases()
    {
        Assert.Equal("opus", AgentSettings.ClaudeCodeAlias(ClaudeModel.Opus));
        Assert.Equal("sonnet", AgentSettings.ClaudeCodeAlias(ClaudeModel.Sonnet));
        Assert.Equal("haiku", AgentSettings.ClaudeCodeAlias(ClaudeModel.Haiku));
    }

    [Fact]
    public void TheApiUsesTheBuiltInVersionUnlessAskedForTheNewest() =>
        Assert.Equal("claude-opus-5-5", new AgentSettings { UseNewestApiModels = false }.ApiModelId(ClaudeModel.Opus));

    [Theory]
    [InlineData("2.1.300", "2.1.296", 1)]
    [InlineData("2.1.296", "2.1.300", -1)]
    [InlineData("2.2.0", "2.1.999", 1)]
    [InlineData("2.1.296", "2.1.296", 0)]
    [InlineData("2.1", "2.1.0", 0)]
    [InlineData("3.0.0-beta.1", "2.9.9", 1)]
    public void ComparesClaudeCodeVersions(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(ClaudeCodeCli.CompareVersions(a, b)));
}
