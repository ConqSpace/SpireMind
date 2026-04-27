namespace SpireMindMod;

internal sealed class SpireMindLogger
{
    private static readonly object FileLock = new();
    private readonly string source;

    public SpireMindLogger(string source)
    {
        this.source = source;
    }

    public void Info(string message)
    {
        Write("INFO", message);
    }

    public void Warning(string message)
    {
        Write("WARN", message);
    }

    private void Write(string level, string message)
    {
        string line = $"[{DateTimeOffset.UtcNow:O}][{level}][{source}] {message}";
        Console.WriteLine(line);
        TryWriteFile(line);
    }

    private static void TryWriteFile(string line)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2",
                "SpireMind");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, "spiremind.log");
            lock (FileLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // 파일 로그 실패는 게임 진행을 막지 않는다.
        }
    }
}
