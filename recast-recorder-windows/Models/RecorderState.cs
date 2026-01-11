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
        private int _screenWidth = 1920;
        private int _screenHeight = 1080;
        private int _framerate = 30;
        private string _preset = "ultrafast";
        private int _crf = 28;
        private int _maxrateKbps = 2000;
        private int _bufsizeKbps = 4000;
        private int _bitrateKbps = 192;
        private int _sampleRate = 48000;
        private int _channels = 2;
        private bool _isRecording;
        private DateTimeOffset? _recordingStartTime;
        private int? _testJobId;

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

        public int ScreenWidth
        {
            get => _screenWidth;
            set { if (_screenWidth != value) { _screenWidth = value; OnPropertyChanged(); } }
        }
        public int ScreenHeight
        {
            get => _screenHeight;
            set { if (_screenHeight != value) { _screenHeight = value; OnPropertyChanged(); } }
        }
        public int Framerate
        {
            get => _framerate;
            set { if (_framerate != value) { _framerate = value; OnPropertyChanged(); } }
        }
        public string Preset
        {
            get => _preset;
            set { if (_preset != value) { _preset = value; OnPropertyChanged(); } }
        }
        public int Crf
        {
            get => _crf;
            set { if (_crf != value) { _crf = value; OnPropertyChanged(); } }
        }
        public int MaxrateKbps
        {
            get => _maxrateKbps;
            set { if (_maxrateKbps != value) { _maxrateKbps = value; OnPropertyChanged(); } }
        }
        public int BufsizeKbps
        {
            get => _bufsizeKbps;
            set { if (_bufsizeKbps != value) { _bufsizeKbps = value; OnPropertyChanged(); } }
        }
        public int BitrateKbps
        {
            get => _bitrateKbps;
            set { if (_bitrateKbps != value) { _bitrateKbps = value; OnPropertyChanged(); } }
        }
        public int SampleRate
        {
            get => _sampleRate;
            set { if (_sampleRate != value) { _sampleRate = value; OnPropertyChanged(); } }
        }
        public int Channels
        {
            get => _channels;
            set { if (_channels != value) { _channels = value; OnPropertyChanged(); } }
        }
        public bool IsRecording
        {
            get => _isRecording;
            set { if (_isRecording != value) { _isRecording = value; OnPropertyChanged(); } }
        }
        public int? TestJobId
        {
            get => _testJobId;
            set { if (_testJobId != value) { _testJobId = value; OnPropertyChanged(); } }
        }
        public int RecordingElapsedSeconds
        {
            get
            {
                if (!_isRecording || !_recordingStartTime.HasValue)
                    return 0;
                return (int)(DateTimeOffset.UtcNow - _recordingStartTime.Value).TotalSeconds;
            }
        }

        public void StartRecording()
        {
            _recordingStartTime = DateTimeOffset.UtcNow;
            IsRecording = true;
        }

        public int StopRecording()
        {
            var elapsed = RecordingElapsedSeconds;
            IsRecording = false;
            _recordingStartTime = null;
            return elapsed;
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
