using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Cerebral.PromptReviewPlugin;

/// <summary>Constrains a review card to the physical scrolling mount without CLR-name or logical-ancestor lookup.</summary>
internal sealed class PromptReviewViewportConstraint : IDisposable
{
    private readonly FrameworkElement card;
    private ScrollViewer? viewer;
    private BindingExpressionBase? binding;
    private bool disposed;

    internal PromptReviewViewportConstraint(FrameworkElement card)
    {
        this.card = card;
        // A card can be measured before Loaded. LayoutUpdated observes an established visual
        // parent without forcing realization; Loaded also covers an already-realized mount.
        card.LayoutUpdated += Resolve;
        card.Loaded += Loaded;
    }

    private void Loaded(object sender, RoutedEventArgs args) => Resolve(sender, args);
    private void Resolve(object? sender, EventArgs args)
    {
        if (disposed) return;
        ScrollViewer? outer = null;
        var node = VisualTreeHelper.GetParent(card);
        for (var depth = 0; node is not null && depth < 128; depth++, node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer candidate) { outer = candidate; break; }
        if (ReferenceEquals(viewer, outer)) return;
        ClearOwnedBinding();
        viewer = outer;
        if (outer is null) return;
        // Use the observed physical source and DP token directly. The1.0.7 live mount retained
        // a PathError expression for FindAncestor + "ViewportHeight", leaving Height automatic.
        binding = BindingOperations.SetBinding(card, FrameworkElement.HeightProperty, new Binding
        {
            Source = outer, Path = new PropertyPath(ScrollViewer.ViewportHeightProperty),
            Mode = BindingMode.OneWay, Converter = HeightConverter.Instance
        });
    }

    private void ClearOwnedBinding()
    {
        if (binding is not null && ReferenceEquals(
            BindingOperations.GetBindingExpressionBase(card, FrameworkElement.HeightProperty), binding))
            BindingOperations.ClearBinding(card, FrameworkElement.HeightProperty);
        binding = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        card.LayoutUpdated -= Resolve;
        card.Loaded -= Loaded;
        ClearOwnedBinding();
        viewer = null;
    }

    private sealed class HeightConverter : IValueConverter
    {
        internal static readonly HeightConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double height && double.IsFinite(height) && height > 0 ? height : double.NaN;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException("The host viewport is read-only.");
    }
}
