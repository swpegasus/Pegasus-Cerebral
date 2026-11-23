using System.Text;
using Cerebral.Programming;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

internal static class ProgrammingCohesiveLibraryTests
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-programming-cohesive-" + Guid.NewGuid().ToString("N"));
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        void RemoveOwned(string path)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside the owned fixture.");
            SqliteConnection.ClearAllPools(); Directory.Delete(full, true);
        }
        try
        {
            Directory.CreateDirectory(root);
            var data = Path.Combine(root, "PrivateStorage"); var source = Path.Combine(root, "Source"); Directory.CreateDirectory(source);
            var library = new ProgrammingLibrary(data); var healthy = library.Create("Healthy", source, "");
            Check(library.Create("Same root", source, "").Id == healthy.Id && library.List().Count == 1, "healthy roots still reuse the existing project");
            var plan = library.SavePlan(healthy.Id, [new("Milestone", "", [new("One", "", "code"), new("Two", "", "test")])]);
            var first = plan.Milestones[0].Items[0].Id; var second = plan.Milestones[0].Items[1].Id;
            string Status() => library.Inspect(healthy.Id).Milestones[0].Status;
            Check(Status() == "planned", "initial milestone is planned");
            library.UpdateItem(healthy.Id, first, "done", "", []); Check(Status() == "active", "partial completion advances milestone progress");
            library.UpdateItem(healthy.Id, second, "blocked", "", []); Check(Status() == "blocked", "blocked work is visible at milestone level");
            library.UpdateItem(healthy.Id, second, "done", "", []); Check(Status() == "done", "all completed items complete the milestone");
            library.UpdateItem(healthy.Id, first, "cancelled", "", []); library.UpdateItem(healthy.Id, second, "cancelled", "", []);
            Check(Status() == "cancelled" && library.Inspect(healthy.Id).Checks.Count == 0, "cancelled domain work creates no execution/check credit");
            library.UpdateItem(healthy.Id, first, "planned", "", []); library.UpdateItem(healthy.Id, second, "planned", "", []);
            Check(Status() == "planned", "replanned work restores planned progress");
            var projects = Path.Combine(data, "Projects");
            foreach (var fault in new[] { "corrupt", "missing-metadata", "locked" })
            {
                var id = Guid.NewGuid().ToString("N"); var folder = Path.Combine(projects, id); Directory.CreateDirectory(folder);
                var database = Path.Combine(folder, "ProgrammingProject.db");
                if (fault == "corrupt") File.WriteAllText(database, "This is not a SQLite database.", Encoding.UTF8);
                else if (fault == "missing-metadata")
                {
                    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()); connection.Open();
                    using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=1;"; command.ExecuteNonQuery();
                }
                else
                {
                    Directory.Delete(folder);
                    var locked = library.Create("Locked", Path.Combine(root, "LockedSource"), ""); id = locked.Id; folder = Path.Combine(projects, id); database = Path.Combine(folder, "ProgrammingProject.db");
                }
                SqliteConnection.ClearAllPools();
                FileStream? lockedFile = fault == "locked" ? new(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
                try
                {
                    var listedFailure = false;
                    try { _ = library.List(); }
                    catch (IOException error)
                    {
                        listedFailure = error.Message.Contains(id, StringComparison.Ordinal) && !error.Message.Contains(root, StringComparison.OrdinalIgnoreCase);
                    }
                    Check(listedFailure, fault + " is reported with an opaque project identity, without private storage paths");
                    var before = Directory.GetDirectories(projects).Length; var creationBlocked = false;
                    try { _ = library.Create("Must not infer absence", Path.Combine(root, "UnknownRoot"), ""); }
                    catch (IOException) { creationBlocked = true; }
                    Check(creationBlocked && Directory.GetDirectories(projects).Length == before,
                        fault + " prevents incomplete discovery from allocating a new project database");
                }
                finally { lockedFile?.Dispose(); RemoveOwned(folder); }
                Check(library.List().Count == 1 && library.Create("Healthy reuse", source, "").Id == healthy.Id, "healthy reuse remains exact after " + fault + " is removed");
            }
            return count;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing fixture cleanup outside temporary storage.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
