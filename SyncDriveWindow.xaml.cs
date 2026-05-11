#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WebDavEncryptManager
{
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
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private bool _isGlobalPaused = false;
        private string _localSyncPath = "";
        private string _remoteSyncPath = "";
        private FileSystemWatcher _watcher;
        private System.Windows.Threading.DispatcherTimer _debounceTimer;

        private ConcurrentDictionary<string, DateTime> _pendingFiles = new ConcurrentDictionary<string, DateTime>();
        private ObservableCollection<SyncItem> SyncFiles = new ObservableCollection<SyncItem>();

        // 🌟 用于控制真实同步的取消令牌
        private ConcurrentDictionary<string, CancellationTokenSource> _syncTokens = new ConcurrentDictionary<string, CancellationTokenSource>();

        public SyncDriveWindow()
        {
            InitializeComponent();
            ListLocalSync.ItemsSource = SyncFiles;

            InitSystemTray();
            InitDebounceTimer();
        }

        private void InitSystemTray()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon();
            _trayIcon.Icon = System.Drawing.SystemIcons.Shield;
            _trayIcon.Text = "WebDAV 同步盘 - 运行中";
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += (s, e) => ShowSyncWindow();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("打开同步盘", null, (s, e) => ShowSyncWindow()));
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("打开主界面", null, (s, e) => {
                this.Hide();
                var mainWin = this.Owner as MainWindow;
                if (mainWin != null)
                {
                    mainWin.Show();
                    mainWin.WindowState = WindowState.Normal;
                    mainWin.Activate();
                }
            }));
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

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
            var mainWin = this.Owner as MainWindow;
            mainWin?.Hide(); // 连带主窗口一起隐藏
            _trayIcon.ShowBalloonTip(2000, "同步盘", "已最小化至系统托盘，后台持续为您同步。", System.Windows.Forms.ToolTipIcon.Info);
        }

        private void BtnMinimizeTray_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            var mainWin = this.Owner as MainWindow;
            mainWin?.Hide(); // 连带主窗口一起隐藏
            _trayIcon.ShowBalloonTip(2000, "同步盘", "已最小化至系统托盘，后台持续为您同步。", System.Windows.Forms.ToolTipIcon.Info);
        }

        private void BtnReturnMain_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            if (this.Owner != null)
            {
                this.Owner.Show();
                this.Owner.Activate();
            }
        }

        private void InitDebounceTimer()
        {
            _debounceTimer = new System.Windows.Threading.DispatcherTimer();
            _debounceTimer.Interval = TimeSpan.FromSeconds(1);
            _debounceTimer.Tick += (s, e) =>
            {
                if (_isGlobalPaused) return;

                var now = DateTime.Now;
                foreach (var kvp in _pendingFiles.ToList())
                {
                    if ((now - kvp.Value).TotalSeconds >= 3)
                    {
                        string targetFile = kvp.Key;
                        _pendingFiles.TryRemove(targetFile, out _);
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
            _watcher.IncludeSubdirectories = false;

            FileSystemEventHandler handler = (s, e) =>
            {
                _pendingFiles.AddOrUpdate(e.FullPath, DateTime.Now, (key, oldValue) => DateTime.Now);

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

        private void BtnSelectLocal_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _localSyncPath = dialog.SelectedPath;
                TxtLocalPath.Text = _localSyncPath;

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
            var mainWin = this.Owner as MainWindow;
            if (mainWin == null) return;

            MessageBox.Show(
                "⚠️ 安全警告\n\n" +
                "同步盘仅会自动上传本地【新增】或【修改】的文件。\n" +
                "请务必使用与云端目录完全一致的【主密码】进行加密！\n" +
                "切勿混合不同密码上传至同一云端文件夹，否则易造成数据损坏或解密失败。\n\n" +
                "请妥善保管您的主密码！",
                "配置确认", MessageBoxButton.OK, MessageBoxImage.Warning);

            var folderWin = new RemoteFolderSelectWindow(mainWin.SyncConfig, "/");
            folderWin.Owner = this;
            if (folderWin.ShowDialog() == true)
            {
                _remoteSyncPath = folderWin.SelectedPath;
                MessageBox.Show($"已绑定云端目录: {_remoteSyncPath}");
            }
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
                    if (item.IsPaused)
                    {
                        item.StateText = "⏸ 已暂停";
                        if (_syncTokens.TryGetValue(item.FilePath, out var cts))
                        {
                            cts.Cancel();
                            _syncTokens.TryRemove(item.FilePath, out _);
                        }
                    }
                    else
                    {
                        item.StateText = "正在恢复...";
                        StartUploadSync(item.FilePath);
                    }
                }
            }
        }

        // 🌟 完美集成真实底层同步逻辑
        private async void StartUploadSync(string filePath)
        {
            var item = SyncFiles.FirstOrDefault(f => f.FilePath == filePath);
            if (item == null || item.IsPaused) return;

            var mainWin = this.Owner as MainWindow;
            if (mainWin == null || string.IsNullOrEmpty(_remoteSyncPath)) return;

            item.StateText = "⬆️ 正在同步...";

            bool useEncryption = ChkEncrypt.IsChecked == true;
            string customKey = useEncryption ? mainWin.SyncCustomKey : "";

            // 🌟 如果勾选了加密但主界面没密码，拦截报错
            if (useEncryption && string.IsNullOrEmpty(customKey))
            {
                item.StateText = "❌ 失败";
                MessageBox.Show("您勾选了加密同步，但未在主界面填写【信封密钥】！\n请返回主界面填写密钥，或取消勾选加密同步。", "密钥缺失");
                return;
            }

            try
            {
                string fileName = Path.GetFileName(filePath);
                string targetRemotePath = _remoteSyncPath.TrimEnd('/') + "/" + fileName;

                var payload = new
                {
                    taskId = Guid.NewGuid().ToString(),
                    localPath = filePath,
                    remotePath = targetRemotePath,
                    webdavUrl = mainWin.SyncConfig.WebDavUrl,
                    username = mainWin.SyncConfig.Username,
                    password = mainWin.SyncConfig.Password,
                    customKey = customKey
                };

                var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

                var cts = new CancellationTokenSource();
                _syncTokens[item.FilePath] = cts;

                var resp = await mainWin.SyncHttpClient.PostAsync($"{mainWin.SyncEngineUrl}/api/upload", content, cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    item.Percentage = 100;
                    item.StateText = "✅ 已同步";
                }
                else
                {
                    item.StateText = "❌ 失败";
                }
            }
            catch (TaskCanceledException)
            {
                item.StateText = "⏸ 已暂停";
            }
            catch (Exception)
            {
                item.StateText = "❌ 失败";
            }
            finally
            {
                _syncTokens.TryRemove(item.FilePath, out _);
            }
        }

        private void MenuRemoteOpen_Click(object sender, RoutedEventArgs e) { }
        private void MenuRemoteRename_Click(object sender, RoutedEventArgs e) { }
        private void MenuRemoteDelete_Click(object sender, RoutedEventArgs e) { }
        private void ListRemoteSync_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { }
    }
}