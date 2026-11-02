using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace PLC_Dispatcher_SXM_Rework
{
 delegate void receiveEventHandler(Byte[] byteArray, EventArgs e);//接收到資料事件
 delegate void connectionChangedEventHandler(bool connected, EventArgs e);//連線狀態變更事件：connected = true 連線成功、false 斷線
 class SocketClientClass
 {
  public event receiveEventHandler eventReceive;
  public event connectionChangedEventHandler eventConnectionChanged;
  public string ip;
  public int port;
  private Socket socket;
  /// <summary>
  /// 控制接收迴圈
  /// </summary>
  private Thread thread_Receive;
  private volatile bool isRunning;
  /// <summary>
  /// 主動斷線旗標
  /// </summary>
  private volatile bool stopRequested;
  /// <summary>
  ///  是否已通知過 DISCONNECTED (避免重複記錄)
  /// </summary>
  private volatile bool disconnectedNotified;
  /// <summary>
  /// Interlocked 防止重複進入 Connect（0=閒置, 1=連線中）
  /// </summary>
  private int isConnecting = 0;
  private readonly object _lockSocket = new object(); // 保護 socket 物件多執行緒存取
  private const int RetryDelayMs = 2000;      // 重連間隔 ms
  /// <summary>
  /// 這台連線所屬的 PLC 編號 (appsetting.json 的 plcs.id)，
  /// 寫例外日誌時標記來源用，多台 PLC 才分得出是哪一台出錯
  /// </summary>
  private readonly int plcId;

  /// <summary>
  /// 建構子PLC 編號、IP 與 Port
  /// </summary>
  /// <param name="plcId">PLC 編號 (appsetting.json 的 plcs.id，例外日誌標記用)</param>
  /// <param name="ip">PLC IP 位址</param>
  /// <param name="port">PLC Port</param>
  public SocketClientClass(int plcId, string ip, int port)
  {
   this.plcId = plcId;
   this.ip = ip;
   this.port = port;
  }

  /// <summary>
  /// 組例外日誌的來源標籤：類別.方法 + PLC 編號 + IP:Port
  /// (例：SocketClientClass.Connect [PLC1 192.168.1.10:6000])，多台 PLC 才分得出是哪一台
  /// </summary>
  /// <param name="method">發生例外的方法名稱 (例：Connect / Receive / SendData)</param>
  /// <returns>完整來源標籤字串</returns>
  private string LogSource(string method)
  {
   return "SocketClientClass." + method + " [PLC" + plcId + " " + ip + ":" + port + "]";
  }
  /// <summary>
  /// 建立連線；失敗時每 RetryDelayMs 重試一次直到成功或主動斷線
  /// </summary>
  public void Connect()
  {
   // 防止多執行緒同時進入 Connect（例如 Receive 與 SendData 同時觸發）
   if (Interlocked.CompareExchange(ref isConnecting, 1, 0) != 0)
    return;

   try
   {
    stopRequested = false;
    IPEndPoint ipep = new IPEndPoint(IPAddress.Parse(ip), port);

    while (!stopRequested)
    {
     Socket newSocket = null;
     try
     {
      newSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
      newSocket.Connect(ipep);
      newSocket.IOControl(IOControlCode.KeepAliveValues, KeepAlive(1, 3000, 500), null);// 心跳封包，防止 Server 靜默斷線
      // 原子性替換 socket，舊的先關閉
      lock (_lockSocket)
      {
       try { socket?.Close(); } catch { }
       socket = newSocket;
       newSocket = null; // 避免 catch 誤關
      }
      // 確保舊接收執行緒已結束，避免兩條並存
      Thread oldReceive = thread_Receive;
      if (oldReceive != null && oldReceive.IsAlive && oldReceive != Thread.CurrentThread)
      {
       oldReceive.Join(2000);
      }
      // 啟動新接收執行緒
      isRunning = true;
      thread_Receive = new Thread(Receive) { IsBackground = true };
      thread_Receive.Start();
      // 通知外部：連線成功 (Form1 會記錄到 System log)
      disconnectedNotified = false; // 重置旗標，下次斷線才能再通知
      try { eventConnectionChanged?.Invoke(true, EventArgs.Empty); }
      catch (Exception ex) { FileLogger.WriteException(LogSource("eventConnectionChanged(true)"), ex); }

      return; // 連線成功，結束
     }
     catch (Exception ex)
     {
      // 連線失敗，關閉本次嘗試的 socket
      lock (_lockSocket)
      {
       try { newSocket?.Close(); } catch { }
      }

      if (stopRequested) break;

      // 一開始就連不到：第一次失敗先通知一次 DISCONNECTED，
      // 之後持續背景重試 (不重複通知)，成功時再通知 CONNECTED
      if (!disconnectedNotified)
      {
       disconnectedNotified = true;
       // 第一次失敗把例外細節寫入例外日誌 (之後每 2 秒重試不再重複記錄，避免洗版)
       FileLogger.WriteException(LogSource("Connect"), ex);
       try { eventConnectionChanged?.Invoke(false, EventArgs.Empty); }
       catch (Exception ex2) { FileLogger.WriteException(LogSource("eventConnectionChanged(false)"), ex2); }
      }

      Thread.Sleep(RetryDelayMs); // 間隔後重試
     }
    }
   }
   finally
   {
    // 無論成功、失敗或 stopRequested，都釋放旗標
    Interlocked.Exchange(ref isConnecting, 0);
   }
  }
  /// <summary>
  /// 接收執行緒
  /// </summary>
  private void Receive()
  {
   while (isRunning)
   {
    try
    {
     // 取得當下 socket 的本地參照，避免在 Receive 途中被其他執行緒置換
     Socket currentSocket;
     lock (_lockSocket)
     {
      currentSocket = socket;
      if (currentSocket == null) break;
     }

     byte[] byteBuffer = new byte[2048];
     int length = currentSocket.Receive(byteBuffer); // 阻塞等待資料

     if (length > 0)
     {
      byte[] byteData = new byte[length];
      Buffer.BlockCopy(byteBuffer, 0, byteData, 0, length); // 比 for 迴圈效率更高
      eventReceive?.Invoke(byteData, EventArgs.Empty);
     }
     else
     {
      // length == 0：對方正常關閉連線
      break;
     }
    }
    catch (Exception ex)
    {
     // Socket 異常或已關閉 → 跳出，避免忙迴圈吃滿 CPU
     // 主動斷線 (DisConnect 關閉 socket) 造成的例外屬預期不記錄；其他狀況寫入例外日誌
     if (!stopRequested)
     {
      FileLogger.WriteException(LogSource("Receive"), ex);
     }
     break;
    }
   }

   isRunning = false;

   // 通知外部：連線斷開 (Form1 會記錄到 System log)
   if (!disconnectedNotified)
   {
    disconnectedNotified = true;
    try { eventConnectionChanged?.Invoke(false, EventArgs.Empty); }
    catch (Exception ex) { FileLogger.WriteException("SocketClientClass.eventConnectionChanged(false)", ex); }
   }

   // 非主動斷線（stopRequested == false）→ 自動重連
   if (!stopRequested)
   {
    lock (_lockSocket)
    {
     try { socket?.Close(); } catch { }
     socket = null;
    }
    Connect(); // 在接收執行緒上跑，不阻塞 UI
   }
  }

  /// <summary>
  /// 送出資料到 PLC；送失敗且接收執行緒已結束時觸發重連補救
  /// </summary>
  /// <param name="data">要送出的封包</param>
  public void SendData(byte[] data)
  {
   try
   {
    lock (_lockSocket)
    {
     socket?.Send(data);
    }
   }
   catch (Exception ex)
   {
    // Send 失敗：
    // 通常 Receive 執行緒也會同時偵測到斷線並觸發重連
    // 此處僅在 Receive 執行緒已完全結束時作為補救
    // 主動斷線中 (stopRequested) 的送失敗屬預期不記錄；其他狀況寫入例外日誌
    if (!stopRequested)
    {
     FileLogger.WriteException(LogSource("SendData"), ex);
    }

    if (!isRunning && !stopRequested)
    {
     lock (_lockSocket)
     {
      try { socket?.Close(); } catch { }
      socket = null;
     }
     // 用 ThreadPool 避免卡住呼叫端執行緒
     ThreadPool.QueueUserWorkItem(_ => Connect());
    }
   }
  }

  /// <summary>
  /// 主動斷線：關閉 socket、停止接收執行緒與自動重連
  /// </summary>
  public void DisConnect()
  {
   stopRequested = true;   // 讓重連迴圈與 Receive 知道是主動斷線
   isRunning = false;

   lock (_lockSocket)
   {
    try
    {
     socket?.Close(); // 關閉 socket 讓 Receive.Receive() 拋出例外自然結束
     socket = null;
    }
    catch { }
   }

   // 等待接收執行緒結束（不在接收執行緒自身呼叫，避免 Join 自己）
   Thread currentReceive = thread_Receive;
   if (currentReceive != null && currentReceive.IsAlive && currentReceive != Thread.CurrentThread)
   {
    currentReceive.Join(2000);
   }
  }

  /// <summary>
  /// 組 Socket KeepAlive 選項的位元組陣列 (心跳封包設定)
  /// </summary>
  /// <param name="onOff">1 = 啟用、0 = 停用</param>
  /// <param name="keepAliveTime">開始送心跳前的閒置時間 (ms)</param>
  /// <param name="keepAliveInterval">心跳重試間隔 (ms)</param>
  /// <returns>IOControl 用的選項位元組陣列</returns>
  private byte[] KeepAlive(int onOff, int keepAliveTime, int keepAliveInterval)
  {
   uint dummy = 0;
   byte[] inOptionValues = new byte[Marshal.SizeOf(dummy) * 3];
   BitConverter.GetBytes((uint)onOff).CopyTo(inOptionValues, 0);
   BitConverter.GetBytes((uint)keepAliveTime).CopyTo(inOptionValues, Marshal.SizeOf(dummy));//keep-alive間格ms
   BitConverter.GetBytes((uint)keepAliveInterval).CopyTo(inOptionValues, Marshal.SizeOf(dummy) * 2);// 嘗試間格ms
   return inOptionValues;
  }
  /// <summary>
  /// 目前是否已連線 (socket 存在、狀態 Connected 且接收執行緒運行中)
  /// </summary>
  public bool IsConnected
  {
   get { return socket != null && socket.Connected && isRunning; }
  }

  /// <summary>
  /// 本機電腦名稱
  /// </summary>
  public string HostName
  {
   get { return Dns.GetHostName(); }
  }

  /// <summary>
  /// 連線到 PLC 時作業系統實際使用的本機 IP
  ///   1. 優先從已建立的 TCP socket 取得本地端點
  ///   2. 取不到時從網卡列出 IPv4，過濾停用/迴路/虛擬介面，取第一個啟用中的實體 IP
  /// </summary>
  public string LocalIP
  {
   get
   {
    // 1. 優先從已建立的 TCP socket 取得本地端點
    lock (_lockSocket)
    {
     if (socket != null)
     {
      try
      {
       IPEndPoint local = socket.LocalEndPoint as IPEndPoint;
       if (local != null && local.Address != IPAddress.Any)
        return local.Address.ToString();
      }
      catch { }
     }
    }

    // 2. 取不到時，從網卡 (NetworkInterface) 列出這台電腦的 IPv4
    // 過濾掉停用/迴路/虛擬介面，取第一個啟用中的實體 IP
    try
    {
     foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
     {
      if (nic.OperationalStatus != OperationalStatus.Up) continue;             // 停用中的網卡跳過
      if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue; // 127.0.0.1 跳過
      if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;   // 虛擬通道跳過

      foreach (UnicastIPAddressInformation addrInfo in nic.GetIPProperties().UnicastAddresses)
      {
       if (addrInfo.Address.AddressFamily == AddressFamily.InterNetwork &&
           !IPAddress.IsLoopback(addrInfo.Address))
       {
        return addrInfo.Address.ToString();
       }
      }
     }
    }
    catch { }
    return "";
   }
  }
  /// <summary>
  /// 連線到 PLC 時使用的本機 Port
  /// </summary>
  public string LocalPort
  {
   get
   {
    lock (_lockSocket)
    {
     if (socket != null && socket.Connected)
     {
      IPEndPoint local = socket.LocalEndPoint as IPEndPoint;
      if (local != null) return local.Port.ToString();
     }
    }
    return "";
   }
  }
 }
}