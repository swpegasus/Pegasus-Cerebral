using System.Text.Json;
using Cerebral.BrowserPlugin;
using Cerebral.Core;

internal static class BrowserWorkflowPortableSettingsChecks
{
    internal static int Run()
    {
        var checks = 0;
        // No Initialize, host, data root, ExecuteAsync, browser, or HTTP fixture is used in this lane.
        using var plugin = new BrowserServicePlugin();
        IServiceWorkflowPortableSettingsValidator validator = plugin;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }
        void Reject(string json, string name)
        {
            using var document = JsonDocument.Parse(json);
            try
            {
                validator.ValidateWorkflowPortableSettings(document.RootElement);
                throw new InvalidOperationException("Expected rejection: " + name);
            }
            catch (Exception error) when (error is ArgumentException or JsonException) { checks++; }
        }

        Check(validator.WorkflowPortableSettingsValidatorVersion ==
            ServiceWorkflowPortableSettingsValidatorContract.Version, "workflow validator contract version");

        using (var valid = JsonDocument.Parse("""
            {"maximumJobs":1,"maximumTabsPerJob":1,"maximumTabs":2,
             "allowedDomains":["localhost"],"downloads":"confirm"}
            """))
        {
            validator.ValidateWorkflowPortableSettings(valid.RootElement);
            checks++;
        }

        // This binding matches the Browser package's declared allowedDomains shape. Structural admission succeeds.
        var domainField = new ServicePluginPortableSettingFieldBinding(
            "allowedDomains", "string-list", false, 253, 32, null, null, []);
        using (var credentialUrl = JsonDocument.Parse("""{"allowedDomains":["https://user:credential@host"]}"""))
        {
            ServicePluginPortableSettingsPolicy.ValidateValue(
                domainField, credentialUrl.RootElement.GetProperty("allowedDomains"));
            checks++;
        }
        // BrowserPolicy owns the semantic refusal after the same value passes the generic shape check.
        Reject("""{"allowedDomains":["https://user:credential@host"]}""",
            "credential-bearing URL is not a DNS domain");
        Reject("""{"allowedDomains":["*.example.com"]}""", "wildcard hostname rejected");
        Reject("""{"maximumTabsPerJob":4,"maximumTabs":2}""",
            "cross-field tab limit rejected");
        Reject("""[]""", "projected settings must be an object");

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            suite = "browser-workflow-portable-settings",
            evidence = "pure uninitialized component validator fixtures; no browser or network call",
            checks
        }));
        return checks;
    }
}
