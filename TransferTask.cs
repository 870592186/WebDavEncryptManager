#nullable disable

using System.ComponentModel;

namespace WebDavEncryptManager
{
    // ==========================================
    // 扩展的 TransferTask 实体类 (提供 MVVM 绑定)
    // ==========================================
    public class TransferTask : INotifyPropertyChanged
    {
        public string TaskId { get; set; }
        public string FileName { get; set; }
        public bool IsUpload { get; set; }

        // 🌟 记录物理路径，方便重试和续传
        public string LocalPath { get; set; }
        public string RemotePath { get; set; }
        public string TargetRemoteDir { get; set; }
        public long RemoteSize { get; set; }

        private double _percentage;
        public double Percentage { get => _percentage; set { _percentage = value; OnPropertyChanged(nameof(Percentage)); } }

        private string _statusText;
        public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(nameof(StatusText)); } }

        private bool _isCompleted;
        public bool IsCompleted { get => _isCompleted; set { _isCompleted = value; OnPropertyChanged(nameof(IsCompleted)); } }

        private bool _isPaused;
        public bool IsPaused { get => _isPaused; set { _isPaused = value; OnPropertyChanged(nameof(IsPaused)); } }

        private bool _isCancelled;
        public bool IsCancelled { get => _isCancelled; set { _isCancelled = value; OnPropertyChanged(nameof(IsCancelled)); } }

        private bool _isFileMissing;
        public bool IsFileMissing { get => _isFileMissing; set { _isFileMissing = value; OnPropertyChanged(nameof(IsFileMissing)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}