using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DrawPointMethod
{
    public partial class Form1 : Form
    {
        // 用于存储需要绘制的点（避免重绘时消失）
        private List<Point> points = new List<Point>();

        public Form1()
        {
            InitializeComponent();
            this.Size = new Size(500, 400);
            this.Text = "WinForms 画点方法示例";
            this.Paint += Form1_Paint;

            // 示例：初始化时画几个点
            DrawPoint(100, 100);  // 在(100,100)画点
            DrawPoint(200, 150);  // 在(200,150)画点
            DrawPoint(300, 200);  // 在(300,200)画点
        }

        /// <summary>
        /// 画点方法：输入x、y坐标，在对应位置画点
        /// </summary>
        /// <param name="x">点的X坐标</param>
        /// <param name="y">点的Y坐标</param>
        public void DrawPoint(int x, int y)
        {
            // 保存点坐标（用于重绘）
            points.Add(new Point(x, y));
            // 触发窗口重绘（立即显示新点）
            this.Invalidate();
        }

        // 窗口绘制事件：重绘所有保存的点
        private void Form1_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // 遍历所有点，逐个绘制
            foreach (var point in points)
            {
                // 画2x2像素的点（比1x1更清晰），颜色为红色
                g.FillRectangle(Brushes.Red, point.X, point.Y, 2, 2);
            }
        }

        // 程序入口
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }
    }
}

