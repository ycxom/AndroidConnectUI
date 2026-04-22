using System.Windows;
using System.Windows.Controls;

namespace AndroidConnectUI
{
    public partial class ResolutionInputDialog : Window
    {
        public string Resolution => txtResolution.Text;

        public ResolutionInputDialog()
        {
            InitializeComponent();
        }

        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtResolution.Text))
            {
                MessageBox.Show("请输入分辨率", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
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
    }
}
