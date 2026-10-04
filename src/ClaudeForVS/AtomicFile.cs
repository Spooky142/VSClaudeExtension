using System;
using System.IO;
using System.Text;

namespace ClaudeForVS;

/// <summary>
/// Writes a file through a temporary file in the same folder that then replaces it, so a crash or power cut part
/// way through leaves either the old contents or the new ones, never a truncated mix. Creates the folder if needed.
/// </summary>
internal static class AtomicFile
{
    /// <summary>Writes UTF-8 without a BOM, like <see cref="File.WriteAllText(string, string)"/>.</summary>
    public static void WriteAllText(string path, string contents)
        => WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = $"{path}.{Guid.NewGuid().ToString("N").Substring(0, 8)}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            try
            {
                File.Delete(temp); // only still there if something failed
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
