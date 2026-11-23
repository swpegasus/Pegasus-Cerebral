using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Cerebral.Desktop.Plugins;

namespace Cerebral.HelpPlugin;

/// <summary>Passive contract map: compact overview, then owner-scoped connected drilldown.</summary>
public sealed class HelpArchitectureView : UserControl
{
    private readonly Canvas map=new(), paths=new(), controls=new();
    private readonly StackPanel inspector=new();
    private readonly ComboBox scopePicker=new();
    private readonly Dictionary<string,Button> nodeButtons=new(StringComparer.Ordinal), edgeButtons=new(StringComparer.Ordinal);
    private readonly Dictionary<string,List<TextBlock>> nodeTexts=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Rect> nodeRects=new(StringComparer.Ordinal), groupRects=new(StringComparer.Ordinal);
    private readonly Dictionary<string,List<Shape>> edgeMarks=new(StringComparer.Ordinal);
    private readonly Action<string,string> openReference;
    private string selected, scope="overview";
    private string? hover;
    private double mapWidth;
    private bool changingScope;

    public HelpArchitectureView(Action<string,string> openReference,string initial="n:core",string? initialScope=null)
    {
        this.openReference=openReference;selected=Valid(initial)?initial:"n:core";
        AutomationProperties.SetAutomationId(this,"HelpArchitecture");
        AutomationProperties.SetName(this,"Interactive Core contract and data-flow map");
        var layout=new StackPanel();
        layout.Children.Add(Text("Core and contract flows",18,true));
        layout.Children.Add(Text("Logical framework boundary; current Worker still composes host/domain/model facilities. Source map, not live status.",12,muted:true));
        var nav=new DockPanel{Margin=new Thickness(0,6,0,6)};
        var overview=new Button{Content="Core overview",Padding=new Thickness(8,4,8,4),Margin=new Thickness(0,0,8,0)};
        AutomationProperties.SetAutomationId(overview,"HelpArchitectureOverview");
        overview.Click+=(_,_)=>Explore("overview");nav.Children.Add(overview);
        scopePicker.Items.Add(new ComboBoxItem{Content="Core overview",Tag="overview"});
        foreach(var node in HelpArchitectureCatalog.Nodes.Where(n=>n.ParentId is null))
            scopePicker.Items.Add(new ComboBoxItem{Content=node.Title,Tag=node.Id});
        scopePicker.SelectedIndex=0;
        scopePicker.SelectionChanged+=(_,_)=>{if(!changingScope&&scopePicker.SelectedItem is ComboBoxItem{Tag:string id})Explore(id);};
        AutomationProperties.SetAutomationId(scopePicker,"HelpArchitectureScope");
        AutomationProperties.SetName(scopePicker,"Explore a component's contract pipeline");
        nav.Children.Add(scopePicker);layout.Children.Add(nav);
        AutomationProperties.SetAutomationId(inspector,"HelpArchitectureInspector");
        inspector.Margin=new Thickness(0,4,0,8);layout.Children.Add(inspector);
        map.Children.Add(paths);map.Children.Add(controls);
        map.SetResourceReference(BackgroundProperty,SystemColors.WindowBrushKey);
        AutomationProperties.SetAutomationId(map,"HelpArchitectureMap");
        var viewport=new ScrollViewer{Content=map,Height=720,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,CanContentScroll=false,Padding=new Thickness(6),BorderThickness=new Thickness(1)};
        viewport.SetResourceReference(BorderBrushProperty,SystemColors.ControlDarkBrushKey);
        AutomationProperties.SetAutomationId(viewport,"HelpArchitectureViewport");
        AutomationProperties.SetName(viewport,"Contract map viewport, pan when narrow");
        layout.Children.Add(viewport);
        layout.Children.Add(Text("Processes are rounded; outcome classification is a diamond; owner stores are rectangular. Solid: source boundary. Dashed: private/proposed. Select a branch to reveal its contracts and fringe parts.",12,muted:true));
        Content=layout;SizeChanged+=(_,_)=>ArrangeMap(Math.Max(900,ActualWidth-32),false);
        ArrangeMap(900,true);Select(selected,initialScope is null&&selected!="n:core");
        if(initialScope is not null)Explore(ValidScope(initialScope)?initialScope:"overview");
    }

    public event Action<string>? SelectionChanged;
    public string SelectedReference=>selected;
    public string ScopeId=>scope;
    public void Explore(string id)
    {
        if(!ValidScope(id))throw new ArgumentException("Unknown map scope.",nameof(id));
        scope=id;changingScope=true;
        scopePicker.SelectedItem=scopePicker.Items.OfType<ComboBoxItem>().Single(i=>Equals(i.Tag,id));
        changingScope=false;ArrangeMap(mapWidth,true);
    }
    private static bool ValidScope(string id)=>id=="overview"||HelpArchitectureCatalog.Nodes.Any(n=>n.Id==id&&n.ParentId is null);
    private static bool Valid(string value)=>value.StartsWith("n:",StringComparison.Ordinal)
        ?HelpArchitectureCatalog.Nodes.Any(n=>"n:"+n.Id==value):HelpArchitectureCatalog.Edges.Any(e=>"e:"+e.Id==value);
    private static string RootGroup(string id)=>HelpArchitectureCatalog.Nodes.Single(n=>n.Id==id).ParentId??id;

    private void ArrangeMap(double width,bool force)
    {
        if(!force&&Math.Abs(width-mapWidth)<1)return;
        // Scope and size changes replace map controls. Preserve the user's keyboard position
        // only when focus was inside the replaced controls; never move focus on first render.
        var focusedId = System.Windows.Input.Keyboard.FocusedElement is FrameworkElement focused && controls.IsAncestorOf(focused)
            ? AutomationProperties.GetAutomationId(focused) : null;
        mapWidth=width;map.Width=paths.Width=controls.Width=width;
        map.Height=paths.Height=controls.Height=scope=="overview"?760:900;
        paths.Children.Clear();controls.Children.Clear();nodeButtons.Clear();edgeButtons.Clear();nodeTexts.Clear();nodeRects.Clear();groupRects.Clear();edgeMarks.Clear();
        if(scope=="overview")ArrangeOverview(width);else if(scope=="core")ArrangeCore(width);else ArrangeBranch(width);
        var visible=HelpArchitectureCatalog.Edges.Where(e=>nodeRects.ContainsKey(e.From)&&nodeRects.ContainsKey(e.To)).ToArray();
        if(scope=="overview")visible=HelpArchitectureCatalog.Edges.Where(e=>e.Id!="settings.proposal" && RootGroup(e.From)!=RootGroup(e.To)
            &&HelpArchitectureCatalog.Nodes.Single(n=>n.Id==(RootGroup(e.From)=="core"?e.To:e.From)).ParentId is null).ToArray();
        else if(scope=="core")visible=HelpArchitectureCatalog.Edges.Where(e=>nodeRects.ContainsKey(e.From)&&nodeRects.ContainsKey(e.To)).ToArray();
        foreach(var edge in visible)DrawConnection(edge,scope=="overview");
        Highlight();
        if (!string.IsNullOrEmpty(focusedId))
        {
            var replacement = controls.Children.OfType<FrameworkElement>().SingleOrDefault(element =>
                AutomationProperties.GetAutomationId(element) == focusedId);
            (replacement ?? scopePicker).Focus();
        }
    }

    private void ArrangeOverview(double width)
    {
        var coreRect=new Rect((width-240)/2,270,240,170);
        AddBoundary("core",coreRect);
        CreateNode(HelpArchitectureCatalog.Nodes.Single(n=>n.Id=="core"),new Rect(coreRect.X+8,coreRect.Y+18,coreRect.Width-16,100),false);
        var explore=new Button{Content="Explore Core pipeline",Padding=new Thickness(8,4,8,4)};
        AutomationProperties.SetAutomationId(explore,"HelpArchitectureExploreCore");explore.Click+=(_,_)=>Explore("core");
        Place(controls,explore,coreRect.X+28,coreRect.Bottom-38);
        foreach(var node in HelpArchitectureCatalog.Nodes.Where(n=>n.ParentId is null&&n.Id!="core"))
        {
            var y=new[]{26,164,318,490,656}[node.Row];
            var rect=new Rect(node.Side=="left"?12:width-190,y,178,62);
            groupRects[node.Id]=rect;CreateNode(node,rect,false);
        }
    }

    private void ArrangeCore(double width)
    {
        var coreRect=new Rect((width-360)/2,8,360,862);AddBoundary("core",coreRect);
        CreateNode(HelpArchitectureCatalog.Nodes.Single(n=>n.Id=="core"),new Rect(coreRect.X+8,16,344,60),false);
        var order=new[]{"input-core","admission-core","engine-bridge","dispatch-core","outcomes-core","publish-core","discovery-core"};
        for(var i=0;i<order.Length;i++)
        {
            var node=HelpArchitectureCatalog.Nodes.Single(n=>n.Id==order[i]);
            CreateNode(node,new Rect(coreRect.X+155,104+i*100,186,node.Kind=="decision"?64:52),true);
        }
        CreateNode(HelpArchitectureCatalog.Nodes.Single(n=>n.Id=="journal"),new Rect(coreRect.X+12,382,124,82),true);
        CreateNode(HelpArchitectureCatalog.Nodes.Single(n=>n.Id=="settings-core"),new Rect(coreRect.X+12,672,124,86),true);
        foreach(var node in HelpArchitectureCatalog.Nodes.Where(n=>n.ParentId is null&&n.Id!="core"))
        {
            var y=new[]{104,260,414,584,742}[node.Row];
            var rect=new Rect(node.Side=="left"?8:width-180,y,172,58);
            groupRects[node.Id]=rect;CreateNode(node,rect,false);
        }
    }

    private void ArrangeBranch(double width)
    {
        var coreRect=new Rect(14,40,330,808);AddBoundary("core",coreRect);
        CreateNode(HelpArchitectureCatalog.Nodes.Single(n=>n.Id=="core"),new Rect(24,50,310,60),false);
        var pipeline=new[]{"input-core","admission-core","engine-bridge","dispatch-core","outcomes-core","publish-core","discovery-core"};
        for(var i=0;i<pipeline.Length;i++)
            CreateNode(HelpArchitectureCatalog.Nodes.Single(n=>n.Id==pipeline[i]),new Rect(166,150+i*100,158,52),true);
        CreateNode(HelpArchitectureCatalog.Nodes.Single(n=>n.Id=="journal"),new Rect(26,350,120,76),true);
        if(scope=="config")CreateNode(HelpArchitectureCatalog.Nodes.Single(n=>n.Id=="settings-core"),new Rect(26,574,120,86),true);
        var branch=HelpArchitectureCatalog.Nodes.Single(n=>n.Id==scope);
        var children=HelpArchitectureCatalog.Nodes.Where(n=>n.ParentId==scope).OrderBy(n=>n.Row).ToArray();
        var rect=new Rect(width-286,80,272,Math.Max(160,130+children.Length*140));AddBoundary(scope,rect);
        CreateNode(branch,new Rect(rect.X+8,rect.Y+8,256,64),false);
        for(var i=0;i<children.Length;i++)CreateNode(children[i],new Rect(rect.X+18,rect.Y+122+i*140,236,64),true);
    }

    private void AddBoundary(string id,Rect rect)
    {
        groupRects[id]=rect;var border=new Border{Width=rect.Width,Height=rect.Height,BorderThickness=new Thickness(1)};
        border.SetResourceReference(Border.BorderBrushProperty,SystemColors.ControlDarkBrushKey);Place(controls,border,rect.X,rect.Y);
    }

    private void CreateNode(HelpArchitectureNode node,Rect rect,bool child)
    {
        var body=new StackPanel();var title=Text(node.Title,child?12:14,true);body.Children.Add(title);
        var texts=new List<TextBlock>{title};
        if(!child){var caption=Text(node.Id=="core"?"Admit · dispatch · return · hooks":node.ParentId is null&&scope=="overview"?"Select to explore contracts":node.Caption,12);body.Children.Add(caption);texts.Add(caption);}
        var button=new Button{Content=body,Width=rect.Width,Height=rect.Height,Padding=new Thickness(6,3,6,3),HorizontalContentAlignment=HorizontalAlignment.Stretch,ToolTip=node.Current};
        if(node.Kind is "process" or "decision")
        {
            var grid=new FrameworkElementFactory(typeof(Grid));
            FrameworkElementFactory background;
            if(node.Kind=="decision")
            {
                background=new FrameworkElementFactory(typeof(Polygon));
                background.SetValue(Polygon.PointsProperty,new PointCollection{new(0,.5),new(.5,0),new(1,.5),new(.5,1)});
                background.SetValue(Shape.StretchProperty,Stretch.Fill);
                background.SetValue(Shape.FillProperty,new TemplateBindingExtension(Control.BackgroundProperty));
                background.SetValue(Shape.StrokeProperty,new DynamicResourceExtension(SystemColors.ControlDarkBrushKey));
            }
            else
            {
                background=new FrameworkElementFactory(typeof(Border));
                background.SetValue(Border.CornerRadiusProperty,new CornerRadius(22));
                background.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
                background.SetValue(Border.BorderBrushProperty,new DynamicResourceExtension(SystemColors.ControlDarkBrushKey));
                background.SetValue(Border.BorderThicknessProperty,new Thickness(1));
            }
            background.SetValue(IsHitTestVisibleProperty,false);grid.AppendChild(background);
            var presenter=new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty,new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(MarginProperty,new Thickness(node.Kind=="decision"?28:10,4,node.Kind=="decision"?28:10,4));
            presenter.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);grid.AppendChild(presenter);
            button.Template=new ControlTemplate(typeof(Button)){VisualTree=grid};
        }
        AutomationProperties.SetAutomationId(button,"HelpArchitectureNode."+node.Id);
        AutomationProperties.SetName(button,"Select "+node.Title+". "+node.Owner);
        Bind(button,"n:"+node.Id);Place(controls,button,rect.X,rect.Y);
        nodeButtons[node.Id]=button;nodeRects[node.Id]=rect;nodeTexts[node.Id]=texts;
    }

    private void DrawConnection(HelpArchitectureEdge edge,bool collapsed)
    {
        var from=collapsed?RootGroup(edge.From):edge.From;var to=collapsed?RootGroup(edge.To):edge.To;
        var a=nodeRects[from];var b=nodeRects[to];
        var request=edge.Id.EndsWith(".request",StringComparison.Ordinal);
        var lane=edge.Id.EndsWith(".return",StringComparison.Ordinal)?13:request?-13:0;
        List<Point> points;Rect labelRect;
        if(!collapsed && (edge.From=="journal" || edge.To=="journal"))
        {
            var store=edge.From=="journal"?a:b;var process=edge.From=="journal"?b:a;
            var fromStore=edge.From=="journal";var above=process.Top<store.Top;
            var storeY=above?store.Top:store.Bottom;
            var processY=process.Top+process.Height/2;
            var routeX=store.Left+store.Width/2+(fromStore?-16:0);
            var outgoing=new List<Point>{new(process.Left,processY),new(routeX,processY),new(routeX,storeY)};
            points=fromStore?outgoing.AsEnumerable().Reverse().ToList():outgoing;
            labelRect=new Rect(routeX-43,(processY+storeY)/2+(fromStore?-23:3),86,20);
        }
        else if(edge.Id=="settings.proposal")
        {
            var outer=a;var store=b;var routeX=groupRects["core"].Right+22;
            var lowerY=store.Bottom+36;var storeX=store.Left+store.Width/2;
            points=[new(outer.Left,outer.Top+outer.Height/2),new(routeX,outer.Top+outer.Height/2),
                new(routeX,lowerY),new(storeX,lowerY),new(storeX,store.Bottom)];
            labelRect=new Rect(storeX+8,lowerY-10,86,20);
        }
        else if(collapsed)
        {
            var other=from=="core"?to:from;var root=HelpArchitectureCatalog.Nodes.Single(n=>n.Id==other);
            var core=nodeRects["core"];var outer=nodeRects[other];var left=root.Side=="left";
            var outerY=outer.Top+outer.Height/2+lane;
            var coreY=core.Top+22+root.Row*26+lane/2;
            var coreX=left?core.Left:core.Right;var outerX=left?outer.Right:outer.Left;
            var bendX=(coreX+outerX)/2+(request?-8:8);
            var outward=new List<Point>{new(coreX,coreY),new(bendX,coreY),new(bendX,outerY),new(outerX,outerY)};
            points=from=="core"?outward:outward.AsEnumerable().Reverse().ToList();
            labelRect=new Rect(left?outerX+6:outerX-92,outerY-10,86,20);
        }
        else if(Math.Abs(a.Left-b.Left)<8 || RootGroup(edge.From)==RootGroup(edge.To) && RootGroup(edge.From)!="core")
        {
            var down=b.Top>a.Top;
            var x=a.Left+a.Width/2+lane;
            points=[new(x,down?a.Bottom:a.Top),new(x,down?b.Top:b.Bottom)];
            if(Math.Abs(b.Top-a.Top)>150)
            {
                var routeX=a.Right+22+(edge.Id=="pipeline.next"?26:edge.Id.EndsWith(".request",StringComparison.Ordinal)?-10:edge.Id.EndsWith(".return",StringComparison.Ordinal)?10:0);
                points=[points[0],new(routeX,points[0].Y),new(routeX,points[^1].Y),points[^1]];
            }
            labelRect=new Rect(points.Count>2?points[1].X-43:(points[0].X+points[^1].X)/2-43,(points[0].Y+points[^1].Y)/2-10+(request?-13:edge.Id.EndsWith(".return",StringComparison.Ordinal)?13:0),86,20);
        }
        else
        {
            var right=b.Left>a.Left;
            var y1=a.Top+a.Height/2+lane;var y2=b.Top+b.Height/2+lane;
            var x1=right?a.Right:a.Left;var x2=right?b.Left:b.Right;
            var middle=(x1+x2)/2+(request?-8:8);
            points=[new(x1,y1),new(middle,y1),new(middle,y2),new(x2,y2)];
            labelRect=new Rect(middle-43,Math.Abs(y1-y2)<60?(y1+y2)/2-10:y1-10,86,20);
            if(RootGroup(edge.From)=="core" != (RootGroup(edge.To)=="core"))
            {
                var outer=RootGroup(edge.From)=="core"?b:a;
                var outerLeft=outer.Left < groupRects["core"].Left;
                labelRect=new Rect(outerLeft?outer.Right+6:outer.Left-92,outer.Top+outer.Height/2+lane-10,86,20);
            }
        }
        DrawArrow(edge,points);CreateEdgeLabel(edge,labelRect);
    }

    private void DrawArrow(HelpArchitectureEdge edge,IReadOnlyList<Point> points)
    {
        var line=new Polyline{Points=new PointCollection(points),StrokeThickness=1.5,IsHitTestVisible=false};
        if(edge.Proposed)line.StrokeDashArray=new DoubleCollection{4,3};
        var hit=new Polyline{Points=new PointCollection(points),StrokeThickness=14,Stroke=Brushes.Transparent};
        AutomationProperties.SetAutomationId(hit,"HelpArchitectureArrow."+edge.Id);AutomationProperties.SetName(hit,edge.From+" to "+edge.To+": "+edge.Payload);hit.ToolTip=edge.Payload;Bind(hit,"e:"+edge.Id);
        paths.Children.Add(hit);paths.Children.Add(line);var end=points[^1];var direction=end-points[^2];
        if(direction.Length<.1)direction=new Vector(0,1);direction.Normalize();var normal=new Vector(-direction.Y,direction.X);
        var arrow=new Polygon{Points=new PointCollection{end,end-direction*8+normal*4,end-direction*8-normal*4},IsHitTestVisible=false};paths.Children.Add(arrow);edgeMarks[edge.Id]=[line,arrow,hit];
    }
    private void CreateEdgeLabel(HelpArchitectureEdge edge,Rect rect)
    {
        var button=new Button{Content=Text(edge.Label,12),Width=rect.Width,Height=rect.Height,Padding=new Thickness(1),BorderThickness=new Thickness(0),ToolTip=edge.Payload};
        AutomationProperties.SetAutomationId(button,"HelpArchitectureEdge."+edge.Id);
        AutomationProperties.SetName(button,"Select "+edge.From+" to "+edge.To+": "+edge.Label);
        Bind(button,"e:"+edge.Id);Place(controls,button,rect.X,rect.Y);edgeButtons[edge.Id]=button;
    }
    private void Bind(FrameworkElement element,string id)
    {
        if(element is Button button)button.Click+=(_,_)=>Select(id,true);else element.MouseLeftButtonUp+=(_,_)=>Select(id,false);
        element.MouseEnter+=(_,_)=>{hover=id;Highlight();};element.MouseLeave+=(_,_)=>{hover=null;Highlight();};
        element.GotKeyboardFocus+=(_,_)=>{hover=id;Highlight();};element.LostKeyboardFocus+=(_,_)=>{hover=null;Highlight();};
    }
    private void Select(string id, bool explore = false)
    {
        if (!Valid(id)) throw new ArgumentException("Unknown architecture reference.", nameof(id));
        var continueInspectorFocus = Keyboard.FocusedElement is DependencyObject focused && inspector.IsAncestorOf(focused);
        selected = id; hover = null;
        if(explore)
        {
            var root=id.StartsWith("n:",StringComparison.Ordinal)?RootGroup(id[2..]):
                HelpArchitectureCatalog.Edges.Where(e=>e.Id==id[2..]).Select(e=>RootGroup(e.From)=="core"?RootGroup(e.To):RootGroup(e.From)).Single();
            Explore(root);
        }
        inspector.Children.Clear();
        var detail = new StackPanel();
        string page; string section; string heading; string summary;
        if (id.StartsWith("n:", StringComparison.Ordinal))
        {
            var node = HelpArchitectureCatalog.Nodes.Single(n => n.Id == id[2..]);
            heading = node.Title; summary = "Owner · " + node.Owner;
            page = node.PageId; section = node.SectionId;
            detail.Children.Add(Text("Current source · " + node.Current));
            detail.Children.Add(Text("Intended boundary · " + node.Target));
            detail.Children.Add(Text("Source · " + node.Source, 12, muted: true));
            foreach (var edge in HelpArchitectureCatalog.Edges.Where(e => e.From == node.Id || e.To == node.Id))
            {
                var connection = new Button { Content = Text(edge.From + " → " + edge.To + " · " + edge.Label, 12), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 2, 0, 2) };
                connection.Click += (_, _) => Select("e:" + edge.Id, true); detail.Children.Add(connection);
            }
        }
        else
        {
            var edge = HelpArchitectureCatalog.Edges.Single(e => e.Id == id[2..]);
            heading = edge.From + " → " + edge.To + " · " + edge.Label;
            summary = "Payload · " + edge.Payload;
            page = edge.PageId; section = edge.SectionId;
            detail.Children.Add(Text("Direction / ports · " + edge.From + " [" + edge.FromPort + "] → " + edge.To + " [" + edge.ToPort + "]"));
            detail.Children.Add(Text("Contract / identity · " + edge.Contract));
            detail.Children.Add(Text("Status · " + edge.Status));
            detail.Children.Add(Text("Failure / cancellation · " + edge.FailureCancellation));
            detail.Children.Add(Text("Source · " + edge.Source, 12, muted: true));
        }
        var title = Text("Selected: " + heading, 16, true);
        AutomationProperties.SetAutomationId(title, "HelpArchitectureTitle");
        inspector.Children.Add(title);
        var current = Text(summary, 12);
        AutomationProperties.SetAutomationId(current, "HelpArchitectureSummary");
        inspector.Children.Add(current);
        var expander = new Expander { Header = "Contract, direction and boundary details", Content = detail };
        expander.SetResourceReference(ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        AutomationProperties.SetAutomationId(expander, "HelpArchitectureDetails");
        inspector.Children.Add(expander);
        var referencePage = HelpCatalog.Pages.FirstOrDefault(candidate => candidate.Id == page);
        var referenceSection = referencePage?.Sections.FirstOrDefault(candidate => candidate.Id == section);
        if (referencePage is null || referenceSection is null)
        {
            var unavailable = Text("This Help reference is no longer available. Choose a page from Help navigation.", 12, muted: true);
            AutomationProperties.SetAutomationId(unavailable, "HelpArchitectureReferenceUnavailable");
            inspector.Children.Add(unavailable);
        }
        else
        {
            var link = new Button { Content = Text("Help: " + referencePage.Title + " / " + referenceSection.Title, 12), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(link, "HelpArchitectureReference");
            AutomationProperties.SetName(link, "Open Help " + page + " " + section);
            link.Click += (_, _) => openReference(page, section);
            inspector.Children.Add(link);
        }
        // An inspector connection replaces its own button; continue at the new contract details.
        if (continueInspectorFocus)
        {
            expander.IsExpanded = true;
            expander.Focus();
        }
        Highlight(); SelectionChanged?.Invoke(selected);
    }

    private void Highlight()
    {
        var active=hover??selected;var node=active.StartsWith("n:",StringComparison.Ordinal)?active[2..]:null;
        var activeEdges=HelpArchitectureCatalog.Edges.Where(e=>node is not null?e.From==node||e.To==node||RootGroup(e.From)==node||RootGroup(e.To)==node:"e:"+e.Id==active).ToArray();
        var endpoints=activeEdges.SelectMany(e=>new[]{e.From,e.To,RootGroup(e.From),RootGroup(e.To)}).ToHashSet(StringComparer.Ordinal);
        foreach(var pair in nodeButtons)
        {
            var chosen=selected=="n:"+pair.Key;var related=pair.Key==node||endpoints.Contains(pair.Key);
            pair.Value.SetResourceReference(Control.BackgroundProperty,chosen?SystemColors.HighlightBrushKey:related?SystemColors.InactiveSelectionHighlightBrushKey:SystemColors.ControlBrushKey);
            foreach(var text in nodeTexts[pair.Key])text.SetResourceReference(TextBlock.ForegroundProperty,chosen?SystemColors.HighlightTextBrushKey:DesktopThemeResourceKeys.TextBrush);
            AutomationProperties.SetItemStatus(pair.Value,chosen?"Selected":related?"Related":"");
        }
        foreach(var pair in edgeMarks)
        {
            var related=activeEdges.Any(e=>e.Id==pair.Key);var chosen=selected=="e:"+pair.Key;
            foreach(var mark in pair.Value.Take(2)){mark.SetResourceReference(Shape.StrokeProperty,related?SystemColors.HighlightBrushKey:SystemColors.ControlDarkBrushKey);if(mark is Polygon)mark.SetResourceReference(Shape.FillProperty,related?SystemColors.HighlightBrushKey:SystemColors.ControlDarkBrushKey);else mark.Fill=Brushes.Transparent;AutomationProperties.SetItemStatus(mark,related?"Highlighted":"");}
            var button=edgeButtons[pair.Key];button.SetResourceReference(Control.BackgroundProperty,chosen?SystemColors.HighlightBrushKey:related?SystemColors.InactiveSelectionHighlightBrushKey:SystemColors.WindowBrushKey);
            if(button.Content is TextBlock text)text.SetResourceReference(TextBlock.ForegroundProperty,chosen?SystemColors.HighlightTextBrushKey:DesktopThemeResourceKeys.TextBrush);
            AutomationProperties.SetItemStatus(button,chosen?"Selected":related?"Related":"");
        }
    }
    private static void Place(Canvas canvas, FrameworkElement element, double x, double y)
    { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element); }

    private static TextBlock Text(string value, double size = 14, bool heading = false, bool muted = false)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
        text.SetResourceReference(TextBlock.ForegroundProperty, muted ? DesktopThemeResourceKeys.MutedTextBrush : DesktopThemeResourceKeys.TextBrush);
        return text;
    }
}
