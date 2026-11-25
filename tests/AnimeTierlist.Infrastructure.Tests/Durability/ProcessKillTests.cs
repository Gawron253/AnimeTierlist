using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AnimeTierlist.Infrastructure.Archives;

namespace AnimeTierlist.Infrastructure.Tests.Durability;

/// <summary>
/// Interruption by real process termination. A separate worker process (tests/AnimeTierlist.CrashWorker)
/// performs commits, asset installs and repairs, exports, and imports; the test kills it with TerminateProcess and
/// checks what is on disk. Kills are aimed at specific phases (a staging file appearing, the final rename, a queued
/// refresh transaction), and each test requires evidence that the aimed kills reached those phases. This covers process
/// death, not power loss: the OS keeps buffered writes. ANIMETIERLIST_KILL_ITERATIONS raises the kills per case
/// (default 8).
/// </summary>
public sealed partial class ProcessKillTests : IDisposable
{
    /// <summary>The exit code <see cref="Process.Kill()"/> gives a terminated process; the worker never uses it.</summary>
    private const int TerminatedExitCode = -1;

    /// <summary>The most extra runs a test makes to land a kill inside a short staging write.</summary>
    private const int ExtraAimedRuns = 12;

    private static readonly int Iterations = int.TryParse(Environment.GetEnvironmentVariable("ANIMETIERLIST_KILL_ITERATIONS"), out var n) && n > 1 ? n : 8;
    private static readonly string WorkerPath = FindWorker();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "animetierlist-tests", Guid.NewGuid().ToString("N"));

    public ProcessKillTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Runs alternate between three aims: a cover install (killed when its staging file appears, so before the
    /// "pending" line), a refresh commit (killed 0-2 ms after the worker reports "writing", which SQLite's update hook
    /// prints when the open transaction has written its first row), and a random point. The reopened revision must be
    /// the last committed one, or the queued one when its commit finished first. The test requires a kill inside a
    /// cover install and a refresh kill after "writing" that reopened at the previous revision: the kill landed inside
    /// the open transaction, before its commit record, and the transaction was rolled back. Extra runs aimed at a missing phase follow, up to a cap. Install kills usually land just after
    /// the staging rename; the repair test requires kills inside the staged write itself.
    /// </summary>
    [Fact]
    public async Task Killed_edits_reopen_at_the_last_committed_or_the_next_queued_revision()
    {
        var project = await SeedAsync();
        var assets = Path.Combine(project, "assets");
        var before = await VerifyAsync(project);
        var random = new Random(6);
        var outcomes = new int[3];
        var installs = 0;
        var stagedInstalls = 0;
        var refreshCommits = 0;
        var rolledBack = 0;
        var tails = new List<string>();
        var i = 0;
        for (; i < Iterations || ((installs == 0 || rolledBack == 0) && i < Iterations + ExtraAimedRuns); i++)
        {
            var kind = i < Iterations ? i % 3 : installs == 0 ? 0 : 1;
            var target = 5 * random.Next(1, 8);
            var delay = TimeSpan.FromMilliseconds(random.Next(0, 3));
            var anyStep = random.Next(1, 40);
            var anyDelay = TimeSpan.FromMilliseconds(random.Next(0, 20));
            var armed = 0;
            var refreshes = 0;
            var run = kind switch
            {
                // Arms at a cover install and kills when its staging file appears.
                0 => await RunAsync(["edit", project, "400"], watch: project,
                    onLine: l => { if (l == $"installing {target}") { Volatile.Write(ref armed, 1); } return null; },
                    killOnEvent: e => Volatile.Read(ref armed) == 1 && Path.GetFileName(e.Name)!.StartsWith(".incoming-", StringComparison.Ordinal)),

                // Kills the n-th refresh commit once its open transaction has written a row.
                1 => await RunAsync(["edit", project, "400"], onLine: l => l == "writing" && ++refreshes == target / 5 ? delay : null),
                _ => await RunAsync(["edit", project, "400"], onLine: l => l.StartsWith($"committed {anyStep} ", StringComparison.Ordinal) ? anyDelay : null),
            };
            Assert.True(run.Killed, $"Run {i} was not terminated by the test (exit {run.ExitCode}): {run.Error}");

            var pending = new Dictionary<int, string>();
            var committed = new Dictionary<int, string>();
            foreach (var parts in run.Lines.Select(l => l.Split(' ')).Where(p => p.Length == 3))
            {
                (parts[0] == "pending" ? pending : committed)[int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)] = parts[2];
            }

            Assert.All(committed, c => Assert.Equal(pending[c.Key], c.Value));
            var lastCommitted = committed.Count == 0 ? 0 : committed.Keys.Max();
            var expected = new List<string> { committed.GetValueOrDefault(lastCommitted, before) };
            if (pending.TryGetValue(lastCommitted + 1, out var queued))
            {
                expected.Add(queued);
            }

            var last = run.Lines.LastOrDefault() ?? string.Empty;
            var leftovers = Directory.GetFiles(assets, ".incoming-*");
            Array.ForEach(leftovers, File.Delete);
            var digest = await VerifyAsync(project);
            Assert.True(expected.Contains(digest), $"Run {i}: reopened {digest}, expected one of [{string.Join(", ", expected)}]. Output tail: {string.Join(" | ", run.Lines.TakeLast(4))}");

            var inInstall = kind == 0 && last.StartsWith("installing ", StringComparison.Ordinal);
            var inRefreshCommit = kind == 1 && last == "writing";
            installs += inInstall ? 1 : 0;
            stagedInstalls += inInstall && leftovers.Length > 0 ? 1 : 0;
            refreshCommits += inRefreshCommit ? 1 : 0;
            rolledBack += inRefreshCommit && digest == expected[0] ? 1 : 0;
            if (kind != 2)
            {
                tails.Add($"[{kind}: {last}; {leftovers.Length} staging file(s); {(digest == expected[0] ? "previous" : "queued")}]");
            }

            outcomes[digest == before ? 0 : digest == expected[0] ? 1 : 2]++;
            before = digest;
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{i} runs killed; reopened unchanged {outcomes[0]}, at the last committed edit {outcomes[1]}, at the queued edit {outcomes[2]}; " +
            $"{installs} kill(s) inside a cover install ({stagedInstalls} left its staging file); {refreshCommits} after a refresh transaction wrote its first row, {rolledBack} of them rolled back.");
        Assert.True(installs > 0, "No kill landed inside a cover install: " + string.Join(" ", tails));
        Assert.True(rolledBack > 0, "No refresh kill inside an open transaction was rolled back: " + string.Join(" ", tails));
    }

    /// <summary>
    /// Every cover starts damaged. An uninterrupted repair restores them all; interrupted repairs, killed when a staging
    /// file appears or right after a replacement rename, leave each cover either damaged or the exact original. If no
    /// staging-aimed kill leaves a staging file, extra staging-aimed runs follow, up to a cap.
    /// </summary>
    [Fact]
    public async Task Killed_cover_repair_leaves_each_cover_damaged_or_the_complete_original()
    {
        var project = await SeedAsync();
        var assets = Path.Combine(project, "assets");
        var originals = Directory.EnumerateFiles(assets).ToDictionary(f => f, File.ReadAllBytes);
        var source = Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;
        foreach (var (path, bytes) in originals)
        {
            File.WriteAllBytes(Path.Combine(source, Path.GetFileNameWithoutExtension(path)), bytes);
        }

        void Damage()
        {
            foreach (var path in originals.Keys)
            {
                File.WriteAllBytes(path, "DAMAGED"u8.ToArray());
            }
        }

        Damage();
        var reference = await RunAsync(["repair", project, source]);
        Assert.True(reference.ExitCode == 0, reference.Error);
        Assert.All(originals, o => Assert.Equal(o.Value, File.ReadAllBytes(o.Key)));

        var stagedRepairs = 0;
        var completedReplacements = 0;
        var i = 0;
        for (; i < Iterations || (stagedRepairs == 0 && i < Iterations + ExtraAimedRuns); i++)
        {
            Damage();

            // Spread over the covers, stopping before the last so a replacement kill always leaves some damaged.
            var runsOfAim = (Iterations + (i % 2 == 0 ? 1 : 0)) / 2;
            var at = i < Iterations ? 1 + (i / 2 * (originals.Count - 2) / Math.Max(1, runsOfAim - 1)) : 1 + (i * 17 % (originals.Count - 1));
            var seen = 0;
            var staging = i >= Iterations || i % 2 == 0;
            var run = await RunAsync(["repair", project, source], watch: assets, killOnEvent: e =>
                (staging ? e.ChangeType == WatcherChangeTypes.Created && Path.GetFileName(e.Name)!.StartsWith(".incoming-", StringComparison.Ordinal)
                         : e.ChangeType == WatcherChangeTypes.Renamed && !Path.GetFileName(e.Name)!.StartsWith('.')) && Interlocked.Increment(ref seen) == at,
                lingerMilliseconds: 300);
            Assert.True(run.Killed, $"Run {i} was not terminated by the test (exit {run.ExitCode}): {run.Error}");

            var restored = 0;
            foreach (var (path, bytes) in originals)
            {
                var actual = File.ReadAllBytes(path);
                var original = actual.AsSpan().SequenceEqual(bytes);
                Assert.True(original || actual.AsSpan().SequenceEqual("DAMAGED"u8), $"{Path.GetFileName(path)} is neither the original nor the damaged file ({actual.Length} bytes).");
                restored += original ? 1 : 0;
            }

            var leftovers = Directory.GetFiles(assets).Where(f => !originals.ContainsKey(f)).ToList();
            Assert.All(leftovers, f => Assert.StartsWith(".incoming-", Path.GetFileName(f), StringComparison.Ordinal));
            stagedRepairs += staging && leftovers.Count > 0 ? 1 : 0;
            completedReplacements += !staging && restored > 0 && restored < originals.Count ? 1 : 0;
            leftovers.ForEach(File.Delete);
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{i} repairs of {originals.Count} damaged covers killed; {stagedRepairs} inside a staged repair, {completedReplacements} right after a completed replacement, none partially repaired.");
        Assert.True(stagedRepairs > 0, "No kill landed inside a staged repair.");
        Assert.True(completedReplacements > 0, "No kill landed right after a completed replacement.");
    }

    [Fact]
    public async Task Killed_png_replacement_leaves_the_previous_file_or_the_complete_new_png()
    {
        var project = await SeedAsync();
        static void Previous(string dir) => File.WriteAllText(Path.Combine(dir, "board.png"), "previous export");
        string[] Args(string dir) => ["export-png", project, Path.Combine(dir, "board.png"), "1"];
        var (referenceDir, reference) = await ReferenceAsync("png", Previous, Args, PngStaging());
        var expected = File.ReadAllBytes(Path.Combine(referenceDir, "board.png"));
        await SweepAsync("png", Previous, Args, "board.png", PngStaging(), reference, dir =>
        {
            var actual = File.ReadAllBytes(Path.Combine(dir, "board.png"));
            var replaced = actual.AsSpan().SequenceEqual(expected);
            Assert.True(replaced || actual.AsSpan().SequenceEqual("previous export"u8), "The destination is neither the previous file nor the complete export.");
            return Task.FromResult(replaced);
        });
    }

    [Fact]
    public async Task Killed_cover_export_leaves_no_folder_or_the_complete_export()
    {
        var project = await SeedAsync();
        string[] Args(string dir) => ["export-covers", project, Path.Combine(dir, "covers")];
        var (referenceDir, reference) = await ReferenceAsync("covers", _ => { }, Args, CoverStaging());
        var expected = Tree(Path.Combine(referenceDir, "covers"));
        await SweepAsync("covers", _ => { }, Args, "covers", CoverStaging(), reference, dir =>
        {
            var destination = Path.Combine(dir, "covers");
            Assert.False(File.Exists(destination), "A file is at the export folder's path.");
            var installed = Directory.Exists(destination);
            if (installed)
            {
                Assert.Equal(expected, Tree(destination));
            }

            return Task.FromResult(installed);
        });
    }

    [Fact]
    public async Task Killed_archive_replacement_leaves_the_previous_file_or_a_valid_complete_archive()
    {
        var project = await SeedAsync();
        var source = await VerifyAsync(project);
        static void Previous(string dir) => File.WriteAllText(Path.Combine(dir, "project.animerank"), "previous archive");
        string[] Args(string dir) => ["export-archive", project, Path.Combine(dir, "project.animerank"), "1"];
        var (_, reference) = await ReferenceAsync("archive", Previous, Args, ExportStaging());
        await SweepAsync("archive", Previous, Args, "project.animerank", ExportStaging(), reference, async dir =>
        {
            var archive = Path.Combine(dir, "project.animerank");
            if (File.ReadAllText(archive) == "previous archive")
            {
                return false;
            }

            await ProjectArchiveImporter.ValidateAsync(archive, ArchiveLimits.Default, Ct);
            var imported = Path.Combine(_root, "imported " + Path.GetFileName(dir));
            await ProjectArchiveImporter.ImportAsync(archive, imported, ArchiveLimits.Default, Ct);
            Assert.Equal(source, await VerifyAsync(imported));
            return true;
        });
    }

    [Fact]
    public async Task Killed_import_leaves_no_project_or_the_complete_project()
    {
        var project = await SeedAsync();
        var source = await VerifyAsync(project);
        var archive = Path.Combine(_root, "source.animerank");
        Assert.Equal(0, (await RunAsync(["export-archive", project, archive, "0"])).ExitCode);
        string[] Args(string dir) => ["import", archive, Path.Combine(dir, "project")];
        var (referenceDir, reference) = await ReferenceAsync("import", _ => { }, Args, ImportStaging());
        var expected = Tree(Path.Combine(referenceDir, "project"));
        await SweepAsync("import", _ => { }, Args, "project", ImportStaging(), reference, async dir =>
        {
            var destination = Path.Combine(dir, "project");
            Assert.False(File.Exists(destination), "A file is at the project folder's path.");
            var installed = Directory.Exists(destination);
            if (installed)
            {
                Assert.Equal(expected, Tree(destination));
                Assert.Equal(source, await VerifyAsync(destination));
            }

            return installed;
        });
    }

    [GeneratedRegex(@"^\.board\.png\.[0-9a-f]{32}\.tmp$")]
    private static partial Regex PngStaging();

    [GeneratedRegex(@"^\.covers\.staging-[0-9a-f]{8}$")]
    private static partial Regex CoverStaging();

    // The exporter validates its temporary archive with the importer, which stages beside it.
    [GeneratedRegex(@"^\.animerank-(export-[0-9a-f]{32}(\.tmp)?|import-[0-9a-f]{32})$")]
    private static partial Regex ExportStaging();

    [GeneratedRegex(@"^\.animerank-import-[0-9a-f]{32}$")]
    private static partial Regex ImportStaging();

    private async Task<string> SeedAsync()
    {
        var project = Path.Combine(_root, "project");
        var run = await RunAsync(["seed", project, "120"]);
        Assert.True(run.ExitCode == 0, run.Error);
        return project;
    }

    private static async Task<string> VerifyAsync(string project)
    {
        var run = await RunAsync(["verify", project]);
        Assert.True(run.ExitCode == 0, "Verification failed: " + run.Error);
        return run.Lines.Single(l => l.StartsWith("digest ", StringComparison.Ordinal))["digest ".Length..];
    }

    /// <summary>The first path component of a watcher event, which is what sits beside the destination.</summary>
    private static string TopLevel(FileSystemEventArgs e) => e.Name!.Split(Path.DirectorySeparatorChar)[0];

    /// <summary>Runs the operation uninterrupted in its own folder, counting the file-system events inside its staging.</summary>
    private async Task<(string Directory, WorkerRun Run)> ReferenceAsync(string name, Action<string> prepare, Func<string, string[]> args, Regex staging)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name + " reference")).FullName;
        prepare(dir);
        var run = await RunAsync(args(dir), watch: dir, countEvent: e => staging.IsMatch(TopLevel(e)));
        Assert.True(run.ExitCode == 0, run.Error);
        Assert.True(run.Events > 0, "The reference run caused no staging events.");
        return (dir, run);
    }

    /// <summary>
    /// Repeats the operation in fresh folders with three kinds of kill: at the n-th event inside the operation's
    /// staging (spread over the reference count), at the event that installs the final name, and at times spread
    /// over the reference duration. The worker lingers briefly after finishing so the final-name kill lands before it
    /// exits. <paramref name="installed"/> asserts that the folder holds the previous or the complete new state and
    /// says which. Besides the destination, only the operation's staging entries may remain. Staging kills must
    /// leave the previous state with staging beside it at least once, and final-name kills the complete output.
    /// </summary>
    private async Task SweepAsync(string name, Action<string> prepare, Func<string, string[]> args, string final, Regex staging, WorkerRun reference, Func<string, Task<bool>> installed)
    {
        var finals = Math.Max(1, Iterations / 8);
        var stagings = Iterations / 2;
        var timed = Iterations - stagings - finals;
        var plans = Enumerable.Range(0, stagings).Select(i => (Kind: "staging", At: 1 + (int)Math.Round((reference.Events - 1) * (double)i / Math.Max(1, stagings - 1)), After: (TimeSpan?)null))
            .Concat(Enumerable.Range(0, finals).Select(_ => (Kind: "final", At: 0, After: (TimeSpan?)null)))
            .Concat(Enumerable.Range(0, timed).Select(i => (Kind: "timed", At: 0, After: (TimeSpan?)(reference.Elapsed * (0.2 + (0.9 * i / Math.Max(1, timed - 1)))))));
        var counts = new Dictionary<string, int> { ["killed"] = 0, ["previous"] = 0, ["staged"] = 0, ["installed"] = 0 };
        var n = 0;
        foreach (var plan in plans)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, $"{name} {++n}")).FullName;
            prepare(dir);
            var seen = 0;
            Func<FileSystemEventArgs, bool>? trigger = plan.Kind switch
            {
                "staging" => e => staging.IsMatch(TopLevel(e)) && Interlocked.Increment(ref seen) == plan.At,
                "final" => e => e.Name == final && e.ChangeType is WatcherChangeTypes.Renamed or WatcherChangeTypes.Created,
                _ => null,
            };
            var run = await RunAsync(args(dir), watch: dir, killOnEvent: trigger, killAfter: plan.After, lingerMilliseconds: 300);

            // Listed before the check, which may stage its own validation files beside the output.
            var entries = Directory.EnumerateFileSystemEntries(dir).Select(e => Path.GetFileName(e)!).ToList();
            Assert.All(entries, entry => Assert.True(entry == final || staging.IsMatch(entry), $"Unexpected entry '{entry}' after {plan}."));
            var complete = await installed(dir);
            if (!run.Killed)
            {
                Assert.True(run.ExitCode == 0 && complete, $"A run the test did not terminate must finish and install ({plan}): exit {run.ExitCode} {run.Error}");
            }

            counts["killed"] += run.Killed ? 1 : 0;
            counts["previous"] += run.Killed && !complete ? 1 : 0;
            counts["staged"] += plan.Kind == "staging" && run.Killed && !complete && entries.Any(staging.IsMatch) ? 1 : 0;
            counts["installed"] += plan.Kind == "final" && run.Killed && complete ? 1 : 0;
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{counts["killed"]} of {Iterations} runs killed ({reference.Events} reference staging events); {counts["previous"]} left the previous state, " +
            $"{counts["killed"] - counts["previous"]} the complete new output; {counts["staged"]} staging kill(s) left staging beside the previous state, " +
            $"{counts["installed"]} final-name kill(s) found the complete output.");
        Assert.True(counts["killed"] >= Iterations / 2, $"Only {counts["killed"]} of {Iterations} runs were interrupted.");
        Assert.True(counts["staged"] > 0, "No staging kill left staging beside the previous state.");
        Assert.True(counts["installed"] > 0, "No final-name kill found the complete output.");
    }

    /// <summary>Every entry below <paramref name="directory"/> by relative path: files with their content hash, folders as such.</summary>
    private static SortedDictionary<string, string> Tree(string directory) =>
        new(Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(directory, f), f => Directory.Exists(f) ? "<folder>" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))), StringComparer.Ordinal);

    /// <summary>
    /// Starts the worker and terminates it after <paramref name="killAfter"/>, at the first file-system event in
    /// <paramref name="watch"/> that <paramref name="killOnEvent"/> accepts, or after the delay that
    /// <paramref name="onLine"/> returns for an output line. A run counts as killed only when the test terminated it.
    /// </summary>
    private static async Task<WorkerRun> RunAsync(
        string[] args,
        string? watch = null,
        Func<string, TimeSpan?>? onLine = null,
        Func<FileSystemEventArgs, bool>? killOnEvent = null,
        Func<FileSystemEventArgs, bool>? countEvent = null,
        TimeSpan? killAfter = null,
        int lingerMilliseconds = 0)
    {
        var start = new ProcessStartInfo(WorkerPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["ANIMETIERLIST_WORKER_LINGER_MS"] = lingerMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var kill = new CancellationTokenSource();
        var events = 0;
        using var watcher = watch is null ? null : new FileSystemWatcher(watch)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        if (watcher is not null)
        {
            void OnEvent(object sender, FileSystemEventArgs e)
            {
                if (countEvent?.Invoke(e) == true)
                {
                    Interlocked.Increment(ref events);
                }

                if (killOnEvent?.Invoke(e) == true)
                {
                    kill.Cancel();
                }
            }

            watcher.Created += OnEvent;
            watcher.Changed += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += (sender, e) => OnEvent(sender, e);
            watcher.EnableRaisingEvents = true;
        }

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The worker did not start.");
        try
        {
            if (killAfter is { } delay)
            {
                kill.CancelAfter(delay);
            }

            var lines = new List<string>();
            var error = process.StandardError.ReadToEndAsync(Ct);
            var output = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(Ct) is { } line)
                {
                    lines.Add(line);
                    if (onLine?.Invoke(line) is { } after)
                    {
                        kill.CancelAfter(after);
                    }
                }
            }, Ct);

            var terminated = false;
            try
            {
                await process.WaitForExitAsync(kill.Token).WaitAsync(TimeSpan.FromMinutes(2), Ct);
            }
            catch (OperationCanceledException) when (kill.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    terminated = true;
                }
                catch (InvalidOperationException)
                {
                    // It exited on its own first.
                }

                await process.WaitForExitAsync(Ct);
            }

            var elapsed = clock.Elapsed;
            await output;
            return new WorkerRun(process.ExitCode, terminated && process.ExitCode == TerminatedExitCode, lines, await error, elapsed, Volatile.Read(ref events));
        }
        finally
        {
            // A timeout or test cancellation must not leave the worker writing into a folder that cleanup deletes.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }

    private static string FindWorker()
    {
        // tests/AnimeTierlist.Infrastructure.Tests/bin/<Configuration>/<TargetFramework>/ → tests/AnimeTierlist.CrashWorker/bin/<Configuration>/<TargetFramework>/
        var framework = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = framework.Parent!;
        var tests = configuration.Parent!.Parent!.Parent!;
        var path = Path.Combine(tests.FullName, "AnimeTierlist.CrashWorker", "bin", configuration.Name, framework.Name, "AnimeTierlist.CrashWorker.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException("Build the solution so the crash worker exists.", path);
    }

    private sealed record WorkerRun(int ExitCode, bool Killed, List<string> Lines, string Error, TimeSpan Elapsed, int Events);
}
