using Cerebral.Worker;

// This is only the existing operator client presentation. Linking its narrow host
// avoids the Worker entry point and its model/engine/application startup paths.
var launch = args.Length >= 2 && args[0] == "mcp" && args[1] == "--operator"
    ? args
    : new[] { "mcp", "--operator" }.Concat(args).ToArray();
return await OperatorMcpHost.RunInstalledAsync(launch);
