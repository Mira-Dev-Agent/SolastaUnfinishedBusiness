using System;
using System.IO;
using SolastaUnfinishedBusiness.Models;

var count = 0;
void Check(bool value, string name)
{
    if (!value) { throw new Exception(name); }
    count++;
}
var reports = new BoundedTickReports<string>(5, 400);
Check(reports.Add(200, 100, "early"), "accept early report");
Check(!reports.Add(200, 100, "replacement"), "ignore duplicate");
Check(reports.Take(200, out var early) && early == "early", "retain original early report");
Check(!reports.Take(200, out _), "consume once");
Check(!reports.Add(ulong.MaxValue, 100, "invalid"), "overflow-safe future rejection");
Check(!reports.Add(0, ulong.MaxValue, "invalid"), "overflow-safe past rejection");
for (ulong tick = 100; tick < 120; tick++) { reports.Add(tick, 100, tick.ToString()); }
Check(reports.Count == 5, "hard retention bound");
reports.Prune(1000);
Check(reports.Count == 0, "stale reports expire");

var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var fixture = Path.Combine(root, Guid.NewGuid().ToString("N"));
var source = Path.Combine(fixture, "source");
var target = Path.Combine(fixture, "target");
var transaction = Path.Combine(fixture, "transaction");
Directory.CreateDirectory(source);
Directory.CreateDirectory(target);
File.WriteAllText(Path.Combine(source, "existing"), "new");
File.WriteAllText(Path.Combine(source, "added"), "added");
File.WriteAllText(Path.Combine(target, "existing"), "old");
ModInstallTransaction.Apply(source, target, transaction);
Check(File.ReadAllText(Path.Combine(target, "existing")) == "new", "replacement");
Check(File.Exists(Path.Combine(target, "added")), "new file");
ModInstallTransaction.Recover(target, transaction);
Check(File.ReadAllText(Path.Combine(target, "existing")) == "old", "restore prior file");
Check(!File.Exists(Path.Combine(target, "added")), "remove added file");
ModInstallTransaction.Recover(target, transaction);
Check(File.ReadAllText(Path.Combine(target, "existing")) == "old", "repeat recovery");
try { ModInstallTransaction.Apply(source, target, transaction); throw new Exception("archive overwritten"); }
catch (IOException) { count++; }

var failure = Path.Combine(fixture, "failed-transaction");
using (var locked = new FileStream(Path.Combine(target, "existing"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    try { ModInstallTransaction.Apply(source, target, failure); throw new Exception("lock ignored"); }
    catch (IOException) { count++; }
}
Check(File.ReadAllText(Path.Combine(target, "existing")) == "old", "locked file preserved");
Check(!File.Exists(Path.Combine(target, "added")), "preflight failure leaves installation untouched");
var interrupted = Path.Combine(fixture, "interrupted-transaction");
File.WriteAllText(Path.Combine(target, "added"), "previous-added");
using (var locked = new FileStream(Path.Combine(target, "added"), FileMode.Open, FileAccess.Read, FileShare.Read))
{
    try { ModInstallTransaction.Apply(source, target, interrupted); throw new Exception("replacement lock ignored"); }
    catch (IOException) { count++; }
}
Check(File.Exists(Path.Combine(interrupted, "journal.xml")), "durable inventory survives failed install");
ModInstallTransaction.Recover(target, interrupted);
Check(File.ReadAllText(Path.Combine(target, "existing")) == "old", "retry restores prior replacement");
Check(File.ReadAllText(Path.Combine(target, "added")) == "previous-added", "retry preserves locked-file backup");
try { ModInstallTransaction.Apply(source, source, Path.Combine(fixture, "overlap")); throw new Exception("overlap accepted"); }
catch (IOException) { count++; }
var barrier = new CoordinatedTickBarrier("attempt-a", 120, new[] { 1, 2, 3, 4, 5, 6 });
Check(!barrier.Holds(119) && barrier.Holds(120), "hold at exact boundary");
Check(!barrier.Arrive("old-attempt", 1, 120), "reject stale arrival");
Check(!barrier.Arrive("attempt-a", 1, 119), "reject wrong arrival tick");
Check(!barrier.Arrive("attempt-a", 7, 120), "reject outsider arrival");
for (var actor = 1; actor <= 5; actor++) { barrier.Arrive("attempt-a", actor, 120); }
Check(!barrier.AllArrived && barrier.Holds(120), "wait for sixth slow player");
Check(!barrier.Arrive("attempt-a", 5, 120) && !barrier.AllArrived, "duplicate is not extra player");
Check(barrier.Arrive("attempt-a", 6, 120) && barrier.AllArrived, "complete captured roster");
Check(!barrier.Release("attempt-a", 121), "never apply late");
Check(!barrier.Release("old-attempt", 120), "never release different attempt");
Check(!barrier.RosterMatches(new[] { 1, 2, 3, 4, 5 }), "detect departed player");
Check(!barrier.RosterMatches(new[] { 1, 2, 3, 4, 5, 6, 7 }), "detect joined player");
Check(barrier.Release("attempt-a", 120) && !barrier.Holds(120), "release at agreed tick");
for (var delay = 0; delay < 50; delay++)
{
    var hostBarrier = new CoordinatedTickBarrier("run", 20, new[] { 1, 2 });
    var clientBarrier = new CoordinatedTickBarrier("run", 20, new[] { 1, 2 });
    long hostTick = 0, clientTick = 0;
    for (var frame = 0; frame < 200; frame++)
    {
        if (!hostBarrier.Holds(hostTick)) { hostTick++; }
        if (frame >= delay && frame % 3 == 0 && !clientBarrier.Holds(clientTick)) { clientTick++; }
        hostBarrier.Arrive("run", 1, hostTick);
        hostBarrier.Arrive("run", 2, clientTick);
        if (!hostBarrier.AllArrived) { continue; }
        Check(hostTick == 20 && clientTick == 20, "slow peer reaches same boundary without fast peer crossing");
        Check(hostBarrier.Release("run", hostTick) && clientBarrier.Release("run", clientTick), "shared release tick");
        break;
    }
    Check(hostBarrier.Released && clientBarrier.Released, "eventual release with finite delay");
}
var waiting = new CoordinatedTickBarrier("waiting", 5, new[] { 3, 1, 2 });
Check(string.Join(",", waiting.Missing()) == "1,2,3", "everyone is missing before arriving");
waiting.Arrive("waiting", 2, 5);
Check(string.Join(",", waiting.Missing()) == "1,3", "an arrived player is no longer missing");

// the update helper, when its path is given: install, roll back, and a locked file that fails every attempt
if (args.Length > 1)
{
    var helper = Path.GetFullPath(args[1]);
    var mod = Path.Combine(fixture, "helper-mod");
    var staged = Path.Combine(fixture, "helper-staged");
    var backups = Path.Combine(fixture, "helper-transaction");
    var result = Path.Combine(fixture, "helper-result.txt");
    var log = Path.Combine(fixture, "helper.log");
    Directory.CreateDirectory(mod);
    Directory.CreateDirectory(staged);
    File.WriteAllText(Path.Combine(mod, "Info.json"), "1");
    File.WriteAllText(Path.Combine(staged, "Info.json"), "2");
    File.WriteAllText(Path.Combine(staged, "new.dll"), "new");
    int Run(string mode, string folder)
    {
        var arguments = new[]
        {
            "--mode", mode, "--version", "2", "--target", mod, "--transaction", folder, "--source", staged,
            "--result", result, "--log", log
        };
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(helper,
            string.Join(" ", Array.ConvertAll(arguments, x => "\"" + x + "\""))) { UseShellExecute = false })!;
        process.WaitForExit();
        return process.ExitCode;
    }
    string Value(string key) => Array.Find(File.ReadAllLines(result), x => x.StartsWith(key + "="))![(key.Length + 1)..];
    Check(Run("install", backups) == 0 && Value("status") == "installed", "helper installs");
    Check(File.ReadAllText(Path.Combine(mod, "Info.json")) == "2" && File.Exists(Path.Combine(mod, "new.dll")),
        "helper replaces and adds files");
    Check(Run("rollback", backups) == 0 && Value("status") == "rolledback", "helper rolls back");
    Check(File.ReadAllText(Path.Combine(mod, "Info.json")) == "1" && !File.Exists(Path.Combine(mod, "new.dll")),
        "rollback restores the previous files");
    using (new FileStream(Path.Combine(mod, "Info.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        Check(Run("install", backups + "-locked") == 1 && Value("status") == "failed", "helper reports a failed install");
    }
    Check(Value("restored") == "yes", "a failed install says the previous files are in place");
    Check(File.ReadAllText(Path.Combine(mod, "Info.json")) == "1" && !File.Exists(Path.Combine(mod, "new.dll")),
        "a failed install leaves the previous files");
}

Console.WriteLine($"Passed {count} backend checks. Fixtures: {fixture}");
