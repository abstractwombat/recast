using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Recast.WindowsRecorder
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OpenControl_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = "http://localhost:5001/control", UseShellExecute = true }); } catch { }
        }

        private void OpenHls_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = Path.Combine(AppContext.BaseDirectory, "hls"), UseShellExecute = true }); } catch { }
        }

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}
