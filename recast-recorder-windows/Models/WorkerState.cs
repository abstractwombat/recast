using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Recast.WindowsRecorder.Models
{
    public class RecorderState : INotifyPropertyChanged
    {
        private string _managementUrl = "";
        private string _recorderId = "";
        private string _hostname = Environment.MachineName;
        private bool _managementConnected;
        private string _status = "IDLE";
        private int? _currentJobId;
        private DateTimeOffset? _lastHeartbeat;

        public string ManagementUrl
        {
            get => _managementUrl;
            set { if (_managementUrl != value) { _managementUrl = value; OnPropertyChanged(); } }
        }
        public string RecorderId
        {
            get => _recorderId;
            set { if (_recorderId != value) { _recorderId = value; OnPropertyChanged(); } }
        }
        public string Hostname
        {
            get => _hostname;
            set { if (_hostname != value) { _hostname = value; OnPropertyChanged(); } }
        }
        public bool ManagementConnected
        {
            get => _managementConnected;
            set { if (_managementConnected != value) { _managementConnected = value; OnPropertyChanged(); } }
        }
        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(); } }
        }
        public int? CurrentJobId
        {
            get => _currentJobId;
            set { if (_currentJobId != value) { _currentJobId = value; OnPropertyChanged(); } }
        }
        public DateTimeOffset? LastHeartbeat
        {
            get => _lastHeartbeat;
            set { if (_lastHeartbeat != value) { _lastHeartbeat = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            var handler = PropertyChanged;
            if (handler == null) return;

            var args = new PropertyChangedEventArgs(name);
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.Invoke(() => handler(this, args));
            }
            else
            {
                handler(this, args);
            }
        }
    }
}
