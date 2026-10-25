using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeForVS.Agent;
using Xunit;

namespace ClaudeForVS.Tests;

/// <summary>The API engine's file sandbox, against a throwaway folder in the temp directory.</summary>
public sealed class RepoFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));
    private readonly RepoFiles files;

    public RepoFilesTests()
    {
        Directory.CreateDirectory(this.root);
        this.files = new RepoFiles(this.root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"src\..\..\outside.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@".git\config")]
    [InlineData(@"sub\.GIT\HEAD")]
    public void PathsOutsideTheRepositoryOrInGitAreRefused(string path) =>
        Assert.Throws<ToolInputException>(() => this.files.Resolve(path));

    [Fact]
    public void AFileOpenInTheEditorIsReadAndWrittenThere()
    {
        string path = Path.Combine(this.root, "open.cs");
        File.WriteAllText(path, "saved\n");
        string? editor = "unsaved\n";

        // Only this test's file is "open", so other tests running meanwhile see the defaults.
        Func<string, string?> previousRead = OpenDocuments.UnsavedText;
        Func<string, string, EditorWrite> previousWrite = OpenDocuments.WriteToEditor;
        OpenDocuments.UnsavedText = p => p == path ? editor : previousRead(p);
        OpenDocuments.WriteToEditor = (p, text) =>
        {
            if (p != path)
            {
                return previousWrite(p, text);
            }

            editor = text;
            return EditorWrite.LeftUnsaved;
        };
        try
        {
            RepoFiles.TextFile file = this.files.Read("open.cs");
            Assert.Equal("unsaved\n", file.Text);
            Assert.True(file.Unsaved);

            Assert.Equal(EditorWrite.LeftUnsaved, this.files.Write(path, "edited\n", file.Encoding));
            Assert.Equal("edited\n", editor);
            Assert.Equal("saved\n", File.ReadAllText(path)); // the editor has it; the disk waits for the user to save
        }
        finally
        {
            OpenDocuments.UnsavedText = previousRead;
            OpenDocuments.WriteToEditor = previousWrite;
        }
    }

    [Fact]
    public void PathsInsideTheRepositoryResolve() =>
        Assert.Equal(Path.Combine(this.root, "src", "a.cs"), this.files.Resolve(@"src\..\src\a.cs"));

    [Fact]
    public void PathsThroughAJunctionAreRefused()
    {
        string target = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            using Process mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(this.root, "link")}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);

            Assert.Throws<ToolInputException>(() => this.files.Resolve(@"link\file.txt"));
        }
        finally
        {
            Directory.Delete(Path.Combine(this.root, "link"));
            Directory.Delete(target);
        }
    }

    [Fact]
    public void Utf16FilesWithABomAreTextAndKeepTheirEncoding()
    {
        File.WriteAllText(Path.Combine(this.root, "a.rc"), "line one\r\ncafé\r\n", Encoding.Unicode);
        RepoFiles.TextFile file = this.files.Read("a.rc");
        Assert.Equal("line one\r\ncafé\r\n", file.Text);
        Assert.Equal("\r\n", file.Newline);

        this.files.Write(file.FullPath, file.Text.Replace("one", "1"), file.Encoding);
        byte[] bytes = File.ReadAllBytes(file.FullPath);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes.Take(2));
        Assert.Equal("line 1\r\ncafé\r\n", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
    }

    [Fact]
    public void Utf8FilesKeepWhetherTheyHaveABom()
    {
        File.WriteAllText(Path.Combine(this.root, "bom.txt"), "é", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(this.root, "plain.txt"), "é", new UTF8Encoding(false));
        Assert.Equal(3, this.files.Read("bom.txt").Encoding.GetPreamble().Length);
        Assert.Empty(this.files.Read("plain.txt").Encoding.GetPreamble());
        Assert.Equal("é", this.files.Read("plain.txt").Text);
    }

    [Fact]
    public void FilesThatArentUtf8RoundTripThroughTheAnsiCodePage()
    {
        // 0xE9 is "é" in Windows-1252 and not valid UTF-8 on its own.
        byte[] original = [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n'];
        (string text, Encoding encoding) = RepoFiles.Decode(original, "legacy.cpp");
        Assert.DoesNotContain('\uFFFD', text);
        Assert.Equal(original, encoding.GetBytes(text));
    }

    [Fact]
    public void WritesKeepTheBomAndLeaveNoTemporaryFile()
    {
        string path = Path.Combine(this.root, "sub", "bom.txt");
        this.files.Write(path, "é", new UTF8Encoding(true));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0xC3, 0xA9 }, File.ReadAllBytes(path));

        this.files.Write(path, "e", new UTF8Encoding(false)); // replacing an existing file
        Assert.Equal(new byte[] { (byte)'e' }, File.ReadAllBytes(path));
        Assert.Equal(["bom.txt"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void FilesTooLargeToReadAreRefused()
    {
        using (var stream = new FileStream(Path.Combine(this.root, "huge.log"), FileMode.Create))
        {
            stream.SetLength(RepoFiles.MaxReadBytes + 1);
        }

        var ex = Assert.Throws<ToolInputException>(() => this.files.Read("huge.log"));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void FilesWithZeroBytesAndNoBomAreBinary() =>
        Assert.Throws<ToolInputException>(() => RepoFiles.Decode([0x4D, 0x5A, 0x00, 0x01], "app.exe"));
}
