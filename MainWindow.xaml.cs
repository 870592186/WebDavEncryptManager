using System;
using System.Collections.Generic; // 🌟 确保引入了泛型集合
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.FileIO;
using System.Collections.ObjectModel;
using System.Reflection;


namespace WebDavEncryptManager
{
    public partial class MainWindow : Window
    {
        private static readonly HttpClient httpClient = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        private AppConfig currentConfig = new AppConfig();
        private static readonly System.Threading.SemaphoreSlim _downloadSemaphore = new System.Threading.SemaphoreSlim(5, 5);
        private static readonly System.Threading.SemaphoreSlim _uploadSemaphore = new System.Threading.SemaphoreSlim(5, 5);
        private System.Windows.Threading.DispatcherTimer _refreshDebounceTimer;
        private string currentLocalPath = "";
        private string currentRemotePath = "/";
        private FileSystemWatcher _localWatcher;
        private readonly string GoEngineApiUrl = "http://127.0.0.1:8888";
        private System.Windows.Threading.DispatcherTimer _progressTimer;
        private string _currentTaskId;
        private ObservableCollection<TransferTask> GlobalTasks = new ObservableCollection<TransferTask>();
        private TransferWindow _transferWindow;
        private Process _goEngineProcess;
        private string _extractedGoEnginePath;

        private void StartGoEngine()
        {
            try
            {
                // 1. 确定释放到系统的临时目录
                string tempFolder = Path.GetTempPath();
                _extractedGoEnginePath = Path.Combine(tempFolder, "WebDavEncryptEngine.exe");

                // 2. 如果文件已存在，先尝试删除（清理旧版本）
                if (File.Exists(_extractedGoEnginePath))
                {
                    try { File.Delete(_extractedGoEnginePath); }
                    catch { /* 如果被占用，说明后台可能有残留进程，暂不处理 */ }
                }

                // 3. 🌟 核心排错：自动寻找嵌入的资源名称
                if (!File.Exists(_extractedGoEnginePath))
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    string[] allResourceNames = assembly.GetManifestResourceNames();

                    // 自动匹配结尾是目标文件名的资源
                    string resourceName = allResourceNames.FirstOrDefault(n => n.EndsWith("WebDavEncryptEngine.exe"));

                    if (string.IsNullOrEmpty(resourceName))
                    {
                        // 如果找不到，弹出极其详细的错误列表供我们排查
                        MessageBox.Show("❌ 错误：在程序集中找不到嵌入的 Go 引擎！\n\n" +
                                        "请确保你已经在 Visual Studio 中将 WebDavEncryptEngine.exe 的【生成操作】改为了【嵌入的资源】。\n\n" +
                                        "当前程序集内的所有资源有：\n" + string.Join("\n", allResourceNames),
                                        "资源缺失诊断");
                        return;
                    }

                    // 释放文件到本地
                    using (Stream resourceStream = assembly.GetManifestResourceStream(resourceName))
                    using (FileStream fileStream = new FileStream(_extractedGoEnginePath, FileMode.Create))
                    {
                        resourceStream.CopyTo(fileStream);
                    }

                    // 调试弹窗 1：确认释放成功
                    // MessageBox.Show($"✅ 成功将 Go 引擎释放到:\n{_extractedGoEnginePath}", "调试信息");
                }

                // 4. 启动 Go 引擎 (🛠️ 开启调试模式)
                _goEngineProcess = new Process();
                _goEngineProcess.StartInfo.FileName = _extractedGoEnginePath;

                // 🌟 调试阶段：临时让黑窗口显示出来，以便查看 Go 是否有报错输出
                _goEngineProcess.StartInfo.UseShellExecute = false;       // 不使用系统外壳程序启动
                _goEngineProcess.StartInfo.CreateNoWindow = true;         // 明确指示不创建控制台窗口
                _goEngineProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden; // 窗口样式设置为隐藏（双重保险）
                _goEngineProcess.Start();

                // 调试弹窗 2：确认触发启动
                // MessageBox.Show("🚀 Go 引擎已触发启动！请查看是否弹出了黑色的命令行窗口。如果没有闪退，现在去测试网页吧！", "调试信息");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ 加密传输引擎启动发生严重异常:\n{ex.Message}", "启动失败");
            }
        }

        // 确保在窗口关闭时杀掉后台的 Go 进程
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (_goEngineProcess != null && !_goEngineProcess.HasExited)
            {
                try { _goEngineProcess.Kill(); } catch { }
            }
        }
        // 用于接收 Go 返回的进度数据
        public class TaskProgress
        {
            public long total { get; set; }
            public long current { get; set; }
            public double percentage { get; set; }
            public string status { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();
            InitDriveList();

            // 启动全局唯一守护定时器
            _progressTimer = new System.Windows.Threading.DispatcherTimer();
            _progressTimer.Interval = TimeSpan.FromSeconds(1);
            _progressTimer.Tick += GlobalProgressTimer_Tick;
            _progressTimer.Start();

            // 🌟 初始化节流定时器 (设置为 1.5 秒延迟)
            _refreshDebounceTimer = new System.Windows.Threading.DispatcherTimer();
            _refreshDebounceTimer.Interval = TimeSpan.FromSeconds(1.5);
            _refreshDebounceTimer.Tick += (s, e) =>
            {
                _refreshDebounceTimer.Stop(); // 触发一次后立刻停止
                _ = LoadRemoteFiles(currentRemotePath); // 执行真正的刷新
            };
            StartGoEngine();

            currentConfig = HardwareCryptoHelper.LoadEncryptedConfig("config.dat");
            if (!string.IsNullOrEmpty(currentConfig.Username))
                _ = LoadRemoteFiles("/");
        }

        private void SetProgress(bool isWorking, string text)
        {
            PrgTask.IsIndeterminate = isWorking;
            TxtStatus.Text = text;
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow(currentConfig) { Owner = this };
            if (win.ShowDialog() == true)
            {
                currentConfig = win.Config;
                _ = LoadRemoteFiles("/");
            }
        }

        // ==========================================
        // 📁 本地文件逻辑
        // ==========================================
        private void InitDriveList()
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                ComboDrives.Items.Add(d.Name);
            if (ComboDrives.Items.Count > 0)
                ComboDrives.SelectedIndex = 0;
        }

        private void ComboDrives_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboDrives.SelectedItem != null)
                LoadLocalFiles(ComboDrives.SelectedItem.ToString());
        }

        private void LoadLocalFiles(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                ListLocal.Items.Clear(); currentLocalPath = path;
                var di = new DirectoryInfo(path);

                if (di.Parent != null)
                    ListLocal.Items.Add(new FileItem { Name = "..", FullPath = di.Parent.FullName, IsDirectory = true });

                foreach (var d in di.GetDirectories().Where(d => (d.Attributes & FileAttributes.Hidden) == 0))
                    ListLocal.Items.Add(new FileItem { Name = d.Name, FullPath = d.FullName, IsDirectory = true });

                foreach (var f in di.GetFiles())
                    ListLocal.Items.Add(new FileItem { Name = f.Name, FullPath = f.FullName, IsDirectory = false, Size = f.Length });

                if (_localWatcher != null) { _localWatcher.EnableRaisingEvents = false; _localWatcher.Dispose(); }
                _localWatcher = new FileSystemWatcher(path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
                FileSystemEventHandler handler = (s, ev) => Dispatcher.Invoke(() => LoadLocalFiles(currentLocalPath));
                _localWatcher.Created += handler; _localWatcher.Deleted += handler; _localWatcher.Renamed += (s, ev) => handler(s, null);
                _localWatcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        private void ListLocal_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListLocal.SelectedItem is FileItem item && item.IsDirectory) LoadLocalFiles(item.FullPath);
        }

        // 打开传输列表
        private void BtnTransferList_Click(object sender, RoutedEventArgs e)
        {
            // 懒加载核心逻辑：如果窗口还没被创建过，我们才去创建它
            if (_transferWindow == null)
            {
                _transferWindow = new TransferWindow(GlobalTasks);
                _transferWindow.Owner = this;
            }

            // 显示并激活窗口
            _transferWindow.Show();
            _transferWindow.Activate();
        }

        // 全局唯一轮询：遍历 GlobalTasks 更新进度
        private async void GlobalProgressTimer_Tick(object sender, EventArgs e)
        {
            var activeTasks = GlobalTasks.Where(t => !t.IsCompleted).ToList();

            // 主界面底部状态栏只显示“第一个正在运行”的任务概况
            if (activeTasks.Count == 0)
            {
                PrgTask.Value = 0;
                TxtStatus.Text = "就绪";
                TxtPercentage.Text = "";
            }
            else
            {
                var firstTask = activeTasks.First();
                TxtStatus.Text = $"正在处理: {firstTask.FileName} (共 {activeTasks.Count} 个任务)";
                PrgTask.Value = firstTask.Percentage;
                TxtPercentage.Text = $"{firstTask.Percentage:F1}%";
            }

            // 后台向 Go 请求每个任务的进度并更新
            foreach (var task in activeTasks)
            {
                try
                {
                    var resp = await httpClient.GetStringAsync($"{GoEngineApiUrl}/api/progress?id={task.TaskId}");
                    var progress = JsonSerializer.Deserialize<TaskProgress>(resp);
                    if (progress != null)
                    {
                        task.Percentage = progress.percentage;
                        task.StatusText = $"{progress.percentage:F1}% ({progress.current / 1024 / 1024} MB / {progress.total / 1024 / 1024} MB)";

                        if (progress.percentage >= 100)
                        {
                            task.IsCompleted = true;
                            task.StatusText = "✅ 已完成";
                        }
                    }
                }
                catch { /* 忽略瞬时网络错误 */ }
            }
        }

        private void MenuLocalOpen_Click(object sender, RoutedEventArgs e)
        {
            foreach (FileItem item in ListLocal.SelectedItems)
                Process.Start(new ProcessStartInfo { FileName = item.FullPath, UseShellExecute = true });
        }

        private void MenuLocalRename_Click(object sender, RoutedEventArgs e)
        {
            if (ListLocal.SelectedItem is FileItem item)
            {
                string newName = Interaction.InputBox("新名称:", "重命名", item.Name);
                if (!string.IsNullOrEmpty(newName) && newName != item.Name)
                {
                    string newPath = Path.Combine(Path.GetDirectoryName(item.FullPath), newName);
                    if (item.IsDirectory) Directory.Move(item.FullPath, newPath); else File.Move(item.FullPath, newPath);
                }
            }
        }

        private void MenuLocalDelete_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确认将选中的项目移入回收站吗？", "确认删除", MessageBoxButton.YesNo) == MessageBoxResult.No)
                return;

            foreach (FileItem item in ListLocal.SelectedItems.Cast<FileItem>().ToList())
            {
                if (item.IsDirectory)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
        }

        // ==========================================
        // ☁️ 云端 WebDAV 逻辑 (融合安全编码修复版)
        // ==========================================
        private async Task LoadRemoteFiles(string path)
        {
            if (string.IsNullOrEmpty(currentConfig.Username)) return;
            SetProgress(true, "读取云端目录...");

            try
            {
                // 使用 Uri 确保带有中文的路径被正确编码
                string davBase = currentConfig.WebDavUrl.TrimEnd('/');
                Uri baseUri = new Uri(davBase + "/");
                Uri requestUri = new Uri(baseUri, path.TrimStart('/'));

                var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUri.AbsoluteUri);
                req.Headers.Add("Depth", "1");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}")));

                var resp = await httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 207)
                {
                    ListRemote.Items.Clear();
                    currentRemotePath = path;
                    TxtRemotePath.Text = $"☁️ 云端路径: {Uri.UnescapeDataString(path)}";

                    string xml = await resp.Content.ReadAsStringAsync();
                    XDocument doc = XDocument.Parse(xml);
                    XNamespace ns = "DAV:";

                    if (path != "/" && !string.IsNullOrEmpty(path))
                    {
                        string p = path.TrimEnd('/').LastIndexOf('/') >= 0 ? path.Substring(0, path.TrimEnd('/').LastIndexOf('/') + 1) : "/";
                        ListRemote.Items.Add(new FileItem { Name = "..", FullPath = p, IsDirectory = true });
                    }

                    foreach (var res in doc.Descendants(ns + "response"))
                    {
                        string href = Uri.UnescapeDataString(res.Element(ns + "href")?.Value ?? "");
                        string davAbsolutePath = new Uri(currentConfig.WebDavUrl).AbsolutePath;
                        string rel = href.Substring(href.IndexOf(davAbsolutePath) + davAbsolutePath.Length);

                        if (rel.TrimEnd('/') == path.TrimEnd('/')) continue;

                        long sz = 0;
                        var lenEl = res.Descendants(ns + "getcontentlength").FirstOrDefault();
                        if (lenEl != null) long.TryParse(lenEl.Value, out sz);

                        ListRemote.Items.Add(new FileItem
                        {
                            Name = rel.TrimEnd('/').Split('/').Last(),
                            FullPath = rel,
                            IsDirectory = res.Descendants(ns + "collection").Any(),
                            Size = sz
                        });
                    }
                }
                else
                {
                    MessageBox.Show($"无法打开云端文件夹。服务器返回状态码: {resp.StatusCode}", "读取失败");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取云端目录异常: {ex.Message}");
            }
            finally { SetProgress(false, "就绪"); }
        }

        // ==========================================
        // ⚙️ 核心 API 交互 (上传、下载、播放、重命名、删除)
        // ==========================================
        private void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentConfig.Username)) { MessageBox.Show("请先配置 WebDAV"); return; }
            if (ListLocal.SelectedItems.Count == 0) return;

            string targetRemoteDir = currentRemotePath;
            bool overwriteConflict = CmbConflictPolicy.SelectedIndex == 1;

            foreach (FileItem selected in ListLocal.SelectedItems.Cast<FileItem>().ToList())
            {
                if (selected.IsDirectory)
                {
                    _ = UploadFolderRecursive(selected.FullPath, targetRemoteDir, selected.Name, overwriteConflict);
                }
                else
                {
                    bool isExistRemote = ListRemote.Items.Cast<FileItem>().Any(f => f.Name == selected.Name);
                    if (isExistRemote && !overwriteConflict)
                    {
                        GlobalTasks.Add(new TransferTask { TaskId = Guid.NewGuid().ToString(), FileName = selected.Name, IsUpload = true, Percentage = 100, StatusText = "⏭️ 已跳过 (同名)", IsCompleted = true });
                        continue;
                    }

                    _ = EnqueueUploadTask(selected.Name, selected.FullPath, targetRemoteDir);
                }
            }
        }

        private async Task EnqueueUploadTask(string fileName, string localFullPath, string targetRemoteDir)
        {
            string taskId = Guid.NewGuid().ToString();
            string remoteFullPath = targetRemoteDir.TrimEnd('/') + "/" + fileName;

            var newTask = new TransferTask { TaskId = taskId, FileName = fileName, IsUpload = true, Percentage = 0, StatusText = "排队中..." };
            Application.Current.Dispatcher.Invoke(() => GlobalTasks.Add(newTask));

            await _uploadSemaphore.WaitAsync();

            try
            {
                newTask.StatusText = "准备上传...";
                var payload = new { taskId = taskId, localPath = localFullPath, remotePath = remoteFullPath, webdavUrl = currentConfig.WebDavUrl, username = currentConfig.Username, password = currentConfig.Password, customKey = TxtCustomKeyVisible.Text.Trim() };
                var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

                var resp = await httpClient.PostAsync($"{GoEngineApiUrl}/api/upload", content);
                if (!resp.IsSuccessStatusCode) newTask.StatusText = "❌ 上传失败";
            }
            catch (Exception) { newTask.StatusText = "❌ 发生异常"; }
            finally
            {
                _uploadSemaphore.Release();

                if (!newTask.StatusText.Contains("失败") && !newTask.StatusText.Contains("异常"))
                {
                    newTask.Percentage = 100;
                    newTask.StatusText = "✅ 已完成";
                }
                newTask.IsCompleted = true;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    _refreshDebounceTimer.Stop();
                    _refreshDebounceTimer.Start();
                });
            }
        }

        private async Task CreateRemoteDirectory(string remotePath)
        {
            try
            {
                string davBase = currentConfig.WebDavUrl.TrimEnd('/');
                Uri baseUri = new Uri(davBase + "/");
                Uri requestUri = new Uri(baseUri, remotePath.TrimStart('/'));

                var req = new HttpRequestMessage(new HttpMethod("MKCOL"), requestUri.AbsoluteUri);
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}")));

                await httpClient.SendAsync(req);
            }
            catch { }
        }

        private async Task UploadFolderRecursive(string localDirPath, string targetRemoteDir, string relativeDirName, bool overwriteConflict)
        {
            try
            {
                string currentRemoteFolder = targetRemoteDir.TrimEnd('/') + "/" + relativeDirName;
                await CreateRemoteDirectory(currentRemoteFolder);

                HashSet<string> existingFiles = new HashSet<string>();
                if (!overwriteConflict)
                {
                    try
                    {
                        string davBase = currentConfig.WebDavUrl.TrimEnd('/');
                        Uri baseUri = new Uri(davBase + "/");
                        Uri requestUri = new Uri(baseUri, currentRemoteFolder.TrimStart('/'));

                        var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUri.AbsoluteUri);
                        req.Headers.Add("Depth", "1");
                        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}")));

                        var resp = await httpClient.SendAsync(req);
                        if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 207)
                        {
                            string xml = await resp.Content.ReadAsStringAsync();
                            XDocument doc = XDocument.Parse(xml);
                            XNamespace ns = "DAV:";
                            foreach (var res in doc.Descendants(ns + "response"))
                            {
                                string href = Uri.UnescapeDataString(res.Element(ns + "href")?.Value ?? "");
                                string itemName = href.TrimEnd('/').Split('/').Last();
                                bool isDir = res.Descendants(ns + "collection").Any();
                                if (!isDir && !string.IsNullOrEmpty(itemName))
                                {
                                    existingFiles.Add(itemName);
                                }
                            }
                        }
                    }
                    catch { }
                }

                string[] files = Directory.GetFiles(localDirPath);
                foreach (string file in files)
                {
                    string fileName = Path.GetFileName(file);
                    if (!overwriteConflict && existingFiles.Contains(fileName))
                    {
                        Application.Current.Dispatcher.Invoke(() => {
                            GlobalTasks.Add(new TransferTask { TaskId = Guid.NewGuid().ToString(), FileName = fileName, IsUpload = true, Percentage = 100, StatusText = "⏭️ 已跳过 (同名)", IsCompleted = true });
                        });
                        continue;
                    }
                    _ = EnqueueUploadTask(fileName, file, currentRemoteFolder);
                }

                string[] subDirs = Directory.GetDirectories(localDirPath);
                foreach (string subDir in subDirs)
                {
                    string dirName = Path.GetFileName(subDir);
                    await UploadFolderRecursive(subDir, currentRemoteFolder, dirName, overwriteConflict);
                }
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => {
                    MessageBox.Show($"读取本地文件夹 {localDirPath} 失败: {ex.Message}");
                });
            }
        }


        private void ListRemote_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ListRemote.SelectedItem is FileItem item) HandleRemoteItem(item); }
        private void MenuRemoteOpen_Click(object sender, RoutedEventArgs e) { if (ListRemote.SelectedItem is FileItem item) HandleRemoteItem(item); }

        private async void HandleRemoteItem(FileItem item)
        {
            if (item.IsDirectory) { await LoadRemoteFiles(item.FullPath); return; }

            string ext = Path.GetExtension(item.Name).ToLower();
            string[] vids = { ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv" };
            SetProgress(true, "请求引擎处理...");

            if (Array.Exists(vids, x => x == ext))
            {
                string url = $"{GoEngineApiUrl}/api/stream?path={Uri.EscapeDataString(item.FullPath)}&url={Uri.EscapeDataString(currentConfig.WebDavUrl)}&user={Uri.EscapeDataString(currentConfig.Username)}&pass={Uri.EscapeDataString(currentConfig.Password)}&key={Uri.EscapeDataString(ActualCustomKey)}";
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                SetProgress(false, "就绪");
            }
            else
            {
                await PreviewRemoteFile(item);
            }
        }

        private async Task PreviewRemoteFile(FileItem item)
        {
            SetProgress(true, $"正在准备预览 {item.Name}...");
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), item.Name);
                var payload = new { localPath = tempPath, remotePath = item.FullPath, webdavUrl = currentConfig.WebDavUrl, username = currentConfig.Username, password = currentConfig.Password, customKey = ActualCustomKey };
                var resp = await httpClient.PostAsync($"{GoEngineApiUrl}/api/download", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true });
                }
                else MessageBox.Show("解密预览失败！");
            }
            catch (Exception ex) { MessageBox.Show($"预览异常: {ex.Message}"); }
            finally { SetProgress(false, "就绪"); }
        }

        private void MenuRemoteDownload_Click(object sender, RoutedEventArgs e)
        {
            if (ListRemote.SelectedItems.Count == 0) return;

            string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string baseDownloadsFolder = Path.Combine(userProfilePath, "Downloads");

            foreach (FileItem item in ListRemote.SelectedItems.Cast<FileItem>().ToList())
            {
                if (item.IsDirectory)
                {
                    _ = DownloadFolderRecursive(item.FullPath, item.Name, baseDownloadsFolder);
                }
                else
                {
                    _ = EnqueueDownloadTask(item, "", baseDownloadsFolder);
                }
            }
        }

        private async Task DownloadFolderRecursive(string remoteDirPath, string relativeDir, string baseLocalFolder)
        {
            try
            {
                string davBase = currentConfig.WebDavUrl.TrimEnd('/');
                Uri baseUri = new Uri(davBase + "/");
                Uri requestUri = new Uri(baseUri, remoteDirPath.TrimStart('/'));

                var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUri.AbsoluteUri);
                req.Headers.Add("Depth", "1");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}")));

                var resp = await httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 207)
                {
                    string xml = await resp.Content.ReadAsStringAsync();
                    XDocument doc = XDocument.Parse(xml);
                    XNamespace ns = "DAV:";

                    foreach (var res in doc.Descendants(ns + "response"))
                    {
                        string href = Uri.UnescapeDataString(res.Element(ns + "href")?.Value ?? "");
                        string davAbsolutePath = new Uri(currentConfig.WebDavUrl).AbsolutePath;
                        string relPath = href.Substring(href.IndexOf(davAbsolutePath) + davAbsolutePath.Length);

                        if (relPath.TrimEnd('/') == remoteDirPath.TrimEnd('/')) continue;

                        string itemName = relPath.TrimEnd('/').Split('/').Last();
                        bool isDir = res.Descendants(ns + "collection").Any();

                        long sz = 0;
                        var lenEl = res.Descendants(ns + "getcontentlength").FirstOrDefault();
                        if (lenEl != null) long.TryParse(lenEl.Value, out sz);

                        FileItem currentItem = new FileItem
                        {
                            Name = itemName,
                            FullPath = relPath,
                            IsDirectory = isDir,
                            Size = sz
                        };

                        if (isDir)
                        {
                            await DownloadFolderRecursive(relPath, relativeDir + "/" + itemName, baseLocalFolder);
                        }
                        else
                        {
                            _ = EnqueueDownloadTask(currentItem, relativeDir, baseLocalFolder);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => {
                    MessageBox.Show($"解析文件夹 {remoteDirPath} 失败: {ex.Message}");
                });
            }
        }

        // 🌟 终极修复版 EnqueueDownloadTask
        private async Task EnqueueDownloadTask(FileItem remoteItem, string relativeLocalDir, string baseDownloadsFolder)
        {
            string fileName = remoteItem.Name;
            string remotePath = remoteItem.FullPath;
            long remoteSize = remoteItem.Size; // 云端真实大小

            string targetFolder = string.IsNullOrEmpty(relativeLocalDir)
                ? baseDownloadsFolder
                : Path.Combine(baseDownloadsFolder, relativeLocalDir.Replace("/", "\\"));
            string finalSavePath = Path.Combine(targetFolder, fileName);

            bool overwritePolicy = false;
            Application.Current.Dispatcher.Invoke(() => { overwritePolicy = CmbConflictPolicy.SelectedIndex == 1; });

            long offset = 0;
            bool needDownload = true;

            if (File.Exists(finalSavePath))
            {
                FileInfo fi = new FileInfo(finalSavePath);
                long localSize = fi.Length;

                // 🌟 核心突破点 1：计算本地应该达到的“完美大小”
                long expectedSize = remoteSize;
                // 128 是你的加密文件头的固定大小。如果是加密状态，解密后的文件必须比云端小 128 字节
                if (!string.IsNullOrEmpty(ActualCustomKey) && remoteSize >= 128)
                {
                    expectedSize = remoteSize - 128;
                }

                if (overwritePolicy)
                {
                    // 🌟 核心突破点 2：只要用户选了“覆盖”，不管下没下完，直接咔嚓删掉从 0 开始
                    try { File.Delete(finalSavePath); } catch { }
                    offset = 0;
                    needDownload = true;
                }
                else
                {
                    // 🌟 核心突破点 3：选了“跳过/断点续传”时的精准识别
                    if (localSize >= expectedSize)
                    {
                        // 真的下完了，跳过它！
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            GlobalTasks.Add(new TransferTask
                            {
                                TaskId = Guid.NewGuid().ToString(),
                                FileName = fileName,
                                IsUpload = false,
                                Percentage = 100,
                                StatusText = "⏭️ 已跳过 (已完成)",
                                IsCompleted = true
                            });
                        });
                        return;
                    }
                    else
                    {
                        // 下了一半，开启续传！
                        offset = localSize;
                        needDownload = true;
                    }
                }
            }

            if (!needDownload) return;

            string taskId = Guid.NewGuid().ToString();
            var newTask = new TransferTask
            {
                TaskId = taskId,
                FileName = string.IsNullOrEmpty(relativeLocalDir) ? fileName : $"{relativeLocalDir}/{fileName}",
                IsUpload = false,
                Percentage = 0,
                StatusText = "排队中..."
            };

            Application.Current.Dispatcher.Invoke(() => GlobalTasks.Add(newTask));

            await _downloadSemaphore.WaitAsync();

            try
            {
                newTask.StatusText = "正在下载...";
                if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);

                var payload = new
                {
                    taskId = taskId,
                    localPath = finalSavePath,
                    remotePath = remotePath,
                    webdavUrl = currentConfig.WebDavUrl,
                    username = currentConfig.Username,
                    password = currentConfig.Password,
                    customKey = ActualCustomKey,
                    offset = offset
                };

                var json = System.Text.Json.JsonSerializer.Serialize(payload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var resp = await httpClient.PostAsync($"{GoEngineApiUrl}/api/download", content);

                if (!resp.IsSuccessStatusCode) newTask.StatusText = "❌ 下载失败";
            }
            catch (Exception) { newTask.StatusText = "❌ 传输异常"; }
            finally
            {
                _downloadSemaphore.Release();
                if (!newTask.StatusText.Contains("失败") && !newTask.StatusText.Contains("异常"))
                {
                    newTask.Percentage = 100;
                    newTask.StatusText = "✅ 已完成";
                }
                newTask.IsCompleted = true;
            }
        }

        private void MenuLocalUpload_Click(object sender, RoutedEventArgs e)
        {
            BtnUpload_Click(null, null);
        }

        private async void MenuRemoteRename_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListRemote.SelectedItem is FileItem item)) return;

            string newName = Interaction.InputBox("请输入新名称:", "重命名", item.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;

            SetProgress(true, "正在重命名...");
            try
            {
                string davBase = currentConfig.WebDavUrl.TrimEnd('/');
                Uri baseUri = new Uri(davBase + "/");
                Uri sourceUri = new Uri(baseUri, item.FullPath.TrimStart('/'));

                string destRelPath = currentRemotePath.TrimEnd('/').TrimStart('/') + "/" + newName.TrimStart('/');
                Uri destUri = new Uri(baseUri, destRelPath);

                var req = new HttpRequestMessage(new HttpMethod("MOVE"), sourceUri.AbsoluteUri);
                req.Headers.Add("Destination", destUri.AbsoluteUri);
                req.Headers.Add("Overwrite", "F");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}")));

                var resp = await httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 201 || (int)resp.StatusCode == 204)
                    await LoadRemoteFiles(currentRemotePath);
                else
                    MessageBox.Show($"重命名失败: {resp.StatusCode}");
            }
            catch (Exception ex) { MessageBox.Show($"重命名异常: {ex.Message}"); }
            finally { SetProgress(false, "就绪"); }
        }

        private async void MenuRemoteMove_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListRemote.SelectedItem is FileItem item)) return;

            var folderWin = new RemoteFolderSelectWindow(currentConfig, "/");
            folderWin.Owner = this;

            if (folderWin.ShowDialog() == true)
            {
                string targetDir = folderWin.SelectedPath;
                if (targetDir.TrimEnd('/') == currentRemotePath.TrimEnd('/')) return;

                SetProgress(true, "正在移动文件...");
                try
                {
                    string davBase = currentConfig.WebDavUrl.TrimEnd('/');
                    Uri baseUri = new Uri(davBase + "/");
                    Uri sourceUri = new Uri(baseUri, item.FullPath.TrimStart('/'));

                    string destRelPath = targetDir.TrimEnd('/').TrimStart('/') + "/" + item.Name.TrimStart('/');
                    Uri destUri = new Uri(baseUri, destRelPath);

                    var req = new HttpRequestMessage(new HttpMethod("MOVE"), sourceUri.AbsoluteUri);
                    req.Headers.Add("Destination", destUri.AbsoluteUri);
                    req.Headers.Add("Overwrite", "F");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}")));

                    var resp = await httpClient.SendAsync(req);
                    if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 201 || (int)resp.StatusCode == 204)
                    {
                        await LoadRemoteFiles(currentRemotePath);
                        MessageBox.Show($"成功移动到: {targetDir}");
                    }
                    else MessageBox.Show($"移动失败: {resp.StatusCode}");
                }
                catch (Exception ex) { MessageBox.Show($"移动异常: {ex.Message}"); }
                finally { SetProgress(false, "就绪"); }
            }
        }

        private async void MenuRemoteDelete_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确认要从云端彻底删除选中的文件吗？此操作不可恢复！", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                return;

            SetProgress(true, "正在删除...");
            try
            {
                foreach (FileItem item in ListRemote.SelectedItems.Cast<FileItem>().ToList())
                {
                    string davBase = currentConfig.WebDavUrl.TrimEnd('/');
                    Uri deleteUri = new Uri(new Uri(davBase + "/"), item.FullPath.TrimStart('/'));

                    var req = new HttpRequestMessage(HttpMethod.Delete, deleteUri.AbsoluteUri);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}")));

                    await httpClient.SendAsync(req);
                }
                await LoadRemoteFiles(currentRemotePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除发生异常: {ex.Message}");
            }
            finally
            {
                SetProgress(false, "就绪");
            }
        }

        private string ActualCustomKey = "";

        private void BtnConfirmKey_Click(object sender, RoutedEventArgs e)
        {
            if (TxtCustomKeyVisible.Visibility == Visibility.Visible)
            {
                string key = TxtCustomKeyVisible.Text.Trim();
                if (string.IsNullOrEmpty(key)) return;

                ActualCustomKey = key;
                TxtCustomKeyHidden.Password = key;

                TxtCustomKeyVisible.Visibility = Visibility.Collapsed;
                TxtCustomKeyHidden.Visibility = Visibility.Visible;
                BtnConfirmKey.Content = "修改";
            }
            else
            {
                TxtCustomKeyVisible.Text = ActualCustomKey;

                TxtCustomKeyHidden.Visibility = Visibility.Collapsed;
                TxtCustomKeyVisible.Visibility = Visibility.Visible;
                BtnConfirmKey.Content = "确定";
            }
        }

        // ==========================================
        // 用于保存同步盘窗口的唯一实例（防止用户重复点击打开多个）
        private SyncDriveWindow _syncDriveWindow;

        // 同步盘按钮点击事件
        private void BtnSyncDrive_Click(object sender, RoutedEventArgs e)
        {
            // 如果窗口尚未创建，或者已经被用户关闭了，我们就重新实例化一个
            if (_syncDriveWindow == null || !_syncDriveWindow.IsLoaded)
            {
                _syncDriveWindow = new SyncDriveWindow();
                _syncDriveWindow.Owner = this; // 设定父窗口，保证层级关系
            }

            // 显示窗口并将其带到最前面
            _syncDriveWindow.Show();
            _syncDriveWindow.Activate();
        }
        // 进度条旧版方法 (保留以防报错)
        // ==========================================
        private void StartProgressTracker(string taskId, string taskName)
        {
            _currentTaskId = taskId;
            PrgTask.Value = 0;
            PrgTask.IsIndeterminate = false;
            TxtStatus.Text = taskName;
            TxtPercentage.Text = "0.0%";

            _progressTimer = new System.Windows.Threading.DispatcherTimer();
            _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
            _progressTimer.Tick += async (s, e) =>
            {
                try
                {
                    var resp = await httpClient.GetStringAsync($"{GoEngineApiUrl}/api/progress?id={_currentTaskId}");
                    var progress = JsonSerializer.Deserialize<TaskProgress>(resp);

                    if (progress != null)
                    {
                        PrgTask.Value = progress.percentage;
                        TxtPercentage.Text = $"{progress.percentage:F1}%";
                    }
                }
                catch { }
            };
            _progressTimer.Start();
        }

        private void StopProgressTracker()
        {
            if (_progressTimer != null)
            {
                _progressTimer.Stop();
                _progressTimer = null;
            }
            PrgTask.Value = 100;
            TxtPercentage.Text = "100%";
            TxtStatus.Text = "任务完成";

            Task.Delay(2000).ContinueWith(t => Dispatcher.Invoke(() =>
            {
                PrgTask.Value = 0;
                TxtPercentage.Text = "";
                TxtStatus.Text = "就绪";
            }));
        }
    }
}