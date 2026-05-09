using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using System.ComponentModel;

namespace WebDavEncryptManager
{
    public partial class TransferWindow : Window
    {
        public TransferWindow(ObservableCollection<TransferTask> globalTasks)
        {
            InitializeComponent();

            // 利用 CollectionViewSource 将全局任务分类显示
            var downloadView = new CollectionViewSource { Source = globalTasks }.View;
            downloadView.Filter = item => !((TransferTask)item).IsUpload;
            ListDownloads.ItemsSource = downloadView;

            var uploadView = new CollectionViewSource { Source = globalTasks }.View;
            uploadView.Filter = item => ((TransferTask)item).IsUpload;
            ListUploads.ItemsSource = uploadView;
        }

        // 窗口关闭时只是隐藏，不销毁，保持后台进度更新
        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }
    }
}