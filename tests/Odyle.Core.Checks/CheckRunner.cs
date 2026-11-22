using System.Diagnostics;

// Runs the check groups one after another. A failing group is reported and the next one runs, so one
// run lists every failure; the exit code is 1 when any group failed.
//
// Arguments: --only <names> (comma-separated, matched case-insensitively against a group's name, a
// part of it is enough: "realtime" runs Realtime and RealtimeClient), --list (names the groups), and
// optionally [<server URL>] <pairing code>, which adds the live pairing checks against that backend.
sealed class CheckRunner
{
    readonly List<(string Name, Func<Task> Run)> groups = [];
    readonly string[]? only;
    readonly bool list;
    // The positional arguments: [<server URL>] <pairing code> for the live checks, or none.
    public string[] Pairing { get; }
    public string? Problem { get; }

    public CheckRunner(string[] args)
    {
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--list") list = true;
            else if (arg == "--only" && i + 1 < args.Length) only = Names(args[++i]);
            else if (arg.StartsWith("--only=", StringComparison.Ordinal)) only = Names(arg["--only=".Length..]);
            else if (arg.StartsWith("--", StringComparison.Ordinal)) Problem = "Unknown option " + arg + ". Use --only <groups>, --list, or [<server URL>] <pairing code>.";
            else positional.Add(arg);
        }
        if (positional.Count > 2) Problem = "Expected at most a server URL and a pairing code, got " + positional.Count + " arguments.";
        Pairing = [.. positional];
    }
    static string[] Names(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Add(string name, Action run) => groups.Add((name, () => { run(); return Task.CompletedTask; }));
    public void Add(string name, Func<Task> run) => groups.Add((name, run));

    public async Task<int> RunAsync()
    {
        if (Problem is not null) { Console.Error.WriteLine(Problem); return 2; }
        if (list) { foreach (var (name, _) in groups) Console.WriteLine(name); return 0; }
        var chosen = groups.Where(g => only is null || only.Any(o => g.Name.Contains(o, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (chosen.Length == 0) { Console.Error.WriteLine($"No check group matches \"{string.Join(",", only ?? [])}\". Groups: {string.Join(", ", groups.Select(g => g.Name))}."); return 2; }
        var failed = new List<string>();
        foreach (var (name, run) in chosen)
        {
            var clock = Stopwatch.StartNew();
            try { await run(); }
            catch (Exception error)
            {
                failed.Add(name);
                // A failed Check says where and what; anything else gets its stack trace.
                Console.Error.WriteLine($"FAILED {name} after {clock.ElapsedMilliseconds} ms: " + (error is CheckFailed ? error.Message : error.ToString()));
            }
        }
        Console.WriteLine(failed.Count == 0
            ? $"All {chosen.Length} check groups passed."
            : $"{failed.Count} of {chosen.Length} check groups failed: {string.Join(", ", failed)}.");
        return failed.Count == 0 ? 0 : 1;
    }
}
