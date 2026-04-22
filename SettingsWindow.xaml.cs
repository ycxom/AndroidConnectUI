using System.Diagnostics;
using System.Windows;

namespace AndroidConnectUI
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
        }

        private async void btnApply_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string stayOnValue;
                if (rbUsbNoSleep.IsChecked == true)
                {
                    stayOnValue = "2";
                }
                else if (rbPowerNoSleep.IsChecked == true)
                {
                    stayOnValue = "1";
                }
                else
                {
                    stayOnValue = "0";
                }

                var process = new Process();
                process.StartInfo.FileName = "adb";
                process.StartInfo.Arguments = $"shell settings put global stay_on_while_plugged_in {stayOnValue}";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                if (!string.IsNullOrEmpty(error) && !error.Contains("Warning"))
                {
                    MessageBox.Show($"设置失败: {error}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                MessageBox.Show("设置已应用", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"设置失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
