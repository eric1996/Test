using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace PLC_Dispatcher_SXM_Rework
{
 /// <summary>
 /// WebService 客戶端
 /// 流程：Login (logInWebService，payload { userId, pwd }) 取得 token → 重複使用
 ///       → SendPlcData (spWebService) 時帶上 token，payload 對應 SP sp_PLC_WriteData 參數
 /// token 生命週期：舊 token 不會被新 login 蓋掉，伺服器端約 1 小時後才失效，
 ///       所以快取 token 重複使用，超過 45 分鐘 (預留 15 分鐘安全邊際) 才重新 login 換新
 /// 併發：POST 不鎖，多台 PLC 讀完可同時送，各自帶當下快取的 token；
 ///       token 換新用 tokenLock 序列化 (double-check)，同時過期只會 login 一次
 /// </summary>
 public class WebServiceClient
 {
  private readonly AppConfig config;
  /// <summary>
  /// WS 逾時 (ms)，由 webServiceTimeoutSeconds 決定
  /// </summary>
  private readonly int timeoutMs;
  /// <summary>
  /// token 鎖：保護「檢查過期 → login → 更新快取不會重複運行」
  /// </summary>
  private readonly object tokenLock = new object();
  // 快取的 token 與取得時間 (Environment.TickCount，重複使用，超過 TokenRefreshAfterMinutes 才換新)
  private string token;
  private int tokenTick = int.MinValue;
  // 伺服器端 token 約 1 小時失效；45 分鐘就主動換新，預留 15 分鐘更新token
  private const int TokenRefreshAfterMinutes = 45;
  // 執行 App 這台設備的 Host Name / IP (對應 SP 的 @SRC_SERVER_NM / @SRC_SERVER_IP)
  private readonly string srcServerName;
  private readonly string srcServerIp;

  public WebServiceClient(AppConfig config)
  {
   this.config = config ?? throw new ArgumentNullException(nameof(config));
   // 沒填或 ≤0 用預設 5分鐘
   this.timeoutMs = config.webServiceTimeoutSeconds > 0 ? config.webServiceTimeoutSeconds * 1000 : 300000;
   this.srcServerName = Environment.MachineName;
   this.srcServerIp = GetLocalIp();
   // https 需要 TLS 1.2 (.NET 4.0 沒有 Tls12 列舉值，直接用數值 3072)
   ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls;
  }
  /// <summary>
  /// 取得本機 IPv4 (對應 SP 的 @SRC_SERVER_IP)
  /// </summary>
  /// <returns>本機 IPv4；抓不到回空字串</returns>
  private static string GetLocalIp()
  {
   try
   {
    IPAddress[] addresses = Dns.GetHostAddresses(Dns.GetHostName());
    foreach (IPAddress ip in addresses)
    {
     if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
      return ip.ToString();
    }
   }
   catch (Exception ex)
   {
    // 本機 IP 抓不到時回空字串，例外仍記錄到例外日誌方便追查
    FileLogger.WriteException("WebServiceClient.GetLocalIp", ex);
   }
   return "";
  }
  /// <summary>
  /// 登入取得 token POST logInWebService，payload { userId, pwd }，回應 data.accessToken 快取重複使用
  /// </summary>
  /// <returns>token</returns>
  public string Login()
  {
   var payload = new Dictionary<string, object>();
   payload["userId"] = config.userID;       // WS 規格欄位名：userId
   payload["pwd"] = config.password;        // WS 規格欄位名：pwd

   var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
   string json = serializer.Serialize(payload);

   string response = HttpPost(BuildUrl(config.logInWebService), json);

   // 從回應取出 token (格式固定：data.accessToken，失敗回 null)
   string newToken = ExtractToken(response);

   if (string.IsNullOrEmpty(newToken))
    throw new InvalidOperationException("Login failed: token not found in response, response=" + response);

   // 快取 token 與取得時間
   token = newToken;
   tokenTick = Environment.TickCount;

   return newToken;
  }
  /// <summary>
  /// appsetting 沒寫 http/https 前綴時補上；URL 沒設定時拋出例外
  /// </summary>
  /// <param name="url">appsetting.json 設定的 URL</param>
  /// <returns>補上前綴後的完整 URL</returns>
  private static string BuildUrl(string url)
  {
   if (string.IsNullOrEmpty(url))
    throw new InvalidOperationException("WS URL not set (check appsetting.json)");
   return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "http://" + url;
  }
  /// <summary>
  /// 從登入回應抓 token，回應格式固定：{ status, success, message, data:{ accessToken, expiredTime } }
  /// </summary>
  /// <param name="response">登入回應內容</param>
  /// <returns>token；登入失敗回 null</returns>
  private static string ExtractToken(string response)
  {
   if (string.IsNullOrEmpty(response)) return null;
   try
   {
    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    var dict = serializer.DeserializeObject(response) as Dictionary<string, object>;
    if (dict == null) return null;

    // 檢查回應狀態：status = 200 且 success = true 才算登入成功
    int status = dict.ContainsKey("status") && int.TryParse(dict["status"].ToString(), out int s) ? s : -1;
    bool success = dict.ContainsKey("success") && dict["success"] != null && Convert.ToBoolean(dict["success"]);
    if (status != 200 || !success) return null;    // 登入失敗 (例：帳密錯)

    // token 在 data.accessToken
    var dataDict = dict.ContainsKey("data") ? dict["data"] as Dictionary<string, object> : null;
    if (dataDict == null) return null;

    object tokenObj;
    if (!dataDict.TryGetValue("accessToken", out tokenObj) || tokenObj == null) return null;

    string t = tokenObj.ToString();
    return string.IsNullOrEmpty(t) ? null : t;
   }
   catch (Exception ex)
   {
    // 回應不是 JSON 或解析失敗，記錄後回 null (Login 會帶著原始回應拋例外)
    FileLogger.WriteSystem("[ExtractToken Warning] " + ex.GetType().Name + ": " + ex.Message);
    return null;
   }
  }
  /// <summary>
  /// 確保有可用的 token：沒有、或快取超過 45 分鐘 (伺服器 1 小時失效) 才重新 login。
  /// double-check：多台同時發現過期時只有一台 login，其他台等它換完直接用新 token；
  /// 已經在送資料的那台拿的是舊 token 參照，不受影響 (舊 token 伺服器端 1 小時才失效)
  /// </summary>
  /// <returns>可用的 token</returns>
  private string EnsureToken()
  {
   // 快速路徑：token 還新，直接回傳 (不進鎖，多台同時送不會互相卡)
   string cached = token;
   int tick = tokenTick;
   if (!string.IsNullOrEmpty(cached) && tick != int.MinValue && Environment.TickCount - tick < TokenRefreshAfterMinutes * 60 * 1000)
    return cached;

   lock (tokenLock)
   {
    // double-check：等鎖期間可能已被其他台換新，再檢查一次
    if (string.IsNullOrEmpty(token) || tokenTick == int.MinValue || Environment.TickCount - tokenTick >= TokenRefreshAfterMinutes * 60 * 1000)
    {
     Login();
    }
    return token;
   }
  }
  /// <summary>
  /// 把 plcData 讀取結果送給 spWebService (對應 SP sp_PLC_WriteData)
  /// payload 欄位對應 SP 參數，WS 再把 @PROCESSFLAG/@PROCESSMSG 回傳
  /// MESSID 由 Form1 的 SN→GUID 目錄決定：第一台產生、其他台依 SN 對應同一個，最後台用完刪除，確保同一片 SN 的所有站帶同一個 MESSID
  /// </summary>
  /// <param name="plc">該台 PLC 的設定</param>
  /// <param name="plcData">讀到的資料 (每筆一個 JSON 字串，對應 @EXTENDTRANSINFO 內容)</param>
  /// <param name="messId">要帶的 MESSID (由 Form1 依 SN 從目錄取得)</param>
  /// <returns>"S" = success , "F" = fail(@PROCESSFLAG)</returns>
  public string SendPlcData(PlcConfig plc, List<string> plcData, string messId, out string resultMsg, out string actionCondition)
  {
   // 防止 plc 本身為 null 造成例外
   if (plc == null)
    throw new ArgumentNullException(nameof(plc), "PlcConfig cannot be null.");

   string plcId = plc.id.ToString();

   // 沒 token 或快取超過 45 分鐘才重新 login (內部 double-check，同時過期只 login 一次)；
   // 拿到 token 後 POST 不鎖，多台可同時送
   string currentToken = EnsureToken();

   // @EXTENDTRANSINFO：把每筆 plcData 的 JSON 串成一個 JSON 陣列字串
   var items = plcData ?? new List<string>();
   string extendTransInfo = "[" + string.Join(",", items.ToArray()) + "]";

   // @SN：從 plcData 取出 WU_SN 的值 (D2500~D2539)
   string wuSn = ExtractInfoValue(items, "WU_SN", plcId);

   // 打包成送給 WS 的 JSON，欄位對應 SP sp_PLC_WriteData 參數
   var payload = new Dictionary<string, object>();
   payload["MessId"] = messId;                     // @MESSID 由 Form1 依 SN 從目錄取得 (第一台產生、其他台對應)
   payload["RepairLN"] = config.repairLine;
   payload["PLCId"] = plcId;
   payload["SN"] = wuSn;
   payload["PLCStation"] = plc.station;
   payload["PLCIP"] = plc.ip;
   payload["PLCPort"] = plc.port;
   payload["Projcd"] = config.projectCode;
   payload["Plant"] = config.plant;
   payload["Rhub"] = config.rhub;
   payload["ExtendtransInfo"] = extendTransInfo;
   payload["SrcServerName"] = srcServerName;
   payload["SrcServerIP"] = srcServerIp;
   payload["UserID"] = config.userID;

   var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
   string json = serializer.Serialize(payload);

   // 完整記錄 WS 帶入的資訊 (URL + payload JSON) 到 yyyyMMdd_PLC{id}.txt，方便對照 DB
   FileLogger.Write(plc.id, "[WS Payload ] ID=" + plcId + " | URL=" + BuildUrl(config.spWebService) + " | " + json);

   try
   {
    // POST 到 spWebService，header 帶上登入取得的 token
    string response = HttpPost(BuildUrl(config.spWebService), json, "Bearer " + currentToken);

    // 解析 WS 回傳的三個 output：processFlag (S/F/E)、resultMsg、actionCondition
    ParseProcessResult(response, plcId, out string flag, out resultMsg, out actionCondition);
    return flag;
   }
   catch (WebException webEx)
   {
    // 記錄HTTP錯誤
    string errorDetails = GetWebExceptionErrorDetails(webEx, plcId);
    resultMsg = "[WebException] " + webEx.Message + (string.IsNullOrEmpty(errorDetails) ? "" : " | Details: " + errorDetails);
    FileLogger.Write(plc.id, "[WS Error   ] ID=" + plcId + " | " + resultMsg);

    actionCondition = "";
    return "F";
   }
   catch (Exception ex)
   {
    resultMsg = "[Exception] " + ex.Message;
    FileLogger.Write(plc.id, "[WS Error   ] ID=" + plcId + " | " + resultMsg);

    actionCondition = "";
    return "F";
   }
  }
  /// <summary>
  /// 從 plcData 的 JSON 字串陣列中找出指定 infoName 的 infoValue
  /// </summary>
  /// <param name="plcData">每筆一個 JSON 字串的資料清單</param>
  /// <param name="infoName">要找的資料名稱 (例：WU_SN)</param>
  /// <param name="plcId">PLC 編號（用於記錄 Log；非數字時例外日誌以 PLC0 標記）</param>
  /// <returns>對應的值；找不到回空字串</returns>
  public static string ExtractInfoValue(List<string> plcData, string infoName, string plcId = "SYSTEM")
  {
   if (plcData == null) return "";

   // plcId 可能是 "SYSTEM" 等非數字，先安全轉換 (Convert.ToInt32("SYSTEM") 會拋 FormatException)
   int logId;
   if (!int.TryParse(plcId, out logId)) logId = 0;

   var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
   foreach (string item in plcData)
   {
    if (string.IsNullOrEmpty(item)) continue;
    try
    {
     var dict = serializer.DeserializeObject(item) as Dictionary<string, object>;
     if (dict == null) continue;

     object name;
     if ((dict.TryGetValue("infoName", out name) || dict.TryGetValue("infoname", out name))
         && name != null
         && string.Equals(name.ToString(), infoName, StringComparison.OrdinalIgnoreCase))
     {
      object val;
      if (dict.TryGetValue("infoValue", out val) || dict.TryGetValue("infovalue", out val))
       return val == null ? "" : val.ToString();
     }
    }
    catch (Exception ex)
    {
     // 不是合法 JSON 時記錄下來，避免例外被默默吞掉
     FileLogger.Write(logId, "[JSON Parse Warning] ExtractInfoValue item failed: " + ex.Message + " | Item=" + item);
    }
   }
   return "";
  }
  /// <summary>
  /// 解析 WS 回傳的三個 output：processFlag (S/F)、resultMsg (訊息)、actionCondition (條件)
  /// 遞迴搜尋整個 JSON 樹，不管包在第幾層的 data 裡都抓得到；
  /// processFlag 拓不到或非 S 開頭視為 "F"，拓不到時把原始回應放進 resultMsg 方便 log 診斷
  /// </summary>
  /// <param name="response">WS 回應內容</param>
  /// <param name="plcId">PLC 編號（用於記錄 Log；非數字時以 PLC0 標記）</param>
  /// <param name="flag">輸出：S 或 F</param>
  /// <param name="resultMsg">輸出：WS 回傳的訊息</param>
  /// <param name="actionCondition">輸出：WS 回傳的條件 (actions 篩選用)</param>
  private static void ParseProcessResult(string response, string plcId, out string flag, out string resultMsg, out string actionCondition)
  {
   flag = "F";
   resultMsg = "";
   actionCondition = "";

   if (string.IsNullOrEmpty(response)) return;

   // plcId 可能是非數字 (例："UNKNOWN_PLC")，先安全轉換 (Convert.ToInt32 會拋 FormatException)
   int logId;
   if (!int.TryParse(plcId, out logId)) logId = 0;

   try
   {
    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    object root = serializer.DeserializeObject(response);

    // WS 回應欄位名固定，直接依名稱抓 (比對不分大小寫)
    string f = FindValueDeep(root, "processFlag");
    resultMsg = FindValueDeep(root, "resultMsg");
    actionCondition = FindValueDeep(root, "actionCondition");

    if (!string.IsNullOrEmpty(f))
    {
     flag = f.Trim().ToUpperInvariant().StartsWith("S") ? "S" : "F";
     return;
    }

    // JSON 解析成功但找不到 processFlag：把原始回應帶出去，方便在 log 看 WS 到底回什麼
    if (string.IsNullOrEmpty(resultMsg))
    {
     resultMsg = "[processFlag not found] raw response=" + response.Trim();
    }
    return;
   }
   catch (Exception ex)
   {
    // 不是 JSON，記錄例外後往下用整個回應粗略判斷
    FileLogger.Write(logId, "[ParseProcessResult Warning] Response is not valid JSON: " + ex.Message + " | Raw=" + response);
   }

   flag = response.Trim().ToUpperInvariant().StartsWith("S") ? "S" : "F";
   if (flag == "F") resultMsg = "[Non-JSON response] raw response=" + response.Trim();
  }
  /// <summary>
  /// 遞迴往下找指定 key 的字串值 (走 Dictionary 與陣列，key 比對不分大小寫)
  /// </summary>
  /// <param name="node">目前走訪的 JSON 節點</param>
  /// <param name="key">要找的 key 名稱</param>
  /// <returns>找到的值；找不到回空字串</returns>
  private static string FindValueDeep(object node, string key)
  {
   var dict = node as Dictionary<string, object>;
   if (dict != null)
   {
    // 先找本層的目標 key (排除物件/陣列，只取純值)
    foreach (var kv in dict)
    {
     if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)
         && kv.Value != null
         && !(kv.Value is Dictionary<string, object>) && !(kv.Value is object[]))
      return kv.Value.ToString();
    }

    // 本層沒有就往子節點找
    foreach (var kv in dict)
    {
     string found = FindValueDeep(kv.Value, key);
     if (!string.IsNullOrEmpty(found)) return found;
    }
    return "";
   }

   var arr = node as object[];
   if (arr != null)
   {
    foreach (object item in arr)
    {
     string found = FindValueDeep(item, key);
     if (!string.IsNullOrEmpty(found)) return found;
    }
   }
   return "";
  }
  /// <summary>
  /// HTTP POST JSON 字串，可帶 Authorization header，回傳回應內容
  /// </summary>
  /// <param name="url">目標 URL</param>
  /// <param name="json">要送的 JSON 內容</param>
  /// <param name="authorization">Authorization header 內容 (例：Bearer token)；可省略</param>
  /// <returns>回應內容字串</returns>
  private string HttpPost(string url, string json, string authorization = null)
  {
   HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
   req.Method = "POST";
   req.ContentType = "application/json";
   req.Timeout = timeoutMs;             // 逾時由 webServicetimeout 換算
   req.ReadWriteTimeout = timeoutMs;

   // 登入取得的 token 放在 Authorization header
   if (!string.IsNullOrEmpty(authorization))
   {
    req.Headers[HttpRequestHeader.Authorization] = authorization;
   }

   byte[] body = Encoding.UTF8.GetBytes(json);
   req.ContentLength = body.Length;

   using (Stream stream = req.GetRequestStream())
   {
    stream.Write(body, 0, body.Length);
   }

   using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
   using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
   {
    return reader.ReadToEnd();
   }
  }

  /// <summary>
  /// 從 WebException 讀取伺服器回傳的詳細錯誤訊息
  /// </summary>
  /// <param name="webEx">WebException 例外物件</param>
  /// <param name="plcId">PLC 編號（用於記錄 Log；非數字時以 PLC0 標記）</param>
  /// <returns>錯誤內文字串；讀取失敗則回傳 null</returns>
  private static string GetWebExceptionErrorDetails(WebException webEx, string plcId)
  {
   // plcId 可能是非數字 (例："UNKNOWN_PLC")，先安全轉換 (Convert.ToInt32 會拋 FormatException)
   int logId;
   if (!int.TryParse(plcId, out logId)) logId = 0;

   try
   {
    if (webEx.Response != null)
    {
     using (var errResp = (HttpWebResponse)webEx.Response)
     using (var reader = new StreamReader(errResp.GetResponseStream(), Encoding.UTF8))
     {
      return reader.ReadToEnd();
     }
    }
   }
   catch (Exception ex)
   {
    // 當伺服器回傳錯誤，但去讀取其錯誤 Body 串流失敗時，把這個例外也記錄下來
    FileLogger.Write(logId, "[Error] Failed to read error response stream: " + ex.Message);
   }
   return null;
  }
 }
}