using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Policy;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;



namespace w2
{
    //internal static class Program
    //{
    //    /// <summary>
    //    /// 应用程序的主入口点。
    //    /// </summary>
    //    [STAThread]
    //    //static void Main()
    //    //{
    //    //    Application.EnableVisualStyles();
    //    //    Application.SetCompatibleTextRenderingDefault(false);
    //    //    Application.Run(new Form1());
    //    //}
    //}
}
namespace DrawPointMethod
{
    public class xy
    {
        public static int ax = 200;
        public static int ay = 800;
        public static double sita = 30;
        public static int b = 300;
        public static int c = 250;
        public static int d = 200;
        public static int e = 150;
        public static int f1 = 0;
        public static double f2 = 0;
        public static int ex;
        public static int ey;
        public static int blockWidth = 50;
        public const int AngleStep = 1;
        public static double lineLength = b + c + d + e + 3 * sita * blockWidth * 2 * Math.PI / 360;
        public static List<Point> points = new List<Point>();
        public static List<string> pointLabels = new List<string> { "a", "b", "c", "d", "e" };
        public static int zhi = 1; 
    }
    public class write
    {
        public static void DrawPoint(int x, int y)
        {
            // 保存点坐标（用于重绘）
            xy.points.Add(new Point(x, y));
        }
    }
    public partial class Form1 : Form
    {
        // 用于存储需要绘制的点（避免重绘时消失）
        private List<Point> points = new List<Point>();

        public Form1()
        {
            this.Size = new Size(1000, 1000);
            this.Text = "WinForms 图像 - 上下键控制角度";
            this.Paint += Form1_Paint;
            this.KeyDown += Form1_KeyDown; // 绑定键盘按下事件
            this.KeyPreview = true; // 确保窗体优先捕获键盘事件
            this.DoubleBuffered = true;

            // 初始化绘制
            UpdatePointsAndRedraw();

        }
        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up:
                    xy.sita += xy.AngleStep; // 上键：角度增加（逆时针旋转）
                    xy.lineLength=xy.b + xy.c + xy.d + xy.e + 3 * xy.sita * xy.blockWidth * 2 * Math.PI / 360;
                    xy.f2 = xy.f1 * (Math.Sin((3 * xy.sita * Math.PI / 180) + Math.PI / 2));
                    break;
                case Keys.Down:
                    xy.sita -= xy.AngleStep; // 下键：角度减少（顺时针旋转）
                    xy.lineLength = xy.b + xy.c + xy.d + xy.e + 3 * xy.sita * xy.blockWidth * 2 * Math.PI / 360;
                    xy.f2 = xy.f1 * (Math.Sin((3 * xy.sita * Math.PI / 180) + Math.PI / 2));
                    break;
                case Keys.W:
                    xy.lineLength += xy.AngleStep; //
                    xy.sita = (xy.lineLength-(xy.b + xy.c + xy.d + xy.e))*360/(3* xy.blockWidth * 2 * Math.PI);
                    xy.f2 = xy.f1 * (Math.Sin((3 * xy.sita * Math.PI / 180) + Math.PI / 2));
                    break;
                case Keys.S:
                    xy.lineLength -= xy.AngleStep; //
                    xy.sita = (xy.lineLength - (xy.b + xy.c + xy.d + xy.e)) * 360 / (3* xy.blockWidth * 2 * Math.PI);
                    xy.f2 = xy.f1 * (Math.Sin((3 * xy.sita * Math.PI / 180) + Math.PI / 2));
                    break;
                case Keys.D1:
                    xy.b = 85*4;
                    xy.c = 37*4;
                    xy.d = 38 * 4;
                    xy.e = 34 * 4;
                    break;
                case Keys.D2:
                    xy.b = 110 * 4;
                    xy.c = 42 * 4;
                    xy.d = 38 * 4;
                    xy.e = 34 * 4;
                    break;
                case Keys.D3:
                    xy.b = 120 * 4;
                    xy.c = 47 * 4;
                    xy.d = 38 * 4;
                    xy.e = 34 * 4;
                    break;
                case Keys.D4:
                    xy.b = 110 * 4;
                    xy.c = 42 * 4;
                    xy.d = 38 * 4;
                    xy.e = 34 * 4;
                    break;
                case Keys.D5:
                    xy.b = 65 * 4;
                    xy.c = 43 * 4;
                    xy.d = 34 * 4;  
                    xy.e = 1 * 4;
                    xy.zhi = 5;
                    break;
                case Keys.J:
                    xy.f1 += 1;
                    xy.f2 = xy.f1*(Math.Sin((3*xy.sita * Math.PI / 180)+Math.PI/2));
                    break;
                case Keys.K:
                    xy.f1 -= 1;
                    xy.f2 = xy.f1 * (Math.Sin((3 * xy.sita * Math.PI / 180) + Math.PI / 2));
                    break;
                default:
                    return; // 其他按键不处理
            }

            // 限制角度范围（可选，避免过度旋转导致图形超出窗口）
            xy.sita = Math.Max(0, Math.Min(80, xy.sita));

            // 重新计算坐标并绘制
            UpdatePointsAndRedraw();

        }
        private void UpdatePointsAndRedraw()
        {
            double sitaRad = xy.sita * Math.PI / 180;

            // 计算各点坐标（保留double精度）
            double bx = xy.ax;
            double by = xy.ay - xy.b;
            double cxDouble = bx + xy.c * Math.Sin(sitaRad);
            double cyDouble = by - xy.c * Math.Cos(sitaRad);
            double dxDouble = cxDouble + xy.d * Math.Sin(sitaRad * 2);
            double dyDouble = cyDouble - xy.d * Math.Cos(sitaRad * 2);


            // 转为int坐标
            int bxInt = (int)Math.Round(bx);
            int byInt = (int)Math.Round(by);
            int cxInt = (int)Math.Round(cxDouble);
            int cyInt = (int)Math.Round(cyDouble);
            int dxInt = (int)Math.Round(dxDouble);
            int dyInt = (int)Math.Round(dyDouble);



            // 清空旧点并添加新点
            xy.points.Clear();
            write.DrawPoint(xy.ax, xy.ay);
            write.DrawPoint(bxInt, byInt);
            write.DrawPoint(cxInt, cyInt);
            write.DrawPoint(dxInt, dyInt);
            if (xy.zhi != 5)
            {
                double exDouble = dxDouble + xy.e * Math.Sin(sitaRad * 3);
                double eyDouble = dyDouble - xy.e * Math.Cos(sitaRad * 3);
                xy.ex = (int)Math.Round(exDouble);
                xy.ey = (int)Math.Round(eyDouble);
                int exInt = (int)Math.Round(exDouble);
                int eyInt = (int)Math.Round(eyDouble);
                write.DrawPoint(exInt, eyInt);
            }
            else
            {
                double exDouble = dxDouble;
                double eyDouble = dyDouble;
                xy.ex = (int)Math.Round(exDouble);
                xy.ey = (int)Math.Round(eyDouble);
                int exInt = (int)Math.Round(exDouble);
                int eyInt = (int)Math.Round(eyDouble);
                write.DrawPoint(exInt, eyInt);
            }
            // 触发重绘
            this.Invalidate();
        }

        // 窗口绘制事件：重绘所有保存的点
        private void Form1_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 绘制块（以每条线段为右侧边）
            if (xy.points.Count >= 2)
            {
                using (Pen pen = new Pen(Color.Black, 1)) // 边框笔
                using (Brush brush = new SolidBrush(Color.LightBlue)) // 填充色
                {
                    if(xy.zhi!=5)
                    {
                        for (int i = 0; i < xy.points.Count - 1; i++)
                        {
                            Point p1 = xy.points[i];   // 线段起点（右侧边的起点）
                            Point p2 = xy.points[i + 1]; // 线段终点（右侧边的终点）

                            // 1. 计算线段的方向向量
                            int dx = p2.X - p1.X;
                            int dy = p2.Y - p1.Y;

                            // 2. 计算垂直于线段的单位向量（向左的方向，用于确定矩形左侧边）
                            double length = Math.Sqrt(dx * dx + dy * dy); // 线段长度
                            double unitX = -dy / length; // X分量（单位向量）
                            double unitY = dx / length;  // Y分量（单位向量）

                            // 3. 计算矩形左侧边的两个点（右侧边向左偏移blockWidth距离）
                            Point leftP1 = new Point(
                                (int)(p1.X - unitX * xy.blockWidth),
                                (int)(p1.Y - unitY * xy.blockWidth)
                            );
                            Point leftP2 = new Point(
                                (int)(p2.X - unitX * xy.blockWidth),
                                (int)(p2.Y - unitY * xy.blockWidth)
                            );

                            // 4. 定义矩形的4个顶点（顺时针顺序）
                            Point[] rectanglePoints = {
                            leftP1,   // 左侧边起点
                            leftP2,   // 左侧边终点
                            p2,       // 右侧边终点（原线段终点）
                            p1        // 右侧边起点（原线段起点）
                        };

                            // 5. 绘制矩形（填充+边框）
                            g.FillPolygon(brush, rectanglePoints); // 填充块
                            g.DrawPolygon(pen, rectanglePoints);   // 绘制边框
                        }
                    }
                    else
                    {
                        for (int i = 0; i < xy.points.Count - 2; i++)
                        {
                            Point p1 = xy.points[i];   // 线段起点（右侧边的起点）
                            Point p2 = xy.points[i + 1]; // 线段终点（右侧边的终点）

                            // 1. 计算线段的方向向量
                            int dx = p2.X - p1.X;
                            int dy = p2.Y - p1.Y;

                            // 2. 计算垂直于线段的单位向量（向左的方向，用于确定矩形左侧边）
                            double length = Math.Sqrt(dx * dx + dy * dy); // 线段长度
                            double unitX = -dy / length; // X分量（单位向量）
                            double unitY = dx / length;  // Y分量（单位向量）

                            // 3. 计算矩形左侧边的两个点（右侧边向左偏移blockWidth距离）
                            Point leftP1 = new Point(
                                (int)(p1.X - unitX * xy.blockWidth),
                                (int)(p1.Y - unitY * xy.blockWidth)
                            );
                            Point leftP2 = new Point(
                                (int)(p2.X - unitX * xy.blockWidth),
                                (int)(p2.Y - unitY * xy.blockWidth)
                            );

                            // 4. 定义矩形的4个顶点（顺时针顺序）
                            Point[] rectanglePoints = {
                            leftP1,   // 左侧边起点
                            leftP2,   // 左侧边终点
                            p2,       // 右侧边终点（原线段终点）
                            p1        // 右侧边起点（原线段起点）
                        };

                            // 5. 绘制矩形（填充+边框）
                            g.FillPolygon(brush, rectanglePoints); // 填充块
                            g.DrawPolygon(pen, rectanglePoints);   // 绘制边框
                        }
                    }
                }
            }
            // 绘制自定义文字（整合到Paint事件中，避免消失）
            string title = "手套的一些参数";
            using (Font titleFont = new Font("微软雅黑", 16, FontStyle.Bold))
            {
                g.DrawString(title, titleFont, Brushes.Black, 30, 30);
            }

            string angleText1 = $"当前角度：{xy.sita:F1}°";
            g.DrawString(angleText1, new Font("Arial", 12), Brushes.Red, 30, 60);

            string angleText3 = $"当前受力：{xy.f1:F1}N";
            g.DrawString(angleText3, new Font("Arial", 12), Brushes.Red, xy.ex, xy.ey);

            string angleText2 = $"当前线长：{xy.lineLength/4:F1}";
            g.DrawString(angleText2, new Font("Arial", 12), Brushes.Red, 30, 80);

            string angleText4 = $"等效拉力：{xy.f2:F1}";
            g.DrawString(angleText4, new Font("Arial", 12), Brushes.Red, 30, 100);
            // 绘制原始点和标签
            for (int i = 0; i < xy.points.Count; i++)
            {
                Point point = xy.points[i];
                string label = xy.pointLabels[i];
                g.FillEllipse(Brushes.Red, point.X - 3, point.Y - 3, 6, 6);
                g.DrawString(label, new Font("Arial", 10), Brushes.Black, point.X + 5, point.Y + 5);
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