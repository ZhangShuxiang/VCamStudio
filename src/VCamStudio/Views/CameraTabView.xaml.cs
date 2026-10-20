using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VCamStudio.Models;
using VCamStudio.Services;
using VCamStudio.ViewModels;

namespace VCamStudio.Views;

public partial class CameraTabView : UserControl
{
    public CameraTabView()
    {
        InitializeComponent();
    }

    private async void CameraToggle_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CameraTabViewModel viewModel || sender is not ToggleButton toggle)
            return;

        // Derive the desired state from the view-model, not from the visual
        // toggle. The ContentControl reuses this view when switching tabs, and
        // assigning IsChecked directly would replace the OneWay binding with a
        // local value, leaving the switch stale for the next tab.
        bool desired = !viewModel.IsCameraOn;
        await viewModel.SetCameraOnAsync(desired);

        // The user may have switched tabs while the camera was starting. In that
        // case the visual now belongs to another view-model and must not be
        // overwritten with this tab's state.
        if (!ReferenceEquals(DataContext, viewModel))
            return;

        // SetCurrentValue keeps the binding intact while correcting the thumb
        // after a failed start.
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, viewModel.IsCameraOn);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not CameraTabViewModel viewModel)
            return;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;

        string? supported = files.FirstOrDefault(file => MediaTypeDetector.Detect(file) != MediaKind.None);
        viewModel.SetMediaFromPath(supported ?? files[0]);
        e.Handled = true;
    }
}
