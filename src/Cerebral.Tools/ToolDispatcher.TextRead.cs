using System.Text;

namespace Cerebral.Tools;

public sealed partial class ToolDispatcher
{
    private static string DecodeReadText(byte[] bytes, string operation)
    {
        try { return ToolPolicy.Decode(bytes); }
        catch (DecoderFallbackException error)
        {
            // Keep strict decoding and the existing rejected-before-dispatch evidence.
            // A raw decoder exception gives the model no supported correction.
            throw new InvalidDataException(operation +
                " requires UTF-8 or BOM-marked UTF-16 text. This file contains binary data or a different text encoding. " +
                "Read the matching source file or use a permitted tool that understands this format. " +
                "Choose a different input or operation; repeating the same text read will fail.", error);
        }
    }
}
