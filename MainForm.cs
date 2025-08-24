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
using System.Web;

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
            UpdateLog("Developer by ©Nông Văn Phấn");

            // Đảm bảo ngăn sleep đã được kích hoạt nhưng KHÔNG hiển thị log
            if (!_preventSleep)
            {
                PreventSleep(false); // Không hiển thị log tự động
            }

            // HIỂN THỊ DÒNG NÀY SAU
            UpdateLog("Đã kích hoạt chế độ ngăn máy tính sleep");
            notifyIcon.Text = "Ứng dụng chia sẻ file đã sẵn sàng";

            //TỰ ĐỘNG BẮT ĐẦU CHIA SẺ NGAY KHI MỞ APP
            btnStart.PerformClick();
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
                UpdateLog("📁 TRUY CẬP TỪ TRÌNH DUYỆT:");
                UpdateLog($"   http://{localIP}:{port}/upload");
                UpdateLog($"   Để upload chia sẻ file");
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

            // Nếu yêu cầu favicon.ico thì trả về icon ứng dụng
            if (string.Equals(relativePath, "/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                using (var ms = new MemoryStream())
                {
                    // Nếu ứng dụng có Icon thì dùng, không thì dùng icon mặc định của Windows
                    Icon icon = this.Icon ?? SystemIcons.Application;

                    using (var bmp = icon.ToBitmap())
                    {
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    }

                    byte[] buffer = ms.ToArray();
                    context.Response.ContentType = "image/png";
                    context.Response.ContentLength64 = buffer.Length;
                    await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    context.Response.OutputStream.Close();
                }
                return;
            }


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
                await HandleFileUpload(context);
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

                    if (extension == ".txt" || extension == ".ini" || extension == ".html" || extension == ".htm" || extension == ".md" ||
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
                ".txt", ".html", ".htm", ".css", ".js", ".json", ".xml", ".md", ".ini",
        
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
                .Replace("%2B", "~PLUS~")    // + (uncomment để bảo vệ + gốc)
                .Replace("%3D", "~EQUAL~")   // =
                .Replace("%28", "(")
                .Replace("%29", ")")
                .Replace("%2F", "/")
                .Replace("%5B", "[")
                .Replace("%5D", "]")
                .Replace("%20", "~")
                .Replace("%21", "!")
                .Replace("%40", "@");

                //.Replace("%21", "!")
                //.Replace("%40", "@")
                //.Replace("%23", "~hash~");
                //.Replace("%24", "$")
                //.Replace("%25", "%")
                //.Replace("%5E", "^")
                //.Replace("%26", "&")
                //.Replace("%28", "(")
                //.Replace("%29", ")")
                //.Replace("%60", "`")
                //.Replace("%7E", "~")
                //.Replace("%7B", "{")
                //.Replace("%7D", "}")
                //.Replace("%5B", "[")
                //.Replace("%5D", "]");

            // Encode URL
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
                .Replace("~PLUS~", "%2B")    // Uncomment để bảo vệ + gốc
                .Replace("~EQUAL~", "%3D")
                .Replace("/", "%2F")
                .Replace("(", "%28")
                .Replace(")", "%29")
                .Replace("[", "%5B")
                .Replace("]", "%5D")
                .Replace("!", "%21")
                .Replace("~", "%20")
                .Replace("@", "%40");

                //.Replace("!", "%21")
                //.Replace("@", "%40")
                //.Replace("#", "%23")
                //.Replace("$", "%24")
                //.Replace("%", "%25")
                //.Replace("^", "%5E")
                //.Replace("&", "%26")
                //.Replace("(", "%28")
                //.Replace(")", "%29")
                //.Replace("`", "%60")
                //.Replace("~", "%7E")
                //.Replace("{", "%7B")
                //.Replace("}", "%7D")
                //.Replace("[", "%5B")
                //.Replace("]", "%5D");

            // Decode URL
            return Uri.UnescapeDataString(decoded);
        }

        public class MultipartParser
        {
            private readonly Stream _stream;
            private readonly byte[] _boundaryBytes;
            private readonly byte[] _boundaryEndBytes;
            private readonly MainForm _mainForm;
            private readonly Queue<byte> _pushback = new Queue<byte>();
            private bool _isFirstPart = true;

            public string Filename { get; private set; }
            public string ContentType { get; private set; }

            public MultipartParser(Stream stream, string boundary, MainForm mainForm)
            {
                _stream = stream;
                _boundaryBytes = Encoding.ASCII.GetBytes($"--{boundary}\r\n");
                _boundaryEndBytes = Encoding.ASCII.GetBytes($"--{boundary}--\r\n");
                _mainForm = mainForm;
            }

            public bool ReadNextPart()
            {
                _mainForm.UpdateLog($"[MultipartParser] Đang đọc phần tiếp theo...");
                if (_isFirstPart)
                {
                    if (!SkipBoundary())
                    {
                        _mainForm.UpdateLog($"[MultipartParser] Không tìm thấy boundary ban đầu.", true);
                        return false;
                    }
                    _isFirstPart = false;
                }
                else
                {
                    if (!SkipBoundary())
                    {
                        _mainForm.UpdateLog($"[MultipartParser] Không tìm thấy boundary tiếp theo.", true);
                        return false;
                    }
                }

                Filename = null;
                ContentType = null;

                string headerLine;
                while (!string.IsNullOrEmpty(headerLine = ReadLine()))
                {
                    _mainForm.UpdateLog($"[MultipartParser] Header: {headerLine}");
                    if (headerLine.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase))
                    {
                        Match encodedMatch = Regex.Match(headerLine, "filename\\*=UTF-8''([^\"]*)");
                        if (encodedMatch.Success)
                        {
                            Filename = HttpUtility.UrlDecode(encodedMatch.Groups[1].Value);
                            _mainForm.UpdateLog($"[MultipartParser] Tên file (UTF-8 encoded): {Filename}");
                        }
                        else
                        {
                            Match standardMatch = Regex.Match(headerLine, "filename=\"([^\"]*)\"");
                            if (standardMatch.Success)
                            {
                                string rawFilename = standardMatch.Groups[1].Value;
                                string fixedFilename = _mainForm.FixVietnameseEncoding(rawFilename);
                                Filename = fixedFilename.Normalize(NormalizationForm.FormC);
                                _mainForm.UpdateLog($"[MultipartParser] Tên file (fixed): {Filename}");
                            }
                        }
                    }
                    else if (headerLine.StartsWith("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        ContentType = headerLine.Substring(headerLine.IndexOf(':') + 1).Trim();
                        _mainForm.UpdateLog($"[MultipartParser] Content-Type: {ContentType}");
                    }
                }

                if (!string.IsNullOrEmpty(Filename))
                {
                    _mainForm.UpdateLog($"[MultipartParser] Đã tìm thấy file: {Filename}");
                    return true;
                }
                else
                {
                    _mainForm.UpdateLog($"[MultipartParser] Không tìm thấy tên file trong phần này.", true);
                    return false;
                }
            }

            public void WritePartDataTo(Stream outputStream)
            {
                byte[] buffer = new byte[8192];
                int bytesInBuffer = 0;
                int keepTail = Math.Max(_boundaryBytes.Length, _boundaryEndBytes.Length) + 2;
                byte[] tail = new byte[keepTail];

                while ((bytesInBuffer += ReadBytesInternal(buffer, bytesInBuffer, buffer.Length - bytesInBuffer)) > 0)
                {
                    int boundaryIndex = FindBoundary(buffer, bytesInBuffer);

                    if (boundaryIndex >= 0)
                    {
                        int toWrite = Math.Max(0, boundaryIndex - 2);
                        if (toWrite > 0)
                        {
                            outputStream.Write(buffer, 0, toWrite);
                            _mainForm.UpdateLog($"[MultipartParser] Ghi {toWrite} bytes cho file: {Filename}");
                        }

                        for (int i = boundaryIndex; i < bytesInBuffer; i++)
                            _pushback.Enqueue(buffer[i]);

                        return;
                    }
                    else
                    {
                        int toWrite = Math.Max(0, bytesInBuffer - keepTail);
                        if (toWrite > 0)
                        {
                            outputStream.Write(buffer, 0, toWrite);
                            _mainForm.UpdateLog($"[MultipartParser] Ghi {toWrite} bytes cho file: {Filename}");
                        }

                        int remain = bytesInBuffer - toWrite;
                        if (remain > 0)
                        {
                            Array.Copy(buffer, toWrite, buffer, 0, remain);
                        }
                        bytesInBuffer = remain;
                    }
                }

                if (bytesInBuffer > 0)
                {
                    outputStream.Write(buffer, 0, bytesInBuffer);
                    _mainForm.UpdateLog($"[MultipartParser] Ghi {bytesInBuffer} bytes cuối cho file: {Filename}");
                }
            }

            public string ReadLine()
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
                return sb.Length > 0 ? sb.ToString() : null;
            }

            private bool SkipBoundary()
            {
                byte[] buffer = new byte[_boundaryBytes.Length];
                int got = ReadBytesInternal(buffer, 0, buffer.Length);
                if (got != buffer.Length)
                {
                    _mainForm.UpdateLog($"[MultipartParser] Không đủ dữ liệu để đọc boundary.", true);
                    return false;
                }
                bool isBoundary = ByteArrayEquals(buffer, _boundaryBytes);
                bool isEndBoundary = ByteArrayEquals(buffer, _boundaryEndBytes);
                if (!isBoundary && !isEndBoundary)
                {
                    _mainForm.UpdateLog($"[MultipartParser] Không khớp với boundary hoặc end boundary.", true);
                    return false;
                }
                return true;
            }

            private int ReadBytesInternal(byte[] buffer, int offset, int count)
            {
                int written = 0;
                while (written < count && _pushback.Count > 0)
                {
                    buffer[offset + written] = _pushback.Dequeue();
                    written++;
                }
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

            private int FindBoundary(byte[] buffer, int length)
            {
                for (int i = 0; i <= length - Math.Min(_boundaryBytes.Length, _boundaryEndBytes.Length); i++)
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
        private string FixVietnameseEncoding(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            try
            {
                byte[] bytes = Encoding.GetEncoding("Windows-1252").GetBytes(input);
                string result = Encoding.UTF8.GetString(bytes);
                if (result.Contains("�"))
                {
                    return FixVietnameseCharacters(input);
                }
                return result.Normalize(NormalizationForm.FormC);
            }
            catch
            {
                return FixVietnameseCharacters(input).Normalize(NormalizationForm.FormC);
            }
        }

        private string FixVietnameseCharacters(string input)
        {
            var replacements = new Dictionary<string, string>
            {
                { "á", "a" }, { "à", "a" }, { "ả", "a" }, { "ã", "a" }, { "ạ", "a" },
                { "ă", "a" }, { "ắ", "a" }, { "ằ", "a" }, { "ẳ", "a" }, { "ẵ", "a" }, { "ặ", "a" },
                { "đ", "d" }, { "í", "i" }, { "ì", "i" }, { "ỉ", "i" }, { "ĩ", "i" }, { "ị", "i" },
                { "ó", "o" }, { "ò", "o" }, { "ỏ", "o" }, { "õ", "o" }, { "ọ", "o" },
                { "ô", "o" }, { "ố", "o" }, { "ồ", "o" }, { "ổ", "o" }, { "ỗ", "o" }, { "ộ", "o" },
                { "ơ", "o" }, { "ớ", "o" }, { "ờ", "o" }, { "ở", "o" }, { "ỡ", "o" }, { "ợ", "o" },
                { "ú", "u" }, { "ù", "u" }, { "ủ", "u" }, { "ũ", "u" }, { "ụ", "u" },
                { "ư", "u" }, { "ứ", "u" }, { "ừ", "u" }, { "ử", "u" }, { "ữ", "u" }, { "ự", "u" },
                { "ý", "y" }, { "ỳ", "y" }, { "ỷ", "y" }, { "ỹ", "y" }, { "ỵ", "y" }
            };
            return replacements.Aggregate(input, (current, pair) => current.Replace(pair.Key, pair.Value));
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
                { ".ini", "text/plain; charset=utf-8" },
                { ".md", "text/html; charset=utf-8" },
        
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
            string prefix = isError ? "❖ [!] " : "• "; // Giữ nguyên phần prefix nếu cần highlight lỗi
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
        private string GenerateUploadPageHtml()
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>");
            sb.Append("<html lang='vi'>");
            sb.Append("<head>");
            sb.Append("<meta charset='UTF-8'>");
            sb.Append("<meta http-equiv='X-UA-Compatible' content='IE=edge'>");
            sb.Append("<meta name='viewport' content='width=device-width, initial-scale=1.0'>");
            sb.Append("<title>Tải lên tập tin - ShareFile</title>");
            sb.Append("<link rel='icon' type='image/x-icon' href='/favicon.ico'>");
            sb.Append("<style>");
            sb.Append("* { box-sizing: border-box; margin: 0; padding: 0; }");
            sb.Append("body { font-family: 'Segoe UI', Arial, sans-serif; background: #f0f2f5; min-height: 100vh; padding: 20px; }");
            sb.Append(".upload-container { background: #FFFFFF; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); max-width: 600px; margin: 20px auto; padding: 20px; }");
            sb.Append(".header { text-align: center; margin-bottom: 20px; }");
            sb.Append(".header h1 { font-size: 24px; color: #333; }");
            sb.Append(".upload-zone { border: 2px dashed #ccc; border-radius: 8px; padding: 20px; text-align: center; cursor: pointer; background: #fafafa; }");
            sb.Append(".upload-zone:hover { border-color: #007bff; background: #e9f4ff; }");
            sb.Append(".upload-zone.dragover { border-color: #007bff; background: #d0e6ff; }");
            sb.Append(".upload-icon { font-size: 36px; color: #007bff; margin-bottom: 10px; }");
            sb.Append(".upload-text { font-size: 16px; color: #333; }");
            sb.Append(".upload-subtext { font-size: 12px; color: #666; margin-top: 5px; }");
            sb.Append(".file-list { margin: 15px 0; max-height: 200px; overflow-y: auto; }");
            sb.Append(".file-item { display: flex; align-items: center; padding: 8px; margin-bottom: 5px; background: #f5f5f5; border-radius: 4px; }");
            sb.Append(".file-name { flex: 1; font-size: 14px; color: #333; }");
            sb.Append(".file-size { font-size: 12px; color: #666; margin-right: 10px; }");
            sb.Append(".file-remove { background: #dc3545; color: white; border: none; border-radius: 4px; padding: 5px 10px; cursor: pointer; }");
            sb.Append(".button-group { display: flex; gap: 10px; justify-content: center; margin-top: 15px; }");
            sb.Append(".btn { background: #007bff; color: white; border: none; padding: 10px 20px; border-radius: 4px; cursor: pointer; }");
            sb.Append(".btn:disabled { background: #ccc; cursor: not-allowed; }");
            sb.Append(".btn-secondary { background: #6c757d; }");
            sb.Append(".progress-container { margin-top: 15px; display: none; }");
            sb.Append(".progress-label { font-size: 12px; color: #333; margin-bottom: 5px; text-align: center; }");
            sb.Append(".progress { height: 6px; background: #e0e0e0; border-radius: 3px; overflow: hidden; }");
            sb.Append(".progress-bar { height: 100%; background: #007bff; width: 0; }");
            sb.Append(".status-message { padding: 10px; border-radius: 4px; margin-bottom: 15px; font-size: 14px; text-align: center; display: none; }");
            sb.Append(".status-success { background: #d4edda; color: #155724; border: 1px solid #c3e6cb; }");
            sb.Append(".status-error { background: #f8d7da; color: #721c24; border: 1px solid #f5c6cb; }");
            sb.Append("@media (max-width: 600px) { .upload-container { margin: 10px; padding: 15px; } .header h1 { font-size: 20px; } }");
            sb.Append("</style>");
            sb.Append("</head>");

            sb.Append("<body>");
            sb.Append("<div class='upload-container'>");
            sb.Append("<div class='header'><h1>Tải lên tập tin</h1></div>");
            sb.Append("<div id='statusMessage' class='status-message'></div>");
            sb.Append("<div class='upload-zone' id='uploadZone'>");
            sb.Append("<div class='upload-icon'>⇪ ⇪ ⇪</div>");
            sb.Append("<div class='upload-text'>Kéo thả tập tin vào đây</div>");
            sb.Append("<div class='upload-subtext'>hoặc nhấn để chọn</div>");
            sb.Append("<input type='file' id='fileInput' multiple style='display: none;' accept='*/*'>");
            sb.Append("</div>");
            sb.Append("<div id='fileList' class='file-list'></div>");
            sb.Append("<div class='button-group'>");
            sb.Append("<button id='uploadBtn' class='btn' disabled>Tải lên</button>");
            sb.Append("<button id='clearBtn' class='btn btn-secondary' disabled>Xóa danh sách</button>");
            sb.Append("</div>");
            sb.Append("<div id='progressContainer' class='progress-container'>");
            sb.Append("<div class='progress-label'>Đang tải...</div>");
            sb.Append("<div class='progress'><div id='progressBar' class='progress-bar'></div></div>");
            sb.Append("</div>");
            sb.Append("</div>");

            sb.Append("<script>");
            sb.Append("(function() {");
            sb.Append("  var fileInput = document.getElementById('fileInput');");
            sb.Append("  var fileList = document.getElementById('fileList');");
            sb.Append("  var uploadBtn = document.getElementById('uploadBtn');");
            sb.Append("  var clearBtn = document.getElementById('clearBtn');");
            sb.Append("  var uploadZone = document.getElementById('uploadZone');");
            sb.Append("  var progressContainer = document.getElementById('progressContainer');");
            sb.Append("  var progressBar = document.getElementById('progressBar');");
            sb.Append("  var statusMessage = document.getElementById('statusMessage');");
            sb.Append("  var selectedFiles = [];");

            sb.Append("  function showStatus(message, type) {");
            sb.Append("    statusMessage.innerHTML = message;");
            sb.Append("    statusMessage.className = 'status-message status-' + type;");
            sb.Append("    statusMessage.style.display = 'block';");
            sb.Append("    setTimeout(function() { statusMessage.style.display = 'none'; }, 5000);");
            sb.Append("  }");

            sb.Append("  function formatFileSize(bytes) {");
            sb.Append("    if (bytes === 0) return '0 B';");
            sb.Append("    var k = 1024, sizes = ['B', 'KB', 'MB', 'GB'], i = Math.floor(Math.log(bytes) / Math.log(k));");
            sb.Append("    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];");
            sb.Append("  }");

            sb.Append("  function updateFileList() {");
            sb.Append("    fileList.innerHTML = '';");
            sb.Append("    if (selectedFiles.length === 0) {");
            sb.Append("      fileList.style.display = 'none';");
            sb.Append("      uploadBtn.disabled = true;");
            sb.Append("      clearBtn.disabled = true;");
            sb.Append("      return;");
            sb.Append("    }");
            sb.Append("    fileList.style.display = 'block';");
            sb.Append("    uploadBtn.disabled = false;");
            sb.Append("    clearBtn.disabled = false;");
            sb.Append("    for (var i = 0; i < selectedFiles.length; i++) {");
            sb.Append("      var file = selectedFiles[i];");
            sb.Append("      console.log('Tập tin đã được thêm vào danh sách: ' + file.name + ', size: ' + file.size);");
            sb.Append("      var div = document.createElement('div');");
            sb.Append("      div.className = 'file-item';");
            sb.Append("      div.innerHTML = '<span class=\"file-name\">' + file.name + '</span>' +");
            sb.Append("                     '<span class=\"file-size\">' + formatFileSize(file.size) + '</span>' +");
            sb.Append("                     '<button class=\"file-remove\" onclick=\"removeFile(' + i + ')\">Xóa</button>';");
            sb.Append("      fileList.appendChild(div);");
            sb.Append("    }");
            sb.Append("  }");

            sb.Append("  window.removeFile = function(index) {");
            sb.Append("    console.log('Đang xóa file tại chỉ mục: ' + index);");
            sb.Append("    selectedFiles.splice(index, 1);");
            sb.Append("    updateFileList();");
            sb.Append("    showStatus('Đã xóa tập tin', 'success');");
            sb.Append("  };");

            sb.Append("  function handleFiles(files) {");
            sb.Append("    console.log('Handling ' + files.length + ' files');");
            sb.Append("    var newFiles = Array.prototype.slice.call(files);");
            sb.Append("    var duplicates = 0;");
            sb.Append("    for (var i = 0; i < newFiles.length; i++) {");
            sb.Append("      var file = newFiles[i];");
            sb.Append("      var exists = false;");
            sb.Append("      for (var j = 0; j < selectedFiles.length; j++) {");
            sb.Append("        if (selectedFiles[j].name === file.name && selectedFiles[j].size === file.size) {");
            sb.Append("          exists = true; duplicates++; break;");
            sb.Append("        }");
            sb.Append("      }");
            sb.Append("      if (!exists) selectedFiles.push(file);");
            sb.Append("    }");
            sb.Append("    updateFileList();");
            sb.Append("    var message = 'Đã thêm ' + (newFiles.length - duplicates) + ' tập tin';");
            sb.Append("    if (duplicates > 0) message += ' (' + duplicates + ' trùng lặp bị bỏ qua)';");
            sb.Append("    showStatus(message, 'success');");
            sb.Append("  }");

            sb.Append("  uploadZone.onclick = function() {");
            sb.Append("    console.log('Đã bấm nút Tải lên');");
            sb.Append("    fileInput.click();");
            sb.Append("  };");
            sb.Append("  fileInput.onchange = function() {");
            sb.Append("    console.log('Tập tin đã được chọn/cập nhật: ' + this.files.length);");
            sb.Append("    if (this.files.length > 0) { handleFiles(this.files); }");
            sb.Append("    else { showStatus('Không có file nào được chọn', 'error'); }");
            sb.Append("  };");

            sb.Append("  uploadZone.ondragover = function(e) {");
            sb.Append("    e.preventDefault();");
            sb.Append("    uploadZone.className = 'upload-zone dragover';");
            sb.Append("  };");
            sb.Append("  uploadZone.ondragleave = function(e) {");
            sb.Append("    e.preventDefault();");
            sb.Append("    uploadZone.className = 'upload-zone';");
            sb.Append("  };");
            sb.Append("  uploadZone.ondrop = function(e) {");
            sb.Append("    e.preventDefault();");
            sb.Append("    uploadZone.className = 'upload-zone';");
            sb.Append("    console.log('Tập tin đã được thêm vào: ' + e.dataTransfer.files.length);");
            sb.Append("    if (e.dataTransfer.files.length > 0) { handleFiles(e.dataTransfer.files); }");
            sb.Append("    else { showStatus('Không có file nào được kéo thả', 'error'); }");
            sb.Append("  };");

            sb.Append("  clearBtn.onclick = function() {");
            sb.Append("    console.log('Nút xóa đã được nhấp');");
            sb.Append("    selectedFiles = []; fileInput.value = ''; updateFileList();");
            sb.Append("    showStatus('Đã xóa danh sách tập tin', 'success');");
            sb.Append("  };");

            sb.Append("  uploadBtn.onclick = function() {");
            sb.Append("    if (selectedFiles.length === 0) {");
            sb.Append("      showStatus('Vui lòng chọn ít nhất một file', 'error');");
            sb.Append("      console.log('Không có file nào được chọn để tải lên');");
            sb.Append("      return;");
            sb.Append("    }");
            sb.Append("    var formData = new FormData();");
            sb.Append("    for (var i = 0; i < selectedFiles.length; i++) {");
            sb.Append("      console.log('Thêm dữ liệu vào FormData: ' + selectedFiles[i].name + ', size: ' + selectedFiles[i].size);");
            sb.Append("      formData.append('files[]', selectedFiles[i], selectedFiles[i].name);");
            sb.Append("    }");
            sb.Append("    var xhr = new XMLHttpRequest();");
            sb.Append("    xhr.open('POST', '/upload', true);");
            sb.Append("    xhr.upload.onprogress = function(e) {");
            sb.Append("      if (e.lengthComputable) {");
            sb.Append("        progressContainer.style.display = 'block';");
            sb.Append("        var percent = Math.round((e.loaded / e.total) * 100);");
            sb.Append("        progressBar.style.width = percent + '%';");
            sb.Append("        document.querySelector('.progress-label').innerHTML = 'Đang tải... ' + percent + '%';");
            sb.Append("        console.log('Tiến độ tải lên: ' + percent + '%');");
            sb.Append("      }");
            sb.Append("    };");
            sb.Append("    xhr.onload = function() {");
            sb.Append("      progressContainer.style.display = 'none';");
            sb.Append("      console.log('Đã nhận được phản hồi: ' + xhr.status + ' ' + xhr.statusText);");
            sb.Append("      console.log('Nội dung phản hồi: ' + xhr.responseText);");
            sb.Append("      if (xhr.status === 200) {");
            sb.Append("        document.open(); document.write(xhr.responseText); document.close();");
            sb.Append("      } else {");
            sb.Append("        showStatus('Lỗi tải lên: ' + xhr.statusText + ' (Status: ' + xhr.status + ')', 'error');");
            sb.Append("      }");
            sb.Append("    };");
            sb.Append("    xhr.onerror = function() {");
            sb.Append("      progressContainer.style.display = 'none';");
            sb.Append("      showStatus('Lỗi kết nối mạng', 'error');");
            sb.Append("      console.log('Lỗi mạng trong quá trình tải lên');");
            sb.Append("    };");
            sb.Append("    xhr.onreadystatechange = function() {");
            sb.Append("      if (xhr.readyState === 4) {");
            sb.Append("        console.log('Phản hồi cuối cùng: ' + xhr.status + ' ' + xhr.statusText);");
            sb.Append("      }");
            sb.Append("    };");
            sb.Append("    uploadBtn.disabled = true; clearBtn.disabled = true;");
            sb.Append("    showStatus('Đang tải lên ' + selectedFiles.length + ' tập tin...', 'success');");
            sb.Append("    console.log('Đang bắt đầu tải lên với' + selectedFiles.length + ' files');");
            sb.Append("    xhr.send(formData);");
            sb.Append("  };");
            sb.Append("})();");
            sb.Append("</script>");
            sb.Append("</body></html>");

            return sb.ToString();
        }

        private string GenerateSuccessPageHtml(List<string> uploadedFiles, List<string> failedFiles)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>");
            sb.Append("<html lang='vi'>");
            sb.Append("<head>");
            sb.Append("<meta charset='UTF-8'>");
            sb.Append("<meta http-equiv='X-UA-Compatible' content='IE=edge'>");
            sb.Append("<meta name='viewport' content='width=device-width, initial-scale=1.0'>");
            sb.Append("<title>Tải lên thành công! - ShareFile</title>");
            sb.Append("<link rel='icon' type='image/x-icon' href='/favicon.ico'>");
            sb.Append("<style>");
            sb.Append("* { box-sizing: border-box; margin: 0; padding: 0; }");
            sb.Append("body { font-family: 'Segoe UI', Arial, sans-serif; background: #f0f2f5; min-height: 100vh; padding: 20px; }");
            sb.Append(".container { background: #fcfcfc; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); max-width: 600px; margin: 20px auto; padding: 20px; }");
            sb.Append(".header { text-align: center; margin-bottom: 20px; }");
            sb.Append(".header h1 { font-size: 24px; color: #228B22; }");
            sb.Append("ul { list-style-type: none; padding: 0; }");
            sb.Append("li { padding: 10px; margin-bottom: 5px; border-radius: 4px; }");
            sb.Append(".success { background: #d4edda; color: #155724; }");
            sb.Append(".error { background: #f8d7da; color: #721c24; }");
            sb.Append(".button-group { text-align: center; margin-top: 20px; }");
            sb.Append(".button { display: inline-block; padding: 8px 8px; margin: 0 20px; border-radius: 4px; text-decoration: none; font-size: 16px; font-weight: Reguler; cursor: pointer; transition: background-color 0.3s; }");
            sb.Append(".button-upload { background: #28a745; color: #fff; }");
            sb.Append(".button-upload:hover { background: #218838; }");
            sb.Append(".button-back { background: #007bff; color: #fff; }");
            sb.Append(".button-back:hover { background: #0056b3; }");
            sb.Append("@media (max-width: 600px) { .container { margin: 10px; padding: 15px; } .header h1 { font-size: 20px; } .button { display: block; margin: 10px auto; width: 80%; } }");
            sb.Append("</style>");
            sb.Append("</head>");

            sb.Append("<body>");
            sb.Append("<div class='container'>");
            sb.Append("<div class='header'><h1>✔ Tải lên thành công!</h1></div>");

            if (uploadedFiles.Any())
            {
                sb.Append("<h3>𓆰 Đã tải lên thành công file:</h3>");
                sb.Append("<ul>");
                foreach (var file in uploadedFiles)
                {
                    sb.Append($"<li class='success'>{WebUtility.HtmlEncode(file)}</li>");
                }
                sb.Append("</ul>");
            }

            if (failedFiles.Any())
            {
                sb.Append("<h3>✖ Các file tải lên thất bại:</h3>");
                sb.Append("<ul>");
                foreach (var file in failedFiles)
                {
                    sb.Append($"<li class='error'>{WebUtility.HtmlEncode(file)}</li>");
                }
                sb.Append("</ul>");
            }

            if (!uploadedFiles.Any() && !failedFiles.Any())
            {
                sb.Append("<p>Không có file nào được upload.</p>");
            }

            sb.Append("<div class='button-group'>");
            sb.Append("<a href='/upload' class='button button-upload'>Tải lên file khác</a>");
            sb.Append("<a href='/' class='button button-back'>Quay lại thư mục chính</a>");
            sb.Append("</div>");
            sb.Append("</div>");
            sb.Append("</body></html>");

            return sb.ToString();
        }


        private async Task HandleFileUpload(HttpListenerContext context)
        {
            string clientIp = context.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
            var uploadedFiles = new List<string>();
            var failedFiles = new List<string>();

            try
            {
                var request = context.Request;
                UpdateLog($"[{clientIp}] Nhận yêu cầu POST /upload, Content-Type: {request.ContentType}, Content-Length: {request.ContentLength64}");

                if (!request.HasEntityBody || request.ContentLength64 == 0)
                {
                    await SendErrorResponse(context, 400, "Không có dữ liệu tải lên.");
                    UpdateLog($"[{clientIp}] Không có dữ liệu tải lên.", true);
                    return;
                }

                if (!request.ContentType.Contains("multipart/form-data"))
                {
                    await SendErrorResponse(context, 400, "Content-Type phải là multipart/form-data.");
                    UpdateLog($"[{clientIp}] Content-Type không hợp lệ: {request.ContentType}", true);
                    return;
                }

                string boundary = GetBoundary(request.ContentType);
                if (string.IsNullOrEmpty(boundary))
                {
                    await SendErrorResponse(context, 400, "Thiếu boundary trong Content-Type.");
                    UpdateLog($"[{clientIp}] Thiếu boundary trong Content-Type.", true);
                    return;
                }
                UpdateLog($"[{clientIp}] Boundary: {boundary}");

                string uploadDir = Path.Combine(_sharedFolderPath, "Uploads");
                try
                {
                    if (!Directory.Exists(uploadDir))
                    {
                        Directory.CreateDirectory(uploadDir);
                        UpdateLog($"[{clientIp}] Đã tạo thư mục Uploads: {uploadDir}");
                    }
                    string testFile = Path.Combine(uploadDir, "test_" + Guid.NewGuid().ToString() + ".txt");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                    UpdateLog($"[{clientIp}] Kiểm tra quyền ghi vào Uploads: Thành công");
                }
                catch (Exception ex)
                {
                    await SendErrorResponse(context, 500, $"Không thể tạo hoặc ghi vào thư mục Uploads: {ex.Message}");
                    UpdateLog($"[{clientIp}] Lỗi tạo hoặc ghi thư mục Uploads: {ex.Message}", true);
                    return;
                }

                var parser = new MultipartParser(request.InputStream, boundary, this);
                while (parser.ReadNextPart())
                {
                    string fileName = parser.Filename;
                    if (string.IsNullOrEmpty(fileName))
                    {
                        UpdateLog($"[{clientIp}] Không tìm thấy tên file trong phần multipart.", true);
                        continue;
                    }

                    string safeFileName = GetUniqueFilename(uploadDir, fileName);
                    string savePath = Path.Combine(uploadDir, safeFileName);

                    try
                    {
                        using (var fileStream = new FileStream(
                            savePath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            8192,
                            FileOptions.Asynchronous))
                        {
                            parser.WritePartDataTo(fileStream);
                            await fileStream.FlushAsync();
                            fileStream.Close();
                        }

                        if (File.Exists(savePath))
                        {
                            FileInfo fi = new FileInfo(savePath);
                            if (fi.Length > 0)
                            {
                                UpdateLog($"[{clientIp}] Đã upload thành công: {safeFileName} ({FormatFileSize(fi.Length)})");
                                uploadedFiles.Add(safeFileName);
                            }
                            else
                            {
                                UpdateLog($"[{clientIp}] File hỏng (kích thước 0): {safeFileName}", true);
                                failedFiles.Add(safeFileName);
                            }
                        }
                        else
                        {
                            UpdateLog($"[{clientIp}] File không tồn tại sau khi lưu: {safeFileName}", true);
                            failedFiles.Add(safeFileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        UpdateLog($"[{clientIp}] Lỗi khi lưu file {safeFileName}: {ex.Message}", true);
                        failedFiles.Add(safeFileName);
                    }
                }

                if (parser.ReadLine() != null)
                {
                    UpdateLog($"[{clientIp}] Dữ liệu dư thừa sau khi xử lý multipart.", true);
                }

                if (uploadedFiles.Any() || failedFiles.Any())
                {
                    UpdateLog($"[{clientIp}] Kết quả: {uploadedFiles.Count} file thành công, {failedFiles.Count} file thất bại.");
                    await SendSuccessResponse(context, uploadedFiles, failedFiles);
                }
                else
                {
                    await SendErrorResponse(context, 400, "Không có file nào được upload.");
                    UpdateLog($"[{clientIp}] Không có file nào được upload.", true);
                }
            }
            catch (Exception ex)
            {
                await SendErrorResponse(context, 500, "Lỗi khi upload: " + ex.Message);
                UpdateLog($"[{clientIp}] Lỗi khi upload: {ex.Message}", true);
            }
        }


        private async Task SendSuccessResponse(HttpListenerContext context, List<string> uploadedFiles, List<string> failedFiles)
        {
            // Kiểm tra lại file để đảm bảo không bị hỏng
            string uploadDir = Path.Combine(_sharedFolderPath, "Uploads");
            var verifiedFiles = new List<string>();
            var corruptedFiles = new List<string>(failedFiles);

            foreach (var file in uploadedFiles)
            {
                string filePath = Path.Combine(uploadDir, file);
                try
                {
                    FileInfo fi = new FileInfo(filePath);
                    if (fi.Length > 0)
                    {
                        verifiedFiles.Add(file);
                    }
                    else
                    {
                        corruptedFiles.Add(file);
                        UpdateLog($"[{context.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown"}] File hỏng (kích thước 0): {file}", true);
                    }
                }
                catch (Exception ex)
                {
                    corruptedFiles.Add(file);
                    UpdateLog($"[{context.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown"}] Lỗi kiểm tra file {file}: {ex.Message}", true);
                }
            }

            string htmlContent = GenerateSuccessPageHtml(verifiedFiles, corruptedFiles);
            byte[] buffer = Encoding.UTF8.GetBytes(htmlContent);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=UTF-8";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            context.Response.Close();
        }

        private async Task SendErrorResponse(HttpListenerContext context, int statusCode, string message)
        {
            string htmlContent = $"<html><body><h1>{statusCode} Error</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
            byte[] buffer = Encoding.UTF8.GetBytes(htmlContent);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html; charset=UTF-8";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            context.Response.Close();
        }

        private string GetBoundary(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return null;
            var match = Regex.Match(contentType, @"boundary=(?:(?:""([^""]*)"")|([^\s;]+))");
            return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
        }

        private string GetUniqueFilename(string directory, string fileName)
        {
            string safeFileName = GetSafeFilename(fileName);
            string baseName = Path.GetFileNameWithoutExtension(safeFileName);
            string extension = Path.GetExtension(safeFileName);
            string path = Path.Combine(directory, safeFileName);
            int counter = 1;

            while (File.Exists(path))
            {
                string newFileName = $"{baseName}_{counter}{extension}";
                path = Path.Combine(directory, newFileName);
                counter++;
            }

            return Path.GetFileName(path);
        }

        private string GetSafeFilename(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "unnamed_file";
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string safe = Regex.Replace(fileName, $"[{invalidChars}]", "_");
            return FixVietnameseCharacters(safe);
        }
    }
}