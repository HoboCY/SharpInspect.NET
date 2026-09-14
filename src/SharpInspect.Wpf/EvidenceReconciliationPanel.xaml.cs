using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class EvidenceReconciliationPanel : UserControl
{
    public EvidenceReconciliationPanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => Deactivate();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is EvidenceReconciliationViewModel old) old.Deactivate();
        };
    }
    public void Deactivate()
    {
        Dispatcher.VerifyAccess();
        (DataContext as EvidenceReconciliationViewModel)?.Deactivate();
    }
}
