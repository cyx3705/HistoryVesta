namespace b_Code_三维坐标机器人前端设计
{
    public partial class Form1 : Form
    {
        private readonly PathPlanner _planner = new();
        private readonly CommandParser _parser;
        private readonly RobotDemoRunner _demoRunner;

        private const string ExampleScript =
            """
            reset
            add_point 0 0 0
            add_point 100 50 30
            add_point 200 100 60
            interpolate spline 50
            generate_points 2.0
            convert_to_freq 20
            """;

        public Form1()
        {
            InitializeComponent();
            _parser = new CommandParser(_planner);
            _demoRunner = new RobotDemoRunner(_planner);

            consoleControl.CommandExecuted += OnCommandExecuted;
            graphicalPanel.CommandRequested += OnGraphicalCommand;
            graphicalPanel.RunExampleRequested += RunExample;
            dataProcessingPanel.CommandRequested += OnDataProcessingCommand;
            simulationDemoPanel.Bind(_demoRunner);
            simulationDemoPanel.SceneRefreshRequested += RefreshAll;
            simulationDemoPanel.LogProduced += msg => consoleControl.WriteSystemLine(msg);
        }

        private void Form1_Load(object? sender, EventArgs e)
        {
            int available = mainSplit.Height - mainSplit.SplitterWidth;
            int bottomHeight = Math.Max(mainSplit.Panel2MinSize, (int)(available * 0.34));
            mainSplit.SplitterDistance = Math.Max(mainSplit.Panel1MinSize, available - bottomHeight);

            int topWidth = topSplit.Width - topSplit.SplitterWidth;
            topSplit.SplitterDistance = Math.Max(topSplit.Panel1MinSize, (int)(topWidth * 0.58));

            RefreshAll();
        }

        private string OnCommandExecuted(string command)
        {
            string result = _parser.Execute(command);
            RefreshAll();
            return result;
        }

        private void OnGraphicalCommand(string command) => ExecuteFromPanel("Graphical", command);

        private void OnDataProcessingCommand(string command) => ExecuteFromPanel("DataProcessing", command);

        private void ExecuteFromPanel(string source, string command)
        {
            consoleControl.WriteSystemLine($"[{source}] {command}");
            string result = _parser.Execute(command);
            consoleControl.AppendExecutedCommand(command, result);
            RefreshAll();
        }

        private void RunExample()
        {
            consoleControl.WriteSystemLine("[Graphical] Run built-in example");
            consoleControl.RunScript(ExampleScript);
            RefreshAll();
        }

        private void RefreshAll()
        {
            viewer3D.UpdateScene(
                _planner.ControlPoints,
                _planner.InterpolatedPoints,
                _planner.SampledPoints,
                _planner.CurrentPosition);
            pathDataTablePanel.Bind(_planner);
        }

        private void pathDataTablePanel_Load(object sender, EventArgs e)
        {
        }
    }
}
