using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
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


namespace Share_files
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class MainWindow : Window
    {
        private TcpClient ftpClient;
        private TcpListener tcpListener;
        private CancellationTokenSource listenerCts;
        private const int ftpPort = 1145; // FTPのデフォルトポート
        string saveDir;
        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;

            // 2. 画面に対する「比例（％）」を決めて計算する
            // 例：画面の横幅の 50%、高さの 60% の大きさにしたい場合
            this.Width = screenWidth * 0.4;
            this.Height = screenHeight * 0.4;

            // 💡【おまけ】サイズを変えた後、画面の「真ん中」に配置し直す
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _ = StartListeningAsync();
        }

        private async Task StartListeningAsync()
        {
            // ② 保存先フォルダを作成
            saveDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "SharedFiles");
            Directory.CreateDirectory(saveDir);

            SaveFileTo.Text = $"{saveDir}";
        }

        private async Task HandleIncomingClientAsync(TcpClient client)
        {
            string clientIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();

            await Task.Run(async () =>
            {
                try
                {
                    using (client)
                    using (NetworkStream stream = client.GetStream())
                    {
                        // ① ファイル名を受信
                        var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                        int fileCount = reader.ReadInt32();//最初に、クライアントから送られてくる「ファイルの総数」を受け取ります
                        
                        List<string> receivedFiles = new List<string>();


                        //string fileName = reader.ReadString();

                        for (int i = 0; i < fileCount; i++)
                        {
                            string fileName = reader.ReadString();
                            long fileSize = reader.ReadInt64();

                            // ③ ファイルデータを受信して保存
                            string savePath = System.IO.Path.Combine(saveDir, fileName);
                            using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write))
                            {
                                long bytesReadTotal = 0;
                                byte[] buffer = new byte[8092];

                                while (bytesReadTotal < fileSize)
                                {
                                    // 残りの未読バイト数とバッファサイズ(8192)の、小さいほうのサイズ分だけ読み込む
                                    int bytesToRead = (int)Math.Min(buffer.Length, fileSize - bytesReadTotal);
                                    int bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead);

                                    if (bytesRead == 0)
                                        throw new Exception("通信が途中で切断されました。");

                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    bytesReadTotal += bytesRead; // 読んだ総バイト数を足していく
                                }
                            }
                            // 💡 【追加】無事に1個保存できたら、ファイル名をリストに記録します
                            //ReceivedFilesList.Items.Add(fileName);
                        }
                            // ④ 完了をUIに通知
                            Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show(
                                $"ファイルを受信しました\n名前: 送信元: {clientIP}\n",
                                "受信完了");
                        });
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"受信エラー: {ex.Message}", "エラー");
                    });
                }
            });
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            string targetIP = Ftp_Server_IP.Text.Trim();//FTPサーバーのIPアドレスを取得

            Connection_Statement.Text = "Connecting...";//接続状況を表示

            try
            {
                ftpClient = new TcpClient();//新しいTcpClientを作成

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
            dialog.Multiselect = true;
            if (dialog.ShowDialog() == true)
            {
                SelectedFilesList.Items.Clear(); // 前の選択をリセット

                foreach (string filePath in dialog.FileNames)
                {
                    SelectedFilesList.Items.Add(System.IO.Path.GetFileName(filePath));
                }
            }

            try
            {
                NetworkStream stream = ftpClient.GetStream();

                var writer = new BinaryWriter(stream, Encoding.UTF8);

                writer.Write(dialog.FileNames.Length);

                foreach (string filePath in dialog.FileNames)
                {
                    string fileName = System.IO.Path.GetFileName(filePath);

                    long fileSize = new System.IO.FileInfo(filePath).Length;

                    writer.Write(fileName);
                    writer.Write(fileSize);

                    using (var fileStream = File.OpenRead(filePath))
                    {
                        await fileStream.CopyToAsync(stream);
                    }
                }

                //await stream.FlushAsync();

                //ftpClient.Close();
                //ftpClient = null;

                //Connection_Statement.Text = "Disconnected";
                //Connection_Statement.Foreground = Brushes.Gray;
                //MessageBox.Show("すべてのファイルの送信が完了しました！", "送信完了");
            }
            catch (Exception ex)
            {
                ftpClient.Close();
                ftpClient = null;
            }
        }

        private async void btnListen_Click(object sender, RoutedEventArgs e)
        {
            listenerCts = new CancellationTokenSource();
            try
            {
                tcpListener = new TcpListener(IPAddress.Any, ftpPort);
                tcpListener.Start();

                Listen_Statement.Text = $"Listen中 (Port: {ftpPort})";
                Listen_Statement.Foreground = Brushes.Blue;

                while (!listenerCts.IsCancellationRequested)
                {
                    TcpClient client = await tcpListener.AcceptTcpClientAsync();
                    _ = HandleIncomingClientAsync(client);
                }
            }
            catch (SocketException) when (listenerCts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Listen_Statement.Text = $"Listenエラー: {ex.Message}";
                Listen_Statement.Foreground = Brushes.Red;
            }
        }

        private void btnSaveTo_Click(object sender, RoutedEventArgs e)
        {

        }

        private async void btnClose_Click(object sender, RoutedEventArgs e)
        {
            // 💡 追加：データを送り終わったら通信を閉じて、サーバーに「終わり」を知らせる
            ftpClient.Close();
            ftpClient = null;

            // UIも切断状態に戻しておく
            Connection_Statement.Text = "Disconnected";
            Connection_Statement.Foreground = Brushes.Red;
            MessageBox.Show("ファイルの送信が完了しました！", "送信完了");
        }
    }
}
