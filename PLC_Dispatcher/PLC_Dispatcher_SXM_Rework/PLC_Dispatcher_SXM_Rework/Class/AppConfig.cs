using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json; // 使用 Newtonsoft.Json

namespace PLC_Dispatcher_SXM_Rework
{
 /// <summary>
 /// 管理appsetting.json Class
 /// </summary>
 public class AppConfig
 {
  /// <summary>
  /// WS 呼叫逾時秒數，超過沒回應就當 E (exception) 處理；0 以下用預設 30 秒
  /// </summary>
  public int webServiceTimeoutSeconds { get; set; }
  /// <summary>
  /// WS登入的URL(取得Bearer token)
  /// </summary>
  public string logInWebService { get; set; }
  /// <summary>
  /// WS 驗證結果和執行SP動作的URL
  /// </summary>
  public string spWebService { get; set; }
  /// <summary>
  /// WS 登入帳號
  /// </summary>
  public string userID { get; set; }
  /// <summary>
  /// WS 登入密碼
  /// </summary>
  public string password { get; set; }
  /// <summary>
  /// repairLine (WS @REPAIRLN)
  /// </summary>
  public string repairLine { get; set; }
  /// <summary>
  /// projectCode (WS @PROJCD)
  /// </summary>
  public string projectCode { get; set; }
  /// <summary>
  /// plant (WS @PLANT)
  /// </summary>
  public string plant { get; set; }
  /// <summary>
  /// rhub (WS @RHUB)
  /// </summary>
  public string rhub { get; set; }
  /// <summary>
  /// Log檔保留天數，超過時間就自動刪除 (每天午夜檢查一次；為0 = 不清除)
  /// </summary>
  public int logRetentionDays { get; set; }
  /// <summary>
  /// Ready 輪詢間隔 (ms)：還沒 Ready 的 PLC 每隔這段時間再問一次訊號
  /// </summary>
  public int plcsReadySignalTimer { get; set; }
  /// <summary>
  /// 每個 UI log 分頁最多顯示筆數，超過就從最上面刪掉舊紀錄 (0 以下用預設 60 筆)
  /// </summary>
  public int maxLogLines { get; set; }
  /// <summary>
  /// SN→GUID 目錄中的項目超過幾天就自動清除(避免殘留佔用記憶體)
  /// </summary>
  public int messIdRetentionDays { get; set; }
  /// <summary>
  /// 各站 PLC 設定資訊
  /// </summary>
  public List<PlcConfig> plcs { get; set; }
 }

 /// <summary>
 /// 單一PLC連線設定資訊
 /// </summary>
 public class PlcConfig
 {
  /// <summary>
  /// PLC 編號 (log 檔名用 PLC{id}、WS @PLC_ID)
  /// </summary>
  public int id { get; set; }
  /// <summary>
  /// station (WS @PLC_STATION)
  /// </summary>
  public string station { get; set; }
  /// <summary>
  /// PLC IP (WS @PLC_IP)
  /// </summary>
  public string ip { get; set; }
  /// <summary>
  /// PLC Port (WS @PLC_PORT)
  /// </summary>
  public int port { get; set; }
  /// <summary>
  /// 連線後需讀取PLC點位訊號，如果值等於value才開始讀plcData
  /// </summary>
  public ReadySignalConfig readySignal { get; set; }
  /// <summary>
  /// readySignal成立後，讀取PLC點位回傳給WS的資訊
  /// </summary>
  public List<PlcDataConfig> plcData { get; set; }
  /// <summary>
  /// WS 回應後的資訊，依結果(S/F/E)執行對應的動作
  /// </summary>
  public ActionsConfig actions { get; set; }
 }

 /// <summary>
 /// Ready 訊號設定：讀到此位址的值等於value才開始讀 plcData
 /// </summary>
 public class ReadySignalConfig
 {
  /// <summary>
  /// Ready訊號的 PLC位址 (例：M7700)
  /// </summary>
  public string address { get; set; }
  /// <summary>
  /// 讀到address的值等於value才開始讀plcData
  /// </summary>
  public int value { get; set; }
 }

 /// <summary>
 /// Ready 後要讀取的單筆資料設定
 /// </summary>
 public class PlcDataConfig
 {
  /// <summary>
  /// infoName
  /// </summary>
  public string infoName { get; set; }
  /// <summary>
  /// PLC address
  /// </summary>
  public string address { get; set; }
  /// <summary>
  /// PLC address length
  /// </summary>
  public int length { get; set; }
 }

 /// <summary>
 /// 交握動作清單：依 WS 回應結果分組 (S=success / F=fail / E=exception)
 /// </summary>
 public class ActionsConfig
 {
  /// <summary>
  /// WS 回 S (成功) 時執行的動作
  /// </summary>
  public List<PlcActionConfig> success { get; set; }
  /// <summary>
  /// WS 回 F (失敗) 時執行的動作
  /// </summary>
  public List<PlcActionConfig> fail { get; set; }
  /// <summary>
  /// WS 回 E (例外) 或 WS/WRITE 失敗時執行的動作
  /// </summary>
  public List<PlcActionConfig> exception { get; set; }
 }

 /// <summary>
 /// 單筆交握動作
 /// </summary>
 public class PlcActionConfig
 {
  /// <summary>
  /// 執行動作(WRITE、READ)
  /// </summary>
  public string actionType { get; set; }
  /// <summary>
  /// PLC address
  /// </summary>
  public string address { get; set; }
  /// <summary>
  /// PLC address read/write value
  /// </summary>
  public int value { get; set; }
  /// <summary>
  /// 執行條件：空白=一定執行；有值=需與 WS 回傳的 @actionCondition 相同時才執行
  /// </summary>
  public string condition { get; set; }
 }

 /// <summary>
 /// 讀取 appsetting.json（使用 Newtonsoft.Json 進行嚴格檢查與驗證）
 /// </summary>
 public static class AppConfigLoader
 {
  /// <summary>
  /// 讀取appsetting.json，回傳設定物件，如果欄位拼錯、格式錯誤時拋出例外
  /// </summary>
  public static AppConfig Load()
  {
   string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsetting.json");
   if (!File.Exists(path))
    throw new FileNotFoundException("appsetting.json file not found:\r\npath:" + path);

   string json = File.ReadAllText(path);
   AppConfig config;

   try
   {
    var settings = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error };
    config = JsonConvert.DeserializeObject<AppConfig>(json, settings);
   }
   catch (JsonException ex)
   {
    // 當欄位名稱拼錯、或JSON語法結構/型別錯誤時，會被攔截並拋出詳細錯誤
    throw new InvalidOperationException("AppConfig configuration error: " + ex.Message, ex);
   }
   // 基本完整性驗證
   if (config == null)
    throw new InvalidOperationException("AppConfig deserialization failed, object is null.");
   // 檢查頂層必填字串欄位
   if (string.IsNullOrWhiteSpace(config.logInWebService))
    throw new InvalidOperationException("Configuration error: logInWebService cannot be empty.");
   if (string.IsNullOrWhiteSpace(config.spWebService))
    throw new InvalidOperationException("Configuration error: spWebService cannot be empty.");
   if (string.IsNullOrWhiteSpace(config.userID))
    throw new InvalidOperationException("Configuration error: userID cannot be empty.");
   if (string.IsNullOrWhiteSpace(config.password))
    throw new InvalidOperationException("Configuration error: password cannot be empty.");
   if (string.IsNullOrWhiteSpace(config.repairLine))
    throw new InvalidOperationException("Configuration error: repairLine cannot be empty.");
   if (string.IsNullOrWhiteSpace(config.projectCode))
    throw new InvalidOperationException("Configuration error: projectCode cannot be empty.");
   if (string.IsNullOrWhiteSpace(config.plant))
    throw new InvalidOperationException("Configuration error: plant cannot be empty.");
   if (string.IsNullOrWhiteSpace(config.rhub))
    throw new InvalidOperationException("Configuration error: rhub cannot be empty.");

   // 檢查 plcs 清單
   if (config.plcs == null || config.plcs.Count == 0)
    throw new InvalidOperationException("Configuration error: plcs list cannot be empty.");

   // 檢查每個 PLC 的設定
   foreach (var plc in config.plcs)
   {
    if (string.IsNullOrWhiteSpace(plc.station))
     throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}): station cannot be empty.");
    if (string.IsNullOrWhiteSpace(plc.ip))
     throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): IP address cannot be empty.");
    if (plc.port <= 0 || plc.port > 65535)
     throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): Port value ({plc.port}) is invalid, must be between 1 and 65535.");

    // ReadySignal 檢查
    if (plc.readySignal == null)
     throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): readySignal configuration cannot be null.");
    if (string.IsNullOrWhiteSpace(plc.readySignal.address))
     throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): readySignal.address cannot be empty.");

    // PlcData 檢查
    if (plc.plcData == null || plc.plcData.Count == 0)
     throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): plcData list cannot be empty.");
    foreach (var data in plc.plcData)
    {
     if (string.IsNullOrWhiteSpace(data.infoName))
      throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): plcData infoName cannot be empty.");
     if (string.IsNullOrWhiteSpace(data.address))
      throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): plcData address cannot be empty.");
     if (data.length <= 0)
      throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): plcData ({data.infoName}) length must be greater than 0.");
    }

    // Actions 檢查
    if (plc.actions == null)
     throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): actions configuration cannot be null.");

    // 檢查 success 動作
    if (plc.actions.success != null)
    {
     foreach (var act in plc.actions.success)
     {
      if (string.IsNullOrWhiteSpace(act.actionType))
       throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): actions.success actionType cannot be empty.");
      if (string.IsNullOrWhiteSpace(act.address))
       throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): actions.success address cannot be empty.");
     }
    }

    // 檢查 fail 動作
    if (plc.actions.fail != null)
    {
     foreach (var act in plc.actions.fail)
     {
      if (string.IsNullOrWhiteSpace(act.actionType))
       throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): actions.fail actionType cannot be empty.");
      if (string.IsNullOrWhiteSpace(act.address))
       throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): actions.fail address cannot be empty.");
     }
    }

    // 檢查 exception 動作
    if (plc.actions.exception != null)
    {
     foreach (var act in plc.actions.exception)
     {
      if (string.IsNullOrWhiteSpace(act.actionType))
       throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): actions.exception actionType cannot be empty.");
      if (string.IsNullOrWhiteSpace(act.address))
       throw new InvalidOperationException($"PLC configuration error (ID: {plc.id}, Station: {plc.station}): actions.exception address cannot be empty.");
     }
    }
   }
   return config;
  }
 }
}