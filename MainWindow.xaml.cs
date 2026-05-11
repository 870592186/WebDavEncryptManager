#nullable disable

using System;
using System.Collections.Generic;
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
using System.Windows.Input;
using System.Xml.Linq;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.FileIO;
using System.Collections.ObjectModel;
using System.Reflection;
using System.ComponentModel;

namespace WebDavEncryptManager
{
    // 🌟 已经被我彻底删除了这里的 TransferTask 类！现在它只会去读取你独立的 TransferTask.cs 文件。

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
        private ObservableCollection<TransferTask> GlobalTasks = new ObservableCollection<TransferTask>();
        private TransferWindow _transferWindow;
        private SyncDriveWindow _syncDriveWindow;
        private Process _goEngineProcess;
        private string _extractedGoEnginePath;
        private string ActualCustomKey = "";
        private Dictionary<string, CancellationTokenSource> _taskTokens = new Dictionary<string, CancellationTokenSource>();

        private readonly string TasksSavePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tasks.json");
        private int _autoSaveTick = 0;

        public MainWindow()
        {
            InitializeComponent();
            InitDriveList();
            LoadTasks();

            _progressTimer = new System.Windows.Threading.DispatcherTimer();
            _progressTimer.Interval = TimeSpan.FromSeconds(1);
            _progressTimer.Tick += GlobalProgressTimer_Tick;
            _progressTimer.Start();

            _refreshDebounceTimer = new System.Windows.Threading.DispatcherTimer();
            _refreshDebounceTimer.Interval = TimeSpan.FromSeconds(1.5);
            _refreshDebounceTimer.Tick += (s, e) =>
            {
                _refreshDebounceTimer.Stop();
                _ = LoadRemoteFiles(currentRemotePath);
            };

            StartGoEngine();

            currentConfig = HardwareCryptoHelper.LoadEncryptedConfig("config.dat");
            if (!string.IsNullOrEmpty(currentConfig.Username))
                _ = LoadRemoteFiles("/");
        }

        // ==========================================
        // 🌟 优化：防呆机制，操作前检查密钥
        // ==========================================
        private bool CheckCustomKeyBeforeAction()
        {
            if (string.IsNullOrEmpty(ActualCustomKey))
            {
                var res = MessageBox.Show(
                    "系统检测到您当前未填写【信封密钥】。\n\n" +
                    "▶ 如果您即将下载或打开的是【加密文件】，请点击“取消”，并在右上角填好密钥后再试，否则将导致报错或下载出乱码。\n\n" +
                    "▶ 如果您操作的是【普通未加密文件】，请直接点击“确定”继续。",
                    "缺少密钥提示",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                if (res == MessageBoxResult.Cancel)
                {
                    return false; // 用户选择了取消，中止后续的打开或下载操作
                }
            }
            return true;
        }

        private void LoadTasks()
        {
            try
            {
                if (File.Exists(TasksSavePath))
                {
                    string json = File.ReadAllText(TasksSavePath);
                    var savedTasks = JsonSerializer.Deserialize<List<TransferTask>>(json);
                    if (savedTasks != null)
                    {
                        foreach (var task in savedTasks)
                        {
                            if (!task.IsCompleted && !task.IsCancelled && task.StatusText != "❌ 已失效")
                            {
                                task.IsPaused = true;
                                task.StatusText = "⏸ 已暂停 (应用重启)";
                            }
                            GlobalTasks.Add(task);
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveTasks()
        {
            try
            {
                string json = JsonSerializer.Serialize(GlobalTasks.ToList());
                File.WriteAllText(TasksSavePath, json);
            }
            catch { }
        }

        private void StartGoEngine()
        {
            try
            {
                // 🌟 修复核心：在释放和启动新引擎之前，先无情斩杀后台所有残留的旧引擎进程！
                // 防止 8888 端口被旧进程持续霸占，导致新引擎启动闪退。
                var existingProcesses = Process.GetProcessesByName("WebDavEncryptEngine");
                foreach (var p in existingProcesses)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(1000); // 等待进程彻底被杀死释放
                    }
                    catch { }
                }

                string tempFolder = Path.GetTempPath();
                _extractedGoEnginePath = Path.Combine(tempFolder, "WebDavEncryptEngine.exe");

                // 删除旧文件（因为上面杀死了进程，这里现在可以100%成功删除了）
                if (File.Exists(_extractedGoEnginePath))
                {
                    try { File.Delete(_extractedGoEnginePath); } catch { }
                }

                if (!File.Exists(_extractedGoEnginePath))
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    string[] allResourceNames = assembly.GetManifestResourceNames();
                    string resourceName = allResourceNames.FirstOrDefault(n => n.EndsWith("WebDavEncryptEngine.exe"));

                    if (string.IsNullOrEmpty(resourceName)) return;

                    using (Stream resourceStream = assembly.GetManifestResourceStream(resourceName))
                    using (FileStream fileStream = new FileStream(_extractedGoEnginePath, FileMode.Create))
                    {
                        resourceStream.CopyTo(fileStream);
                    }
                }

                _goEngineProcess = new Process();
                _goEngineProcess.StartInfo.FileName = _extractedGoEnginePath;
                _goEngineProcess.StartInfo.UseShellExecute = false;
                _goEngineProcess.StartInfo.CreateNoWindow = true;
                _goEngineProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                _goEngineProcess.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"引擎启动失败:\n{ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveTasks();
            base.OnClosed(e);
            if (_goEngineProcess != null && !_goEngineProcess.HasExited) { try { _goEngineProcess.Kill(); } catch { } }
        }

        public void PauseTask(TransferTask task)
        {
            if (task.IsCompleted || task.IsCancelled || task.IsFileMissing) return;
            task.IsPaused = true;
            task.StatusText = "⏸ 已暂停";

            if (_taskTokens.TryGetValue(task.TaskId, out var cts))
            {
                cts.Cancel();
                _taskTokens.Remove(task.TaskId);
            }
            SaveTasks();
        }

        public void ResumeTask(TransferTask task)
        {
            if (task.IsCompleted || task.IsCancelled) return;

            if (task.IsUpload && !File.Exists(task.LocalPath))
            {
                task.IsFileMissing = true;
                task.StatusText = "❌ 找不到本地文件";
                SaveTasks();
                return;
            }

            task.IsPaused = false;
            task.StatusText = "排队中...";

            if (task.IsUpload) _ = ExecuteUploadTask(task);
            else _ = ExecuteDownloadTask(task);

            SaveTasks();
        }

        public void CancelTask(TransferTask task)
        {
            if (task.IsCompleted) return;
            task.IsCancelled = true;
            task.StatusText = "⏹ 已取消";
            task.Percentage = 0;

            if (_taskTokens.TryGetValue(task.TaskId, out var cts))
            {
                cts.Cancel();
                _taskTokens.Remove(task.TaskId);
            }
            SaveTasks();
        }

        public void RetryTask(TransferTask task)
        {
            if (task.IsFileMissing) return;
            task.IsCompleted = false;
            task.IsCancelled = false;
            task.Percentage = 0;
            ResumeTask(task);
        }

        public void DeleteTask(TransferTask task)
        {
            CancelTask(task);
            Application.Current.Dispatcher.Invoke(() => GlobalTasks.Remove(task));
            SaveTasks();
        }

        public void PauseAll(bool isUpload) { foreach (var t in GlobalTasks.Where(t => t.IsUpload == isUpload && !t.IsCompleted && t.StatusText != "❌ 已失效")) PauseTask(t); }
        public void ResumeAll(bool isUpload) { foreach (var t in GlobalTasks.Where(t => t.IsUpload == isUpload && t.IsPaused)) ResumeTask(t); }
        public void CancelAll(bool isUpload) { foreach (var t in GlobalTasks.Where(t => t.IsUpload == isUpload && !t.IsCompleted && t.StatusText != "❌ 已失效")) CancelTask(t); }
        public void RetryAll(bool isUpload) { foreach (var t in GlobalTasks.Where(t => t.IsUpload == isUpload && (t.StatusText.Contains("失败") || t.StatusText.Contains("异常") || t.IsCancelled))) RetryTask(t); }
        public void ClearFailed() { foreach (var t in GlobalTasks.Where(t => t.StatusText.Contains("失效") || t.StatusText.Contains("失败") || t.StatusText.Contains("取消")).ToList()) DeleteTask(t); }
        public void ClearCompleted() { foreach (var t in GlobalTasks.Where(t => t.IsCompleted).ToList()) DeleteTask(t); }

        private async Task EnqueueUploadTask(string fileName, string localFullPath, string targetRemoteDir)
        {
            string taskId = Guid.NewGuid().ToString();
            string remoteFullPath = targetRemoteDir.TrimEnd('/') + "/" + fileName;

            var newTask = new TransferTask
            {
                TaskId = taskId,
                FileName = fileName,
                IsUpload = true,
                LocalPath = localFullPath,
                RemotePath = remoteFullPath,
                TargetRemoteDir = targetRemoteDir,
                Percentage = 0,
                StatusText = "排队中..."
            };
            Application.Current.Dispatcher.Invoke(() => GlobalTasks.Add(newTask));
            SaveTasks();
            await ExecuteUploadTask(newTask);
        }

        private async Task ExecuteUploadTask(TransferTask task)
        {
            await _uploadSemaphore.WaitAsync();
            if (task.IsPaused || task.IsCancelled) { _uploadSemaphore.Release(); return; }

            var cts = new CancellationTokenSource();
            _taskTokens[task.TaskId] = cts;

            try
            {
                task.StatusText = "准备上传...";
                var payload = new { taskId = task.TaskId, localPath = task.LocalPath, remotePath = task.RemotePath, webdavUrl = currentConfig.WebDavUrl, username = currentConfig.Username, password = currentConfig.Password, customKey = ActualCustomKey };
                var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var resp = await httpClient.PostAsync($"{GoEngineApiUrl}/api/upload", content, cts.Token);

                // 🌟 核心排错机制：如果底层返回失败，读取具体死因并弹窗！
                if (!resp.IsSuccessStatusCode)
                {
                    string errorDetail = await resp.Content.ReadAsStringAsync(); // 获取 Go 传回来的真实死因
                    task.StatusText = "❌ 上传失败";

                    Application.Current.Dispatcher.Invoke(() => {
                        MessageBox.Show($"文件 [{task.FileName}] 上传失败！\n\n底层引擎报错信息：\n{errorDetail}", "云端拒绝请求", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
            catch (Exception ex) { task.StatusText = "❌ 发生异常"; }
            finally
            {
                _taskTokens.Remove(task.TaskId);
                _uploadSemaphore.Release();

                if (!task.IsPaused && !task.IsCancelled && !task.StatusText.Contains("失败") && !task.StatusText.Contains("异常") && task.StatusText != "❌ 已失效")
                {
                    task.Percentage = 100; task.StatusText = "✅ 已完成"; task.IsCompleted = true;
                }
                Application.Current.Dispatcher.Invoke(() => { _refreshDebounceTimer.Stop(); _refreshDebounceTimer.Start(); });
            }
        }

        private async Task EnqueueDownloadTask(FileItem remoteItem, string relativeLocalDir, string baseDownloadsFolder)
        {
            string fileName = remoteItem.Name;
            string targetFolder = string.IsNullOrEmpty(relativeLocalDir) ? baseDownloadsFolder : Path.Combine(baseDownloadsFolder, relativeLocalDir.Replace("/", "\\"));
            string finalSavePath = Path.Combine(targetFolder, fileName);

            var newTask = new TransferTask
            {
                TaskId = Guid.NewGuid().ToString(),
                FileName = string.IsNullOrEmpty(relativeLocalDir) ? fileName : $"{relativeLocalDir}/{fileName}",
                IsUpload = false,
                LocalPath = finalSavePath,
                RemotePath = remoteItem.FullPath,
                RemoteSize = remoteItem.Size,
                Percentage = 0,
                StatusText = "排队中..."
            };
            Application.Current.Dispatcher.Invoke(() => GlobalTasks.Add(newTask));
            SaveTasks();
            await ExecuteDownloadTask(newTask);
        }

        private async Task ExecuteDownloadTask(TransferTask task)
        {
            bool overwritePolicy = false;
            Application.Current.Dispatcher.Invoke(() => { overwritePolicy = CmbConflictPolicy.SelectedIndex == 1; });

            long offset = 0;
            if (File.Exists(task.LocalPath))
            {
                long localSize = new FileInfo(task.LocalPath).Length;
                long expectedSize = task.RemoteSize;
                if (!string.IsNullOrEmpty(ActualCustomKey) && task.RemoteSize >= 128) expectedSize = task.RemoteSize - 128;

                if (overwritePolicy) { try { File.Delete(task.LocalPath); } catch { } }
                else
                {
                    if (localSize >= expectedSize)
                    {
                        task.Percentage = 100; task.StatusText = "⏭️ 已跳过 (已完成)"; task.IsCompleted = true;
                        return;
                    }
                    offset = localSize;
                }
            }

            await _downloadSemaphore.WaitAsync();
            if (task.IsPaused || task.IsCancelled) { _downloadSemaphore.Release(); return; }

            var cts = new CancellationTokenSource();
            _taskTokens[task.TaskId] = cts;

            try
            {
                task.StatusText = "正在下载...";
                string targetFolder = Path.GetDirectoryName(task.LocalPath);
                if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);

                var payload = new { taskId = task.TaskId, localPath = task.LocalPath, remotePath = task.RemotePath, webdavUrl = currentConfig.WebDavUrl, username = currentConfig.Username, password = currentConfig.Password, customKey = ActualCustomKey, offset = offset };
                var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var resp = await httpClient.PostAsync($"{GoEngineApiUrl}/api/download", content, cts.Token);
                if (!resp.IsSuccessStatusCode) task.StatusText = "❌ 下载失败";
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
            catch (Exception) { task.StatusText = "❌ 传输异常"; }
            finally
            {
                _taskTokens.Remove(task.TaskId);
                _downloadSemaphore.Release();

                if (!task.IsPaused && !task.IsCancelled && !task.StatusText.Contains("失败") && !task.StatusText.Contains("异常") && task.StatusText != "❌ 已失效")
                {
                    task.Percentage = 100; task.StatusText = "✅ 已完成"; task.IsCompleted = true;
                }
            }
        }

        public class TaskProgress { public long total { get; set; } public long current { get; set; } public double percentage { get; set; } public string status { get; set; } }

        private void SetProgress(bool isWorking, string text) { PrgTask.IsIndeterminate = isWorking; TxtStatus.Text = text; }
        private void BtnSettings_Click(object sender, RoutedEventArgs e) { var win = new SettingsWindow(currentConfig) { Owner = this }; if (win.ShowDialog() == true) { currentConfig = win.Config; _ = LoadRemoteFiles("/"); } }
        private void InitDriveList() { foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady)) ComboDrives.Items.Add(d.Name); if (ComboDrives.Items.Count > 0) ComboDrives.SelectedIndex = 0; }
        private void ComboDrives_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ComboDrives.SelectedItem != null) LoadLocalFiles(ComboDrives.SelectedItem.ToString()); }
        private void LoadLocalFiles(string path) { try { if (!Directory.Exists(path)) return; ListLocal.Items.Clear(); currentLocalPath = path; var di = new DirectoryInfo(path); if (di.Parent != null) ListLocal.Items.Add(new FileItem { Name = "..", FullPath = di.Parent.FullName, IsDirectory = true }); foreach (var d in di.GetDirectories().Where(d => (d.Attributes & FileAttributes.Hidden) == 0)) ListLocal.Items.Add(new FileItem { Name = d.Name, FullPath = d.FullName, IsDirectory = true }); foreach (var f in di.GetFiles()) ListLocal.Items.Add(new FileItem { Name = f.Name, FullPath = f.FullName, IsDirectory = false, Size = f.Length }); if (_localWatcher != null) { _localWatcher.EnableRaisingEvents = false; _localWatcher.Dispose(); } _localWatcher = new FileSystemWatcher(path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite }; FileSystemEventHandler handler = (s, ev) => Dispatcher.Invoke(() => LoadLocalFiles(currentLocalPath)); _localWatcher.Created += handler; _localWatcher.Deleted += handler; _localWatcher.Renamed += (s, ev) => handler(s, null); _localWatcher.EnableRaisingEvents = true; } catch { } }
        private void ListLocal_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ListLocal.SelectedItem is FileItem item && item.IsDirectory) LoadLocalFiles(item.FullPath); }
        private void BtnTransferList_Click(object sender, RoutedEventArgs e) { if (_transferWindow == null) { _transferWindow = new TransferWindow(GlobalTasks); _transferWindow.Owner = this; } _transferWindow.Show(); _transferWindow.Activate(); }

        private async void GlobalProgressTimer_Tick(object sender, EventArgs e)
        {
            var activeTasks = GlobalTasks.Where(t => !t.IsCompleted && !t.IsPaused && !t.IsCancelled).ToList();
            if (activeTasks.Count == 0) { PrgTask.Value = 0; TxtStatus.Text = "就绪"; TxtPercentage.Text = ""; }
            else { var firstTask = activeTasks.First(); TxtStatus.Text = $"正在处理: {firstTask.FileName} (共 {activeTasks.Count} 个)"; PrgTask.Value = firstTask.Percentage; TxtPercentage.Text = $"{firstTask.Percentage:F1}%"; }

            _autoSaveTick++;
            if (_autoSaveTick >= 3)
            {
                SaveTasks();
                _autoSaveTick = 0;
            }

            foreach (var task in activeTasks)
            {
                try
                {
                    var resp = await httpClient.GetStringAsync($"{GoEngineApiUrl}/api/progress?id={task.TaskId}");
                    var progress = JsonSerializer.Deserialize<TaskProgress>(resp);
                    if (progress != null) { task.Percentage = progress.percentage; task.StatusText = $"{progress.percentage:F1}% ({progress.current / 1024 / 1024} MB / {progress.total / 1024 / 1024} MB)"; if (progress.percentage >= 100) { task.IsCompleted = true; task.StatusText = "✅ 已完成"; } }
                }
                catch { }
            }
        }

        private void MenuLocalOpen_Click(object sender, RoutedEventArgs e) { foreach (FileItem item in ListLocal.SelectedItems) Process.Start(new ProcessStartInfo { FileName = item.FullPath, UseShellExecute = true }); }
        private void MenuLocalRename_Click(object sender, RoutedEventArgs e) { if (ListLocal.SelectedItem is FileItem item) { string newName = Interaction.InputBox("新名称:", "重命名", item.Name); if (!string.IsNullOrEmpty(newName) && newName != item.Name) { string newPath = Path.Combine(Path.GetDirectoryName(item.FullPath), newName); if (item.IsDirectory) Directory.Move(item.FullPath, newPath); else File.Move(item.FullPath, newPath); } } }
        private void MenuLocalDelete_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show("确认将选中的项目移入回收站吗？", "确认删除", MessageBoxButton.YesNo) == MessageBoxResult.No) return; foreach (FileItem item in ListLocal.SelectedItems.Cast<FileItem>().ToList()) { if (item.IsDirectory) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); } }

        private async Task LoadRemoteFiles(string path) { if (string.IsNullOrEmpty(currentConfig.Username)) return; SetProgress(true, "读取云端目录..."); try { string davBase = currentConfig.WebDavUrl.TrimEnd('/'); Uri baseUri = new Uri(davBase + "/"); Uri requestUri = new Uri(baseUri, path.TrimStart('/')); var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUri.AbsoluteUri); req.Headers.Add("Depth", "1"); req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}"))); var resp = await httpClient.SendAsync(req); if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 207) { ListRemote.Items.Clear(); currentRemotePath = path; TxtRemotePath.Text = $"☁️ 云端路径: {Uri.UnescapeDataString(path)}"; string xml = await resp.Content.ReadAsStringAsync(); XDocument doc = XDocument.Parse(xml); XNamespace ns = "DAV:"; if (path != "/" && !string.IsNullOrEmpty(path)) { string p = path.TrimEnd('/').LastIndexOf('/') >= 0 ? path.Substring(0, path.TrimEnd('/').LastIndexOf('/') + 1) : "/"; ListRemote.Items.Add(new FileItem { Name = "..", FullPath = p, IsDirectory = true }); } foreach (var res in doc.Descendants(ns + "response")) { string href = Uri.UnescapeDataString(res.Element(ns + "href")?.Value ?? ""); string davAbsolutePath = new Uri(currentConfig.WebDavUrl).AbsolutePath; string rel = href.Substring(href.IndexOf(davAbsolutePath) + davAbsolutePath.Length); if (rel.TrimEnd('/') == path.TrimEnd('/')) continue; long sz = 0; var lenEl = res.Descendants(ns + "getcontentlength").FirstOrDefault(); if (lenEl != null) long.TryParse(lenEl.Value, out sz); ListRemote.Items.Add(new FileItem { Name = rel.TrimEnd('/').Split('/').Last(), FullPath = rel, IsDirectory = res.Descendants(ns + "collection").Any(), Size = sz }); } } else { MessageBox.Show($"无法打开云端文件夹。服务器返回状态码: {resp.StatusCode}", "读取失败"); } } catch (Exception ex) { MessageBox.Show($"读取云端目录异常: {ex.Message}"); } finally { SetProgress(false, "就绪"); } }

        private void BtnUpload_Click(object sender, RoutedEventArgs e) { if (string.IsNullOrEmpty(currentConfig.Username)) { MessageBox.Show("请先配置 WebDAV"); return; } if (ListLocal.SelectedItems.Count == 0) return; string targetRemoteDir = currentRemotePath; bool overwriteConflict = CmbConflictPolicy.SelectedIndex == 1; foreach (FileItem selected in ListLocal.SelectedItems.Cast<FileItem>().ToList()) { if (selected.IsDirectory) { _ = UploadFolderRecursive(selected.FullPath, targetRemoteDir, selected.Name, overwriteConflict); } else { bool isExistRemote = ListRemote.Items.Cast<FileItem>().Any(f => f.Name == selected.Name); if (isExistRemote && !overwriteConflict) { GlobalTasks.Add(new TransferTask { TaskId = Guid.NewGuid().ToString(), FileName = selected.Name, IsUpload = true, Percentage = 100, StatusText = "⏭️ 已跳过 (同名)", IsCompleted = true }); continue; } _ = EnqueueUploadTask(selected.Name, selected.FullPath, targetRemoteDir); } } }
        private async Task CreateRemoteDirectory(string remotePath) { try { string davBase = currentConfig.WebDavUrl.TrimEnd('/'); Uri baseUri = new Uri(davBase + "/"); Uri requestUri = new Uri(baseUri, remotePath.TrimStart('/')); var req = new HttpRequestMessage(new HttpMethod("MKCOL"), requestUri.AbsoluteUri); req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}"))); await httpClient.SendAsync(req); } catch { } }
        private async Task UploadFolderRecursive(string localDirPath, string targetRemoteDir, string relativeDirName, bool overwriteConflict) { try { string currentRemoteFolder = targetRemoteDir.TrimEnd('/') + "/" + relativeDirName; await CreateRemoteDirectory(currentRemoteFolder); HashSet<string> existingFiles = new HashSet<string>(); if (!overwriteConflict) { try { string davBase = currentConfig.WebDavUrl.TrimEnd('/'); Uri baseUri = new Uri(davBase + "/"); Uri requestUri = new Uri(baseUri, currentRemoteFolder.TrimStart('/')); var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUri.AbsoluteUri); req.Headers.Add("Depth", "1"); req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}"))); var resp = await httpClient.SendAsync(req); if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 207) { string xml = await resp.Content.ReadAsStringAsync(); XDocument doc = XDocument.Parse(xml); XNamespace ns = "DAV:"; foreach (var res in doc.Descendants(ns + "response")) { string href = Uri.UnescapeDataString(res.Element(ns + "href")?.Value ?? ""); string itemName = href.TrimEnd('/').Split('/').Last(); bool isDir = res.Descendants(ns + "collection").Any(); if (!isDir && !string.IsNullOrEmpty(itemName)) { existingFiles.Add(itemName); } } } } catch { } } string[] files = Directory.GetFiles(localDirPath); foreach (string file in files) { string fileName = Path.GetFileName(file); if (!overwriteConflict && existingFiles.Contains(fileName)) { Application.Current.Dispatcher.Invoke(() => { GlobalTasks.Add(new TransferTask { TaskId = Guid.NewGuid().ToString(), FileName = fileName, IsUpload = true, Percentage = 100, StatusText = "⏭️ 已跳过 (同名)", IsCompleted = true }); }); continue; } _ = EnqueueUploadTask(fileName, file, currentRemoteFolder); } string[] subDirs = Directory.GetDirectories(localDirPath); foreach (string subDir in subDirs) { string dirName = Path.GetFileName(subDir); await UploadFolderRecursive(subDir, currentRemoteFolder, dirName, overwriteConflict); } } catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => { MessageBox.Show($"读取本地文件夹 {localDirPath} 失败: {ex.Message}"); }); } }

        private void ListRemote_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ListRemote.SelectedItem is FileItem item) HandleRemoteItem(item); }

        // 🌟 拦截：打开预览时检查密钥
        private async void HandleRemoteItem(FileItem item)
        {
            if (item.IsDirectory) { await LoadRemoteFiles(item.FullPath); return; }

            if (!CheckCustomKeyBeforeAction()) return;

            string ext = Path.GetExtension(item.Name).ToLower();
            string[] vids = { ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv" };
            SetProgress(true, "请求引擎处理...");
            if (Array.Exists(vids, x => x == ext)) { string url = $"{GoEngineApiUrl}/api/stream?path={Uri.EscapeDataString(item.FullPath)}&url={Uri.EscapeDataString(currentConfig.WebDavUrl)}&user={Uri.EscapeDataString(currentConfig.Username)}&pass={Uri.EscapeDataString(currentConfig.Password)}&key={Uri.EscapeDataString(ActualCustomKey)}"; Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); SetProgress(false, "就绪"); } else { await PreviewRemoteFile(item); }
        }

        private void MenuRemoteOpen_Click(object sender, RoutedEventArgs e) { if (ListRemote.SelectedItem is FileItem item) HandleRemoteItem(item); }

        private async Task PreviewRemoteFile(FileItem item) { SetProgress(true, $"正在准备预览 {item.Name}..."); try { string tempPath = Path.Combine(Path.GetTempPath(), item.Name); var payload = new { localPath = tempPath, remotePath = item.FullPath, webdavUrl = currentConfig.WebDavUrl, username = currentConfig.Username, password = currentConfig.Password, customKey = ActualCustomKey }; var resp = await httpClient.PostAsync($"{GoEngineApiUrl}/api/download", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")); if (resp.IsSuccessStatusCode) { Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true }); } else MessageBox.Show("解密预览失败！"); } catch (Exception ex) { MessageBox.Show($"预览异常: {ex.Message}"); } finally { SetProgress(false, "就绪"); } }

        // 🌟 拦截：下载时检查密钥
        private void MenuRemoteDownload_Click(object sender, RoutedEventArgs e)
        {
            if (ListRemote.SelectedItems.Count == 0) return;

            if (!CheckCustomKeyBeforeAction()) return;

            string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); string baseDownloadsFolder = Path.Combine(userProfilePath, "Downloads"); foreach (FileItem item in ListRemote.SelectedItems.Cast<FileItem>().ToList()) { if (item.IsDirectory) { _ = DownloadFolderRecursive(item.FullPath, item.Name, baseDownloadsFolder); } else { _ = EnqueueDownloadTask(item, "", baseDownloadsFolder); } }
        }

        private async Task DownloadFolderRecursive(string remoteDirPath, string relativeDir, string baseLocalFolder) { try { string davBase = currentConfig.WebDavUrl.TrimEnd('/'); Uri baseUri = new Uri(davBase + "/"); Uri requestUri = new Uri(baseUri, remoteDirPath.TrimStart('/')); var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUri.AbsoluteUri); req.Headers.Add("Depth", "1"); req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}"))); var resp = await httpClient.SendAsync(req); if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 207) { string xml = await resp.Content.ReadAsStringAsync(); XDocument doc = XDocument.Parse(xml); XNamespace ns = "DAV:"; foreach (var res in doc.Descendants(ns + "response")) { string href = Uri.UnescapeDataString(res.Element(ns + "href")?.Value ?? ""); string davAbsolutePath = new Uri(currentConfig.WebDavUrl).AbsolutePath; string relPath = href.Substring(href.IndexOf(davAbsolutePath) + davAbsolutePath.Length); if (relPath.TrimEnd('/') == remoteDirPath.TrimEnd('/')) continue; string itemName = relPath.TrimEnd('/').Split('/').Last(); bool isDir = res.Descendants(ns + "collection").Any(); long sz = 0; var lenEl = res.Descendants(ns + "getcontentlength").FirstOrDefault(); if (lenEl != null) long.TryParse(lenEl.Value, out sz); FileItem currentItem = new FileItem { Name = itemName, FullPath = relPath, IsDirectory = isDir, Size = sz }; if (isDir) { await DownloadFolderRecursive(relPath, relativeDir + "/" + itemName, baseLocalFolder); } else { _ = EnqueueDownloadTask(currentItem, relativeDir, baseLocalFolder); } } } } catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => { MessageBox.Show($"解析文件夹 {remoteDirPath} 失败: {ex.Message}"); }); } }

        private void MenuLocalUpload_Click(object sender, RoutedEventArgs e) { BtnUpload_Click(null, null); }
        private async void MenuRemoteRename_Click(object sender, RoutedEventArgs e) { if (!(ListRemote.SelectedItem is FileItem item)) return; string newName = Interaction.InputBox("请输入新名称:", "重命名", item.Name); if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return; SetProgress(true, "正在重命名..."); try { string davBase = currentConfig.WebDavUrl.TrimEnd('/'); Uri baseUri = new Uri(davBase + "/"); Uri sourceUri = new Uri(baseUri, item.FullPath.TrimStart('/')); string destRelPath = currentRemotePath.TrimEnd('/').TrimStart('/') + "/" + newName.TrimStart('/'); Uri destUri = new Uri(baseUri, destRelPath); var req = new HttpRequestMessage(new HttpMethod("MOVE"), sourceUri.AbsoluteUri); req.Headers.Add("Destination", destUri.AbsoluteUri); req.Headers.Add("Overwrite", "F"); req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}"))); var resp = await httpClient.SendAsync(req); if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 201 || (int)resp.StatusCode == 204) await LoadRemoteFiles(currentRemotePath); else MessageBox.Show($"重命名失败: {resp.StatusCode}"); } catch (Exception ex) { MessageBox.Show($"重命名异常: {ex.Message}"); } finally { SetProgress(false, "就绪"); } }
        private async void MenuRemoteMove_Click(object sender, RoutedEventArgs e) { if (!(ListRemote.SelectedItem is FileItem item)) return; var folderWin = new RemoteFolderSelectWindow(currentConfig, "/"); folderWin.Owner = this; if (folderWin.ShowDialog() == true) { string targetDir = folderWin.SelectedPath; if (targetDir.TrimEnd('/') == currentRemotePath.TrimEnd('/')) return; SetProgress(true, "正在移动文件..."); try { string davBase = currentConfig.WebDavUrl.TrimEnd('/'); Uri baseUri = new Uri(davBase + "/"); Uri sourceUri = new Uri(baseUri, item.FullPath.TrimStart('/')); string destRelPath = targetDir.TrimEnd('/').TrimStart('/') + "/" + item.Name.TrimStart('/'); Uri destUri = new Uri(baseUri, destRelPath); var req = new HttpRequestMessage(new HttpMethod("MOVE"), sourceUri.AbsoluteUri); req.Headers.Add("Destination", destUri.AbsoluteUri); req.Headers.Add("Overwrite", "F"); req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}"))); var resp = await httpClient.SendAsync(req); if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 201 || (int)resp.StatusCode == 204) { await LoadRemoteFiles(currentRemotePath); MessageBox.Show($"成功移动到: {targetDir}"); } else MessageBox.Show($"移动失败: {resp.StatusCode}"); } catch (Exception ex) { MessageBox.Show($"移动异常: {ex.Message}"); } finally { SetProgress(false, "就绪"); } } }
        private async void MenuRemoteDelete_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show("确认要从云端彻底删除选中的文件吗？此操作不可恢复！", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No) return; SetProgress(true, "正在删除..."); try { foreach (FileItem item in ListRemote.SelectedItems.Cast<FileItem>().ToList()) { string davBase = currentConfig.WebDavUrl.TrimEnd('/'); Uri deleteUri = new Uri(new Uri(davBase + "/"), item.FullPath.TrimStart('/')); var req = new HttpRequestMessage(HttpMethod.Delete, deleteUri.AbsoluteUri); req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}"))); await httpClient.SendAsync(req); } await LoadRemoteFiles(currentRemotePath); } catch (Exception ex) { MessageBox.Show($"删除发生异常: {ex.Message}"); } finally { SetProgress(false, "就绪"); } }
        private void BtnConfirmKey_Click(object sender, RoutedEventArgs e) { if (TxtCustomKeyVisible.Visibility == Visibility.Visible) { string key = TxtCustomKeyVisible.Text.Trim(); if (string.IsNullOrEmpty(key)) return; ActualCustomKey = key; TxtCustomKeyHidden.Password = key; TxtCustomKeyVisible.Visibility = Visibility.Collapsed; TxtCustomKeyHidden.Visibility = Visibility.Visible; BtnConfirmKey.Content = "修改"; } else { TxtCustomKeyVisible.Text = ActualCustomKey; TxtCustomKeyHidden.Visibility = Visibility.Collapsed; TxtCustomKeyVisible.Visibility = Visibility.Visible; BtnConfirmKey.Content = "确定"; } }
        private void BtnSyncDrive_Click(object sender, RoutedEventArgs e) { if (_syncDriveWindow == null || !_syncDriveWindow.IsLoaded) { _syncDriveWindow = new SyncDriveWindow(); _syncDriveWindow.Owner = this; } _syncDriveWindow.Show(); _syncDriveWindow.Activate(); }
    }
}