using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using VCamStudio.ViewModels;

namespace VCamStudio;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _closingInProgress;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        if (_closingInProgress || DataContext is not MainViewModel viewModel)
            return;

        _closingInProgress = true;
        try
        {
            if (!await viewModel.PrepareForShutdownAsync())
                return;

            viewModel.Shutdown();
            _allowClose = true;

            // The Closing event is still on the stack. Closing the window again
            // synchronously throws, so let the event return first.
            _ = Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()), DispatcherPriority.Background);
        }
        finally
        {
            _closingInProgress = false;
        }
    }
}
