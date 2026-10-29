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
Console.WriteLine($"Passed {count} recovery backend checks. Fixtures: {fixture}");
