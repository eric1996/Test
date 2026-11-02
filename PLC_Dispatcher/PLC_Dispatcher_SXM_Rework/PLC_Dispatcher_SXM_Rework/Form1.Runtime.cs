using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace PLC_Dispatcher_SXM_Rework
{
 public partial class Form1 : Form
 {
  /// <summary>
  /// 每台 PLC 的執行階段狀態。
  /// 用 dicPLC[station] 就能取得該台全部狀態 (連線/設定/輪詢/等待/log 分頁)
  /// </summary>
  private class PlcRuntime
  {
   /// <summary>該台 PLC 的連線，eventReceive 靠 station 分辨是哪一台</summary>
   public SocketClientClass Socket;
   /// <summary>該台的設定 (readySignal / plcData / actions)</summary>
   public PlcConfig Config;
   /// <summary>目前等待回應的 plcData 索引 (-1 = 還在等 ready 訊號回應)</summary>
   public int DataIndex = -1;
   /// <summary>已送出 Ready 詢問但還沒收到回應 (避免重複發送)</summary>
   public bool ReadyReplyPending;
   /// <summary>輪詢 M7700 的暫存詢問 (等問到 =1 才補寫進 txt)</summary>
   public PendingReadyRequest PendingReady;
   /// <summary>最後一次收到或發送 PLC 回應的時間 (逾時檢查用)</summary>
   public DateTime LastActiveTime = DateTime.Now;
   /// <summary>已讀到的 plcData 結果 (JSON 字串，全部讀完再一次送 WS)</summary>
   public List<string> DataResults;
   /// <summary>等待 WRITE 回應的事件 (null = 沒在等寫入回應)</summary>
   public AutoResetEvent WriteReplyEvent;
   /// <summary>最後一次 WRITE 回應的結束代碼 (0 = 成功、-1 = 解析失敗)</summary>
   public int WriteReplyEndCode = -1;
   /// <summary>是否被手動停止 (true = STOP, false = 正常運作/重試中)</summary>
   public bool ManuallyStopped;
   /// <summary>該台 PLC 專屬的分頁 log 表格</summary>
   public DataGridView LogGrid;
  }

  /// <summary>
  /// 輪詢 M7700 的暫存詢問 (封包 + 原本發送時間)：
  /// 等終於問到 M7700=1 時，才把這筆 Request 用「原本的發送時間」補寫進文字檔，與 Response 成對呈現
  /// </summary>
  private class PendingReadyRequest
  {
   /// <summary>送出的詢問封包</summary>
   public byte[] Packet;
   /// <summary>原本發送的時間 (補寫進 txt 用這個時間，不是補寫當下)</summary>
   public DateTime SendTime;
  }
 }
}
