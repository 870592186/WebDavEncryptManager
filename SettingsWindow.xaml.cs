using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace WebDavEncryptManager
{
    public partial class SettingsWindow : Window
    {
        public AppConfig Config { get; private set; }
        public SettingsWindow(AppConfig currentConfig)
        {
            InitializeComponent();
            TxtUrl.Text = currentConfig.WebDavUrl; TxtUser.Text = currentConfig.Username; TxtPass.Password = currentConfig.Password;
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtUrl.Text.Trim(), user = TxtUser.Text.Trim(), pass = TxtPass.Password.Trim();
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) return;

            TxtStatus.Text = "验证中..."; TxtStatus.Foreground = Brushes.Orange;
            try
            {
                using (var client = new HttpClient())
                {
                    var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
                    req.Headers.Add("Depth", "0");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
                    var resp = await client.SendAsync(req);

                    if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 207)
                    {
                        Config = new AppConfig { WebDavUrl = url, Username = user, Password = pass };
                        HardwareCryptoHelper.SaveEncryptedConfig(Config, "config.dat");
                        this.DialogResult = true;
                    }
                    else { TxtStatus.Text = "验证失败"; TxtStatus.Foreground = Brushes.Red; }
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); TxtStatus.Text = "网络错误"; }
        }
    }
}