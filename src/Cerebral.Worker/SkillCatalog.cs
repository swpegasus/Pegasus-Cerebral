using System.Text;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>
/// Owner-managed instruction packages. Content is stored in SQLite, selected by explicit scope, and projected below
/// Cerebral's host protocol. A skill cannot add executable capability or change authorization.
/// </summary>
public sealed class SkillCatalog(StateStore store, EventHub? events = null)
{
    public const string RecordKind = "skill";
    public const int MaximumContentUtf8Bytes = 32 * 1024;
    public const int MaximumInstalledSkills = 128;
    public const int MaximumResolvedSkills = 32;
    private const int StorageSchemaVersion = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private sealed record StoredSkill(int SchemaVersion, SkillManifest Manifest, string Content, string Source,
        bool BuiltIn, bool Enabled, IReadOnlyList<SkillBinding> Bindings, int Revision);

    private static readonly SkillManifest ResponseBalanceManifest = new()
    {
        Id = "response-balance",
        Name = "Response balance",
        Version = "1.0.0",
        Description = "Adjust the next model generation between retained input context and visible response output.",
        Triggers = ["response limit", "context limit", "token balance", "response balance", "go@", "length outcome", "output budget"]
    };

    private const string ResponseBalanceContent = """
        # Response balance

        Use this skill when a Pegasus Cerebral assignment cannot leave enough room for the next useful response, or when the next run needs more retained input context.

        - Call `conversation.response-balance` with `responseSharePercent` from 5 through 50.
        - The percentage applies only to the next model generation in the current durable assignment.
        - A larger response share leaves less room for retained input. A smaller share leaves more room for input.
        - The command never enlarges the loaded model context and cannot override required-input or safety reserves.
        - After a Length outcome, use Cerebral's exact prepared input, effective-context, response-budget, and safety-reserve counts. Increase the response share only when the same measured input leaves unused output capacity; otherwise compact reducible context or split the work.
        - Regenerate the same unfinished logical action. Never execute partial model output or repeat a completed tool effect.

        The equivalent user shorthand is `go@N`; plain `go` keeps the conversation's last accepted balance.
        """;

    public void EnsureBuiltIns()
    {
        store.Locked(() =>
        {
            var current = store.Get<StoredSkill>(RecordKind, ResponseBalanceManifest.Id);
            if (current is null)
            {
                store.Put(RecordKind, ResponseBalanceManifest.Id, new StoredSkill(StorageSchemaVersion,
                    Copy(ResponseBalanceManifest), ResponseBalanceContent, "Built-in Pegasus Cerebral skill", true, true,
                    [new("global")], 1));
            }
            else if (current.BuiltIn && (current.Manifest.Version != ResponseBalanceManifest.Version ||
                     current.Content != ResponseBalanceContent))
            {
                store.Put(RecordKind, ResponseBalanceManifest.Id, current with
                {
                    Manifest = Copy(ResponseBalanceManifest), Content = ResponseBalanceContent,
                    Revision = checked(current.Revision + 1)
                });
            }
            return true;
        });
    }

    public IReadOnlyList<SkillSummary> List()
    {
        EnsureBuiltIns();
        return store.List<StoredSkill>(RecordKind).Select(ValidateStored).OrderBy(x => x.Manifest.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Summary).ToArray();
    }

    public IReadOnlyList<ConfigurationBackupSkill> ExportForBackup()
    {
        EnsureBuiltIns();
        return store.List<StoredSkill>(RecordKind).Select(ValidateStored).OrderBy(x => x.Manifest.Id, StringComparer.Ordinal)
            .Select(x => new ConfigurationBackupSkill(Copy(x.Manifest), x.BuiltIn ? null : x.Content, x.Enabled,
                x.Bindings.ToArray(), x.BuiltIn)).ToArray();
    }

    public static void ValidateBackupSkill(ConfigurationBackupSkill item)
    {
        ArgumentNullException.ThrowIfNull(item); ValidateManifest(item.Manifest);
        if (item.Bindings is null || item.Bindings.Count > 64) throw new ArgumentException("A restored skill requires at most 64 bindings.");
        foreach (var binding in item.Bindings) _ = ValidateBinding(binding);
        if (item.BuiltIn)
        {
            if (item.Content is not null) throw new ArgumentException("Built-in skill backups cannot carry program-owned content.");
        }
        else
        {
            if (item.Content is null) throw new ArgumentException("Portable skills require their bounded SKILL.md content.");
            ValidateContent(item.Content);
        }
    }

    /// <summary>Joins the caller's restore transaction. Built-in content stays owned by the destination build.</summary>
    public void UpsertForRestore(ConfigurationBackupSkill item)
    {
        ValidateBackupSkill(item);
        var bindings = item.Bindings.Select(ValidateBinding).Distinct().OrderBy(x => x.Scope, StringComparer.Ordinal)
            .ThenBy(x => x.Target, StringComparer.Ordinal).ToArray();
        EnsureBuiltIns();
        store.Locked(() =>
        {
            var current = store.Get<StoredSkill>(RecordKind, item.Manifest.Id);
            if (item.BuiltIn)
            {
                if (item.Content is not null) throw new ArgumentException("Built-in skill backups cannot carry program-owned content.");
                if (current?.BuiltIn != true) throw new ArgumentException("The destination does not provide the referenced built-in skill.");
                store.Put(RecordKind, item.Manifest.Id, current with
                {
                    Enabled = item.Enabled, Bindings = bindings, Revision = checked(current.Revision + 1)
                });
                return true;
            }
            if (current?.BuiltIn == true) throw new ArgumentException("A portable skill cannot replace a built-in skill.");
            if (item.Content is null) throw new ArgumentException("Portable skills require their bounded SKILL.md content.");
            ValidateContent(item.Content);
            var next = new StoredSkill(StorageSchemaVersion, Copy(item.Manifest), item.Content, "Configuration restore",
                false, item.Enabled, bindings, checked((current?.Revision ?? 0) + 1));
            store.Put(RecordKind, item.Manifest.Id, next); return true;
        });
    }

    public SkillInspection Inspect(string id)
    {
        EnsureBuiltIns(); Configuration.ValidateId(id);
        var value = ValidateStored(store.Get<StoredSkill>(RecordKind, id) ?? throw new KeyNotFoundException("Skill was not found."));
        return new(Summary(value), value.Content);
    }

    public SkillInspection Install(SkillManifest manifest, string content, string source, int? expectedRevision)
    {
        EnsureBuiltIns(); ValidateManifest(manifest); ValidateContent(content);
        if (string.IsNullOrWhiteSpace(source) || source.Length > 2048 || source.Contains('\0'))
            throw new ArgumentException("Skill source must be a bounded nonempty label or path.");
        var saved = store.Locked(() =>
        {
            var current = store.Get<StoredSkill>(RecordKind, manifest.Id);
            if (current?.BuiltIn == true) throw new InvalidOperationException("Built-in skills are updated with Cerebral.");
            if (current is null && expectedRevision is not null)
                throw new InvalidOperationException("The skill no longer exists. Refresh before updating it.");
            if (current is not null && expectedRevision is null)
                throw new InvalidOperationException("This skill is already installed. Inspect it and supply its current revision to update it.");
            if (current is not null && current.Revision != expectedRevision)
                throw new InvalidOperationException("The skill changed since it was inspected. Refresh before updating it.");
            if (current is null && store.List<StoredSkill>(RecordKind).Count >= MaximumInstalledSkills)
                throw new InvalidOperationException($"At most {MaximumInstalledSkills} skills may be installed.");
            var next = new StoredSkill(StorageSchemaVersion, Copy(manifest), content, source, false,
                current?.Enabled ?? false, current?.Bindings ?? [], checked((current?.Revision ?? 0) + 1));
            store.Put(RecordKind, manifest.Id, next); return next;
        });
        Changed(saved.Manifest.Id); return new(Summary(saved), saved.Content);
    }

    public SkillInspection Configure(string id, bool enabled, IReadOnlyList<SkillBinding> bindings, int expectedRevision)
    {
        EnsureBuiltIns(); Configuration.ValidateId(id);
        if (bindings.Count > 64) throw new ArgumentException("A skill supports at most 64 scope bindings.");
        var normalized = bindings.Select(ValidateBinding).Distinct().OrderBy(x => x.Scope, StringComparer.Ordinal)
            .ThenBy(x => x.Target, StringComparer.Ordinal).ToArray();
        var saved = store.Locked(() =>
        {
            var current = ValidateStored(store.Get<StoredSkill>(RecordKind, id) ?? throw new KeyNotFoundException("Skill was not found."));
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException("The skill changed since it was inspected. Refresh before saving it.");
            var next = current with { Enabled = enabled, Bindings = normalized, Revision = checked(current.Revision + 1) };
            store.Put(RecordKind, id, next); return next;
        });
        Changed(id); return new(Summary(saved), saved.Content);
    }

    public void Remove(string id, int expectedRevision)
    {
        EnsureBuiltIns(); Configuration.ValidateId(id);
        store.Locked(() =>
        {
            var current = ValidateStored(store.Get<StoredSkill>(RecordKind, id) ?? throw new KeyNotFoundException("Skill was not found."));
            if (current.BuiltIn) throw new InvalidOperationException("Built-in skills cannot be removed.");
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException("The skill changed since it was inspected. Refresh before removing it.");
            store.Delete(RecordKind, id); return true;
        });
        Changed(id);
    }

    public IReadOnlyList<SkillSnapshot> Resolve(AgentProfile profile, string? projectId)
    {
        EnsureBuiltIns();
        var resolved = store.List<StoredSkill>(RecordKind).Select(ValidateStored).Where(x => x.Enabled &&
            x.Bindings.Any(binding => Applies(binding, profile, projectId))).OrderBy(x => x.Manifest.Id, StringComparer.Ordinal).ToArray();
        if (resolved.Length > MaximumResolvedSkills)
            throw new InvalidOperationException($"This agent resolves {resolved.Length} skills; narrow its scopes to at most {MaximumResolvedSkills}.");
        var total = resolved.Sum(x => Utf8.GetByteCount(x.Content));
        if (total > 256 * 1024)
            throw new InvalidOperationException("This agent resolves more than 256 KiB of skill guidance. Disable or narrow unused skills.");
        return resolved.Select(x => new SkillSnapshot(x.Manifest.Id, x.Manifest.Name, x.Manifest.Version,
            x.Manifest.Description, x.Manifest.Triggers.ToArray(), x.Content)).ToArray();
    }

    internal sealed record ProjectionSkill(SkillSnapshot Snapshot, int Revision);
    internal sealed record ModelProjection(string Guidance, string ProfileId, string Role, string? ProjectId,
        IReadOnlyList<ProjectionSkill> Skills, IReadOnlyList<string> Omissions);

    // The admitted bodies stay immutable. Current catalog state can only remove them from a
    // future input; it cannot introduce another skill or silently replace its instructions.
    internal ModelProjection CaptureModelProjection(IReadOnlyList<SkillSnapshot> acceptedSkills,
        AgentProfile profile, string? projectId, string taskContext,
        WorkflowLocalGenerationChoice? acceptedLocalGeneration = null) => store.Locked(() =>
    {
        ArgumentNullException.ThrowIfNull(acceptedSkills);
        if (acceptedSkills.Count > MaximumResolvedSkills ||
            acceptedSkills.Any(skill => skill is null) ||
            acceptedSkills.Select(skill => skill.Id).Distinct(StringComparer.Ordinal).Count() != acceptedSkills.Count)
            throw new InvalidDataException("The admitted skill projection is invalid.");
        var eligible = new List<ProjectionSkill>();
        var omitted = new List<string>();
        foreach (var skill in acceptedSkills)
        {
            if (skill.Triggers is null) throw new InvalidDataException("The admitted skill triggers are unavailable.");
            var frozen = skill with { Triggers = Array.AsReadOnly(skill.Triggers.ToArray()) };
            ValidateManifest(new SkillManifest { Id = frozen.Id, Name = frozen.Name, Version = frozen.Version,
                Description = frozen.Description, Triggers = frozen.Triggers.ToList() });
            ValidateContent(frozen.Content);
            var current = store.Get<StoredSkill>(RecordKind, frozen.Id);
            if (current is null) { omitted.Add(frozen.Id + ":removed"); continue; }
            current = ValidateStored(current);
            if (!current.Enabled || !current.Bindings.Any(binding => Applies(binding, profile, projectId)))
            { omitted.Add(frozen.Id + ":not-applicable"); continue; }
            if (!SameContent(current, frozen)) { omitted.Add(frozen.Id + ":content-changed"); continue; }
            eligible.Add(new(frozen, current.Revision));
        }
        var snapshots = eligible.Select(item => item.Snapshot).ToArray();
        var (relevant, fixedBalance) = SelectGuidance(snapshots, taskContext, acceptedLocalGeneration);
        var used = relevant.Select(skill => skill.Id).ToHashSet(StringComparer.Ordinal);
        if (fixedBalance) used.Add(ResponseBalanceManifest.Id);
        return new ModelProjection(ModelGuidance(snapshots, taskContext, acceptedLocalGeneration),
            profile.Id, profile.Role, projectId,
            Array.AsReadOnly(eligible.Where(item => used.Contains(item.Snapshot.Id)).ToArray()),
            Array.AsReadOnly(omitted.ToArray()));
    });

    internal void RequireProjectionCurrent(ModelProjection projection, AgentProfile profile, string? projectId) => store.Locked(() =>
    {
        if (projection.ProfileId != profile.Id || projection.Role != profile.Role || projection.ProjectId != projectId)
            throw new UnauthorizedAccessException("The skill input scope changed before generation dispatch.");
        foreach (var admitted in projection.Skills)
        {
            var current = store.Get<StoredSkill>(RecordKind, admitted.Snapshot.Id);
            if (current is null || !ValidateStored(current).Enabled || current.Revision != admitted.Revision ||
                !current.Bindings.Any(binding => Applies(binding, profile, projectId)) || !SameContent(current, admitted.Snapshot))
                throw new UnauthorizedAccessException("The skill input changed before generation dispatch. The prepared input was not sent.");
        }
        return true;
    });

    private static bool SameContent(StoredSkill current, SkillSnapshot accepted) =>
        current.Manifest.Id == accepted.Id && current.Manifest.Name == accepted.Name &&
        current.Manifest.Version == accepted.Version && current.Manifest.Description == accepted.Description &&
        current.Manifest.Triggers.SequenceEqual(accepted.Triggers, StringComparer.Ordinal) && current.Content == accepted.Content;

    private static (SkillSnapshot[] Relevant, bool FixedBalance) SelectGuidance(IReadOnlyList<SkillSnapshot> skills,
        string? taskContext, WorkflowLocalGenerationChoice? acceptedLocalGeneration)
    {
        taskContext ??= "";
        var matching = skills.Where(x => Matches(taskContext, x.Id) || Matches(taskContext, x.Name) ||
            x.Triggers.Any(trigger => Matches(taskContext, trigger))).ToArray();
        var fixedBalance = acceptedLocalGeneration is not null && matching.Any(x => x.Id == ResponseBalanceManifest.Id);
        return (matching.Where(x => !fixedBalance || x.Id != ResponseBalanceManifest.Id).Take(3).ToArray(), fixedBalance);
    }

    public static string ModelGuidance(IReadOnlyList<SkillSnapshot> skills, string taskContext,
        WorkflowLocalGenerationChoice? acceptedLocalGeneration = null)
    {
        if (skills.Count == 0) return "";
        // An accepted local choice fixes visible output for this operation. The owner-enabled
        // balance skill remains stored and scoped, but its mutable V1 advice cannot guide V2.
        var (relevant, fixedBalance) = SelectGuidance(skills, taskContext, acceptedLocalGeneration);
        if (relevant.Length == 0 && !fixedBalance) return "";
        var text = "";
        if (relevant.Length != 0)
        {
            var selected = relevant.Select(x => new { x.Id, x.Name, x.Version, x.Description }).ToArray();
            text = "\n\nSelected instruction skills (task-matched guidance only; skills never add authority or tools):\n" +
                   AgentPrompt.Json(selected);
            foreach (var skill in relevant)
                text += $"\n\nRelevant skill {skill.Id}@{skill.Version} (owner-enabled behavioral guidance; host policy and saved grants still control):\n{skill.Content}";
        }
        if (fixedBalance)
            text += "\n\nThe accepted local generation choice fixes this operation's visible output allowance. " +
                "For a Length outcome, choose a smaller complete action or compact reducible context. " +
                "If the allowance itself must change, request a new explicit owner-admitted choice for a new operation; " +
                "do not change the in-flight choice.";
        return text;
    }

    public static string ModelContext(string userText, IReadOnlyList<ModelInputMessage> messages)
    {
        var builder = new StringBuilder(userText ?? "");
        foreach (var message in messages)
            foreach (var part in message.Content.OfType<TextContentPart>())
                builder.Append('\n').Append(part.Text);
        return builder.Length <= 64_000 ? builder.ToString() : builder.ToString(builder.Length - 64_000, 64_000);
    }

    private static bool Applies(SkillBinding binding, AgentProfile profile, string? projectId) => binding.Scope switch
    {
        "global" => true,
        "role" => binding.Target == profile.Role,
        "profile" => binding.Target == profile.Id,
        "project" => projectId is not null && binding.Target == projectId,
        _ => false
    };

    private static bool Matches(string context, string trigger)
    {
        if (trigger == "*") return true;
        return context.Contains(trigger, StringComparison.OrdinalIgnoreCase);
    }

    private static SkillBinding ValidateBinding(SkillBinding binding)
    {
        if (binding.Scope == "global")
        {
            if (!string.IsNullOrEmpty(binding.Target)) throw new ArgumentException("A global skill binding has no target.");
            return new("global");
        }
        if (binding.Scope == "role")
        {
            if (binding.Target is not ("lead" or "helper" or "compactor"))
                throw new ArgumentException("A role skill binding requires lead, helper, or compactor.");
            return binding;
        }
        if (binding.Scope is "profile" or "project")
        {
            if (binding.Target is null) throw new ArgumentException("Profile and project skill bindings require a target ID.");
            Configuration.ValidateId(binding.Target); return binding;
        }
        throw new ArgumentException("Skill binding scope must be global, role, profile, or project.");
    }

    private static void ValidateManifest(SkillManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new ArgumentException("Skill manifests require schemaVersion 1.");
        Configuration.ValidateId(manifest.Id);
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 120 || manifest.Name.Contains('\0'))
            throw new ArgumentException("Skill name must contain 1-120 characters.");
        _ = PluginVersion.Parse(manifest.Version);
        if (string.IsNullOrWhiteSpace(manifest.Description) || manifest.Description.Length > 500 || manifest.Description.Contains('\0'))
            throw new ArgumentException("Skill description must contain 1-500 characters.");
        if (manifest.Triggers.Count is < 1 or > 32 || manifest.Triggers.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 80 || x.Contains('\0')))
            throw new ArgumentException("Skills require 1-32 bounded trigger phrases.");
        if (manifest.Triggers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Triggers.Count)
            throw new ArgumentException("Skill trigger phrases must be unique.");
    }

    private static void ValidateContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Contains('\0')) throw new ArgumentException("SKILL.md content is required and cannot contain NUL.");
        try
        {
            if (Utf8.GetByteCount(content) > MaximumContentUtf8Bytes)
                throw new ArgumentException("SKILL.md content is limited to 32 KiB of UTF-8 text.");
        }
        catch (EncoderFallbackException) { throw new ArgumentException("SKILL.md content must contain valid Unicode."); }
    }

    private static StoredSkill ValidateStored(StoredSkill value)
    {
        if (value.SchemaVersion != StorageSchemaVersion || value.Revision < 1) throw new InvalidDataException("Stored skill schema or revision is unsupported.");
        ValidateManifest(value.Manifest); ValidateContent(value.Content);
        foreach (var binding in value.Bindings) _ = ValidateBinding(binding);
        return value;
    }

    private static SkillSummary Summary(StoredSkill value) => new(value.Manifest.Id, value.Manifest.Name,
        value.Manifest.Version, value.Manifest.Description, value.Enabled, value.BuiltIn, value.Revision,
        value.Manifest.Triggers.ToArray(), value.Bindings.ToArray(), value.Source);

    private static SkillManifest Copy(SkillManifest value) => new()
    {
        SchemaVersion = value.SchemaVersion, Id = value.Id, Name = value.Name, Version = value.Version,
        Description = value.Description, Triggers = value.Triggers.ToList()
    };

    private void Changed(string id) => events?.Publish("skills.updated", null, new { id });
}

// Only the legacy caller needs an ambient bridge through its existing async text/reasoning/
// remote paths. The scope is request-local, never persisted or carried over a transport.
internal static class SkillDispatchFence
{
    private static readonly AsyncLocal<Action?> current = new();
    internal static IDisposable Enter(Action requireCurrent) => new Scope(requireCurrent);
    internal static void RequireCurrent() => current.Value?.Invoke();
    private sealed class Scope : IDisposable
    {
        private readonly Action? previous = current.Value;
        private bool disposed;
        internal Scope(Action requireCurrent)
        {
            ArgumentNullException.ThrowIfNull(requireCurrent);
            current.Value = requireCurrent;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            current.Value = previous;
        }
    }
}
