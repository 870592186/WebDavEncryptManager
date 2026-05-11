#nullable disable

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WebDavEncryptManager
{
    public partial class TransferWindow : Window
    {
        private ObservableCollection<TransferTask> _globalTasks;

        public TransferWindow(ObservableCollection<TransferTask> globalTasks)
        {
            InitializeComponent();
            _globalTasks = globalTasks;

            var downloadView = new CollectionViewSource { Source = globalTasks }.View;
            downloadView.Filter = item => !((TransferTask)item).IsUpload;
            ListDownloads.ItemsSource = downloadView;

            var uploadView = new CollectionViewSource { Source = globalTasks }.View;
            uploadView.Filter = item => ((TransferTask)item).IsUpload;
            ListUploads.ItemsSource = uploadView;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }

        private MainWindow ParentWindow => this.Owner as MainWindow;

        // ==========================================
        // 🌟 智能合并的单任务操作逻辑
        // ==========================================

        // 处理下载时的 暂停/继续 点击
        private void BtnTogglePauseDown_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is TransferTask task)
            {
                // 如果目前是暂停状态，则调用继续；反之调用暂停
                if (task.IsPaused)
                    ParentWindow?.ResumeTask(task);
                else
                    ParentWindow?.PauseTask(task);
            }
        }

        // 处理上传时的 取消/重试 点击
        private void BtnToggleCancelUp_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is TransferTask task)
            {
                // 如果目前是取消或失败状态，则调用重试；反之调用取消
                if (task.IsCancelled || task.StatusText.Contains("失败") || task.StatusText.Contains("异常"))
                    ParentWindow?.RetryTask(task);
                else
                    ParentWindow?.CancelTask(task);
            }
        }

        private void BtnItemDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is TransferTask task) ParentWindow?.DeleteTask(task);
        }

        // ==========================================
        // 全局操作
        // ==========================================
        private void BtnAllPauseDown_Click(object sender, RoutedEventArgs e) => ParentWindow?.PauseAll(false);
        private void BtnAllResumeDown_Click(object sender, RoutedEventArgs e) => ParentWindow?.ResumeAll(false);
        private void BtnAllCancelUp_Click(object sender, RoutedEventArgs e) => ParentWindow?.CancelAll(true);
        private void BtnAllRetryUp_Click(object sender, RoutedEventArgs e) => ParentWindow?.RetryAll(true);

        // ==========================================
        // 清理逻辑
        // ==========================================
        private void BtnClearFailed_Click(object sender, RoutedEventArgs e) => ParentWindow?.ClearFailed();
        private void BtnClearCompleted_Click(object sender, RoutedEventArgs e) => ParentWindow?.ClearCompleted();
    }
}