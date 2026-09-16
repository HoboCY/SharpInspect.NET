using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class DiagnosticsPanel : UserControl
{
    public DiagnosticsPanel()
    {
        InitializeComponent(); Unloaded += (_, _) => (DataContext as DiagnosticsViewModel)?.Deactivate();
        DataContextChanged += (_, args) => (args.OldValue as DiagnosticsViewModel)?.Deactivate();
    }
}
