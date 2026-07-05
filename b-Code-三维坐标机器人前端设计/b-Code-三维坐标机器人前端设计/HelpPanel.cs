namespace b_Code_三维坐标机器人前端设计;

public sealed class HelpPanel : UserControl
{
    private const string HelpText =
        """
        【项目概述】
        本程序为宁波工程学院直角坐标机器人课程设计的桌面端路径规划前端。
        当前阶段：插值 → 线段长度表 → 等距采样 → 位置差→脉冲频率。

        【界面说明】
        · 上方左侧 3D 视图：左键拖动旋转，滚轮缩放
          - 红色点：控制点  蓝色线：插值曲线  黄色线：等距采样路径
        · 上方右侧数据表格（三个子页）：
          - 插值线段：相邻插值点之间的线段长度与分量
          - 等距小线段：等距采样后的 Δx/Δy/Δz/L
          - 脉冲频率：位置差转换后的 Fx/Fy/Fz
        · 控制台：CMD 风格，Enter 执行
        · 图形操作：按钮式路径规划
        · 数据处理：批处理指令（采样 + 频率转换）

        【路径规划指令】
        reset / home / stop
        add_point x y z
        clear_points
        interpolate type steps    (linear / bezier / spline)

        【数据处理 / 批处理指令】
        generate_points distance  等距采样，计算小线段分量
        convert_to_freq speed     位置差 → 三轴脉冲频率 (Hz)
        process_all distance speed一键批处理：采样 + 频率转换
        set_freq_ratio x y z      设置 X/Y/Z 频率比例系数（>0）
        set_freq_ratio reset      频率比例恢复默认 1,1,1
        show_freq_ratio           查看当前频率比例
        clear_freq                清空频率数据
        show_tables               刷新表格概览

        【推荐流程】
        reset
        add_point 0 0 0
        add_point 100 50 30
        add_point 200 100 60
        interpolate spline 50
        generate_points 2.0
        convert_to_freq 20

        【频率转换原理】
        1. 分量速度：Vx = V×(Δx/L)，Vy = V×(Δy/L)，Vz = V×(Δz/L)
        2. 转速：X轴 Vx/95，Y/Z轴 Vy或Vz/5  (r/s)
        3. 频率：转速 × 800 脉冲/转  (Hz)
        4. 轴比例：Fx/Fy/Fz 分别乘以 ratioX/ratioY/ratioZ（默认 1,1,1）
        """;

    public HelpPanel()
    {
        var text = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 30, 30),
            Font = new Font("Microsoft YaHei UI", 10F),
            BorderStyle = BorderStyle.None,
            Text = HelpText
        };
        Controls.Add(text);
    }
}
