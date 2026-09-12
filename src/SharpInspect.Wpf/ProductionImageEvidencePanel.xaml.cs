using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class ProductionImageEvidencePanel : UserControl
{
    public ProductionImageEvidencePanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => Deactivate();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is ProductionImageEvidenceViewModel old) old.Deactivate();
        };
    }
    public void Deactivate()
    {
        Dispatcher.VerifyAccess();
        (DataContext as ProductionImageEvidenceViewModel)?.Deactivate();
    }
}
