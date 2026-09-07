using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SharpInspect.Wpf;

public partial class AlgorithmResultPanel : UserControl
{
    private Point? _dragStart;
    private double _panStartX;
    private double _panStartY;
    private AlgorithmResultHistoryViewModel? _observed;

    public AlgorithmResultPanel()
    {
        InitializeComponent();
        DataContextChanged += ContextChanged;
        Loaded += (_, _) => Observe(DataContext as AlgorithmResultHistoryViewModel);
        Unloaded += (_, _) => { Observe(null); _dragStart = null; Viewer.ReleaseMouseCapture(); };
    }

    public FrameOverlayPresenter Presenter => Viewer;
    public FramePreviewImage? SourceImage { get => Viewer.SourceImage; set => Viewer.SourceImage = value; }

    private void ContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        Observe(IsLoaded ? args.NewValue as AlgorithmResultHistoryViewModel : null);
        Viewer.SourceImage = null;
    }

    private void Observe(AlgorithmResultHistoryViewModel? current)
    {
        if (ReferenceEquals(current, _observed)) return;
        if (_observed is not null) _observed.PropertyChanged -= SelectionChanged;
        _observed = current;
        if (_observed is not null) _observed.PropertyChanged += SelectionChanged;
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AlgorithmResultHistoryViewModel.SelectedRecord)) return;
        Viewer.SourceImage = null;
        Viewer.PanX = 0; Viewer.PanY = 0;
    }

    private void FitView(object sender, RoutedEventArgs args)
    {
        if (Viewer.Snapshot is not { } snapshot) return;
        Viewer.Zoom = Math.Clamp(Math.Min(Viewer.ActualWidth / snapshot.FrameMetadata.Width,
            Viewer.ActualHeight / snapshot.FrameMetadata.Height), 0.1, 8);
        Viewer.PanX = 0; Viewer.PanY = 0;
    }

    private void StartPan(object sender, MouseButtonEventArgs args)
    {
        if (Viewer.Snapshot is null) return;
        _dragStart = args.GetPosition(Viewer);
        _panStartX = Viewer.PanX; _panStartY = Viewer.PanY;
        Viewer.CaptureMouse(); args.Handled = true;
    }

    private void MovePan(object sender, MouseEventArgs args)
    {
        if (_dragStart is not { } start || args.LeftButton != MouseButtonState.Pressed) return;
        var current = args.GetPosition(Viewer);
        Viewer.PanX = _panStartX + current.X - start.X;
        Viewer.PanY = _panStartY + current.Y - start.Y;
    }

    private void EndPan(object sender, MouseButtonEventArgs args)
    { _dragStart = null; Viewer.ReleaseMouseCapture(); args.Handled = true; }
}
