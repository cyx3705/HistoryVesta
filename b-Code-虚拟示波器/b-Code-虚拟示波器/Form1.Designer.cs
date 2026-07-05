namespace b_Code_虚拟示波器
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            pnlToolbar = new Panel();
            lblPort = new Label();
            cmbPorts = new ComboBox();
            btnRefreshPorts = new Button();
            btnConnect = new Button();
            btnDisconnect = new Button();
            btnDemo = new Button();
            lblStatus = new Label();
            lblFrequency = new Label();
            lblInfo = new Label();
            pnlScope = new Panel();
            timerRefresh = new System.Windows.Forms.Timer(components);
            pnlToolbar.SuspendLayout();
            SuspendLayout();
            // 
            // pnlToolbar
            // 
            pnlToolbar.Controls.Add(lblPort);
            pnlToolbar.Controls.Add(cmbPorts);
            pnlToolbar.Controls.Add(btnRefreshPorts);
            pnlToolbar.Controls.Add(btnConnect);
            pnlToolbar.Controls.Add(btnDisconnect);
            pnlToolbar.Controls.Add(btnDemo);
            pnlToolbar.Controls.Add(lblStatus);
            pnlToolbar.Controls.Add(lblFrequency);
            pnlToolbar.Controls.Add(lblInfo);
            pnlToolbar.Dock = DockStyle.Top;
            pnlToolbar.Location = new Point(0, 0);
            pnlToolbar.Name = "pnlToolbar";
            pnlToolbar.Padding = new Padding(8);
            pnlToolbar.Size = new Size(1000, 72);
            pnlToolbar.TabIndex = 0;
            // 
            // lblPort
            // 
            lblPort.AutoSize = true;
            lblPort.Location = new Point(11, 14);
            lblPort.Name = "lblPort";
            lblPort.Size = new Size(44, 17);
            lblPort.TabIndex = 0;
            lblPort.Text = "串口：";
            // 
            // cmbPorts
            // 
            cmbPorts.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPorts.Location = new Point(61, 10);
            cmbPorts.Name = "cmbPorts";
            cmbPorts.Size = new Size(120, 25);
            cmbPorts.TabIndex = 1;
            // 
            // btnRefreshPorts
            // 
            btnRefreshPorts.Location = new Point(187, 9);
            btnRefreshPorts.Name = "btnRefreshPorts";
            btnRefreshPorts.Size = new Size(75, 27);
            btnRefreshPorts.TabIndex = 2;
            btnRefreshPorts.Text = "刷新";
            btnRefreshPorts.UseVisualStyleBackColor = true;
            btnRefreshPorts.Click += BtnRefreshPorts_Click;
            // 
            // btnConnect
            // 
            btnConnect.Location = new Point(268, 9);
            btnConnect.Name = "btnConnect";
            btnConnect.Size = new Size(75, 27);
            btnConnect.TabIndex = 3;
            btnConnect.Text = "连接";
            btnConnect.UseVisualStyleBackColor = true;
            btnConnect.Click += BtnConnect_Click;
            // 
            // btnDisconnect
            // 
            btnDisconnect.Enabled = false;
            btnDisconnect.Location = new Point(349, 9);
            btnDisconnect.Name = "btnDisconnect";
            btnDisconnect.Size = new Size(75, 27);
            btnDisconnect.TabIndex = 4;
            btnDisconnect.Text = "断开";
            btnDisconnect.UseVisualStyleBackColor = true;
            btnDisconnect.Click += BtnDisconnect_Click;
            // 
            // btnDemo
            // 
            btnDemo.Location = new Point(430, 9);
            btnDemo.Name = "btnDemo";
            btnDemo.Size = new Size(100, 27);
            btnDemo.TabIndex = 5;
            btnDemo.Text = "演示模式";
            btnDemo.UseVisualStyleBackColor = true;
            btnDemo.Click += BtnDemo_Click;
            // 
            // lblStatus
            // 
            lblStatus.AutoSize = true;
            lblStatus.ForeColor = Color.DimGray;
            lblStatus.Location = new Point(11, 44);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(68, 17);
            lblStatus.TabIndex = 6;
            lblStatus.Text = "状态：未连接";
            // 
            // lblFrequency
            // 
            lblFrequency.AutoSize = true;
            lblFrequency.ForeColor = Color.DarkGreen;
            lblFrequency.Location = new Point(280, 44);
            lblFrequency.Name = "lblFrequency";
            lblFrequency.Size = new Size(99, 17);
            lblFrequency.TabIndex = 7;
            lblFrequency.Text = "频率：-- Hz";
            // 
            // lblInfo
            // 
            lblInfo.AutoSize = true;
            lblInfo.ForeColor = Color.DimGray;
            lblInfo.Location = new Point(520, 44);
            lblInfo.Name = "lblInfo";
            lblInfo.Size = new Size(320, 17);
            lblInfo.TabIndex = 8;
            lblInfo.Text = "监测 P55 方波 | 时基 500ms/div | 115200 | H=高 L=低";
            // 
            // pnlScope
            // 
            pnlScope.BackColor = Color.FromArgb(10, 14, 20);
            pnlScope.Dock = DockStyle.Fill;
            pnlScope.Location = new Point(0, 72);
            pnlScope.Name = "pnlScope";
            pnlScope.Size = new Size(1000, 528);
            pnlScope.TabIndex = 1;
            pnlScope.Paint += PnlScope_Paint;
            pnlScope.Resize += PnlScope_Resize;
            // 
            // timerRefresh
            // 
            timerRefresh.Interval = 33;
            timerRefresh.Tick += TimerRefresh_Tick;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1000, 600);
            Controls.Add(pnlScope);
            Controls.Add(pnlToolbar);
            MinimumSize = new Size(800, 500);
            Name = "Form1";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "虚拟示波器 - P55 方波监测";
            FormClosing += Form1_FormClosing;
            Load += Form1_Load;
            pnlToolbar.ResumeLayout(false);
            pnlToolbar.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private Panel pnlToolbar;
        private Label lblPort;
        private ComboBox cmbPorts;
        private Button btnRefreshPorts;
        private Button btnConnect;
        private Button btnDisconnect;
        private Button btnDemo;
        private Label lblStatus;
        private Label lblFrequency;
        private Label lblInfo;
        private Panel pnlScope;
        private System.Windows.Forms.Timer timerRefresh;
    }
}