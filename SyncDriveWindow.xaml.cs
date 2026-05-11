#nullable disable

using Microsoft.VisualBasic;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Xml.Linq;

namespace WebDavEncryptManager
{
    public class BoolToFolderIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => (bool)value ? "📁" : "📄";
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => null;
    }

    public class SyncItem : INotifyPropertyChanged
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string SizeText { get; set; }
        public bool IsDirectory { get; set; }

        private double _percentage;
        public double Percentage { get => _percentage; set { _percentage = value; OnPropertyChanged(nameof(Percentage)); } }

        private string _stateText = "等待比对...";
        public string StateText { get => _stateText; set { _stateText = value; OnPropertyChanged(nameof(StateText)); } }

        private bool _isPaused;
        public bool IsPaused { get => _isPaused; set { _isPaused = value; OnPropertyChanged(nameof(IsPaused)); } }

        public string TaskId { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class RemoteSyncFile
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
    }

    public class SyncSettings
    {
        public string LocalRoot { get; set; }
        public string RemoteRoot { get; set; }
        public bool IsEncrypted { get; set; }
    }

    public partial class SyncDriveWindow : Window
    {
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private bool _isGlobalPaused = false;
        private bool _isKeyMissingBlocked = false;

        private string _localSyncRoot = "";
        private string _localCurrentPath = "";
        private string _remoteSyncRoot = "";
        private string _remoteCurrentPath = "";

        private FileSystemWatcher _watcher;
        private System.Windows.Threading.DispatcherTimer _debounceTimer;
        private System.Windows.Threading.DispatcherTimer _progressTimer;

        private ConcurrentDictionary<string, DateTime> _pendingFiles = new ConcurrentDictionary<string, DateTime>();
        private static ConcurrentDictionary<string, string> _fileStatesCache = new ConcurrentDictionary<string, string>();
        private static ConcurrentDictionary<string, double> _fileProgressCache = new ConcurrentDictionary<string, double>();

        private ObservableCollection<SyncItem> SyncFiles = new ObservableCollection<SyncItem>();
        private ObservableCollection<RemoteSyncFile> RemoteFiles = new ObservableCollection<RemoteSyncFile>();
        private ConcurrentDictionary<string, CancellationTokenSource> _syncTokens = new ConcurrentDictionary<string, CancellationTokenSource>();

        private readonly string SyncConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sync_config.json");

        public SyncDriveWindow()
        {
            InitializeComponent();
            if (!this.Resources.Contains("BoolToFolderIconConverter"))
                this.Resources.Add("BoolToFolderIconConverter", new BoolToFolderIconConverter());

            ListLocalSync.ItemsSource = SyncFiles;
            ListRemoteSync.ItemsSource = RemoteFiles;

            InitSystemTray();
            InitTimers();
            this.Loaded += (s, e) => LoadSyncConfig();
        }

        private void LoadSyncConfig()
        {
            try
            {
                if (File.Exists(SyncConfigPath))
                {
                    string json = File.ReadAllText(SyncConfigPath);
                    var cfg = JsonSerializer.Deserialize<SyncSettings>(json);
                    if (cfg != null)
                    {
                        if (Directory.Exists(cfg.LocalRoot))
                        {
                            _localSyncRoot = cfg.LocalRoot;
                            _localCurrentPath = _localSyncRoot;
                            TxtLocalPath.Text = _localSyncRoot;
                            StartFolderWatcher();
                        }
                        if (!string.IsNullOrEmpty(cfg.RemoteRoot))
                        {
                            _remoteSyncRoot = cfg.RemoteRoot;
                            _remoteCurrentPath = _remoteSyncRoot;
                            TxtRemotePath.Text = _remoteSyncRoot;
                        }
                        ChkEncrypt.IsChecked = cfg.IsEncrypted;

                        if (!string.IsNullOrEmpty(_localSyncRoot) && !string.IsNullOrEmpty(_remoteSyncRoot))
                        {
                            LoadLocalSyncFiles();
                            _ = LoadRemoteFilesToRightSideAsync();
                            // 🌟 修复：启动时全量重对比
                            _ = DeepScanSyncAsync();
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveSyncConfig()
        {
            try
            {
                var cfg = new SyncSettings { LocalRoot = _localSyncRoot, RemoteRoot = _remoteSyncRoot, IsEncrypted = ChkEncrypt.IsChecked == true };
                File.WriteAllText(SyncConfigPath, JsonSerializer.Serialize(cfg));
            }
            catch { }
        }

        private void InitSystemTray()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon();
            _trayIcon.Icon = System.Drawing.SystemIcons.Shield;
            _trayIcon.Text = "WebDAV 加密同步盘";
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += (s, e) => ShowSyncWindow();
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("打开同步盘", null, (s, e) => ShowSyncWindow()));
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("打开主界面", null, (s, e) => {
                this.Hide();
                var mainWin = this.Owner as MainWindow;
                if (mainWin != null) { mainWin.Show(); mainWin.WindowState = WindowState.Normal; mainWin.Activate(); }
            }));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("暂停全部同步", null, (s, e) => ToggleGlobalSync((System.Windows.Forms.ToolStripMenuItem)s)));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("退出软件", null, (s, e) => Application.Current.Shutdown()));
            _trayIcon.ContextMenuStrip = menu;
        }

        private void ShowSyncWindow() { this.Show(); this.WindowState = WindowState.Normal; this.Activate(); }

        private void ToggleGlobalSync(System.Windows.Forms.ToolStripMenuItem menuItem)
        {
            _isGlobalPaused = !_isGlobalPaused;
            menuItem.Text = _isGlobalPaused ? "继续全部同步" : "暂停全部同步";
            TxtSyncStatus.Text = _isGlobalPaused ? "⏸ 全局同步已暂停" : "▶ 正在监听本地变化...";
        }

        protected override void OnClosing(CancelEventArgs e) { e.Cancel = true; MinimizeToTray(); }
        private void BtnMinimizeTray_Click(object sender, RoutedEventArgs e) { MinimizeToTray(); }
        private void MinimizeToTray() { this.Hide(); var mainWin = this.Owner as MainWindow; mainWin?.Hide(); _trayIcon.ShowBalloonTip(2000, "同步盘", "后台持续监控同步中。", System.Windows.Forms.ToolTipIcon.Info); }
        private void BtnReturnMain_Click(object sender, RoutedEventArgs e) { this.Hide(); if (this.Owner != null) { this.Owner.Show(); this.Owner.Activate(); } }

        private void InitTimers()
        {
            _debounceTimer = new System.Windows.Threading.DispatcherTimer();
            _debounceTimer.Interval = TimeSpan.FromSeconds(1);
            _debounceTimer.Tick += (s, e) =>
            {
                if (_isGlobalPaused || string.IsNullOrEmpty(_remoteSyncRoot) || _isKeyMissingBlocked) return;
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

            _progressTimer = new System.Windows.Threading.DispatcherTimer();
            _progressTimer.Interval = TimeSpan.FromSeconds(1);
            _progressTimer.Tick += async (s, e) =>
            {
                var mainWin = this.Owner as MainWindow; if (mainWin == null) return;
                var activeItems = SyncFiles.Where(i => !string.IsNullOrEmpty(i.TaskId) && i.StateText.Contains("正在同步")).ToList();
                foreach (var item in activeItems)
                {
                    try
                    {
                        var resp = await mainWin.SyncHttpClient.GetStringAsync($"{mainWin.SyncEngineUrl}/api/progress?id={item.TaskId}");
                        var progress = JsonSerializer.Deserialize<MainWindow.TaskProgress>(resp);
                        if (progress != null)
                        {
                            item.Percentage = progress.percentage;
                            item.StateText = $"正在同步... {progress.percentage:F1}%";
                            _fileStatesCache[item.FilePath] = item.StateText;
                            _fileProgressCache[item.FilePath] = item.Percentage;
                        }
                    }
                    catch { }
                }
            };
            _progressTimer.Start();
        }

        private void StartFolderWatcher()
        {
            if (string.IsNullOrEmpty(_localSyncRoot) || !Directory.Exists(_localSyncRoot)) return;
            if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); }

            _watcher = new FileSystemWatcher(_localSyncRoot);
            _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName;
            _watcher.IncludeSubdirectories = true;

            FileSystemEventHandler handler = (s, e) =>
            {
                if (string.IsNullOrEmpty(_remoteSyncRoot)) return;
                if (Directory.Exists(e.FullPath)) return;

                _pendingFiles.AddOrUpdate(e.FullPath, DateTime.Now, (key, oldValue) => DateTime.Now);
                _fileStatesCache[e.FullPath] = "检测到修改...";
                _fileProgressCache[e.FullPath] = 0;

                Application.Current.Dispatcher.Invoke(() => {
                    var item = SyncFiles.FirstOrDefault(f => f.FilePath == e.FullPath);
                    if (item != null) { item.StateText = "检测到修改..."; item.Percentage = 0; }
                    else if (Path.GetDirectoryName(e.FullPath) == _localCurrentPath)
                    {
                        var info = new FileInfo(e.FullPath);
                        SyncFiles.Add(new SyncItem { FilePath = e.FullPath, FileName = info.Name, SizeText = (info.Length / 1024) + " KB", StateText = "检测到修改...", Percentage = 0 });
                    }
                });
            };

            _watcher.Changed += handler;
            _watcher.Created += handler;
            _watcher.Renamed += (s, e) => { _fileStatesCache.TryRemove(e.OldFullPath, out _); _fileProgressCache.TryRemove(e.OldFullPath, out _); handler(s, e); };
            _watcher.EnableRaisingEvents = true;
        }

        // 🌟 核心修复：全量扫描递归算法
        private async Task DeepScanSyncAsync()
        {
            if (string.IsNullOrEmpty(_localSyncRoot) || string.IsNullOrEmpty(_remoteSyncRoot)) return;
            Application.Current.Dispatcher.Invoke(() => TxtSyncStatus.Text = "🔍 正在执行全量递归同步比对...");

            try
            {
                // 1. 获取本地所有文件（递归）
                var allLocalFiles = Directory.GetFiles(_localSyncRoot, "*.*", SearchOption.AllDirectories);

                foreach (var file in allLocalFiles)
                {
                    // 🌟 如果状态不是“已同步”，或者我们刚刚更换了同步盘（手动清空了缓存），则全部加入队列
                    if (!_fileStatesCache.TryGetValue(file, out var state) || state != "✅ 已同步")
                    {
                        _fileStatesCache[file] = "正在排队...";
                        // 强制给 3 秒倒计时，模拟“刚修改过”，触发自动上传
                        _pendingFiles.AddOrUpdate(file, DateTime.Now.AddSeconds(-2), (k, v) => DateTime.Now.AddSeconds(-2));
                    }
                }

                Application.Current.Dispatcher.Invoke(() => { LoadLocalSyncFiles(); TxtSyncStatus.Text = "▶ 同步盘正在运行"; });
            }
            catch { }
        }

        private void BtnSelectLocal_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _localSyncRoot = dialog.SelectedPath;
                _localCurrentPath = _localSyncRoot;
                TxtLocalPath.Text = _localSyncRoot;
                _fileStatesCache.Clear(); // 🌟 切换本地根目录，必须清空缓存重新比对
                SaveSyncConfig();
                StartFolderWatcher();
                LoadLocalSyncFiles();
                _ = DeepScanSyncAsync();
            }
        }

        private void LoadLocalSyncFiles()
        {
            string selectedPath = (ListLocalSync.SelectedItem as SyncItem)?.FilePath;
            SyncFiles.Clear();
            if (string.IsNullOrEmpty(_localCurrentPath) || !Directory.Exists(_localCurrentPath)) return;

            if (_localCurrentPath != _localSyncRoot)
            {
                var parent = Directory.GetParent(_localCurrentPath);
                if (parent != null) SyncFiles.Add(new SyncItem { FilePath = parent.FullName, FileName = "..", IsDirectory = true, SizeText = "-", StateText = "📁", Percentage = 0 });
            }

            foreach (var dir in Directory.GetDirectories(_localCurrentPath))
            {
                var info = new DirectoryInfo(dir);
                bool allSynced = IsFolderFullySynced(dir);
                SyncFiles.Add(new SyncItem { FilePath = dir, FileName = info.Name, IsDirectory = true, SizeText = "-", StateText = allSynced ? "✅ 已同步" : "📁", Percentage = allSynced ? 100 : 0 });
            }

            foreach (var file in Directory.GetFiles(_localCurrentPath))
            {
                var info = new FileInfo(file);
                string state = _fileStatesCache.TryGetValue(file, out var s) ? s : "等待比对...";
                double prog = _fileProgressCache.TryGetValue(file, out var p) ? p : 0;
                SyncFiles.Add(new SyncItem { FilePath = file, FileName = info.Name, IsDirectory = false, SizeText = (info.Length / 1024) + " KB", StateText = state, Percentage = prog });
            }

            if (!string.IsNullOrEmpty(selectedPath)) { var item = SyncFiles.FirstOrDefault(f => f.FilePath == selectedPath); if (item != null) ListLocalSync.SelectedItem = item; }
            _ = CompareCurrentLocalWithRemoteAsync();
        }

        private bool IsFolderFullySynced(string folderPath)
        {
            try
            {
                var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);
                if (files.Length == 0) return true;
                return files.All(f => _fileStatesCache.TryGetValue(f, out var s) && s == "✅ 已同步");
            }
            catch { return false; }
        }

        private async Task CompareCurrentLocalWithRemoteAsync()
        {
            var mainWin = this.Owner as MainWindow; if (mainWin == null || string.IsNullOrEmpty(_remoteSyncRoot)) return;
            try
            {
                string relPath = Path.GetRelativePath(_localSyncRoot, _localCurrentPath).Replace("\\", "/");
                if (relPath == ".") relPath = "";
                string targetRemoteDir = _remoteSyncRoot.TrimEnd('/') + (string.IsNullOrEmpty(relPath) ? "" : "/" + relPath);
                Uri requestUri = new Uri(new Uri(mainWin.SyncConfig.WebDavUrl.TrimEnd('/') + "/"), targetRemoteDir.TrimStart('/'));
                var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUri.AbsoluteUri);
                req.Headers.Add("Depth", "1");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mainWin.SyncConfig.Username}:{mainWin.SyncConfig.Password}")));
                var resp = await mainWin.SyncHttpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 207)
                {
                    string xml = await resp.Content.ReadAsStringAsync();
                    XDocument doc = XDocument.Parse(xml); XNamespace ns = "DAV:";
                    var remoteFileNames = doc.Descendants(ns + "response").Select(res => Uri.UnescapeDataString(res.Element(ns + "href")?.Value ?? "").TrimEnd('/').Split('/').Last()).ToList();
                    Application.Current.Dispatcher.Invoke(() => {
                        foreach (var localItem in SyncFiles.Where(f => !f.IsDirectory))
                        {
                            if (localItem.StateText.Contains("正在同步") || localItem.StateText == "排队同步中..." || localItem.StateText == "检测到修改...") continue;
                            if (remoteFileNames.Contains(localItem.FileName)) { localItem.StateText = "✅ 已同步"; localItem.Percentage = 100; }
                            else { localItem.StateText = "排队同步中..."; localItem.Percentage = 0; _pendingFiles.AddOrUpdate(localItem.FilePath, DateTime.Now, (k, v) => DateTime.Now); }
                            _fileStatesCache[localItem.FilePath] = localItem.StateText;
                            _fileProgressCache[localItem.FilePath] = localItem.Percentage;
                        }
                        foreach (var folder in SyncFiles.Where(f => f.IsDirectory && f.FileName != ".."))
                        {
                            bool fully = IsFolderFullySynced(folder.FilePath);
                            folder.StateText = fully ? "✅ 已同步" : "📁"; folder.Percentage = fully ? 100 : 0;
                        }
                    });
                }
            }
            catch { }
        }

        private void ListLocalSync_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ListLocalSync.SelectedItem is SyncItem item && item.IsDirectory) { _localCurrentPath = item.FilePath; TxtLocalPath.Text = _localCurrentPath; LoadLocalSyncFiles(); } }

        private void BtnSelectRemote_Click(object sender, RoutedEventArgs e)
        {
            var mainWin = this.Owner as MainWindow; if (mainWin == null) return;
            var folderWin = new RemoteFolderSelectWindow(mainWin.SyncConfig, "/");
            folderWin.Owner = this;
            if (folderWin.ShowDialog() == true)
            {
                _remoteSyncRoot = folderWin.SelectedPath;
                _remoteCurrentPath = _remoteSyncRoot;
                TxtRemotePath.Text = _remoteSyncRoot;
                _fileStatesCache.Clear(); // 🌟 重点修复：更换云端同步盘，必须清空缓存强制重传/比对
                SaveSyncConfig();
                _ = LoadRemoteFilesToRightSideAsync();
                LoadLocalSyncFiles();
                _ = DeepScanSyncAsync();
            }
        }

        private async Task LoadRemoteFilesToRightSideAsync()
        {
            var mainWin = this.Owner as MainWindow; if (mainWin == null || string.IsNullOrEmpty(_remoteCurrentPath)) return;
            try
            {
                string davBase = mainWin.SyncConfig.WebDavUrl.TrimEnd('/');
                Uri requestUri = new Uri(new Uri(davBase + "/"), _remoteCurrentPath.TrimStart('/'));
                var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUri.AbsoluteUri);
                req.Headers.Add("Depth", "1");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mainWin.SyncConfig.Username}:{mainWin.SyncConfig.Password}")));
                var resp = await mainWin.SyncHttpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 207)
                {
                    string xml = await resp.Content.ReadAsStringAsync();
                    XDocument doc = XDocument.Parse(xml); XNamespace ns = "DAV:";
                    var tempList = new System.Collections.Generic.List<RemoteSyncFile>();
                    if (_remoteCurrentPath != _remoteSyncRoot && _remoteCurrentPath != "/")
                    {
                        string p = _remoteCurrentPath.TrimEnd('/').LastIndexOf('/') >= 0 ? _remoteCurrentPath.Substring(0, _remoteCurrentPath.TrimEnd('/').LastIndexOf('/')) : "/";
                        if (string.IsNullOrEmpty(p)) p = "/";
                        tempList.Add(new RemoteSyncFile { Name = "..", FullPath = p, IsDirectory = true });
                    }
                    foreach (var res in doc.Descendants(ns + "response"))
                    {
                        string href = Uri.UnescapeDataString(res.Element(ns + "href")?.Value ?? "");
                        string davAbsolutePath = new Uri(mainWin.SyncConfig.WebDavUrl).AbsolutePath;
                        string relPath = href.StartsWith(davAbsolutePath, StringComparison.OrdinalIgnoreCase) ? href.Substring(davAbsolutePath.Length) : href;
                        if (!relPath.StartsWith("/")) relPath = "/" + relPath;
                        if (relPath.TrimEnd('/') == _remoteCurrentPath.TrimEnd('/')) continue;
                        tempList.Add(new RemoteSyncFile { Name = relPath.TrimEnd('/').Split('/').Last(), FullPath = relPath, IsDirectory = res.Descendants(ns + "collection").Any() });
                    }
                    Application.Current.Dispatcher.Invoke(() => { RemoteFiles.Clear(); foreach (var item in tempList) RemoteFiles.Add(item); });
                }
            }
            catch { }
        }

        private async void ListRemoteSync_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ListRemoteSync.SelectedItem is RemoteSyncFile item && item.IsDirectory) { _remoteCurrentPath = item.FullPath; await LoadRemoteFilesToRightSideAsync(); } }

        private void BtnToggleSync_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is SyncItem item)
            {
                if (_isKeyMissingBlocked) _isKeyMissingBlocked = false;
                if (item.IsPaused) { item.IsPaused = false; item.StateText = "正在恢复..."; StartUploadSync(item.FilePath); }
                else { item.IsPaused = true; item.StateText = "⏸ 已暂停"; if (_syncTokens.TryGetValue(item.FilePath, out var cts)) { cts.Cancel(); _syncTokens.TryRemove(item.FilePath, out _); } }
                _fileStatesCache[item.FilePath] = item.StateText;
            }
        }

        // 🌟 核心函数：上传与递归创建云端目录
        private async void StartUploadSync(string filePath)
        {
            var itemInView = SyncFiles.FirstOrDefault(f => f.FilePath == filePath);
            if (itemInView != null && itemInView.IsPaused) return;

            var mainWin = this.Owner as MainWindow;
            if (mainWin == null || string.IsNullOrEmpty(_remoteSyncRoot)) return;

            bool useEncryption = ChkEncrypt.IsChecked == true;
            string customKey = useEncryption ? mainWin.SyncCustomKey : "";
            if (useEncryption && string.IsNullOrEmpty(customKey))
            {
                if (!_isKeyMissingBlocked) { _isKeyMissingBlocked = true; MessageBox.Show("启用了加密但无密钥！同步已拦截。"); }
                if (itemInView != null) itemInView.StateText = "❌ 缺少密钥";
                _fileStatesCache[filePath] = "❌ 缺少密钥"; return;
            }

            _isKeyMissingBlocked = false;
            string taskId = Guid.NewGuid().ToString();
            if (itemInView != null) { itemInView.TaskId = taskId; itemInView.StateText = "⬆️ 正在同步..."; itemInView.Percentage = 0; }
            _fileStatesCache[filePath] = "⬆️ 正在同步...";

            try
            {
                // 1. 计算相对路径
                string relativePath = Path.GetRelativePath(_localSyncRoot, filePath).Replace("\\", "/");
                string targetRemotePath = _remoteSyncRoot.TrimEnd('/') + "/" + relativePath;

                // 2. 🌟 重点：递归创建父目录
                string[] parts = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                string currentRemoteBuild = _remoteSyncRoot.TrimEnd('/');
                for (int i = 0; i < parts.Length - 1; i++) // 循环到倒数第二个（即父文件夹）
                {
                    currentRemoteBuild += "/" + parts[i];
                    try
                    {
                        Uri mkUri = new Uri(new Uri(mainWin.SyncConfig.WebDavUrl.TrimEnd('/') + "/"), currentRemoteBuild.TrimStart('/'));
                        var mkReq = new HttpRequestMessage(new HttpMethod("MKCOL"), mkUri.AbsoluteUri);
                        mkReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mainWin.SyncConfig.Username}:{mainWin.SyncConfig.Password}")));
                        var mkResp = await mainWin.SyncHttpClient.SendAsync(mkReq);
                        // 405 Method Not Allowed 通常意味着目录已存在，忽略即可
                    }
                    catch { }
                }

                // 3. 开始真实上传
                var payload = new { taskId = taskId, localPath = filePath, remotePath = targetRemotePath, webdavUrl = mainWin.SyncConfig.WebDavUrl, username = mainWin.SyncConfig.Username, password = mainWin.SyncConfig.Password, customKey = customKey };
                var cts = new CancellationTokenSource(); _syncTokens[filePath] = cts;
                var resp = await mainWin.SyncHttpClient.PostAsync($"{mainWin.SyncEngineUrl}/api/upload", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    _fileStatesCache[filePath] = "✅ 已同步"; _fileProgressCache[filePath] = 100;
                    Application.Current.Dispatcher.Invoke(() => {
                        if (itemInView != null) { itemInView.StateText = "✅ 已同步"; itemInView.Percentage = 100; }
                        // 🌟 上传完一个文件，必须遍历刷新当前视图中的文件夹状态，让文件夹“变绿”
                        foreach (var f in SyncFiles.Where(x => x.IsDirectory && x.FileName != ".."))
                        {
                            if (IsFolderFullySynced(f.FilePath)) { f.StateText = "✅ 已同步"; f.Percentage = 100; }
                        }
                        string remoteParent = targetRemotePath.Substring(0, targetRemotePath.LastIndexOf('/'));
                        if (string.IsNullOrEmpty(remoteParent)) remoteParent = "/";
                        if (_remoteCurrentPath.TrimEnd('/') == remoteParent.TrimEnd('/')) _ = LoadRemoteFilesToRightSideAsync();
                    });
                }
                else { if (itemInView != null) itemInView.StateText = "❌ 失败"; _fileStatesCache[filePath] = "❌ 失败"; }
            }
            catch { if (itemInView != null) itemInView.StateText = "❌ 失败"; _fileStatesCache[filePath] = "❌ 失败"; }
            finally { _syncTokens.TryRemove(filePath, out _); }
        }

        private void ChkEncrypt_Click(object sender, RoutedEventArgs e) { _isKeyMissingBlocked = false; SaveSyncConfig(); }

        private async void MenuRemoteOpen_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListRemoteSync.SelectedItem is RemoteSyncFile item)) return;
            var mainWin = this.Owner as MainWindow; if (mainWin == null) return;
            if (item.IsDirectory) { _remoteCurrentPath = item.FullPath; await LoadRemoteFilesToRightSideAsync(); return; }
            string ext = Path.GetExtension(item.Name).ToLower();
            string[] vids = { ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv" };
            TxtSyncStatus.Text = "⏳ 请求处理中...";
            try
            {
                if (vids.Contains(ext))
                {
                    string url = $"{mainWin.SyncEngineUrl}/api/stream?path={Uri.EscapeDataString(item.FullPath)}&url={Uri.EscapeDataString(mainWin.SyncConfig.WebDavUrl)}&user={Uri.EscapeDataString(mainWin.SyncConfig.Username)}&pass={Uri.EscapeDataString(mainWin.SyncConfig.Password)}&key={Uri.EscapeDataString(mainWin.SyncCustomKey)}";
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                }
                else
                {
                    string tempPath = Path.Combine(Path.GetTempPath(), item.Name);
                    var payload = new { localPath = tempPath, remotePath = item.FullPath, webdavUrl = mainWin.SyncConfig.WebDavUrl, username = mainWin.SyncConfig.Username, password = mainWin.SyncConfig.Password, customKey = mainWin.SyncCustomKey };
                    var resp = await mainWin.SyncHttpClient.PostAsync($"{mainWin.SyncEngineUrl}/api/download", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
                    if (resp.IsSuccessStatusCode) Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true });
                }
            }
            catch { }
            finally { TxtSyncStatus.Text = "▶ 同步盘正在运行"; }
        }

        private async void MenuRemoteDownload_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListRemoteSync.SelectedItem is RemoteSyncFile item) || item.IsDirectory) return;
            var mainWin = this.Owner as MainWindow; if (mainWin == null) return;
            string downPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", item.Name);
            TxtSyncStatus.Text = "⏳ 下载中...";
            try
            {
                var payload = new { localPath = downPath, remotePath = item.FullPath, webdavUrl = mainWin.SyncConfig.WebDavUrl, username = mainWin.SyncConfig.Username, password = mainWin.SyncConfig.Password, customKey = mainWin.SyncCustomKey };
                var resp = await mainWin.SyncHttpClient.PostAsync($"{mainWin.SyncEngineUrl}/api/download", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
                if (resp.IsSuccessStatusCode) MessageBox.Show($"下载完成：{downPath}");
            }
            catch { }
            finally { TxtSyncStatus.Text = "▶ 同步盘正在运行"; }
        }

        private async void MenuRemoteNewFolder_Click(object sender, RoutedEventArgs e)
        {
            var mainWin = this.Owner as MainWindow; if (mainWin == null || string.IsNullOrEmpty(_remoteCurrentPath)) return;
            string folderName = Interaction.InputBox("请输入新文件夹名称:", "新建文件夹", "新建文件夹");
            if (string.IsNullOrWhiteSpace(folderName)) return;
            try
            {
                string targetPath = _remoteCurrentPath.TrimEnd('/') + "/" + folderName.Trim();
                Uri requestUri = new Uri(new Uri(mainWin.SyncConfig.WebDavUrl.TrimEnd('/') + "/"), targetPath.TrimStart('/'));
                var req = new HttpRequestMessage(new HttpMethod("MKCOL"), requestUri.AbsoluteUri);
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mainWin.SyncConfig.Username}:{mainWin.SyncConfig.Password}")));
                await mainWin.SyncHttpClient.SendAsync(req);
                await LoadRemoteFilesToRightSideAsync();
            }
            catch { }
        }

        private async void MenuRemoteRename_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListRemoteSync.SelectedItem is RemoteSyncFile item)) return;
            var mainWin = this.Owner as MainWindow; if (mainWin == null) return;
            string newName = Interaction.InputBox("请输入新名称:", "重命名", item.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
            try
            {
                Uri baseUri = new Uri(mainWin.SyncConfig.WebDavUrl.TrimEnd('/') + "/");
                Uri sourceUri = new Uri(baseUri, item.FullPath.TrimStart('/'));
                Uri destUri = new Uri(baseUri, _remoteCurrentPath.TrimEnd('/').TrimStart('/') + "/" + newName.TrimStart('/'));
                var req = new HttpRequestMessage(new HttpMethod("MOVE"), sourceUri.AbsoluteUri);
                req.Headers.Add("Destination", destUri.AbsoluteUri); req.Headers.Add("Overwrite", "F");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mainWin.SyncConfig.Username}:{mainWin.SyncConfig.Password}")));
                await mainWin.SyncHttpClient.SendAsync(req);
                await LoadRemoteFilesToRightSideAsync();
            }
            catch { }
        }

        private async void MenuRemoteDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListRemoteSync.SelectedItem is RemoteSyncFile item)) return;
            if (MessageBox.Show($"确定删除 [{item.Name}] 吗？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.No) return;
            var mainWin = this.Owner as MainWindow; if (mainWin == null) return;
            try
            {
                Uri deleteUri = new Uri(new Uri(mainWin.SyncConfig.WebDavUrl.TrimEnd('/') + "/"), item.FullPath.TrimStart('/'));
                var req = new HttpRequestMessage(HttpMethod.Delete, deleteUri.AbsoluteUri);
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mainWin.SyncConfig.Username}:{mainWin.SyncConfig.Password}")));
                await mainWin.SyncHttpClient.SendAsync(req);
                await LoadRemoteFilesToRightSideAsync();
            }
            catch { }
        }
    }
}