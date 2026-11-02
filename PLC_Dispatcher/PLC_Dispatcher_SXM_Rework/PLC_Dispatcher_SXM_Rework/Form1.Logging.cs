using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace PLC_Dispatcher_SXM_Rework
{
 /// <summary>
 /// Form1 的日誌與分頁表格 UI (從 Form1.cs 拆出，行為不變)：
 /// 封包/WS/文字紀錄 → UI 表格 + yyyyMMdd_PLC{id}.txt；
 /// 分頁建立、高亮繪製、右鍵複製選單
 /// </summary>
 public partial class Form1 : Form
 {
  /// <summary>記錄送出的封包 (Time + ID + Direction + Description + Hex，一行一筆)</summary>
  /// <param name="station">PLC station 名稱</param>
  /// <param name="id">PLC 編號 (log 檔名用)</param>
  /// <param name="data">送出的原始封包</param>
  private void LogSendPacket(string station, int id, byte[] data)
  {
   // 這筆 Request 的時間戳在這裡就固定 (UI 與 txt 共用同一個時間)
   DateTime now = DateTime.Now;

   var hex = new StringBuilder();

   foreach (byte b in data)
   {
    hex.Append(b.ToString("X2")).Append(' ');
   }

   // 把封包解成人看得懂的描述 (例：READ M7700 length=1)，不是 MC Protocol 封包時為空字串
   string desc = FX5UProtocol.Describe(data);

   // Request = APP 詢問 PLC (後面補空格讓括號與 Response 對齊)
   string[] cells = new string[]
   {
             now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
             id.ToString(),
             "Request  (APP->PLC)",
             desc,
             hex.ToString()
   };

   AppendSendLog(station, cells);

   //    輪詢 M7700 的詢問先暫存不寫檔 (避免洗版)；
   //    等終於問到 M7700=1 時，才把這筆 Request 用原本發送時間補寫進文字檔
   bool isPollingM7700 = desc.Contains("READ M7700");

   if (isPollingM7700)
   {
    lock (plcLock)
    {
     PlcRuntime rt;
     if (dicPLC.TryGetValue(station, out rt))
     {
      rt.PendingReady = new PendingReadyRequest { Packet = data, SendTime = now };
     }
    }
   }
   else
   {
    // 不是常規輪詢 M7700，完整寫入文字檔 (包含 Hex)；檔名用 PLC{id} (id = plc.id)
    // [Request ] 後面補一個空白，跟 [Response] 一樣 10 個字，方便對齊比對
    FileLogger.Write(id, $"[Request ] ID={id} | {desc}", data);
   }
  }

  /// <summary>記錄 WS 呼叫 (Time + ID + Direction + Description，Hex 欄留空)；Description 顯示這次的 SN 和 MESSID，方便對照 DB 的 EXTENDTRANSINFO</summary>
  /// <param name="station">PLC station 名稱</param>
  /// <param name="id">PLC 編號</param>
  /// <param name="sn">這次的 WU_SN</param>
  /// <param name="messId">這次帶入 WS 的 MESSID (GUID)</param>
  /// <param name="result">WS 結果 (此方法固定傳 null)</param>
  private void LogWsPacket(string station, int id, string sn, string messId, string result)
  {
   string desc = "WS SN=" + sn + " MESSID=" + messId + (result == null ? "" : " Result=" + result);

   string[] cells = new string[]
   {
             DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
             id.ToString(),
             "APP->WS",
             desc,
             ""
   };

   AppendSendLog(station, cells);

   // WS 詢問也寫入文字檔 (同一個 yyyyMMdd_PLC{id}.txt，Hex 欄無封包只記文字)
   FileLogger.Write(id, $"[WS Request ] ID={id} | {desc}");
  }

  /// <summary>記錄 WS 回覆 (Time + ID + Direction=WS->APP + Description，Hex 欄留空)；Description 顯示 WS 回傳的三個 output：processFlag / resultMsg / actionCondition</summary>
  /// <param name="station">PLC station 名稱</param>
  /// <param name="id">PLC 編號</param>
  /// <param name="flag">WS 回傳的 processFlag (S/F/E)</param>
  /// <param name="resultMsg">WS 回傳的 resultMsg</param>
  /// <param name="actionCondition">WS 回傳的 actionCondition</param>
  private void LogWsResponse(string station, int id, string flag, string resultMsg, string actionCondition)
  {
   string desc = "processFlag=" + flag + " resultMsg=" + (resultMsg ?? "") + " actionCondition=" + (actionCondition ?? "");

   string[] cells = new string[]
   {
             DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
             id.ToString(),
             "WS->APP",
             desc,
             ""
   };

   AppendSendLog(station, cells);

   // WS 回覆也寫入文字檔 ([WS Response] 與 [WS Request ] 一樣 13 個字，方便對齊比對)
   FileLogger.Write(id, $"[WS Response] ID={id} | {desc}");
  }

  /// <summary>記錄純文字訊息 (例：MESSID 對應、逾時訊息)，訊息放 Description 欄、Hex 欄留空</summary>
  /// <param name="station">PLC station 名稱</param>
  /// <param name="id">PLC 編號</param>
  /// <param name="text">要記錄的文字訊息</param>
  private void LogSendPacket(string station, int id, string text)
  {
   string[] cells = new string[]
   {
             DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
             id.ToString(),
             "",
             text,
             ""
   };

   AppendSendLog(station, cells);
  }

  /// <summary>把一筆紀錄加到該台 PLC 分頁的表格 (一行一筆，Hex 不換行、超出寬度用水平捲軸看)</summary>
  /// <param name="station">PLC station 名稱</param>
  /// <param name="cells">一行五欄的紀錄內容</param>
  private void AppendSendLog(string station, string[] cells)
  {
   if (tabLogs.InvokeRequired)
   {
    tabLogs.BeginInvoke((Action)(() => AppendSendLog(station, cells)));
    return;
   }

   lock (plcLock)
   {
    PlcRuntime rt;
    if (dicPLC.TryGetValue(station, out rt) && rt.LogGrid != null)
    {
     AppendToGrid(rt.LogGrid, cells);
    }
   }
  }

  /// <summary>把一筆紀錄加到指定的表格，超過上限時刪掉最上面的舊紀錄，並捲到最下面</summary>
  /// <param name="grid">目標 DataGridView</param>
  /// <param name="cells">一行五欄的紀錄內容</param>
  private void AppendToGrid(DataGridView grid, string[] cells)
  {
   grid.Rows.Add(cells);

   // 太多就從最上面 (最舊的) 開始刪
   if (grid.Rows.Count > MaxLogLines)
   {
    grid.Rows.RemoveAt(0);
   }

   // 自動捲動到最下面顯示最新一筆
   // (捲動失敗例：剛清空列數的瞬間，屬 UI 小事靜默忽略，不影響功能也不記錄避免洗版)
   if (grid.Rows.Count > 0)
   {
    try { grid.FirstDisplayedScrollingRowIndex = grid.Rows.Count - 1; } catch { }
   }
  }

  /// <summary>依 config 重建 log 分頁：每台 PLC 一頁 (詢問/回覆分開看)</summary>
  private void BuildLogTabs()
  {
   if (tabLogs.InvokeRequired)
   {
    tabLogs.Invoke(new Action(BuildLogTabs));
    return;
   }

   lock (logLock)
   {
    // 移除舊分頁後重建
    while (tabLogs.TabPages.Count > 0)
    {
     TabPage page = tabLogs.TabPages[0];
     tabLogs.TabPages.RemoveAt(0);
     page.Dispose();
    }

    foreach (PlcConfig plc in config.plcs)
    {
     DataGridView grid = NewLogGrid();

     TabPage page = new TabPage(plc.id + ". " + plc.station);
     page.UseVisualStyleBackColor = true;
     page.Controls.Add(grid);

     tabLogs.TabPages.Add(page);

     // 表格存到該台的 PlcRuntime.LogGrid (原本存在 dicLogGrids)
     lock (plcLock)
     {
      PlcRuntime rt;
      if (dicPLC.TryGetValue(plc.station, out rt))
      {
       rt.LogGrid = grid;
      }
     }
    }

    // 套用選中分頁的高亮顏色
    HighlightSelectedTab();
   }
  }

  /// <summary>分頁切換時重新套用高亮顏色</summary>
  /// <param name="sender">事件來源 (tabLogs)</param>
  /// <param name="e">事件資料</param>
  private void tabLogs_SelectedIndexChanged(object sender, EventArgs e)
  {
   HighlightSelectedTab();
  }

  /// <summary>選中的分頁標題改成明顯的高亮 (橘色底 + 粗體)，未選中的維持系統預設 (TabControl 原生選中效果只有文字變色，不明顯，所以自己畫)</summary>
  private void HighlightSelectedTab()
  {
   if (tabLogs.InvokeRequired)
   {
    tabLogs.Invoke(new Action(HighlightSelectedTab));
    return;
   }

   tabLogs.DrawMode = TabDrawMode.OwnerDrawFixed;

   for (int i = 0; i < tabLogs.TabPages.Count; i++)
   {
    bool selected = i == tabLogs.SelectedIndex;

    tabLogs.TabPages[i].BackColor = selected
        ? System.Drawing.Color.Orange
        : System.Drawing.Color.WhiteSmoke;
   }

   tabLogs.Invalidate();
  }

  /// <summary>OwnerDraw：自己畫分頁標題，選中的用橘色底 + 粗體白字，未選中的灰底黑字</summary>
  /// <param name="sender">事件來源 (tabLogs)</param>
  /// <param name="e">繪製事件資料</param>
  private void tabLogs_DrawItem(object sender, DrawItemEventArgs e)
  {
   Graphics g = e.Graphics;
   TabPage page = tabLogs.TabPages[e.Index];
   bool selected = e.Index == tabLogs.SelectedIndex;

   // 背景：選中橘色、未選中淺灰
   using (SolidBrush bg = new SolidBrush(selected ? System.Drawing.Color.Orange : System.Drawing.Color.WhiteSmoke))
   {
    g.FillRectangle(bg, e.Bounds);
   }

   // 文字：選中白色粗體、未選中黑色
   string title = page.Text;
   using (Font font = new Font(tabLogs.Font, selected ? FontStyle.Bold : FontStyle.Regular))
   using (SolidBrush fg = new SolidBrush(selected ? System.Drawing.Color.Black : System.Drawing.Color.DimGray))
   {
    StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    g.DrawString(title, font, fg, e.Bounds, sf);
   }
  }

  /// <summary>建立分頁內的 log 表格 (一行一筆紀錄；欄位不可排序、Hex 不換行，超出寬度出現水平捲軸)。複製方式：單格 Ctrl+C；整列點列標題後 Ctrl+C；或直接右鍵複製</summary>
  private DataGridView NewLogGrid()
  {
   var grid = new DataGridView
   {
    Dock = DockStyle.Fill,
    AllowUserToAddRows = false,
    AllowUserToDeleteRows = false,
    AllowUserToResizeRows = false,
    ReadOnly = true,
    MultiSelect = true,
    RowHeadersVisible = true,
    RowHeadersWidth = 26,
    SelectionMode = DataGridViewSelectionMode.RowHeaderSelect,
    ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
    BackgroundColor = System.Drawing.Color.White,
    BorderStyle = BorderStyle.None,
    Font = new System.Drawing.Font("Consolas", 9F),
    RowTemplate = { Height = 20 }
   };

   // 欄位順序：Time、ID、Direction、Description、Hex
   grid.Columns.Add(AddLogColumn("Time", 170));
   grid.Columns.Add(AddLogColumn("ID", 45));
   grid.Columns.Add(AddLogColumn("Direction", 160));
   grid.Columns.Add(AddLogColumn("Description", 160));
   grid.Columns.Add(AddLogColumn("Hex", 300));
   // 右鍵選單：不用先選取，直接在該列上右鍵就能複製
   grid.ContextMenuStrip = BuildLogContextMenu(grid);
   return grid;
  }

  /// <summary>log 表格的右鍵選單：複製整列 / 複製 Hex / 複製內容 / 複製選取範圍</summary>
  /// <param name="grid">套用選單的 DataGridView</param>
  /// <returns>建好的右鍵選單</returns>
  private ContextMenuStrip BuildLogContextMenu(DataGridView grid)
  {
   var menu = new ContextMenuStrip();
   int hitRow = -1;   // 右鍵當下所指的列

   menu.Items.Add("Copy row (Tab separated)", null, (s, e) =>
   {
    if (hitRow < 0) return;

    var cells = new List<string>();
    foreach (DataGridViewCell c in grid.Rows[hitRow].Cells)
    {
     cells.Add(CellText(c));
    }

    Clipboard.SetText(string.Join("\t", cells.ToArray()));
   });

   menu.Items.Add("Copy Hex", null, (s, e) =>
   {
    if (hitRow < 0) return;

    string hex = CellText(grid.Rows[hitRow].Cells[4]);
    if (hex.Length > 0) Clipboard.SetText(hex);
   });

   menu.Items.Add("Copy Description", null, (s, e) =>
   {
    if (hitRow < 0) return;

    string desc = CellText(grid.Rows[hitRow].Cells[3]);
    if (desc.Length > 0) Clipboard.SetText(desc);
   });

   menu.Items.Add(new ToolStripSeparator());

   menu.Items.Add("Copy selection (Ctrl+C)", null, (s, e) =>
   {
    DataObject data = grid.GetClipboardContent();
    if (data != null) Clipboard.SetDataObject(data);
   });

   // 開啟選單前：找出右鍵所指的列；若該列不在選取範圍內，改成只選它
   menu.Opening += (s, e) =>
   {
    hitRow = -1;

    System.Drawing.Point pos = grid.PointToClient(Cursor.Position);
    DataGridView.HitTestInfo hit = grid.HitTest(pos.X, pos.Y);

    if (hit.Type == DataGridViewHitTestType.Cell || hit.Type == DataGridViewHitTestType.RowHeader)
    {
     hitRow = hit.RowIndex;

     if (!grid.Rows[hitRow].Selected)
     {
      grid.ClearSelection();
      grid.Rows[hitRow].Selected = true;
     }
    }
    else
    {
     e.Cancel = true;   // 不是點在資料列上就不顯示選單
    }
   };

   return menu;
  }

  /// <summary>儲存格轉文字 (null → 空字串)</summary>
  /// <param name="cell">目標儲存格</param>
  /// <returns>儲存格文字</returns>
  private static string CellText(DataGridViewCell cell)
  {
   return cell.Value == null ? "" : cell.Value.ToString();
  }

  /// <summary>建立 log 表格的欄位 (固定寬度、不可排序、不自動換行)</summary>
  /// <param name="headerText">欄位標題</param>
  /// <param name="width">欄位寬度 (px)</param>
  /// <returns>建好的欄位</returns>
  private static DataGridViewTextBoxColumn AddLogColumn(string headerText, int width)
  {
   return new DataGridViewTextBoxColumn
   {
    HeaderText = headerText,
    Width = width,
    SortMode = DataGridViewColumnSortMode.NotSortable,
    DefaultCellStyle = { WrapMode = DataGridViewTriState.False }
   };
  }

  /// <summary>記錄收到的回覆封包 (Time + ID + Direction + Description + Hex，一行一筆)；發送和回覆寫在同一個表格，一問一答看得清楚</summary>
  /// <param name="station">PLC station 名稱</param>
  /// <param name="id">PLC 編號 (log 檔名用)</param>
  /// <param name="data">收到的原始封包</param>
  private void LogRecvPacket(string station, int id, byte[] data)
  {
   var hex = new StringBuilder();

   foreach (byte b in data)
   {
    hex.Append(b.ToString("X2")).Append(' ');
   }

   // 把回應解成人看得懂的描述 (例：WRITE OK、READ OK data=...)，解析失敗為空字串
   string desc = FX5UProtocol.DescribeResponse(data);

   // Response = PLC 回覆 APP (括號與 Request 對齊)
   string[] cells = new string[]
   {
             DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
             id.ToString(),
             "Response (PLC->APP)",
             desc,
             hex.ToString()
   };

   AppendSendLog(station, cells);

   // M7700 回傳 0 (尚未 Ready) 不寫入文字檔，避免洗版；暫存的輪詢詢問也一併丟棄
   // 直到讀到 value=1 (Ready) 才記錄，並把「終於問到 Ready 的那筆 Request」一起補寫，一問一答成對呈現
   bool isReadyOff = desc.Contains("READ OK value=0");

   if (isReadyOff)
   {
    lock (plcLock)
    {
     PlcRuntime rt;
     if (dicPLC.TryGetValue(station, out rt))
     {
      rt.PendingReady = null;
     }
    }
   }
   else
   {
    // 終於問到 M7700=1：先補寫暫存的 Request (用原本發送時間)，再寫 Response
    if (desc == "READ OK value=1")
    {
     PendingReadyRequest pending = null;
     lock (plcLock)
     {
      PlcRuntime rt;
      if (dicPLC.TryGetValue(station, out rt))
      {
       pending = rt.PendingReady;
       rt.PendingReady = null;
      }
     }

     if (pending != null)
     {
      FileLogger.Write(id, $"[Request ] ID={id} | {FX5UProtocol.Describe(pending.Packet)}", pending.Packet, pending.SendTime);
     }
    }

    // 檔名用 PLC{id} (id = plc.id)
    FileLogger.Write(id, $"[Response] ID={id} | {desc}", data);
   }
  }
 }
}
