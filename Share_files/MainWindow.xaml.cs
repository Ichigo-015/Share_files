using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Net.Sockets;
using System.IO;

namespace Share_files
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class MainWindow : Window
    {
        private TcpClient ftpClient;
        private const int ftpPort = 1145; // FTPのデフォルトポート
        public MainWindow()
        {
            InitializeComponent();
        }

        private async void btnDownload_Click(object sender, RoutedEventArgs e)
        {
            
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            string targetIP = Ftp_Server_IP.ToString();//FTPサーバーのIPアドレスを取得

            Connection_Statement.Text = "Connecting...";//接続状況を表示

            try
            {
                ftpClient = new TcpClient(targetIP, ftpPort);//新しいTcpClientを作成

                await ftpClient.ConnectAsync(targetIP, ftpPort);//FTPサーバーに接続

                Connection_Statement.Text = "Connected";
                Connection_Statement.Foreground = Brushes.Green;
            }
            catch
            {
                ftpClient = null;
                Connection_Statement.Text = "Connection Failed";
                Connection_Statement.Foreground = Brushes.Red;
            }

            //var filePath = @"C:\Users\Public\Documents\sample.txt";
            //System.IO.File.WriteAllText(filePath, "This is a sample file.");
            //MessageBox.Show($"File created at: {filePath}");
        }

        private async void btnUpload_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Title = "Select a file to share";
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                NetworkStream stream = ftpClient.GetStream();

                var writer = new BinaryWriter(stream, Encoding.UTF8);

                string fileName = System.IO.Path.GetFileName(dialog.FileName);
                writer.Write(fileName);

                using (var fileStream = File.OpenRead(dialog.FileName))
                {
                    await fileStream.CopyToAsync(stream);
                }

                await stream.FlushAsync();

            }
            catch (Exception ex)
            {
                ftpClient.Close();
                ftpClient = null;
            }
        }
    }
}
