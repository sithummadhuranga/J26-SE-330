// Runs PostgreSQL 16's own client tools inside the compose `postgres` service; a 17 client writes SQL that 16
// rejects (decision 47). The password reaches the tool only through PGPASSWORD, never its command line.

using System.Diagnostics;
using System.Text;
using Npgsql;

namespace IngestionPipeline.Db;

public static class ComposePostgres
{
    // Standard output; a failure's messages go to .cache, since psql can quote the rows it failed on.
    public static string Run(RepoPaths paths, NpgsqlConnectionStringBuilder? login, string tool, IEnumerable<string> args, string? stdinFile = null)
    {
        var start = new ProcessStartInfo("docker")
        {
            WorkingDirectory = paths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinFile is not null,
            StandardOutputEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
        };
        string[] exec = ["compose", "exec", "-T", "-e", "PGPASSWORD", "postgres", tool];
        foreach (var a in exec) start.ArgumentList.Add(a);
        if (login is not null)
            foreach (var a in new[] { "-h", "localhost", "-U", login.Username!, "-d", login.Database! }) start.ArgumentList.Add(a);
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment["PGPASSWORD"] = login?.Password ?? "";

        using var p = Process.Start(start) ?? throw new InvalidOperationException("docker could not be started");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (stdinFile is not null)
        {
            using (var input = File.OpenRead(stdinFile)) input.CopyTo(p.StandardInput.BaseStream);
            p.StandardInput.Close();
        }
        p.WaitForExit();
        if (p.ExitCode == 0) return stdout.Result;

        Directory.CreateDirectory(paths.Cache);
        var log = Path.Combine(paths.Cache, tool + "-stderr.txt");
        File.WriteAllText(log, stderr.Result);
        throw new InvalidOperationException($"{tool} exited {p.ExitCode}; its messages are in {Path.GetRelativePath(paths.Root, log)} (may hold guideline text)");
    }
}
