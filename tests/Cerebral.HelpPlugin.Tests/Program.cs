using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;
using Cerebral.HelpPlugin;

internal static partial class Program
{
    private static int checks;
    private static readonly List<object> captures=[];
    private static readonly List<string> interactions=[];
    [STAThread]
    private static int Main(string[] arguments)
    {
        var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","evidence"));
        Directory.CreateDirectory(output);
        try
        {
            if(arguments.Contains("--recovery-only")) return RunRecovery(output);
            if(arguments.Contains("--export-only"))
            {
                File.WriteAllText(Path.Combine(output,"architecture-data.json"),JsonSerializer.Serialize(new{nodes=HelpArchitectureCatalog.Nodes,edges=HelpArchitectureCatalog.Edges},new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
                Console.WriteLine("PASSIVE_GRAPH_EXPORTED");return 0;
            }
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app.Resources[DesktopThemeResourceKeys.TextBrush]=new SolidColorBrush(Color.FromRgb(30,41,59));
            app.Resources[DesktopThemeResourceKeys.MutedTextBrush]=new SolidColorBrush(Color.FromRgb(75,85,99));
            var host=new InertHost();using var plugin=new HelpPlugin();plugin.Initialize(host);
            var mount=(DesktopMountView)plugin.MountContributions.Single();var view=mount.CreateView();
            Check(mount.Descriptor.Slot==DesktopMountSlots.SidebarTabs&&plugin.Panels.Count==0,"frozen passive mount");
            Check(((IList<HelpArchitectureNode>)HelpArchitectureCatalog.Nodes).IsReadOnly&&((IList<HelpArchitectureEdge>)HelpArchitectureCatalog.Edges).IsReadOnly,"immutable graph");
            Check(HelpArchitectureCatalog.Nodes.Count==30&&HelpArchitectureCatalog.Edges.Count==50,"explicit graph bounds");
            Check(HelpWorkflowCatalog.Stages.Where(s=>s.ConnectsTo is not null).All(s=>s.ConnectsTo is System.Collections.ObjectModel.ReadOnlyCollection<string>),"retained links immutable");
            Check(!Descendants(view).Any(e=>AutomationProperties.GetAutomationId(e)=="HelpArchitectureEdge.settings.proposal"),"settings detail hidden until Core/config drilldown");
            Capture(view,output,"architecture-ui-wide",1480,1140);
            Check(Descendants(view).OfType<System.Windows.Shapes.Polyline>().Where(p=>!p.IsHitTestVisible).All(p=>p.Fill is null||p.Fill is SolidColorBrush brush&&brush.Color.A==0),"connector polylines have no opaque area fill");CaptureMap(view,output,"architecture-map-wide");
            Find<HelpArchitectureView>(view,"HelpArchitecture").Explore("core");Flush();CaptureMap(view,output,"core-pipeline-map");
            Find<Button>(view,"HelpArchitectureReference").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Flush();
            Check(Find<Expander>(view,"HelpArchitecturePoint.n:core").IsExpanded,"Core Help opens exact point");
            Select(view,"overview");
            Check(Find<HelpArchitectureView>(view,"HelpArchitecture").ScopeId=="core"&&Find<HelpArchitectureView>(view,"HelpArchitecture").SelectedReference=="n:core","explicit Core scope survives Help roundtrip without changing selection");
            Find<HelpArchitectureView>(view,"HelpArchitecture").Explore("plugins");Flush();CaptureMap(view,output,"plugin-hooks-map");
            foreach(var node in HelpArchitectureCatalog.Nodes)
            {
                Find<HelpArchitectureView>(view,"HelpArchitecture").Explore(node.ParentId??node.Id);
                var button=Find<Button>(view,"HelpArchitectureNode."+node.Id);button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Flush();
                Check(Find<HelpArchitectureView>(view,"HelpArchitecture").SelectedReference=="n:"+node.Id&&AutomationProperties.GetItemStatus(Find<Button>(view,"HelpArchitectureNode."+node.Id))=="Selected","node "+node.Id);
                Check(Find<TextBlock>(view,"HelpArchitectureTitle").Text.Contains(node.Title),"node inspector "+node.Id);
                var detail=Find<Expander>(view,"HelpArchitectureDetails");detail.IsExpanded=true;view.UpdateLayout();Flush();
                Check(Descendants(detail).OfType<TextBlock>().Any(t=>t.Text.Contains(node.Current)),"current boundary "+node.Id);
                interactions.Add("native node click: "+node.Id);
            }
            foreach(var edge in HelpArchitectureCatalog.Edges)
            {
                Find<HelpArchitectureView>(view,"HelpArchitecture").Explore(Root(edge.From)=="core"?Root(edge.To):Root(edge.From));
                Find<Button>(view,"HelpArchitectureEdge."+edge.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Flush();
                Check(Find<HelpArchitectureView>(view,"HelpArchitecture").SelectedReference=="e:"+edge.Id,"edge label "+edge.Id);
                Check(Find<TextBlock>(view,"HelpArchitectureTitle").Text.Contains(edge.From+" → "+edge.To)&&Find<TextBlock>(view,"HelpArchitectureSummary").Text.Contains(edge.Payload),"direction and payload "+edge.Id);
                var detail=Find<Expander>(view,"HelpArchitectureDetails");detail.IsExpanded=true;view.UpdateLayout();Flush();
                Check(Descendants(detail).OfType<TextBlock>().Any(t=>t.Text.Contains(edge.Contract))&&Descendants(detail).OfType<TextBlock>().Any(t=>t.Text.Contains(edge.FailureCancellation)),"contract/failure/cancel "+edge.Id);
                var arrowScope=Find<HelpArchitectureView>(view,"HelpArchitecture").ScopeId;
                Find<Button>(view,"HelpArchitectureNode.core").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Find<HelpArchitectureView>(view,"HelpArchitecture").Explore(arrowScope);Flush();
                Check(Find<HelpArchitectureView>(view,"HelpArchitecture").SelectedReference!="e:"+edge.Id,"different selection before arrow "+edge.Id);
                var arrow=Find<System.Windows.Shapes.Polyline>(view,"HelpArchitectureArrow."+edge.Id);
                arrow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonUpEvent});
                Check(Find<HelpArchitectureView>(view,"HelpArchitecture").SelectedReference=="e:"+edge.Id,"arrow path "+edge.Id);
                Find<Button>(view,"HelpArchitectureReference").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Flush();
                Check(AutomationProperties.GetItemStatus(Find<Border>(view,"HelpSection."+edge.PageId+"."+edge.SectionId))=="Linked section","exact Help "+edge.Id);
                var point=Find<Expander>(view,"HelpArchitecturePoint.e:"+edge.Id);
                Check(point.IsExpanded&&AutomationProperties.GetItemStatus(point)=="Linked contract point","exact expanded contract point "+edge.Id);
                Check(Descendants(point).OfType<TextBlock>().Any(t=>t.Text.Contains(edge.Payload))&&Descendants(point).OfType<TextBlock>().Any(t=>t.Text.Contains(edge.Contract)),"Help payload and contract "+edge.Id);
                Select(view,"overview");Check(Find<HelpArchitectureView>(view,"HelpArchitecture").SelectedReference=="e:"+edge.Id,"return selection "+edge.Id);
                Check(Find<HelpArchitectureView>(view,"HelpArchitecture").ScopeId==(Root(edge.From)=="core"?Root(edge.To):Root(edge.From)),"return scope "+edge.Id);
                interactions.Add("native edge label/path/Help: "+edge.Id);
            }
            Find<HelpArchitectureView>(view,"HelpArchitecture").Explore("remote");
            Find<Button>(view,"HelpArchitectureNode.core").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<HelpArchitectureView>(view,"HelpArchitecture").Explore("remote");
            var hover=Find<System.Windows.Shapes.Polyline>(view,"HelpArchitectureArrow.remote.return");
            hover.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseEnterEvent});
            Check(Find<HelpArchitectureView>(view,"HelpArchitecture").SelectedReference=="n:core"&&AutomationProperties.GetItemStatus(Find<Button>(view,"HelpArchitectureNode.remote"))=="Related","arrow hover preserves selection");
            hover.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseLeaveEvent});
            Find<Button>(view,"HelpArchitectureEdge.remote.return").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Capture(view,output,"remote-return-inspector",1480,1140);CaptureMap(view,output,"remote-return-map");
            Check(HelpArchitectureCatalog.Edges.Where(e=>e.From=="private-store"||e.To=="private-store").All(e=>e.From=="plugins"||e.To=="plugins"),"private store stays with plugin");
            Check(!HelpArchitectureCatalog.Edges.Any(e=>Root(e.From)=="plugins"&&(Root(e.To)=="local"||Root(e.To)=="remote")),"no plugin-model bypass");
            Check(HelpArchitectureCatalog.Edges.Where(e=>e.From=="hosted"||e.To=="hosted").All(e=>e.Proposed),"hosted remains proposed");
            Check(HelpArchitectureCatalog.Nodes.Single(n=>n.Id=="journal").ParentId=="core","durability inside Core");
            Check(!HelpArchitectureCatalog.Edges.Single(e=>e.Id=="settings.proposal").Proposed&&HelpArchitectureCatalog.Nodes.Single(n=>n.Id=="settings-core").Current.Contains("schema 1")&&HelpArchitectureCatalog.Edges.Single(e=>e.Id=="settings.proposal").Status.Contains("retained private owner evidence"),"shared settings source and retained private evidence remain distinct from current adoption");
            Select(view,"reference");
            Check(!AutomationProperties.GetName(Find<Border>(view,"HelpSection.reference.terms")).Contains("content pending"),"authored reference does not announce pending content");
            foreach(var discovery in HelpDiscoveryCatalog.Points)
            {
                var entry=Find<Expander>(view,"HelpDiscovery."+discovery.Id);entry.IsExpanded=true;Flush();
                Find<Button>(view,"HelpDiscoveryLink."+discovery.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Flush();
                Check(AutomationProperties.GetItemStatus(Find<Border>(view,"HelpSection."+discovery.HelpPageId+"."+discovery.HelpSectionId))=="Linked section","discovery Help destination "+discovery.Id);
                if(discovery.HelpPageId=="reference"&&discovery.HelpSectionId=="discovery")
                {
                    Check(entry.IsExpanded&&AutomationProperties.GetItemStatus(entry)=="Linked contract point","self discovery links its own entry "+discovery.Id);
                    Check(!Descendants(view).OfType<Expander>().Any(point=>AutomationProperties.GetAutomationId(point).StartsWith("HelpArchitecturePoint.")&&AutomationProperties.GetItemStatus(point)=="Linked contract point"),"discovery link does not reuse unrelated map selection");
                }
                Select(view,"reference");
            }
            var navigateReference=typeof(HelpView).GetMethod("OpenReference",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing reference callback");
            var navigatePoint=typeof(HelpView).GetMethod("OpenReferencePoint",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing exact reference navigation");
            foreach(var destination in new[]{new[]{"absent-page","contract-map"},new[]{"reference","absent-section"}})
            {
                navigateReference.Invoke(view,destination);Flush();
                Check(TryFind<Border>(view,"HelpReferenceUnavailable") is not null,"missing authored target is visible");
            }
            navigatePoint.Invoke(view,new object?[]{"reference","discovery","d:absent-point"});Flush();
            Check(TryFind<Border>(view,"HelpReferenceUnavailable") is not null&&!Descendants(view).Any(element=>element is Border border&&AutomationProperties.GetItemStatus(border)=="Linked section"),"missing exact point clears linked section");
            navigatePoint.Invoke(view,new object?[]{"reference","contracts",null});Flush();
            Check(TryFind<Border>(view,"HelpReferenceUnavailable") is null&&AutomationProperties.GetItemStatus(Find<Border>(view,"HelpSection.reference.contracts"))=="Linked section","valid section link clears bounded error");
            var linkedContracts=Find<Border>(view,"HelpSection.reference.contracts");
            Check(AutomationProperties.GetItemStatus(linkedContracts)=="Linked section","capture actual linked section before leaving");
            Select(view,"overview");
            Check(AutomationProperties.GetItemStatus(linkedContracts)=="","detached reference highlight cleared after navigation");
            Select(view,"assistant");Check(!Find<TextBox>(view,"HelpAssistantInput").IsEnabled&&!Find<Button>(view,"HelpAssistantSend").IsEnabled,"assistant inert");Capture(view,output,"assistant",1120,780);
            Select(view,"overview");Capture(view,output,"architecture-ui-narrow",460,1140);
            Check(Find<ComboBox>(view,"HelpCompactNavigation").Visibility==Visibility.Visible,"compact navigation");
            var viewport=Find<ScrollViewer>(view,"HelpArchitectureViewport");Check(viewport.ScrollableWidth>0&&viewport.ViewportWidth<960,"readable map pans in narrow viewport");
            Capture(view,output,"architecture-ui-small",320,1140);ApplySyntheticDark(view);Capture(view,output,"architecture-ui-dark",1480,1140);CaptureMap(view,output,"architecture-map-dark");
            Check(host.Calls==0,"zero host calls");
            plugin.Dispose();var rejected=false;try{mount.CreateView();}catch(ObjectDisposedException){rejected=true;}Check(rejected,"disposed factory");
            File.WriteAllText(Path.Combine(output,"offscreen-evidence.json"),JsonSerializer.Serialize(new{schema="cerebral.help.architecture-offscreen/1",tier="programmatic native WPF offscreen interaction/renderer fixture",passed=true,checks,captures,interactions,nodes=HelpArchitectureCatalog.Nodes.Count,edges=HelpArchitectureCatalog.Edges.Count,hostCalls=host.Calls,humanKeyboardScreenReader="not performed",sourceActivation=false},new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
            File.WriteAllText(Path.Combine(output,"architecture-data.json"),JsonSerializer.Serialize(new{nodes=HelpArchitectureCatalog.Nodes,edges=HelpArchitectureCatalog.Edges},new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
            Console.WriteLine(JsonSerializer.Serialize(new{passed=true,checks,captures=captures.Count,hostCalls=host.Calls}));app.Shutdown();return 0;
        }
        catch(Exception error){Console.Error.WriteLine("HELP_ARCHITECTURE_FIXTURE_FAILED: "+error.GetType().Name+": "+error.Message);return 1;}
    }
    private static string Root(string id)=>HelpArchitectureCatalog.Nodes.Single(n=>n.Id==id).ParentId??id;
    private static void CaptureMap(FrameworkElement view,string output,string name)
    {
        view.UpdateLayout();Flush();var map=Find<Canvas>(view,"HelpArchitectureMap");var width=(int)Math.Ceiling(map.ActualWidth);var height=(int)Math.Ceiling(map.ActualHeight);
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(map);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(Path.Combine(output,name+".png"));encoder.Save(stream);captures.Add(new{name,width,height,tier="native full diagram canvas"});
    }
    private static void Check(bool condition, string description)
    { if (!condition) throw new InvalidOperationException(description); checks++; }

    private static void Select(FrameworkElement view, string id)
    {
        var navigation = Find<ListBox>(view, "HelpNavigation");
        navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, id));
        Flush();
        interactions.Add("native ListBox selection: " + id);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static string Title(FrameworkElement view) => Find<TextBlock>(view, "HelpPageTitle").Text;
    private static T Find<T>(DependencyObject root, string automationId) where T : FrameworkElement
    {
        if (root is T element && AutomationProperties.GetAutomationId(element) == automationId) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = TryFind<T>(VisualTreeHelper.GetChild(root, index), automationId);
            if (found is not null) return found;
        }
        throw new InvalidOperationException("Missing control " + automationId);
    }
    private static T? TryFind<T>(DependencyObject root, string id) where T : FrameworkElement
    {
        if (root is T element && AutomationProperties.GetAutomationId(element) == id) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = TryFind<T>(VisualTreeHelper.GetChild(root, index), id);
            if (found is not null) return found;
        }
        return null;
    }
    private static void Layout(FrameworkElement view, int width, int height)
    {
        view.Width = width; view.Height = height;
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        Flush();
    }
    private static void Capture(FrameworkElement view, string output, string name, int width, int height)
    {
        Layout(view, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(stream);
        captures.Add(new { name, width, height, pageTitle = Title(view) });
    }
    // Fixture palette only; this does not load or claim compatibility with the production host theme.
    private static void ApplySyntheticDark(FrameworkElement view)
    {
        static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Color.FromRgb(red, green, blue));
        view.Resources[DesktopThemeResourceKeys.TextBrush] = Brush(229, 231, 235);
        view.Resources[DesktopThemeResourceKeys.MutedTextBrush] = Brush(203, 213, 225);
        view.Resources[SystemColors.WindowBrushKey] = Brush(17, 24, 39);
        view.Resources[SystemColors.ControlBrushKey] = Brush(31, 41, 55);
        view.Resources[SystemColors.ControlTextBrushKey] = Brush(229, 231, 235);
        view.Resources[SystemColors.WindowTextBrushKey] = Brush(229, 231, 235);
        view.Resources[SystemColors.ControlDarkBrushKey] = Brush(100, 116, 139);
        view.Resources[SystemColors.HighlightBrushKey] = Brush(51, 65, 85);
        view.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        view.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Brush(51, 65, 85);
        view.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Brushes.White;
        var buttons = new Style(typeof(Button));
        buttons.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(SystemColors.ControlBrushKey)));
        buttons.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SystemColors.ControlTextBrushKey)));
        buttons.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(SystemColors.ControlDarkBrushKey)));
        view.Resources[typeof(Button)] = buttons;
    }

    private static void Flush()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class InertHost : IDesktopPluginHost
    {
        public int Calls { get; private set; }
        private T Unexpected<T>() { Calls++; throw new InvalidOperationException("Unexpected host operation."); }
        public DesktopConversationSnapshot GetConversation() => Unexpected<DesktopConversationSnapshot>();
        public DesktopDraftSnapshot GetDraft() => Unexpected<DesktopDraftSnapshot>();
        public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged
        { add => Unexpected<object>(); remove => Unexpected<object>(); }
        public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted
        { add => Unexpected<object>(); remove => Unexpected<object>(); }
        public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update, CancellationToken cancellationToken = default) => Unexpected<Task<DesktopOperationResult>>();
        public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) => Unexpected<Task<DesktopOperationResult>>();
        public Task<DesktopOperationResult> SteerAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) => Unexpected<Task<DesktopOperationResult>>();
        public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding binding, CancellationToken cancellationToken = default) => Unexpected<Task<DesktopOperationResult>>();
        public void DismissInputGuidance() => Unexpected<object>();
        public void ReportStatus(string text) => Unexpected<object>();
        public void RefreshPresentation() => Unexpected<object>();
    }
}
