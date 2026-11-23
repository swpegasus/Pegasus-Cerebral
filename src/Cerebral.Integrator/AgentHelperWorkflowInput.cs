using System.Globalization;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

/// <summary>One closed owner setting. Absence retains the Worker's ordinary Chat behavior.</summary>
internal sealed record AgentHelperWorkflowBinding(string DefinitionId, string DefinitionVersion, JsonElement Input)
{
    internal const string Key = AgentWorkflowBinding.SettingsKey;
    internal const int MaximumBytes = AgentWorkflowBinding.MaximumBytes;

    internal static AgentHelperWorkflowBinding Read(JsonElement value)
    {
        var binding = AgentWorkflowBinding.Read(value);
        return new(binding.DefinitionId, binding.DefinitionVersion, binding.Input);
    }

    internal JsonElement ToSettings() => new AgentWorkflowBinding(DefinitionId, DefinitionVersion, Input).ToSettings();

    internal static bool Text(JsonElement value, int maximum) => value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text && text.Length <= maximum && !text.Contains('\0');

    internal static void RequireObject(JsonElement value, int maximumBytes, string label)
    {
        if (value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.GetRawText()) > maximumBytes)
            throw new ArgumentException(label + " is unavailable or exceeds its size limit.");
        if (value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw new ArgumentException(label + " contains duplicate fields.");
    }
}

/// <summary>A guided, deliberately bounded input surface; it never exposes arbitrary profile JSON.</summary>
internal sealed class AgentWorkflowInputForm
{
    private static readonly HashSet<string> RootKeywords = new(StringComparer.Ordinal)
        { "type", "properties", "required", "additionalProperties", "title", "description" };
    private static readonly HashSet<string> FieldKeywords = new(StringComparer.Ordinal)
        { "type", "title", "description", "default", "enum", "minLength", "maxLength", "minimum", "maximum", "items", "minItems", "maxItems" };
    private readonly List<Field> fields = [];
    internal FieldsDialog Form { get; }

    private sealed record Field(string Name, string Id, string Type, bool Required, JsonElement Schema, string[]? Choices);

    internal AgentWorkflowInputForm(System.Windows.Window owner, WorkflowDefinitionDescriptor definition, JsonElement input)
    {
        AgentHelperWorkflowBinding.RequireObject(definition.InputSchema, 64 * 1024, "Workflow input schema");
        AgentHelperWorkflowBinding.RequireObject(input, AgentHelperWorkflowBinding.MaximumBytes, "Workflow inputs");
        var schema = definition.InputSchema;
        RequireKeywords(schema, RootKeywords);
        if (!schema.TryGetProperty("type", out var rootType) || rootType.GetString() != "object" ||
            !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("This workflow needs its feature's guided input form.");
        if (schema.TryGetProperty("additionalProperties", out var additional) &&
            additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("This workflow has unsupported additional input rules.");
        var names = properties.EnumerateObject().Select(x => x.Name).ToArray();
        if (names.Length > 64 || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(x => x.Length is 0 or > 256 || x.Contains('\0')))
            throw new ArgumentException("Workflow input fields are invalid or exceed the guided form limit.");
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var needed))
        {
            if (needed.ValueKind != JsonValueKind.Array) throw new ArgumentException("Workflow required fields are invalid.");
            foreach (var name in needed.EnumerateArray())
                if (name.ValueKind != JsonValueKind.String || !required.Add(name.GetString()!) || !names.Contains(name.GetString(), StringComparer.Ordinal))
                    throw new ArgumentException("Workflow required fields are invalid.");
        }
        if (input.EnumerateObject().Any(x => !names.Contains(x.Name, StringComparer.Ordinal)))
            throw new ArgumentException("Saved inputs contain fields this guided form cannot edit. Keep the saved binding unchanged or choose ordinary Chat before replacing it.");

        Form = new(owner, "Helper workflow inputs", definition.Id + " · " + definition.Version +
            "\nThese inputs configure this helper. They do not add tools, capabilities, folders or project scope.", "Use inputs");
        try
        {
        foreach (var property in properties.EnumerateObject())
        {
            var spec = property.Value;
            AgentHelperWorkflowBinding.RequireObject(spec, 64 * 1024, "Workflow field schema");
            RequireKeywords(spec, FieldKeywords);
            if (!spec.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String)
                throw new ArgumentException("This workflow needs its feature's guided form for " + property.Name + ".");
            var type = kind.GetString()!;
            if (type is not ("string" or "integer" or "number" or "boolean" or "array"))
                throw new ArgumentException("This workflow needs its feature's guided form for " + property.Name + ".");
            string[]? choices = null;
            if (spec.TryGetProperty("enum", out var enumeration))
            {
                if (type != "string" || enumeration.ValueKind != JsonValueKind.Array || enumeration.GetArrayLength() is < 1 or > 64 ||
                    enumeration.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                    throw new ArgumentException("This workflow has unsupported choices for " + property.Name + ".");
                choices = enumeration.EnumerateArray().Select(x => x.GetString()!).ToArray();
                if (choices.Distinct(StringComparer.Ordinal).Count() != choices.Length)
                    throw new ArgumentException("This workflow has duplicate choices.");
            }
            if (type == "array")
            {
                if (!spec.TryGetProperty("items", out var items)) throw new ArgumentException("Array item rules are unavailable.");
                AgentHelperWorkflowBinding.RequireObject(items, 64 * 1024, "Array item schema");
                RequireKeywords(items, new HashSet<string>(StringComparer.Ordinal) { "type", "title", "description", "enum", "minLength", "maxLength" });
                if (!items.TryGetProperty("type", out var itemType) || itemType.GetString() != "string")
                    throw new ArgumentException("This workflow needs its feature's guided form for " + property.Name + ".");
                if (items.TryGetProperty("enum", out var itemChoices) &&
                    (itemChoices.ValueKind != JsonValueKind.Array || itemChoices.GetArrayLength() is < 1 or > 64 ||
                     itemChoices.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)))
                    throw new ArgumentException("Array item choices are unavailable.");
            }
            ValidateBounds(spec);
            if (type == "array") ValidateBounds(spec.GetProperty("items"));
            var id = "workflowInput." + fields.Count.ToString(CultureInfo.InvariantCulture);
            var field = new Field(property.Name, id, type, required.Contains(property.Name), spec.Clone(), choices);
            fields.Add(field);
            var label = spec.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                ? title.GetString()! : Friendly(property.Name);
            var present = input.TryGetProperty(property.Name, out var seed);
            if (present) ValidateValue(seed, field.Type, spec, property.Name);
            else if (spec.TryGetProperty("default", out var preset)) { seed = preset; ValidateValue(seed, field.Type, spec, property.Name); }
            if (!field.Required) Form.Check(id + ".include", "Include " + label, present);
            if (type == "boolean") Form.Check(id, label, seed.ValueKind == JsonValueKind.True);
            else if (choices is not null) Form.Choice(id, label, choices,
                seed.ValueKind == JsonValueKind.String ? seed.GetString()! : choices[0]);
            else
            {
                var text = seed.ValueKind == JsonValueKind.Undefined ? "" : type == "array"
                    ? string.Join(Environment.NewLine, seed.EnumerateArray().Select(x => x.GetString()))
                    : seed.ValueKind == JsonValueKind.String ? seed.GetString()! : seed.GetRawText();
                if (type == "array" && seed.ValueKind != JsonValueKind.Undefined &&
                    seed.EnumerateArray().Any(x => x.GetString() is not { Length: > 0 } value || value.Contains('\r') || value.Contains('\n')))
                    throw new ArgumentException("This array needs its feature's guided form to preserve multiline or empty entries.");
                Form.Add(id, label + (type == "array" ? " · one value per line" : ""), text, type == "array");
                if (type == "array" && spec.GetProperty("items").TryGetProperty("enum", out var permitted))
                    Form.Add(id + ".choices", "Allowed values", string.Join(", ", permitted.EnumerateArray().Select(x => x.GetString()))).SetReadOnly(id + ".choices");
            }
            if (!field.Required) Form.SetEnabled(id, present);
        }
        Form.WhenAnyInputChanged(() =>
        {
            foreach (var field in fields.Where(x => !x.Required)) Form.SetEnabled(field.Id, Form.Checked(field.Id + ".include"));
        });
        }
        catch { Form.DiscardDraftAndClose(); throw; }
    }

    internal string FieldId(string name) => fields.Single(x => x.Name == name).Id;

    internal JsonElement ReadInput()
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!field.Required && !Form.Checked(field.Id + ".include")) continue;
            JsonElement value;
            if (field.Type == "boolean") value = CerebralJson.Element(Form.Checked(field.Id));
            else if (field.Choices is not null) value = CerebralJson.Element(Form.Selection(field.Id));
            else if (field.Type == "string") value = CerebralJson.Element(Form.Raw(field.Id));
            else if (field.Type == "array") value = CerebralJson.Element(Form.Raw(field.Id).Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries));
            else if (field.Type == "integer" && long.TryParse(Form.Raw(field.Id), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                value = CerebralJson.Element(integer);
            else if (field.Type == "number") value = ExactNumber.ParseInput(Form.Raw(field.Id));
            else throw new ArgumentException(Friendly(field.Name) + " must be a valid " + field.Type + ".");
            ValidateValue(value, field.Type, field.Schema, field.Name);
            values.Add(field.Name, value);
        }
        var result = CerebralJson.Element(values);
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > AgentHelperWorkflowBinding.MaximumBytes)
            throw new ArgumentException("Workflow inputs exceed the helper binding limit.");
        return result;
    }

    private static void RequireKeywords(JsonElement value, HashSet<string> supported)
    {
        if (value.EnumerateObject().Any(x => !supported.Contains(x.Name)))
            throw new ArgumentException("This workflow has input rules that need its feature's guided form.");
    }

    private static void ValidateBounds(JsonElement schema)
    {
        foreach (var name in new[] { "minLength", "maxLength", "minItems", "maxItems" })
            if (schema.TryGetProperty(name, out var value) &&
                (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0))
                throw new ArgumentException("Workflow length bounds are invalid.");
        foreach (var name in new[] { "minimum", "maximum" })
            if (schema.TryGetProperty(name, out var value)) _ = ExactNumber.Read(value);
        foreach (var (low, high) in new[] { ("minLength", "maxLength"), ("minItems", "maxItems"), ("minimum", "maximum") })
            if (schema.TryGetProperty(low, out var min) && schema.TryGetProperty(high, out var max) && ExactNumber.Compare(min, max) > 0)
                throw new ArgumentException("Workflow bounds are inconsistent.");
    }

    private static void ValidateValue(JsonElement value, string type, JsonElement schema, string name)
    {
        var matches = type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n),
            "array" => value.ValueKind == JsonValueKind.Array,
            _ => false
        };
        if (!matches) throw new ArgumentException(Friendly(name) + " has the wrong value type.");
        if (schema.TryGetProperty("enum", out var options) && !options.EnumerateArray().Any(x => JsonElement.DeepEquals(x, value)))
            throw new ArgumentException(Friendly(name) + " is not an allowed choice.");
        if (type == "string") CheckLength(value.GetString()!.EnumerateRunes().Count(), schema, "minLength", "maxLength", name);
        if (type is "integer" or "number")
        {
            _ = ExactNumber.Read(value);
            if (schema.TryGetProperty("minimum", out var min) && ExactNumber.Compare(value, min) < 0 ||
                schema.TryGetProperty("maximum", out var max) && ExactNumber.Compare(value, max) > 0)
                throw new ArgumentException(Friendly(name) + " is outside the allowed range.");
        }
        if (type == "array")
        {
            CheckLength(value.GetArrayLength(), schema, "minItems", "maxItems", name);
            foreach (var item in value.EnumerateArray()) ValidateValue(item, "string", schema.GetProperty("items"), name);
        }
    }

    // Keep the JSON token for the draft. IEEE 754 is only a finite-domain admission check;
    // decimal digit/exponent comparison must not round either a value or its schema boundary.
    private readonly record struct ExactNumber(int Sign, string Digits, int Exponent)
    {
        private const int MaximumExponent = 4096;

        internal static JsonElement ParseInput(string text)
        {
            if (text.Length > AgentHelperWorkflowBinding.MaximumBytes)
                throw new ArgumentException("Workflow number exceeds the guided input limit.");
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 1 });
                var value = document.RootElement;
                _ = Read(value);
                return value.Clone();
            }
            catch (JsonException)
            {
                throw new ArgumentException("Workflow number must be a valid JSON number.");
            }
        }

        internal static ExactNumber Read(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var finite) || !double.IsFinite(finite))
                throw Unsupported();
            var text = value.GetRawText();
            if (text.Length > AgentHelperWorkflowBinding.MaximumBytes) throw Unsupported();
            var sign = text[0] == '-' ? -1 : 1;
            var token = text.AsSpan(sign < 0 ? 1 : 0);
            var marker = token.IndexOfAny('e', 'E');
            var exponent = 0;
            if (marker >= 0)
            {
                if (!int.TryParse(token[(marker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent) ||
                    exponent is < -MaximumExponent or > MaximumExponent) throw Unsupported();
                token = token[..marker];
            }
            var point = token.IndexOf('.');
            if (point >= 0) exponent -= token.Length - point - 1;
            var digits = token.ToString().Replace(".", "", StringComparison.Ordinal).TrimStart('0');
            if (digits.Length == 0) return new(0, "0", 0);
            var trimmed = digits.TrimEnd('0');
            exponent += digits.Length - trimmed.Length;
            return new(sign, trimmed, exponent);
        }

        internal static int Compare(JsonElement left, JsonElement right)
        {
            var a = Read(left);
            var b = Read(right);
            if (a.Sign != b.Sign) return a.Sign.CompareTo(b.Sign);
            if (a.Sign == 0) return 0;
            var order = (a.Exponent + a.Digits.Length).CompareTo(b.Exponent + b.Digits.Length);
            if (order != 0) return a.Sign * order;
            // Equal decimal order: compare zero-extended coefficients without allocating powers
            // of ten. Work stays bounded by the admitted input token lengths, even for exponents.
            for (var i = 0; i < Math.Max(a.Digits.Length, b.Digits.Length); i++)
            {
                var x = i < a.Digits.Length ? a.Digits[i] : '0';
                var y = i < b.Digits.Length ? b.Digits[i] : '0';
                if (x != y) return a.Sign * x.CompareTo(y);
            }
            return 0;
        }

        private static ArgumentException Unsupported() => new(
            "This numeric value or range exceeds the guided form's finite number, token or exponent limit. " +
            "Keep the saved binding unchanged or use the feature's guided form.");
    }

    private static void CheckLength(int count, JsonElement schema, string low, string high, string name)
    {
        if (schema.TryGetProperty(low, out var min) && count < min.GetInt32() ||
            schema.TryGetProperty(high, out var max) && count > max.GetInt32())
            throw new ArgumentException(Friendly(name) + " has an invalid length.");
    }

    private static string Friendly(string name) => string.Concat(name.Select((c, i) =>
        i == 0 ? char.ToUpperInvariant(c).ToString() : char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
}
