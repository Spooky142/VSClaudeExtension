using System.Linq;
using ClaudeForVS.Markdown;
using Xunit;

namespace ClaudeForVS.Tests;

public class HighlighterTests
{
    [Theory]
    [InlineData("cs", "csharp")]
    [InlineData("C#", "csharp")]
    [InlineData("ts", "javascript")]
    [InlineData("xaml", "xml")]
    [InlineData("powershell", "powershell")]
    [InlineData("python {linenos}", "python")]
    [InlineData("brainfuck", null)]
    [InlineData("", null)]
    public void FenceNamesAreNormalized(string info, string? expected) =>
        Assert.Equal(expected, SyntaxHighlighter.NormalizeLanguage(info));

    [Theory]
    [InlineData("csharp", "var x = @\"a\"\"b\"; // done\n#if DEBUG\nint y = 0x1F; /* block */")]
    [InlineData("javascript", "const s = `multi\nline`; // c\nlet n = 3.14;")]
    [InlineData("python", "def f():\n    \"\"\"doc\n    string\"\"\"\n    return 'x'  # c")]
    [InlineData("powershell", "$x = Get-Item 'a' -Force  # c\nif ($x -eq $null) { <# block #> }")]
    [InlineData("xml", "<!-- c --><a href=\"x\" b='y'>text<br/></a>")]
    [InlineData("json", "{ \"a\": [1, true, null] }")]
    [InlineData("sql", "SELECT * FROM t -- c\nWHERE x = 'y'")]
    [InlineData(null, "anything at all")]
    [InlineData("csharp", "string unterminated = \"oops\nnext line")]
    public void TokensJoinBackIntoTheOriginalText(string? language, string code) =>
        Assert.Equal(code, string.Concat(SyntaxHighlighter.Tokenize(code, language).Select(t => t.Text)));

    [Fact]
    public void CSharpTokensAreClassified()
    {
        var tokens = SyntaxHighlighter.Tokenize("var x = \"hi\"; // note", "csharp");
        Assert.Contains(new CodeToken("var", TokenKind.Keyword), tokens);
        Assert.Contains(new CodeToken("\"hi\"", TokenKind.String), tokens);
        Assert.Contains(new CodeToken("// note", TokenKind.Comment), tokens);
    }

    [Fact]
    public void VerbatimStringsKeepDoubledQuotes()
    {
        var tokens = SyntaxHighlighter.Tokenize("@\"a\"\"b\"", "csharp");
        Assert.Equal([new CodeToken("@\"a\"\"b\"", TokenKind.String)], tokens);
    }

    [Fact]
    public void PascalCaseNamesAreTypes() =>
        Assert.Contains(new CodeToken("StringBuilder", TokenKind.Type), SyntaxHighlighter.Tokenize("new StringBuilder()", "csharp"));

    [Fact]
    public void XmlTagsAndAttributesAreClassified()
    {
        var tokens = SyntaxHighlighter.Tokenize("<a href=\"x\">t</a>", "xml");
        Assert.Contains(new CodeToken("<a", TokenKind.Tag), tokens);
        Assert.Contains(new CodeToken("href", TokenKind.Attribute), tokens);
        Assert.Contains(new CodeToken("\"x\"", TokenKind.String), tokens);
        Assert.Contains(new CodeToken("</a", TokenKind.Tag), tokens);
    }

    [Theory]
    [InlineData(@"C:\x\Foo.cs", "csharp")]
    [InlineData("app.tsx", "typescript")]
    [InlineData("Makefile", "")]
    [InlineData("data.unknownext", "")]
    public void FilesGetAFenceLanguage(string path, string expected) =>
        Assert.Equal(expected, SyntaxHighlighter.FenceLanguageForFile(path));
}
