using System.Windows;

namespace Optimus.Desktop;

public interface IUserConfirmation
{
    bool Confirm(string title, string message);
}

internal sealed class WindowConfirmation : IUserConfirmation
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
