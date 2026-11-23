using System.IO;

namespace Cerebral.PersonalityPlugin;
internal static class PersonalityLegacyMigration
{
    internal static string Remove(string content, PersonalitySettings legacy)
    {
        if (!PersonalityInstructionLayer.HasLayer(content))
            return content;
        // Remove only a recognized complete legacy edition. Hand-edited style bodies need owner review.
        var recognized = legacy.Choices.Any(preset => content.Contains(PersonalityInstructionLayer.Apply("", preset, true), StringComparison.Ordinal));
        if (!recognized)
            throw new InvalidDataException("The previous personality section does not match a saved local preset. Review the marked lead section before migration; no content was changed.");
        return PersonalityInstructionLayer.Apply(content, legacy.Selected(), false);
    }
}
