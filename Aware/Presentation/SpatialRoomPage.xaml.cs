using System.ComponentModel;
using Aware.Spatial;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace Aware.Presentation;

public sealed partial class SpatialRoomPage : Page
{
    private SpatialRoomViewModel? _viewModel;
    private SpatialRoomView? _viewport;

    // Navigation constructs the view and assigns the resolved view model to
    // DataContext, so the page takes no constructor dependency. The assignment
    // lands *after* Loaded, so start-up hangs off DataContextChanged and Loaded
    // both, whichever arrives with a view model attached.
    public SpatialRoomPage()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => TryStart();

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        TryStart();

    private async void TryStart()
    {
        if (_viewModel is not null) return;
        if (DataContext is not SpatialRoomViewModel viewModel) return;
        if (!IsLoaded) return;

        _viewModel = viewModel;

        AttachViewport(viewModel);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        ApplyTrayState(useTransitions: false);
        ApplyLabelState(useTransitions: false);

        await viewModel.InitializeAsync();
    }

    /// <summary>
    /// The canvas is created in code so an unsupported target gets an honest
    /// message instead of a constructor throw (SKCanvasElement requires Skia
    /// rendering).
    /// </summary>
    private void AttachViewport(SpatialRoomViewModel viewModel)
    {
        if (!SpatialRoomView.IsSupported)
        {
            ViewportHost.Content = new TextBlock
            {
                Text = "The spatial model needs Skia rendering, which is not available on this target.",
                Margin = new Thickness(32),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return;
        }

        _viewport = new SpatialRoomView();
        _viewport.ObjectSelected += OnObjectSelected;

        Bind(SpatialRoomView.SnapshotProperty, nameof(SpatialRoomViewModel.Snapshot), viewModel);
        Bind(SpatialRoomView.IsTrayOpenProperty, nameof(SpatialRoomViewModel.IsTrayOpen), viewModel);
        Bind(SpatialRoomView.ReducedMotionProperty, nameof(SpatialRoomViewModel.ReducedMotion), viewModel);

        ViewportHost.Content = _viewport;
    }

    private void Bind(DependencyProperty property, string path, object source) =>
        _viewport?.SetBinding(property, new Binding
        {
            Path = new PropertyPath(path),
            Source = source,
            Mode = BindingMode.OneWay,
        });

    private void OnObjectSelected(object? sender, SpatialObjectSelectedEventArgs e) =>
        _viewModel?.SelectObject(e.ObjectId);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(SpatialRoomViewModel.IsTrayOpen)) return;

        var useTransitions = _viewModel?.ReducedMotion != true;
        ApplyTrayState(useTransitions);
        ApplyLabelState(useTransitions);
    }

    // Reduced motion drops the transition entirely rather than shortening it,
    // which is what "no stagger, no settling" means for a two-state panel.
    private void ApplyTrayState(bool useTransitions) =>
        VisualStateManager.GoToState(
            this, _viewModel?.IsTrayOpen == true ? "TrayOpen" : "TrayClosed", useTransitions);

    private void ApplyLabelState(bool useTransitions) =>
        VisualStateManager.GoToState(
            this, _viewModel?.IsTrayOpen == true ? "ObjectLabelVisible" : "ObjectLabelHidden", useTransitions);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_viewport is not null) _viewport.ObjectSelected -= OnObjectSelected;
    }
}
