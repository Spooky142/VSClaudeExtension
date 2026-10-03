using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeForVS.Agent;

/// <summary>
/// Stores the Anthropic API key in %LOCALAPPDATA%\ClaudeForVS, encrypted with DPAPI for the
/// current Windows user. Falls back to the ANTHROPIC_API_KEY environment variable.
/// </summary>
internal static class ApiKeyStore
{
    private static readonly string KeyFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeForVS", "apikey.bin");

    public static string? Load()
    {
        try
        {
            if (File.Exists(KeyFile))
            {
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
        }
        catch (CryptographicException)
        {
        }

        string? env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    public static void Save(string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyFile, cipher);
    }
}
