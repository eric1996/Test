using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PLC_Dispatcher_SXM_Rework
{
 public partial class Form1 : Form
 {
  /// <summary>
  /// key = station，value = 該台 PLC 的全部執行階段狀態
  /// </summary>
  Dictionary<string, PlcRuntime> dicPLC = new Dictionary<string, PlcRuntime>();
  /// <summary>
  /// 保護 dicPLC 與其中所有狀態的鎖 (原本每個 Dictionary 各一把鎖，整合後共用這把)
  /// </summary>
  private readonly object plcLock = new object();
  /// <summary>
  /// SN → MESSID (GUID) 目錄：
  ///   第一台 PLC (config 順序第一台) 讀到 SN → 產生新 GUID 放入目錄
  ///   其他台讀到相同 SN → 用 SN 對應到同一個 GUID 送 WS
  ///   最後一台 PLC 用完 (WS 回 S/F) → 從目錄刪除；回 E 則保留
  ///   第一台再收到相同 SN 但目錄裡還有 (上一輪沒走完) → 移除舊的、換新 GUID
  /// </summary>
  Dictionary<string, string> dicSnMessId = new Dictionary<string, string>();
  /// <summary>
  /// 與 dicSnMessId 平行：記錄每個 SN 項目的建立時間，供定時清除過期 GUID 用
  /// </summary>
  Dictionary<string, DateTime> dicSnMessIdTime = new Dictionary<string, DateTime>();
  /// <summary>
  /// 保護 dicSnMessId / dicSnMessIdTime 的鎖
  /// </summary>
  private readonly object snMessIdLock = new object();
  /// <summary>
  /// 定時輪詢 Ready 訊號用的 Timer
  /// </summary>
  System.Windows.Forms.Timer pollTimer;
  /// <summary>
  /// 定時清除過期 SN→GUID 項目的 Timer (依 config.messIdRetentionDays)
  /// </summary>
  System.Windows.Forms.Timer messIdCleanupTimer;
  /// <summary>
  /// 讀取 appsetting.json
  /// </summary>
  AppConfig config;
  /// <summary>
  /// WebService 客戶端
  /// </summary>
  WebServiceClient wsClient;
  /// <summary>
  /// 每個分頁的紀錄最多保留筆數 (由 appsetting.json 的 maxLogLines 決定)，超過就從最上面刪掉舊的
  /// </summary>
  private int MaxLogLines = 60;
  /// <summary>
  /// 每筆 WRITE 等待 PLC 回應的逾時時間 (ms)，超過視為失敗
  /// </summary>
  private const int WriteReplyTimeoutMs = 3000;
  /// <summary>
  /// 保護 log 分頁 UI 重建的鎖 (每台的表格已移到 PlcRuntime.LogGrid)
  /// </summary>
  private readonly object logLock = new object();

  public Form1()
  {
   InitializeComponent();
   initial();
  }
  /// <summary>
  /// 程式初始化：讀 appsetting.json (失敗則顯示錯誤並結束)、初始化 FileLogger、建立 WS 客戶端與兩個 Timer、註冊 FormClosing
  /// </summary>
  private void initial()
  {
   // 程式啟動就先讀appsetting.json，有問題顯示MessageBox並結束
   try
   {
    config = AppConfigLoader.Load();
   }
   catch (Exception ex)
   {
    MessageBox.Show("appsetting.json fail:\r\n" + ex.Message, "config file fail", MessageBoxButtons.OK, MessageBoxIcon.Error);
    Environment.Exit(1);
    return;
   }
   //初始化紀錄本地運行的log檔案 (位置固定 Logs、一律啟用，只由 config 決定保留天數)
   FileLogger.Initialize(config.logRetentionDays);

   // 每個分頁的紀錄顯示筆數由 config 決定 (0 以下用預設 60 筆)
   MaxLogLines = config.maxLogLines > 0 ? config.maxLogLines : 60;

   wsClient = new WebServiceClient(config);
   // 建立 Timer：每plcsReadySignalTimer ms 對還沒 Ready 的 PLC 再問一次 M7700，同時檢查逾時
   pollTimer = new System.Windows.Forms.Timer();
   pollTimer.Interval = config.plcsReadySignalTimer;
   pollTimer.Tick += PollTimer_Tick;

   // 建立清除過期 GUID 的 Timer：每小時掃一次，刪除超過 messIdRetentionDays 天的項目
   messIdCleanupTimer = new System.Windows.Forms.Timer();
   messIdCleanupTimer.Interval = 60 * 60 * 1000; // 1 小時
   messIdCleanupTimer.Tick += MessIdCleanupTimer_Tick;
   messIdCleanupTimer.Start();

   // 表單關閉時把所有連線 close
   this.FormClosing += Form1_FormClosing;

   // 記錄程式啟動資訊 (yyyyMMdd_System.txt)
   FileLogger.WriteSystem("========== APP Open ==========");
   FileLogger.WriteSystem("APP Start | loaded:" + config.plcs.Count + " PLC config(s): " + string.Join(", ", config.plcs.ConvertAll(p => "[" + "PLC" + p.id + "] " + " (" + p.ip + ":" + p.port + ")").ToArray()));
  }
  /// <summary>按壓 START 按鈕：讀 config、逐台建立連線、開始輪詢 Ready 訊號</summary>
  /// <param name="sender">事件來源 (START 按鈕)</param>
  /// <param name="e">事件資料</param>
  private void btnStart_Click(object sender, EventArgs e)
  {
   FileLogger.WriteSystem("User clicked START Button");
   btnStart.Enabled = false;
   btnStop.Enabled = true;

   // 1.停止定時器並徹底清空所有執行階段的舊狀態 (確保每次按 Start 都是全新開始)
   pollTimer.Stop();

   lock (plcLock) { dicPLC.Clear(); }

   // WS 登入不在 START 時先做 (token 會過期)，改成每次要送 WS 時 (SendPlcData) 才重新 login 取得新 token
   //config = AppConfigLoader.Load();不用再重複讀取，每次關掉程式再讀取
   wsClient = new WebServiceClient(config);

   // 依設定 foreach 去 new class 並建立該台的執行階段狀態，讓 UI 馬上顯示出來（初始顯示 Connected 或 Disconnected/Retry）
   foreach (PlcConfig plc in config.plcs)
   {
    string station = plc.station; // 先取出來給 closure 用
    SocketClientClass socket = new SocketClientClass(plc.id, plc.ip, plc.port);

    //接收兩個event
    socket.eventReceive += (byteArray, e2) => Socket_eventReceive(station, byteArray, e2);
    socket.eventConnectionChanged += (connected, e2) => Socket_connectionChanged(station, connected);

    lock (plcLock)
    {
     dicPLC[station] = new PlcRuntime
     {
      Socket = socket,
      Config = plc,
      DataIndex = -1,
      LastActiveTime = DateTime.Now
     };
    }
   }

   // 依 config 重建 log 分頁：每台 PLC 獨立分頁 (表格存到該台的 PlcRuntime.LogGrid)
   BuildLogTabs();
   // 立即重新整理表格顯示初始狀態
   RefreshTable();
   // 開始定時輪詢 Ready M7700訊號
   pollTimer.Start();

   // 透過 Task.Factory.StartNew 在背景執行緒進行連線與重試，不卡住 UI (先快照一份，避免迭代中 dicPLC 被其他執行緒修改)
   List<PlcRuntime> startList;
   lock (plcLock) { startList = new List<PlcRuntime>(dicPLC.Values); }

   foreach (PlcRuntime rt in startList)
   {
    string station = rt.Config.station;
    SocketClientClass socket = rt.Socket;
    PlcConfig plc = rt.Config;

    Task.Factory.StartNew(() =>
    {
     try
     {
      socket.Connect(); // 內部會有重試迴圈直到連線成功

      if (socket.IsConnected)
      {
       // 更新活動時間
       lock (plcLock)
       {
        rt.LastActiveTime = DateTime.Now;
       }

       // 連線成功後更新表格狀態
       RefreshTable();

       // 步驟1：先讀 Ready 訊號 (M7700)
       ReadReadySignal(plc, socket);
      }
     }
     catch (Exception ex)
     {
      // 單台失敗不影響其他台啟動 (畫面跳 MessageBox，同時寫入 System log 保留紀錄)
      FileLogger.WriteSystem("PLC [" + station + "] connect failed: " + ex.GetType().Name + ": " + ex.Message);
      this.BeginInvoke((Action)(() =>
      {
       MessageBox.Show("PLC [" + station + "] connect failed: " + ex.Message);
      }));
     }
    });
   }
  }
  /// <summary>STOP：先彈出確認視窗，確認後才呼叫共用方法斷線，並標記為 STOP 狀態 (保留 Log 與畫面檢視)</summary>
  /// <param name="sender">事件來源 (STOP 按鈕)</param>
  /// <param name="e">事件資料</param>
  private void btnStop_Click(object sender, EventArgs e)
  {
   // 防止誤按：先確認真的要 STOP
   DialogResult dr = MessageBox.Show("Are you sure you want to STOP the system?",
       "Confirm STOP", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
   if (dr != DialogResult.Yes)
   {
    FileLogger.WriteSystem("User clicked STOP but cancelled");
    return;
   }

   FileLogger.WriteSystem("User clicked STOP to stop the system");
   DisconnectAllPLCs(isManualStop: true);

   btnStart.Enabled = true;
   btnStop.Enabled = false;
  }

  /// <summary>停止輪詢並斷開所有 PLC 連線 (START/STOP/關閉程式共用)</summary>
  /// <param name="isManualStop">true = 使用者按 STOP，畫面顯示 STOP；false = 關閉程式</param>
  private void DisconnectAllPLCs(bool isManualStop)
  {
   pollTimer.Stop(); // 停止輪詢

   List<SocketClientClass> socketsToDisconnect = new List<SocketClientClass>();
   lock (plcLock)
   {
    foreach (PlcRuntime rt in dicPLC.Values)
    {
     socketsToDisconnect.Add(rt.Socket);

     if (isManualStop)
     {
      // 標記為手動停止，讓畫面顯示 STOP
      rt.ManuallyStopped = true;
     }
    }
   }

   foreach (var socket in socketsToDisconnect)
   {
    try { socket.DisConnect(); }
    catch (Exception ex) { FileLogger.WriteException("DisconnectAllPLCs", ex); }
   }

   RefreshTable();
  }

  /// <summary>更新 DataGridView 顯示每台 PLC 連線狀態</summary>
  private void RefreshTable()
  {
   if (dgvPLC.InvokeRequired)
   {
    dgvPLC.BeginInvoke(new Action(RefreshTable));
    return;
   }

   dgvPLC.Rows.Clear();
   lock (plcLock)
   {
    foreach (KeyValuePair<string, PlcRuntime> kvp in dicPLC)
    {
     PlcRuntime rt = kvp.Value;
     PlcConfig plc = rt.Config;
     SocketClientClass socket = rt.Socket;
     string statusText = socket.IsConnected ? "Connected"  : (rt.ManuallyStopped ? "STOP" : "Disconnected and  Retry");
     dgvPLC.Rows.Add(plc.id, socket.HostName, socket.LocalIP, kvp.Key, plc.ip, plc.port, statusText);
    }
   }
  }

  /// <summary>
  /// 關閉 APP = 先彈出確認視窗，確認後自動執行完整 STOP 流程，不需先手動按 STOP： 記錄關閉資訊 → 停 Timer → 逐台斷線 → 清空記憶體狀態
  /// </summary>
  private void Form1_FormClosing(object sender, FormClosingEventArgs e)
  {
   // 防止誤按：使用者手動關閉時先確認；系統關機/程式結束則不詢問直接放行
   if (e.CloseReason == CloseReason.UserClosing)
   {
    DialogResult dr = MessageBox.Show("Are you sure you want to close the application?",
        "Confirm Close", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
    if (dr != DialogResult.Yes)
    {
     e.Cancel = true;
     FileLogger.WriteSystem("User clicked Close but cancelled");
     return;
    }
   }

   // 1. 記錄關閉資訊 (yyyyMMdd_System.txt)
   //    說明：關閉 APP 時不是人為按 STOP，而是系統自動執行 STOP 流程
   FileLogger.WriteSystem("========== APP Close ==========");
   FileLogger.WriteSystem("APP closing, system auto-executed STOP");

   // 2. 停止輪詢並逐台斷線 (不標記手動停止，畫面即將關閉無影響)
   //    每台斷線時 SocketClientClass 會觸發 eventConnectionChanged(false)，
   //    由 Socket_connectionChanged 統一記錄 DISCONNECTED，避免重複寫兩次
   if (messIdCleanupTimer != null) messIdCleanupTimer.Stop();
   DisconnectAllPLCs(isManualStop: false);
   FileLogger.WriteSystem("All PLC connections closed, APP exiting");

   // 3. 關閉程式時順便把記憶體狀態清乾淨
   lock (plcLock) { dicPLC.Clear(); }
   lock (snMessIdLock) { dicSnMessId.Clear(); dicSnMessIdTime.Clear(); }
  }
 }
}