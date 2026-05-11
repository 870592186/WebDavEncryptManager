#nullable disable
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WebDavEncryptManager
{
    public partial class App : Application
    {
        // 🌟 定义一个唯一的 Mutex 名称，用于识别程序是否已经在运行
        private static Mutex _mutex = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string appName = "WebDavEncryptManager_Global_Mutex";
            bool createdNew;

            // 🌟 1. 尝试创建一个互斥锁
            _mutex = new Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                // 🌟 如果锁已经存在，说明软件正在运行
                MessageBox.Show("软件已经在运行中，请在系统托盘查看。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown(); // 退出当前实例
                return;
            }

            base.OnStartup(e);

            // 🌟 2. 显示启动页面
            var splash = new SplashWindow();
            splash.Show();

            // 🌟 3. 在后台执行初始化任务（模拟加载，避免主界面卡顿）
            Task.Run(async () =>
            {
                splash.UpdateStatus("正在加载配置...");
                await Task.Delay(800); // 给用户一点反应时间，也确保硬件资源就绪

                splash.UpdateStatus("正在解压并启动 Go 引擎...");
                // 这里的引擎启动逻辑实际上在 MainWindow 构造函数里，我们直接初始化主窗体即可

                // 🌟 4. 初始化完成后，在主线程切换到主窗口
                Dispatcher.Invoke(() =>
                {
                    MainWindow mainWin = new MainWindow();
                    splash.Close(); // 关闭加载页
                    mainWin.Show(); // 显示主界面
                });
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 🌟 释放 Mutex
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}