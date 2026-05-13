using System.Text;
using System.Text.Json;

namespace WebSnapshots;

public static class AtomicWrite
{
    public static async Task WriteAllTextAtomicAsync(string finalPath, string content, Encoding enc)
    {
        var dir = Path.GetDirectoryName(finalPath) ?? ".";
        Directory.CreateDirectory(dir);

        var tmp = Path.Combine(dir, Path.GetRandomFileName());

        try
        {
            await File.WriteAllTextAsync(tmp, content, enc);

            ReplaceFile(tmp, finalPath);
        }
        finally
        {
            SafeDelete(tmp);
        }
    }

    public static async Task WriteAllBytesAtomicAsync(string finalPath, byte[] data)
    {
        var dir = Path.GetDirectoryName(finalPath) ?? ".";
        Directory.CreateDirectory(dir);

        var tmp = Path.Combine(dir, Path.GetRandomFileName());

        try
        {
            await File.WriteAllBytesAsync(tmp, data);

            ReplaceFile(tmp, finalPath);
        }
        finally
        {
            SafeDelete(tmp);
        }
    }

    public static async Task WriteJsonAtomicAsync<T>(string finalPath, T value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await WriteAllTextAtomicAsync(finalPath, json, Encoding.UTF8);
    }

    private static void ReplaceFile(string tempPath, string finalPath)
    {
        if (File.Exists(finalPath))
        {
            // True atomic replace on Windows
            File.Replace(tempPath, finalPath, null);
        }
        else
        {
            File.Move(tempPath, finalPath);
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore cleanup failures
        }
    }
}