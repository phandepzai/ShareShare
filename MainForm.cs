using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;

namespace ShareFile
{
    public partial class MainForm : Form
    {
        private string _sharedFolderPath;
        private HttpListener _listener;
        private Thread _serverThread;
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.NotifyIcon notifyIcon;
        private System.Windows.Forms.ContextMenuStrip contextMenuStrip;
        private System.Windows.Forms.ToolStripMenuItem openMenuItem;
        private System.Windows.Forms.ToolStripMenuItem exitMenuItem;
        private System.Windows.Forms.ToolTip toolTip1;

        // Thêm các hằng số và phương thức API để ngăn sleep
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

        [Flags]
        private enum ExecutionState : uint
        {
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_SYSTEM_REQUIRED = 0x00000001
        }

        private ExecutionState _currentExecutionState;
        private bool _preventSleep = false;
        private bool _isExiting = false;

        // Thêm các constant cho WebDAV
        private const string DAV_HEADER = "DAV: 1, 2";
        private const string MS_AUTHOR_VIA = "MS-Author-Via: DAV";

        public Icon CreateVirtualFavicon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Blue);
                g.DrawString("N", new Font("Arial", 16), Brushes.White, new PointF(4, 4));
            }

            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Seek(0, SeekOrigin.Begin);
                return new Icon(ms); // Lưu ý: Icon từ PNG có thể không tương thích hoàn toàn
            }
        }

        public MainForm()
        {
            InitializeComponent();
            this.ControlBox = true;     // Hiển thị thanh điều khiển
            this.MaximizeBox = false;   // Vô hiệu hóa nút phóng to
            this.MinimizeBox = true;    // GIỮ NÚT THU NHỎ

            // Cấu hình hệ thống cho file lớn
            AppContext.SetSwitch("System.Net.Http.UseSocketsHttpHandler", false);
            // Tăng giới hạn kết nối
            ServicePointManager.DefaultConnectionLimit = int.MaxValue;
            this.MaximizeBox = false;
            _sharedFolderPath = Application.StartupPath;
            lblFolderPath.Text = $"Đường dẫn đã chọn: {_sharedFolderPath}";
            this.Load += MainForm_Load;
            this.FormClosing += MainForm_FormClosing;

            this.components = new System.ComponentModel.Container();
            this.notifyIcon = new System.Windows.Forms.NotifyIcon();
            this.contextMenuStrip = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.openMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.exitMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.notifyIcon.DoubleClick += notifyIcon_DoubleClick;   // MouseDoubleClick cho notifyIcon

            // TỰ ĐỘNG KÍCH HOẠT NGĂN SLEEP KHI KHỞI ĐỘNG ỨNG DỤNG
            PreventSleep(false);

            // Đăng ký sự kiện system power mode changed
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

            // Add tooltips
            toolTip1.SetToolTip(this.txtPort, "Vui lòng nhập một số port hợp lệ (từ 1024 đến 65535)\r\nVí dụ 1234 hoặc 6789 hoặc 8888 hoặc 9999");
            toolTip1.SetToolTip(this.label1, "Vui lòng nhập một số port hợp lệ (từ 1024 đến 65535)\r\nVí dụ 1234 hoặc 6789 hoặc 8888 hoặc 9999");

            this.contextMenuStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
                this.openMenuItem,
                this.exitMenuItem});
            this.contextMenuStrip.Name = "contextMenuStrip";
            this.contextMenuStrip.Size = new System.Drawing.Size(181, 48);

            this.openMenuItem.Name = "openMenuItem";
            this.openMenuItem.Size = new System.Drawing.Size(180, 22);
            this.openMenuItem.Text = "Mở";
            this.openMenuItem.Click += new System.EventHandler(this.openMenuItem_Click);

            this.exitMenuItem.Name = "exitMenuItem";
            this.exitMenuItem.Size = new System.Drawing.Size(180, 22);
            this.exitMenuItem.Text = "Thoát";
            this.exitMenuItem.Click += new System.EventHandler(this.exitMenuItem_Click);

            this.notifyIcon.ContextMenuStrip = this.contextMenuStrip;
            this.notifyIcon.Icon = this.Icon;
            this.notifyIcon.Text = "Chia Sẻ File qua LAN";
            this.notifyIcon.Visible = false;

            this.Resize += new System.EventHandler(this.MainForm_Resize);
            this.FormClosing += MainForm_FormClosing;
            this.txtPort.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.txtPort_KeyPress);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            txtPort.Text = "8888";
            btnStop.Enabled = false;

            // HIỂN THỊ DÒNG NÀY TRƯỚC
            UpdateLog("Coder: ©Nông Văn Phấn"); 

            // Đảm bảo ngăn sleep đã được kích hoạt nhưng KHÔNG hiển thị log
            if (!_preventSleep)
            {
                PreventSleep(false); // Không hiển thị log tự động
            }

            // HIỂN THỊ DÒNG NÀY SAU
            UpdateLog("Đã kích hoạt chế độ ngăn máy tính sleep");

            notifyIcon.Text = "Ứng dụng chia sẻ file đã sẵn sàng";
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_CLOSE = 0xF060;

            if (m.Msg == WM_SYSCOMMAND && (int)m.WParam == SC_CLOSE)
            {
                // Khi bấm nút X - thu nhỏ xuống system tray mà không hiện thông báo
                this.WindowState = FormWindowState.Minimized;
                this.Hide();
                return;
            }

            base.WndProc(ref m);
        }

        private void PreventSleep(bool showLog = true)
        {
            if (!_preventSleep)
            {
                try
                {
                    _currentExecutionState = SetThreadExecutionState(
                        ExecutionState.ES_CONTINUOUS |
                        ExecutionState.ES_SYSTEM_REQUIRED |
                        ExecutionState.ES_DISPLAY_REQUIRED);

                    _preventSleep = true;

                    // CHỈ HIỂN THỊ LOG KHI ĐƯỢC YÊU CẦU
                    if (showLog)
                    {
                        UpdateLog("Đã kích hoạt chế độ ngăn máy tính sleep");
                    }

                    // Cập nhật trạng thái trên tray icon
                    notifyIcon.Text = "Ứng dụng đang chạy - Ngăn sleep";
                }
                catch (Exception ex)
                {
                    UpdateLog($"Lỗi khi ngăn sleep: {ex.Message}", true);
                }
            }
        }

        private void AllowSleep()
        {
            if (_preventSleep)
            {
                try
                {
                    SetThreadExecutionState(ExecutionState.ES_CONTINUOUS);
                    _preventSleep = false;
                    UpdateLog("Đã tắt chế độ ngăn máy tính sleep");
                }
                catch (Exception ex)
                {
                    UpdateLog($"Lỗi khi cho phép sleep: {ex.Message}", true);
                }
            }
        }

        // Thêm phương thức xử lý sự kiện
        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume && _preventSleep)
            {
                // Khi máy tính thức dậy, đảm bảo tiếp tục ngăn sleep
                UpdateLog("Máy tính đã thức dậy, tiếp tục ngăn sleep...");
                PreventSleep();
            }
        }

        // Thêm vào sự kiện FormClosed để hủy đăng ký
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            base.OnFormClosed(e);
        }

        private void notifyIcon_DoubleClick(object sender, EventArgs e)
        {
            RestoreFromTray();
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.Hide(); // Ẩn form
                this.notifyIcon.Visible = true;
                this.ShowInTaskbar = false;
                // ĐÃ XÓA HIỂN THỊ BALLOON TIP
            }
            else
            {
                this.ShowInTaskbar = true;
                this.notifyIcon.Visible = false;
            }
        }

        private void btnExit_Click(object sender, EventArgs e)
        {
            // KHÔNG hiển thị hộp thoại xác nhận
            _isExiting = true;
            this.Close();
        }
        private void openMenuItem_Click(object sender, EventArgs e)
        {
            RestoreFromTray();
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.notifyIcon.Visible = false;
            this.ShowInTaskbar = true;
            this.BringToFront();
            this.Activate();
        }
        //Sự kiện đóng ứng dụng từ notifyIcon
        private void exitMenuItem_Click(object sender, EventArgs e)
        {
            // KHÔNG hiển thị hộp thoại xác nhận
            _isExiting = true;
            AllowSleep();
            this.Dispose();
            Application.Exit();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Nếu người dùng bấm X thông qua các cách khác (Alt+F4, etc.)
            if (e.CloseReason == CloseReason.UserClosing && !_isExiting)
            {
                e.Cancel = true; // Ngăn không cho đóng form

                // Thu nhỏ xuống system tray mà không hiện thông báo
                this.WindowState = FormWindowState.Minimized;
                this.Hide();
                return;
            }

            // Nếu thực sự muốn thoát (từ nút Thoát hoặc menu tray)
            StopSharing();
            AllowSleep();

            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
            }
        }

        private void btnChooseFolder_Click(object sender, EventArgs e)
        {
            using (var fbd = new FolderBrowserDialog())
            {
                DialogResult result = fbd.ShowDialog();
                if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                {
                    _sharedFolderPath = fbd.SelectedPath;
                    lblFolderPath.Text = $"Đường dẫn đã chọn: {_sharedFolderPath}";
                    UpdateLog($"Đã chọn thư mục: {_sharedFolderPath}");
                }
            }
        }

        private void txtPort_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
            {
                e.Handled = true;
            }
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            if (!int.TryParse(txtPort.Text, out int port) || port < 1024 || port > 65535)
            {
                MessageBox.Show("Vui lòng nhập một số cổng hợp lệ (từ 1024 đến 65535).", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnStart.Enabled = false;
            btnChooseFolder.Enabled = false;
            btnStop.Enabled = true;
            txtPort.Enabled = false;
            UpdateLog("Đang khởi động máy chủ...");

            _serverThread = new Thread(() => StartServer(port));
            _serverThread.IsBackground = true;
            _serverThread.Start();
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            StopSharing();
        }


        private void StartServer(int port)
        {
            // Đảm bảo ngăn sleep đã được kích hoạt
            if (!_preventSleep)
            {
                PreventSleep();
            }
            _listener = new HttpListener();
            try
            {
                // Cấu hình timeout cho file lớn
                _listener.TimeoutManager.EntityBody = TimeSpan.FromHours(6); // Tăng lên 6 giờ
                _listener.TimeoutManager.DrainEntityBody = TimeSpan.FromHours(3);
                _listener.TimeoutManager.RequestQueue = TimeSpan.FromHours(3);

                // Cấu hình bổ sung cho file cực lớn
                _listener.UnsafeConnectionNtlmAuthentication = true;
                _listener.IgnoreWriteExceptions = true; // Cho phép tiếp tục nếu client ngắt kết nối
                string uriPrefix = $"http://+:{port}/";
                _listener.Prefixes.Add(uriPrefix);
                _listener.Start();

                string localIP = GetLocalIPAddress();
                string computerName = GetComputerName();

                UpdateLog("═".PadRight(45, '═'));
                UpdateLog("🖥️  ỨNG DỤNG ĐÃ BẮT ĐẦU CHIA SẺ");
                UpdateLog("═".PadRight(45, '═'));
                UpdateLog($"📍 Địa chỉ IP: {localIP}");
                UpdateLog($"📍 Port: {port}");
                UpdateLog($"📍 Tên máy tính: {computerName}");
                UpdateLog("");
                UpdateLog("🌐 TRUY CẬP TỪ TRÌNH DUYỆT:");
                UpdateLog($"   http://{localIP}:{port}");
                UpdateLog($"   http://{computerName}:{port}");
                UpdateLog("");
                UpdateLog("📁 TRUY CẬP TỪ FILE EXPLORER:");
                UpdateLog($"   \\\\{localIP}");
                UpdateLog($"   \\\\{computerName}");
                UpdateLog("");
                UpdateLog("✅ Có thể truy cập từ các thiết bị trong mạng LAN");
                UpdateLog("═".PadRight(45, '═'));

                notifyIcon.Text = $"Đang chia sẻ: {localIP}:{port}";

                while (_listener.IsListening)
                {
                    try
                    {
                        var context = _listener.GetContext();
                        // Thêm WebDAV headers vào mọi response
                        context.Response.Headers.Add("DAV", "1, 2");
                        context.Response.Headers.Add("MS-Author-Via", "DAV");
                        ThreadPool.QueueUserWorkItem(async (c) =>
                        {
                            var ctx = c as HttpListenerContext;
                            try
                            {
                                await ProcessRequest(ctx);
                            }
                            catch (Exception ex)
                            {
                                UpdateLog($"Lỗi xử lý yêu cầu: {ex.Message}");
                            }
                        }, context);
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        UpdateLog($"Lỗi chung khi lắng nghe: {ex.Message}");
                    }
                }
            }
            catch (HttpListenerException ex)
            {
                MessageBox.Show($"Lỗi khi khởi động server: {ex.Message}\r\n" +
                                "Có thể bạn cần chạy ứng dụng với quyền Administrator để sử dụng cổng này.",
                                "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateLog($"Lỗi: {ex.Message}");
                StopSharing();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateLog($"Lỗi: {ex.Message}");
                StopSharing();
            }
        }

        private async Task ProcessRequest(HttpListenerContext context)
        {
            string clientIp = context.Request.RemoteEndPoint.Address.ToString();

            // Xử lý WebDAV requests
            if (context.Request.HttpMethod == "OPTIONS" || context.Request.HttpMethod == "PROPFIND")
            {
                await HandleWebDAVRequest(context);
                return;
            }

            // Lấy path từ URL và decode đúng cách
            string relativePath = context.Request.Url.AbsolutePath;

            // Giải mã URL và xử lý ký tự đặc biệt
            relativePath = SafeDecode(relativePath);

            if (string.IsNullOrEmpty(relativePath))
                relativePath = "/";

            // Các route đặc biệt: upload
            if (context.Request.HttpMethod == "GET" &&
                (string.Equals(relativePath, "/upload", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(relativePath, "/share", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(relativePath, "/up", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(relativePath, "/load", StringComparison.OrdinalIgnoreCase)))
            {
                string htmlContent = GenerateUploadPageHtml();
                byte[] buffer = Encoding.UTF8.GetBytes(htmlContent);
                context.Response.ContentType = "text/html; charset=UTF-8";
                context.Response.ContentLength64 = buffer.LongLength;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
                UpdateLog($"[{clientIp}] Đã truy cập trang upload file.");
                return;
            }

            if (context.Request.HttpMethod == "POST" &&
                string.Equals(relativePath, "/upload", StringComparison.OrdinalIgnoreCase))
            {
                await HandleFileUploadBinary(context);
                return;
            }

            // Map URL -> thư mục thực
            string requestSubPath = relativePath.TrimStart('/')
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            // DECODE thêm một lần nữa để đảm bảo
            requestSubPath = SafeDecode(requestSubPath);

            string root = Path.GetFullPath(_sharedFolderPath);
            string fullPath = Path.GetFullPath(Path.Combine(root, requestSubPath));

            // Ngăn chặn truy cập ra ngoài thư mục share
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                UpdateLog($"[{clientIp}] Bị chặn truy cập ngoài thư mục chia sẻ: {fullPath}");
                return;
            }

            try
            {
                // Thay thế đoạn code xử lý file hiện tại bằng phiên bản tối ưu hóa này:
                if (File.Exists(fullPath))
                {
                    string extension = Path.GetExtension(fullPath).ToLower();
                    string fileName = Path.GetFileName(fullPath);

                    // Xác định xem file có nên mở trên trình duyệt hay tải về
                    bool shouldDisplayInBrowser = ShouldDisplayInBrowser(extension);

                    if (!shouldDisplayInBrowser)
                    {
                        // Thiết lập headers cho file download (chỉ cho file không hiển thị được)
                        context.Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{Uri.EscapeDataString(fileName)}\"");
                    }
                    else
                    {
                        // Cho file hiển thị trên trình duyệt, đặt Content-Disposition là inline
                        context.Response.Headers.Add("Content-Disposition", $"inline; filename=\"{Uri.EscapeDataString(fileName)}\"");
                    }

                    context.Response.Headers.Add("Accept-Ranges", "bytes");

                    // Xử lý Range requests cho file lớn (resume download)
                    long fileSize = new FileInfo(fullPath).Length;
                    long start = 0, end = fileSize - 1;
                    long length = fileSize;

                    if (context.Request.HttpMethod == "GET" && !string.IsNullOrEmpty(context.Request.Headers["Range"]))
                    {
                        string rangeHeader = context.Request.Headers["Range"];
                        var match = Regex.Match(rangeHeader, @"bytes=(\d*)-(\d*)");
                        if (match.Success)
                        {
                            start = string.IsNullOrEmpty(match.Groups[1].Value) ? 0 : long.Parse(match.Groups[1].Value);
                            end = string.IsNullOrEmpty(match.Groups[2].Value) ? fileSize - 1 : long.Parse(match.Groups[2].Value);

                            if (end >= fileSize) end = fileSize - 1;
                            length = end - start + 1;

                            context.Response.StatusCode = 206; // Partial Content
                            context.Response.Headers.Add("Content-Range", $"bytes {start}-{end}/{fileSize}");
                        }
                    }

                    if (extension == ".txt" || extension == ".ini" || extension == ".html" || extension == ".htm" ||
        extension == ".css" || extension == ".js" || extension == ".json" || extension == ".xml")
                    {
                        // Đối với file text, đọc toàn bộ nội dung
                        string content = File.ReadAllText(fullPath, Encoding.UTF8);
                        byte[] buffer = Encoding.UTF8.GetBytes(content);
                        context.Response.ContentType = GetContentType(extension);
                        context.Response.ContentLength64 = buffer.LongLength;
                        await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                        UpdateLog($"[{clientIp}] Đã mở file văn bản: {fullPath}");
                    }
                    else
                    {
                        // Đối với file binary (hình ảnh, pdf, etc.)
                        context.Response.ContentType = GetContentType(extension);
                        context.Response.ContentLength64 = length;

                        using (FileStream fs = new FileStream(
                            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            bufferSize: 65536,
                            useAsync: true))
                        {
                            fs.Seek(start, SeekOrigin.Begin);

                            byte[] buffer = new byte[65536];
                            long bytesRemaining = length;

                            while (bytesRemaining > 0)
                            {
                                int bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
                                int bytesRead = await fs.ReadAsync(buffer, 0, bytesToRead);

                                if (bytesRead == 0) break;

                                await context.Response.OutputStream.WriteAsync(buffer, 0, bytesRead);
                                bytesRemaining -= bytesRead;

                                if (bytesRemaining > 0)
                                    await Task.Yield();
                            }
                        }

                        string action = shouldDisplayInBrowser ? "mở" : "tải xuống";
                        UpdateLog($"[{clientIp}] Đã {action} file: {Path.GetFileName(fullPath)} ({(length / (1024.0 * 1024.0)):0.00} MB)");
                    }

                    context.Response.OutputStream.Close();
                }
                else if (Directory.Exists(fullPath))
                {
                    string htmlContent = GenerateDirectoryListingHtml(fullPath, relativePath);
                    byte[] buffer = Encoding.UTF8.GetBytes(htmlContent);
                    context.Response.ContentType = "text/html; charset=UTF-8";
                    context.Response.ContentLength64 = buffer.LongLength;
                    await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    context.Response.OutputStream.Close();
                    UpdateLog($"[{clientIp}] Đã truy cập thư mục: {fullPath}");
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    UpdateLog($"[{clientIp}] Không tìm thấy: {fullPath}");
                }
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
                UpdateLog($"[{clientIp}] Lỗi xử lý yêu cầu cho {relativePath}: {ex.Message}");
            }
        }

        private bool ShouldDisplayInBrowser(string extension)
        {
            var browserDisplayableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Text files
                ".txt", ".html", ".htm", ".css", ".js", ".json", ".xml",
        
                // Image files
                ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".ico",
        
                // PDF
                ".pdf",
        
                // Audio/Video
                ".mp3", ".mp4", ".webm", ".ogg", ".wav"
            };

            return browserDisplayableExtensions.Contains(extension);
        }

        // Encode tên file/thư mục để sinh link an toàn
        private string SafeEncode(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            // Encode toàn bộ URL nhưng giữ lại slash
            string encoded = Uri.EscapeDataString(name);

            // Thay thế các ký tự đặc biệt bằng placeholder an toàn
            encoded = encoded
                .Replace("%23", "~HASH~")    // #
                .Replace("%25", "~PERCENT~") // %
                .Replace("%26", "~AMP~")     // &
                .Replace("%2A", "~STAR~")    // *
                .Replace("%2B", "~PLUS~")    // +
                .Replace("%3D", "~EQUAL~");  // =

            return encoded;
        }

        // Decode lại khi nhận request
        private string SafeDecode(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            // Khôi phục các ký tự đặc biệt từ placeholder
            string decoded = name
                .Replace("~HASH~", "%23")
                .Replace("~PERCENT~", "%25")
                .Replace("~AMP~", "%26")
                .Replace("~STAR~", "%2A")
                .Replace("~PLUS~", "%2B")
                .Replace("~EQUAL~", "%3D");

            // Decode URL
            return Uri.UnescapeDataString(decoded);
        }


        //Phương thức trang giao diện Upload
        private string GenerateUploadPageHtml()
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"vi\"><head>");
            sb.Append("<meta charset='UTF-8'>");
            sb.Append("<meta http-equiv='X-UA-Compatible' content='IE=edge'>");
            sb.Append("<title>Tải lên tập tin</title>");
            sb.Append("<style>");
            sb.Append("body{font-family:'Segoe UI',Arial,sans-serif;background:#f0f2f5;margin:0;padding:20px;}");
            sb.Append(".upload-card{background:#fff;border-radius:8px;box-shadow:0 2px 8px rgba(0,0,0,.1);max-width:500px;margin:40px auto;padding:30px;text-align:center;}");
            sb.Append(".upload-card h2{margin:0 0 10px;color:#333;}");
            sb.Append(".upload-box{border:2px dashed #bbb;border-radius:6px;padding:25px;cursor:pointer;color:#666;}");
            sb.Append("#fileList{margin-top:15px;text-align:left;max-height:200px;overflow-y:auto;font-size:14px;}");
            sb.Append("#fileList div{padding:6px;border-bottom:1px solid #eee;}");
            sb.Append(".btn{background:#4CAF50;color:#fff;border:none;padding:10px 20px;margin-top:15px;border-radius:4px;cursor:pointer;}");
            sb.Append(".btn:disabled{background:#ccc;cursor:not-allowed;}");
            sb.Append(".progress{height:8px;background:#eee;border-radius:4px;margin-top:10px;display:none;}");
            sb.Append(".progress-bar{height:8px;background:#4CAF50;width:0;border-radius:4px;}");
            sb.Append("</style></head><body>");
            sb.Append("<div class='upload-card'>");
            sb.Append("<h2>Tải lên tập tin</h2>");
            sb.Append("<div class='upload-box' onclick=\"document.getElementById('fileInput').click();\">Bấm vào đây để chọn file</div>");
            sb.Append("<input type='file' id='fileInput' multiple style='display:none'>");
            sb.Append("<div id='fileList'></div>");
            sb.Append("<button id='submitBtn' class='btn' disabled>Bắt đầu tải lên</button>");
            sb.Append("<div class='progress'><div id='progressBar' class='progress-bar'></div></div>");
            sb.Append("</div>");
            sb.Append("<script>");
            sb.Append("var fileInput=document.getElementById('fileInput');");
            sb.Append("var fileList=document.getElementById('fileList');");
            sb.Append("var submitBtn=document.getElementById('submitBtn');");
            sb.Append("var progress=document.querySelector('.progress');");
            sb.Append("var progressBar=document.getElementById('progressBar');");
            sb.Append("fileInput.addEventListener('change',function(){fileList.innerHTML='';if(fileInput.files.length>0){for(var i=0;i<fileInput.files.length;i++){var div=document.createElement('div');div.textContent=fileInput.files[i].name;fileList.appendChild(div);}submitBtn.disabled=false;}else{submitBtn.disabled=true;}});");
            sb.Append("submitBtn.addEventListener('click',function(){if(fileInput.files.length==0)return;var formData=new FormData();for(var i=0;i<fileInput.files.length;i++){formData.append('files[]',fileInput.files[i]);}var xhr=new XMLHttpRequest();xhr.open('POST','/upload',true);xhr.upload.onprogress=function(e){if(e.lengthComputable){progress.style.display='block';var percent=e.loaded/e.total*100;progressBar.style.width=percent+'%';}};xhr.onload=function(){document.open();document.write(xhr.responseText);document.close();};xhr.send(formData);});");
            sb.Append("</script></body></html>");
            return sb.ToString();
        }


        private string GenerateSuccessPageHtml(IEnumerable<string> successFiles, IEnumerable<string> failedFiles)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang='vi'><head>");
            sb.Append("<meta charset='UTF-8'>");
            sb.Append("<meta http-equiv='X-UA-Compatible' content='IE=edge'>");
            sb.Append("<title>Kết quả tải lên</title>");
            sb.Append("<style>");
            sb.Append("body{font-family:'Segoe UI',Arial,sans-serif;background:#f0f2f5;margin:0;padding:20px;}");
            sb.Append(".card{background:#fff;border-radius:8px;box-shadow:0 2px 8px rgba(0,0,0,.1);max-width:500px;margin:40px auto;padding:25px;text-align:center;}");
            sb.Append(".file-list{text-align:left;margin-top:15px;border:1px solid #ddd;padding:10px;border-radius:5px;max-height:250px;overflow-y:auto;}");
            sb.Append(".file-item{padding:6px 0;border-bottom:1px solid #eee;}");
            sb.Append(".file-item:last-child{border-bottom:none;}");
            sb.Append(".btn{background:#4CAF50;color:white;text-decoration:none;padding:8px 15px;margin-top:15px;display:inline-block;border-radius:4px;}");
            sb.Append(".btn-home{background:#007BFF;margin-left:10px;}");
            sb.Append("</style></head><body>");
            sb.Append("<div class='card'>");
            sb.Append("<h2>✔ Kết quả tải lên</h2>");

            if (successFiles != null && successFiles.Any())
            {
                sb.Append($"<p style='color:green'>Thành công: {successFiles.Count()} file</p>");
                sb.Append("<div class='file-list'>");
                foreach (var file in successFiles)
                {
                    var href = "/" + SafeEncode(file); // ← Sử dụng SafeEncode
                    sb.Append($"<div class='file-item' style='color:green'>✔ <a href='{href}'>{WebUtility.HtmlEncode(file)}</a></div>");
                }
                sb.Append("</div>");
            }

            if (failedFiles != null && failedFiles.Any())
            {
                sb.Append($"<p style='color:#d9534f;margin-top:20px;'>Thất bại: {failedFiles.Count()} file</p>");
                sb.Append("<div class='file-list'>");
                foreach (var file in failedFiles)
                {
                    sb.Append($"<div class='file-item' style='color:red'>✗ {WebUtility.HtmlEncode(file)}</div>");
                }
                sb.Append("</div>");
            }

            sb.Append("<a href='/upload' class='btn'>Tải thêm</a>");
            sb.Append("<a href='/' class='btn btn-home'>Trang chủ</a>");
            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        // Trang báo lỗi upload
        private string GenerateErrorPageHtml(string errorMessage)
        {
            var htmlBuilder = new StringBuilder();
            htmlBuilder.Append("<!DOCTYPE html>");
            htmlBuilder.Append("<html lang=\"vi\">");
            htmlBuilder.Append("<head>");
            htmlBuilder.Append("    <meta charset=\"UTF-8\">");
            htmlBuilder.Append("    <meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">");
            htmlBuilder.Append("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            htmlBuilder.Append("    <title>Lỗi Upload</title>");
            htmlBuilder.Append("    <style>");
            htmlBuilder.Append("        body { font-family: Arial, sans-serif; background-color: #f8d7da; margin: 0; padding: 0; }");
            htmlBuilder.Append("        .container { max-width: 600px; margin: 50px auto; background: #fff; padding: 20px; border-radius: 8px; box-shadow: 0 2px 5px rgba(0,0,0,0.1); }");
            htmlBuilder.Append("        h2 { color: #721c24; }");
            htmlBuilder.Append("        p { color: #721c24; font-size: 16px; }");
            htmlBuilder.Append("        a { display:inline-block; margin-top:20px; padding:10px 15px; background:#721c24; color:#fff; text-decoration:none; border-radius:4px; }");
            htmlBuilder.Append("        a:hover { background:#501214; }");
            htmlBuilder.Append("    </style>");
            htmlBuilder.Append("</head>");
            htmlBuilder.Append("<body>");
            htmlBuilder.Append("    <div class=\"container\">");
            htmlBuilder.Append("        <h2>❌ Có lỗi xảy ra khi upload</h2>");
            htmlBuilder.Append($"        <p>{System.Net.WebUtility.HtmlEncode(errorMessage)}</p>");
            htmlBuilder.Append("        <a href=\"/\">Quay lại</a>");
            htmlBuilder.Append("    </div>");
            htmlBuilder.Append("</body>");
            htmlBuilder.Append("</html>");
            return htmlBuilder.ToString();
        }


        private async Task HandleFileUploadBinary(HttpListenerContext context)
        {
            string clientIp = context.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
            var uploadedFiles = new List<string>();
            var failedFiles = new List<string>();

            try
            {
                var request = context.Request;
                if (!request.HasEntityBody)
                {
                    await SendErrorResponse(context, 400, "Không có dữ liệu tải lên.");
                    UpdateLog($"[{clientIp}] Không có dữ liệu tải lên.");
                    return;
                }

                string boundary = GetBoundary(request.ContentType);
                if (string.IsNullOrEmpty(boundary))
                {
                    await SendErrorResponse(context, 400, "Thiếu boundary trong Content-Type.");
                    UpdateLog($"[{clientIp}] Thiếu boundary trong Content-Type.");
                    return;
                }

                // Đọc và xử lý dữ liệu multipart
                using (var input = request.InputStream)
                {
                    byte[] boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary);
                    byte[] endBoundaryBytes = Encoding.UTF8.GetBytes("--" + boundary + "--");
                    byte[] buffer = new byte[256 * 1024];
                    MemoryStream currentPart = new MemoryStream();
                    bool inFile = false;
                    string currentFileName = null;
                    FileStream currentFileStream = null;
                    Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    while (true)
                    {
                        int bytesRead = await input.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break;

                        await currentPart.WriteAsync(buffer, 0, bytesRead);

                        // Xử lý dữ liệu đã đọc
                        byte[] data = currentPart.ToArray();
                        int boundaryIndex = IndexOf(data, boundaryBytes, 0);
                        int endBoundaryIndex = IndexOf(data, endBoundaryBytes, 0);

                        // Nếu tìm thấy boundary, xử lý phần dữ liệu hiện tại
                        if (boundaryIndex >= 0 || endBoundaryIndex >= 0)
                        {
                            int cutIndex = (boundaryIndex >= 0) ? boundaryIndex : endBoundaryIndex;
                            byte[] partData = new byte[cutIndex];
                            Array.Copy(data, 0, partData, 0, cutIndex);

                            // Xử lý phần dữ liệu
                            if (inFile && currentFileStream != null && partData.Length > 0)
                            {
                                // Tìm phần đầu của dữ liệu file (sau headers)
                                int headerEnd = FindHeaderEnd(partData);
                                if (headerEnd >= 0)
                                {
                                    int dataStart = headerEnd + 4; // \r\n\r\n
                                    if (dataStart < partData.Length)
                                    {
                                        await currentFileStream.WriteAsync(partData, dataStart, partData.Length - dataStart);
                                    }
                                }
                                else if (partData.Length > 0)
                                {
                                    // Nếu không tìm thấy header end, ghi toàn bộ dữ liệu
                                    await currentFileStream.WriteAsync(partData, 0, partData.Length);
                                }

                                currentFileStream.Close();
                                currentFileStream.Dispose();
                                uploadedFiles.Add(currentFileName);
                                UpdateLog($"[{clientIp}] Đã upload thành công: {currentFileName}");
                                inFile = false;
                            }

                            // Parse headers cho phần mới
                            if (cutIndex > 0)
                            {
                                headers.Clear();
                                ParseHeaders(partData, headers);

                                if (headers.ContainsKey("Content-Disposition") &&
                                    headers["Content-Disposition"].Contains("filename="))
                                {
                                    string fileName = ExtractFileNameFromContentDisposition(headers["Content-Disposition"]);
                                    if (!string.IsNullOrEmpty(fileName))
                                    {
                                        string uploadDir = Path.Combine(_sharedFolderPath, "Uploads");
                                        if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                                        string safeFileName = GetUniqueFilename(uploadDir, fileName);
                                        string savePath = Path.Combine(uploadDir, safeFileName);

                                        try
                                        {
                                            currentFileStream = new FileStream(
                                                savePath,
                                                FileMode.Create,
                                                FileAccess.Write,
                                                FileShare.None,
                                                8192,
                                                FileOptions.Asynchronous);

                                            currentFileName = safeFileName;
                                            inFile = true;

                                            // Ghi phần dữ liệu sau header (nếu có)
                                            int headerEnd = FindHeaderEnd(partData);
                                            if (headerEnd >= 0)
                                            {
                                                int dataStart = headerEnd + 4;
                                                if (dataStart < partData.Length)
                                                {
                                                    await currentFileStream.WriteAsync(partData, dataStart, partData.Length - dataStart);
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            failedFiles.Add(fileName);
                                            UpdateLog($"[{clientIp}] Lỗi khi tạo file {fileName}: {ex.Message}", true);
                                            inFile = false;
                                            currentFileStream?.Dispose();
                                        }
                                    }
                                }
                            }

                            // Giữ lại phần dữ liệu sau boundary
                            int remainingStart = cutIndex + boundaryBytes.Length;
                            int remainingLength = data.Length - remainingStart;
                            byte[] remainingData = new byte[remainingLength];
                            Array.Copy(data, remainingStart, remainingData, 0, remainingLength);

                            currentPart.Dispose();
                            currentPart = new MemoryStream();
                            if (remainingLength > 0)
                            {
                                await currentPart.WriteAsync(remainingData, 0, remainingLength);
                            }

                            // Nếu là end boundary, thoát
                            if (endBoundaryIndex >= 0) break;
                        }
                    }

                    // Xử lý phần dữ liệu cuối cùng
                    if (inFile && currentFileStream != null)
                    {
                        byte[] finalData = currentPart.ToArray();
                        if (finalData.Length > 0)
                        {
                            // Tìm và cắt bỏ end boundary nếu có
                            int endBoundaryPos = IndexOf(finalData, endBoundaryBytes, 0);
                            if (endBoundaryPos >= 0)
                            {
                                await currentFileStream.WriteAsync(finalData, 0, endBoundaryPos);
                            }
                            else
                            {
                                await currentFileStream.WriteAsync(finalData, 0, finalData.Length);
                            }
                        }

                        currentFileStream.Close();
                        uploadedFiles.Add(currentFileName);
                        UpdateLog($"[{clientIp}] Đã upload thành công: {currentFileName}");
                    }

                    currentPart.Dispose();
                }

                // Gửi response thành công
                await SendSuccessResponse(context, uploadedFiles, failedFiles);
            }
            catch (Exception ex)
            {
                await SendErrorResponse(context, 500, "Lỗi khi upload: " + ex.Message);
                UpdateLog($"[{clientIp}] Lỗi khi upload: {ex.Message}", true);
            }
        }

        // Thêm phương thức ParseHeaders
        private void ParseHeaders(byte[] data, Dictionary<string, string> headers)
        {
            string headerText = Encoding.UTF8.GetString(data);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                int colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    string key = line.Substring(0, colonIndex).Trim();
                    string value = line.Substring(colonIndex + 1).Trim();
                    headers[key] = value;
                }
            }
        }

        // Thêm phương thức này vào class
        private int FindHeaderEnd(byte[] data)
        {
            // Tìm vị trí của \r\n\r\n (ký tự kết thúc header)
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 0x0D && data[i + 1] == 0x0A &&
                    data[i + 2] == 0x0D && data[i + 3] == 0x0A)
                {
                    return i;
                }
            }
            return -1;
        }

        private async Task SendErrorResponse(HttpListenerContext context, int statusCode, string message)
        {
            var errHtml = GenerateErrorPageHtml(message);
            var buf = Encoding.UTF8.GetBytes(errHtml);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html; charset=UTF-8";
            context.Response.ContentLength64 = buf.LongLength;
            await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
            context.Response.OutputStream.Close();
        }

        private async Task SendSuccessResponse(HttpListenerContext context, List<string> successFiles, List<string> failedFiles)
        {
            var okHtml = GenerateSuccessPageHtml(successFiles, failedFiles);
            var okBuf = Encoding.UTF8.GetBytes(okHtml);
            context.Response.ContentType = "text/html; charset=UTF-8";
            context.Response.ContentLength64 = okBuf.LongLength;
            await context.Response.OutputStream.WriteAsync(okBuf, 0, okBuf.Length);
            context.Response.OutputStream.Close();
        }

        // Tìm mảng con trong mảng byte
        private int IndexOf(byte[] searchIn, byte[] searchBytes, int startIndex)
        {
            for (int i = startIndex; i <= searchIn.Length - searchBytes.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < searchBytes.Length; j++)
                {
                    if (searchIn[i + j] != searchBytes[j]) { found = false; break; }
                }
                if (found) return i;
            }
            return -1;
        }

        // Lấy boundary từ Content-Type
        private string GetBoundary(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return null;
            foreach (var part in contentType.Split(';'))
            {
                var t = part.Trim();
                if (t.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
                    return t.Substring("boundary=".Length).Trim('"');
            }
            return null;
        }

        // Parse tên file từ header Content-Disposition (hỗ trợ cả filename*=?)
        private string ExtractFileNameFromContentDisposition(string headerText)
        {
            // filename*=
            int idxStar = headerText.IndexOf("filename*=", StringComparison.OrdinalIgnoreCase);
            if (idxStar >= 0)
            {
                int start = idxStar + 10;
                // lấy đến hết dòng
                int lineEnd = headerText.IndexOf("\r\n", start);
                string val = (lineEnd > start ? headerText.Substring(start, lineEnd - start) : headerText.Substring(start)).Trim();
                // Ví dụ: UTF-8''Screenshot%202024-12-15.png
                int apos = val.IndexOf("''", StringComparison.Ordinal);
                if (apos > 0)
                {
                    string encodedName = val.Substring(apos + 2);
                    return Uri.UnescapeDataString(encodedName).Trim('"');
                }
            }

            // filename="..."
            int idx = headerText.IndexOf("filename=\"", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int start = idx + 10;
                int end = headerText.IndexOf("\"", start);
                if (end > start) return headerText.Substring(start, end - start);
            }

            // filename=không_dấu_ngoặc
            idx = headerText.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int start = idx + 9;
                int lineEnd = headerText.IndexOf("\r\n", start);
                string raw = (lineEnd > start ? headerText.Substring(start, lineEnd - start) : headerText.Substring(start)).Trim();
                return raw.Trim('"');
            }

            return null;
        }

        // Làm sạch tên file (loại ký tự cấm Windows) + tránh trùng
        private string GetSafeFilename(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return "unknown";
            string normalized = filename.Normalize(NormalizationForm.FormC);
            return string.Concat(normalized.Split(Path.GetInvalidFileNameChars()));
        }

        private string GetUniqueFilename(string folder, string filename)
        {
            string safeName = GetSafeFilename(filename);
            string name = Path.GetFileNameWithoutExtension(safeName);
            string ext = Path.GetExtension(safeName);
            string candidate = safeName;
            int i = 1;
            while (File.Exists(Path.Combine(folder, candidate)))
            {
                candidate = $"{name}_{i}{ext}";
                i++;
            }
            return candidate;
        }


      



        public class MultipartParser
        {
            private Stream _stream;
            private byte[] _boundaryBytes;
            private byte[] _boundaryEndBytes;
            private bool _isFirstPart = true;
            private readonly MainForm _mainForm;
            private readonly Queue<byte> _pushback = new Queue<byte>();


            public string Filename { get; private set; }
            public string ContentType { get; private set; }

            public MultipartParser(Stream stream, string boundary, MainForm mainForm)
            {
                _stream = stream;
                _boundaryBytes = Encoding.ASCII.GetBytes($"--{boundary}\r\n");
                _boundaryEndBytes = Encoding.ASCII.GetBytes($"--{boundary}--\r\n");
                _mainForm = mainForm;
            }

            // Đọc phần tiếp theo của dữ liệu multipart
            public bool ReadNextPart()
            {
                if (_isFirstPart)
                {
                    if (!SkipBoundary()) return false;
                    _isFirstPart = false;
                }
                else
                {
                    if (!SkipBoundary()) return false;
                }

                // Reset filename và content type cho mỗi part
                Filename = null;
                ContentType = null;

                string headerLine;
                while (!string.IsNullOrEmpty(headerLine = ReadLine()))
                {
                    if (headerLine.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase))
                    {
                        Match encodedMatch = Regex.Match(headerLine, "filename\\*=UTF-8''([^\"]*)");
                        if (encodedMatch.Success)
                        {
                            Filename = WebUtility.UrlDecode(encodedMatch.Groups[1].Value);
                        }
                        else
                        {
                            Match standardMatch = Regex.Match(headerLine, "filename=\"([^\"]*)\"");
                            if (standardMatch.Success)
                            {
                                string rawFilename = standardMatch.Groups[1].Value;
                                string fixedFilename = FixVietnameseEncoding(rawFilename);
                                if (fixedFilename.Contains("�") || fixedFilename.Contains("Ã") || fixedFilename == rawFilename)
                                    fixedFilename = FixVietnameseCharacters(rawFilename);
                                Filename = fixedFilename.Normalize(NormalizationForm.FormC);
                            }
                        }
                    }
                    else if (headerLine.StartsWith("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        ContentType = headerLine.Substring(headerLine.IndexOf(':') + 1).Trim();
                    }
                }

                return !string.IsNullOrEmpty(Filename);
            }

            private string FixVietnameseEncoding(string input)
            {
                // Chuyển chuỗi từ Windows-1252 sang UTF-8
                // Nếu input là chuỗi bị lỗi, nó thực chất là bytes UTF-8 được đọc bằng Windows-1252
                byte[] bytes = Encoding.Default.GetBytes(input);
                return Encoding.UTF8.GetString(bytes);
            }

            private string FixVietnameseCharacters(string input)
            {
                var pairs = new[]
                {
                    // Ký tự thường
                    new[] { "Ã ", "à" }, new[] { "Ã¡", "á" }, new[] { "áº£", "ả" }, new[] { "Ã£", "ã" }, new[] { "áº¡", "ạ" },
                    new[] { "Ä", "ă" }, new[] { "áº±", "ằ" }, new[] { "áº¯", "ắ" }, new[] { "áº³", "ẳ" }, new[] { "áºµ", "ẵ" }, new[] { "áº·", "ặ" },
                    new[] { "Ã¢", "â" }, new[] { "áº§", "ầ" }, new[] { "áº¥", "ấ" }, new[] { "áº©", "ẩ" }, new[] { "áº«", "ẫ" }, new[] { "áº­", "ậ" },
                    new[] { "Ã¨", "è" }, new[] { "Ã©", "é" }, new[] { "áº»", "ẻ" }, new[] { "áº½", "ẽ" }, new[] { "áº¹", "ẹ" },
                    new[] { "Ãª", "ê" }, new[] { "á»", "ề" }, new[] { "á»", "ể" }, new[] { "á»", "ễ" }, new[] { "á»‡", "ệ" }, new[] { "á»‚", "ế" }, new[] { "áº¿", "ế" },
                    new[] { "Ã¬", "ì" }, new[] { "Ã­", "í" }, new[] { "á»", "ỉ" }, new[] { "á»", "ị" }, new[] { "Ä©", "ĩ" },
                    new[] { "Ã²", "ò" }, new[] { "Ã³", "ó" }, new[] { "á»", "ỏ" }, new[] { "á»", "ọ" }, new[] { "Ãµ", "õ" }, new[] { "", "ọ" }, // Bổ sung ánh xạ cho "lọc"
                    new[] { "Ã´", "ô" }, new[] { "á»", "ồ" }, new[] { "á»", "ố" }, new[] { "á»", "ổ" }, new[] { "á»", "ỗ" }, new[] { "á»", "ộ" },
                    new[] { "Æ¡", "ơ" }, new[] { "á»", "ờ" }, new[] { "á»", "ớ" }, new[] { "á»", "ở" }, new[] { "á»¡", "ỡ" }, new[] { "á»£", "ợ" },
                    new[] { "Ã¹", "ù" }, new[] { "Ãº", "ú" }, new[] { "á»§", "ủ" }, new[] { "á»©", "ụ" }, new[] { "Å©", "ũ" },
                    new[] { "Æ°", "ư" }, new[] { "á»«", "ừ" }, new[] { "á»©", "ứ" }, new[] { "á»­", "ử" }, new[] { "á»¯", "ữ" }, new[] { "á»±", "ự" },
                    new[] { "Ã½", "ý" }, new[] { "á»³", "ỳ" }, new[] { "á»µ", "ỵ" }, new[] { "á»·", "ỷ" }, new[] { "á»¹", "ỹ" },
                    new[] { "Ä", "đ" },
                    // Ký tự hoa (chỉ thêm nếu chưa có key trùng)
                    new[] { "Ã€", "À" }, new[] { "Ã", "Á" }, new[] { "áº¢", "Ả" }, new[] { "Ãƒ", "Ã" }, new[] { "áº ", "Ạ" },
                    new[] { "Ä‚", "Ă" }, new[] { "áº°", "Ằ" }, new[] { "áº®", "Ắ" }, new[] { "áº²", "Ẳ" }, new[] { "áº´", "Ẵ" }, new[] { "áº¶", "Ặ" },
                    new[] { "Ã‚", "Â" }, new[] { "áº¦", "Ầ" }, new[] { "áº¤", "Ấ" }, new[] { "áº¨", "Ẩ" }, new[] { "áºª", "Ẫ" }, new[] { "áº¬", "Ậ" },
                    new[] { "Ãˆ", "È" }, new[] { "Ã‰", "É" }, new[] { "áºº", "Ẻ" }, new[] { "áº¼", "Ẽ" }, new[] { "áº¸", "Ẹ" },
                    new[] { "ÃŠ", "Ê" }, new[] { "á»€", "Ề" }, new[] { "á»„", "Ể" }, new[] { "á»†", "Ệ" }, new[] { "á»ƒ", "Ế" }, new[] { "á»…", "Ễ" },
                    new[] { "ÃŒ", "Ì" }, new[] { "Ã", "Í" }, new[] { "á»ˆ", "Ỉ" }, new[] { "á»Š", "Ị" }, new[] { "Ä¨", "Ĩ" },
                    new[] { "Ã’", "Ò" }, new[] { "Ã“", "Ó" }, new[] { "á»Ž", "Ỏ" }, new[] { "á»", "Ọ" }, new[] { "Ã•", "Õ" },
                    new[] { "Ã", "Ô" }, new[] { "á»’", "Ồ" }, new[] { "á»“", "Ố" }, new[] { "á»•", "Ổ" }, new[] { "á»—", "Ỗ" }, new[] { "á»™", "Ộ" },
                    new[] { "Æ ", "Ơ" }, new[] { "á»œ", "Ờ" }, new[] { "á»š", "Ớ" }, new[] { "á»ž", "Ở" }, new[] { "á» ", "Ỡ" }, new[] { "á»¢", "Ợ" },
                    new[] { "Ã™", "Ù" }, new[] { "Ãš", "Ú" }, new[] { "á»¦", "Ủ" }, new[] { "á»¨", "Ụ" }, new[] { "Å¨", "Ũ" },
                    new[] { "Æ¯", "Ư" }, new[] { "á»ª", "Ừ" }, new[] { "á»¨", "Ứ" }, new[] { "á»¬", "Ử" }, new[] { "á»®", "Ữ" }, new[] { "á»°", "Ự" },
                    new[] { "Ã", "Ý" }, new[] { "á»²", "Ỳ" }, new[] { "á»´", "Ỵ" }, new[] { "á»¶", "Ỷ" }, new[] { "á»¸", "Ỹ" },
                    new[] { "Ä", "Đ" }
                };

                var replacementMap = new Dictionary<string, string>();
                foreach (var pair in pairs)
                {
                    if (!replacementMap.ContainsKey(pair[0]))
                        replacementMap.Add(pair[0], pair[1]);
                }

                var sortedKeys = replacementMap.Keys.OrderByDescending(k => k.Length);
                foreach (var key in sortedKeys)
                {
                    input = input.Replace(key, replacementMap[key]);
                }
                return input;
            }

            public void WritePartDataTo(Stream outputStream)
            {
                byte[] buffer = new byte[8192];
                int bytesRead;
                int bytesInBuffer = 0;

                // Giữ lại đuôi để dò boundary cắt ngang block
                int keepTail = _boundaryEndBytes.Length + _boundaryBytes.Length;
                if (keepTail < 4) keepTail = 4;
                byte[] tail = new byte[keepTail];

                while ((bytesRead = _stream.Read(buffer, bytesInBuffer, buffer.Length - bytesInBuffer)) > 0)
                {
                    bytesInBuffer += bytesRead;
                    int boundaryIndex = FindBoundary(buffer, bytesInBuffer);

                    if (boundaryIndex >= 0)
                    {
                        // ghi dữ liệu của part (loại bỏ "\r\n" ngay trước boundary)
                        int toWrite = Math.Max(0, boundaryIndex - 2);
                        if (toWrite > 0) outputStream.Write(buffer, 0, toWrite);

                        // Đẩy lại PHẦN DƯ (từ boundary trở đi) vào _pushback
                        for (int i = boundaryIndex; i < bytesInBuffer; i++)
                            _pushback.Enqueue(buffer[i]);

                        return;
                    }
                    else
                    {
                        // không thấy boundary: ghi ra trừ phần đuôi để ghép lần sau
                        int toWrite = Math.Max(0, bytesInBuffer - keepTail);
                        if (toWrite > 0) outputStream.Write(buffer, 0, toWrite);

                        // copy phần đuôi sang đầu buffer cho lần đọc kế tiếp
                        int remain = bytesInBuffer - toWrite;
                        if (remain > 0)
                        {
                            Array.Copy(buffer, toWrite, buffer, 0, remain);
                        }
                        bytesInBuffer = remain;
                    }
                }

                // hết stream mà không gặp boundary (trường hợp cuối cùng)
                if (bytesInBuffer > 0)
                {
                    outputStream.Write(buffer, 0, bytesInBuffer);
                    bytesInBuffer = 0;
                }
            }

            private int ReadBytesInternal(byte[] buffer, int offset, int count)
            {
                int written = 0;
                // 1) rút từ pushback trước
                while (written < count && _pushback.Count > 0)
                {
                    buffer[offset + written] = _pushback.Dequeue();
                    written++;
                }
                // 2) nếu còn thiếu thì đọc từ stream
                if (written < count)
                {
                    int n = _stream.Read(buffer, offset + written, count - written);
                    if (n > 0) written += n;
                }
                return written;
            }

            private int ReadByteInternal()
            {
                if (_pushback.Count > 0) return _pushback.Dequeue();
                return _stream.ReadByte();
            }


            private string ReadLine()
            {
                var sb = new StringBuilder();
                int b;
                while ((b = ReadByteInternal()) != -1)
                {
                    if (b == '\n')
                    {
                        string line = sb.ToString().TrimEnd('\r');
                        return line;
                    }
                    sb.Append((char)b);
                }
                return null;
            }


            private bool SkipBoundary()
            {
                byte[] buffer = new byte[_boundaryBytes.Length];
                int got = ReadBytesInternal(buffer, 0, buffer.Length);
                if (got != buffer.Length) return false;
                return ByteArrayEquals(buffer, _boundaryBytes);
            }


            private int FindBoundary(byte[] buffer, int length)
            {
                for (int i = 0; i <= length - _boundaryBytes.Length; i++)
                {
                    if (ByteArrayEquals(buffer, _boundaryBytes, i) || ByteArrayEquals(buffer, _boundaryEndBytes, i))
                    {
                        return i;
                    }
                }
                return -1;
            }

            private bool ByteArrayEquals(byte[] array1, byte[] array2, int offset = 0)
            {
                if (array1.Length - offset < array2.Length) return false;
                for (int i = 0; i < array2.Length; i++)
                {
                    if (array1[i + offset] != array2[i]) return false;
                }
                return true;
            }
        }

        private string GetContentType(string extension)
        {
            var contentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Text files - mở trực tiếp trên trình duyệt
                { ".txt", "text/plain; charset=utf-8" },
                { ".html", "text/html; charset=utf-8" },
                { ".htm", "text/html; charset=utf-8" },
                { ".css", "text/css" },
                { ".js", "application/javascript" },
                { ".json", "application/json" },
                { ".xml", "application/xml" },
        
                // Image files - mở trực tiếp trên trình duyệt
                { ".jpg", "image/jpeg" },
                { ".jpeg", "image/jpeg" },
                { ".png", "image/png" },
                { ".gif", "image/gif" },
                { ".bmp", "image/bmp" },
                { ".webp", "image/webp" },
                { ".svg", "image/svg+xml" },
                { ".ico", "image/x-icon" },
        
                // PDF - có thể mở trên trình duyệt
                { ".pdf", "application/pdf" },
        
                // Audio/Video - có thể mở trên trình duyệt
                { ".mp3", "audio/mpeg" },
                { ".mp4", "video/mp4" },
                { ".webm", "video/webm" },
                { ".ogg", "audio/ogg" },
                { ".wav", "audio/wav" },
        
                // Documents
                { ".doc", "application/msword" },
                { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
                { ".xls", "application/vnd.ms-excel" },
                { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
                { ".ppt", "application/vnd.ms-powerpoint" },
                { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
        
                // Archive files
                { ".zip", "application/zip" },
                { ".rar", "application/x-rar-compressed" },
                { ".7z", "application/x-7z-compressed" },
                { ".tar", "application/x-tar" },
                { ".gz", "application/gzip" },
        
                // Other
                { ".iso", "application/x-iso9660-image" },
                { ".img", "application/octet-stream" },
                { ".exe", "application/octet-stream" },
                { ".dll", "application/octet-stream" }
            };

            return contentTypes.TryGetValue(extension, out string contentType)
                ? contentType
                : "application/octet-stream";
        }

        private void StopSharing()
        {
            if (_listener != null && _listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
                _listener = null;
                UpdateLog("\r\n--- Ứng dụng đã dừng chia sẻ ---");
            }
            notifyIcon.Text = "Ứng dụng chia sẻ file đã dừng";
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() =>
                {
                    btnStart.Enabled = true;
                    btnChooseFolder.Enabled = true;
                    txtPort.Enabled = true;
                    btnStop.Enabled = false;
                }));
            }
            else
            {
                btnStart.Enabled = true;
                btnChooseFolder.Enabled = true;
                txtPort.Enabled = true;
                btnStop.Enabled = false;
            }
        }

        // Replace the GenerateDirectoryListingHtml method with this improved version:
        private string GenerateDirectoryListingHtml(string currentPath, string relativePath)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>");
            sb.Append("<html lang=\"vi\">");
            sb.Append("<head>");
            sb.Append("<meta charset=\"UTF-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.Append("<title>Danh sách tập tin</title>");
            sb.Append("<style>");
            sb.Append("body{font-family:Arial, sans-serif; margin:20px;}");
            sb.Append("table{border-collapse:collapse; width:100%;}");
            sb.Append("th,td{border:1px solid #ccc; padding:8px; text-align:left;}");
            sb.Append("th{background:#f4f4f4;}");
            sb.Append("tr:nth-child(even){background:#fafafa;}");
            sb.Append("a{text-decoration:none; color:#0366d6;}");
            sb.Append("a:hover{text-decoration:underline;}");
            sb.Append("</style>");
            sb.Append("</head>");
            sb.Append("<body>");
            sb.AppendFormat("<h2>Thư mục: {0}</h2>", WebUtility.HtmlEncode(relativePath));

            sb.Append("<table>");
            sb.Append("<tr><th>Tên</th><th>Kích thước</th><th>Type</th></tr>");

            // Thư mục cha
            if (relativePath != "/")
            {
                string parentRelative = Path.GetDirectoryName(relativePath.TrimEnd(Path.DirectorySeparatorChar, '/'))
                                       ?.Replace("\\", "/");
                if (string.IsNullOrEmpty(parentRelative)) parentRelative = "/";

                string encodedParent = SafeEncode(parentRelative); // ← Sử dụng SafeEncode

                sb.Append("<tr>");
                sb.AppendFormat("<td colspan=\"3\"><a href=\"{0}\">↩ Quay lại</a></td>", encodedParent);
                sb.Append("</tr>");
            }

            // Danh sách thư mục con
            foreach (var dir in Directory.GetDirectories(currentPath))
            {
                string dirName = Path.GetFileName(dir);
                string urlPath = (relativePath.TrimEnd('/') + "/" + dirName).Replace("\\", "/");
                string encodedPath = SafeEncode(urlPath); // ← Sử dụng SafeEncode thay vì WebUtility.HtmlEncode

                sb.Append("<tr>");
                sb.AppendFormat("<td><a href=\"{0}\">📁 {1}</a></td>", encodedPath, WebUtility.HtmlEncode(dirName));
                sb.Append("<td>-</td>");
                sb.Append("<td>Thư mục</td>");
                sb.Append("</tr>");
            }

            // Danh sách file
            foreach (var file in Directory.GetFiles(currentPath))
            {
                string fileName = Path.GetFileName(file);
                string urlPath = (relativePath.TrimEnd('/') + "/" + fileName).Replace("\\", "/");
                string encodedPath = SafeEncode(urlPath); // ← Sử dụng SafeEncode thay vì WebUtility.HtmlEncode

                FileInfo fi = new FileInfo(file);
                string sizeStr = FormatFileSize(fi.Length);
                string extension = Path.GetExtension(file).ToLower();

                sb.Append("<tr>");
                sb.AppendFormat("<td><a href=\"{0}\">📄 {1}</a></td>", encodedPath, WebUtility.HtmlEncode(fileName));
                sb.AppendFormat("<td>{0}</td>", sizeStr);
                sb.AppendFormat("<td>{0}</td>", extension);
                sb.Append("</tr>");
            }

            sb.Append("</table>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            else if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:0.00} KB";
            else if (bytes < 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):0.00} MB";
            else
                return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
        }


        private string GetLocalIPAddress()
        {
            string localIP = "N/A";
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    localIP = ip.ToString();
                    break;
                }
            }
            return localIP;
        }

        private void UpdateLog(string message, bool isError = false)
        {
            string prefix = isError ? "❖ [!] " : "⚡ "; // Giữ nguyên phần prefix nếu cần highlight lỗi
            string timePart = $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}]"; // Định dạng thời gian trong []
            string formattedMessage = $"{prefix}{timePart}\n {message}"; // Kết hợp thành chuỗi hoàn chỉnh

            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() =>
                {
                    txtLog.AppendText(formattedMessage + "\r\n");
                }));
            }
            else
            {
                txtLog.AppendText(formattedMessage + "\r\n");
            }
        }

        // Thêm phương thức xử lý WebDAV
        private async Task HandleWebDAVRequest(HttpListenerContext context)
        {
            string clientIp = context.Request.RemoteEndPoint.Address.ToString();

            // Thêm headers cho WebDAV
            context.Response.Headers.Add("DAV", "1, 2");
            context.Response.Headers.Add("MS-Author-Via", "DAV");

            if (context.Request.HttpMethod == "OPTIONS")
            {
                // Trả về headers cho WebDAV
                context.Response.Headers.Add("DAV", "1, 2");
                context.Response.Headers.Add("MS-Author-Via", "DAV");
                context.Response.Headers.Add("Allow", "OPTIONS, GET, HEAD, POST, PUT, DELETE, PROPFIND, PROPPATCH, COPY, MOVE, MKCOL, LOCK, UNLOCK");
                context.Response.Headers.Add("Public", "OPTIONS, GET, HEAD, POST, PUT, DELETE, PROPFIND, PROPPATCH, COPY, MOVE, MKCOL, LOCK, UNLOCK");

                context.Response.StatusCode = 200;
                context.Response.Close();
                UpdateLog($"[{clientIp}] WebDAV OPTIONS request");
                return;
            }
            else if (context.Request.HttpMethod == "PROPFIND")
            {
                // Xử lý PROPFIND request (WebDAV directory listing)
                await HandlePropFindRequest(context);
                return;
            }

            // Với các method khác, chuyển về xử lý thông thường
            await ProcessRequest(context);
        }

        private async Task HandlePropFindRequest(HttpListenerContext context)
        {
            string clientIp = context.Request.RemoteEndPoint.Address.ToString();
            string relativePath = context.Request.Url.AbsolutePath;
            relativePath = SafeDecode(relativePath);

            string root = Path.GetFullPath(_sharedFolderPath);
            string requestSubPath = relativePath.TrimStart('/')
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            string fullPath = Path.GetFullPath(Path.Combine(root, requestSubPath));

            // Ngăn chặn directory traversal
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            try
            {
                if (Directory.Exists(fullPath))
                {
                    string propfindResponse = GenerateWebDAVPropFindResponse(fullPath, relativePath);
                    byte[] buffer = Encoding.UTF8.GetBytes(propfindResponse);

                    context.Response.ContentType = "text/xml; charset=\"utf-8\"";
                    context.Response.ContentLength64 = buffer.Length;
                    context.Response.StatusCode = 207; // Multi-Status

                    await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    context.Response.OutputStream.Close();

                    UpdateLog($"[{clientIp}] WebDAV PROPFIND: {relativePath}");
                }
                else if (File.Exists(fullPath))
                {
                    // Trả về thông tin file
                    string propfindResponse = GenerateWebDAVFilePropFindResponse(fullPath, relativePath);
                    byte[] buffer = Encoding.UTF8.GetBytes(propfindResponse);

                    context.Response.ContentType = "text/xml; charset=\"utf-8\"";
                    context.Response.ContentLength64 = buffer.Length;
                    context.Response.StatusCode = 207; // Multi-Status

                    await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    context.Response.OutputStream.Close();

                    UpdateLog($"[{clientIp}] WebDAV PROPFIND file: {relativePath}");
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
                UpdateLog($"[{clientIp}] WebDAV error: {ex.Message}");
            }
        }

        private string GenerateWebDAVPropFindResponse(string directoryPath, string relativePath)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<D:multistatus xmlns:D=\"DAV:\" xmlns:Z=\"urn:schemas-microsoft-com:\">");

            // Thêm chính thư mục đó
            sb.AppendFormat("<D:response>");
            sb.AppendFormat("<D:href>{0}</D:href>", WebUtility.HtmlEncode(relativePath.EndsWith("/") ? relativePath : relativePath + "/"));
            sb.Append("<D:propstat>");
            sb.Append("<D:prop>");
            sb.Append("<D:resourcetype><D:collection/></D:resourcetype>");
            sb.Append("<D:displayname>" + WebUtility.HtmlEncode(Path.GetFileName(directoryPath)) + "</D:displayname>");
            sb.Append("<D:getlastmodified>" + DateTime.Now.ToString("R") + "</D:getlastmodified>");
            sb.Append("</D:prop>");
            sb.Append("<D:status>HTTP/1.1 200 OK</D:status>");
            sb.Append("</D:propstat>");
            sb.Append("</D:response>");

            // Thêm các thư mục con
            foreach (var dir in Directory.GetDirectories(directoryPath))
            {
                string dirName = Path.GetFileName(dir);
                string href = relativePath.TrimEnd('/') + "/" + SafeEncode(dirName) + "/";

                sb.AppendFormat("<D:response>");
                sb.AppendFormat("<D:href>{0}</D:href>", WebUtility.HtmlEncode(href));
                sb.Append("<D:propstat>");
                sb.Append("<D:prop>");
                sb.Append("<D:resourcetype><D:collection/></D:resourcetype>");
                sb.Append("<D:displayname>" + WebUtility.HtmlEncode(dirName) + "</D:displayname>");
                sb.Append("<D:getlastmodified>" + DateTime.Now.ToString("R") + "</D:getlastmodified>");
                sb.Append("</D:prop>");
                sb.Append("<D:status>HTTP/1.1 200 OK</D:status>");
                sb.Append("</D:propstat>");
                sb.Append("</D:response>");
            }

            // Thêm các file
            foreach (var file in Directory.GetFiles(directoryPath))
            {
                string fileName = Path.GetFileName(file);
                string href = relativePath.TrimEnd('/') + "/" + SafeEncode(fileName);
                FileInfo fi = new FileInfo(file);

                sb.AppendFormat("<D:response>");
                sb.AppendFormat("<D:href>{0}</D:href>", WebUtility.HtmlEncode(href));
                sb.Append("<D:propstat>");
                sb.Append("<D:prop>");
                sb.Append("<D:resourcetype/>");
                sb.Append("<D:displayname>" + WebUtility.HtmlEncode(fileName) + "</D:displayname>");
                sb.Append("<D:getcontentlength>" + fi.Length + "</D:getcontentlength>");
                sb.Append("<D:getlastmodified>" + fi.LastWriteTime.ToString("R") + "</D:getlastmodified>");
                sb.Append("<D:getcontenttype>" + GetContentType(Path.GetExtension(file)) + "</D:getcontenttype>");
                sb.Append("</D:prop>");
                sb.Append("<D:status>HTTP/1.1 200 OK</D:status>");
                sb.Append("</D:propstat>");
                sb.Append("</D:response>");
            }

            sb.Append("</D:multistatus>");
            return sb.ToString();
        }

        private string GenerateWebDAVFilePropFindResponse(string filePath, string relativePath)
        {
            FileInfo fi = new FileInfo(filePath);

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<D:multistatus xmlns:D=\"DAV:\" xmlns:Z=\"urn:schemas-microsoft-com:\">");

            sb.AppendFormat("<D:response>");
            sb.AppendFormat("<D:href>{0}</D:href>", WebUtility.HtmlEncode(relativePath));
            sb.Append("<D:propstat>");
            sb.Append("<D:prop>");
            sb.Append("<D:resourcetype/>");
            sb.Append("<D:displayname>" + WebUtility.HtmlEncode(Path.GetFileName(filePath)) + "</D:displayname>");
            sb.Append("<D:getcontentlength>" + fi.Length + "</D:getcontentlength>");
            sb.Append("<D:getlastmodified>" + fi.LastWriteTime.ToString("R") + "</D:getlastmodified>");
            sb.Append("<D:getcontenttype>" + GetContentType(Path.GetExtension(filePath)) + "</D:getcontenttype>");
            sb.Append("</D:prop>");
            sb.Append("<D:status>HTTP/1.1 200 OK</D:status>");
            sb.Append("</D:propstat>");
            sb.Append("</D:response>");

            sb.Append("</D:multistatus>");
            return sb.ToString();
        }
        //Phương thức lấy tên máy tính
        private string GetComputerName()
        {
            try
            {
                return System.Environment.MachineName;
            }
            catch
            {
                return "SHAREFILE";
            }
        }

        private System.Windows.Forms.Button btnStart;
        private System.Windows.Forms.Button btnStop;
        private System.Windows.Forms.Button btnChooseFolder;
        private System.Windows.Forms.TextBox txtPort;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label lblFolderPath;
        private System.Windows.Forms.TextBox txtLog;

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.btnStart = new System.Windows.Forms.Button();
            this.btnStop = new System.Windows.Forms.Button();
            this.btnChooseFolder = new System.Windows.Forms.Button();
            this.txtPort = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.lblFolderPath = new System.Windows.Forms.Label();
            this.txtLog = new System.Windows.Forms.TextBox();
            this.toolTip1 = new System.Windows.Forms.ToolTip(this.components);
            this.SuspendLayout();
            // 
            // btnStart
            // 
            this.btnStart.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnStart.Location = new System.Drawing.Point(230, 5);
            this.btnStart.Name = "btnStart";
            this.btnStart.Size = new System.Drawing.Size(95, 36);
            this.btnStart.TabIndex = 0;
            this.btnStart.Text = "Bắt đầu chia sẻ";
            this.btnStart.UseVisualStyleBackColor = true;
            this.btnStart.Click += new System.EventHandler(this.btnStart_Click);
            // 
            // btnStop
            // 
            this.btnStop.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnStop.Location = new System.Drawing.Point(333, 5);
            this.btnStop.Name = "btnStop";
            this.btnStop.Size = new System.Drawing.Size(95, 36);
            this.btnStop.TabIndex = 1;
            this.btnStop.Text = "Dừng chia sẻ";
            this.btnStop.UseVisualStyleBackColor = true;
            this.btnStop.Click += new System.EventHandler(this.btnStop_Click);
            // 
            // btnChooseFolder
            // 
            this.btnChooseFolder.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnChooseFolder.Location = new System.Drawing.Point(12, 36);
            this.btnChooseFolder.Name = "btnChooseFolder";
            this.btnChooseFolder.Size = new System.Drawing.Size(100, 32);
            this.btnChooseFolder.TabIndex = 2;
            this.btnChooseFolder.Text = "Chọn Thư mục";
            this.btnChooseFolder.UseVisualStyleBackColor = true;
            this.btnChooseFolder.Click += new System.EventHandler(this.btnChooseFolder_Click);
            // 
            // txtPort
            // 
            this.txtPort.Location = new System.Drawing.Point(131, 13);
            this.txtPort.Name = "txtPort";
            this.txtPort.Size = new System.Drawing.Size(65, 20);
            this.txtPort.TabIndex = 3;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label1.Location = new System.Drawing.Point(48, 16);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(64, 15);
            this.label1.TabIndex = 4;
            this.label1.Text = "Nhập Port:";
            // 
            // lblFolderPath
            // 
            this.lblFolderPath.AutoSize = true;
            this.lblFolderPath.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblFolderPath.Location = new System.Drawing.Point(118, 45);
            this.lblFolderPath.Name = "lblFolderPath";
            this.lblFolderPath.Size = new System.Drawing.Size(115, 15);
            this.lblFolderPath.TabIndex = 5;
            this.lblFolderPath.Text = "Đường dẫn đã chọn:";
            // 
            // txtLog
            // 
            this.txtLog.Font = new System.Drawing.Font("Tahoma", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtLog.Location = new System.Drawing.Point(12, 70);
            this.txtLog.Multiline = true;
            this.txtLog.Name = "txtLog";
            this.txtLog.ReadOnly = true;
            this.txtLog.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtLog.Size = new System.Drawing.Size(416, 230);
            this.txtLog.TabIndex = 6;
            // 
            // MainForm
            // 
            this.ClientSize = new System.Drawing.Size(440, 312);
            this.Controls.Add(this.txtLog);
            this.Controls.Add(this.lblFolderPath);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.txtPort);
            this.Controls.Add(this.btnChooseFolder);
            this.Controls.Add(this.btnStop);
            this.Controls.Add(this.btnStart);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Name = "MainForm";
            this.Text = "Share Share";
            this.ResumeLayout(false);
            this.PerformLayout();

        }
    }
}