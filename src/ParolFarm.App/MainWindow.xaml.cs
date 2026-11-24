using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ParolFarm.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        foreach (var b in FindButtons(CartJog).Concat(FindButtons(JointJog)))
        {
            b.PreviewMouseLeftButtonDown += (_, _) => _vm.HeldJogButton = b.Tag as string;
            b.PreviewMouseLeftButtonUp += (_, _) => _vm.ReleaseJogButton();
            b.LostMouseCapture += (_, _) => _vm.ReleaseJogButton();
        }

        HoldMoveJ.PreviewMouseLeftButtonDown += (_, _) => _vm.StartMoveToSelected(false);
        HoldMoveL.PreviewMouseLeftButtonDown += (_, _) => _vm.StartMoveToSelected(true);
        foreach (var b in new[] { HoldMoveJ, HoldMoveL })
        {
            b.PreviewMouseLeftButtonUp += (_, _) => _vm.StopMoveToSelected();
            b.LostMouseCapture += (_, _) => _vm.StopMoveToSelected();
        }

        SourceInitialized += (_, _) => _vm.AttachSpaceMouse(this);
        Activated += (_, _) => _vm.SetWindowActive(true);
        Deactivated += (_, _) => _vm.SetWindowActive(false);
        Closed += (_, _) => _vm.Dispose();
    }

    static IEnumerable<Button> FindButtons(DependencyObject parent)
    {
        if (parent is Panel p)
            return p.Children.OfType<Button>();
        var list = new List<Button>();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            list.AddRange(FindButtons(VisualTreeHelper.GetChild(parent, i)));
        return list;
    }
}
