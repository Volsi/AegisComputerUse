using System.Windows;
using System.Windows.Interop;
using AegisComputerUse.Core.Gemini;
using AegisComputerUse.Core.Native;

namespace AegisComputerUse.App;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog(ConfirmationRequest req)
    {
        InitializeComponent();
        ActionSummaryText.Text = $"Действие: {req.ActionSummary}";
        ReasonText.Text = req.Reason;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                Win32Interop.EnableModernDarkFrame(hwnd);
                Win32Interop.SetWindowDisplayAffinity(hwnd, Win32Interop.WDA_EXCLUDEFROMCAPTURE);
            }
        };
    }

    private void OnApproveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnDenyClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
