using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace PLC_Dispatcher_SXM_Rework
{
 /// <summary>
 /// Form1 的 PLC 通訊流程 (從 Form1.cs 拆出，行為不變)：
 /// Ready 輪詢、封包接收、plcData 讀取、WS 呼叫與 actions 執行、MESSID (GUID) 管理
 /// </summary>
 public partial class Form1 : Form
 {
  /// <summary>依 appsetting.json 的 readySignal.address 組封包去讀 (例：M7700)</summary>
  private void ReadReadySignal(PlcConfig plc, SocketClientClass socket)
  {
   if (!socket.IsConnected) return;

   string addr = plc.readySignal.address;         // "M7700"
   McDevice device = (McDevice)Enum.Parse(typeof(McDevice), addr.Substring(0, 1));
   int startNo = int.Parse(addr.Substring(1));      // 7700

   byte[] cmd = FX5UProtocol.Build(McFunction.Read, device, startNo, 1, null);
   lock (plcLock)
   {
    PlcRuntime rt;
    if (dicPLC.TryGetValue(plc.station, out rt))
    {
     rt.ReadyReplyPending = true;          // 標記已詢問，等回應
     rt.LastActiveTime = DateTime.Now;     // 每次發送指令時更新發送時間點，作為超時計算起點
    }
   }

   LogSendPacket(plc.station, plc.id, cmd);
   socket.SendData(cmd);
  }

  /// <summary>Timer 每次觸發：對還沒 Ready 的 PLC 再問一次 M7700，並檢查是否有逾時未回覆</summary>
  /// <param name="sender">事件來源 (pollTimer)</param>
  /// <param name="e">事件資料</param>
  private void PollTimer_Tick(object sender, EventArgs e)
  {
   RefreshTable(); // 定時刷新表格狀態，讓自動重連成功時能即時顯示 Connected

   // 快照一份，避免迭代中 dicPLC 被其他執行緒修改
   List<PlcRuntime> snapshot;
   lock (plcLock) { snapshot = new List<PlcRuntime>(dicPLC.Values); }

   foreach (PlcRuntime rt in snapshot)
   {
    string station = rt.Config.station;
    SocketClientClass socket = rt.Socket;
    if (!socket.IsConnected) continue;

    // 正在執行 WRITE actions 等回應時，不要插問 M7700
    if (rt.WriteReplyEvent != null) continue;

    // ===== 逾時斷線重連機制：暫時停用 (2026-10-19) =====
    // PLC 沒有回覆 M7700=1 時不再主動斷線重連，改由下方輪詢邏輯持續詢問
    /*
    // 檢查是否超過 10 秒沒有收到任何回應（可依需求自行調整秒數）
    if ((DateTime.Now - rt.LastActiveTime).TotalSeconds > 10)
    {
        // 清除等待狀態與重置資料索引
        rt.ReadyReplyPending = false;
        rt.DataIndex = -1;

        LogSendPacket(station, rt.Config.id, "[TIMEOUT] PLC did not reply in time, reconnecting...");

        // 1. 先主動斷開舊連線
        socket.DisConnect();

        // 2. 透過 Task.Factory.StartNew 在背景重新執行連線與初始化流程
        PlcConfig plc = rt.Config;
        Task.Factory.StartNew(() =>
        {
            try
            {
                socket.Connect(); // 重新連線

                if (socket.IsConnected)
                {
                    lock (plcLock)
                    {
                        rt.LastActiveTime = DateTime.Now;
                    }
                    RefreshTable();

                    // 連線成功後，重新發送 M7700 Ready 訊號查詢
                    ReadReadySignal(plc, socket);
                }
            }
            catch (Exception ex)
            {
                this.BeginInvoke((Action)(() =>
                {
                    MessageBox.Show("PLC [" + station + "] reconnect failed: " + ex.Message);
                }));
            }
        });

        continue;
    }
    */
    // ===== 逾時斷線重連機制結束 =====

    // 還在等 Ready (index == -1) 且上一筆詢問已有回應，才再問一次
    if (rt.DataIndex == -1 && !rt.ReadyReplyPending)
    {
     ReadReadySignal(rt.Config, socket);
    }
   }
  }
  /// <summary>PLC 連線狀態變更：連線成功 / 斷線 (含自動重連成功) 都記錄到 System log</summary>
  /// <param name="station">發生狀態變更的 PLC station 名稱</param>
  /// <param name="connected">true = 已連線，false = 已斷線</param>
  private void Socket_connectionChanged(string station, bool connected)
  {
   PlcConfig plc;
   lock (plcLock)
   {
    PlcRuntime rt;
    plc = dicPLC.TryGetValue(station, out rt) ? rt.Config : null;
   }

   string name = plc == null ? station : "[" + plc.id + "] " + plc.station;
   FileLogger.WriteSystem("PLC " + name + " (" + plc?.ip + ":" + plc?.port + ") " + (connected ? "CONNECTED" : "DISCONNECTED"));

   // 狀態變了，立即刷新表格
   RefreshTable();
  }
  /// <summary>
  /// 接收封包主處理器：WRITE 回應攔截、Ready 訊號判讀、plcData 逐筆累積、
  /// 全部讀完後送 WS 並依 S/F/E 執行對應 actions，最後管理 SN→GUID 目錄
  /// </summary>
  /// <param name="station">封包來源的 PLC station 名稱</param>
  /// <param name="byteArray">收到的原始封包</param>
  /// <param name="e">事件資料</param>
  private void Socket_eventReceive(string station, byte[] byteArray, EventArgs e)
  {
   try
   {
    // 取得該台的執行階段狀態 (不存在表示已 STOP 或剛清空，直接忽略)
    PlcRuntime rt;
    lock (plcLock)
    {
     if (!dicPLC.TryGetValue(station, out rt)) return;
    }

    // 收到封包第一件事：更新最後活動時間（代表 PLC 有正常回覆）
    rt.LastActiveTime = DateTime.Now;

    // 記錄收到的回覆封包到回覆 LOG (id 取不到用 0)
    int logId = rt.Config != null ? rt.Config.id : 0;
    LogRecvPacket(station, logId, byteArray);

    // WRITE actions 的回應：寫入回應只有 11 bytes 沒有資料區，
    // 要在 Parse 檢查 Data 之前先攔截，記下結束代碼並通知 ExecuteActions 繼續
    AutoResetEvent writeEvt = rt.WriteReplyEvent;

    if (writeEvt != null)
    {
     int endCode = -1;
     try
     {
      McResponse wres = FX5UProtocol.Parse(byteArray);
      endCode = wres.EndCode;
     }
     catch (Exception ex)
     {
      // WRITE 回應解析失敗：原本靜默吞掉會看不到原因，這裡記錄 (含 Hex)；
      // endCode 維持 -1，ExecuteActions 會視同 NG 走 exception actions
      FileLogger.Write(logId, "[WRITE REPLY PARSE ERROR] " + ex.Message, byteArray);
     }

     rt.WriteReplyEndCode = endCode;

     writeEvt.Set();   // 讓 ExecuteActions 的 WaitOne 解除等待
     return;
    }

    McResponse res = FX5UProtocol.Parse(byteArray);
    if (res.EndCode != 0 || res.Data == null || res.Data.Length == 0)
     return;

    int index = rt.DataIndex;

    if (index == -1)
    {
     rt.ReadyReplyPending = false;       // 收到回應，解除詢問中標記

     // 步驟1回應：M7700 ON (0x10) = Ready
     bool ready = res.Data[0] != 0x00;
     if (ready)
     {
      // ▼ 這裡代表 PLC 準備好了，寫入文字檔紀錄交握啟動 ▼ (檔名用 PLC{id})
      FileLogger.Write(logId, $"[READY ON] Ready signal detected (M7700=1), start reading data flow");
      // 變成 1 了，開始問 D 區 (第一筆 plcData)
      SendPlcDataRead(station, 0);
     }
     // 還是 0 的話不做任何事，等 Timer 下一次再問
    }
    else
    {
     // 步驟2回應：plcData 資料 (字元件，每字 2 bytes ASCII)
     PlcConfig plc = rt.Config;
     string value = Encoding.ASCII.GetString(res.Data).TrimEnd('\0', ' ');

     PlcDataConfig data = plc.plcData[index];

     // 串成 JSON 字串先存起來，不跳 MessageBox
     lock (plcLock)
     {
      if (rt.DataResults == null)
      {
       rt.DataResults = new List<string>();
      }

      // 1. 清理 PLC 讀出的 Null 字元與前後空白
      string cleanValue = (value ?? "").Replace("\0", "").Trim();
      string cleanInfoName = data.infoName ?? "";

      // 2. 使用匿名物件與 Newtonsoft.Json 進行安全序列化
      var logItem = new
      {
       infoName = cleanInfoName,
       infoValue = cleanValue,
       address = data.address,
       length = data.length
      };

      // 3. 轉成安全的 JSON 字串加入清單
      rt.DataResults.Add(JsonConvert.SerializeObject(logItem));
     }

     // 繼續讀下一筆；全部讀完才送 WS
     if (index + 1 < plc.plcData.Count)
     {
      SendPlcDataRead(station, index + 1);
     }
     else
     {
      // 全部讀完，把結果串起來 (每筆一個 JSON 字串)
      List<string> allInfo;
      lock (plcLock)
      {
       allInfo = rt.DataResults == null ? new List<string>() : new List<string>(rt.DataResults);
       rt.DataResults = null;
      }

      // ═══════════════════════════════════════════════════
      // ★ 步驟3：送 WS 區段 ★
      // 流程：plcData 全部讀完 → 打包 JSON 送 WS → 回 S 或 F
      //        → S 執行 actions.success / F 執行 actions.fail
      //        → 最後 finally_ → 回去監聽 M7700
      // (背景執行，不卡接收執行緒)
      // ═══════════════════════════════════════════════════
      Task.Factory.StartNew(() =>
      {
       // @SN：從讀取結果取出 WU_SN (D2500~D2539)，當 SN→GUID 目錄的 key
       string mbSn = WebServiceClient.ExtractInfoValue(allInfo, "WU_SN");

       // 依 SN 取得這次要帶的 MESSID：
       // 第一台產生新的、其他台對應同一個、最後台用完後刪除
       string messId = GetMessIdForWs(plc, mbSn);

       // 這次 WS 結果旗標 (S/F/E)，供 finally 決定是否刪 GUID；WS 例外預設當 E (保留)
       string resultFlag = "E";

       try
       {
        string wsResult;
        string resultMsg, actionCondition;

        // ── 實際呼叫 WS (細節在 Class/WebServiceClient.cs) ──
        // 記錄這次 WS 呼叫的 SN / MESSID (APP->WS，Hex 欄留空)
        LogWsPacket(plc.station, plc.id, mbSn, messId, null);

        wsResult = wsClient.SendPlcData(plc, allInfo, messId, out resultMsg, out actionCondition);

        // 記錄 WS 回傳的三個 output (WS->APP，Hex 欄留空)
        LogWsResponse(plc.station, plc.id, wsResult, resultMsg, actionCondition);

        // ══════════════════════════════════════════════════════════
        // ★ 依據 WS 回應的 Flag (S/F/E) 與帶有的 actionCondition 篩選對應 actions
        // ══════════════════════════════════════════════════════════
        string upperFlag = (wsResult ?? "").Trim().ToUpperInvariant();
        resultFlag = upperFlag.StartsWith("S") ? "S" : (upperFlag.StartsWith("E") ? "E" : "F");

        List<PlcActionConfig> targetActions = GetMatchedActions(plc, wsResult, actionCondition);

        // 每筆 WRITE 都等 PLC 回應，中途失敗 (逾時/NG) 會回 false
        bool actionsOk = ExecuteActions(station, targetActions);

        if (!actionsOk)
        {
         // actions 沒全部成功，補走 exception actions
         LogSendPacket(plc.station, plc.id, "[ACTIONS FAIL] WRITE not all succeeded, running exception actions");
         ExecuteActions(station, plc.actions.exception);
        }
       }
       catch (Exception ex)
       {
        // WS 失敗走 exception actions
        LogSendPacket(plc.station, plc.id, "[WS ERROR] " + ex.Message);
        ExecuteActions(station, plc.actions.exception);
       }
       finally
       {
        // 最後一台 PLC：WS 回 S 或 F 把 GUID 刪除；回 E (或 WS 例外) 則保留
        RemoveMessIdAfterLastPlc(plc, mbSn, resultFlag);
        // 重置狀態：回到等 Ready 訊號 (M7700) 的輪詢
        lock (plcLock)
        {
         rt.DataIndex = -1;
         rt.ReadyReplyPending = false;
        }
       }
      });
     }
    }
   }
   catch (Exception ex)
   {
    // 解析失敗只記錄，不讓程式當掉 (畫面跳 MessageBox，同時寫入 System log 保留完整紀錄)
    FileLogger.WriteSystem("PLC [" + station + "] eventReceive error: " + ex.GetType().Name + ": " + ex.Message);
    this.BeginInvoke((Action)(() =>
    {
     MessageBox.Show("PLC [" + station + "] error: " + ex.Message);
    }));
   }
  }

  /// <summary>
  /// 根據 WebService 回傳結果（S/F/E）與帶有的 actionCondition，從 PLC 設定中篩選出對應的動作清單
  /// </summary>
  private List<PlcActionConfig> GetMatchedActions(PlcConfig plc, string wsResult, string actionCondition)
  {
   List<PlcActionConfig> rawActions = null;
   string upperResult = (wsResult ?? "").Trim().ToUpperInvariant();

   // 1. 最上面先分 S、F、E
   if (upperResult.StartsWith("S"))
   {
    rawActions = plc.actions.success;
   }
   else if (upperResult.StartsWith("E"))
   {
    rawActions = plc.actions.exception ?? plc.actions.fail;
   }
   else
   {
    rawActions = plc.actions.fail;
   }

   if (rawActions == null) return new List<PlcActionConfig>();

   // 2. 過濾 actionCondition：
   //    WS 回傳空白 → 只跑 config 裡 condition 為空白的
   //    WS 回傳內容 (如 C1) → 跑 condition 為空白的 + condition 等於該內容的
   string targetCondition = (actionCondition ?? "").Trim();

   var filteredActions = rawActions.FindAll(action =>
   {
    string cond = (action.condition ?? "").Trim();

    if (targetCondition.Length == 0)
     return cond.Length == 0;   // WS 空白 → 只跑 condition 空白的

    // WS 有值 → condition 空白的也要跑，再加上 condition 一樣的
    return cond.Length == 0
        || string.Equals(cond, targetCondition, StringComparison.OrdinalIgnoreCase);
   });

   return filteredActions;
  }

  /// <summary>依 config 的 plcData[index] 組封包讀取 (字元件，讀 length 個字)</summary>
  /// <param name="station">PLC station 名稱</param>
  /// <param name="index">要讀取的 plcData 索引</param>
  private void SendPlcDataRead(string station, int index)
  {
   PlcRuntime rt;
   lock (plcLock)
   {
    dicPLC.TryGetValue(station, out rt);
   }

   if (rt == null || rt.Socket == null || !rt.Socket.IsConnected) return;

   SocketClientClass socket = rt.Socket;
   PlcConfig plc = rt.Config;
   PlcDataConfig data = plc.plcData[index];

   string addr = data.address;                     // "D2500"
   McDevice device = (McDevice)Enum.Parse(typeof(McDevice), addr.Substring(0, 1));
   int startNo = int.Parse(addr.Substring(1));      // 2500

   byte[] cmd = FX5UProtocol.Build(McFunction.Read, device, startNo, data.length, null);

   lock (plcLock)
   {
    rt.DataIndex = index; // 記錄現在在等哪一筆的回應
   }

   // 第一筆開始時先清空上一輪的結果
   if (index == 0)
   {
    lock (plcLock)
    {
     rt.DataResults = null;
    }
   }

   // 發送資料時也更新最後活動時間
   lock (plcLock)
   {
    rt.LastActiveTime = DateTime.Now;
   }

   LogSendPacket(plc.station, plc.id, cmd);
   socket.SendData(cmd);
  }

  /// <summary>依 WS 回應執行 actions (每筆 WRITE 都等 PLC 回應 EndCode=0 才發下一筆，逾時或 NG 就中止)</summary>
  /// <param name="station">PLC station 名稱</param>
  /// <param name="actions">要執行的動作清單</param>
  /// <returns>true = 全部成功；false = 中途失敗 (逾時或 EndCode != 0)</returns>
  private bool ExecuteActions(string station, List<PlcActionConfig> actions)
  {
   if (actions == null || actions.Count == 0) return true;

   PlcRuntime rt;
   lock (plcLock)
   {
    dicPLC.TryGetValue(station, out rt);
   }
   if (rt == null || rt.Socket == null || !rt.Socket.IsConnected) return false;

   SocketClientClass socket = rt.Socket;
   PlcConfig plc = rt.Config;

   // 依 config 由上至下的順序執行
   foreach (PlcActionConfig action in actions)
   {
    // 目前只處理 WRITE，其他 actionType 之後再擴充
    if (!string.Equals(action.actionType, "WRITE", StringComparison.OrdinalIgnoreCase))
     continue;

    string addr = action.address;                               // "M7700"
    McDevice device = (McDevice)Enum.Parse(typeof(McDevice), addr.Substring(0, 1));
    int startNo = int.Parse(addr.Substring(1));     // 7700

    byte[] cmd = FX5UProtocol.BuildBitWrite(device, startNo, action.value);

    // 發送前更新活動時間
    lock (plcLock)
    {
     rt.LastActiveTime = DateTime.Now;
    }

    // 先註冊等待事件再發送，避免回應先到卻沒人等
    AutoResetEvent evt = new AutoResetEvent(false);
    lock (plcLock)
    {
     rt.WriteReplyEvent = evt;
    }

    LogSendPacket(plc.station, plc.id, cmd);
    socket.SendData(cmd);

    // 等 PLC 回應，逾時視為失敗
    bool replied = evt.WaitOne(WriteReplyTimeoutMs);

    lock (plcLock)
    {
     rt.WriteReplyEvent = null;
    }
    // 事件不 Close，避免接收執行緒晚到的 Set 撞到已釋放物件，交給 GC 回收

    int endCode;
    lock (plcLock)
    {
     endCode = rt.WriteReplyEndCode;
     rt.WriteReplyEndCode = -1;
    }

    if (!replied || endCode != 0)
    {
     LogSendPacket(station, plc.id, "[WRITE " + (replied ? "NG EndCode=" + endCode : "TIMEOUT") + "] " + addr + " value=" + action.value);
     return false;   // 這筆失敗，後面的 actions 不繼續
    }
   }

   return true;
  }

  /// <summary>
  /// 依 SN 取得這次送 WS 要用的 MESSID (GUID)，並維護 SN→GUID 目錄：
  ///   第一台 PLC (config 順序第一台)：目錄裡已有相同 SN (上一輪沒走到最後台就重來)
  ///     → 移除舊 GUID、產生新的；沒有 → 直接產生新 GUID 放入
  ///   中間/最後台：用 SN 對應到第一台產生的同一個 GUID
  ///     (目錄裡找不到，例：APP 中途重啟沒收到第一台的 SN → 補產生一個放進去)
  /// </summary>
  /// <param name="plc">目前這台的設定</param>
  /// <param name="sn">從讀取結果取出的 WU_SN</param>
  /// <returns>這次要帶入 WS 的 MESSID (GUID)</returns>
  private string GetMessIdForWs(PlcConfig plc, string sn)
  {
   string firstStation = config.plcs[0].station;

   lock (snMessIdLock)
   {
    string messId;

    if (plc.station == firstStation)
    {
     // 第一台：目錄裡已有相同 SN (上一輪沒走完) → 拿掉舊的換新的 (第一站一律給新 GUID)
     string oldMessId;
     if (sn.Length > 0 && dicSnMessId.TryGetValue(sn, out oldMessId))
     {
      dicSnMessId.Remove(sn);
      dicSnMessIdTime.Remove(sn);
      LogSendPacket(plc.station, plc.id, "[MESSID] SN duplicated but catalog already has GUID (previous round unfinished), removed old " + oldMessId + " and created new");
     }

     messId = Guid.NewGuid().ToString().ToUpperInvariant();

     if (sn.Length > 0)
     {
      dicSnMessId[sn] = messId;
      dicSnMessIdTime[sn] = DateTime.Now;
     }
    }
    else
    {
     // 中間/最後台：用 SN 對應到同一個 GUID
     if (sn.Length > 0 && dicSnMessId.TryGetValue(sn, out messId))
     {
      LogSendPacket(plc.station, plc.id, "[MESSID] Mapped to catalog GUID " + messId);
     }
     else
     {
      // 目錄裡沒有 → 補產生一個 (SN 有值才放入目錄)
      messId = Guid.NewGuid().ToString().ToUpperInvariant();

      if (sn.Length > 0)
      {
       dicSnMessId[sn] = messId;
       dicSnMessIdTime[sn] = DateTime.Now;
      }
      LogSendPacket(plc.station, plc.id, "[MESSID] Catalog has no GUID for SN, generated new " + messId);
     }
    }

    return messId;
   }
  }

  /// <summary>
  /// 最後一台 PLC (config 順序最後一台) 的 WS 流程結束後，依結果決定是否刪 GUID：
  ///   WS 回 S 或 F → 刪除 (這片 SN 的流程已走完)
  ///   WS 回 E (或 WS 例外) → 保留 (方便後續重試沿用同一個 GUID)
  ///   中間台不在此刪除，F/E 自然保留給下一站
  /// </summary>
  /// <param name="plc">目前這台的設定</param>
  /// <param name="sn">這次的 WU_SN</param>
  /// <param name="resultFlag">WS 結果旗標 (S/F/E)</param>
  private void RemoveMessIdAfterLastPlc(PlcConfig plc, string sn, string resultFlag)
  {
   string lastStation = config.plcs[config.plcs.Count - 1].station;
   if (plc.station != lastStation || sn.Length == 0) return;

   // 最後一站只有 S / F 才清除，E 保留
   if (!string.Equals(resultFlag, "S", StringComparison.OrdinalIgnoreCase)
    && !string.Equals(resultFlag, "F", StringComparison.OrdinalIgnoreCase))
   {
    LogSendPacket(plc.station, plc.id, "[MESSID] Last station result=" + resultFlag + ", keep SN GUID");
    return;
   }

   lock (snMessIdLock)
   {
    string removed;
    if (dicSnMessId.TryGetValue(sn, out removed))
    {
     dicSnMessId.Remove(sn);
     dicSnMessIdTime.Remove(sn);
     LogSendPacket(plc.station, plc.id, "[MESSID] Last station result=" + resultFlag + ", removed SN GUID " + removed);
    }
   }
  }

  /// <summary>定時清除 SN→GUID 目錄中超過 config.messIdRetentionDays 天的項目，避免殘留佔記憶體</summary>
  /// <param name="sender">事件來源 (messIdCleanupTimer)</param>
  /// <param name="e">事件資料</param>
  private void MessIdCleanupTimer_Tick(object sender, EventArgs e)
  {
   int retentionDays = config != null ? config.messIdRetentionDays : 0;
   if (retentionDays <= 0) return; // 設定 0 或未設定 → 不自動清除

   DateTime threshold = DateTime.Now.AddDays(-retentionDays);
   List<string> expiredSns = new List<string>();

   lock (snMessIdLock)
   {
    foreach (KeyValuePair<string, DateTime> kv in dicSnMessIdTime)
    {
     if (kv.Value < threshold) expiredSns.Add(kv.Key);
    }

    foreach (string sn in expiredSns)
    {
     string removed;
     dicSnMessId.TryGetValue(sn, out removed);
     dicSnMessId.Remove(sn);
     dicSnMessIdTime.Remove(sn);
     FileLogger.WriteSystem("[MESSID] Expired (>" + retentionDays + "d), removed SN GUID " + removed);
    }
   }
  }
 }
}
