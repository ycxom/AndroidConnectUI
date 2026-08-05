using System.Diagnostics;
using System.Windows;

namespace AndroidConnectUI
{
    public partial class SettingsWindow : Window
    {
        private readonly string _adbPath;
        private readonly string _deviceSerial;

        public SettingsWindow(string adbPath, string deviceSerial)
        {
            _adbPath = adbPath;
            _deviceSerial = deviceSerial;
            InitializeComponent();
            txtDeviceSerial.Text = deviceSerial;
            toggleWindowAnimations.IsChecked = AppPreferences.WindowAnimationsEnabled;
            Loaded += SettingsWindow_Loaded;
        }

        private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= SettingsWindow_Loaded;
            btnApply.IsEnabled = false;
            var (output, error) = await RunAdbAsync("shell settings get global stay_on_while_plugged_in");
            if (int.TryParse(output.Trim(), out int value))
            {
                rbDefaultSleep.IsChecked = value == 0;
                rbUsbNoSleep.IsChecked = value == 2;
                rbPowerNoSleep.IsChecked = value != 0 && value != 2;
                txtLoadStatus.Text = "已读取设备当前设置";
            }
            else
            {
                rbDefaultSleep.IsChecked = true;
                txtLoadStatus.Text = string.IsNullOrWhiteSpace(error) ? "无法识别当前设置" : error.Trim();
            }
            btnApply.IsEnabled = true;
        }

        private async void btnApply_Click(object sender, RoutedEventArgs e)
        {
            btnApply.IsEnabled = false;
            try
            {
                string stayOnValue = rbUsbNoSleep.IsChecked == true
                    ? "2"
                    : rbPowerNoSleep.IsChecked == true ? "7" : "0";

                var (_, error) = await RunAdbAsync(
                    $"shell settings put global stay_on_while_plugged_in {stayOnValue}");
                if (!string.IsNullOrWhiteSpace(error) && !error.Contains("Warning", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"设置失败：{error}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                AppPreferences.WindowAnimationsEnabled = toggleWindowAnimations.IsChecked == true;

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"设置失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnApply.IsEnabled = true;
            }
        }

        private async Task<(string output, string error)> RunAdbAsync(string arguments)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var process = new Process();
                    process.StartInfo.FileName = _adbPath;
                    process.StartInfo.Arguments = $"-s \"{_deviceSerial}\" {arguments}";
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();

                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch { }
                        return ("", "ADB 操作超时");
                    }
                    return (process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
                }
                catch (Exception ex)
                {
                    return ("", ex.Message);
                }
            });
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
