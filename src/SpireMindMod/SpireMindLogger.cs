namespace SpireMindMod;

internal sealed class SpireMindLogger
{
    private readonly string source;

    public SpireMindLogger(string source)
    {
        this.source = source;
    }

    public void Info(string message)
    {
        Console.WriteLine($"[{source}] {message}");
    }

    public void Warning(string message)
    {
        Console.WriteLine($"[{source}][경고] {message}");
    }
}

