using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace AndroidConnectUI;

public partial class WirelessAdbDialog : Window
{
    public int Port { get; private set; }

    public WirelessAdbDialog(string deviceDisplayName, int defaultPort)
    {
        InitializeComponent();
        txtDeviceSerial.Text = deviceDisplayName;
        txtPort.Text = defaultPort.ToString();
        Loaded += (_, _) =>
        {
            txtPort.Focus();
            txtPort.SelectAll();
        };
    }

    private void btnApply_Click(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        if (!int.TryParse(txtPort.Text.Trim(), out int port) || port is < 1024 or > 65535)
        {
            MessageBox.Show(
                "请输入 1024 到 65535 之间的有效 TCP 端口。",
                "端口无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            txtPort.Focus();
            txtPort.SelectAll();
            return;
        }

        Port = port;
        DialogResult = true;
    }

    private void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void txtPort_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
    }

    private void txtPort_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Apply();
        }
    }
}
