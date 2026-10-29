using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SolastaUnfinishedBusiness.Models;

// Filesystem backend. Call only after package identity/integrity validation and game exit.
// Keep each transaction until its backup is no longer needed; never reuse a failed archive.
internal static class ModInstallTransaction
{
    internal static void Apply(string source, string target, string transaction)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        transaction = Path.GetFullPath(transaction);
        if (Directory.Exists(transaction)) { throw new IOException("Transaction already exists; recover it first."); }
        CheckParents(source);
        CheckParents(target);
        CheckParents(transaction);
        if (Contains(source, target) || Contains(target, source) || Contains(source, transaction) ||
            Contains(transaction, source) || Contains(target, transaction) || Contains(transaction, target))
        {
            throw new IOException("Staging, installation and transaction directories must be separate.");
        }
        using var lease = new FileStream(Path.Combine(target, ".fb-installer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var files = EnumerateSafe(source).ToArray();
        if (files.Length == 0) { throw new IOException("Empty update."); }
        Directory.CreateDirectory(transaction);
        var plan = new XElement("files");
        foreach (var file in files)
        {
            CheckParents(file);
            var relative = file.Substring(source.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
            var destination = Resolve(target, relative);
            CheckParents(destination);
            var backup = Resolve(Path.Combine(transaction, "backup"), relative);
            var staged = Resolve(Path.Combine(transaction, "staged"), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.Copy(file, staged);
            var existed = File.Exists(destination);
            if (existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(destination, backup);
            }
            plan.Add(new XElement("file", new XAttribute("path", relative), new XAttribute("existed", existed)));
        }
        // Persist the complete backup inventory before changing any installed file.
        using (var stream = new FileStream(Path.Combine(transaction, "journal.xml"), FileMode.CreateNew))
        {
            plan.Save(stream);
            stream.Flush(true);
        }
        try
        {
            foreach (var entry in plan.Elements())
            {
                var relative = (string)entry.Attribute("path");
                Replace(Resolve(Path.Combine(transaction, "staged"), relative), Resolve(target, relative));
            }
            File.WriteAllText(Path.Combine(transaction, "complete"), "installed");
        }
        catch
        {
            RecoverCore(target, transaction);
            throw;
        }
    }

    internal static void Recover(string target, string transaction)
    {
        CheckParents(target);
        CheckParents(transaction);
        using var lease = new FileStream(Path.Combine(target, ".fb-installer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        RecoverCore(target, transaction);
    }

    private static void RecoverCore(string target, string transaction)
    {
        // Backups are copied, never consumed: recovery can be retried after a locked-file failure.
        var journal = Path.Combine(transaction, "journal.xml");
        var plan = XElement.Load(journal);
        var entries = plan.Elements("file").ToArray();
        foreach (var entry in entries)
        {
            var relative = (string)entry.Attribute("path");
            var destination = Resolve(target, relative);
            CheckParents(destination);
            if ((bool)entry.Attribute("existed"))
            {
                var backup = Resolve(Path.Combine(transaction, "backup"), relative);
                CheckParents(backup);
                if (!File.Exists(backup)) { throw new IOException("Recovery backup missing: " + relative); }
            }
        }
        foreach (var entry in entries)
        {
            var relative = (string)entry.Attribute("path");
            var destination = Resolve(target, relative);
            if ((bool)entry.Attribute("existed"))
            {
                Replace(Resolve(Path.Combine(transaction, "backup"), relative), destination);
            }
            else if (File.Exists(destination)) { File.Delete(destination); }
        }
        File.WriteAllText(Path.Combine(transaction, "recovered"), "restored");
    }

    private static void Replace(string source, string destination)
    {
        CheckParents(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".fb-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary);
            if (File.Exists(destination)) { File.Replace(temporary, destination, null); }
            else { File.Move(temporary, destination); }
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private static string Resolve(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(prefix, relative));
        if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf(':') >= 0 ||
            relative.Equals(".fb-installer.lock", StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(relative) || !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Update path escapes destination.");
        }
        return full;
    }

    private static bool Contains(string parent, string child)
    {
        var prefix = parent.TrimEnd(Path.DirectorySeparatorChar);
        return child.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
               child.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateSafe(string directory)
    {
        CheckParents(directory);
        foreach (var file in Directory.GetFiles(directory))
        {
            CheckParents(file);
            yield return file;
        }
        foreach (var child in Directory.GetDirectories(directory))
        {
            foreach (var file in EnumerateSafe(child)) { yield return file; }
        }
    }

    private static void CheckParents(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Update paths cannot contain directory links.");
            }
        }
    }
}
