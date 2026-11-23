using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace Cerebral.Orchestrator;

/// <summary>Bounded reads of existing public WPF geometry. Never drives layout or reads user content.</summary>
internal static class InputGuidanceLayoutObservation
{
    internal const int MaximumBytes = 48 * 1024;
    private const int MaximumAncestors = 12, MaximumNodes = 256, MaximumDepth = 20, MaximumActions = 16;
    private static readonly ConditionalWeakTable<DependencyObject, Identity> Identities = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false, MaxDepth = 32 };
    private sealed class Identity { internal readonly string Id = Guid.NewGuid().ToString("N"); }

    internal static JsonElement Capture(FrameworkElement view, Window owner)
    {
        ArgumentNullException.ThrowIfNull(view); ArgumentNullException.ThrowIfNull(owner);
        view.Dispatcher.VerifyAccess(); owner.Dispatcher.VerifyAccess();
        var ancestors = new List<object>();
        ScrollViewer? outer = null;
        ScrollContentPresenter? presenter = null;
        var current = VisualTreeHelper.GetParent(view);
        while (current is not null && ancestors.Count < MaximumAncestors)
        {
            if (outer is null && current is ScrollViewer scroll) outer = scroll;
            if (presenter is null && current is ScrollContentPresenter content) presenter = content;
            ancestors.Add(Element(current, owner, includeHeight: false));
            current = VisualTreeHelper.GetParent(current);
        }
        var ancestorRemaining = current is not null;
        var actions = new List<object>();
        var visited = 0;
        var nodeLimit = false; var depthLimit = false; var actionLimit = false;
        var pending = new Queue<(DependencyObject Node, int Depth)>();
        pending.Enqueue((view, 0));
        var enqueued = 1;
        while (pending.TryDequeue(out var item))
        {
            var (node, depth) = item;
            visited++;
            if (node is ButtonBase button)
            {
                if (actions.Count == MaximumActions) { actionLimit = true; break; }
                actions.Add(new { id = Id(button), type = TypeId(button), depth, isVisible = button.IsVisible,
                    visibility = VisibilityId(button.Visibility), isEnabled = button.IsEnabled,
                    ownerBounds = Bounds(button, owner) });
            }
            var children = VisualTreeHelper.GetChildrenCount(node);
            if (depth == MaximumDepth) { if (children > 0) depthLimit = true; continue; }
            for (var index = 0; index < children; index++)
            {
                if (enqueued == MaximumNodes) { nodeLimit = true; break; }
                pending.Enqueue((VisualTreeHelper.GetChild(node, index), depth + 1));
                enqueued++;
            }
        }
        var limits = new Dictionary<string, object?>
        {
            ["maximumBytes"] = MaximumBytes, ["maximumAncestors"] = MaximumAncestors,
            ["maximumActionNodes"] = MaximumNodes, ["maximumActionDepth"] = MaximumDepth,
            ["maximumActions"] = MaximumActions, ["actionNodesVisited"] = visited,
            ["ancestorTraversalComplete"] = !ancestorRemaining,
            ["actionTraversalComplete"] = !nodeLimit && !depthLimit && !actionLimit,
            ["nodeLimitReached"] = nodeLimit, ["depthLimitReached"] = depthLimit,
            ["actionLimitReached"] = actionLimit, ["outputLimitReached"] = false
        };
        var result = new Dictionary<string, object?>
        {
            ["schema"] = "cerebral.input-guidance-layout-observation/1",
            ["coordinateSpace"] = new { units = "DIP", relativeToOwnerId = Id(owner) },
            ["capturedView"] = Element(view, owner, includeHeight: true),
            ["owner"] = Owner(owner),
            ["outerScrollViewer"] = outer is null ? Unavailable(ancestorRemaining ? "ancestor_limit" : "not_found") : Scroll(outer, owner),
            ["scrollContentPresenter"] = presenter is null ? Unavailable(ancestorRemaining ? "ancestor_limit" : "not_found") :
                new { available = true, search = "ancestor", element = Element(presenter, owner, includeHeight: false) },
            ["ancestors"] = ancestors, ["actions"] = actions, ["limits"] = limits,
            ["truncated"] = ancestorRemaining || nodeLimit || depthLimit || actionLimit, ["outputBytes"] = 0
        };
        var bytes = Encode(result);
        // The normal bounded shape is substantially smaller. Retain the central view and owner if
        // unusual numeric/type encodings approach the wire cap; report omitted ancestor/action rows.
        while (bytes.Length > MaximumBytes && (ancestors.Count > 0 || actions.Count > 0))
        {
            if (ancestors.Count > 0) { ancestors.RemoveAt(ancestors.Count - 1); limits["ancestorTraversalComplete"] = false; }
            else { actions.RemoveAt(actions.Count - 1); limits["actionTraversalComplete"] = false; }
            limits["outputLimitReached"] = true; result["truncated"] = true;
            bytes = Encode(result);
        }
        if (bytes.Length > MaximumBytes)
        {
            limits["outputLimitReached"] = true;
            result = new() { ["schema"] = "cerebral.input-guidance-layout-observation/1",
                ["available"] = false, ["reason"] = "output_limit", ["limits"] = limits,
                ["truncated"] = true, ["outputBytes"] = 0 };
            bytes = Encode(result);
        }
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static byte[] Encode(Dictionary<string, object?> value)
    {
        byte[] bytes = [];
        // Updating the decimal count can change its own encoded width, then reaches a fixed point.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (value["outputBytes"] is int length && length == bytes.Length) return bytes;
            value["outputBytes"] = bytes.Length;
        }
        return JsonSerializer.SerializeToUtf8Bytes(value, Json);
    }

    private static object Element(DependencyObject value, Window owner, bool includeHeight)
    {
        if (value is not FrameworkElement element)
            return new { id = Id(value), type = TypeId(value), available = false, reason = "not_framework_element" };
        var result = new Dictionary<string, object?>
        {
            ["id"] = Id(element), ["type"] = TypeId(element), ["available"] = true,
            ["isLoaded"] = element.IsLoaded, ["isVisible"] = element.IsVisible,
            ["visibility"] = VisibilityId(element.Visibility), ["clipToBounds"] = element.ClipToBounds,
            ["clipPresent"] = element.Clip is not null,
            ["actual"] = Size(element.ActualWidth, element.ActualHeight),
            ["desired"] = Size(element.DesiredSize.Width, element.DesiredSize.Height),
            ["render"] = Size(element.RenderSize.Width, element.RenderSize.Height),
            ["layoutSlot"] = LayoutSlot(element, owner), ["ownerBounds"] = Bounds(element, owner),
            ["height"] = double.IsNaN(element.Height) ? new { kind = "auto" } : Number(element.Height),
            ["minHeight"] = Number(element.MinHeight), ["maxHeight"] = Number(element.MaxHeight)
        };
        if (includeHeight)
        {
            result["heightBinding"] = HeightBinding(element);
            var source = DependencyPropertyHelper.GetValueSource(element, FrameworkElement.HeightProperty);
            result["heightValueSource"] = new { baseSource = SourceId(source.BaseValueSource),
                baseSourceCode = (int)source.BaseValueSource, isExpression = source.IsExpression,
                isCoerced = source.IsCoerced, isAnimated = source.IsAnimated,
                hasLocalValue = element.ReadLocalValue(FrameworkElement.HeightProperty) != DependencyProperty.UnsetValue };
        }
        return result;
    }

    private static object HeightBinding(FrameworkElement view)
    {
        var expression = BindingOperations.GetBindingExpressionBase(view, FrameworkElement.HeightProperty);
        if (expression is null) return new { present = false };
        // ResolvedSource and its single terminal property identifier are public binding metadata.
        // Never inspect ParentBinding.Path, DataItem, a source value, or any object's string form.
        var single = expression as BindingExpression;
        var source = single?.ResolvedSource;
        var property = single?.ResolvedSourcePropertyName;
        return new { present = true, status = StatusId(expression.Status), expressionType = TypeId(expression),
            singleBinding = single is not null, resolvedSourceType = source is null ? null : TypeId(source),
            resolvedSourceProperty = PropertyId(property), resolvedSourceAvailable = source is not null };
    }

    private static object Scroll(ScrollViewer scroll, Window owner) => new
    {
        available = true, search = "ancestor", element = Element(scroll, owner, includeHeight: false),
        viewport = Size(scroll.ViewportWidth, scroll.ViewportHeight),
        extent = Size(scroll.ExtentWidth, scroll.ExtentHeight),
        offset = new { horizontal = Number(scroll.HorizontalOffset), vertical = Number(scroll.VerticalOffset) },
        scrollable = Size(scroll.ScrollableWidth, scroll.ScrollableHeight),
        verticalScrollBarVisibility = ScrollBarId(scroll.VerticalScrollBarVisibility),
        horizontalScrollBarVisibility = ScrollBarId(scroll.HorizontalScrollBarVisibility),
        computedVerticalScrollBarVisibility = VisibilityId(scroll.ComputedVerticalScrollBarVisibility),
        computedHorizontalScrollBarVisibility = VisibilityId(scroll.ComputedHorizontalScrollBarVisibility)
    };

    private static object Owner(Window owner)
    {
        object source = Unavailable("no_existing_presentation_source");
        if (PresentationSource.FromVisual(owner) is HwndSource hwnd && !hwnd.IsDisposed &&
            ReferenceEquals(hwnd.RootVisual, owner) && hwnd.CompositionTarget is { } target)
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(owner);
                var origin = owner.PointToScreen(new Point());
                source = new { available = true, dpi = new { scaleX = Number(dpi.DpiScaleX), scaleY = Number(dpi.DpiScaleY),
                    pixelsPerInchX = Number(dpi.PixelsPerInchX), pixelsPerInchY = Number(dpi.PixelsPerInchY) },
                    transformToDevice = Matrix(target.TransformToDevice), transformFromDevice = Matrix(target.TransformFromDevice),
                    screenPixelOrigin = new { x = Number(origin.X), y = Number(origin.Y) } };
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            { source = Unavailable("presentation_unavailable"); }
        }
        return new { id = Id(owner), type = TypeId(owner), isLoaded = owner.IsLoaded, isVisible = owner.IsVisible,
            actual = Size(owner.ActualWidth, owner.ActualHeight), presentationSource = source };
    }

    private static object LayoutSlot(FrameworkElement element, Window owner)
    {
        try
        {
            var slot = LayoutInformation.GetLayoutSlot(element);
            if (VisualTreeHelper.GetParent(element) is not Visual parent) return Unavailable("no_visual_parent");
            return Rect(ReferenceEquals(parent, owner) ? slot : parent.TransformToAncestor(owner).TransformBounds(slot));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { return Unavailable("layout_unavailable"); }
    }

    private static object Bounds(FrameworkElement element, Window owner)
    {
        try
        {
            var local = new Rect(new Point(), element.RenderSize);
            return Rect(ReferenceEquals(element, owner) ? local : element.TransformToAncestor(owner).TransformBounds(local));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { return Unavailable("disconnected"); }
    }

    private static object Number(double value) => double.IsFinite(value) ? new { kind = "finite", value } :
        new { kind = double.IsPositiveInfinity(value) ? "positiveInfinity" : double.IsNegativeInfinity(value) ? "negativeInfinity" : "nan" };
    private static object Size(double width, double height) => new { width = Number(width), height = Number(height) };
    private static object Rect(Rect value) => new { available = true, isEmpty = value.IsEmpty,
        x = Number(value.X), y = Number(value.Y), width = Number(value.Width), height = Number(value.Height) };
    private static object Matrix(Matrix value) => new { m11 = Number(value.M11), m12 = Number(value.M12),
        m21 = Number(value.M21), m22 = Number(value.M22), offsetX = Number(value.OffsetX), offsetY = Number(value.OffsetY) };
    private static object Unavailable(string reason) => new { available = false, kind = "unavailable", reason };
    private static string Id(DependencyObject value) => Identities.GetValue(value, static _ => new()).Id;
    private static string? TypeId(object value)
    {
        var type = value.GetType();
        var name = string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name;
        return name.Length <= 256 && name.All(x => char.IsAsciiLetterOrDigit(x) || x is '.' or '_' or '+' or '`') ? name : null;
    }
    private static string? PropertyId(string? value) => value is { Length: > 0 and <= 128 } &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(x => char.IsAsciiLetterOrDigit(x) || x == '_') ? value : null;
    private static string VisibilityId(Visibility value) => value switch
    { Visibility.Visible => "visible", Visibility.Hidden => "hidden", Visibility.Collapsed => "collapsed", _ => "unknown" };
    private static string ScrollBarId(ScrollBarVisibility value) => value switch
    { ScrollBarVisibility.Disabled => "disabled", ScrollBarVisibility.Auto => "auto", ScrollBarVisibility.Hidden => "hidden",
        ScrollBarVisibility.Visible => "visible", _ => "unknown" };
    private static string StatusId(BindingStatus value) => value switch
    { BindingStatus.Unattached => "unattached", BindingStatus.Inactive => "inactive", BindingStatus.Active => "active",
        BindingStatus.Detached => "detached", BindingStatus.AsyncRequestPending => "asyncRequestPending", BindingStatus.PathError => "pathError",
        BindingStatus.UpdateTargetError => "updateTargetError", BindingStatus.UpdateSourceError => "updateSourceError", _ => "unknown" };
    private static string SourceId(BaseValueSource value) => value switch
    { BaseValueSource.Default => "default", BaseValueSource.Inherited => "inherited", BaseValueSource.Local => "local",
        BaseValueSource.Style => "style", BaseValueSource.StyleTrigger => "styleTrigger", BaseValueSource.DefaultStyle => "defaultStyle",
        BaseValueSource.DefaultStyleTrigger => "defaultStyleTrigger", BaseValueSource.ImplicitStyleReference => "implicitStyleReference",
        BaseValueSource.ParentTemplate => "parentTemplate", BaseValueSource.ParentTemplateTrigger => "parentTemplateTrigger",
        BaseValueSource.TemplateTrigger => "templateTrigger", _ => "unknown" };
}
