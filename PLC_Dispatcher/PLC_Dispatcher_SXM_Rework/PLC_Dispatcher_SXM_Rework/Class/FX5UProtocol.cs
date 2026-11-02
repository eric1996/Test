using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PLC_Dispatcher_SXM_Rework
{
 /// <summary>
 /// MC Protocol (SLMP) 3E frame 封包工具：組讀取/寫入封包、解析回應、封包描述
 /// </summary>
 public static class FX5UProtocol
 {
  /// <summary>
  /// 3E frame 回應資料起始位置 (前 11 bytes 為標頭 + EndCode)
  /// </summary>
  private const int ResponseDataStart = 11;
  /// <summary>
  /// 組出 MC Protocol (3E frame) 封包
  /// </summary>
  /// <param name="function">McFunction.Read 或 McFunction.Write</param>
  /// <param name="device">元件種類 (M/X/Y=位元、D/W=字)</param>
  /// <param name="startNo">起始元件號碼 (例：7700 代表 M7700)</param>
  /// <param name="count">要讀或寫的點數 (位元元件為點數、字元件為字數)</param>
  /// <param name="writeData">寫入時的資料；讀取時為 null</param>
  /// <returns>組出的封包位元組陣列</returns>
  public static byte[] Build(McFunction function, McDevice device, int startNo, int count, int[] writeData)
  {
   McDeviceInfo info = DeviceInfo(device);
   byte deviceCode = info.DeviceCode;
   bool isBit = info.IsBit;
   // 寫入資料位元組；讀取時無資料
   byte[] data = function == McFunction.Write ? BuildWriteData(isBit, writeData) : new byte[0];
   // Data Length =  (固定前置 12 + 寫入資料長度)
   int dataLength = 12 + data.Length;

   var packet = new List<byte>();
   // SubHeader
   packet.Add(0x50);
   packet.Add(0x00);
   // Network No
   packet.Add(0x00);
   // PLC No
   packet.Add(0xFF);
   // Module I/O No
   packet.Add(0xFF);
   packet.Add(0x03);
   // Module Station No
   packet.Add(0x00);
   // Data Length (2 位元組，低位在前)
   packet.AddRange(ToBytesLE(dataLength, 2));
   // Monitoring timer
   packet.Add(0x00);
   packet.Add(0x00);
   // Command 低位元組(=01，讀寫相同)
   packet.Add(0x01);
   // Command 高位元組 (讀=04、寫=14)
   packet.Add((byte)(function == McFunction.Read ? 0x04 : 0x14));
   // Subcommand 低位元組 (位元Bit=01、字Word=00)
   packet.Add((byte)(isBit ? 0x01 : 0x00));
   // Subcommand 高位元組 (固定 00)
   packet.Add(0x00);
   // Device number 起始元件號碼 (3 位元組，低位在前)，轉成M7700
   packet.AddRange(ToBytesLE(startNo, 3));
   // Device code
   packet.Add(deviceCode);
   // Number of device points 讀寫數量 (2 位元組，低位在前)
   packet.AddRange(ToBytesLE(count, 2));
   // 寫入資料
   packet.AddRange(data);
   return packet.ToArray();
  }
  /// <summary>
  /// 解析 3E frame 回應
  /// </summary>
  /// <param name="response">PLC 回傳的完整封包</param>
  /// <returns>解析結果 (EndCode=0 表示成功)</returns>
  public static McResponse Parse(byte[] response)
  {
   if (response == null || response.Length < ResponseDataStart)
    throw new ArgumentException("Response packet length insufficient");
   // EndCode 在 Header(9) 後面 2 bytes，低位在前
   int endCode = response[9] | (response[10] << 8);
   var result = new McResponse { EndCode = endCode };
   if (endCode == 0 && response.Length > ResponseDataStart)
   {
    byte[] data = new byte[response.Length - ResponseDataStart];
    Buffer.BlockCopy(response, ResponseDataStart, data, 0, data.Length);
    result.Data = data;
   }
   return result;
  }
  /// <summary>
  /// 把 MC Protocol (3E frame) 封包解成人看得懂的指令描述
  /// 例：READ M7700 length=1、WRITE M7700 length=1 value=1
  /// 不是合法的 MC Protocol 封包時回傳空字串
  /// </summary>
  /// <param name="packet">要解析的封包</param>
  /// <returns>指令描述字串</returns>
  public static string Describe(byte[] packet)
  {
   try
   {
    // SubHeader 50 00 開頭且長度足夠才是 MC Protocol 封包
    if (packet == null || packet.Length < 21 || packet[0] != 0x50 || packet[1] != 0x00)
     return "";

    // Command 高位元組 (index 12)：04=讀取、14=寫入
    bool isRead = packet[12] == 0x04;
    // Subcommand 低位元組 (index 13)：01=位元、00=字
    bool isBit = packet[13] == 0x01;
    // 起始元件號碼 (index 15-17，3 bytes 低位在前)
    int startNo = packet[15] | (packet[16] << 8) | (packet[17] << 16);
    // 元件代碼 (index 18) 轉成 M/D/X/Y/W
    string device = DeviceName(packet[18]);
    // 讀寫點數 (index 19-20，2 bytes 低位在前)
    int count = packet[19] | (packet[20] << 8);
    string desc = (isRead ? "READ " : "WRITE ") + device + startNo + " length=" + count;
    // 寫入時把寫入的值也列出來 (index 21 開始)
    if (!isRead && packet.Length > 21)
    {
     var values = new List<string>();

     if (isBit)
     {
      // 位元元件：1 byte 一點，0x10=ON、0x00=OFF
      for (int i = 21; i < packet.Length && values.Count < count; i++)
      {
       values.Add(packet[i] != 0x00 ? "1" : "0");
      }
     }
     else
     {
      // 字元件：2 bytes 一字，低位在前
      for (int i = 21; i + 1 < packet.Length && values.Count < count; i += 2)
      {
       values.Add((packet[i] | (packet[i + 1] << 8)).ToString());
      }
     }
     desc += " value=" + string.Join(",", values);
    }
    return desc;
   }
   catch(Exception ex)
   {
    return ex.Message.ToString();
   }
  }
  /// <summary>
  /// 元件代碼轉成元件名稱 (例：0x90 → M)
  /// </summary>
  /// <param name="deviceCode">MC Protocol 元件代碼</param>
  /// <returns>元件名稱；不認得的代碼回 0xNN 格式</returns>
  private static string DeviceName(byte deviceCode)
  {
   switch (deviceCode)
   {
    case 0x90: return "M";
    case 0xA8: return "D";
    case 0x9C: return "X";
    case 0x9D: return "Y";
    case 0xB4: return "W";
    default: return "0x" + deviceCode.ToString("X2");
   }
  }
  /// <summary>
  /// 把 MC Protocol (3E frame)「回應」封包解成人看得懂的描述，WRITE OK、READ M7700 → 1、ERROR EndCode=1234
  /// </summary>
  /// <param name="packet">要解析的回應封包</param>
  /// <returns>回應描述字串</returns>
  public static string DescribeResponse(byte[] packet)
  {
   try
   {
    // SubHeader D0 00 開頭 (回應封包) 且長度至少要有 EndCode (11 bytes) 才是 MC Protocol 回應
    if (packet == null || packet.Length < 11 || packet[0] != 0xD0 || packet[1] != 0x00)
     return "";

    // EndCode 在 index 9-10，低位在前 (0 = 成功)
    int endCode = packet[9] | (packet[10] << 8);
    if (endCode != 0)
    {
     return "ERROR EndCode=" + endCode.ToString("X4");
    }
    // 成功回應：11 bytes = 寫入回應 (無資料區)
    if (packet.Length == 11)
    {
     return "WRITE OK";
    }
    // 資料區只有 1 byte = 位元元件讀取回應 (例：M7700)，0x10=ON、0x00=OFF
    if (packet.Length == 12)
    {
     return "READ OK value=" + (packet[11] != 0x00 ? "1" : "0");
    }
    // 資料區 2 bytes 以上 = 字元件讀取回應 (例：D2500 的 MB SN)
    // 直接當 ASCII 解出來顯示，控制字元 (\0 等) 移除，只留可列印字元
    var ascii = new StringBuilder();
    for (int i = 11; i < packet.Length; i++)
    {
     char c = (char)packet[i];
     if (c >= 0x20 && c <= 0x7E) ascii.Append(c);
    }
    return "READ OK value=" + ascii.ToString();
   }
   catch(Exception ex)
   {
    return ex.Message.ToString();
   }
  }
  /// <summary>
  /// 取得指定元件的代碼與資料單位類型
  /// </summary>
  /// <param name="device">要查詢的元件</param>
  /// <returns>回傳元件代碼與是否為位元資料 (true:位元、false:字)</returns>
  public static McDeviceInfo DeviceInfo(McDevice device)
  {
   switch (device)
   {
    case McDevice.M: return new McDeviceInfo { DeviceCode = 0x90, IsBit = true };
    case McDevice.D: return new McDeviceInfo { DeviceCode = 0xA8, IsBit = false };
    case McDevice.X: return new McDeviceInfo { DeviceCode = 0x9C, IsBit = true };
    case McDevice.Y: return new McDeviceInfo { DeviceCode = 0x9D, IsBit = true };
    case McDevice.W: return new McDeviceInfo { DeviceCode = 0xB4, IsBit = false };
    default: throw new ArgumentOutOfRangeException("device");
   }
  }
  /// <summary>
  /// 組寫入資料區 (位元元件 1 byte/點、字元件 2 bytes/字，低位在前)
  /// </summary>
  private static byte[] BuildWriteData(bool isBit, int[] writeData)
  {
   if (writeData == null || writeData.Length == 0)
    throw new ArgumentException("Write data cannot be empty");

   var bytes = new List<byte>();
   foreach (int value in writeData)
   {
    if (isBit)
    {
     bytes.Add((byte)(value != 0 ? 0x10 : 0x00)); // 位元 ON=0x10、OFF=0x00
    }
    else
    {
     bytes.AddRange(ToBytesLE(value, 2)); // 字元件低位在前
    }
   }

   return bytes.ToArray();
  }
  /// <summary>
  /// 整數轉位元組陣列 (低位在前)
  /// </summary>
  /// <param name="value">要轉換的整數</param>
  /// <param name="length">輸出位元組數 (例：2 = 2 bytes)</param>
  /// <returns>低位在前的位元組陣列</returns>
  private static byte[] ToBytesLE(int value, int length)
  {
   byte[] bytes = new byte[length];
   for (int i = 0; i < length; i++)
   {
    bytes[i] = (byte)((value >> (8 * i)) & 0xFF);
   }
   return bytes;
  }
  /// <summary>
  /// 組「寫入單點位元」的封包 (例：M7700 寫 1)
  /// </summary>
  /// <param name="device">元件種類 (M/D/X/Y/W)</param>
  /// <param name="startNo">起始元件號碼 (例：7700 代表 M7700)</param>
  /// <param name="value">1 = ON、0 = OFF</param>
  /// <returns>組出的封包位元組陣列</returns>
  public static byte[] BuildBitWrite(McDevice device, int startNo, int value)
  {
   return Build(McFunction.Write, device, startNo, 1, new int[] { value });
  }
 }
 /// <summary>
 /// 功能類型(Read/Write)
 /// </summary>
 public enum McFunction
 {
  Read,   // 讀取(0401)
  Write   // 寫入(1401)
 }
 /// <summary>
 /// 元件代碼(M,D,X,Y,W)
 /// </summary>
 public enum McDevice
 {
  M,  // 位元元件   (代碼 0x90，位元單位)
  D,  // 資料暫存器 (代碼 0xA8，字單位)
  X,  // 輸入接點   (代碼 0x9C，位元單位)
  Y,  // 輸出接點   (代碼 0x9D，位元單位)
  W   // 連結暫存器 (代碼 0xB4，字單位)
 }
 /// <summary>
 /// MC Protocol 回應解析結果
 /// </summary>
 public class McResponse
 {
  /// <summary>
  /// 結束代碼 (0 = 成功)
  /// </summary>
  public int EndCode { get; set; }
  /// <summary>
  /// 回應資料 (EndCode = 0 時才有)
  /// </summary>
  public byte[] Data { get; set; }
 }

 /// <summary>
 /// 元件資訊 (代碼 + 是否為位元)
 /// </summary>
 public class McDeviceInfo
 {
  /// <summary>
  /// MC Protocol 元件代碼 (例：M=0x90、D=0xA8)
  /// </summary>
  public byte DeviceCode { get; set; }
  /// <summary>
  /// 是否為位元元件 (true = 位元、false = 字)
  /// </summary>
  public bool IsBit { get; set; }
 }
}
