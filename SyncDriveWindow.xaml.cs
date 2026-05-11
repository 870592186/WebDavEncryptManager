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

        // 核心解耦：Root 是同步目标，Current 是浏览目标
        private string _localSyncRoot = "";
        private string _localCurrentPath = "";
        private string _remoteSyncRoot = "";
        private string _remoteCurrentPath = "";

        private FileSystemWatcher _watcher;
        private System.Windows.Threading.DispatcherTimer _debounceTimer;
        private System.Windows.Threading.DispatcherTimer _progressTimer;

        private ConcurrentDictionary<string, DateTime> _pendingFiles = new ConcurrentDictionary<string, DateTime>();

        // 全局状态缓存，避免UI变红闪烁
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
            LoadSyncConfig();
        }

        // ==========================================
        // 持久化配置
        // ==========================================
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
                            LoadLocalSyncFiles();
                        }
                        if (!string.IsNullOrEmpty(cfg.RemoteRoot))
                        {
                            _remoteSyncRoot = cfg.RemoteRoot;
                            _remoteCurrentPath = _remoteSyncRoot;
                            TxtRemotePath.Text = _remoteSyncRoot;
                            _ = LoadRemoteFilesToRightSideAsync();
                        }
                        ChkEncrypt.IsChecked = cfg.IsEncrypted;
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

        private void ChkEncrypt_Click(object sender, RoutedEventArgs e) => SaveSyncConfig();

        // ==========================================
        // 托盘与窗口
        // ==========================================
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

        private void MinimizeToTray()
        {
            this.Hide();
            var mainWin = this.Owner as MainWindow; mainWin?.Hide();
            _trayIcon.ShowBalloonTip(2000, "同步盘", "已最小化至系统托盘，后台持续为您同步。", System.Windows.Forms.ToolTipIcon.Info);
        }

        private void BtnReturnMain_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            if (this.Owner != null) { this.Owner.Show(); this.Owner.Activate(); }
        }

        // ==========================================
        // 核心监听器
        // ==========================================
        private void InitTimers()
        {
            _debounceTimer = new System.Windows.Threading.DispatcherTimer();
            _debounceTimer.Interval = TimeSpan.FromSeconds(1);
            _debounceTimer.Tick += (s, e) =>
            {
                if (_isGlobalPaused || string.IsNullOrEmpty(_remoteSyncRoot)) return;
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
                    if (Path.GetDirectoryName(e.FullPath) == _localCurrentPath)
                    {
                        var item = SyncFiles.FirstOrDefault(f => f.FilePath == e.FullPath);
                        if (item == null)
                        {
                            var info = new FileInfo(e.FullPath);
                            SyncFiles.Add(new SyncItem { FilePath = e.FullPath, FileName = info.Name, SizeText = (info.Length / 1024) + " KB", StateText = "检测到修改...", Percentage = 0 });
                        }
                        else
                        {
                            item.StateText = "检测到修改..."; item.Percentage = 0;
                        }
                    }
                });
            };

            _watcher.Changed += handler;
            _watcher.Created += handler;
            _watcher.EnableRaisingEvents = true;

            if (!string.IsNullOrEmpty(_remoteSyncRoot)) TxtSyncStatus.Text = "▶ 正在监听本地变化...";
        }

        // ==========================================
        // 左侧逻辑 (本地)
        // ==========================================
        private void BtnSelectLocal_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _localSyncRoot = dialog.SelectedPath;
                _localCurrentPath = _localSyncRoot;
                TxtLocalPath.Text = _localSyncRoot;
                SaveSyncConfig();

                StartFolderWatcher();
                LoadLocalSyncFiles();
            }
        }

        private void LoadLocalSyncFiles()
        {
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
                SyncFiles.Add(new SyncItem { FilePath = dir, FileName = info.Name, IsDirectory = true, SizeText = "-", StateText = "📁", Percentage = 0 });
            }

            foreach (var file in Directory.GetFiles(_localCurrentPath))
            {
                var info = new FileInfo(file);
                string state = _fileStatesCache.TryGetValue(file, out var s) ? s : "等待比对...";
                double prog = _fileProgressCache.TryGetValue(file, out var p) ? p : 0;
                SyncFiles.Add(new SyncItem { FilePath = file, FileName = info.Name, IsDirectory = false, SizeText = (info.Length / 1024) + " KB", StateText = state, Percentage = prog });
            }

            _ = CompareCurrentLocalWithRemoteAsync();
        }

        private async Task CompareCurrentLocalWithRemoteAsync()
        {
            var mainWin = this.Owner as MainWindow;
            if (mainWin == null || string.IsNullOrEmpty(_remoteSyncRoot)) return;

            try
            {
                string relPath = _localCurrentPath.Substring(_localSyncRoot.Length).TrimStart('\\').Replace("\\", "/");
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

                    var remoteFileNames = doc.Descendants(ns + "response")
                        .Select(res => Uri.UnescapeDataString(res.Element(ns + "href")?.Value ?? "").TrimEnd('/').Split('/').Last())
                        .ToList();

                    Application.Current.Dispatcher.Invoke(() => {
                        foreach (var localItem in SyncFiles.Where(f => !f.IsDirectory))
                        {
                            if (localItem.StateText.Contains("正在同步") || localItem.StateText == "排队同步中..." || localItem.StateText == "检测到修改...") continue;

                            if (remoteFileNames.Contains(localItem.FileName))
                            {
                                localItem.StateText = "✅ 已同步"; localItem.Percentage = 100;
                            }
                            else
                            {
                                localItem.StateText = "排队同步中..."; localItem.Percentage = 0;
                                _pendingFiles.AddOrUpdate(localItem.FilePath, DateTime.Now, (k, v) => DateTime.Now);
                            }
                            _fileStatesCache[localItem.FilePath] = localItem.StateText;
                            _fileProgressCache[localItem.FilePath] = localItem.Percentage;
                        }
                    });
                }
            }
            catch { }
        }

        private void ListLocalSync_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListLocalSync.SelectedItem is SyncItem item && item.IsDirectory)
            {
                _localCurrentPath = item.FilePath;
                TxtLocalPath.Text = _localCurrentPath;
                LoadLocalSyncFiles();
            }
        }

        // ==========================================
        // 右侧逻辑 (云端)
        // ==========================================
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
                SaveSyncConfig();

                _ = LoadRemoteFilesToRightSideAsync();
                LoadLocalSyncFiles();
            }
        }

        // 🌟 核心修复：更健壮的 WebDAV 路径解析，防止闪退不显示
        private async Task LoadRemoteFilesToRightSideAsync()
        {
            var mainWin = this.Owner as MainWindow;
            if (mainWin == null || string.IsNullOrEmpty(_remoteCurrentPath)) return;

            Application.Current.Dispatcher.Invoke(() => TxtSyncStatus.Text = "🔄 正在刷新云端列表...");
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

                        // 🌟 强健字符串处理：防止索引越界引发闪退
                        string relPath = href;
                        if (href.StartsWith(davAbsolutePath, StringComparison.OrdinalIgnoreCase))
                        {
                            relPath = href.Substring(davAbsolutePath.Length);
                        }
                        if (!relPath.StartsWith("/")) relPath = "/" + relPath;

                        if (relPath.TrimEnd('/') == _remoteCurrentPath.TrimEnd('/')) continue;

                        bool isDir = res.Descendants(ns + "collection").Any();
                        long sz = 0;
                        var lenEl = res.Descendants(ns + "getcontentlength").FirstOrDefault();
                        if (lenEl != null) long.TryParse(lenEl.Value, out sz);
                        tempList.Add(new RemoteSyncFile { Name = relPath.TrimEnd('/').Split('/').Last(), FullPath = relPath, IsDirectory = isDir, Size = sz });
                    }

                    Application.Current.Dispatcher.Invoke(() => {
                        RemoteFiles.Clear();
                        foreach (var item in tempList) RemoteFiles.Add(item);
                        TxtSyncStatus.Text = "▶ 正在监听本地变化...";
                    });
                }
                else { Application.Current.Dispatcher.Invoke(() => TxtSyncStatus.Text = "❌ 云端目录读取失败"); }
            }
            catch { Application.Current.Dispatcher.Invoke(() => TxtSyncStatus.Text = "❌ 云端目录读取异常"); }
        }

        private async void ListRemoteSync_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListRemoteSync.SelectedItem is RemoteSyncFile item && item.IsDirectory)
            {
                _remoteCurrentPath = item.FullPath;
                await LoadRemoteFilesToRightSideAsync();
            }
        }

        // ==========================================
        // 自动上传核心逻辑
        // ==========================================
        private void BtnToggleSync_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is SyncItem item)
            {
                if (item.StateText == "❌ 失败") { StartUploadSync(item.FilePath); }
                else
                {
                    item.IsPaused = !item.IsPaused;
                    if (item.IsPaused)
                    {
                        item.StateText = "⏸ 已暂停";
                        if (_syncTokens.TryGetValue(item.FilePath, out var cts)) { cts.Cancel(); _syncTokens.TryRemove(item.FilePath, out _); }
                    }
                    else { item.StateText = "正在恢复..."; StartUploadSync(item.FilePath); }

                    _fileStatesCache[item.FilePath] = item.StateText;
                }
            }
        }

        private async void StartUploadSync(string filePath)
        {
            var item = SyncFiles.FirstOrDefault(f => f.FilePath == filePath);
            // 这里我们还要考虑可能文件在深层，不在当前显示的 UI 列表里
            if (item != null && item.IsPaused) return;

            var mainWin = this.Owner as MainWindow;
            if (mainWin == null || string.IsNullOrEmpty(_remoteSyncRoot)) return;

            if (item != null) { item.TaskId = Guid.NewGuid().ToString(); item.StateText = "⬆️ 正在同步..."; item.Percentage = 0; }
            string taskId = item?.TaskId ?? Guid.NewGuid().ToString();

            _fileStatesCache[filePath] = "⬆️ 正在同步...";

            bool useEncryption = ChkEncrypt.IsChecked == true;
            string customKey = useEncryption ? mainWin.SyncCustomKey : "";

            if (useEncryption && string.IsNullOrEmpty(customKey))
            {
                if (item != null) item.StateText = "❌ 失败";
                _fileStatesCache[filePath] = "❌ 失败";
                Application.Current.Dispatcher.Invoke(() => MessageBox.Show("您勾选了加密同步，但未在主界面填写密钥！", "密钥缺失")); return;
            }

            try
            {
                // 🌟 绝对解耦：只往设置好的同步根目录（_remoteSyncRoot）里传，不看右侧当前列表在哪里！
                string relPath = filePath.Substring(_localSyncRoot.Length).TrimStart('\\').Replace("\\", "/");
                string targetRemotePath = _remoteSyncRoot.TrimEnd('/') + "/" + relPath;

                // 🌟 解决深层级联：自动逐层创建云端文件夹
                string[] folders = relPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                string currentCreatePath = _remoteSyncRoot.TrimEnd('/');
                for (int i = 0; i < folders.Length - 1; i++)
                {
                    currentCreatePath += "/" + folders[i];
                    try
                    {
                        Uri mkcolUri = new Uri(new Uri(mainWin.SyncConfig.WebDavUrl.TrimEnd('/') + "/"), currentCreatePath.TrimStart('/'));
                        var mkcolReq = new HttpRequestMessage(new HttpMethod("MKCOL"), mkcolUri.AbsoluteUri);
                        mkcolReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mainWin.SyncConfig.Username}:{mainWin.SyncConfig.Password}")));
                        await mainWin.SyncHttpClient.SendAsync(mkcolReq);
                    }
                    catch { }
                }

                var payload = new { taskId = taskId, localPath = filePath, remotePath = targetRemotePath, webdavUrl = mainWin.SyncConfig.WebDavUrl, username = mainWin.SyncConfig.Username, password = mainWin.SyncConfig.Password, customKey = customKey };
                var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var cts = new CancellationTokenSource();
                _syncTokens[filePath] = cts;

                var resp = await mainWin.SyncHttpClient.PostAsync($"{mainWin.SyncEngineUrl}/api/upload", content, cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    if (item != null) { item.Percentage = 100; item.StateText = "✅ 已同步"; }
                    _fileStatesCache[filePath] = "✅ 已同步";
                    _fileProgressCache[filePath] = 100;

                    // 如果右侧刚好浏览到这个目录，就刷新一下UI
                    if (_remoteCurrentPath == currentCreatePath) _ = LoadRemoteFilesToRightSideAsync();
                }
                else
                {
                    if (item != null) item.StateText = "❌ 失败";
                    _fileStatesCache[filePath] = "❌ 失败";
                }
            }
            catch (TaskCanceledException)
            {
                if (item != null) item.StateText = "⏸ 已暂停";
                _fileStatesCache[filePath] = "⏸ 已暂停";
            }
            catch (Exception)
            {
                if (item != null) item.StateText = "❌ 失败";
                _fileStatesCache[filePath] = "❌ 失败";
            }
            finally { _syncTokens.TryRemove(filePath, out _); }
        }

        // ==========================================
        // 云端右键操作
        // ==========================================
        private async void MenuRemoteOpen_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListRemoteSync.SelectedItem is RemoteSyncFile item)) return;
            var mainWin = this.Owner as MainWindow; if (mainWin == null) return;
            if (item.IsDirectory) { _remoteCurrentPath = item.FullPath; await LoadRemoteFilesToRightSideAsync(); return; }

            string ext = Path.GetExtension(item.Name).ToLower();
            string[] vids = { ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv" };
            TxtSyncStatus.Text = "⏳ 请求引擎处理文件...";
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
            finally { TxtSyncStatus.Text = "▶ 正在监听本地变化..."; }
        }

        private async void MenuRemoteDownload_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListRemoteSync.SelectedItem is RemoteSyncFile item) || item.IsDirectory) return;
            var mainWin = this.Owner as MainWindow; if (mainWin == null) return;
            string downPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", item.Name);
            TxtSyncStatus.Text = "⏳ 正在下载到 Downloads 文件夹...";
            try
            {
                var payload = new { localPath = downPath, remotePath = item.FullPath, webdavUrl = mainWin.SyncConfig.WebDavUrl, username = mainWin.SyncConfig.Username, password = mainWin.SyncConfig.Password, customKey = mainWin.SyncCustomKey };
                var resp = await mainWin.SyncHttpClient.PostAsync($"{mainWin.SyncEngineUrl}/api/download", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
                if (resp.IsSuccessStatusCode) MessageBox.Show($"下载完成！\n{downPath}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
            finally { TxtSyncStatus.Text = "▶ 正在监听本地变化..."; }
        }

        private async void MenuRemoteNewFolder_Click(object sender, RoutedEventArgs e)
        {
            var mainWin = this.Owner as MainWindow; if (mainWin == null || string.IsNullOrEmpty(_remoteCurrentPath)) return;
            string folderName = Interaction.InputBox("请输入新文件夹名称:", "新建文件夹", "新建文件夹");
            if (string.IsNullOrWhiteSpace(folderName)) return;
            TxtSyncStatus.Text = "⏳ 正在新建...";
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
            TxtSyncStatus.Text = "⏳ 正在重命名...";
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
            if (MessageBox.Show($"确定要删除 [{item.Name}] 吗？\n不可恢复！", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No) return;
            var mainWin = this.Owner as MainWindow; if (mainWin == null) return;
            TxtSyncStatus.Text = "⏳ 正在删除...";
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