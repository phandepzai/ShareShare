namespace ShareFile
{
    partial class MainForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        //private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Hủy đăng ký sự kiện trước khi dispose
                if (notifyIcon != null)
                {
                    notifyIcon.DoubleClick -= notifyIcon_DoubleClick;
                    notifyIcon.Dispose();
                }

                if (components != null)
                {
                    components.Dispose();
                }
            }
            base.Dispose(disposing); // Luôn gọi base
        }
    }
}

