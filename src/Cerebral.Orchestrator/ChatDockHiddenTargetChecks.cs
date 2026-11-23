using System.Windows;
using System.Windows.Controls;

namespace Cerebral.Orchestrator;

/// <summary>Unshown control fixture; no Window, native Show, dispatcher loop, Worker or model.</summary>
public static class ChatDockHiddenTargetChecks
{
    public static int Verify()
    {
        var checks=0;
        void Check(bool value,string message)
        { if(!value) throw new InvalidOperationException(message);checks++; }
        var host=new Grid();
        var main=new Expander {Header="Main"};
        var hidden=new Expander {Header="Hidden target",Visibility=Visibility.Collapsed};
        var visible=new Expander {Header="Visible target"};
        host.Children.Add(main);host.Children.Add(hidden);host.Children.Add(visible);
        using var accordion=new WindowPanelAccordion();
        accordion.Register(main);accordion.Register(hidden);accordion.Register(visible);
        int OwnVisibleOpen() => new[]{main,hidden,visible}.Count(panel=>panel.Visibility==Visibility.Visible&&panel.IsExpanded);
        Check(!main.IsVisible&&main.Visibility==Visibility.Visible,"The fixture is unshown; own Visibility remains distinct from IsVisible.");
        accordion.Open(main);
        Check(main.IsExpanded&&OwnVisibleOpen()==1,"An own-visible bar can open before its window is shown.");
        accordion.Open(hidden);
        Check(main.IsExpanded&&!hidden.IsExpanded&&OwnVisibleOpen()==1,"An already-Collapsed target cannot replace the visible selection.");
        hidden.Visibility=Visibility.Hidden;
        accordion.Open(hidden);
        Check(main.IsExpanded&&!hidden.IsExpanded&&OwnVisibleOpen()==1,"An already-Hidden target cannot replace the visible selection.");
        main.IsExpanded=false;
        Check(main.IsExpanded&&OwnVisibleOpen()==1,"Rejected hidden targets do not corrupt the selected-bar collapse guard.");
        host.Visibility=Visibility.Collapsed;
        accordion.Open(visible);
        Check(!visible.IsVisible&&visible.Visibility==Visibility.Visible&&visible.IsExpanded&&!main.IsExpanded&&OwnVisibleOpen()==1,
            "A hidden ancestor must not block an own-visible bar or reset startup/hide selection.");
        host.Visibility=Visibility.Visible;
        Check(visible.IsExpanded&&OwnVisibleOpen()==1,"Restoring an ancestor preserves the selected bar.");
        hidden.Visibility=Visibility.Visible;
        accordion.Open(hidden);
        Check(hidden.IsExpanded&&!visible.IsExpanded&&OwnVisibleOpen()==1,"The same target becomes selectable after its own Visibility is restored.");
        hidden.Visibility=Visibility.Collapsed;
        Check(main.IsExpanded&&!hidden.IsExpanded&&OwnVisibleOpen()==1,
            "An own Visibility change restores a visible fallback even while the whole fixture is unshown.");
        main.Visibility=Visibility.Hidden;
        Check(visible.IsExpanded&&OwnVisibleOpen()==1,"Hiding the fallback selects another own-visible registered bar.");
        visible.Visibility=Visibility.Hidden;
        hidden.Visibility=Visibility.Visible;
        Check(hidden.IsExpanded&&!visible.IsExpanded&&OwnVisibleOpen()==1,"The first bar restored from an all-hidden set becomes selected.");
        visible.Visibility=Visibility.Visible;
        accordion.Unregister(hidden);
        Check(visible.IsExpanded&&!hidden.IsExpanded&&OwnVisibleOpen()==1,
            "Unregistering selection skips a hidden original fallback.");
        return checks;
    }
}
