#nullable disable
using System.Threading.Tasks;
using System.Windows;

namespace WebDavEncryptManager
{
    public partial class SplashWindow : Window
    {
        // 🌟 核心：用于异步等待用户的点击结果
        private TaskCompletionSource<bool> _tcs = new TaskCompletionSource<bool>();

        public SplashWindow()
        {
            InitializeComponent();
        }

        // 提供给 App.xaml.cs 调用的等待方法
        public Task<bool> WaitForAcceptanceAsync()
        {
            return _tcs.Task;
        }

        // 点击同意
        private void BtnAccept_Click(object sender, RoutedEventArgs e)
        {
            // 隐藏按钮，显示进度条
            ActionPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Visible;
            TxtStatus.Text = "准备就绪...";

            // 通知后台任务继续执行
            _tcs.TrySetResult(true);
        }

        // 点击拒绝
        private void BtnReject_Click(object sender, RoutedEventArgs e)
        {
            _tcs.TrySetResult(false);
            Application.Current.Shutdown(); // 拒绝直接关闭软件
        }

        // 外部更新加载文字
        public void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() => {
                TxtStatus.Text = message;
            });
        }
    }
}