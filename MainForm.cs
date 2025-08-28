using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using System.Xml.Linq;
using ZXing;
using ZXing.Common;
using ZXing.Datamatrix;
using ZXing.QrCode;



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

        //Tạo mã QR code và Data Matrix
        private const string QRCODE_ENDPOINT = "/code";
        private const string GENERATE_IMAGE_ENDPOINT = "/generate-image";

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

        // Thêm đoạn code này vào trong class MainForm của bạn
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;


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
            this.notifyIcon.DoubleClick += notifyIcon_DoubleClick;   // Nhấp đúp chuột vào biểu tượng thông báo để mở lại ứng dụng
            this.txtLog.MouseDown += new System.Windows.Forms.MouseEventHandler(this.txtLog_MouseDown);
            

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

        private string GetIconBase64(string path, bool isDirectory)
        {
            uint flags = SHGFI_ICON;
            if (isDirectory)
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
            }
            else
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
            }

            var shinfo = new SHFILEINFO();
            IntPtr hIcon = SHGetFileInfo(
                isDirectory ? "directory" : Path.GetFileName(path), // Dùng chuỗi "directory" để lấy icon mặc định cho thư mục
                isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                ref shinfo,
                (uint)Marshal.SizeOf(shinfo),
                flags);

            if (hIcon == IntPtr.Zero)
            {
                return null;
            }

            using (Icon icon = Icon.FromHandle(shinfo.hIcon))
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    icon.ToBitmap().Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    byte[] imageBytes = ms.ToArray();
                    return "data:image/png;base64," + Convert.ToBase64String(imageBytes);
                }
            }
        }

        private void txtLog_MouseDown(object sender, MouseEventArgs e)
        {
            // Đặt đường dẫn file log tương ứng với phương thức UpdateLog
            string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Log");
            string logFilePath = Path.Combine(logDirectory, "log.txt");

            // Mở file log nếu nó tồn tại
            if (File.Exists(logFilePath))
            {
                System.Diagnostics.Process.Start(logFilePath);
            }
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
            System.Windows.Forms.Application.Exit();
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

                UpdateLog("╔════════════════════════════════╗");
                UpdateLog("║                 ỨNG DỤNG ĐÃ KHỞI ĐỘNG                        ║");
                UpdateLog("╚════════════════════════════════╝");
                UpdateLog($"📍 Địa chỉ IP:          {localIP}");
                UpdateLog($"📍 Port:                   {port}");
                UpdateLog($"📍 Tên máy tính:     {computerName}");
                UpdateLog("══════════════════════════════════");
                UpdateLog("🌐 Truy cập từ trình duyệt:");
                UpdateLog($"   ➜ http://{localIP}:{port}");
                UpdateLog($"   ➜ http://{computerName}:{port}");
                UpdateLog("══════════════════════════════════");
                UpdateLog("🌐 Truy cập chức năng khác:");
                UpdateLog($"   ➜ http://{localIP}:{port}/upload");
                UpdateLog($"       (Upload & chia sẻ file)");
                UpdateLog($"   ➜ http://{localIP}:{port}/time");
                UpdateLog("        (Xem đồng hồ & lịch)");
                UpdateLog($"   ➜ http://{localIP}:{port}/qrcode");
                UpdateLog("        (Tạo mã QR & Data Matrix)"); // Dòng mới
                UpdateLog($"   ➜ http://{localIP}:{port}/kit");
                UpdateLog("        (Tạo mã QR Code kitting)"); // Dòng mới
                UpdateLog("✅ Có thể truy cập từ các thiết bị khác trong mạng LAN");
                UpdateLog("══════════════════════════════════");

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

        #region PROCESS_REQUEST
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
            // Trang đồng hồ & lịch
            if (context.Request.HttpMethod == "GET" &&
                string.Equals(relativePath, "/time", StringComparison.OrdinalIgnoreCase)||
                string.Equals(relativePath, "/clock", StringComparison.OrdinalIgnoreCase)||
                string.Equals(relativePath, "/t", StringComparison.OrdinalIgnoreCase)
                )
            {
                string htmlContent = GetTimePageHtml();
                byte[] buffer = Encoding.UTF8.GetBytes(htmlContent);
                context.Response.ContentType = "text/html; charset=UTF-8";
                context.Response.ContentLength64 = buffer.LongLength;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
                UpdateLog($"[{clientIp}] Đã truy cập trang time.");
                return;
            }

            // Trang tạo mã QR & Data Matrix
            if (context.Request.HttpMethod == "GET" &&
                (string.Equals(relativePath, QRCODE_ENDPOINT, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(relativePath, "/qr", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(relativePath, "/code", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(relativePath, "/qrcode", StringComparison.OrdinalIgnoreCase))
                 )
            {
                string htmlContent = GetQrCodePageHtml();
                byte[] buffer = Encoding.UTF8.GetBytes(htmlContent);
                context.Response.ContentType = "text/html; charset=UTF-8";
                context.Response.ContentLength64 = buffer.LongLength;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
                UpdateLog($"[{clientIp}] Đã truy cập trang tạo mã QR/Data Matrix.");
                return;
            }
            // Endpoint để tạo và trả về hình ảnh mã
            if (context.Request.HttpMethod == "GET" &&
                string.Equals(relativePath, GENERATE_IMAGE_ENDPOINT, StringComparison.OrdinalIgnoreCase))
            {
                await HandleGenerateImageRequest(context);
                return;
            }

            // Giao diện tạo 2 mã QR một lúc
            if (context.Request.HttpMethod == "GET" && 
                string.Equals(relativePath, "/kit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relativePath, "/kitting", StringComparison.OrdinalIgnoreCase)
                )
            {
                string htmlContent = GetKitPageHtml();
                byte[] buffer = Encoding.UTF8.GetBytes(htmlContent);
                context.Response.ContentType = "text/html; charset=UTF-8";
                context.Response.ContentLength64 = buffer.LongLength;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
                UpdateLog($"[{clientIp}] Đã truy cập trang tạo mã QR kitting.");
                return;
            }

            // Endpoint để tạo và trả về hình ảnh mã
            if (context.Request.HttpMethod == "GET" &&
                string.Equals(relativePath, GENERATE_IMAGE_ENDPOINT, StringComparison.OrdinalIgnoreCase))
            {
                await HandleGenerateImageRequest(context);
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
        #endregion

        //Các định dạng file mở trực tiếp trên trình duyệt
        private bool ShouldDisplayInBrowser(string extension)
        {
            var browserDisplayableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // File văn bản
                ".txt", ".html", ".htm", ".css", ".js", ".json", ".xml", ".md", ".ini",
        
                // File ảnh
                ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".ico",
        
                // PDF
                ".pdf",
        
                // Audio/Video
                ".mp3", ".mp4", ".webm", ".ogg", ".wav"
            };

            return browserDisplayableExtensions.Contains(extension);
        }
        #region Endcode-Decode
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
                .Replace("%21", "!")
                .Replace("%40", "@")
                .Replace("%2C", ",")
                //.Replace("%20", "~")
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
                ;
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
                .Replace("@", "%40")
                .Replace(",", "%2C")
                //.Replace("~", "%20")
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
                ;
            // Decode URL
            return Uri.UnescapeDataString(decoded);
        }
        #endregion

        #region ĐỒNG HỒ VÀ LỊCH
        // Trang đồng hồ & lịch 
        private string GetTimePageHtml()
        {
            return @"<!DOCTYPE html>
            <html lang='vi'>
            <head>
                <meta charset='UTF-8'>
                <meta http-equiv='X-UA-Compatible' content='IE=edge'>
                <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                <title>Đồng hồ & Lịch</title>
                <style>
                    * {
                        margin: 0;
                        padding: 0;
                        box-sizing: border-box;
                    }
        
                    body {
                        font-family: 'Segoe UI', 'Roboto', Arial, sans-serif;
                        background: linear-gradient(135deg, #1f3c54, #173d5c);
                        color: #f5f5f5;
                        min-height: 100vh;
                        padding: 20px;
                        display: flex;
                        flex-direction: column;
                        align-items: center;
                    }
        
                    .container {
                        display: flex;
                        flex-wrap: wrap;
                        gap: 30px;
                        justify-content: center;
                        max-width: 1200px;
                        width: 100%;
                        margin-top: 50px;
                    }
        
                    .card {
                        background: rgba(255, 255, 255, 0.08);
                        backdrop-filter: blur(10px);
                        border-radius: 20px;
                        box-shadow: 0 10px 30px rgba(0, 0, 0, 0.3);
                        padding: 25px;
                        border: 1px solid rgba(255, 255, 255, 0.1);
                    }
        
                    .title {
                        font-weight: 600;
                        margin: 0 0 20px 0;
                        text-align: center;
                        font-size: 1.4rem;
                        color: #e0e0e0;
                        letter-spacing: 1px;
                    }
        
                    /* Thiết kế khối đồng hồ */
                    .clock-container {
                        display: flex;
                        flex-direction: column;
                        align-items: center;
                        justify-content: center;
                    }
        
                    .clock {
                        position: relative;
                        width: 280px;
                        height: 280px;
                        border-radius: 50%;
                        background: rgba(0, 0, 0, 0.3);
                        border: 8px solid rgba(255, 255, 255, 0.1);
                        box-shadow: 
                            inset 0 0 25px rgba(0, 0, 0, 0.5),
                            0 5px 15px rgba(0, 0, 0, 0.3);
                    }
        
                    /* Số của đồng hồ */
                    .number {
                        position: absolute;
                        width: 100%;
                        height: 100%;
                        text-align: center;
                        font-weight: 600;
                        font-size: 16px;
                        color: rgba(255, 255, 255, 0.9);
                        transform-origin: center;
                    }
        
                    /* Tích tắc của đồng hồ */
                    .tick {
                        position: absolute;
                        left: 50%;
                        top: 0;
                        transform-origin: 50% 132px;
                        background: rgba(255, 255, 255, 0.7);
                    }
        
                    .tick.major {
                        width: 3px;
                        height: 12px;
                        background: rgba(255, 255, 255, 0.9);
                    }
        
                    .tick.minor {
                        width: 1px;
                        height: 6px;
                        background: rgba(255, 255, 255, 0.5);
                    }
        
                    /* Kim đồng hồ */
                    .hand {
                        position: absolute;
                        left: 50%;
                        top: 50%;
                        border-radius: 5px;
                        box-shadow: 0 0 5px rgba(0, 0, 0, 0.3);
                        z-index: 5;
                    }
        
                    .hand.hour {
                        width: 70px;
                        height: 6px;
                        background: #ff9800;
                        transform-origin: 0 50%;
                        margin-top: -3px;
                    }
        
                    .hand.minute {
                        width: 95px;
                        height: 4px;
                        background: #2196f3;
                        transform-origin: 0 50%;
                        margin-top: -2px;
                    }
        
                    .hand.second {
                        width: 110px;
                        height: 2px;
                        background: #f44336;
                        transform-origin: 0 50%;
                        margin-top: -1px;
                    }
        
                    .center-dot {
                        position: absolute;
                        left: 50%;
                        top: 50%;
                        width: 12px;
                        height: 12px;
                        border-radius: 50%;
                        background: #f5f5f5;
                        transform: translate(-50%, -50%);
                        box-shadow: 0 0 8px rgba(0, 0, 0, 0.5);
                        z-index: 10;
                    }
        
                    .digital-time {
                        margin-top: 20px;
                        font-size: 1.5rem;
                        font-weight: 500;
                        letter-spacing: 2px;
                        color: rgba(255, 255, 255, 0.9);
                        background: rgba(0, 0, 0, 0.2);
                        padding: 8px 15px;
                        border-radius: 10px;
                    }
        
                    /* Kiểu dáng lịch */
                    .calendar {
                        width: 520px;
                        max-width: 100%;
                    }
        
                    .cal-head {
                        display: flex;
                        align-items: center;
                        justify-content: space-between;
                        margin-bottom: 15px;
                    }
        
                    .btn {
                        user-select: none;
                        cursor: pointer;
                        background: rgba(255, 255, 255, 0.1);
                        border: 1px solid rgba(255, 255, 255, 0.2);
                        border-radius: 10px;
                        padding: 8px 15px;
                        color: #e0e0e0;
                        transition: all 0.2s;
                    }
        
                    .btn:hover {
                        background: rgba(255, 255, 255, 0.2);
                    }
        
                    .month-label {
                        font-size: 1.3rem;
                        font-weight: 600;
                        letter-spacing: 0.5px;
                        color: rgba(255, 255, 255, 0.9);
                    }
        
                    table.cal {
                        width: 100%;
                        border-collapse: separate;
                        border-spacing: 6px;
                    }
        
                    table.cal th, table.cal td {
                        text-align: center;
                        padding: 10px 0;
                        border-radius: 10px;
                    }
        
                    table.cal thead th {
                        font-weight: 700;
                        color: #bb86fc;
                        background: rgba(255, 255, 255, 0.1);
                        border: 1px solid rgba(255, 255, 255, 0.1);
                    }
        
                    td.day {
                        background: rgba(255, 255, 255, 0.05);
                        border: 1px solid rgba(255, 255, 255, 0.1);
                        color: rgba(255, 255, 255, 0.8);
                        transition: all 0.2s;
                    }
        
                    td.day:hover {
                        background: rgba(255, 255, 255, 0.1);
                    }
        
                    td.muted {
                        opacity: 0.3;
                    }
        
                    td.today {
                        background: #2196f3;
                        border-color: #64b5f6;
                        color: #fff;
                        font-weight: 700;
                        box-shadow: 0 0 10px rgba(33, 150, 243, 0.5);
                    }
        
                    /* Thêm style cho ngày Chủ Nhật */
                    td.sunday {
                        color: #f44336 !important;
                        font-weight: bold;
                    }
        
                    .legend {
                        margin-top: 25px;
                        font-size: 1.1rem; /* Đã tăng cỡ chữ */
                        font-weight: bold; /* Đã thêm để chữ nổi bật */
                        color: rgba(255, 255, 255, 0.7);
                        text-align: center;
                    }
        
                    .footer {
                        position: fixed;
                        bottom: 0;
                        width: 100%;
                        padding: 10px 0;
                        font-size: 0.8rem;
                        color: #778899;
                        font-style: italic;
                        text-align: center;
                        linear-gradient(135deg, #1f3c54, #173d5c);
                        backdrop-filter: blur(5px);
                        z-index: 999;
                    }
        
                    @media (max-width: 768px) {
                        .container {
                            flex-direction: column;
                            align-items: center;
                        }
            
                        .calendar {
                            width: 100%;
                        }
            
                        .clock {
                            width: 240px;
                            height: 240px;
                        }
            
                        .tick {
                            transform-origin: 50% 120px;
                        }
                    }
                </style>
            </head>
            <body>
                <div class='container'>
                    <div class='card clock-container'>
                        <h2 class='title'>Đồng hồ</h2>
                        <div class='clock' id='clock'>
                            <div class='hand hour' id='h'></div>
                            <div class='hand minute' id='m'></div>
                            <div class='hand second' id='s'></div>
                            <div class='center-dot'></div>
                        </div>
                        <div class='digital-time' id='digitalTime'>00:00:00</div>
                    </div>
        
                    <div class='card calendar'>
                        <div class='cal-head'>
                            <div class='btn' id='prev' onclick='shiftMonth(-1)' aria-label='Tháng trước'>&larr;</div>
                            <div class='month-label' id='monthLabel'>Tháng</div>
                            <div class='btn' id='next' onclick='shiftMonth(1)' aria-label='Tháng sau'>&rarr;</div>
                        </div>
                        <table class='cal' aria-label='Lịch tháng'>
                            <thead>
                                <tr>
                                    <th>T2</th><th>T3</th><th>T4</th><th>T5</th><th>T6</th><th>T7</th><th style='color: #f44336;'>CN</th>
                                </tr>
                            </thead>
                            <tbody id='calBody'></tbody>
                        </table>
                        <div class='legend' id='todayLegend'></div>
                        <div class='legend' id='lunarLegend'></div>
                    </div>
                </div>
    
                <div class='footer'>Thiết kế bởi Nông Văn Phấn®</div>
    
                <script>
                    // ===== ĐỒNG HỒ =====
                    function createClockElements() {
                        const clock = document.getElementById('clock');
                        const clockSize = 280; // Kích thước đồng hồ
                        const radius = clockSize / 2; // Bán kính
            
                        // Tạo vạch chia phút
                        for (let i = 0; i < 60; i++) {
                            const tick = document.createElement('div');
                            tick.className = 'tick ' + (i % 5 === 0 ? 'major' : 'minor');
                            const angle = i * 6; // 6 độ cho mỗi phút
                            tick.style.transform = 'rotate(' + angle + 'deg)';
                            clock.appendChild(tick);
                        }
            
                        // Tạo các số giờ
                        const numbers = [12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
                        const numberRadius = radius - 30; // Khoảng cách từ tâm đến số
                        numbers.forEach((num, i) => {
                            const number = document.createElement('div');
                            number.className = 'number';
                            number.innerHTML = num;
                            const angle = (i - 3) * 30; // Mỗi số cách nhau 30 độ
                            const radian = angle * Math.PI / 180;
                            const x = Math.cos(radian) * numberRadius;
                            const y = Math.sin(radian) * numberRadius;
                            number.style.position = 'absolute';
                            number.style.left = '50%';
                            number.style.top = '50%';
                            number.style.transform = 'translate(' + x + 'px, ' + y + 'px) translate(-50%, -50%)';
                            number.style.width = '20px';
                            number.style.height = '20px';
                            number.style.textAlign = 'center';
                            number.style.lineHeight = '20px';
                            number.style.zIndex = '2';
                            clock.appendChild(number);
                        });
                    }
        
                    // Hàm lấy thời gian theo GMT+7 (UTC+7)
                    function getGMT7Time() {
                        const now = new Date();
                        const utc = new Date(now.getTime() + now.getTimezoneOffset() * 60000); // Chuyển sang UTC
                        return new Date(utc.getTime() + 7 * 3600000); // Thêm 7 giờ cho GMT+7
                    }
        
                    function updateClock() {
                        const now = getGMT7Time(); // Sử dụng thời gian GMT+7
                        const sec = now.getSeconds();
                        const min = now.getMinutes();
                        const hr = now.getHours() % 12;
            
                        // Chỉnh lại góc quay để kim đồng hồ bắt đầu từ 12 giờ
                        const secDeg = (sec * 6) - 90; // 6 độ/giây, trừ 90 độ để kim thẳng đứng
                        const minDeg = (min * 6 + sec * 0.1) - 90; // 6 độ/phút, cộng thêm góc từ giây, trừ 90 độ
                        const hrDeg = (hr * 30 + min * 0.5) - 90; // 30 độ/giờ, cộng thêm góc từ phút, trừ 90 độ
            
                        document.getElementById('s').style.transform = 'rotate(' + secDeg + 'deg)';
                        document.getElementById('m').style.transform = 'rotate(' + minDeg + 'deg)';
                        document.getElementById('h').style.transform = 'rotate(' + hrDeg + 'deg)';
            
                        // Cập nhật đồng hồ số theo GMT+7
                        const digitalTime = document.getElementById('digitalTime');
                        digitalTime.textContent = String(now.getHours()).padStart(2, '0') + ':' + String(min).padStart(2, '0') + ':' + String(sec).padStart(2, '0');
                    }
        
                    // Khởi tạo đồng hồ
                    createClockElements();
                    updateClock();
                    setInterval(updateClock, 500);
        
                    // ===== LỊCH =====
                    let viewMonth, viewYear;
                    const monthNames = ['Tháng 1', 'Tháng 2', 'Tháng 3', 'Tháng 4', 'Tháng 5', 'Tháng 6', 'Tháng 7', 'Tháng 8', 'Tháng 9', 'Tháng 10', 'Tháng 11', 'Tháng 12'];
                    const lunarMonthNames = ['Một', 'Hai', 'Ba', 'Tư', 'Năm', 'Sáu', 'Bảy', 'Tám', 'Chín', 'Mười', 'Mười một', 'Chạp'];
                    const canChiDay = ['Giáp', 'Ất', 'Bính', 'Đinh', 'Mậu', 'Kỷ', 'Canh', 'Tân', 'Nhâm', 'Quý'];
                    const canChiYear = ['Tý', 'Sửu', 'Dần', 'Mão', 'Thìn', 'Tị', 'Ngọ', 'Mùi', 'Thân', 'Dậu', 'Tuất', 'Hợi'];
        
                    // Hàm chuyển đổi dương lịch sang âm lịch (phiên bản minh họa)
                    function toLunarDate(solarDate) {
                        // Đây là một hàm đơn giản và không chính xác hoàn toàn.
                        // Để có lịch âm chính xác, bạn cần sử dụng một thư viện chuyên dụng.
                        const day = solarDate.getDate();
                        const month = solarDate.getMonth() + 1;
                        const year = solarDate.getFullYear();
            
                        // Giả lập một cách tính đơn giản (không chính xác)
                        const lunarDay = (day + 7) % 30 || 30; 
                        const lunarMonth = (month + 10) % 12 || 12;
                        const lunarYear = (year + 7) % 12;
            
                        return 'Ngày ' + lunarDay + ', ' + 'Tháng ' + lunarMonth;
                    }
        
                    function pad(n) {
                        return (n < 10 ? '0' : '') + n;
                    }
        
                    function renderCalendar(y, m) {
                        const today = new Date();
                        const isThisMonth = (y === today.getFullYear() && m === today.getMonth());
                        const tbody = document.getElementById('calBody');
                        const label = document.getElementById('monthLabel');
                        const legend = document.getElementById('todayLegend');
                        const lunarLegend = document.getElementById('lunarLegend');
            
                        label.textContent = monthNames[m] + ' ' + y;
            
                        // Xóa nội dung cũ
                        tbody.innerHTML = '';
            
                        // Tính toán ngày
                        const first = new Date(y, m, 1);
                        const daysInMonth = new Date(y, m + 1, 0).getDate();
                        const daysInPrevMonth = new Date(y, m, 0).getDate();
            
                        // 0=Chủ nhật, 1=Thứ 2, ... 6=Thứ 7
                        let startDayOfWeek = first.getDay(); 
                        // Điều chỉnh để Thứ Hai là ngày đầu tiên (0), ..., Chủ Nhật là ngày cuối cùng (6)
                        if (startDayOfWeek === 0) {
                            startDayOfWeek = 6;
                        } else {
                            startDayOfWeek--;
                        }
            
                        let date = 1;
                        for (let i = 0; i < 6; i++) { // Tối đa 6 hàng tuần
                            let tr = document.createElement('tr');
                            for (let j = 0; j < 7; j++) { // 7 ngày trong tuần
                                const td = document.createElement('td');
                                let displayDay;
                                let isMuted = false;
                    
                                if (i === 0 && j < startDayOfWeek) {
                                    // Ngày của tháng trước
                                    displayDay = daysInPrevMonth - startDayOfWeek + j + 1;
                                    isMuted = true;
                                } else if (date > daysInMonth) {
                                    // Ngày của tháng sau
                                    displayDay = date - daysInMonth;
                                    isMuted = true;
                                    date++;
                                } else {
                                    // Ngày trong tháng hiện tại
                                    displayDay = date;
                                    date++;
                                }
                    
                                td.textContent = displayDay;
                                td.className = 'day';
                    
                                if (isMuted) {
                                    td.classList.add('muted');
                                }
                    
                                // Kiểm tra và thêm class 'today'
                                if (!isMuted && isThisMonth && displayDay === today.getDate()) {
                                    td.classList.add('today');
                                }
                    
                                // Kiểm tra và thêm class 'sunday' (cột cuối cùng)
                                if (j === 6) { 
                                    td.classList.add('sunday');
                                }
                    
                                tr.appendChild(td);
                            }
                            tbody.appendChild(tr);
                            if (date > daysInMonth && i >= 4) break; // Thoát nếu đã render hết các ngày
                        }
            
                        // Chú thích hôm nay
                        const txt = 'Hôm nay: ' + pad(today.getDate()) + '/' + pad(today.getMonth() + 1) + '/' + today.getFullYear();
                        legend.textContent = txt;
            
                        // Hiển thị ngày âm lịch
                        //const lunarDateStr = toLunarDate(today);
                        //lunarLegend.textContent = 'Âm lịch: ' + lunarDateStr;
                    }
        
                    function shiftMonth(delta) {
                        viewMonth += delta;
                        if (viewMonth < 0) {
                            viewMonth = 11;
                            viewYear--;
                        } else if (viewMonth > 11) {
                            viewMonth = 0;
                            viewYear++;
                        }
                        renderCalendar(viewYear, viewMonth);
                    }
        
                    // Khởi tạo lịch
                    (function() {
                        const now = new Date();
                        viewMonth = now.getMonth();
                        viewYear = now.getFullYear();
                        renderCalendar(viewYear, viewMonth);
                    })();
                </script>
            </body>
            </html>";
        }
        #endregion
       
        #region QR Code & Data Matrix
        /// <summary>
        /// Xử lý yêu cầu tạo hình ảnh mã QR/Data Matrix và trả về
        /// </summary>
        private async Task HandleGenerateImageRequest(HttpListenerContext context)
        {
            string clientIp = context.Request.RemoteEndPoint.Address.ToString();
            var query = HttpUtility.ParseQueryString(context.Request.Url.Query);
            string data = query["data"] ?? "";
            string type = query["type"] ?? "qrcode";
            string suffix = query["suffix"] ?? "off";

            // Trả về một hình ảnh trống nếu dữ liệu rỗng
            if (string.IsNullOrEmpty(data))
            {
                context.Response.ContentType = "image/png";
                using (var bmp = new Bitmap(1, 1))
                {
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        byte[] emptyBuffer = ms.ToArray();
                        context.Response.ContentLength64 = emptyBuffer.Length;
                        await context.Response.OutputStream.WriteAsync(emptyBuffer, 0, emptyBuffer.Length);
                    }
                }
                context.Response.Close();
                return;
            }

            if (type == "qrcode")
            {
                if (suffix == "1")
                {
                    data += "#1";
                }
                else if (suffix == "2")
                {
                    data += "#2";
                }
            }

            try
            {
                var barcodeWriter = new BarcodeWriter
                {
                    Format = (type == "datamatrix") ? BarcodeFormat.DATA_MATRIX : BarcodeFormat.QR_CODE,
                    Options = new EncodingOptions
                    {
                        Height = 300,
                        Width = 300,
                        Margin = 1
                    }
                };

                var barcodeBitmap = barcodeWriter.Write(data);

                using (MemoryStream ms = new MemoryStream())
                {
                    barcodeBitmap.Save(ms, ImageFormat.Png);
                    byte[] imageBytes = ms.ToArray();

                    context.Response.ContentType = "image/png";
                    context.Response.ContentLength64 = imageBytes.Length;
                    await context.Response.OutputStream.WriteAsync(imageBytes, 0, imageBytes.Length);
                    UpdateLog($"[{clientIp}] Đã tạo và gửi mã {type.ToUpper()} code.");
                }
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                UpdateLog($"[{clientIp}] Lỗi khi tạo mã: {ex.Message}");
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// Trả về mã HTML của trang tạo mã QR/Data Matrix
        /// </summary>
        private string GetQrCodePageHtml()
        {
            return @"
            <!DOCTYPE html>
            <html lang='vi'>
            <head>
                <meta charset='UTF-8'>
                <meta http-equiv='X-UA-Compatible' content='IE=edge'>
                <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                <title>TRÌNH TẠO MÃ CODE</title>
                <style>
                    body {
                        font-family: 'Segoe UI', 'Roboto', 'Helvetica Neue', Arial, sans-serif;
                        background: #2c3e50;
                        color: #ecf0f1;
                        display: flex;
                        justify-content: center;
                        align-items: flex-start;
                        min-height: 100vh;
                        margin: 0;
                        padding: 20px;
                        box-sizing: border-box;
                        padding-bottom: 50px;
                    }
                    .container {
                        background: #34495e;
                        padding: 30px;
                        border-radius: 15px;
                        box-shadow: 0 10px 25px rgba(0, 0, 0, 0.2);
                        width: 100%;
                        max-width: 600px;
                        text-align: center;
                        border: 1px solid #4a647e;
                        margin-top: 30px;
                    }
                    h1 {
                        color: #ecf0f1;
                        font-weight: bold;
                        margin-bottom: 25px;
                        font-size: 1.8em;
                    }
                    .form-group {
                        margin-bottom: 20px;
                        display: flex;
                        flex-direction: column;
                        align-items: center;
                    }
                    .data-input-wrapper {
                        width: 100%;
                        margin-bottom: 15px;
                    }
                    .data-input-wrapper label {
                        display: block;
                        text-align: center;
                        margin-bottom: 5px;
                        color: #bdc3c7;
                        font-size: 0.9em;
                    }
                    .data-input-wrapper textarea {
                        width: 95%;
                        padding: 5px 12px;
                        border-radius: 10px;
                        border: 1px solid #546a81;
                        background: #253341;
                        color: #ecf0f1;
                        font-size: 1.5em;
                        box-shadow: inset 0 2px 5px rgba(0,0,0,0.1);
                        resize: vertical;
                        min-height: 20px;
                        font-weight: bold; //Font chữ đậm
                    }
                    .data-input-wrapper textarea:focus {
                        outline: none;
                        border-color: #3498db;
                        box-shadow: 0 0 10px rgba(52, 152, 219, 0.4);
                    }
                    .button-group {
                        display: flex;
                        justify-content: center; /* Đã thay đổi */
                        align-items: center;
                        margin-bottom: 25px;
                        flex-wrap: wrap;
                        gap: 50px; /* Thêm dòng này để chỉnh khoảng cách */
                    }
                    .button-group button {
                        background: #3498db;
                        border: none;
                        color: white;
                        padding: 12px 20px;
                        font-size: 1em;
                        
                        border-radius: 8px;
                        cursor: pointer;
                        transition: background-color 0.3s, box-shadow 0.3s;
                        flex-grow: 0; /*Thuộc tính flex-grow: 1; các nút tự động dãn ra để lấp đầy toàn bộ không gian còn trống, flex-grow: 0; Nếu muốn đặt chiều rộng cố định*/
                        width: 150px; /* Đặt chiều rộng cố định mong muốn tại đây */
                        font-weight: bold; //Font chữ đậm
                    }
                    .button-group button:hover {
                        background: #2980b9;
                    }
                    .button-group button.active {
                        background: #2ecc71;
                        box-shadow: 0 0 10px rgba(46, 204, 113, 0.5);
                    }
                    .button-group button#resetBtn {
                        background: #e74c3c;
                    }
                    .button-group button#resetBtn:hover {
                        background: #c0392b;
                    }
                    .toggle-switch-group {
                        display: flex;
                        justify-content: center; /* Đã thay đổi */
                        align-items: center;
                        width: 100%;
                        margin-top: 10px;
                        margin-bottom: 10px;
            
                    }
                    .toggle-label {
                        margin-right: 15px;
                        font-size: 0.9em;
                        color: #bdc3c7;
                        white-space: nowrap;
                        font-weight: bold;
                    }
                    .toggle-switch {
                        position: relative;
                        display: inline-block;
                        width: 48px;
                        height: 28px;
                    }
                    .toggle-switch input {
                        display: none;
                    }
                    .toggle-slider {
                        position: absolute;
                        cursor: pointer;
                        top: 0;
                        left: 0;
                        right: 0;
                        bottom: 0;
                        background-color: #7f8c8d;
                        transition: .4s;
                        border-radius: 28px;
                    }
                    .toggle-slider:before {
                        position: absolute;
                        content: '';
                        height: 20px;
                        width: 20px;
                        left: 4px;
                        bottom: 4px;
                        background-color: white;
                        transition: .4s;
                        border-radius: 50%;
                    }
                    input:checked + .toggle-slider {
                        background-color: #2ecc71;
                    }
                    input:checked + .toggle-slider:before {
                        transform: translateX(20px);
                    }
                    .code-display-container {
                        display: flex;
                        justify-content: space-around;
                        flex-wrap: wrap;
                        margin-top: 20px;
                    }
                    .code-item {
                        display: flex;
                        flex-direction: column;
                        align-items: center;
                        margin: 10px;
                    }
                    .code-image {
                        width: 200px;
                        height: 200px;
                        background: #253341;
                        border: 1px solid #546a81;
                        border-radius: 8px;
                        padding: 5px;
                        box-sizing: border-box;
                        box-shadow: 0 4px 15px rgba(0, 0, 0, 0.3);
                        display: block;
                    }
                    .code-name {
                        margin-top: 10px;
                        font-size: 0.9em;
                        color: #bdc3c7;
                        word-wrap: break-word;
                        width: 200px;
                        text-align: center;
                        min-height: 1.2em;
                        font-weight: bold; //Font chữ đậm
                    }
                    .download-link {
                        display: none;
                        margin-top: 10px;
                        padding: 8px 12px;
                        background: #f39c12;
                        color: white;
                        text-decoration: none;
                        border-radius: 5px;
                        font-size: 0.9em;
                        transition: background-color 0.3s;
                    }
                    .download-link:hover {
                        background: #e67e22;
                    }
                    #qrCodeContainer, #dataMatrixContainer {
                        display: flex;
                        justify-content: space-around;
                        flex-wrap: wrap;
                        width: 100%;
                    }
                    .footer {
                        position: fixed;
                        bottom: 0;
                        left: 0;
                        right: 0;
                        background: #2c3e50;
                        padding: 10px 0;
                        text-align: center;
                        font-size: 0.7em;
                        color: #778899;
                        font-style: italic;
                        z-index: 100;
                    }
                    @media (max-width: 600px) {
                        .container {
                            padding: 15px;
                            margin-top: 15px;
                        }
                        .button-group {
                            flex-direction: column;
                        }
                        .button-group button {
                            width: 100%;
                            margin: 5px 0;
                        }
                        .data-input-wrapper textarea {
                            width: 90%;
                        }
                    }
                </style>
            </head>
            <body>
                <div class='container'>
                    <h1>TRÌNH TẠO MÃ QRCODE & DATA MATRIX</h1>
                    <div class='form-group'>
                        <div class='data-input-wrapper'>
                            <label for='dataInput'>Nhập dữ liệu để tạo mã:</label>
                            <textarea id='dataInput' placeholder='Nhập dữ liệu để tạo mã...' rows='4'></textarea>
                        </div>
                    </div>
                    <div class='button-group'>
                        <button id='typeBtn' class='active' title='Bấm vào đây để chuyển đổi giữa Mã QR và Mã Data Matrix'>Mã QR</button>
                        <button id='resetBtn'>Reset</button>
                    </div>
                    <div class='toggle-switch-group'>
                        <span class='toggle-label'>Thêm hậu tố #1 #2</span>
                        <label class='toggle-switch'>
                            <input type='checkbox' id='suffixToggle'>
                            <span class='toggle-slider'></span>
                        </label>
                    </div>
        
                    <div id='codeDisplayContainer' class='code-display-container'>
                        <div id='qrCodeContainer'>
                            <div class='code-item'>
                                <img id='qrCodeImage1' class='code-image' src=''>
                                <div id='qrCodeName1' class='code-name'></div>
                                <a id='downloadQr1' class='download-link' href=''>Tải về</a>
                            </div>
                            <div class='code-item'>
                                <img id='qrCodeImage2' class='code-image' src=''>
                                <div id='qrCodeName2' class='code-name'></div>
                                <a id='downloadQr2' class='download-link' href=''>Tải về</a>
                            </div>
                        </div>
                        <div id='dataMatrixContainer' style='display: none;'>
                            <div class='code-item'>
                                <img id='dataMatrixImage' class='code-image' src=''>
                                <div id='dataMatrixName' class='code-name'></div>
                                <a id='downloadDm' class='download-link' href=''>Tải về</a>
                            </div>
                        </div>
                    </div>
                </div>
                <div class='footer'>Thiết kế bởi Nông Văn Phấn®</div>
                <script>
                    function getBaseUrl() {
                        var url = window.location.protocol + '//' + window.location.host;
                        return url;
                    }

                    function getSafeFilename(text) {
                        // Thay thế các ký tự không hợp lệ cho tên file bằng dấu gạch dưới
                        // Giữ lại khoảng trắng và #
                        var invalidChars = /[\/\\?%*:|""<>]/g;
                        var safeText = text.replace(invalidChars, '_');
                        return safeText.substring(0, 50).trim(); // Giới hạn độ dài và loại bỏ khoảng trắng thừa
                    }

                    function updateCode() {
                        var data = document.getElementById('dataInput').value.trim();
                        var isQrCode = document.getElementById('typeBtn').textContent === 'Mã QR';
                        var addSuffix = document.getElementById('suffixToggle').checked;
                        var baseUrl = getBaseUrl();
                        var qrCodeContainer = document.getElementById('qrCodeContainer');
                        var dataMatrixContainer = document.getElementById('dataMatrixContainer');

                        var downloadQr1Link = document.getElementById('downloadQr1');
                        var downloadQr2Link = document.getElementById('downloadQr2');
                        var downloadDmLink = document.getElementById('downloadDm');

                        if (data === '') {
                            downloadQr1Link.style.display = 'none';
                            downloadQr2Link.style.display = 'none';
                            downloadDmLink.style.display = 'none';
                            document.getElementById('qrCodeImage1').src = baseUrl + '/generate-image?data=&type=qrcode';
                            document.getElementById('qrCodeImage2').src = baseUrl + '/generate-image?data=&type=qrcode';
                            document.getElementById('dataMatrixImage').src = baseUrl + '/generate-image?data=&type=datamatrix';
                            document.getElementById('qrCodeName1').textContent = '';
                            document.getElementById('qrCodeName2').textContent = '';
                            document.getElementById('dataMatrixName').textContent = '';
                            return;
                        }

                        if (isQrCode) {
                            qrCodeContainer.style.display = 'flex';
                            dataMatrixContainer.style.display = 'none';

                            var data1 = data;
                            var data2 = data;
                
                            if (addSuffix) {
                                data1 += '#1';
                                data2 += '#2';
                            }

                            var url1 = baseUrl + '/generate-image?data=' + encodeURIComponent(data) + '&type=qrcode&suffix=' + (addSuffix ? '1' : 'off');
                            document.getElementById('qrCodeImage1').src = url1;
                            document.getElementById('qrCodeName1').textContent = data1;
                            downloadQr1Link.style.display = 'block';
                            downloadQr1Link.href = url1;
                            downloadQr1Link.setAttribute('download', getSafeFilename(data1) + '.png');

                            var url2 = baseUrl + '/generate-image?data=' + encodeURIComponent(data) + '&type=qrcode&suffix=' + (addSuffix ? '2' : 'off');
                            document.getElementById('qrCodeImage2').src = url2;
                            document.getElementById('qrCodeName2').textContent = data2;
                            downloadQr2Link.style.display = 'block';
                            downloadQr2Link.href = url2;
                            downloadQr2Link.setAttribute('download', getSafeFilename(data2) + '.png');

                        } else {
                            qrCodeContainer.style.display = 'none';
                            dataMatrixContainer.style.display = 'flex';
                
                            var url = baseUrl + '/generate-image?data=' + encodeURIComponent(data) + '&type=datamatrix&suffix=off';
                            document.getElementById('dataMatrixImage').src = url;
                            document.getElementById('dataMatrixName').textContent = data;
                            downloadDmLink.style.display = 'block';
                            downloadDmLink.href = url;
                            downloadDmLink.setAttribute('download', getSafeFilename(data) + '.png');
                        }
                    }

                    document.getElementById('dataInput').addEventListener('input', updateCode);
                    document.getElementById('typeBtn').addEventListener('click', function() {
                        var typeBtn = document.getElementById('typeBtn');
                        var currentType = typeBtn.textContent;
                        if (currentType === 'Mã QR') {
                            typeBtn.textContent = 'Mã Data Matrix';
                            typeBtn.classList.remove('active');
                        } else {
                            typeBtn.textContent = 'Mã QR';
                            typeBtn.classList.add('active');
                        }
                        document.getElementById('suffixToggle').closest('.toggle-switch-group').style.display = (typeBtn.textContent === 'Mã QR') ? 'flex' : 'none';
                        updateCode();
                    });
                    document.getElementById('resetBtn').addEventListener('click', function() {
                        document.getElementById('dataInput').value = '';
                        updateCode();
                    });
                    document.getElementById('suffixToggle').addEventListener('change', updateCode);
        
                    document.addEventListener('DOMContentLoaded', function() {
                        document.getElementById('suffixToggle').closest('.toggle-switch-group').style.display = (document.getElementById('typeBtn').textContent === 'Mã QR') ? 'flex' : 'none';
                        updateCode();
                    });
                </script>
            </body>
            </html>";
        }
        #endregion

        #region TRÌNH TẠO MÃ QRCODE KITING
        /// <summary>
        /// Trả về mã HTML của trang tạo mã QR theo bộ
        /// </summary>
        private string GetKitPageHtml()
        {
            return @"
            <!DOCTYPE html>
            <html lang='vi'>
            <head>
                <meta charset='UTF-8'>
                <meta http-equiv='X-UA-Compatible' content='IE=edge'>
                <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                <title>TRÌNH TẠO MÃ QRCODE</title>
                <style>
                    body {
                        font-family: 'Segoe UI', 'Roboto', 'Helvetica Neue', Arial, sans-serif;
                        background: #2c3e50;
                        color: #ecf0f1;
                        display: flex;
                        justify-content: center;
                        align-items: flex-start;
                        min-height: 100vh;
                        margin: 0;
                        padding: 20px;
                        box-sizing: border-box;
                        padding-bottom: 50px;
                    }
                    .container {
                        background: #34495e;
                        padding: 30px;
                        border-radius: 15px;
                        box-shadow: 0 10px 25px rgba(0, 0, 0, 0.2);
                        width: 100%;
                        max-width: 800px;
                        text-align: center;
                        border: 1px solid #4a647e;
                        margin-top: 30px;
                    }
                    h1 {
                        color: #ecf0f1;
                        font-weight: bold;
                        margin-bottom: 25px;
                        font-size: 1.8em;
                    }
                    .form-group {
                        margin-bottom: 20px;
                        display: flex;
                        flex-direction: column;
                        align-items: center;
                    }
                    .input-pair {
                        display: flex;
                        flex-wrap: wrap;
                        gap: 20px;
                        width: 100%;
                        justify-content: center;
                    }
                    .data-input-wrapper {
                        flex: 1;
                        min-width: 250px;
                        margin-bottom: 15px;
                    }
                    .data-input-wrapper label {
                        display: block;
                        text-align: center;
                        margin-bottom: 5px;
                        color: #bdc3c7;
                        font-size: 0.9em;
                        font-style: italic;
                    }
                    .data-input-wrapper textarea {
                        width: 100%;
                        padding: 5px 12px;
                        border-radius: 8px;
                        border: 1px solid #546a81;
                        background: #253341;
                        color: #ecf0f1;
                        font-size: 1.3em;
                        box-shadow: inset 0 2px 5px rgba(0,0,0,0.1);
                        resize: vertical;
                        min-height: 20px;
                        font-weight: bold;
                        box-sizing: border-box; /* Quan trọng để padding không làm hỏng width */
                    }
                    .button-group {
                        display: flex;
                        justify-content: center;
                        align-items: center;
                        margin-bottom: 25px;
                        flex-wrap: wrap;
                        gap: 10px;
                    }
                    .button-group button {
                        background: #e74c3c;
                        border: none;
                        color: white;
                        padding: 12px 20px;
                        font-size: 1em;
                        font-weight: bold;
                        border-radius: 8px;
                        cursor: pointer;
                        transition: background-color 0.3s, box-shadow 0.3s;
                        width: 100px;
                    }
                    .button-group button:hover {
                        background: #c0392b;
                    }
                    .code-display-container {
                        display: flex;
                        justify-content: space-around;
                        flex-wrap: wrap;
                        margin-top: 20px;
                    }
                    .code-item {
                        display: flex;
                        flex-direction: column;
                        align-items: center;
                        margin: 10px;
                    }
                    .code-image {
                        width: 200px;
                        height: 200px;
                        background: #253341;
                        border: 1px solid #546a81;
                        border-radius: 8px;
                        padding: 5px;
                        box-sizing: border-box;
                        box-shadow: 0 4px 15px rgba(0, 0, 0, 0.3);
                        display: block;
                    }
                    .code-name {
                        margin-top: 10px;
                        font-size: 0.9em;
                        color: #bdc3c7;
                        word-wrap: break-word;
                        width: 200px;
                        text-align: center;
                        min-height: 1.2em;
                        font-weight: bold;
                    }
                    .download-link {
                        display: none;
                        margin-top: 10px;
                        padding: 8px 12px;
                        background: #f39c12;
                        color: white;
                        text-decoration: none;
                        border-radius: 5px;
                        font-size: 0.9em;
                        transition: background-color 0.3s;
                    }
                    .download-link:hover {
                        background: #e67e22;
                    }
                    .footer {
                        position: fixed;
                        bottom: 0;
                        left: 0;
                        right: 0;
                        background: #2c3e50;
                        padding: 10px 0;
                        text-align: center;
                        font-size: 0.8em;
                        color: #778899;
                        font-style: italic;
                        z-index: 100;
                    }
                    @media (max-width: 600px) {
                        .container {
                            padding: 15px;
                            margin-top: 15px;
                        }
                        .button-group {
                            flex-direction: column;
                        }
                        .button-group button {
                            width: 100%;
                            margin: 5px 0;
                        }
                        .input-pair {
                            flex-direction: column;
                            gap: 0;
                        }
                        .data-input-wrapper {
                            min-width: unset;
                            width: 100%;
                        }
                    }
                </style>
            </head>
            <body>
                <div class='container'>
                    <h1>TRÌNH TẠO MÃ QRCODE</h1>
                    <div class='form-group'>
                        <div class='input-pair'>
                            <div class='data-input-wrapper'>
                                <label for='dataInput1'>Dữ liệu mã QR #1:</label>
                                <textarea id='dataInput1' placeholder='Nhập dữ liệu cho mã QR thứ nhất...' rows='2'></textarea>
                            </div>
                            <div class='data-input-wrapper'>
                                <label for='dataInput2'>Dữ liệu mã QR #2:</label>
                                <textarea id='dataInput2' placeholder='Nhập dữ liệu cho mã QR thứ hai...' rows='2'></textarea>
                            </div>
                        </div>
                    </div>
                    <div class='button-group'>
                        <button id='resetBtn'>Reset</button>
                    </div>
        
                    <div class='code-display-container'>
                        <div class='code-item'>
                            <img id='qrCodeImage1' class='code-image' src=''>
                            <div id='qrCodeName1' class='code-name'></div>
                            <a id='downloadQr1' class='download-link' href=''>Tải về</a>
                        </div>
                        <div class='code-item'>
                            <img id='qrCodeImage2' class='code-image' src=''>
                            <div id='qrCodeName2' class='code-name'></div>
                            <a id='downloadQr2' class='download-link' href=''>Tải về</a>
                        </div>
                    </div>
                </div>
                <div class='footer'>Thiết kế bởi Nông Văn Phấn®</div>
                <script>
                    function getBaseUrl() {
                        var url = window.location.protocol + '//' + window.location.host;
                        return url;
                    }

                    function getSafeFilename(text) {
                        var invalidChars = /[\\/?%*:|\""<>]/g;
                        var safeText = text.replace(invalidChars, '_');
                        return safeText.substring(0, 50).trim();
                    }

                    function updateCode() {
                        var data1 = document.getElementById('dataInput1').value.trim();
                        var data2 = document.getElementById('dataInput2').value.trim();
                        var baseUrl = getBaseUrl();
            
                        // Xử lý mã QR 1
                        var img1 = document.getElementById('qrCodeImage1');
                        var name1 = document.getElementById('qrCodeName1');
                        var link1 = document.getElementById('downloadQr1');
                        if (data1 !== '') {
                            var url1 = baseUrl + '/generate-image?data=' + encodeURIComponent(data1) + '&type=qrcode';
                            img1.src = url1;
                            name1.textContent = data1;
                            link1.href = url1;
                            link1.setAttribute('download', getSafeFilename(data1) + '.png');
                            link1.style.display = 'block';
                        } else {
                            img1.src = '';
                            name1.textContent = '';
                            link1.style.display = 'none';
                        }
            
                        // Xử lý mã QR 2
                        var img2 = document.getElementById('qrCodeImage2');
                        var name2 = document.getElementById('qrCodeName2');
                        var link2 = document.getElementById('downloadQr2');
                        if (data2 !== '') {
                            var url2 = baseUrl + '/generate-image?data=' + encodeURIComponent(data2) + '&type=qrcode';
                            img2.src = url2;
                            name2.textContent = data2;
                            link2.href = url2;
                            link2.setAttribute('download', getSafeFilename(data2) + '.png');
                            link2.style.display = 'block';
                        } else {
                            img2.src = '';
                            name2.textContent = '';
                            link2.style.display = 'none';
                        }
                    }

                    function resetInputs() {
                        document.getElementById('dataInput1').value = '';
                        document.getElementById('dataInput2').value = '';
                        updateCode();
                    }

                    document.getElementById('dataInput1').addEventListener('input', updateCode);
                    document.getElementById('dataInput2').addEventListener('input', updateCode);
                    document.getElementById('resetBtn').addEventListener('click', resetInputs);

                    document.addEventListener('DOMContentLoaded', updateCode);
                </script>
            </body>
            </html>";
                    }
        #endregion

        #region Xử lý upload file
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
                byte[] buffer = new byte[32768];
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
        #endregion

        #region LẤY NỘI DUNG FILE
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
        #endregion
        private void StopSharing()
        {
            if (_listener != null && _listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
                _listener = null;
                UpdateLog("╔════════════════════════════════╗");
                UpdateLog("║            ■ Ứng dụng đã dừng chia sẻ                            ║");
                UpdateLog("╚════════════════════════════════╝");
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
            string prefix = isError ? "❖ [!] " : "• ";
            string timePart = $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}]";
            string formattedMessage = $"{prefix}{timePart} {message}";
            //\r\n là xuống dòng trong Windows

            // Tạo thư mục "Logs" nếu chưa tồn tại
            string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Log");
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // Đặt đường dẫn file log trong thư mục Logs
            string logFilePath = Path.Combine(logDirectory, "log.txt");

            // Ghi log vào file
            try
            {
                File.AppendAllText(logFilePath, formattedMessage + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // Ghi lại lỗi nếu không thể ghi file
                string errorLogMessage = $"Lỗi ghi log vào file: {ex.Message}";
                if (this.InvokeRequired)
                {
                    this.Invoke(new Action(() =>
                    {
                        txtLog.AppendText($"❖ [!] {timePart} {errorLogMessage}\r\n");
                    }));
                }
                else
                {
                    txtLog.AppendText($"❖ [!] {timePart} {errorLogMessage}\r\n");
                }
                // Kết thúc phương thức tại đây để tránh ghi đúp log
                return;
            }

            // Hiển thị log lên TextBox trên giao diện
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
        #region Phương thức GetFileIcon
        private string GetFileIcon(string fileExtension)
        {
            fileExtension = fileExtension.ToLower();
            switch (fileExtension)
            {
                case ".pdf":
                    return "📕"; // Sách đỏ cho file PDF
                case ".doc":
                case ".docx":
                    return "📝"; // Ghi chú cho file Word
                case ".xls":
                case ".xlsx":
                    return "📊"; // Biểu đồ cho file Excel
                case ".ppt":
                case ".pptx":
                    return "📈"; // Biểu đồ tăng cho file PowerPoint
                case ".jpg":
                case ".jpeg":
                case ".png":
                case ".gif":
                case ".bmp":
                    return "🖼️"; // Khung ảnh cho file ảnh
                case ".zip":
                case ".rar":
                case ".7z":
                    return "📦"; // Hộp cho file nén
                case ".mp3":
                case ".wav":
                case ".flac":
                    return "🎵"; // Nốt nhạc cho file âm thanh
                case ".mp4":
                case ".avi":
                case ".mov":
                    return "🎬"; // Máy quay cho file video
                case ".txt":
                    return "🗒️"; // Cuộn giấy cho file văn bản
                case ".html":
                case ".htm":
                    return "🌐"; // Địa cầu cho file web
                case ".cs":
                case ".js":
                case ".json":
                case ".xml":
                case ".css":
                    return "💻"; // Máy tính cho file code
                case ".exe":
                    return "⚙️"; // Bánh răng cho file thực thi

                // Thêm các định dạng file phổ biến khác
                case ".py":
                    return "🐍"; // Rắn cho file Python
                case ".java":
                    return "☕"; // Tách cà phê cho file Java
                case ".c":
                case ".cpp":
                case ".h":
                    return "🔧"; // Cờ lê cho file C/C++
                case ".php":
                    return "🐘"; // Voi cho file PHP
                case ".sql":
                    return "🗄️"; // Tủ tài liệu cho file SQL
                case ".md":
                    return "📋"; // Bảng ghi chú cho Markdown
                case ".csv":
                    return "📊"; // Biểu đồ cho file CSV
                case ".rtf":
                    return "📄"; // Tài liệu cho file RTF
                case ".log":
                    return "📜"; // Cuộn giấy cho file log
                case ".psd":
                case ".ai":
                    return "🎨"; // Bảng màu cho file thiết kế
                case ".svg":
                    return "🖌️"; // Cọ vẽ cho file SVG
                case ".ttf":
                case ".otf":
                case ".woff":
                    return "🔤"; // Chữ cái cho file font
                case ".eml":
                case ".msg":
                    return "✉️"; // Thư cho file email
                case ".ics":
                    return "📅"; // Lịch cho file calendar
                case ".torrent":
                    return "⬇️"; // Mũi tên xuống cho file torrent
                case ".iso":
                case ".img":
                case ".dmg":
                    return "💿"; // Đĩa CD cho file disk image
                case ".db":
                case ".sqlite":
                    return "🗃️"; // Thẻ chỉ mục cho file database
                case ".bak":
                    return "💾"; // Đĩa mềm cho file backup
                case ".ini":
                case ".cfg":
                    return "⚙️"; // Bánh răng cho file cấu hình
                case ".cer":
                case ".crt":
                case ".pem":
                    return "🔒"; // Khóa cho file certificate
                case ".pkey":
                case ".key":
                    return "🔑"; // Chìa khóa cho file key
                case ".apk":
                    return "📱"; // Điện thoại cho file APK
                case ".dll":
                    return "🧩"; // Mảnh ghép cho file DLL
                case ".bat":
                    return "🦇"; // Dơi cho file BAT
                case ".sh":
                    return "🔧"; // Cờ lê cho file shell
                case ".jar":
                case ".war":
                    return "☕"; // Tách cà phê cho file Java archive
                case ".swf":
                case ".fla":
                    return "🎬"; // Máy quay cho file Flash
                case ".raw":
                case ".cr2":
                case ".nef":
                case ".arw":
                    return "📷"; // Máy ảnh cho file RAW
                case ".dwg":
                case ".dxf":
                    return "📐"; // Thước kẻ cho file CAD
                case ".stl":
                    return "🖨️"; // Máy in 3D cho file STL
                case ".step":
                case ".stp":
                    return "🏗️"; // Công trường xây dựng cho file STEP
                case ".gcode":
                    return "🖨️"; // Máy in 3D cho file G-code

                default:
                    return "🗎"; // Ký hiệu chung cho các file khác
            }
        }
        #endregion

        #region File Explore
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

        #endregion

        #region HTML liệt kê nội dung thư mục
        // Tạo ra một trang HTML liệt kê nội dung thư mục
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
            sb.Append("main{width:75%; margin: 0 auto;}");
            sb.Append("table{border-collapse:collapse; width:95%;}");
            sb.Append("th,td{border:1px solid #ccc; padding:6px; text-align:left;font-size: 15px; font-weight: Regular; font-family: 'Roboto',Segoe UI, Arial, sans-serif;}");
            sb.Append("th{background:#f4f4f4;}");
            sb.Append("tr:nth-child(even){background:#fafafa;}");
            sb.Append("a{text-decoration:none; color:#0366d6;}");
            sb.Append("a:hover{text-decoration:underline;}");
            sb.Append("h2 { font-size: 18px; font-family: 'Roboto',Segoe UI, Arial, sans-serif;}");
            sb.Append("</style>");
            sb.Append("</head>");
            sb.Append("<body>");
            sb.Append("<main>");
            sb.AppendFormat("<h2>Thư mục: {0}</h2>", WebUtility.HtmlEncode(relativePath));

            sb.Append("<table>");
            sb.Append("<tr><th style=\"width: 60%;\">Tên</th><th style=\"width: 20%;\">Ngày sửa đổi</th><th style=\"width: 10%;\">Loại tập tin</th><th style=\"width: 10%;\">Kích thước</th></tr>");

            // Thư mục cha
            if (relativePath != "/")
            {
                string parentRelative = Path.GetDirectoryName(relativePath.TrimEnd(Path.DirectorySeparatorChar, '/'))?.Replace("\\", "/");
                if (string.IsNullOrEmpty(parentRelative)) parentRelative = "/";
                string encodedParent = SafeEncode(parentRelative);

                sb.Append("<tr>");
                sb.AppendFormat("<td colspan=\"4\"><a href=\"{0}\">↩ Quay lại</a></td>", encodedParent);
                //string backIcon = GetIconBase64("..", true); // Lấy icon cho thư mục
                //sb.AppendFormat("<td colspan=\"4\"><a href=\"{0}\"><img src=\"{1}\" style=\"width:16px; height:16px; vertical-align:middle;\" /> Quay lại</a></td>", encodedParent, backIcon);
                sb.Append("</tr>");
            }

            // Danh sách thư mục con
            foreach (var dir in Directory.GetDirectories(currentPath).OrderBy(d => Path.GetFileName(d)))
            {
                string dirName = Path.GetFileName(dir);
                string urlPath = (relativePath.TrimEnd('/') + "/" + dirName).Replace("\\", "/");
                string encodedPath = SafeEncode(urlPath);
                DirectoryInfo di = new DirectoryInfo(dir);

                sb.Append("<tr>");
                //sb.AppendFormat("<td><a href=\"{0}\">📁 {1}</a></td>", encodedPath, WebUtility.HtmlEncode(dirName));
                string folderIcon = GetIconBase64(dir, true);
                sb.AppendFormat("<td><a href=\"{0}\"><img src=\"{1}\" style=\"width:16px; height:16px; vertical-align:middle;\" /> {2}</a></td>", encodedPath, folderIcon, WebUtility.HtmlEncode(dirName));
                sb.AppendFormat("<td>{0}</td>", di.LastWriteTime.ToString("dd/MM/yyyy HH:mm:ss"));
                sb.Append("<td>Thư mục</td>");
                sb.Append("<td>-</td>");               
                sb.Append("</tr>");
            }

            // Danh sách file
            foreach (var file in Directory.GetFiles(currentPath).OrderBy(f => Path.GetFileName(f)))
            {
                string fileName = Path.GetFileName(file);
                string urlPath = (relativePath.TrimEnd('/') + "/" + fileName).Replace("\\", "/");
                string encodedPath = SafeEncode(urlPath);
                FileInfo fi = new FileInfo(file);
                string sizeStr = FormatFileSize(fi.Length);
                string extension = Path.GetExtension(file).ToLower();

                sb.Append("<tr>");
                string fileIcon = GetIconBase64(file, false);
                sb.AppendFormat("<td><a href=\"{0}\"><img src=\"{1}\" style=\"width:16px; height:16px; vertical-align:middle;\" /> {2}</a></td>", encodedPath, fileIcon, WebUtility.HtmlEncode(fileName));
                sb.AppendFormat("<td>{0}</td>", fi.LastWriteTime.ToString("dd/MM/yyyy HH:mm:ss"));
                sb.AppendFormat("<td>{0}</td>", extension);
                sb.AppendFormat("<td>{0}</td>", sizeStr);
                sb.Append("</tr>");
            }

            sb.Append("</table>");
            sb.Append("</main>");
            sb.Append("</body></html>");
            return sb.ToString();
        }
        #endregion

        #region Trang hiển thị upload tập tin
        //Trang hiển thị upload tập tin
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
            sb.Append(".upload-subtext { font-size: 14px; color: #666; margin-top: 5px; }");
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
        //Trang phan hoi upload thanh cong
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
            sb.Append("h3 { font-size: 16px; margin-bottom: 10px; }"); // ← Thêm cỡ chữ cho tiêu đề h3
            sb.Append("ul { list-style-type: none; padding: 0; }");
            sb.Append("li { padding: 10px; margin-bottom: 5px; border-radius: 5px; font-size: 14px; }"); // ← Thêm cỡ chữ 14px cho danh sách file
            sb.Append(".success { background: #d4edda; color: #155724; }");
            sb.Append(".error { background: #f8d7da; color: #721c24; }");
            sb.Append(".button-group { text-align: center; margin-top: 20px; }");
            sb.Append(".button { display: inline-block; padding: 8px 8px; margin: 0 20px; border-radius: 4px; text-decoration: none; font-size: 14px; font-weight: Reguler; cursor: pointer; transition: background-color 0.3s; font-family: 'Segoe UI', Arial, sans-serif; }");
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
                sb.Append("<h3>𓆰 Tập tin đã tải lên thành công:</h3>");
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

        #endregion

        #region Upload file to sever
        //Phương thức xử lý việc tải file lên máy chủ.
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
                            32768,
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

        #endregion
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