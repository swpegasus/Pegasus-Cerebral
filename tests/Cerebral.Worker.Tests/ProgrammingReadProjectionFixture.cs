using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Retained failed MODEL-AUTHORED source from Text Metrics run
/// textmetrics-programming-fb86540bdef442f68125b6d106b623f0, used only as projection test data.
/// This invalid source is never compiled or executed and is not an analyzer implementation by Codex.
/// </summary>
internal static class ProgrammingReadProjectionFixture
{
    internal const string Sha256 = "d67a6caa85932acdb8a4dfc75d088ee9504245ae81c63f4d5ff84d3464956410";
    private const string RecordedPath = @"C:\Development\Pegasus-Cerebral\build\Plugins\SettingsPresets\AutonomousTextMetrics\Service\GeneratedServicePlugin.cs";
    internal const string Source = """
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using Cerebral.Core;

namespace Cerebral.GeneratedPlugin;

public sealed class GeneratedServicePlugin : IServicePlugin
{
    public const string PluginId = "pegasus.textmetrics";
    public const string PluginName = "Text Metrics";
    public const string ServiceMessage = "Generated service ready";

    public void Initialize(IServicePluginHost host)
    {
        if (host.PluginId != PluginId) throw new InvalidOperationException("The host plugin identity does not match this package.");
    }

    public IReadOnlyList<IToolExtension> ToolExtensions { get; } = [new TextMetricsTool()];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions { get; } = Array.Empty<IServiceCommandExtension>();
    public void Dispose() { }

    private sealed class TextMetricsTool : IToolExtension
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [
            new(PluginId + ".analyze", "Analyze text for word, line, scalar, and UTF8 byte counts.",
                CerebralJson.Element(new
                {
                    type = "object",
                    properties = new { text = new { type = "string", description = "The text to analyze. Empty string is valid." } },
                    required = new[] { "text" },
                    additionalProperties = false
                }, "diagnostics")
            ]
        ];

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (call.Name != PluginId + ".analyze")
                throw new ArgumentException($"Tool call name must be {PluginId}.analyze");

            if (call.Arguments.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Arguments must be a JSON object.");

            var errorResult = new ToolResult(false, null, false, "", "")
            {
                Evidence = new(ToolExecutionOutcome.Rejected, PluginId, State: "invalid_input")
            };

            var text = call.Arguments.GetProperty("text");
            if (text.ValueKind != JsonValueKind.String)
                return Task.FromResult(errorResult);

            var inputText = text.GetString();
            if (inputText == null)
                return Task.FromResult(errorResult);

            // Validate UTF-8 encoding
            byte[] utf8Bytes;
            try
            {
                utf8Bytes = Encoding.UTF8.GetBytes(inputText);
            }
            catch (Exception)
            {
                return Task.FromResult(errorResult);
            }

            // Check size limit
            if (utf8Bytes.Length > 4096)
            {
                var tooLargeResult = new ToolResult(false, null, false, "", "")
                {
                    Evidence = new(ToolExecutionOutcome.Rejected, PluginId, State: "input_too_large")
                };
                return Task.FromResult(tooLargeResult);
            }

            // Compute metrics
            int wordCount = 0;
            int lineCount = 0;
            int scalarCount = 0;

            // Words: maximal non-whitespace runs under .NET Unicode-whitespace semantics
            bool inWord = false;
            foreach (char c in inputText)
            {
                if (char.IsWhiteSpace(c))
                {
                    inWord = false;
                }
                else
                {
                    if (!inWord)
                    {
                        wordCount++;
                        inWord = true;
                    }
                }
            }

            // Lines: 1 + newline sequence count
            // CRLF is one, lone CR or LF is one, trailing/empty segments count
            int newlineCount = 0;
            int i = 0;
            while (i < inputText.Length)
            {
                if (inputText[i] == '\r')
                {
                    if (i + 1 < inputText.Length && inputText[i + 1] == '\n')
                    {
                        newlineCount++;
                        i += 2;
                    }
                    else
                    {
                        newlineCount++;
                        i++;
                    }
                }
                else if (inputText[i] == '\n')
                {
                    newlineCount++;
                    i++;
                }
                else
                {
                    i++;
                }
            }
            lineCount = 1 + newlineCount;

            // Scalars: Unicode scalar values (not UTF16 units)
            // A scalar value is either a single char or a surrogate pair
            scalarCount = 0;
            int j = 0;
            while (j < inputText.Length)
            {
                if (j + 1 < inputText.Length && 
                    char.IsSurrogatePair(inputText[j], inputText[j + 1]))
                {
                    // This is a surrogate pair - count as 1 scalar
                    scalarCount++;
                    j += 2;
                }
                else
                {
                    scalarCount++;
                    j++;
                }
            }

            var result = new ToolResult(true, JsonSerializer.Serialize(new
            {
                words = wordCount,
                lines = lineCount,
                scalars = scalarCount,
                utf8Bytes = utf8Bytes.Length
            }), false, "", "")
            {
                Evidence = new(ToolExecutionOutcome.Succeeded, PluginId, State: "Succeeded", ProviderId: PluginId)
            };

            return Task.FromResult(result);
        }
    }
}
""" + "\n";

    internal static string Result(int startLine = 1, int lineCount = 200)
    {
        var lines = Source.Split('\n');
        return JsonSerializer.Serialize(new
        {
            path = RecordedPath, sha256 = Sha256, startLine, totalLines = lines.Length,
            content = string.Join('\n', lines.Skip(startLine - 1).Take(lineCount))
        }, CerebralJson.Options);
    }
}

