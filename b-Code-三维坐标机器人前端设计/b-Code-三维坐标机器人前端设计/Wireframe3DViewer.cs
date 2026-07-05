using b_Code_三维坐标机器人前端设计.Models;

namespace b_Code_三维坐标机器人前端设计;

public sealed class Wireframe3DViewer : Control
{
    private IReadOnlyList<Point3D> _controlPoints = [];
    private IReadOnlyList<Point3D> _interpolatedPoints = [];
    private IReadOnlyList<Point3D> _sampledPoints = [];
    private Point3D _currentPosition = new(0, 0, 0);

    private float _yaw = 0.6f;
    private float _pitch = -0.35f;
    private float _zoom = 1.0f;
    private Point _lastMouse;
    private bool _dragging;

    public Wireframe3DViewer()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 28, 36);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public void UpdateScene(
        IReadOnlyList<Point3D> controlPoints,
        IReadOnlyList<Point3D> interpolatedPoints,
        IReadOnlyList<Point3D> sampledPoints,
        Point3D currentPosition)
    {
        _controlPoints = controlPoints;
        _interpolatedPoints = interpolatedPoints;
        _sampledPoints = sampledPoints;
        _currentPosition = currentPosition;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _lastMouse = e.Location;
            Capture = true;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = false;
            Capture = false;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;

        int dx = e.X - _lastMouse.X;
        int dy = e.Y - _lastMouse.Y;
        _yaw += dx * 0.01f;
        _pitch += dy * 0.01f;
        _pitch = Math.Clamp(_pitch, -1.4f, 1.4f);
        _lastMouse = e.Location;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _zoom *= e.Delta > 0 ? 1.1f : 0.9f;
        _zoom = Math.Clamp(_zoom, 0.2f, 8f);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        DrawAxes(g);
        DrawPolyline(g, _interpolatedPoints, Color.FromArgb(80, 160, 255), 2f);
        DrawPolyline(g, _sampledPoints, Color.FromArgb(255, 200, 60), 1.5f);
        DrawPoints(g, _controlPoints, Color.FromArgb(255, 90, 90), 5f);
        DrawPoints(g, _sampledPoints, Color.FromArgb(255, 220, 80), 3f);
        DrawRobotMarker(g, _currentPosition);

        using var font = new Font("Segoe UI", 9f);
        using var brush = new SolidBrush(Color.FromArgb(200, 220, 230));
        g.DrawString("左键拖动旋转 | 滚轮缩放", font, brush, 8, 8);
        g.DrawString($"位置: {_currentPosition}", font, brush, 8, Height - 24);
    }

    private void DrawAxes(Graphics g)
    {
        var origin = Project(0, 0, 0);
        var xEnd = Project(50, 0, 0);
        var yEnd = Project(0, 50, 0);
        var zEnd = Project(0, 0, 50);

        DrawLine(g, origin, xEnd, Color.FromArgb(220, 80, 80), 2f);
        DrawLine(g, origin, yEnd, Color.FromArgb(80, 220, 80), 2f);
        DrawLine(g, origin, zEnd, Color.FromArgb(80, 140, 255), 2f);

        using var font = new Font("Segoe UI", 8f, FontStyle.Bold);
        g.DrawString("X", font, Brushes.IndianRed, xEnd.X + 4, xEnd.Y);
        g.DrawString("Y", font, Brushes.LightGreen, yEnd.X + 4, yEnd.Y);
        g.DrawString("Z", font, Brushes.LightSkyBlue, zEnd.X + 4, zEnd.Y);
    }

    private void DrawPolyline(Graphics g, IReadOnlyList<Point3D> points, Color color, float width)
    {
        if (points.Count < 2)
            return;

        using var pen = new Pen(color, width);
        for (int i = 0; i < points.Count - 1; i++)
        {
            PointF a = Project(points[i].X, points[i].Y, points[i].Z);
            PointF b = Project(points[i + 1].X, points[i + 1].Y, points[i + 1].Z);
            g.DrawLine(pen, a, b);
        }
    }

    private void DrawPoints(Graphics g, IReadOnlyList<Point3D> points, Color color, float size)
    {
        foreach (Point3D point in points)
            DrawPoint(g, point, color, size);
    }

    private void DrawPoint(Graphics g, Point3D point, Color color, float size)
    {
        PointF p = Project(point.X, point.Y, point.Z);
        float r = size * 0.5f;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, p.X - r, p.Y - r, size, size);
    }

    private void DrawRobotMarker(Graphics g, Point3D point)
    {
        PointF p = Project(point.X, point.Y, point.Z);
        const float size = 12f;
        float r = size * 0.5f;
        using var glow = new SolidBrush(Color.FromArgb(60, 255, 60, 60));
        g.FillEllipse(glow, p.X - r - 4, p.Y - r - 4, size + 8, size + 8);
        using var brush = new SolidBrush(Color.FromArgb(255, 50, 50));
        g.FillEllipse(brush, p.X - r, p.Y - r, size, size);
    }

    private void DrawLine(Graphics g, PointF a, PointF b, Color color, float width)
    {
        using var pen = new Pen(color, width);
        g.DrawLine(pen, a, b);
    }

    private PointF Project(double x, double y, double z)
    {
        float cosY = MathF.Cos(_yaw);
        float sinY = MathF.Sin(_yaw);
        float cosP = MathF.Cos(_pitch);
        float sinP = MathF.Sin(_pitch);

        float rx = (float)(x * cosY + z * sinY);
        float rz = (float)(-x * sinY + z * cosY);
        float ry = (float)y;

        float ry2 = ry * cosP - rz * sinP;
        float rz2 = ry * sinP + rz * cosP;

        float scale = Math.Min(Width, Height) * 0.004f * _zoom;
        float cx = Width * 0.5f;
        float cy = Height * 0.55f;
        return new PointF(cx + rx * scale, cy - ry2 * scale);
    }
}