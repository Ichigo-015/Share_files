using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Share_files
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック。
    ///
    /// 1 つのアプリが「送信(クライアント)」と「受信(サーバー)」の両方を兼ねる
    /// P2P 型のファイル共有アプリ。
    ///
    /// 通信プロトコル(1 セッション = 1 バッチ):
    ///   [Int32 ファイル数]
    ///   ファイルごとに:
    ///     [String ファイル名][Int64 ファイルサイズ][そのサイズ分の生バイト列]
    /// 同じ接続で複数バッチを連続送信でき、受信側は接続が切れるまでループで待ち受ける。
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int DefaultPort = 1145;
        private const int BufferSize = 81920;

        // 送信用(自分が接続する側)
        private TcpClient ftpClient;

        // 受信用(自分が待ち受ける側)
        private TcpListener tcpListener;
        private CancellationTokenSource listenerCts;
        private bool isListening;

        // 選択中の送信ファイル(フルパス)
        private readonly List<string> selectedFiles = new List<string>();

        // 受信ファイルの保存先
        private string saveDir;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            // 画面サイズに対する比率でウィンドウサイズを決め、中央に配置
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            this.Width = Math.Max(MinWidth, screenWidth * 0.5);
            this.Height = Math.Max(MinHeight, screenHeight * 0.55);
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // ===================================================================
        //  初期化 / 終了
        // ===================================================================

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 既定の保存先(ダウンロード\SharedFiles)を用意
            saveDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "SharedFiles");
            Directory.CreateDirectory(saveDir);
            SaveFileTo.Text = saveDir;

            ConnectPort.Text = DefaultPort.ToString();
            ListenPort.Text = DefaultPort.ToString();
            SetStatus("準備完了");
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 後始末。例外は無視してアプリを確実に閉じる。
            try { listenerCts?.Cancel(); } catch { }
            try { tcpListener?.Stop(); } catch { }
            try { ftpClient?.Close(); } catch { }
        }

        // ===================================================================
        //  送信側 — 接続 / 切断
        // ===================================================================

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            string targetIP = Ftp_Server_IP.Text.Trim();
            if (string.IsNullOrEmpty(targetIP))
            {
                MessageBox.Show("接続先の IP アドレスを入力してください。", "入力エラー",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(ConnectPort.Text.Trim(), out int port))
            {
                MessageBox.Show("ポート番号が不正です。", "入力エラー",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetConnectionState("Connecting...", Brushes.DarkOrange);
            btnConnect.IsEnabled = false;

            try
            {
                ftpClient = new TcpClient();
                await ftpClient.ConnectAsync(targetIP, port);

                SetConnectionState($"Connected → {targetIP}:{port}", Brushes.Green);
                btnClose.IsEnabled = true;
                UpdateUploadButton();
                SetStatus("接続しました。ファイルを選択して送信できます。");
            }
            catch (Exception ex)
            {
                ftpClient?.Close();
                ftpClient = null;
                SetConnectionState("Connection Failed", Brushes.Red);
                btnConnect.IsEnabled = true;
                SetStatus("接続に失敗しました: " + ex.Message);
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            try { ftpClient?.Close(); } catch { }
            ftpClient = null;

            SetConnectionState("未接続", Brushes.Gray);
            btnConnect.IsEnabled = true;
            btnClose.IsEnabled = false;
            UpdateUploadButton();
            SetStatus("切断しました。");
        }

        // ===================================================================
        //  送信側 — ファイル選択 / 送信
        // ===================================================================

        private void btnSelect_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "共有するファイルを選択",
                Multiselect = true
            };

            if (dialog.ShowDialog() != true)
                return;

            selectedFiles.Clear();
            SelectedFilesList.Items.Clear();

            foreach (string filePath in dialog.FileNames)
            {
                selectedFiles.Add(filePath);
                long size = new FileInfo(filePath).Length;
                SelectedFilesList.Items.Add($"{System.IO.Path.GetFileName(filePath)}  ({FormatSize(size)})");
            }

            UpdateUploadButton();
            SetStatus($"{selectedFiles.Count} 個のファイルを選択しました。");
        }

        private async void btnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (ftpClient == null || !ftpClient.Connected)
            {
                MessageBox.Show("先に相手へ接続してください。", "未接続",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (selectedFiles.Count == 0)
            {
                MessageBox.Show("送信するファイルを選択してください。", "ファイル未選択",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 送信中は操作を止める
            btnUpload.IsEnabled = false;
            btnSelect.IsEnabled = false;
            btnClose.IsEnabled = false;

            try
            {
                long totalBytes = 0;
                foreach (string f in selectedFiles)
                    totalBytes += new FileInfo(f).Length;

                NetworkStream stream = ftpClient.GetStream();
                long sentBytes = 0;
                byte[] buffer = new byte[BufferSize];

                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(selectedFiles.Count);

                    for (int i = 0; i < selectedFiles.Count; i++)
                    {
                        string filePath = selectedFiles[i];
                        string fileName = System.IO.Path.GetFileName(filePath);
                        long fileSize = new FileInfo(filePath).Length;

                        writer.Write(fileName);
                        writer.Write(fileSize);
                        writer.Flush();

                        SetStatus($"送信中 ({i + 1}/{selectedFiles.Count}): {fileName}");

                        using (var fileStream = File.OpenRead(filePath))
                        {
                            int read;
                            while ((read = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await stream.WriteAsync(buffer, 0, read);
                                sentBytes += read;
                                SetProgress(totalBytes == 0 ? 100 : sentBytes * 100.0 / totalBytes);
                            }
                        }
                        await stream.FlushAsync();
                    }
                }

                SetProgress(100);
                SetStatus($"送信完了: {selectedFiles.Count} 個のファイルを送りました。");
                MessageBox.Show("ファイルの送信が完了しました。", "送信完了",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SetStatus("送信エラー: " + ex.Message);
                MessageBox.Show("送信中にエラーが発生しました:\n" + ex.Message, "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);

                // 接続が壊れた可能性があるので切断状態に戻す
                try { ftpClient?.Close(); } catch { }
                ftpClient = null;
                SetConnectionState("未接続", Brushes.Gray);
                btnConnect.IsEnabled = true;
            }
            finally
            {
                SetProgress(0);
                btnSelect.IsEnabled = true;
                btnClose.IsEnabled = ftpClient != null && ftpClient.Connected;
                UpdateUploadButton();
            }
        }

        // ===================================================================
        //  受信側 — 待ち受け開始 / 停止
        // ===================================================================

        private async void btnListen_Click(object sender, RoutedEventArgs e)
        {
            if (isListening)
            {
                StopListening();
                return;
            }

            if (!int.TryParse(ListenPort.Text.Trim(), out int port))
            {
                MessageBox.Show("待ち受けポートが不正です。", "入力エラー",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            listenerCts = new CancellationTokenSource();
            try
            {
                tcpListener = new TcpListener(IPAddress.Any, port);
                tcpListener.Start();

                isListening = true;
                btnListen.Content = "待ち受け停止";
                ListenPort.IsEnabled = false;
                SetListenState($"待ち受け中 (Port: {port})", Brushes.Blue);
                SetStatus("接続を待っています...");

                while (!listenerCts.IsCancellationRequested)
                {
                    TcpClient client = await tcpListener.AcceptTcpClientAsync();
                    _ = HandleIncomingClientAsync(client);
                }
            }
            catch (ObjectDisposedException) { /* 停止操作 */ }
            catch (SocketException) when (listenerCts.IsCancellationRequested) { /* 停止操作 */ }
            catch (Exception ex)
            {
                SetListenState("待ち受けエラー: " + ex.Message, Brushes.Red);
                SetStatus("待ち受けを開始できませんでした: " + ex.Message);
                StopListening();
            }
        }

        private void StopListening()
        {
            try { listenerCts?.Cancel(); } catch { }
            try { tcpListener?.Stop(); } catch { }

            isListening = false;
            btnListen.Content = "待ち受け開始";
            ListenPort.IsEnabled = true;
            SetListenState("停止中", Brushes.Gray);
            SetStatus("待ち受けを停止しました。");
        }

        /// <summary>受信した 1 接続を処理する。接続が切れるまで複数バッチを連続受信する。</summary>
        private async Task HandleIncomingClientAsync(TcpClient client)
        {
            string clientIP = "unknown";
            try { clientIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString(); }
            catch { }

            SetStatus($"接続を受け付けました: {clientIP}");

            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
                {
                    byte[] buffer = new byte[BufferSize];

                    while (true)
                    {
                        int fileCount;
                        try
                        {
                            // 相手が接続を閉じるとここで EndOfStream / IOException
                            fileCount = reader.ReadInt32();
                        }
                        catch (EndOfStreamException) { break; }
                        catch (IOException) { break; }

                        for (int i = 0; i < fileCount; i++)
                        {
                            string fileName = reader.ReadString();
                            long fileSize = reader.ReadInt64();

                            // パストラバーサル対策: ファイル名のみを使う
                            string safeName = System.IO.Path.GetFileName(fileName);
                            string savePath = GetUniquePath(System.IO.Path.Combine(saveDir, safeName));

                            SetStatus($"受信中 ({i + 1}/{fileCount}): {safeName}");

                            using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write))
                            {
                                long readTotal = 0;
                                while (readTotal < fileSize)
                                {
                                    int toRead = (int)Math.Min(buffer.Length, fileSize - readTotal);
                                    int read = await stream.ReadAsync(buffer, 0, toRead);
                                    if (read == 0)
                                        throw new IOException("通信が途中で切断されました。");

                                    await fileStream.WriteAsync(buffer, 0, read);
                                    readTotal += read;
                                    SetProgress(fileSize == 0 ? 100 : readTotal * 100.0 / fileSize);
                                }
                            }

                            string finalName = System.IO.Path.GetFileName(savePath);
                            Dispatcher.Invoke(() => ReceivedFilesList.Items.Add(finalName));
                        }

                        SetProgress(0);
                        SetStatus($"{fileCount} 個のファイルを受信しました ({clientIP})");
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus("受信エラー: " + ex.Message);
            }
        }

        // ===================================================================
        //  受信側 — 保存先 / フォルダ操作
        // ===================================================================

        private void btnSaveTo_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "受信ファイルの保存先フォルダを選択";
                dialog.SelectedPath = saveDir;
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    saveDir = dialog.SelectedPath;
                    Directory.CreateDirectory(saveDir);
                    SaveFileTo.Text = saveDir;
                    SetStatus("保存先を変更しました: " + saveDir);
                }
            }
        }

        private void btnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Directory.Exists(saveDir))
                    Process.Start("explorer.exe", saveDir);
            }
            catch (Exception ex)
            {
                SetStatus("フォルダを開けませんでした: " + ex.Message);
            }
        }

        private void ReceivedFilesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ReceivedFilesList.SelectedItem == null)
                return;

            string path = System.IO.Path.Combine(saveDir, ReceivedFilesList.SelectedItem.ToString());
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus("ファイルを開けませんでした: " + ex.Message);
            }
        }

        // ===================================================================
        //  ヘルパー
        // ===================================================================

        private void UpdateUploadButton()
        {
            bool connected = ftpClient != null && ftpClient.Connected;
            btnUpload.IsEnabled = connected && selectedFiles.Count > 0;
        }

        /// <summary>同名ファイルがあるときは "(2)" などを付けて衝突を避ける。</summary>
        private static string GetUniquePath(string path)
        {
            if (!File.Exists(path))
                return path;

            string dir = System.IO.Path.GetDirectoryName(path);
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            string ext = System.IO.Path.GetExtension(path);

            for (int i = 2; ; i++)
            {
                string candidate = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate))
                    return candidate;
            }
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} {units[unit]}" : $"{size:0.0} {units[unit]}";
        }

        private void SetStatus(string text) =>
            Dispatcher.Invoke(() => StatusText.Text = text);

        private void SetProgress(double percent) =>
            Dispatcher.Invoke(() => TransferProgress.Value = Math.Max(0, Math.Min(100, percent)));

        private void SetConnectionState(string text, Brush color)
        {
            Connection_Statement.Text = text;
            Connection_Statement.Foreground = color;
        }

        private void SetListenState(string text, Brush color) =>
            Dispatcher.Invoke(() =>
            {
                Listen_Statement.Text = text;
                Listen_Statement.Foreground = color;
            });
    }
}
