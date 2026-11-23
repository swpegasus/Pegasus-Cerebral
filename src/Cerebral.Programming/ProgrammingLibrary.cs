using Cerebral.Core;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cerebral.Programming;

public sealed record ProgrammingProjectSummary(string Id, string Name, string RootPath, string Purpose,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MilestoneCount, int OpenItemCount);
public sealed record ProgrammingWorkItem(long Id, long MilestoneId, string Title, string Details, string Kind,
    string Status, string Notes, IReadOnlyList<string> FilePaths, DateTimeOffset UpdatedAt);
public sealed record ProgrammingMilestone(long Id, int Sequence, string Title, string Objective, string Status,
    IReadOnlyList<ProgrammingWorkItem> Items);
public sealed record ProgrammingDecision(long Id, string Title, string Decision, string Rationale, DateTimeOffset CreatedAt);
public sealed record ProgrammingCheck(long Id, string Kind, string Command, string Outcome, string Details, DateTimeOffset CreatedAt);
public sealed record ProgrammingProjectDetails(ProgrammingProjectSummary Project, IReadOnlyList<ProgrammingMilestone> Milestones,
    IReadOnlyList<ProgrammingDecision> Decisions, IReadOnlyList<ProgrammingCheck> Checks);
public sealed record ProgrammingPlanMilestone(string Title, string Objective, IReadOnlyList<ProgrammingPlanItem> Items);
public sealed record ProgrammingPlanItem(string Title, string Details, string Kind);

public sealed class ProgrammingLibrary
{
    private readonly string directory;
    public ProgrammingLibrary(string dataDirectory)
    {
        directory = Path.Combine(Path.GetFullPath(dataDirectory), "Projects");
        Directory.CreateDirectory(directory);
    }

    public object Status() => new { directory, projectCount = List().Count };

    public IReadOnlyList<ProgrammingProjectSummary> List()
    {
        var projects = new List<ProgrammingProjectSummary>();
        var failures = new List<string>();
        Exception? firstFailure = null;
        var failureCount = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "ProgrammingProject.db", SearchOption.AllDirectories))
            {
                try { projects.Add(ReadSummary(path)); }
                catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or FormatException or JsonException or InvalidDataException)
                {
                    firstFailure ??= error;
                    failureCount++;
                    if (failures.Count < 8)
                    {
                        var candidate = Path.GetFileName(Path.GetDirectoryName(path));
                        var id = candidate is { Length: 32 } && candidate.All(char.IsAsciiHexDigit) ? candidate.ToLowerInvariant() : "unidentified project";
                        failures.Add(id + " (" + StorageFailureKind(error) + ")");
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Programming project storage could not be listed. Check storage availability and access, then refresh. No project was removed.", error);
        }
        // Preserve the successful array contract, but never pass an incomplete list as a healthy
        // result or let Create deduplicate against it. UI text contains no private database paths.
        if (failureCount != 0)
            throw new IOException($"Programming project storage could not read {failureCount} project(s): {string.Join("; ", failures)}" +
                (failureCount > failures.Count ? "; additional failures omitted" : "") +
                ". Check the named project storage and access, then refresh. No project was removed or repaired.", firstFailure);
        return projects.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string StorageFailureKind(Exception error) => error switch
    {
        SqliteException sqlite => "database error " + sqlite.SqliteErrorCode,
        UnauthorizedAccessException => "access denied",
        JsonException or FormatException or InvalidDataException => "invalid metadata",
        _ => "I/O error"
    };

    public ProgrammingProjectSummary Create(string name, string rootPath, string purpose)
    {
        name = Required(name, nameof(name), 160); purpose = Bounded(purpose, nameof(purpose), 4000);
        if (!Path.IsPathFullyQualified(rootPath)) throw new ArgumentException("The programming project root must be an absolute path.");
        rootPath = Path.GetFullPath(rootPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var existing = List().FirstOrDefault(x => string.Equals(Path.GetFullPath(x.RootPath), rootPath, comparison));
        if (existing is not null) return existing;
        var id = Guid.NewGuid().ToString("N"); var projectDirectory = Path.Combine(directory, id); Directory.CreateDirectory(projectDirectory);
        var path = Path.Combine(projectDirectory, "ProgrammingProject.db"); EnsureSchema(path);
        var now = DateTimeOffset.UtcNow;
        using var connection = Open(path); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO project(id,name,root_path,purpose,created_at,updated_at) VALUES($id,$name,$root,$purpose,$now,$now);";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$root", rootPath);
        command.Parameters.AddWithValue("$purpose", purpose); command.Parameters.AddWithValue("$now", now.ToString("O")); command.ExecuteNonQuery();
        return ReadSummary(path);
    }

    public ProgrammingProjectDetails Inspect(string projectId)
    {
        var path = ProjectPath(projectId); var summary = ReadSummary(path);
        using var connection = Open(path);
        var items = ReadItems(connection).GroupBy(x => x.MilestoneId).ToDictionary(x => x.Key, x => (IReadOnlyList<ProgrammingWorkItem>)x.ToArray());
        var milestones = new List<ProgrammingMilestone>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,sequence,title,objective,status FROM milestones ORDER BY sequence,id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var work = items.GetValueOrDefault(reader.GetInt64(0), []);
                milestones.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), MilestoneStatus(work), work));
            }
        }
        var decisions = new List<ProgrammingDecision>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,title,decision,rationale,created_at FROM decisions ORDER BY id DESC LIMIT 100;";
            using var reader = command.ExecuteReader(); while (reader.Read()) decisions.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4))));
        }
        var checks = new List<ProgrammingCheck>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,kind,command,outcome,details,created_at FROM checks ORDER BY id DESC LIMIT 100;";
            using var reader = command.ExecuteReader(); while (reader.Read()) checks.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5))));
        }
        return new(summary, milestones, decisions, checks);
    }

    // Domain progress is derived from its work items; it is never workflow execution or check credit.
    private static string MilestoneStatus(IReadOnlyList<ProgrammingWorkItem> items)
    {
        if (items.Any(item => item.Status is not ("planned" or "active" or "blocked" or "done" or "cancelled")))
            throw new InvalidDataException("A programming work item has an invalid status. Inspect its project metadata before continuing.");
        if (items.Count == 0) return "planned";
        if (items.All(item => item.Status == "cancelled")) return "cancelled";
        if (items.All(item => item.Status is "done" or "cancelled")) return "done";
        if (items.Any(item => item.Status == "blocked")) return "blocked";
        return items.Any(item => item.Status is "active" or "done") ? "active" : "planned";
    }

    public ProgrammingProjectDetails SavePlan(string projectId, IReadOnlyList<ProgrammingPlanMilestone> plan)
    {
        if (plan.Count is < 1 or > 50) throw new ArgumentException("A programming plan needs 1-50 milestones.");
        var path = ProjectPath(projectId); using var connection = Open(path); using var transaction = connection.BeginTransaction();
        using (var check = connection.CreateCommand()) { check.Transaction = transaction; check.CommandText = "SELECT COUNT(*) FROM milestones;"; if (Convert.ToInt32(check.ExecuteScalar()) > 0) throw new InvalidOperationException("This project already has a plan. Update its individual work items instead of silently replacing it."); }
        for (var index = 0; index < plan.Count; index++)
        {
            var milestone = plan[index];
            using var add = connection.CreateCommand(); add.Transaction = transaction;
            add.CommandText = "INSERT INTO milestones(sequence,title,objective,status) VALUES($sequence,$title,$objective,'planned'); SELECT last_insert_rowid();";
            add.Parameters.AddWithValue("$sequence", index + 1); add.Parameters.AddWithValue("$title", Required(milestone.Title, "milestone title", 200));
            add.Parameters.AddWithValue("$objective", Bounded(milestone.Objective, "milestone objective", 8000)); var milestoneId = Convert.ToInt64(add.ExecuteScalar());
            if (milestone.Items.Count is < 1 or > 100) throw new ArgumentException("Each milestone needs 1-100 work items.");
            foreach (var item in milestone.Items)
            {
                using var work = connection.CreateCommand(); work.Transaction = transaction;
                work.CommandText = "INSERT INTO work_items(milestone_id,title,details,kind,status,notes,file_paths,updated_at) VALUES($milestone,$title,$details,$kind,'planned','', '[]',$now);";
                work.Parameters.AddWithValue("$milestone", milestoneId); work.Parameters.AddWithValue("$title", Required(item.Title, "work item title", 240));
                work.Parameters.AddWithValue("$details", Bounded(item.Details, "work item details", 12000)); work.Parameters.AddWithValue("$kind", Required(item.Kind, "work item kind", 60));
                work.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); work.ExecuteNonQuery();
            }
        }
        Touch(connection, transaction); transaction.Commit(); return Inspect(projectId);
    }

    public ProgrammingWorkItem UpdateItem(string projectId, long itemId, string status, string notes, IReadOnlyList<string> filePaths)
    {
        if (status is not ("planned" or "active" or "blocked" or "done" or "cancelled")) throw new ArgumentException("Work item status is invalid.");
        if (filePaths.Count > 32 || filePaths.Any(x => x.Length > 1000)) throw new ArgumentException("A work item supports at most 32 bounded file paths.");
        var path = ProjectPath(projectId); using var connection = Open(path); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE work_items SET status=$status,notes=$notes,file_paths=$paths,updated_at=$now WHERE id=$id;";
        command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$notes", Bounded(notes, nameof(notes), 16000));
        command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(filePaths)); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", itemId);
        if (command.ExecuteNonQuery() != 1) throw new ArgumentException("The programming work item was not found.");
        Touch(connection, transaction); transaction.Commit();
        return Inspect(projectId).Milestones.SelectMany(x => x.Items).Single(x => x.Id == itemId);
    }

    public ProgrammingDecision RecordDecision(string projectId, string title, string decision, string rationale) => InsertAndRead(projectId,
        "INSERT INTO decisions(title,decision,rationale,created_at) VALUES($a,$b,$c,$now); SELECT last_insert_rowid();",
        Required(title, nameof(title), 240), Bounded(decision, nameof(decision), 12000), Bounded(rationale, nameof(rationale), 12000),
        id => Inspect(projectId).Decisions.Single(x => x.Id == id));

    public ProgrammingCheck RecordCheck(string projectId, string kind, string commandText, string outcome, string details) => InsertAndRead(projectId,
        "INSERT INTO checks(kind,command,outcome,details,created_at) VALUES($a,$b,$c,$d,$now); SELECT last_insert_rowid();",
        Required(kind, nameof(kind), 60), Bounded(commandText, "command", 4000), Required(outcome, nameof(outcome), 60), Bounded(details, nameof(details), 16000),
        id => Inspect(projectId).Checks.Single(x => x.Id == id));

    public object Context(string projectId, string focus, int maximumBytes)
    {
        maximumBytes = Math.Clamp(maximumBytes, 1024, 24 * 1024);
        focus = Bounded(focus, nameof(focus), 2000);
        var project = Inspect(projectId);
        var active = project.Milestones.SelectMany(milestone => milestone.Items
            .Where(item => item.Status is "planned" or "active" or "blocked")
            .Select(item => new { milestone = milestone.Title, item })).ToArray();
        var decisions = project.Decisions.ToArray();
        var checks = project.Checks.ToArray();
        var activeIds = active.Select(entry => entry.item.Id).ToArray();
        var decisionIds = decisions.Select(entry => entry.Id).ToArray();
        var checkIds = checks.Select(entry => entry.Id).ToArray();
        var limits = new[] { Math.Min(40, active.Length), Math.Min(20, decisions.Length), Math.Min(20, checks.Length) };

        object Projection(bool reducedProject, int[] counts) => new
        {
            project = reducedProject ? (object)new { project.Project.Id, project.Project.RootPath } : project.Project,
            focus,
            active = active.Take(counts[0]).ToArray(),
            decisions = decisions.Take(counts[1]).ToArray(),
            checks = checks.Take(counts[2]).ToArray(),
            truncated = reducedProject || counts[0] < active.Length || counts[1] < decisions.Length || counts[2] < checks.Length,
            omitted = new
            {
                scope = "ordered inspect suffixes; active=planned|active|blocked; history=latest100",
                projectFields = reducedProject
                    ? new[] { "name", "purpose", "createdAt", "updatedAt", "milestoneCount", "openItemCount" } : [],
                active = OmittedSuffix(activeIds, counts[0]),
                decisions = OmittedSuffix(decisionIds, counts[1]),
                checks = OmittedSuffix(checkIds, counts[2])
            },
            recovery = new { name = "program.inspect", arguments = new { projectId = project.Project.Id } }
        };
        // The service serializes with these indented options. Compact/default JSON undercounts
        // the actual response, including omission metadata and escaped Unicode.
        static int Bytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options).Length;
        var full = Projection(false, limits);
        if (Bytes(full) <= maximumBytes) return full;

        var counts = new int[3];
        var bounded = Projection(true, counts);
        var floor = Bytes(bounded);
        if (floor > maximumBytes)
            throw new InvalidOperationException($"Required programming project/root/focus and recovery metadata need {floor} UTF-8 bytes, exceeding maxUtf8Bytes={maximumBytes}. Increase the budget up to 24576 or narrow focus; required values were not clipped.");

        // Admit whole prefixes only. A suffix's first/last IDs are exact recovery endpoints
        // in program.inspect order, not an integer range or an immutable plan revision.
        for (var collection = 0; collection < counts.Length; collection++)
        {
            while (counts[collection] < limits[collection])
            {
                counts[collection]++;
                var candidate = Projection(true, counts);
                if (Bytes(candidate) > maximumBytes) { counts[collection]--; break; }
                bounded = candidate;
            }
        }
        return bounded;
    }

    private static ContextOmission? OmittedSuffix(long[] ids, int included) => included == ids.Length
        ? null : new(ids.Length - included, ids[included], ids[^1]);
    private sealed record ContextOmission(int Count, long FirstId, long LastId);

    private T InsertAndRead<T>(string projectId, string sql, string a, string b, string c, Func<long, T> read)
    {
        var path = ProjectPath(projectId); using var connection = Open(path); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = sql; command.Parameters.AddWithValue("$a", a); command.Parameters.AddWithValue("$b", b); command.Parameters.AddWithValue("$c", c); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var id = Convert.ToInt64(command.ExecuteScalar()); Touch(connection, transaction); transaction.Commit(); return read(id);
    }
    private T InsertAndRead<T>(string projectId, string sql, string a, string b, string c, string d, Func<long, T> read)
    {
        var path = ProjectPath(projectId); using var connection = Open(path); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = sql; command.Parameters.AddWithValue("$a", a); command.Parameters.AddWithValue("$b", b); command.Parameters.AddWithValue("$c", c); command.Parameters.AddWithValue("$d", d);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var id = Convert.ToInt64(command.ExecuteScalar()); Touch(connection, transaction); transaction.Commit(); return read(id);
    }

    private static IReadOnlyList<ProgrammingWorkItem> ReadItems(SqliteConnection connection)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT id,milestone_id,title,details,kind,status,notes,file_paths,updated_at FROM work_items ORDER BY id;";
        using var reader = command.ExecuteReader(); var result = new List<ProgrammingWorkItem>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? [], DateTimeOffset.Parse(reader.GetString(8))));
        return result;
    }
    private ProgrammingProjectSummary ReadSummary(string path)
    {
        EnsureSchema(path); using var connection = Open(path); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,root_path,purpose,created_at,updated_at,(SELECT COUNT(*) FROM milestones),(SELECT COUNT(*) FROM work_items WHERE status NOT IN ('done','cancelled')) FROM project LIMIT 1;";
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw new InvalidDataException("Programming project metadata is missing.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5)), reader.GetInt32(6), reader.GetInt32(7));
    }
    private string ProjectPath(string projectId)
    {
        if (projectId.Length != 32 || projectId.Any(x => !Uri.IsHexDigit(x))) throw new ArgumentException("The programming project ID is invalid.");
        var path = Path.Combine(directory, projectId, "ProgrammingProject.db"); if (!File.Exists(path)) throw new ArgumentException("The programming project was not found."); return path;
    }
    private static SqliteConnection Open(string path) { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString()); connection.Open(); return connection; }
    private static void EnsureSchema(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var connection = Open(path); using var command = connection.CreateCommand(); command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS project(id TEXT PRIMARY KEY,name TEXT NOT NULL,root_path TEXT NOT NULL,purpose TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS milestones(id INTEGER PRIMARY KEY AUTOINCREMENT,sequence INTEGER NOT NULL,title TEXT NOT NULL,objective TEXT NOT NULL,status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS work_items(id INTEGER PRIMARY KEY AUTOINCREMENT,milestone_id INTEGER NOT NULL REFERENCES milestones(id) ON DELETE CASCADE,title TEXT NOT NULL,details TEXT NOT NULL,kind TEXT NOT NULL,status TEXT NOT NULL,notes TEXT NOT NULL,file_paths TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS decisions(id INTEGER PRIMARY KEY AUTOINCREMENT,title TEXT NOT NULL,decision TEXT NOT NULL,rationale TEXT NOT NULL,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS checks(id INTEGER PRIMARY KEY AUTOINCREMENT,kind TEXT NOT NULL,command TEXT NOT NULL,outcome TEXT NOT NULL,details TEXT NOT NULL,created_at TEXT NOT NULL);
            """; command.ExecuteNonQuery();
    }
    private static void Touch(SqliteConnection connection, SqliteTransaction transaction) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE project SET updated_at=$now;"; command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery(); }
    private static string Required(string value, string field, int max) { value = (value ?? "").Trim(); if (value.Length is 0 || value.Length > max || value.Contains('\0')) throw new ArgumentException($"{field} must contain 1-{max} safe characters."); return value; }
    private static string Bounded(string value, string field, int max) { value ??= ""; if (value.Length > max || value.Contains('\0')) throw new ArgumentException($"{field} exceeds {max} safe characters."); return value.Trim(); }
}
