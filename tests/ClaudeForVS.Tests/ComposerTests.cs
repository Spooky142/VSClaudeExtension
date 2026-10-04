using System;
using System.Collections.Generic;
using System.Text.Json;
using ClaudeForVS.Agent;
using ClaudeForVS.ToolWindows;
using Xunit;

namespace ClaudeForVS.Tests;

public class AttachmentTests
{
    private const string Root = @"C:\repo";

    [Fact]
    public void ASelectionIsSentAsAFencedBlockAfterTheText()
    {
        var editor = new EditorContext(@"C:\repo\src\Foo.cs", 3, 4, "int x;\nint y;");
        (ChatPrompt prompt, string note) = Attachment.Compose("Why?", [Attachment.ForSelection(editor, Root)]);

        Assert.StartsWith("Why?\n\n<context from Visual Studio>\n", prompt.Text);
        Assert.Contains("Lines 3-4 of `src/Foo.cs`:\n```csharp\nint x;\nint y;\n```", prompt.Text);
        Assert.EndsWith("</context from Visual Studio>", prompt.Text);
        Assert.Equal("Foo.cs:3-4", note);
        Assert.Empty(prompt.Images);
    }

    [Fact]
    public void TheFenceIsLongerThanAnyBackticksInTheCode()
    {
        var editor = new EditorContext(@"C:\repo\README.md", 1, 1, "```\ncode\n```");
        (ChatPrompt prompt, _) = Attachment.Compose("x", [Attachment.ForSelection(editor, Root)]);
        Assert.Contains("````markdown\n```\ncode\n```\n````", prompt.Text);
    }

    [Fact]
    public void TheActiveFileWithoutASelectionSendsTheCaretLine()
    {
        var editor = new EditorContext(@"C:\repo\src\Foo.cs", 42, 42, string.Empty);
        Attachment active = Attachment.ForActiveFile(editor, Root);
        Assert.Equal("Foo.cs", active.Label);
        Assert.Equal("The user has `src/Foo.cs` open in the editor, with the cursor on line 42.", active.Context);
    }

    [Fact]
    public void FilesOutsideTheRepositoryKeepTheirFullPath() =>
        Assert.Equal(@"D:\other\x.cs", Attachment.DisplayPath(@"D:\other\x.cs", Root));

    [Fact]
    public void ImagesAreSentAsImagesNotText()
    {
        var image = new PromptImage("image/png", [1, 2, 3]);
        (ChatPrompt prompt, string note) = Attachment.Compose("Look", [Attachment.ForImage(image, null, "shot.png")]);
        Assert.Equal("Look", prompt.Text);
        Assert.Same(image, Assert.Single(prompt.Images));
        Assert.Equal("1 image", note);
    }

    [Fact]
    public void ErrorsAreListedWithTheirLocations()
    {
        ErrorListItem[] errors =
        [
            new("error", "CS0103", "The name 'x' does not exist", @"C:\repo\src\Foo.cs", 12, "App"),
            new("error", "CS1002", "; expected", null, 0, null),
        ];
        Attachment attachment = Attachment.ForErrors(errors, Root);
        Assert.Equal("2 errors", attachment.Label);
        Assert.Contains("- src/Foo.cs:12: error CS0103 The name 'x' does not exist [App]", attachment.Context);
        Assert.Contains("- error CS1002 ; expected", attachment.Context);
    }
}

public class PromptHistoryTests
{
    [Fact]
    public void UpAndDownWalkThroughPromptsAndBackToTheDraft()
    {
        PromptHistory history = PromptHistory.InMemory();
        history.Add("first");
        history.Add("second");

        Assert.Equal("second", history.Older("draft"));
        Assert.Equal("first", history.Older("ignored"));
        Assert.Null(history.Older("ignored"));
        Assert.Equal("second", history.Newer());
        Assert.Equal("draft", history.Newer());
        Assert.False(history.IsBrowsing);
        Assert.Null(history.Newer());
    }

    [Fact]
    public void RepeatingAPromptMovesItToTheNewestEnd()
    {
        PromptHistory history = PromptHistory.InMemory();
        history.Add("a");
        history.Add("b");
        history.Add(" a ");
        Assert.Equal(["b", "a"], history.Entries);
    }
}

public class ChatExporterTests
{
    [Fact]
    public void TheConversationIsWrittenAsMarkdownWithoutThinking()
    {
        var info = new SessionInfo { Title = "Fix the build", RepoPath = @"C:\repo", Created = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero) };
        SavedEntry[] entries =
        [
            new() { Kind = ChatEntryKind.User, Text = "Why does it fail?", Note = "2 errors" },
            new() { Kind = ChatEntryKind.Thinking, Text = "secret reasoning" },
            new() { Kind = ChatEntryKind.Tool, Header = "Edited src/Foo.cs", Activity = ActivityKind.Edit },
            new() { Kind = ChatEntryKind.Assistant, Text = "Fixed it." },
        ];

        string md = ChatExporter.ToMarkdown(info, entries);
        Assert.StartsWith("# Fix the build\n", md);
        Assert.Contains("## You\n\nWhy does it fail?\n\n_Attached: 2 errors_", md);
        Assert.Contains("- `Edited src/Foo.cs`", md);
        Assert.Contains("## Claude\n\nFixed it.", md);
        Assert.DoesNotContain("secret reasoning", md);
    }

    [Fact]
    public void FileNamesAreMadeSafe() =>
        Assert.Equal("a_b_ c_.md", ChatExporter.SuggestedFileName(new SessionInfo { Title = "a/b: c?" }));
}

public class FileChangeTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void AnEditMatchesTheFilesLineEndings()
    {
        FileChange? change = FileChange.ForClaudeCodeTool("Edit", Json(new { old_string = "a\nb", new_string = "A\nB" }), @"C:\f.txt", "x\r\na\r\nb\r\n");
        Assert.Equal("x\r\nA\r\nB\r\n", change?.After);
    }

    [Fact]
    public void MultiEditAppliesEditsInOrder()
    {
        var input = Json(new { edits = new object[] { new { old_string = "one", new_string = "two" }, new { old_string = "two", new_string = "three" } } });
        Assert.Equal("three", FileChange.ForClaudeCodeTool("MultiEdit", input, @"C:\f.txt", "one")?.After);
    }

    [Fact]
    public void ReplaceAllReplacesEveryOccurrence() =>
        Assert.Equal("b b b", FileChange.ForClaudeCodeTool("Edit", Json(new { old_string = "a", new_string = "b", replace_all = true }), @"C:\f.txt", "a a a")?.After);

    [Fact]
    public void WriteReplacesTheWholeFile() =>
        Assert.Equal("new", FileChange.ForClaudeCodeTool("Write", Json(new { content = "new" }), @"C:\f.txt", "old")?.After);

    [Theory]
    [InlineData("Edit", "{\"old_string\":\"missing\",\"new_string\":\"x\"}")]
    [InlineData("Edit", "{\"new_string\":\"x\"}")]
    [InlineData("Read", "{}")]
    public void EditsThatCantBeAppliedGiveNoChange(string tool, string input)
    {
        using JsonDocument doc = JsonDocument.Parse(input);
        Assert.Null(FileChange.ForClaudeCodeTool(tool, doc.RootElement, @"C:\f.txt", "text"));
    }
}
