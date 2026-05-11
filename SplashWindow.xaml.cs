using System.Threading.Tasks;
using System.Windows;

namespace WebDavEncryptManager
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
        }

        // 用于更新界面上的文字
        public void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() => TxtStatus.Text = message);
        }
    }
}