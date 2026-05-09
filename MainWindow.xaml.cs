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
            SetProgress(true, "正在加密上传...");
            foreach (FileItem selected in ListLocal.SelectedItems)
            {
                if (selected.IsDirectory) continue;
                var payload = new { localPath = selected.FullPath, remotePath = currentRemotePath.TrimEnd('/') + "/" + selected.Name, webdavUrl = currentConfig.WebDavUrl, username = currentConfig.Username, password = currentConfig.Password, customKey = TxtCustomKey.Text.Trim() };
                await httpClient.PostAsync($"{GoEngineApiUrl}/api/upload", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            }
            SetProgress(false, "上传完毕");
            await LoadRemoteFiles(currentRemotePath);
        }

        private void ListRemote_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ListRemote.SelectedItem is FileItem item) HandleRemoteItem(item); }
        private void MenuRemoteOpen_Click(object sender, RoutedEventArgs e) { if (ListRemote.SelectedItem is FileItem item) HandleRemoteItem(item); }

        // 🌟 新增缺失的下载菜单点击事件
        private async void MenuRemoteDownload_Click(object sender, RoutedEventArgs e)
        {
            foreach (FileItem item in ListRemote.SelectedItems)
            {
                if (!item.IsDirectory) await DownloadAndOpenFile(item);
            }
        }

        private async void HandleRemoteItem(FileItem item)
        {
            if (item.IsDirectory) { await LoadRemoteFiles(item.FullPath); return; }

            string ext = Path.GetExtension(item.Name).ToLower();
            string[] vids = { ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv" };
            SetProgress(true, "请求引擎处理...");

            if (Array.Exists(vids, x => x == ext))
            {
                string url = $"{GoEngineApiUrl}/api/stream?path={Uri.EscapeDataString(item.FullPath)}&url={Uri.EscapeDataString(currentConfig.WebDavUrl)}&user={Uri.EscapeDataString(currentConfig.Username)}&pass={Uri.EscapeDataString(currentConfig.Password)}&key={Uri.EscapeDataString(TxtCustomKey.Text.Trim())}";
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                SetProgress(false, "就绪");
            }
            else
            {
                await DownloadAndOpenFile(item);
            }
        }

        // 将非视频文件的下载提取出来复用
        // 将非视频文件的下载提取出来复用，并修改为下载到系统“下载”文件夹
        private async Task DownloadAndOpenFile(FileItem item)
        {
            SetProgress(true, $"正在下载解密 {item.Name}...");
            try
            {
                // 🌟 1. 获取当前 Windows 用户的“下载”文件夹路径
                string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string downloadsFolder = Path.Combine(userProfilePath, "Downloads");

                // 确保“下载”文件夹存在（绝大多数情况存在，以防万一）
                if (!Directory.Exists(downloadsFolder))
                {
                    Directory.CreateDirectory(downloadsFolder);
                }

                // 🌟 2. 拼接最终的保存路径
                string finalSavePath = Path.Combine(downloadsFolder, item.Name);

                // 构造发给 Go 引擎的请求体，将 localPath 设置为最终保存路径
                var payload = new
                {
                    localPath = finalSavePath,
                    remotePath = item.FullPath,
                    webdavUrl = currentConfig.WebDavUrl,
                    username = currentConfig.Username,
                    password = currentConfig.Password,
                    customKey = TxtCustomKey.Text.Trim()
                };

                var resp = await httpClient.PostAsync($"{GoEngineApiUrl}/api/download", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    // 🌟 3. 下载成功后，打开资源管理器并选中该文件
                    Process.Start("explorer.exe", $"/select,\"{finalSavePath}\"");
                    SetProgress(false, "下载完成");
                }
                else
                {
                    MessageBox.Show("解密下载失败！请检查网络或后端引擎。");
                    SetProgress(false, "下载失败");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下载异常: {ex.Message}");
                SetProgress(false, "就绪");
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
    }
}