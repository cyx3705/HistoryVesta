namespace b_Code_三维坐标机器人前端设计
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
            mainSplit = new SplitContainer();
            topSplit = new SplitContainer();
            viewer3D = new Wireframe3DViewer();
            rightTabs = new TabControl();
            tabTables = new TabPage();
            pathDataTablePanel = new PathDataTablePanel();
            tabGraphical = new TabPage();
            graphicalPanel = new GraphicalPanel();
            tabDataProcessing = new TabPage();
            dataProcessingPanel = new DataProcessingPanel();
            tabSimulation = new TabPage();
            simulationDemoPanel = new SimulationDemoPanel();
            tabHelp = new TabPage();
            helpPanel = new HelpPanel();
            consoleControl = new ConsoleControl();
            ((System.ComponentModel.ISupportInitialize)mainSplit).BeginInit();
            mainSplit.Panel1.SuspendLayout();
            mainSplit.Panel2.SuspendLayout();
            mainSplit.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)topSplit).BeginInit();
            topSplit.Panel1.SuspendLayout();
            topSplit.Panel2.SuspendLayout();
            topSplit.SuspendLayout();
            rightTabs.SuspendLayout();
            tabTables.SuspendLayout();
            tabGraphical.SuspendLayout();
            tabDataProcessing.SuspendLayout();
            tabSimulation.SuspendLayout();
            tabHelp.SuspendLayout();
            SuspendLayout();
            // 
            // mainSplit
            // 
            mainSplit.Dock = DockStyle.Fill;
            mainSplit.Location = new Point(0, 0);
            mainSplit.Name = "mainSplit";
            mainSplit.Orientation = Orientation.Horizontal;
            // 
            // mainSplit.Panel1
            // 
            mainSplit.Panel1.Controls.Add(topSplit);
            mainSplit.Panel1MinSize = 280;
            // 
            // mainSplit.Panel2
            // 
            mainSplit.Panel2.Controls.Add(consoleControl);
            mainSplit.Panel2MinSize = 180;
            mainSplit.Size = new Size(1886, 1129);
            mainSplit.SplitterDistance = 733;
            mainSplit.TabIndex = 0;
            // 
            // topSplit
            // 
            topSplit.Dock = DockStyle.Fill;
            topSplit.Location = new Point(0, 0);
            topSplit.Name = "topSplit";
            // 
            // topSplit.Panel1
            // 
            topSplit.Panel1.Controls.Add(viewer3D);
            topSplit.Panel1MinSize = 300;
            // 
            // topSplit.Panel2
            // 
            topSplit.Panel2.Controls.Add(rightTabs);
            topSplit.Panel2MinSize = 320;
            topSplit.Size = new Size(1886, 733);
            topSplit.SplitterDistance = 1131;
            topSplit.TabIndex = 0;
            // 
            // viewer3D
            // 
            viewer3D.BackColor = Color.FromArgb(24, 28, 36);
            viewer3D.Dock = DockStyle.Fill;
            viewer3D.Location = new Point(0, 0);
            viewer3D.Name = "viewer3D";
            viewer3D.Size = new Size(1131, 733);
            viewer3D.TabIndex = 0;
            // 
            // rightTabs
            // 
            rightTabs.Controls.Add(tabTables);
            rightTabs.Controls.Add(tabGraphical);
            rightTabs.Controls.Add(tabDataProcessing);
            rightTabs.Controls.Add(tabSimulation);
            rightTabs.Controls.Add(tabHelp);
            rightTabs.Dock = DockStyle.Fill;
            rightTabs.Location = new Point(0, 0);
            rightTabs.Name = "rightTabs";
            rightTabs.SelectedIndex = 0;
            rightTabs.Size = new Size(751, 733);
            rightTabs.TabIndex = 0;
            // 
            // tabTables
            // 
            tabTables.Controls.Add(pathDataTablePanel);
            tabTables.Location = new Point(4, 33);
            tabTables.Name = "tabTables";
            tabTables.Padding = new Padding(4);
            tabTables.Size = new Size(743, 696);
            tabTables.TabIndex = 0;
            tabTables.Text = "Tables";
            tabTables.UseVisualStyleBackColor = true;
            // 
            // pathDataTablePanel
            // 
            pathDataTablePanel.Dock = DockStyle.Fill;
            pathDataTablePanel.Location = new Point(4, 4);
            pathDataTablePanel.Name = "pathDataTablePanel";
            pathDataTablePanel.Size = new Size(735, 688);
            pathDataTablePanel.TabIndex = 0;
            pathDataTablePanel.Load += pathDataTablePanel_Load;
            // 
            // tabGraphical
            // 
            tabGraphical.Controls.Add(graphicalPanel);
            tabGraphical.Location = new Point(4, 33);
            tabGraphical.Name = "tabGraphical";
            tabGraphical.Padding = new Padding(4);
            tabGraphical.Size = new Size(743, 696);
            tabGraphical.TabIndex = 1;
            tabGraphical.Text = "Graphical";
            tabGraphical.UseVisualStyleBackColor = true;
            // 
            // graphicalPanel
            // 
            graphicalPanel.AutoScroll = true;
            graphicalPanel.Dock = DockStyle.Fill;
            graphicalPanel.Location = new Point(4, 4);
            graphicalPanel.Name = "graphicalPanel";
            graphicalPanel.Padding = new Padding(19, 17, 19, 17);
            graphicalPanel.Size = new Size(735, 688);
            graphicalPanel.TabIndex = 0;
            // 
            // tabDataProcessing
            // 
            tabDataProcessing.Controls.Add(dataProcessingPanel);
            tabDataProcessing.Location = new Point(4, 33);
            tabDataProcessing.Name = "tabDataProcessing";
            tabDataProcessing.Padding = new Padding(4);
            tabDataProcessing.Size = new Size(743, 696);
            tabDataProcessing.TabIndex = 2;
            tabDataProcessing.Text = "Data";
            tabDataProcessing.UseVisualStyleBackColor = true;
            // 
            // dataProcessingPanel
            // 
            dataProcessingPanel.AutoScroll = true;
            dataProcessingPanel.Dock = DockStyle.Fill;
            dataProcessingPanel.Location = new Point(4, 4);
            dataProcessingPanel.Name = "dataProcessingPanel";
            dataProcessingPanel.Padding = new Padding(19, 17, 19, 17);
            dataProcessingPanel.Size = new Size(735, 688);
            dataProcessingPanel.TabIndex = 0;
            // 
            // tabSimulation
            // 
            tabSimulation.Controls.Add(simulationDemoPanel);
            tabSimulation.Location = new Point(4, 33);
            tabSimulation.Name = "tabSimulation";
            tabSimulation.Padding = new Padding(4);
            tabSimulation.Size = new Size(743, 696);
            tabSimulation.TabIndex = 3;
            tabSimulation.Text = "Simulation";
            tabSimulation.UseVisualStyleBackColor = true;
            // 
            // simulationDemoPanel
            // 
            simulationDemoPanel.Dock = DockStyle.Fill;
            simulationDemoPanel.Location = new Point(4, 4);
            simulationDemoPanel.Name = "simulationDemoPanel";
            simulationDemoPanel.Padding = new Padding(19, 17, 19, 17);
            simulationDemoPanel.Size = new Size(735, 688);
            simulationDemoPanel.TabIndex = 0;
            // 
            // tabHelp
            // 
            tabHelp.Controls.Add(helpPanel);
            tabHelp.Location = new Point(4, 33);
            tabHelp.Name = "tabHelp";
            tabHelp.Padding = new Padding(4);
            tabHelp.Size = new Size(743, 696);
            tabHelp.TabIndex = 4;
            tabHelp.Text = "Help";
            tabHelp.UseVisualStyleBackColor = true;
            // 
            // helpPanel
            // 
            helpPanel.Dock = DockStyle.Fill;
            helpPanel.Location = new Point(4, 4);
            helpPanel.Name = "helpPanel";
            helpPanel.Size = new Size(735, 688);
            helpPanel.TabIndex = 0;
            // 
            // consoleControl
            // 
            consoleControl.Dock = DockStyle.Fill;
            consoleControl.Location = new Point(0, 0);
            consoleControl.Name = "consoleControl";
            consoleControl.Size = new Size(1886, 390);
            consoleControl.TabIndex = 0;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(11F, 24F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1886, 1129);
            Controls.Add(mainSplit);
            MinimumSize = new Size(1559, 895);
            Name = "Form1";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "3D Cartesian Robot Frontend";
            Load += Form1_Load;
            mainSplit.Panel1.ResumeLayout(false);
            mainSplit.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)mainSplit).EndInit();
            mainSplit.ResumeLayout(false);
            topSplit.Panel1.ResumeLayout(false);
            topSplit.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)topSplit).EndInit();
            topSplit.ResumeLayout(false);
            rightTabs.ResumeLayout(false);
            tabTables.ResumeLayout(false);
            tabGraphical.ResumeLayout(false);
            tabDataProcessing.ResumeLayout(false);
            tabSimulation.ResumeLayout(false);
            tabHelp.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private SplitContainer mainSplit;
        private SplitContainer topSplit;
        private Wireframe3DViewer viewer3D;
        private TabControl rightTabs;
        private TabPage tabTables;
        private PathDataTablePanel pathDataTablePanel;
        private TabPage tabGraphical;
        private GraphicalPanel graphicalPanel;
        private TabPage tabDataProcessing;
        private DataProcessingPanel dataProcessingPanel;
        private TabPage tabSimulation;
        private SimulationDemoPanel simulationDemoPanel;
        private TabPage tabHelp;
        private HelpPanel helpPanel;
        private ConsoleControl consoleControl;
    }
}
