using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Contracts;

namespace Cerebral.Desktop;

public static class AttachmentPresentation
{
    public static string Size(long bytes) => bytes < 1024 ? bytes.ToString(CultureInfo.InvariantCulture) + " B" :
        bytes < 1024 * 1024 ? (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KiB" :
        (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MiB";

    public static FrameworkElement Reference(AttachmentReference reference, string kind)
    {
        var text = new TextBox { Text = kind + " · " + reference.OriginalName + " · " + Size(reference.Length),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0, 3, 0, 3),
            ToolTip = reference.MediaType + " · revision " + reference.Revision };
        TextContextMenus.Attach(text);
        return text;
    }
}
