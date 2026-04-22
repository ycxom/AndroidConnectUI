using System.Windows;
using System.Windows.Controls;

namespace AndroidConnectUI
{
    public partial class ResolutionInputDialog : Window
    {
        public string Resolution => txtResolution.Text;
        public string Density => txtDensity.Text;

        public ResolutionInputDialog()
        {
            InitializeComponent();
        }

        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtResolution.Text) && string.IsNullOrWhiteSpace(txtDensity.Text))
            {
                MessageBox.Show("请输入分辨率或 DPI 值", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(txtResolution.Text))
            {
                var parts = txtResolution.Text.Split('x');
                if (parts.Length != 2 || !int.TryParse(parts[0], out _) || !int.TryParse(parts[1], out _))
                {
                    MessageBox.Show("分辨率格式不正确，例如: 1080x1920", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(txtDensity.Text))
            {
                if (!int.TryParse(txtDensity.Text, out int density) || density < 100 || density > 1000)
                {
                    MessageBox.Show("DPI 值无效，请输入 100-1000 之间的整数", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            DialogResult = true;
            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void PresetResolution_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string resolution)
            {
                txtResolution.Text = resolution;
            }
        }

        private void PresetDensity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string density)
            {
                txtDensity.Text = density;
            }
        }
    }
}
