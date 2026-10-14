// Reads the SQL statements embedded from Sql/*.sql, so each statement is reviewed as one file.

using System.Reflection;

namespace WoundRag.Infrastructure;

internal static class SqlFiles
{
    public static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded SQL file '{name}' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
