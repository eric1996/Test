
namespace PLC_Dispatcher_SXM_Rework
{
 partial class Form1
 {
  /// <summary>
  /// 設計工具所需的變數。
  /// </summary>
  private System.ComponentModel.IContainer components = null;
  /// <summary>
  /// 清除任何使用中的資源。
  /// </summary>
  /// <param name="disposing">如果應該處置受控資源則為 true，否則為 false。</param>
  protected override void Dispose(bool disposing)
  {
   if (disposing && (components != null))
   {
    components.Dispose();
   }
   base.Dispose(disposing);
  }
  #region Windows Form 設計工具產生的程式碼
  /// <summary>
  /// 此為設計工具支援所需的方法 - 請勿使用程式碼編輯器修改
  /// 這個方法的內容。
  /// </summary>
  private void InitializeComponent()
  {
   this.btnStart = new System.Windows.Forms.Button();
   this.btnStop = new System.Windows.Forms.Button();
   this.dgvPLC = new System.Windows.Forms.DataGridView();
   this.colID = new System.Windows.Forms.DataGridViewTextBoxColumn();
   this.colHostName = new System.Windows.Forms.DataGridViewTextBoxColumn();
   this.colLocalIP = new System.Windows.Forms.DataGridViewTextBoxColumn();
   this.colStation = new System.Windows.Forms.DataGridViewTextBoxColumn();
   this.colIP = new System.Windows.Forms.DataGridViewTextBoxColumn();
   this.colPort = new System.Windows.Forms.DataGridViewTextBoxColumn();
   this.colStatus = new System.Windows.Forms.DataGridViewTextBoxColumn();
   this.tabLogs = new System.Windows.Forms.TabControl();
   this.lblSend = new System.Windows.Forms.Label();
   ((System.ComponentModel.ISupportInitialize)(this.dgvPLC)).BeginInit();
   this.SuspendLayout();
   // 
   // btnStart
   // 
   this.btnStart.Location = new System.Drawing.Point(12, 12);
   this.btnStart.Name = "btnStart";
   this.btnStart.Size = new System.Drawing.Size(90, 32);
   this.btnStart.TabIndex = 0;
   this.btnStart.Text = "START";
   this.btnStart.UseVisualStyleBackColor = true;
   this.btnStart.UseWaitCursor = true;
   this.btnStart.Click += new System.EventHandler(this.btnStart_Click);
   // 
   // btnStop
   // 
   this.btnStop.Enabled = false;
   this.btnStop.Location = new System.Drawing.Point(112, 12);
   this.btnStop.Name = "btnStop";
   this.btnStop.Size = new System.Drawing.Size(90, 32);
   this.btnStop.TabIndex = 1;
   this.btnStop.Text = "STOP";
   this.btnStop.UseVisualStyleBackColor = true;
   this.btnStop.UseWaitCursor = true;
   this.btnStop.Click += new System.EventHandler(this.btnStop_Click);
   // 
   // dgvPLC
   // 
   this.dgvPLC.AllowUserToAddRows = false;
   this.dgvPLC.AllowUserToDeleteRows = false;
   this.dgvPLC.AllowUserToResizeRows = false;
   this.dgvPLC.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
   this.dgvPLC.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
   this.dgvPLC.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colID,
            this.colHostName,
            this.colLocalIP,
            this.colStation,
            this.colIP,
            this.colPort,
            this.colStatus});
   this.dgvPLC.Location = new System.Drawing.Point(12, 52);
   this.dgvPLC.MultiSelect = false;
   this.dgvPLC.Name = "dgvPLC";
   this.dgvPLC.ReadOnly = true;
   this.dgvPLC.RowHeadersVisible = false;
   this.dgvPLC.RowTemplate.Height = 24;
   this.dgvPLC.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
   this.dgvPLC.Size = new System.Drawing.Size(976, 150);
   this.dgvPLC.TabIndex = 2;
   this.dgvPLC.UseWaitCursor = true;
   // 
   // colID
   // 
   this.colID.HeaderText = "ID";
   this.colID.Name = "colID";
   this.colID.ReadOnly = true;
   this.colID.SortMode = System.Windows.Forms.DataGridViewColumnSortMode.NotSortable;
   // 
   // colHostName
   // 
   this.colHostName.HeaderText = "HostName";
   this.colHostName.Name = "colHostName";
   this.colHostName.ReadOnly = true;
   this.colHostName.SortMode = System.Windows.Forms.DataGridViewColumnSortMode.NotSortable;
   // 
   // colLocalIP
   // 
   this.colLocalIP.HeaderText = "LocalIP";
   this.colLocalIP.Name = "colLocalIP";
   this.colLocalIP.ReadOnly = true;
   this.colLocalIP.SortMode = System.Windows.Forms.DataGridViewColumnSortMode.NotSortable;
   // 
   // colStation
   // 
   this.colStation.HeaderText = "Station";
   this.colStation.Name = "colStation";
   this.colStation.ReadOnly = true;
   this.colStation.SortMode = System.Windows.Forms.DataGridViewColumnSortMode.NotSortable;
   // 
   // colIP
   // 
   this.colIP.HeaderText = "PLC IP";
   this.colIP.Name = "colIP";
   this.colIP.ReadOnly = true;
   this.colIP.SortMode = System.Windows.Forms.DataGridViewColumnSortMode.NotSortable;
   // 
   // colPort
   // 
   this.colPort.HeaderText = "PLC Port";
   this.colPort.Name = "colPort";
   this.colPort.ReadOnly = true;
   this.colPort.SortMode = System.Windows.Forms.DataGridViewColumnSortMode.NotSortable;
   // 
   // colStatus
   // 
   this.colStatus.HeaderText = "Status";
   this.colStatus.Name = "colStatus";
   this.colStatus.ReadOnly = true;
   this.colStatus.SortMode = System.Windows.Forms.DataGridViewColumnSortMode.NotSortable;
   // 
   // tabLogs
   // 
   this.tabLogs.Location = new System.Drawing.Point(12, 232);
   this.tabLogs.Name = "tabLogs";
   this.tabLogs.SelectedIndex = 0;
   this.tabLogs.Size = new System.Drawing.Size(976, 206);
   this.tabLogs.TabIndex = 4;
   this.tabLogs.UseWaitCursor = true;
   this.tabLogs.DrawItem += new System.Windows.Forms.DrawItemEventHandler(this.tabLogs_DrawItem);
   this.tabLogs.SelectedIndexChanged += new System.EventHandler(this.tabLogs_SelectedIndexChanged);
   // 
   // lblSend
   // 
   this.lblSend.AutoSize = true;
   this.lblSend.Location = new System.Drawing.Point(12, 212);
   this.lblSend.Name = "lblSend";
   this.lblSend.Size = new System.Drawing.Size(174, 12);
   this.lblSend.TabIndex = 3;
   this.lblSend.Text = "Communication Log (per-PLC tabs)";
   this.lblSend.UseWaitCursor = true;
   // 
   // Form1
   // 
   this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
   this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
   this.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
   this.ClientSize = new System.Drawing.Size(1000, 450);
   this.Controls.Add(this.tabLogs);
   this.Controls.Add(this.lblSend);
   this.Controls.Add(this.dgvPLC);
   this.Controls.Add(this.btnStop);
   this.Controls.Add(this.btnStart);
   this.MaximizeBox = false;
   this.Name = "Form1";
   this.Text = "PLC_Dispatcher_SXM_Rework";
   ((System.ComponentModel.ISupportInitialize)(this.dgvPLC)).EndInit();
   this.ResumeLayout(false);
   this.PerformLayout();

  }

  #endregion

  private System.Windows.Forms.Button btnStart;
  private System.Windows.Forms.Button btnStop;
  private System.Windows.Forms.DataGridView dgvPLC;
  private System.Windows.Forms.DataGridViewTextBoxColumn colID;
  private System.Windows.Forms.DataGridViewTextBoxColumn colHostName;
  private System.Windows.Forms.DataGridViewTextBoxColumn colLocalIP;
  private System.Windows.Forms.DataGridViewTextBoxColumn colStation;
  private System.Windows.Forms.DataGridViewTextBoxColumn colIP;
  private System.Windows.Forms.DataGridViewTextBoxColumn colPort;
  private System.Windows.Forms.DataGridViewTextBoxColumn colStatus;
  private System.Windows.Forms.Label lblSend;
  private System.Windows.Forms.TabControl tabLogs;
 }
}

