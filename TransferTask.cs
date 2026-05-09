using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WebDavEncryptManager
{
    // 实现 INotifyPropertyChanged，当属性改变时自动通知界面更新进度条
    public class TransferTask : INotifyPropertyChanged
    {
        public string TaskId { get; set; }
        public string FileName { get; set; }
        public bool IsUpload { get; set; } // true 为上传，false 为下载

        private double _percentage;
        public double Percentage
        {
            get => _percentage;
            set { _percentage = value; OnPropertyChanged(); }
        }

        private string _statusText = "排队中...";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        // 后续扩展：控制任务暂停、取消的标志位
        public bool IsPaused { get; set; }
        public bool IsCompleted { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}