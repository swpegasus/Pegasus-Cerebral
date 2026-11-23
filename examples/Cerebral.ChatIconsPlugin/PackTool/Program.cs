using Cerebral.ChatIconsPlugin;

try
{
    if (args.Length == 2 && args[0] == "validate")
    {
        var pack = AssetPack.Load(args[1]);
        Console.WriteLine($"Validated {pack.Id} {pack.Version}: {pack.Assets.Count} icons; catalogSha256={pack.CatalogSha256}; packSha256={pack.ContentSha256}");
        return 0;
    }
    if (args.Length is 3 or 4 && args[0] == "export")
    {
        var pack = AssetPack.Load(args[1]);
        pack.Export(args[2], args.Length == 4 ? args[3] : null);
        Console.WriteLine("Exported validated passive assets with licensing to " + Path.GetFullPath(args[2]));
        return 0;
    }
    Console.Error.WriteLine("Usage: validate <pack-root> | export <pack-root> <absent-destination> [exact-key-or-alias]");
    return 2;
}
catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or FormatException or ArgumentException or System.Xml.XmlException or KeyNotFoundException)
{
    Console.Error.WriteLine("Icon pack rejected: " + error.Message);
    return 1;
}
