using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
namespace AgyLauncher {
public static class Diagnostics {
    static long requests,input,output,thinking;static readonly object sync=new object();static string category="None";
    static readonly HashSet<string> categories=new HashSet<string>{"Success","Region","Login","Quota","Eligibility","Model","Network","Timeout","Canceled","LocalPermission","RoutingEvidenceMissing","Unknown"};
    public static void Request(){Interlocked.Increment(ref requests);}
    public static void Category(string value){lock(sync)category=categories.Contains(value??"")?value:"Unknown";}
    static long Number(Dictionary<string,object> map,string key){object raw;long value;return map!=null&&map.TryGetValue(key,out raw)&&Int64.TryParse(Convert.ToString(raw),out value)&&value>=0&&value<=1000000000?value:0;}
    public static void Usage(string json){
        try{var obj=Util.Map(Util.Json.DeserializeObject(json));object raw;if(obj==null||!obj.TryGetValue("usage",out raw))return;var usage=Util.Map(raw);
            Interlocked.Add(ref input,Number(usage,"input_tokens"));Interlocked.Add(ref output,Number(usage,"output_tokens"));Interlocked.Add(ref thinking,Number(usage,"thinking_tokens"));
        }catch{}
    }
    public static Dictionary<string,object> Snapshot(){
        string last;lock(sync)last=category;
        return new Dictionary<string,object>{{"schema",1},{"launcherVersion",Bootstrap.Version},{"is64BitProcess",Environment.Is64BitProcess},{"lastCategory",last},{"modelRequestsStarted",Interlocked.Read(ref requests)},
            {"reportedInputTokens",Interlocked.Read(ref input)},{"reportedOutputTokens",Interlocked.Read(ref output)},{"reportedThinkingTokens",Interlocked.Read(ref thinking)},
            {"usageScope","current-launcher-process; CLI-reported values only; missing/failed/canceled requests may have unreported usage; not billing totals"}};
    }
}
public sealed partial class MainForm {
    void SetupDiagnostics(){
        trayMenu.Items.Add("重新配置（需客户端退出）",null,(a,b)=>{
            RestoreFromTray();if(busy){Write("请先保存并正常退出客户端，检测结束后再重新配置；不会强制退出。");return;}
            try{if(Bootstrap.EnsureConfigured(true)){
                var saved=Util.LoadSettings();settings.AppPath=saved.AppPath;settings.CorePath=saved.CorePath;settings.ClashConfigPath=saved.ClashConfigPath;settings.CreateDesktopShortcut=saved.CreateDesktopShortcut;
                Write("配置已保存，下次检测使用新路径。未修改 Clash 或系统代理。");
            }}catch{Write("重新配置未完成，请检查本地文件权限。");}
        });
        trayMenu.Items.Add("导出脱敏诊断报告",null,(a,b)=>{
            RestoreFromTray();using(var dialog=new SaveFileDialog{Filter="诊断报告|*.json",FileName="antigravity-diagnostic.json"})if(dialog.ShowDialog(this)==DialogResult.OK){
                try{File.WriteAllText(dialog.FileName,Util.Json.Serialize(Diagnostics.Snapshot()));Write("已导出固定字段的脱敏诊断：不含路径、节点、IP、账号、Token 或原始日志。CLI 返回的 token 统计不等于计费总额。");}
                catch{Write("诊断报告未保存，请检查目标目录权限。");}
            }
        });
    }
}
}
