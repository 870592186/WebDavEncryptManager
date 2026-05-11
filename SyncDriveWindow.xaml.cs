#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

// ⚠️ 注意：这里绝对不能写 using System.Windows.Forms; ！！！

namespace WebDavEncryptManager
{
    // 同步专属的数据模型
    public class SyncItem : INotifyPropertyChanged
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string SizeText { get; set; }

        private double _percentage;
        public double Percentage { get => _percentage; set { _percentage = value; OnPropertyChanged(nameof(Percentage)); } }

        private string _stateText = "等待同步...";
        public string StateText { get => _stateText; set { _stateText = value; OnPropertyChanged(nameof(StateText)); } }

        private bool _isPaused;
        public bool IsPaused { get => _isPaused; set { _isPaused = value; OnPropertyChanged(nameof(IsPaused)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class SyncDriveWindow : Window
    {
        // 🌟 使用绝对路径调用 WinForms 组件，完美避开冲突
        private System.Windows.Forms.NotifyIcon _trayIcon;

        private bool _isGlobalPaused = false;
        private string _localSyncPath = "";
        private string _remoteSyncPath = "";

        private FileSystemWatcher _watcher;
        private System.Windows.Threading.DispatcherTimer _debounceTimer;

        // 记录文件的最后修改时间，用于 3 秒防抖
        private ConcurrentDictionary<string, DateTime> _pendingFiles = new ConcurrentDictionary<string, DateTime>();

        private ObservableCollection<SyncItem> SyncFiles = new ObservableCollection<SyncItem>();

        public SyncDriveWindow()
        {
            InitializeComponent();
            ListLocalSync.ItemsSource = SyncFiles;

            InitSystemTray();
            InitDebounceTimer();
        }

        // ==========================================
        // 🌟 1. 系统托盘逻辑 (System Tray)
        // ==========================================
        private void InitSystemTray()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon();
            _trayIcon.Icon = System.Drawing.SystemIcons.Shield; // 系统盾牌图标
            _trayIcon.Text = "WebDAV 同步盘 - 运行中";
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += (s, e) => ShowSyncWindow();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("打开同步盘", null, (s, e) => ShowSyncWindow()));
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("打开主界面", null, (s, e) => { ShowSyncWindow(); BtnReturnMain_Click(null, null); }));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var pauseMenu = new System.Windows.Forms.ToolStripMenuItem("暂停全部同步", null, (s, e) => ToggleGlobalSync((System.Windows.Forms.ToolStripMenuItem)s));
            menu.Items.Add(pauseMenu);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("退出软件", null, (s, e) => Application.Current.Shutdown()));

            _trayIcon.ContextMenuStrip = menu;
        }

        private void ShowSyncWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ToggleGlobalSync(System.Windows.Forms.ToolStripMenuItem menuItem)
        {
            _isGlobalPaused = !_isGlobalPaused;
            menuItem.Text = _isGlobalPaused ? "继续全部同步" : "暂停全部同步";
            TxtSyncStatus.Text = _isGlobalPaused ? "⏸ 全局同步已暂停" : "▶ 正在监听本地变化...";
        }

        // 拦截关闭按钮，使其最小化到托盘
        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
            _trayIcon.ShowBalloonTip(2000, "同步盘", "已最小化至系统托盘，后台持续为您同步。", System.Windows.Forms.ToolTipIcon.Info);
        }

        private void BtnMinimizeTray_Click(object sender, RoutedEventArgs e) => this.Hide();

        private void BtnReturnMain_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            if (this.Owner != null)
            {
                this.Owner.Show();
                this.Owner.Activate();
            }
        }

        // ==========================================
        // 🌟 2. 三秒防抖监听逻辑 (Debounce Watcher)
        // ==========================================
        private void InitDebounceTimer()
        {
            // 定时器每 1 秒巡检一次队列
            _debounceTimer = new System.Windows.Threading.DispatcherTimer();
            _debounceTimer.Interval = TimeSpan.FromSeconds(1);
            _debounceTimer.Tick += (s, e) =>
            {
                if (_isGlobalPaused) return;

                var now = DateTime.Now;
                foreach (var kvp in _pendingFiles.ToList())
                {
                    // 如果该文件 3 秒内没有再被修改过，说明用户/软件保存完毕，开始触发上传！
                    if ((now - kvp.Value).TotalSeconds >= 3)
                    {
                        string targetFile = kvp.Key;
                        _pendingFiles.TryRemove(targetFile, out _); // 从队列移除

                        Application.Current.Dispatcher.Invoke(() => StartUploadSync(targetFile));
                    }
                }
            };
            _debounceTimer.Start();
        }

        private void StartFolderWatcher()
        {
            if (string.IsNullOrEmpty(_localSyncPath) || !Directory.Exists(_localSyncPath)) return;

            if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); }

            _watcher = new FileSystemWatcher(_localSyncPath);
            _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
            _watcher.IncludeSubdirectories = false; // 目前仅支持单层监听

            FileSystemEventHandler handler = (s, e) =>
            {
                // 只要文件发生变动，就更新/加入队列的最后修改时间
                _pendingFiles.AddOrUpdate(e.FullPath, DateTime.Now, (key, oldValue) => DateTime.Now);

                // UI 上增加未同步的红底条目
                Application.Current.Dispatcher.Invoke(() => {
                    if (!SyncFiles.Any(f => f.FilePath == e.FullPath))
                    {
                        var info = new FileInfo(e.FullPath);
                        SyncFiles.Add(new SyncItem { FilePath = e.FullPath, FileName = info.Name, SizeText = (info.Length / 1024) + " KB", StateText = "检测到修改..." });
                    }
                    else
                    {
                        var item = SyncFiles.First(f => f.FilePath == e.FullPath);
                        item.StateText = "检测到修改...";
                        item.Percentage = 0;
                    }
                });
            };

            _watcher.Changed += handler;
            _watcher.Created += handler;
            _watcher.EnableRaisingEvents = true;

            TxtSyncStatus.Text = "▶ 正在监听本地变化...";
        }

        // ==========================================
        // 🌟 3. 业务入口与模拟同步
        // ==========================================
        private void BtnSelectLocal_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _localSyncPath = dialog.SelectedPath;
                TxtLocalPath.Text = _localSyncPath;

                // 首次选中，加载本地已有文件列表（默认标为已同步）
                SyncFiles.Clear();
                foreach (var file in Directory.GetFiles(_localSyncPath))
                {
                    var info = new FileInfo(file);
                    SyncFiles.Add(new SyncItem { FilePath = file, FileName = info.Name, SizeText = (info.Length / 1024) + " KB", StateText = "✅ 已同步", Percentage = 100 });
                }

                StartFolderWatcher();
            }
        }

        private void BtnSelectRemote_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "⚠️ 安全警告\n\n" +
                "同步盘仅会自动上传本地【新增】或【修改】的文件。\n" +
                "请务必使用与云端目录完全一致的【主密码】进行加密！\n" +
                "切勿混合不同密码上传至同一云端文件夹，否则易造成数据损坏或解密失败。\n\n" +
                "请妥善保管您的主密码！",
                "配置确认", MessageBoxButton.OK, MessageBoxImage.Warning);

            _remoteSyncPath = "/SyncFolder";
            MessageBox.Show($"已绑定云端目录: {_remoteSyncPath}");
        }

        private void BtnToggleSync_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is SyncItem item)
            {
                if (item.StateText == "❌ 失败")
                {
                    StartUploadSync(item.FilePath); // 重试
                }
                else
                {
                    item.IsPaused = !item.IsPaused;
                    item.StateText = item.IsPaused ? "⏸ 已暂停" : "正在恢复...";
                }
            }
        }

        // 核心上传触发点（对接你的 Go 引擎）
        private async void StartUploadSync(string filePath)
        {
            var item = SyncFiles.FirstOrDefault(f => f.FilePath == filePath);
            if (item == null || item.IsPaused) return;

            item.StateText = "⬆️ 正在同步...";

            // UI 效果模拟
            for (int i = 0; i <= 100; i += 10)
            {
                if (item.IsPaused) return; // 检查暂停
                item.Percentage = i;
                await Task.Delay(200);
            }
            item.StateText = "✅ 已同步";
        }

        // 右侧云端右键菜单 (可直接调用 MainWindow 已有的 HandleRemoteItem 逻辑)
        private void MenuRemoteOpen_Click(object sender, RoutedEventArgs e) { }
        private void MenuRemoteRename_Click(object sender, RoutedEventArgs e) { }
        private void MenuRemoteDelete_Click(object sender, RoutedEventArgs e) { }
        private void ListRemoteSync_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { }
    }
}