using System.Windows;
using System.Diagnostics;
using System.Windows.Documents;
using System.Windows.Navigation;
using Optimus.Application;

namespace Optimus.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    public static bool IsOfficialFirmwareNavigation(FirmwareSupportReference reference, Uri target)
    {
        string? official = FirmwareSupportCatalog.OfficialUrl(reference.Manufacturer);
        return official is not null && string.Equals(reference.Url, official, StringComparison.Ordinal)
            && target.IsAbsoluteUri && target.Scheme == Uri.UriSchemeHttps
            && string.Equals(target.OriginalString, official, StringComparison.Ordinal);
    }

    private void FirmwareRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (sender is not Hyperlink { DataContext: FirmwareSupportReference reference }
            || !IsOfficialFirmwareNavigation(reference, e.Uri)) return;
        try { Process.Start(new ProcessStartInfo(reference.Url) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        { MessageBox.Show(error.Message, "opTImus · Não foi possível abrir o suporte oficial", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainViewModel { IsBusy: true } viewModel)
        {
            viewModel.RequestCancellation();
            e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
