using System.Text;

namespace RutonyCredits;

// Limits apply to individual records/files, never to the number of valid events.
internal static class BoundedFiles
{
    internal const int SettingsBytes = 256 * 1024, ManifestBytes = 4096, CacheBytes = 65536, ObsBytes = 65536;
    internal const int JournalLineCharacters = 16384;

    internal static byte[] Read(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("Файл превышает допустимый размер.");
        // A length check alone is insufficient if a file grows while being read.
        var bytes = new byte[maximum + 1];
        int count = 0, read;
        while (count < bytes.Length && (read = file.Read(bytes, count, bytes.Length - count)) != 0) count += read;
        if (count > maximum) throw new InvalidDataException("Файл превышает допустимый размер.");
        return bytes[..count];
    }
    internal static string Text(string path, int maximum)
    {
        using var reader = new StreamReader(new MemoryStream(Read(path, maximum)), new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }
    internal static IEnumerable<string> Lines(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), true);
        var line = new StringBuilder();
        while (reader.Read() is var c && c != -1)
        {
            if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n') reader.Read();
                yield return line.ToString(); line.Clear();
            }
            else
            {
                if (line.Length >= JournalLineCharacters) throw new InvalidDataException("Строка журнала превышает допустимый размер.");
                line.Append((char)c);
            }
        }
        if (line.Length != 0) yield return line.ToString();
    }
    internal static string? Preserve(string path)
    {
        if (!File.Exists(path)) return null;
        var backup = path + ".recovery-" + Guid.NewGuid().ToString("N");
        File.Copy(path, backup); // streaming, including oversized/corrupt input
        return backup;
    }
}
