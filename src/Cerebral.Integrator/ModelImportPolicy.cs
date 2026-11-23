namespace Cerebral.Integrator;

internal enum ModelImportMethod { Upload, Reference, Copy }

/// <summary>Renderer choices follow the selected transport's existing capabilities.</summary>
internal sealed class ModelImportPolicy(bool isLocalPipe)
{
    internal const string Upload = "Upload from this computer (recommended)";
    internal const string Reference = "Reference a file on the service machine";
    internal const string Copy = "Copy a file on the service machine into its repository";
    internal string[] Choices => isLocalPipe ? [Reference, Copy] : [Upload, Reference, Copy];
    internal string Default => isLocalPipe ? Reference : Upload;
    internal string Description => isLocalPipe
        ? "Choose a GGUF path on this machine. Reference and copy require the service identity to have access to that path. File upload is unavailable on this native connection."
        : "Upload reads the file as your signed-in user. Reference and copy use paths on the service machine and require its identity to have access.";

    internal ModelImportMethod Resolve(string choice) => choice switch
    {
        Reference => ModelImportMethod.Reference,
        Copy => ModelImportMethod.Copy,
        Upload when !isLocalPipe => ModelImportMethod.Upload,
        Upload => throw new InvalidOperationException("File upload is unavailable on this native connection. Choose reference or copy. No file was opened or request sent."),
        _ => throw new ArgumentException("Choose a supported model source method.", nameof(choice))
    };
}
