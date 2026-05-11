#nullable disable

using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using Microsoft.VisualBasic; // 🌟 引入用于弹出输入框的库

namespace WebDavEncryptManager
{
    // 用于列表展示的简易文件夹模型
    public class FolderItem { public string Name { get; set; } public string FullPath { get; set; } }

    public partial class RemoteFolderSelectWindow : Window
    {
        public string SelectedPath { get; private set; }
        private string currentPath;
        private AppConfig config;
        private HttpClient httpClient = new HttpClient();

        public RemoteFolderSelectWindow(AppConfig appConfig, string startPath)
        {
            InitializeComponent();
            config = appConfig;
            currentPath = startPath;
            _ = LoadFolders(currentPath);
        }

        private async Task LoadFolders(string path)
        {
            try
            {
                TxtCurrentPath.Text = $"📂 当前路径: {Uri.UnescapeDataString(path)}";
                ListFolders.Items.Clear();
                currentPath = path;

                string davBase = config.WebDavUrl.TrimEnd('/');
                Uri requestUri = new Uri(new Uri(davBase + "/"), path.TrimStart('/'));

                var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUri.AbsoluteUri);
                req.Headers.Add("Depth", "1");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}")));

                var resp = await httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 207)
                {
                    XDocument doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
                    XNamespace ns = "DAV:";

                    // 添加返回上一级功能
                    if (path != "/" && !string.IsNullOrEmpty(path))
                    {
                        string p = path.TrimEnd('/').LastIndexOf('/') >= 0 ? path.Substring(0, path.TrimEnd('/').LastIndexOf('/') + 1) : "/";
                        ListFolders.Items.Add(new FolderItem { Name = "📁 .. (返回上一级)", FullPath = p });
                    }

                    foreach (var res in doc.Descendants(ns + "response"))
                    {
                        // 仅提取文件夹 (包含 collection 标签的)
                        if (!res.Descendants(ns + "collection").Any()) continue;

                        string href = Uri.UnescapeDataString(res.Element(ns + "href")?.Value ?? "");
                        string davAbsolutePath = new Uri(config.WebDavUrl).AbsolutePath;
                        string rel = href.Substring(href.IndexOf(davAbsolutePath) + davAbsolutePath.Length);

                        if (rel.TrimEnd('/') == path.TrimEnd('/')) continue; // 排除自己

                        string name = rel.TrimEnd('/').Split('/').Last();
                        ListFolders.Items.Add(new FolderItem { Name = "📁 " + name, FullPath = rel });
                    }
                }
            }
            catch { MessageBox.Show("加载目录失败，请检查网络。"); }
        }

        private void ListFolders_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListFolders.SelectedItem is FolderItem item)
                _ = LoadFolders(item.FullPath); // 双击进入文件夹
        }

        // ==========================================
        // 🌟 核心新增：向 WebDAV 发送新建文件夹指令
        // ==========================================
        private async void BtnNewFolder_Click(object sender, RoutedEventArgs e)
        {
            // 弹出一个原生输入框让用户输入文件夹名
            string folderName = Interaction.InputBox("请输入新文件夹名称:", "新建云端文件夹", "新建文件夹");
            if (string.IsNullOrWhiteSpace(folderName)) return;

            try
            {
                // 拼接出新文件夹的完整云端路径
                string targetPath = currentPath.TrimEnd('/') + "/" + folderName.Trim();
                Uri requestUri = new Uri(new Uri(config.WebDavUrl.TrimEnd('/') + "/"), targetPath.TrimStart('/'));

                // 使用 MKCOL 协议命令要求服务器创建文件夹
                var req = new HttpRequestMessage(new HttpMethod("MKCOL"), requestUri.AbsoluteUri);
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}")));

                var resp = await httpClient.SendAsync(req);

                // 201 Created 代表创建成功
                if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 201)
                {
                    // 创建成功后立刻刷新列表
                    _ = LoadFolders(currentPath);
                }
                else
                {
                    MessageBox.Show($"新建文件夹失败，服务器返回状态码: {resp.StatusCode}");
                }
            }
            catch { MessageBox.Show("新建文件夹异常，请检查网络或账号权限。"); }
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e)
        {
            SelectedPath = currentPath; // 用户确认选择当前所在的路径
            this.DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => this.DialogResult = false;
    }
}