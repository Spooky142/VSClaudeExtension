using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeForVS.Markdown;

internal enum TokenKind
{
    Plain,
    Keyword,
    Type,
    String,
    Comment,
    Number,
    Preprocessor,
    Tag,
    Attribute,
}

/// <summary>One coloured run of code.</summary>
internal readonly record struct CodeToken(string Text, TokenKind Kind);

/// <summary>
/// A small, forgiving tokenizer for colouring code blocks. It knows comments, strings, numbers and keywords for
/// common languages, plus tags for XML-like ones; anything it doesn't recognise is left plain.
/// </summary>
internal static class SyntaxHighlighter
{
    private sealed record Language(
        string[] Keywords,
        string[] LineComments,
        (string Start, string End)? BlockComment,
        string Quotes,
        bool CaseInsensitive = false,
        bool PascalCaseTypes = false,
        bool HashPreprocessor = false,
        bool TripleQuotes = false,
        bool DollarVariables = false);

    private static readonly string[] CSharpKeywords =
    [
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false",
        "finally", "fixed", "float", "for", "foreach", "get", "goto", "if", "implicit", "in", "init", "int", "interface", "internal",
        "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "partial", "private",
        "protected", "public", "readonly", "record", "ref", "required", "return", "sbyte", "sealed", "set", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "when", "where", "while", "yield",
        "nameof", "not", "and", "or", "with", "value", "dynamic", "global", "file", "scoped",
    ];

    private static readonly string[] JsKeywords =
    [
        "as", "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do",
        "else", "enum", "export", "extends", "false", "finally", "for", "from", "function", "if", "implements", "import", "in",
        "instanceof", "interface", "let", "new", "null", "of", "private", "protected", "public", "readonly", "return", "static",
        "super", "switch", "this", "throw", "true", "try", "type", "typeof", "undefined", "var", "void", "while", "yield",
        "keyof", "declare", "namespace", "abstract", "any", "boolean", "number", "string", "never", "unknown", "satisfies",
    ];

    private static readonly string[] PythonKeywords =
    [
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "False",
        "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not", "or", "pass",
        "raise", "return", "True", "try", "while", "with", "yield", "self", "match", "case",
    ];

    private static readonly string[] CKeywords =
    [
        "auto", "bool", "break", "case", "catch", "char", "class", "const", "constexpr", "continue", "default", "delete", "do",
        "double", "else", "enum", "explicit", "extern", "false", "float", "for", "friend", "goto", "if", "inline", "int", "long",
        "mutable", "namespace", "new", "noexcept", "nullptr", "operator", "override", "private", "protected", "public",
        "register", "return", "short", "signed", "sizeof", "static", "static_cast", "struct", "switch", "template", "this",
        "throw", "true", "try", "typedef", "typename", "union", "unsigned", "using", "virtual", "void", "volatile", "while",
    ];

    private static readonly string[] JavaKeywords =
    [
        "abstract", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default", "do", "double",
        "else", "enum", "extends", "false", "final", "finally", "float", "for", "if", "implements", "import", "instanceof", "int",
        "interface", "long", "new", "null", "package", "private", "protected", "public", "return", "short", "static", "super",
        "switch", "this", "throw", "throws", "true", "try", "var", "void", "volatile", "while", "fun", "val", "when", "object",
    ];

    private static readonly string[] GoKeywords =
    [
        "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "false", "for", "func", "go",
        "goto", "if", "import", "interface", "map", "nil", "package", "range", "return", "select", "struct", "switch", "true",
        "type", "var",
    ];

    private static readonly string[] RustKeywords =
    [
        "as", "async", "await", "break", "const", "continue", "crate", "dyn", "else", "enum", "extern", "false", "fn", "for", "if",
        "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self", "static", "struct",
        "super", "trait", "true", "type", "unsafe", "use", "where", "while",
    ];

    private static readonly string[] PowerShellKeywords =
    [
        "begin", "break", "catch", "class", "continue", "data", "do", "dynamicparam", "else", "elseif", "end", "exit", "filter",
        "finally", "for", "foreach", "function", "if", "in", "param", "process", "return", "switch", "throw", "trap", "try",
        "until", "while", "-eq", "-ne", "-gt", "-lt", "-ge", "-le", "-like", "-match", "-not", "-and", "-or",
    ];

    private static readonly string[] ShellKeywords =
    [
        "if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case", "esac", "in", "function", "return", "export",
        "local", "echo", "exit", "set", "unset",
    ];

    private static readonly string[] SqlKeywords =
    [
        "select", "from", "where", "insert", "into", "values", "update", "set", "delete", "create", "table", "alter", "drop",
        "join", "inner", "left", "right", "outer", "on", "and", "or", "not", "null", "is", "in", "as", "order", "by", "group",
        "having", "limit", "top", "distinct", "union", "all", "primary", "key", "foreign", "references", "index", "view",
        "begin", "end", "case", "when", "then", "else", "exists", "like", "between", "with", "declare", "int", "varchar",
        "nvarchar", "bit", "datetime", "default", "constraint", "unique", "count", "sum", "max", "min", "avg",
    ];

    private static readonly string[] JsonKeywords = ["true", "false", "null"];

    private static readonly Dictionary<string, Language> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = new(CSharpKeywords, ["//"], ("/*", "*/"), "\"'", PascalCaseTypes: true, HashPreprocessor: true),
        ["javascript"] = new(JsKeywords, ["//"], ("/*", "*/"), "\"'`", PascalCaseTypes: true),
        ["python"] = new(PythonKeywords, ["#"], null, "\"'", TripleQuotes: true),
        ["cpp"] = new(CKeywords, ["//"], ("/*", "*/"), "\"'", PascalCaseTypes: true, HashPreprocessor: true),
        ["java"] = new(JavaKeywords, ["//"], ("/*", "*/"), "\"'", PascalCaseTypes: true),
        ["go"] = new(GoKeywords, ["//"], ("/*", "*/"), "\"'`", PascalCaseTypes: true),
        ["rust"] = new(RustKeywords, ["//"], ("/*", "*/"), "\"", PascalCaseTypes: true),
        ["powershell"] = new(PowerShellKeywords, ["#"], ("<#", "#>"), "\"'", CaseInsensitive: true, DollarVariables: true),
        ["shell"] = new(ShellKeywords, ["#"], null, "\"'", DollarVariables: true),
        ["sql"] = new(SqlKeywords, ["--"], ("/*", "*/"), "'", CaseInsensitive: true),
        ["json"] = new(JsonKeywords, ["//"], ("/*", "*/"), "\""),
        ["css"] = new([], [], ("/*", "*/"), "\"'"),
        ["yaml"] = new(["true", "false", "null", "yes", "no"], ["#"], null, "\"'"),
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "csharp", ["c#"] = "csharp", ["csharp"] = "csharp", ["razor"] = "csharp", ["cshtml"] = "csharp",
        ["js"] = "javascript", ["jsx"] = "javascript", ["javascript"] = "javascript", ["ts"] = "javascript", ["tsx"] = "javascript",
        ["typescript"] = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript",
        ["py"] = "python", ["python"] = "python",
        ["c"] = "cpp", ["h"] = "cpp", ["cpp"] = "cpp", ["c++"] = "cpp", ["cc"] = "cpp", ["hpp"] = "cpp", ["cxx"] = "cpp", ["objc"] = "cpp",
        ["java"] = "java", ["kotlin"] = "java", ["kt"] = "java", ["scala"] = "java", ["swift"] = "java", ["dart"] = "java",
        ["go"] = "go", ["golang"] = "go",
        ["rs"] = "rust", ["rust"] = "rust",
        ["ps1"] = "powershell", ["psm1"] = "powershell", ["powershell"] = "powershell", ["pwsh"] = "powershell", ["ps"] = "powershell",
        ["sh"] = "shell", ["bash"] = "shell", ["zsh"] = "shell", ["shell"] = "shell", ["console"] = "shell", ["cmd"] = "shell", ["bat"] = "shell", ["batch"] = "shell",
        ["sql"] = "sql", ["tsql"] = "sql", ["mysql"] = "sql", ["postgres"] = "sql", ["postgresql"] = "sql",
        ["json"] = "json", ["jsonc"] = "json", ["json5"] = "json",
        ["css"] = "css", ["scss"] = "css", ["less"] = "css",
        ["yaml"] = "yaml", ["yml"] = "yaml",
        ["xml"] = "xml", ["xaml"] = "xml", ["html"] = "xml", ["htm"] = "xml", ["csproj"] = "xml", ["vbproj"] = "xml", ["props"] = "xml",
        ["targets"] = "xml", ["svg"] = "xml", ["resx"] = "xml", ["config"] = "xml", ["vsct"] = "xml", ["vsixmanifest"] = "xml",
        ["nuspec"] = "xml", ["xsd"] = "xml", ["xslt"] = "xml", ["vue"] = "xml",
    };

    /// <summary>The highlighter's name for a fence's info string (e.g. "cs" → "csharp"), or null if it isn't supported.</summary>
    public static string? NormalizeLanguage(string? info)
    {
        string name = (info ?? string.Empty).Trim().Split(' ', '{', ',').FirstOrDefault() ?? string.Empty;
        return Aliases.TryGetValue(name, out string? language) ? language : null;
    }

    /// <summary>A Markdown fence language for a file, e.g. "csharp" for Foo.cs; empty if unknown.</summary>
    public static string FenceLanguageForFile(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "cs" => "csharp",
            "ts" or "tsx" => "typescript",
            "js" or "jsx" or "mjs" or "cjs" => "javascript",
            "py" => "python",
            "ps1" or "psm1" => "powershell",
            "sh" => "bash",
            "md" => "markdown",
            "yml" => "yaml",
            "h" or "hpp" or "cc" or "cxx" => "cpp",
            "" => string.Empty,
            _ => Aliases.ContainsKey(ext) ? ext : string.Empty,
        };
    }

    public static IReadOnlyList<CodeToken> Tokenize(string code, string? language)
    {
        if (language == "xml")
        {
            return TokenizeXml(code);
        }

        return language != null && Languages.TryGetValue(language, out Language? lang)
            ? TokenizeCode(code, lang)
            : [new CodeToken(code, TokenKind.Plain)];
    }

    private static List<CodeToken> TokenizeCode(string code, Language lang)
    {
        var tokens = new List<CodeToken>();
        var plain = new StringBuilder();
        var keywords = new HashSet<string>(lang.Keywords, lang.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        void Emit(string text, TokenKind kind)
        {
            if (kind == TokenKind.Plain)
            {
                plain.Append(text);
                return;
            }

            if (plain.Length > 0)
            {
                tokens.Add(new CodeToken(plain.ToString(), TokenKind.Plain));
                plain.Clear();
            }

            tokens.Add(new CodeToken(text, kind));
        }

        int i = 0;
        bool lineStart = true;
        while (i < code.Length)
        {
            char c = code[i];

            if (lang.BlockComment is (string start, string end) && At(code, i, start))
            {
                int close = code.IndexOf(end, i + start.Length, StringComparison.Ordinal);
                int stop = close < 0 ? code.Length : close + end.Length;
                Emit(code.Substring(i, stop - i), TokenKind.Comment);
                i = stop;
                continue;
            }

            if (lang.LineComments.FirstOrDefault(lc => At(code, i, lc)) is string _)
            {
                int stop = LineEnd(code, i);
                Emit(code.Substring(i, stop - i), TokenKind.Comment);
                i = stop;
                continue;
            }

            if (lang.HashPreprocessor && lineStart && c == '#')
            {
                int stop = LineEnd(code, i);
                Emit(code.Substring(i, stop - i), TokenKind.Preprocessor);
                i = stop;
                continue;
            }

            // C# verbatim and interpolated strings: @"...", $"...", $@"...".
            if ((c == '@' || c == '$') && lang.PascalCaseTypes && lang.HashPreprocessor)
            {
                int q = i;
                while (q < code.Length && (code[q] == '@' || code[q] == '$') && q - i < 3)
                {
                    q++;
                }

                if (q < code.Length && code[q] == '"' && q > i)
                {
                    bool verbatim = code.Substring(i, q - i).Contains('@');
                    int stop = StringEnd(code, q, '"', verbatim);
                    Emit(code.Substring(i, stop - i), TokenKind.String);
                    i = stop;
                    continue;
                }
            }

            if (lang.TripleQuotes && (At(code, i, "\"\"\"") || At(code, i, "'''")))
            {
                string fence = code.Substring(i, 3);
                int close = code.IndexOf(fence, i + 3, StringComparison.Ordinal);
                int stop = close < 0 ? code.Length : close + 3;
                Emit(code.Substring(i, stop - i), TokenKind.String);
                i = stop;
                continue;
            }

            if (lang.Quotes.IndexOf(c) >= 0)
            {
                int stop = StringEnd(code, i, c, verbatim: false);
                Emit(code.Substring(i, stop - i), TokenKind.String);
                i = stop;
                continue;
            }

            if (lang.DollarVariables && c == '$' && i + 1 < code.Length && (char.IsLetter(code[i + 1]) || code[i + 1] == '_' || code[i + 1] == '{'))
            {
                int stop = i + 1;
                while (stop < code.Length && (char.IsLetterOrDigit(code[stop]) || code[stop] is '_' or ':' or '{' or '}'))
                {
                    stop++;
                }

                Emit(code.Substring(i, stop - i), TokenKind.Attribute);
                i = stop;
                lineStart = false;
                continue;
            }

            if (char.IsDigit(c) && (i == 0 || !IsIdentifierChar(code[i - 1])))
            {
                int stop = i;
                while (stop < code.Length && (char.IsLetterOrDigit(code[stop]) || code[stop] == '.' || code[stop] == '_'))
                {
                    stop++;
                }

                Emit(code.Substring(i, stop - i), TokenKind.Number);
                i = stop;
                lineStart = false;
                continue;
            }

            bool dashWord = lang.CaseInsensitive && lang.DollarVariables && c == '-' && i + 1 < code.Length && char.IsLetter(code[i + 1]);
            if (char.IsLetter(c) || c == '_' || dashWord)
            {
                int stop = i + 1;
                while (stop < code.Length && IsIdentifierChar(code[stop]))
                {
                    stop++;
                }

                string word = code.Substring(i, stop - i);
                TokenKind kind = keywords.Contains(word) ? TokenKind.Keyword
                    : lang.PascalCaseTypes && char.IsUpper(word[0]) && word.Any(char.IsLower) ? TokenKind.Type
                    : TokenKind.Plain;
                Emit(word, kind);
                i = stop;
                lineStart = false;
                continue;
            }

            Emit(c.ToString(), TokenKind.Plain);
            if (c == '\n')
            {
                lineStart = true;
            }
            else if (!char.IsWhiteSpace(c))
            {
                lineStart = false;
            }

            i++;
        }

        if (plain.Length > 0)
        {
            tokens.Add(new CodeToken(plain.ToString(), TokenKind.Plain));
        }

        return tokens;
    }

    private static List<CodeToken> TokenizeXml(string code)
    {
        var tokens = new List<CodeToken>();
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length > 0)
            {
                tokens.Add(new CodeToken(plain.ToString(), TokenKind.Plain));
                plain.Clear();
            }
        }

        int i = 0;
        while (i < code.Length)
        {
            if (At(code, i, "<!--"))
            {
                Flush();
                int close = code.IndexOf("-->", i + 4, StringComparison.Ordinal);
                int stop = close < 0 ? code.Length : close + 3;
                tokens.Add(new CodeToken(code.Substring(i, stop - i), TokenKind.Comment));
                i = stop;
                continue;
            }

            if (code[i] == '<' && i + 1 < code.Length && (char.IsLetter(code[i + 1]) || code[i + 1] is '/' or '?' or '!'))
            {
                Flush();

                // Tag name, including the bracket and any / ? ! prefix.
                int stop = code[i + 1] is '/' or '?' or '!' ? i + 2 : i + 1;
                while (stop < code.Length && !char.IsWhiteSpace(code[stop]) && code[stop] is not '>' and not '/')
                {
                    stop++;
                }

                tokens.Add(new CodeToken(code.Substring(i, stop - i), TokenKind.Tag));
                i = stop;

                // Attributes until the tag closes.
                while (i < code.Length && code[i] != '>')
                {
                    char c = code[i];
                    if (c is '"' or '\'')
                    {
                        int end = code.IndexOf(c, i + 1);
                        int s = end < 0 ? code.Length : end + 1;
                        tokens.Add(new CodeToken(code.Substring(i, s - i), TokenKind.String));
                        i = s;
                    }
                    else if (char.IsLetter(c) || c == '_')
                    {
                        int s = i;
                        while (s < code.Length && (char.IsLetterOrDigit(code[s]) || code[s] is ':' or '-' or '_' or '.'))
                        {
                            s++;
                        }

                        tokens.Add(new CodeToken(code.Substring(i, s - i), TokenKind.Attribute));
                        i = s;
                    }
                    else if (c is '/' or '?')
                    {
                        tokens.Add(new CodeToken(c.ToString(), TokenKind.Tag));
                        i++;
                    }
                    else
                    {
                        tokens.Add(new CodeToken(c.ToString(), TokenKind.Plain));
                        i++;
                    }
                }

                if (i < code.Length)
                {
                    tokens.Add(new CodeToken(">", TokenKind.Tag));
                    i++;
                }

                continue;
            }

            plain.Append(code[i]);
            i++;
        }

        Flush();
        return tokens;
    }

    private static bool At(string code, int i, string text) => string.CompareOrdinal(code, i, text, 0, text.Length) == 0;

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int LineEnd(string code, int i)
    {
        int newline = code.IndexOf('\n', i);
        return newline < 0 ? code.Length : newline;
    }

    /// <summary>Index just past the string starting at <paramref name="open"/>. Ordinary strings also end at a line break.</summary>
    private static int StringEnd(string code, int open, char quote, bool verbatim)
    {
        int i = open + 1;
        while (i < code.Length)
        {
            char c = code[i];
            if (!verbatim && c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == quote)
            {
                if (verbatim && i + 1 < code.Length && code[i + 1] == quote)
                {
                    i += 2; // "" inside a verbatim string
                    continue;
                }

                return i + 1;
            }

            if (c == '\n' && !verbatim && quote != '`')
            {
                return i;
            }

            i++;
        }

        return code.Length;
    }
}
