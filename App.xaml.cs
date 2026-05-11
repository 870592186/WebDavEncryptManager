#nullable disable
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WebDavEncryptManager
{
    public partial class App : Application
    {
        private static Mutex _mutex = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string appName = "WebDavEncryptManager_Unique_Mutex";
            bool createdNew;

            // 1. 防止重复启动
            _mutex = new Mutex(true, appName, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("程序已经在运行中，请在系统托盘查看。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown();
                return;
            }

            base.OnStartup(e);

            // 2. 显示启动页面 (含免责声明)
            var splash = new SplashWindow();
            splash.Show();

            // 3. 后台控制流程
            Task.Run(async () =>
            {
                // 🌟 挂起任务，等待用户在 UI 上点击同意或拒绝
                bool isAccepted = await splash.WaitForAcceptanceAsync();

                // 如果用户拒绝，窗口里的逻辑已经调用了 Shutdown，这里直接 Return
                if (!isAccepted) return;

                // 用户点击了同意，开始走进度条和加载逻辑
                UpdateSplash(splash, "正在加载本地配置与密钥...", 800);

                UpdateSplash(splash, "正在检查 WebDAV 节点连接...", 1000);

                UpdateSplash(splash, "正在启动加密解密引擎(Go)...", 1200);

                // 4. 全部准备就绪，切换主窗口
                Dispatcher.Invoke(() =>
                {
                    MainWindow mainWin = new MainWindow();
                    mainWin.Show();

                    splash.Close(); // 关闭加载页
                });
            });
        }

        private void UpdateSplash(SplashWindow sw, string msg, int delay)
        {
            sw.UpdateStatus(msg);
            Thread.Sleep(delay); // 模拟加载过程
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}