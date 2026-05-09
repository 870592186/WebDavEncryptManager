using System;
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

namespace WebDavEncryptManager
{
    public partial class MainWindow : Window
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private AppConfig currentConfig = new AppConfig();
        private string currentLocalPath = "";
        private string currentRemotePath = "/";
        private FileSystemWatcher _localWatcher;
        private readonly string GoEngineApiUrl = "http://127.0.0.1:8888";
        private System.Windows.Threading.DispatcherTimer _progressTimer;
        private string _currentTaskId;
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

            // 读取硬件绑定的加密配置
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
                // 🌟 使用 Uri 确保带有中文的路径被正确编码，解决打不开中文文件夹的问题
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
        private async void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentConfig.Username)) { MessageBox.Show("请先配置 WebDAV"); return; }

            foreach (FileItem selected in ListLocal.SelectedItems)
            {
                if (selected.IsDirectory) continue;

                // 🌟 1. 生成独一无二的 TaskID
                string taskId = Guid.NewGuid().ToString();

                // 🌟 2. 启动进度追踪器
                StartProgressTracker(taskId, $"正在加密上传 {selected.Name}...");

                try
                {
                    var payload = new
                    {
                        taskId = taskId, // 发送给 Go
                        localPath = selected.FullPath,
                        remotePath = currentRemotePath.TrimEnd('/') + "/" + selected.Name,
                        webdavUrl = currentConfig.WebDavUrl,
                        username = currentConfig.Username,
                        password = currentConfig.Password,
                        customKey = TxtCustomKeyVisible.Text.Trim() // 使用前面确定的密钥变量
                    };

                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    await httpClient.PostAsync($"{GoEngineApiUrl}/api/upload", content);
                }
                finally
                {
                    // 🌟 3. 任务结束（无论成功或失败），停止追踪器
                    StopProgressTracker();
                }
            }
            await LoadRemoteFiles(currentRemotePath); // 刷新列表
        }

        private void ListRemote_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ListRemote.SelectedItem is FileItem item) HandleRemoteItem(item); }
        private void MenuRemoteOpen_Click(object sender, RoutedEventArgs e) { if (ListRemote.SelectedItem is FileItem item) HandleRemoteItem(item); }

        // 🌟 新增缺失的下载菜单点击事件
        // ==========================================
        // 核心拆分 1：右键下载菜单事件
        // ==========================================
        private async void MenuRemoteDownload_Click(object sender, RoutedEventArgs e)
        {
            foreach (FileItem item in ListRemote.SelectedItems)
            {
                // 触发正式下载
                if (!item.IsDirectory) await DownloadRemoteFile(item);
            }
        }

        // ==========================================
        // 核心拆分 2：双击/打开菜单事件路由
        // ==========================================
        private async void HandleRemoteItem(FileItem item)
        {
            if (item.IsDirectory) { await LoadRemoteFiles(item.FullPath); return; }

            string ext = Path.GetExtension(item.Name).ToLower();
            string[] vids = { ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv" };
            SetProgress(true, "请求引擎处理...");

            if (Array.Exists(vids, x => x == ext))
            {
                // 视频文件走流式播放
                string url = $"{GoEngineApiUrl}/api/stream?path={Uri.EscapeDataString(item.FullPath)}&url={Uri.EscapeDataString(currentConfig.WebDavUrl)}&user={Uri.EscapeDataString(currentConfig.Username)}&pass={Uri.EscapeDataString(currentConfig.Password)}&key={Uri.EscapeDataString(ActualCustomKey)}";
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                SetProgress(false, "就绪");
            }
            else
            {
                // 🌟 修复：非视频文件，走临时预览逻辑
                await PreviewRemoteFile(item);
            }
        }

        // ==========================================
        // 核心拆分 3：临时预览逻辑 (解密到 Temp 目录并直接打开)
        // ==========================================
        private async Task PreviewRemoteFile(FileItem item)
        {
            SetProgress(true, $"正在准备预览 {item.Name}...");
            try
            {
                // 使用系统临时文件夹
                string tempPath = Path.Combine(Path.GetTempPath(), item.Name);
                var payload = new { localPath = tempPath, remotePath = item.FullPath, webdavUrl = currentConfig.WebDavUrl, username = currentConfig.Username, password = currentConfig.Password, customKey = ActualCustomKey };
                var resp = await httpClient.PostAsync($"{GoEngineApiUrl}/api/download", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    // 预览成功后，直接运行该文件
                    Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true });
                }
                else MessageBox.Show("解密预览失败！");
            }
            catch (Exception ex) { MessageBox.Show($"预览异常: {ex.Message}"); }
            finally { SetProgress(false, "就绪"); }
        }

        // ==========================================
        // 核心拆分 4：正式下载逻辑 (解密到 Downloads 目录并打开文件夹)
        // ==========================================
        // ==========================================
        // 正式下载逻辑 (解密到 Downloads 目录并打开文件夹)
        // ==========================================
        private async Task DownloadRemoteFile(FileItem item)
        {
            // 🌟 1. 为本次下载生成独一无二的 TaskID
            string taskId = Guid.NewGuid().ToString();

            // 🌟 2. 启动进度追踪器 (替代原来的 SetProgress)
            StartProgressTracker(taskId, $"正在下载解密 {item.Name}...");

            try
            {
                // 获取系统“下载”文件夹
                string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string downloadsFolder = Path.Combine(userProfilePath, "Downloads");
                if (!Directory.Exists(downloadsFolder)) Directory.CreateDirectory(downloadsFolder);

                string finalSavePath = Path.Combine(downloadsFolder, item.Name);

                // 🌟 3. 在发给 Go 引擎的 JSON 数据中加入 taskId
                var payload = new
                {
                    taskId = taskId, // 发送给 Go 用于绑定进度
                    localPath = finalSavePath,
                    remotePath = item.FullPath,
                    webdavUrl = currentConfig.WebDavUrl,
                    username = currentConfig.Username,
                    password = currentConfig.Password,
                    customKey = TxtCustomKeyVisible.Text.Trim() // 确保使用正确的密钥变量
                };

                var resp = await httpClient.PostAsync($"{GoEngineApiUrl}/api/download", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    // 下载成功后，打开资源管理器并高亮选中文件
                    Process.Start("explorer.exe", $"/select,\"{finalSavePath}\"");
                }
                else
                {
                    MessageBox.Show("解密下载失败！请检查网络或后端引擎。");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下载异常: {ex.Message}");
            }
            finally
            {
                // 🌟 4. 任务结束（无论成功或失败），停止追踪器并恢复 UI
                StopProgressTracker();
            }
        }

        // ==========================================
        // 🌟 新增/恢复的右键菜单功能
        // ==========================================

        // 左侧：右键加密上传（直接调用之前写好的 BtnUpload_Click 逻辑）
        private void MenuLocalUpload_Click(object sender, RoutedEventArgs e)
        {
            BtnUpload_Click(null, null);
        }

        // 右侧：仅重命名功能 (不需要输入完整路径了，只改名字)
        private async void MenuRemoteRename_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListRemote.SelectedItem is FileItem item)) return;

            // 只需要输入新名字
            string newName = Microsoft.VisualBasic.Interaction.InputBox("请输入新名称:", "重命名", item.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;

            SetProgress(true, "正在重命名...");
            try
            {
                string davBase = currentConfig.WebDavUrl.TrimEnd('/');
                Uri baseUri = new Uri(davBase + "/");
                Uri sourceUri = new Uri(baseUri, item.FullPath.TrimStart('/'));

                // 目标路径依然是当前目录，只是名字变了
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

        // 右侧：唤出新的【可视化目录选择器】进行移动
        private async void MenuRemoteMove_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListRemote.SelectedItem is FileItem item)) return;

            // 打开我们刚刚写好的目录选择窗口
            var folderWin = new RemoteFolderSelectWindow(currentConfig, "/");
            folderWin.Owner = this;

            if (folderWin.ShowDialog() == true)
            {
                // 获取用户选择的目录路径
                string targetDir = folderWin.SelectedPath;
                if (targetDir.TrimEnd('/') == currentRemotePath.TrimEnd('/')) return; // 没变动就不移动

                SetProgress(true, "正在移动文件...");
                try
                {
                    string davBase = currentConfig.WebDavUrl.TrimEnd('/');
                    Uri baseUri = new Uri(davBase + "/");
                    Uri sourceUri = new Uri(baseUri, item.FullPath.TrimStart('/'));

                    // 目标路径 = 用户选的目录 + 原文件名
                    string destRelPath = targetDir.TrimEnd('/').TrimStart('/') + "/" + item.Name.TrimStart('/');
                    Uri destUri = new Uri(baseUri, destRelPath);

                    var req = new HttpRequestMessage(new HttpMethod("MOVE"), sourceUri.AbsoluteUri);
                    req.Headers.Add("Destination", destUri.AbsoluteUri);
                    req.Headers.Add("Overwrite", "F");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{currentConfig.Username}:{currentConfig.Password}")));

                    var resp = await httpClient.SendAsync(req);
                    if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 201 || (int)resp.StatusCode == 204)
                    {
                        await LoadRemoteFiles(currentRemotePath); // 刷新当前列表，文件应该消失了
                        MessageBox.Show($"成功移动到: {targetDir}");
                    }
                    else MessageBox.Show($"移动失败: {resp.StatusCode}");
                }
                catch (Exception ex) { MessageBox.Show($"移动异常: {ex.Message}"); }
                finally { SetProgress(false, "就绪"); }
            }
        }

        // 🌟 修复：写入真正的云端删除逻辑
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

        // 在 MainWindow 类中定义一个变量保存最终的密钥
        private string ActualCustomKey = "";

        // 处理确定/修改按钮的点击
        private void BtnConfirmKey_Click(object sender, RoutedEventArgs e)
        {
            if (TxtCustomKeyVisible.Visibility == Visibility.Visible)
            {
                // 动作：锁定并隐藏
                string key = TxtCustomKeyVisible.Text.Trim();
                if (string.IsNullOrEmpty(key)) return;

                ActualCustomKey = key;
                TxtCustomKeyHidden.Password = key; // 同步给密码框显示星号

                TxtCustomKeyVisible.Visibility = Visibility.Collapsed;
                TxtCustomKeyHidden.Visibility = Visibility.Visible;
                BtnConfirmKey.Content = "修改";
            }
            else
            {
                // 动作：解锁并修改
                TxtCustomKeyVisible.Text = ActualCustomKey;

                TxtCustomKeyHidden.Visibility = Visibility.Collapsed;
                TxtCustomKeyVisible.Visibility = Visibility.Visible;
                BtnConfirmKey.Content = "确定";
            }
        }
        // ==========================================
        // 🌟 进度条轮询控制器
        // ==========================================
        private void StartProgressTracker(string taskId, string taskName)
        {
            _currentTaskId = taskId;
            PrgTask.Value = 0;
            PrgTask.IsIndeterminate = false; // 明确关闭无限循环动画
            TxtStatus.Text = taskName;
            TxtPercentage.Text = "0.0%";

            _progressTimer = new System.Windows.Threading.DispatcherTimer();
            _progressTimer.Interval = TimeSpan.FromMilliseconds(500); // 每 0.5 秒查询一次
            _progressTimer.Tick += async (s, e) =>
            {
                try
                {
                    // 向 Go 核心请求最新进度
                    var resp = await httpClient.GetStringAsync($"{GoEngineApiUrl}/api/progress?id={_currentTaskId}");
                    var progress = JsonSerializer.Deserialize<TaskProgress>(resp);

                    if (progress != null)
                    {
                        // 更新 UI
                        PrgTask.Value = progress.percentage;
                        TxtPercentage.Text = $"{progress.percentage:F1}%"; // F1 表示保留一位小数
                    }
                }
                catch
                {
                    // 忽略轮询期间偶发的网络抖动，防止程序崩溃
                }
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
            PrgTask.Value = 100; // 强制填满
            TxtPercentage.Text = "100%";
            TxtStatus.Text = "任务完成";

            // 延迟 2 秒后恢复初始状态
            Task.Delay(2000).ContinueWith(t => Dispatcher.Invoke(() =>
            {
                PrgTask.Value = 0;
                TxtPercentage.Text = "";
                TxtStatus.Text = "就绪";
            }));
        }


    }
}