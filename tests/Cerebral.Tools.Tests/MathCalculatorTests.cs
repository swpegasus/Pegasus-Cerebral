using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;

internal static class MathCalculatorTests
{
    public static async Task RunAsync(ToolDispatcher dispatcher, Action<bool, string> check)
    {
        var profile = new AgentProfile { Id = "calculator-test", AllowedTools = ["math.calculate"], Capabilities = ["math.calculate"] };
        var context = new ToolExecutionContext(profile, "calculator-test", "calculator-test");
        Task<ToolResult> Call(string expression, int decimalPlaces = 20) =>
            dispatcher.ExecuteAsync(new("math.calculate", CerebralJson.Element(new { expression, decimalPlaces })), context, default);
        foreach (var (expression, expected) in new[]
        {
            ("choose(5,2)*choose(7,1)/choose(12,3)", "7/22"),
            ("0.1 + 0.2", "3/10"), ("1/3 + 1/6", "1/2"), ("-2*(3+4)/5", "-14/5"),
            ("8/4/2", "1"), ("0/-7", "0"), ("factorial(10)", "3628800"),
            ("choose(0,0)", "1"), ("choose(100,3)", "161700"),
            ("12345 - 97*127", "26"), ("-(2+3)*-4", "20")
        })
        {
            var result = await Call(expression);
            using var body = JsonDocument.Parse(result.Success ? result.Content : "{}");
            check(result.Success && body.RootElement.GetProperty("exact").GetString() == expected, "exact arithmetic: " + expression);
        }
        var recurring = await Call("1/3", 8);
        using (var body = JsonDocument.Parse(recurring.Content))
            check(body.RootElement.GetProperty("decimal").GetString() == "0.33333333" && body.RootElement.GetProperty("decimalTruncated").GetBoolean(), "recurring decimal reports truncation honestly");
        var terminating = await Call("-1/4", 8);
        using (var body = JsonDocument.Parse(terminating.Content))
            check(body.RootElement.GetProperty("decimal").GetString() == "-0.25" && !body.RootElement.GetProperty("decimalTruncated").GetBoolean(), "terminating decimal stays exact with sign");
        foreach (var invalid in new[] { "1/0", "choose(3,4)", "factorial(-1)", "factorial(1.5)", "factorial(1001)", "System.IO.File.Delete(1)", "1;2", "(1+2", ".", "1e100", new string('(', 65) + "1" + new string(')', 65), new string('9', 257), string.Join("+", Enumerable.Repeat("1", 258)), new string('1', 4097) })
            check(!(await Call(invalid)).Success, "calculator rejects invalid or excessive input: " + invalid[..Math.Min(45, invalid.Length)]);
        check(!(await Call("1", 51)).Success, "decimal precision is bounded");
        profile.Capabilities.Clear();
        check(!(await Call("1+1")).Success && !(await dispatcher.ListAsync(profile, default)).Any(x => x.Name == "math.calculate"), "calculator requires its saved capability for listing and execution");
    }
}
