// Antigravity Chinese launcher. No credential-store access; no changes to Clash settings.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyVersion("1.10.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.10.0.0")]

namespace AgyLauncher {
public sealed class Settings {
    public string AppPath, CorePath, ClashConfigPath;
    public int MaxAttempts = 6, HttpTimeoutSeconds = 12, ModelTimeoutSeconds = 75;
    public string Model = "";
    public bool CreateDesktopShortcut;
}
public sealed class State {
    public string LastCliNode, LastDesktopNode, LastCliAt, LastDesktopAt;
    public string RoundKey, RoundStartedAt, LastModel;
    public List<string> RoundAttempted=new List<string>();
    public int Parallelism=3; public bool QuickDefaultsApplied;
}
public sealed class ProbeResult {
    public string Category, Reply;
    public bool Success;
}
public sealed class Capture {
    public int ExitCode;
    public string Out, Err;
    public HashSet<string> Processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
public sealed class WebResult { public int Status; public string Body; }
public enum ClientLife { Alive, Exited, Unknown }
public sealed class Job : IDisposable {
    IntPtr handle;
    [StructLayout(LayoutKind.Sequential)] struct Basic {
        public long ProcessTime, JobTime; public uint Flags;
        public UIntPtr MinWorking, MaxWorking; public uint Active;
        public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong a,b,c,d,e,f; }
    [StructLayout(LayoutKind.Sequential)] struct Extended {
        public Basic Basic; public Io Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr sa, string name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public Job() {
        handle=CreateJobObject(IntPtr.Zero,null);
        if(handle==IntPtr.Zero) throw new InvalidOperationException("无法建立辅助进程清理作业。");
        var info=new Extended(); info.Basic.Flags=0x2000; // KILL_ON_JOB_CLOSE
        int size=Marshal.SizeOf(info); IntPtr p=Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(info,p,false);
            if(!SetInformationJobObject(handle,9,p,(uint)size)) throw new InvalidOperationException("无法设置辅助进程自动清理。");
        } catch { Dispose(); throw; } finally { Marshal.FreeHGlobal(p); }
    }
    public void Add(Process p) {
        if(!AssignProcessToJobObject(handle,p.Handle)) {
            try { if(!p.HasExited)p.Kill(); } catch {}
            throw new InvalidOperationException("无法约束辅助进程生命周期；已停止启动。");
        }
    }
    public void Dispose(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
}
public static class Util {
    public static JavaScriptSerializer Json {get{return new JavaScriptSerializer { MaxJsonLength=8*1024*1024 };}}
    public static string Quote(string s) { return "\""+s.Replace("\"","\\\"")+"\""; }
    public static readonly string AppRoot=AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static readonly string InstallRoot=ResolveInstallRoot();
    public static readonly string StoreRoot=Bootstrap.StoreRoot(InstallRoot);
    public static readonly string Root=Path.Combine(StoreRoot,"用户数据");
    public static string CliRoot {get{return Path.Combine(StoreRoot,InstallRoot!=null&&StoreRoot==Path.Combine(InstallRoot,"共享")?"运行组件":"runtime");}}
    public static string LogsRoot {get{return Path.Combine(StoreRoot,"logs");}}
    public static string TempRoot {get{return Path.Combine(StoreRoot,"private-runtime");}}
    public static string ReportRoot {get{string path=Path.Combine(StoreRoot,"reports",Bootstrap.Version);Directory.CreateDirectory(path);return path;}}
    static string ResolveInstallRoot(){
        if(!File.Exists(Path.Combine(AppRoot,"layout.json")))return null;
        var parent=Directory.GetParent(AppRoot);
        if(parent==null||(parent.Name!="版本"&&parent.Name!="versions")||parent.Parent==null)throw new InvalidOperationException("版本目录结构不正确，未寻找或更改其他目录。");
        return parent.Parent.FullName;
    }
    public static Settings LoadSettings(){return Json.Deserialize<Settings>(File.ReadAllText(Path.Combine(Root,"settings.json")));}
    public static State LoadState(){try{return Json.Deserialize<State>(File.ReadAllText(Path.Combine(Root,"state.json")))??new State();}catch{return new State();}}
    public static string LastSuccessful(State state){
        DateTime cli,desktop;
        if(!String.IsNullOrEmpty(state.LastCliNode)&&DateTime.TryParse(state.LastCliAt,out cli)&&DateTime.TryParse(state.LastDesktopAt,out desktop)&&cli>desktop)return state.LastCliNode;
        return state.LastDesktopNode??state.LastCliNode;
    }
    public static List<string> PlanBatch(State state,List<string> nodes,int limit,string model,string sourceStamp){
        string signature=String.Join("\n",nodes.OrderBy(x=>x,StringComparer.Ordinal))+"\n"+model+"\n"+sourceStamp;
        string key;using(var hash=System.Security.Cryptography.SHA256.Create())key=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(signature))).Replace("-","");
        if(state.RoundAttempted==null)state.RoundAttempted=new List<string>();
        DateTime began;bool expired=!DateTime.TryParse(state.RoundStartedAt,out began)||DateTime.UtcNow-began.ToUniversalTime()>TimeSpan.FromHours(1);
        if(state.RoundKey!=key||expired||nodes.All(x=>state.RoundAttempted.Contains(x))){state.RoundAttempted.Clear();state.RoundStartedAt=DateTime.UtcNow.ToString("o");}
        state.RoundKey=key;
        var pool=nodes.Where(x=>!state.RoundAttempted.Contains(x)).Distinct().ToList();
        string preferred=LastSuccessful(state);if(preferred!=null&&pool.Remove(preferred))pool.Insert(0,preferred);
        return pool.Take(limit).ToList();
    }
    public static string ParseTermsCountry(string html){
        // Prefer the country-version control before falling back to the complete page text.
        string fragment=html??"";
        var control=Regex.Match(fragment,"<[^>]+(?:data-name|id)=[\"']country-version[\"'][^>]*>(.*?)</(?:button|a|div)>",RegexOptions.IgnoreCase|RegexOptions.Singleline);
        if(control.Success)fragment=control.Groups[1].Value;
        string plain=Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(fragment,"<[^>]+>"," ")),"\\s+"," ").Trim();
        var match=Regex.Match(plain,"Country version:\\s*([A-Za-z][A-Za-z .()'-]{1,59}?)(?=\\s+(?:What[’']s covered|Terms of Service|Effective|Archived)|$)",RegexOptions.IgnoreCase);
        return match.Success?match.Groups[1].Value.Trim():"";
    }
    public static async Task<string> AdvisoryTerms(Func<Task<WebResult>> fetch,CancellationToken ct){
        try{var result=await fetch();return result.Status>=200&&result.Status<300?ParseTermsCountry(result.Body):"";}
        catch(OperationCanceledException){ct.ThrowIfCancellationRequested();return "";}
        catch(HttpRequestException){return "";}
        catch(TimeoutException){return "";}
    }
    public static void SaveState(State s){
        PrivateDirectory(Root);
        string target=Path.Combine(Root,"state.json"), tmp=target+".tmp";
        File.WriteAllText(tmp,Json.Serialize(s),new UTF8Encoding(false));
        if(File.Exists(target))File.Replace(tmp,target,null);else File.Move(tmp,target);
    }
    public static Dictionary<string,object> Map(object o){return o as Dictionary<string,object>;}
    public static string Str(Dictionary<string,object> m,string k){object v;return m!=null&&m.TryGetValue(k,out v)&&v!=null?Convert.ToString(v):"";}
    public static string Category(string text){
        if(Regex.IsMatch(text,"access is denied|permission denied|unauthorizedaccessexception|EACCES",RegexOptions.IgnoreCase))return "LocalPermission";
        if(Regex.IsMatch(text,"User location is not supported|LOCATION_NOT_ELIGIBLE|unsupported.{0,12}(country|region)|location.{0,20}not supported",RegexOptions.IgnoreCase))return "Region";
        if(Regex.IsMatch(text,"invalid_grant|UNAUTHENTICATED|authentication required|not authenticated|not logged in|login required|sign.?in required|no.{0,10}(cached |saved )?credentials|no valid token|token profile.{0,30}(not found|missing)",RegexOptions.IgnoreCase)||HttpCode(text,401))return "Login";
        if(Regex.IsMatch(text,"RESOURCE_EXHAUSTED|quota|rate.limit|credits.{0,20}(exhausted|insufficient)",RegexOptions.IgnoreCase)||HttpCode(text,429))return "Quota";
        if(Regex.IsMatch(text,"ineligible|not eligible|eligibility|age verification|VALIDATION_REQUIRED|PERMISSION_DENIED",RegexOptions.IgnoreCase)||HttpCode(text,403))return "Eligibility";
        if(Regex.IsMatch(text,"unknown model|invalid model|model.{0,30}not found|unsupported model",RegexOptions.IgnoreCase))return "Model";
        if(Regex.IsMatch(text,"timeout|timed out|deadline.exceeded|connection refused|connection reset|no such host|TLS handshake|proxyconnect",RegexOptions.IgnoreCase))return "Network";
        return "Unknown";
    }
    static bool HttpCode(string text,int code){return Regex.IsMatch(text,"(?:\\bHTTP(?:/[0-9.]+)?\\s*[:=]?\\s*|[\"']?(?:code|status|statusCode)[\"']?\\s*[:=]\\s*)"+code+"\\b",RegexOptions.IgnoreCase);}
    public static string Explain(string category){
        switch(category){
        case "LocalPermission":return "本地权限错误：无法创建 CLI 运行目录或文件。停止换节点。";
        case "Login":return "登录缺失或失效：请点击“官方 CLI 登录”，自行完成登录后重新检测。停止换节点。";
        case "Quota":return "模型额度耗尽或请求限流：停止换节点，请等待额度恢复或检查订阅。";
        case "Eligibility":return "账号资格、验证或权限错误：停止换节点，请检查官方账号提示。";
        case "Model":return "模型配置不受支持：请检查模型标识，停止换节点。";
        case "Region":return "真实模型请求被 Google 地区检查拒绝。";
        case "Network":return "请求超时或网络连接失败。";
        default:return "未识别的 CLI 错误；为避免消耗额度，停止自动重试。原始输出未写入公开日志。";
        }
    }
    public static ProbeResult Parse(Capture c){
        Diagnostics.Usage(c.Out);
        try {
            var obj=Json.DeserializeObject(c.Out.Trim()) as Dictionary<string,object>;
            string status=Str(obj,"status"), response=Str(obj,"response").Trim();
            if(c.ExitCode==0&&status=="SUCCESS"&&response=="AGY_OK")return new ProbeResult{Success=true,Category="Success",Reply=response};
            return new ProbeResult{Category=Category(Str(obj,"error")+"\n"+c.Err)};
        }catch{return new ProbeResult{Category=Category(c.Err+"\n"+c.Out)};}
    }
    public static void PrivateDirectory(string path){
        Directory.CreateDirectory(path);
        var acl=new DirectorySecurity(); acl.SetAccessRuleProtection(true,false);
        var user=WindowsIdentity.GetCurrent().User;
        // Preserve owner; only restrict the DACL, avoiding unnecessary WRITE_OWNER access.
        acl.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    public static string Blocks(string yaml){
        // Copy only complete node/group blocks. Never copy TUN, routes, DNS hijacking, controller secrets, or subscriptions.
        var lines=yaml.Replace("\r\n","\n").Split('\n');
        var sb=new StringBuilder(); bool keep=false; bool proxies=false;
        foreach(string line in lines){
            var m=Regex.Match(line,"^([A-Za-z][A-Za-z0-9_-]*):");
            if(m.Success){
                string key=m.Groups[1].Value;
                if(key=="proxy-providers")throw new InvalidOperationException("当前版本仅支持现有内联节点；检测到订阅提供器，已安全停止，不会复制或下载订阅。");
                keep=key=="proxies"||key=="proxy-groups";
                if(key=="proxies")proxies=true;
            }
            if(keep)sb.AppendLine(line);
        }
        if(!proxies)throw new InvalidOperationException("未找到内联代理节点定义。");
        return sb.ToString();
    }
    public static int FreePort(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int p=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return p;}
    public static bool IsCandidate(string name,string type,ISet<string> selectable){
        return selectable.Contains(name)&&!Regex.IsMatch(name,"^(DIRECT|REJECT|REJECT-DROP|PASS|PASS-RULE|COMPATIBLE|GLOBAL)$",RegexOptions.IgnoreCase)
            &&!Regex.IsMatch(type,"^(Direct|Reject|RejectDrop|Compatible|Pass|PassRule|Selector|URLTest|Fallback|LoadBalance|Relay)$",RegexOptions.IgnoreCase);
    }
    public static int NodeRank(string name){
        // Ordering is only a hint. Never infer availability from a name.
        if(Regex.IsMatch(name,"美国|日本|新加坡|台湾|United States|Japan|Singapore|Taiwan|\\b(US|JP|SG|TW)\\b",RegexOptions.IgnoreCase))return 0;
        if(Regex.IsMatch(name,"香港|中国|Hong Kong|China|\\b(HK|CN)\\b",RegexOptions.IgnoreCase))return 2;
        return 1;
    }
    public static void VerifyCli(){
        var manifest=Map(Json.DeserializeObject(File.ReadAllText(Path.Combine(CliRoot,"official-manifest.json"))));
        string expected=Str(manifest,"sha512");
        using(var hash=System.Security.Cryptography.SHA512.Create())using(var input=File.OpenRead(Path.Combine(CliRoot,"agy.exe"))){
            string actual=BitConverter.ToString(hash.ComputeHash(input)).Replace("-","");
            if(!Regex.IsMatch(expected,"^[a-fA-F0-9]{128}$")||!String.Equals(actual,expected,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("官方 CLI 的 SHA-512 与安装清单不符；已停止执行，请重新安装校验。");
        }
    }
    public static void InstallDesktopShortcut(){
        InstallOneShortcut("Antigravity代理检测启动器.lnk","--launch");
        InstallOneShortcut("Antigravity（代理启动）.lnk","--launch");
    }
    static void InstallOneShortcut(string name,string arguments){
        string link=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),name);
        string exe=InstallRoot==null?Process.GetCurrentProcess().MainModule.FileName:Path.Combine(InstallRoot,"Antigravity代理.exe");object shell=null,shortcut=null;
        try{
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            shortcut=shell.GetType().InvokeMember("CreateShortcut",System.Reflection.BindingFlags.InvokeMethod,null,shell,new object[]{link});
            if(File.Exists(link)){
                string current=Convert.ToString(shortcut.GetType().InvokeMember("TargetPath",System.Reflection.BindingFlags.GetProperty,null,shortcut,null));
                if(!String.Equals(current,exe,StringComparison.OrdinalIgnoreCase)){
                    bool owned=String.Equals(Path.GetDirectoryName(current),Root,StringComparison.OrdinalIgnoreCase)&&Regex.IsMatch(Path.GetFileName(current),"^Antigravity启动器(?:-托盘版|-恢复版|-v[0-9.]+)?\\.exe$",RegexOptions.IgnoreCase);
                    string backup=link+".bak";
                    if(File.Exists(backup)){if(!owned)throw new InvalidOperationException("同名快捷方式已有备份，未覆盖。");backup=link+"."+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".bak";}
                    File.Copy(link,backup);
                }
            }
            foreach(var item in new Dictionary<string,string>{{"TargetPath",exe},{"Arguments",arguments},{"WorkingDirectory",InstallRoot??AppRoot},{"IconLocation",exe+",0"},{"Description","真实模型请求验证后启动 Antigravity"}})
                shortcut.GetType().InvokeMember(item.Key,System.Reflection.BindingFlags.SetProperty,null,shortcut,new object[]{item.Value});
            shortcut.GetType().InvokeMember("Save",System.Reflection.BindingFlags.InvokeMethod,null,shortcut,null);
        }finally{if(shortcut!=null)Marshal.FinalReleaseComObject(shortcut);if(shell!=null)Marshal.FinalReleaseComObject(shell);}
    }
    public static bool IsMainClient(string commandLine){
        return !Regex.IsMatch(commandLine??"","(?:^|\\s|\")--(?:type(?:=|\\s)|ms-enable-electron-run-as-node|run-as-node)",RegexOptions.IgnoreCase);
    }
    public static ClientLife ClientStatus(Process process){
        if(process==null)return ClientLife.Unknown;
        try{return process.HasExited?ClientLife.Exited:ClientLife.Alive;}catch{return ClientLife.Unknown;}
    }
    public static bool AppRunning(Settings s){
        // WMI failures are a blocker, not evidence that no client is running.
        using(var query=new ManagementObjectSearcher("SELECT ProcessId,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='Antigravity.exe'"))
        using(var rows=query.Get()){
            foreach(ManagementObject row in rows){string path=Convert.ToString(row["ExecutablePath"]);string commandLine=Convert.ToString(row["CommandLine"]);
                if(!IsMainClient(commandLine))continue;
                if(String.IsNullOrEmpty(path)||String.Equals(path,s.AppPath,StringComparison.OrdinalIgnoreCase))return true;
            }
        }
        return false;
    }
    public static void SetProxy(ProcessStartInfo psi,string proxy){
        foreach(string k in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","http_proxy","https_proxy","all_proxy"})psi.EnvironmentVariables[k]=proxy;
        foreach(string k in new[]{"NO_PROXY","no_proxy"})psi.EnvironmentVariables[k]="localhost,127.0.0.1,::1";
    }
}
public sealed partial class ProxySession : IDisposable {
    public readonly string Dir, Proxy, Controller, Secret;
    public readonly int Port;
    public int CorePid {get {return core==null?0:core.Id;}}
    readonly Job job=new Job();
    Process core; HttpClient api; readonly object sync=new object();
    public readonly HashSet<string> Seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public ProxySession(Settings s){
        try {
        Dir=Path.Combine(Util.TempRoot,Guid.NewGuid().ToString("N"));
        Util.PrivateDirectory(Path.GetDirectoryName(Dir));Util.PrivateDirectory(Dir);
        Port=Util.FreePort(); int control=Util.FreePort();while(control==Port)control=Util.FreePort();
        Proxy="http://127.0.0.1:"+Port; Controller="http://127.0.0.1:"+control;
        Secret=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
        string config="mixed-port: "+Port+"\nallow-lan: false\nbind-address: 127.0.0.1\nmode: global\nlog-level: silent\nipv6: false\nfind-process-mode: always\nexternal-controller: 127.0.0.1:"+control+"\nsecret: "+Secret+"\ntun:\n  enable: false\ndns:\n  enable: false\nprofile:\n  store-selected: false\n"+Util.Blocks(File.ReadAllText(s.ClashConfigPath))+"\nrules:\n  - MATCH,GLOBAL\n";
        File.WriteAllText(Path.Combine(Dir,"config.yaml"),config,new UTF8Encoding(false));
        var psi=new ProcessStartInfo(s.CorePath,"-d "+Util.Quote(Dir)+" -f "+Util.Quote(Path.Combine(Dir,"config.yaml")));
        psi.UseShellExecute=false;psi.CreateNoWindow=true;psi.RedirectStandardOutput=true;psi.RedirectStandardError=true;
        core=new Process{StartInfo=psi};
        try {
            core.Start();job.Add(core);core.OutputDataReceived+=(a,b)=>{};core.ErrorDataReceived+=(a,b)=>{};core.BeginOutputReadLine();core.BeginErrorReadLine();
            api=new HttpClient(new HttpClientHandler{UseProxy=false});api.Timeout=TimeSpan.FromSeconds(3);
            api.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",Secret);
        }catch{Dispose();throw;}
        } catch { Dispose(); throw; }
    }
    public async Task<Dictionary<string,object>> Get(string path,CancellationToken ct){
        var r=await api.GetAsync(Controller+path,ct);r.EnsureSuccessStatusCode();
        return Util.Map(Util.Json.DeserializeObject(await r.Content.ReadAsStringAsync()));
    }
    public async Task Ready(CancellationToken ct){
        DateTime deadline=DateTime.UtcNow.AddSeconds(10);
        for(int i=0;i<50&&DateTime.UtcNow<deadline;i++){
            ct.ThrowIfCancellationRequested();if(core.HasExited)throw new InvalidOperationException("独立 Mihomo 启动失败；配置未写入公开日志。");
            try{await Get("/version",ct);return;}catch(HttpRequestException){}catch(TaskCanceledException){ct.ThrowIfCancellationRequested();}
            await Task.Delay(150,ct);
        }
        throw new InvalidOperationException("独立代理控制端未能就绪。");
    }
    public async Task<List<string>> Nodes(CancellationToken ct){
        var all=Util.Map((await Get("/proxies",ct))["proxies"]);var list=new List<string>();
        var selector=await Get("/proxies/GLOBAL",ct);object choices;var selectable=new HashSet<string>(StringComparer.Ordinal);
        if(!selector.TryGetValue("all",out choices)||!(choices is IEnumerable))throw new InvalidOperationException("无法读取独立代理可选节点列表，未猜测候选。");
        foreach(object choice in (IEnumerable)choices)selectable.Add(Convert.ToString(choice));
        foreach(var entry in all){var node=Util.Map(entry.Value);string type=Util.Str(node,"type");
            if(!Util.IsCandidate(entry.Key,type,selectable))continue;
            if(Regex.IsMatch(entry.Key,"剩余流量|到期时间|套餐到期|官网地址|订阅更新|流量重置"))continue;
            list.Add(entry.Key);
        }
        return list.OrderBy(Util.NodeRank).ToList();
    }
    public async Task Select(string node,CancellationToken ct){
        var content=new StringContent(Util.Json.Serialize(new {name=node}),Encoding.UTF8,"application/json");
        var r=await api.PutAsync(Controller+"/proxies/GLOBAL",content,ct);r.EnsureSuccessStatusCode();
        // Only our isolated instance is affected.
        var clear=await api.DeleteAsync(Controller+"/connections",ct);clear.EnsureSuccessStatusCode();
    }
    public async Task<WebResult> Fetch(string url,int seconds,CancellationToken ct){
        // Use the Windows-supplied curl/SChannel client rather than legacy Framework HTTPS defaults.
        string curl=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"curl.exe");
        if(!File.Exists(curl))throw new InvalidOperationException("缺少 Windows 自带 curl.exe，已停止网络检测。");
        string marker="AGY_HTTP_STATUS:";
        string args="--silent --show-error --location --max-redirs 3 --proto =https --proto-redir =https --noproxy \"\" --proxy "+Util.Quote(Proxy)+" --connect-timeout 6 --max-time "+seconds+" --url "+Util.Quote(url)+" --write-out "+Util.Quote("\\n"+marker+"%{http_code}");
        var psi=new ProcessStartInfo(curl,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        using(var proc=new Process{StartInfo=psi}){
            proc.Start();job.Add(proc);var output=proc.StandardOutput.ReadToEndAsync();var error=proc.StandardError.ReadToEndAsync();
            var until=DateTime.UtcNow.AddSeconds(seconds+4);
            try{while(!proc.HasExited){ct.ThrowIfCancellationRequested();if(DateTime.UtcNow>until)throw new TimeoutException();await Task.Delay(100,ct);}}
            finally{if(!proc.HasExited)proc.Kill();}
            string text=await output;await error;
            if(proc.ExitCode==28)throw new TimeoutException();
            if(proc.ExitCode!=0)throw new HttpRequestException("Windows HTTPS 请求未成功。");
            int pos=text.LastIndexOf(marker,StringComparison.Ordinal);int status;
            if(pos<0||!Int32.TryParse(text.Substring(pos+marker.Length).Trim(),out status))throw new HttpRequestException("无法解析 HTTP 检测结果。");
            return new WebResult{Status=status,Body=text.Substring(0,pos).TrimEnd()};
        }
    }
    public async Task<HashSet<string>> Connections(CancellationToken ct){
        var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var obj=await Get("/connections",ct);object rows;
        if(obj.TryGetValue("connections",out rows)&&rows is IEnumerable){
            foreach(object row in (IEnumerable)rows){var m=Util.Map(row);if(m==null)continue;
                object raw;if(!m.TryGetValue("metadata",out raw))continue;var meta=Util.Map(raw);
                string path=Util.Str(meta,"processPath"),name=Util.Str(meta,"process");
                string identity=String.IsNullOrEmpty(path)?name:path;
                if(!String.IsNullOrEmpty(identity)){result.Add(identity);lock(sync)Seen.Add(identity);}
            }
        }
        return result;
    }
    public async Task<Capture> RunCli(Settings s,string arguments,int seconds,CancellationToken ct){
        var psi=new ProcessStartInfo(Path.Combine(Util.CliRoot,"agy.exe"),arguments);
        psi.UseShellExecute=false;psi.CreateNoWindow=true;psi.WorkingDirectory=Dir;
        psi.RedirectStandardOutput=true;psi.RedirectStandardError=true;psi.RedirectStandardInput=true;
        Util.SetProxy(psi,Proxy);
        var result=new Capture();
        using(var proc=new Process{StartInfo=psi}){
            proc.Start();Diagnostics.Request();
            // A separate job prevents CLI background servers from surviving a failed test.
            using(var cliJob=new Job()){
                cliJob.Add(proc);proc.StandardInput.Close();
                var output=proc.StandardOutput.ReadToEndAsync();var error=proc.StandardError.ReadToEndAsync();
                var until=DateTime.UtcNow.AddSeconds(seconds);
                try {
                    while(!proc.HasExited){
                        ct.ThrowIfCancellationRequested();if(DateTime.UtcNow>until){proc.Kill();throw new TimeoutException();}
                        try{result.Processes.UnionWith(await Connections(ct));}catch(HttpRequestException){}
                        await Task.Delay(200,ct);
                    }
                    result.ExitCode=proc.ExitCode;
                }finally{cliJob.Dispose();}
                result.Out=await output;result.Err=await error;
            }
        }
        return result;
    }
    public void Dispose(){
        if(api!=null){api.Dispose();api=null;}job.Dispose();
        if(core!=null){try{core.WaitForExit(2000);}catch{}core.Dispose();core=null;}
        if(Dir!=null&&Directory.Exists(Dir)){
            // Delete only this generated GUID directory under our own runtime root.
            string root=Path.GetFullPath(Util.TempRoot)+Path.DirectorySeparatorChar;
            string resolved=Path.GetFullPath(Dir);
            if(resolved.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&Regex.IsMatch(Path.GetFileName(resolved),"^[a-f0-9]{32}$"))
                try{Directory.Delete(resolved,true);}catch{}
        }
    }
}
public sealed partial class MainForm : Form {
    readonly TextBox log=new TextBox(),model=new TextBox();readonly Label status=new Label();
    readonly Button start=new Button(),cancel=new Button(),login=new Button(),confirm=new Button();
    readonly Settings settings; readonly State state; CancellationTokenSource cts;
    ProxySession proxy;bool busy,desktop;string node="";bool observedMain,observedBackend;DateTime desktopStart;
    readonly string logPath;
    readonly LinkLabel resetRound=new LinkLabel();
    readonly Label nodeInfo=new Label();
    readonly NotifyIcon tray=new NotifyIcon();readonly ContextMenuStrip trayMenu=new ContextMenuStrip();
    readonly ToolStripMenuItem trayStatus=new ToolStripMenuItem();
    bool exitRequested,trayNoticeShown,restartRequested;readonly bool preview;
    Process clientProcess;Icon applicationIcon;
    public MainForm(bool trayPreview=false){
        preview=trayPreview;
        Directory.CreateDirectory(Util.LogsRoot);logPath=Path.Combine(Util.LogsRoot,preview?"ui-test.log":"progress.log");
        settings=Util.LoadSettings();state=Util.LoadState();
        Text="Antigravity 代理启动器 v1.10.0-beta.1";Size=new Size(840,620);MinimumSize=new Size(740,560);
        Font=new Font("Microsoft YaHei UI",10);StartPosition=FormStartPosition.CenterScreen;
        status.Text="每次启动重新验证；真实模型测试会消耗少量额度。";status.SetBounds(18,15,780,40);status.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
        nodeInfo.Text="当前节点：尚未检测";nodeInfo.SetBounds(18,57,590,22);nodeInfo.AutoEllipsis=true;nodeInfo.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(nodeInfo);
        if(!state.QuickDefaultsApplied){state.Parallelism=3;state.QuickDefaultsApplied=true;}parallelChoice.Minimum=1;parallelChoice.Maximum=3;parallelChoice.Value=Math.Max(1,Math.Min(3,state.Parallelism));parallelChoice.SetBounds(735,55,55,25);parallelChoice.Anchor=AnchorStyles.Top|AnchorStyles.Right;
        var parallelLabel=new Label{Text="并发数",AutoSize=true};parallelLabel.SetBounds(665,57,65,22);parallelLabel.Anchor=AnchorStyles.Top|AnchorStyles.Right;Controls.Add(parallelLabel);Controls.Add(parallelChoice);
        var label=new Label{Text="CLI 模型标识（留空用 CLI 默认模型；请与桌面模型保持一致）：",AutoSize=true};label.SetBounds(18,82,770,25);
        model.Text=state.LastModel??settings.Model??"";model.SetBounds(18,110,780,30);model.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
        log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;log.SetBounds(18,152,780,323);log.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Bottom;
        start.Text="检测并启动";start.SetBounds(18,490,150,38);start.Anchor=AnchorStyles.Bottom|AnchorStyles.Left;
        cancel.Text="取消检测";cancel.SetBounds(180,490,130,38);cancel.Enabled=false;cancel.Anchor=AnchorStyles.Bottom|AnchorStyles.Left;
        login.Text="官方 CLI 登录";login.SetBounds(322,490,160,38);login.Anchor=AnchorStyles.Bottom|AnchorStyles.Left;
        confirm.Text="已收到桌面真实回复";confirm.SetBounds(494,490,270,38);confirm.Enabled=false;confirm.Anchor=AnchorStyles.Bottom|AnchorStyles.Left;
        Controls.AddRange(new Control[]{status,label,model,log,start,cancel,login,confirm});
        start.Click+=async (a,b)=>await StartRun();cancel.Click+=(a,b)=>{if(recoveryStop!=null)recoveryStop.Cancel();else if(cts!=null)cts.Cancel();};
        login.Click+=async (a,b)=>await Login();confirm.Click+=(a,b)=>ConfirmDesktop();
        try{string iconFile=Path.Combine(Util.AppRoot,"Launcher.ico");applicationIcon=File.Exists(iconFile)?new Icon(iconFile):Icon.ExtractAssociatedIcon(settings.AppPath);}catch{applicationIcon=(Icon)SystemIcons.Application.Clone();}
        Icon=applicationIcon;tray.Icon=applicationIcon;tray.Text=preview?"Antigravity 启动器：托盘预览":"Antigravity 代理启动器";
        trayStatus.Text="状态：待命";trayStatus.Enabled=false;
        trayMenu.Items.Add(trayStatus);trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("显示窗口",null,(a,b)=>RestoreFromTray());
        trayMenu.Items.Add("最小化到托盘",null,(a,b)=>HideToTray());
        trayMenu.Items.Add(new ToolStripSeparator());trayMenu.Items.Add("退出启动器",null,(a,b)=>RequestExit());
        trayMenu.Items.Add("重新检测并启动 Antigravity",null,async(a,b)=>await LaunchOrRecover());
        trayMenu.Items.Add("打开日志文件夹",null,(a,b)=>Process.Start("explorer.exe",Util.Quote(Util.LogsRoot)));
        trayMenu.Items.Add("打开当前版本文件夹",null,(a,b)=>Process.Start("explorer.exe",Util.Quote(Util.AppRoot)));
        var exitLink=new LinkLabel{Text="退出启动器（会关闭独立代理，不结束客户端）",AutoSize=true};
        exitLink.SetBounds(18,538,780,25);exitLink.Anchor=AnchorStyles.Bottom|AnchorStyles.Left;exitLink.LinkClicked+=(a,b)=>RequestExit();Controls.Add(exitLink);
        resetRound.Text="重新从成功节点开始";resetRound.AutoSize=true;resetRound.SetBounds(570,538,220,25);resetRound.Anchor=AnchorStyles.Bottom|AnchorStyles.Right;
        resetRound.LinkClicked+=(a,b)=>{if(busy||preview)return;state.RoundAttempted=new List<string>();state.RoundKey=null;Util.SaveState(state);start.Text="检测并启动";Write("已重置候选轮次。下次优先检测上次成功节点；不会清除成功记录。");};Controls.Add(resetRound);
        SetupRuntimeRecovery();SetupDiagnostics();
        tray.ContextMenuStrip=trayMenu;tray.DoubleClick+=(a,b)=>RestoreFromTray();tray.Visible=true;
        KeyPreview=true;KeyDown+=(a,b)=>{if(b.Control&&b.KeyCode==Keys.Q){b.Handled=true;b.SuppressKeyPress=true;RequestExit();}};
        Resize+=(a,b)=>{if(WindowState==FormWindowState.Minimized)HideToTray();};
        FormClosing+=(a,b)=>{
            if(b.CloseReason==CloseReason.WindowsShutDown||b.CloseReason==CloseReason.ApplicationExitCall){Cleanup();return;}
            if(!exitRequested){b.Cancel=true;HideToTray();return;}
            if(busy){b.Cancel=true;if(cts!=null){cts.Cancel();Write("正在取消并清理，请稍后关闭。");}else Write("请先自行完成并退出官方 CLI 登录窗口，再关闭启动器。");return;}
            Cleanup();
        };
        FormClosed+=(a,b)=>{tray.Visible=false;tray.Dispose();trayMenu.Dispose();if(applicationIcon!=null)applicationIcon.Dispose();};
        Write("启动器 v1.10.0-beta.1 已就绪。仅使用独立代理，不修改 Clash 节点、系统代理、TUN 或订阅。每批最大候选数："+settings.MaxAttempts+"。");
        if(!preview&&settings.CreateDesktopShortcut){try{Util.InstallDesktopShortcut();Write("桌面快捷方式已安装或已存在。");}catch{Write("桌面快捷方式未安装：当前进程无写入权限或存在同名备份冲突。可从本目录直接启动。");}}
        Write("Google 条款页只代表未登录网页的地区识别，不等同于账号归属；真实模型请求才是最终检测。");
        if(preview){Text+="（托盘预览）";start.Enabled=false;login.Enabled=false;model.Enabled=false;resetRound.Enabled=false;parallelChoice.Enabled=false;Write("托盘界面测试模式：不会启动客户端、检测节点或更改桌面快捷方式。");}
    }
    public void RestoreFromTray(){ShowInTaskbar=true;Show();WindowState=FormWindowState.Normal;Activate();}
    public async Task LaunchOrRecover(){
        RestoreFromTray();
        if(preview)return;
        if(desktop&&Util.ClientStatus(clientProcess)!=ClientLife.Exited){Write("客户端仍在运行或状态未知；请保存并正常退出后重试，不会强制重启。");return;}
        if(busy){
            if(desktop&&cts!=null){restartRequested=true;cts.Cancel();Write("检测到原客户端主进程已退出；先清理旧代理，再重新验证并启动。");}
            else Write("正在检测或登录，请等待当前操作结束。");
            return;
        }
        await StartRun();
    }
    public Dictionary<string,bool> ExerciseTray(){
        if(!preview)throw new InvalidOperationException();
        var result=new Dictionary<string,bool>();
        result["periodicModelChecksDefaultOff"]=!periodic.Checked;
        result["manualRecoveryUnavailableWithoutClient"]=!recoverLive.Enabled;
        result["recoveryControlsDoNotOverlapLog"]=recoverLive.Bottom<=log.Top&&periodic.Bottom<=log.Top;
        Close();result["closeHidesWithoutExiting"]=!IsDisposed&&!Visible&&!ShowInTaskbar&&tray.Visible;
        RestoreFromTray();result["restoreShowsWindow"]=Visible&&ShowInTaskbar&&WindowState==FormWindowState.Normal;
        WindowState=FormWindowState.Minimized;result["minimizeHidesToTray"]=!Visible&&!ShowInTaskbar&&tray.Visible;
        ((ToolStripMenuItem)trayMenu.Items[2]).PerformClick();result["trayMenuRestoresWindow"]=Visible&&ShowInTaskbar&&WindowState==FormWindowState.Normal;
        ((ToolStripMenuItem)trayMenu.Items[5]).PerformClick();result["trayMenuExitsWhenIdle"]=IsDisposed&&!tray.Visible;
        return result;
    }
    public async Task<Dictionary<string,bool>> ExerciseRecovery(){
        if(!preview)throw new InvalidOperationException();
        var result=new Dictionary<string,bool>();
        string cmd=Path.Combine(Environment.SystemDirectory,"cmd.exe");
        // Only disposable child processes created by this test are used. Never crash a real client.
        using(var exited=Process.Start(new ProcessStartInfo(cmd,"/d /c exit 7"){UseShellExecute=false,CreateNoWindow=true})){
            await Task.Run(()=>exited.WaitForExit());clientProcess=exited;desktop=true;
            using(var timeout=new CancellationTokenSource(3000)){
                try{await Monitor(timeout.Token);result["exitedMainDetectedWithoutProxyApi"]=!desktop;}catch(OperationCanceledException){result["exitedMainDetectedWithoutProxyApi"]=false;}
            }
            Cleanup();result["staleRoutingEvidenceCleared"]=!observedMain&&!observedBackend&&!confirm.Enabled;
        }
        using(var ownedJob=new Job())using(var child=Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--wait-test"){UseShellExecute=false,CreateNoWindow=true})){
            ownedJob.Add(child);
            using(var independentHandle=Process.GetProcessById(child.Id)){
                clientProcess=child;desktop=true;
                using(var stop=new CancellationTokenSource(200)){
                    try{await Monitor(stop.Token);result["cancelWorksWithUnavailableProxyApi"]=false;}catch(OperationCanceledException){result["cancelWorksWithUnavailableProxyApi"]=true;}
                }
                Cleanup();result["cleanupDoesNotKillLiveClient"]=!independentHandle.HasExited;
            }
        }
        using(var exited=Process.Start(new ProcessStartInfo(cmd,"/d /c exit 9"){UseShellExecute=false,CreateNoWindow=true})){
            await Task.Run(()=>exited.WaitForExit());clientProcess=exited;desktop=true;busy=false;
            RequestExit();result["staleDesktopFlagDoesNotBlockExit"]=IsDisposed;
        }
        return result;
    }
    void HideToTray(){
        ShowInTaskbar=false;Hide();
        if(!trayNoticeShown){trayNoticeShown=true;tray.ShowBalloonTip(3500,"已收起到托盘","启动器仍在运行，代理连接保持。双击托盘图标恢复窗口，右键可退出。",ToolTipIcon.Info);}
    }
    void RequestExit(){
        RestoreFromTray();
        if(desktop&&Util.ClientStatus(clientProcess)!=ClientLife.Exited){
            if(MessageBox.Show(this,"客户端仍在运行，或无法确认其状态。退出启动器会关闭独立代理，客户端网络将中断，但不会结束客户端进程。\n\n建议先保存工作。是否仅退出启动器？","退出并断开独立代理",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        }
        restartRequested=false;
        if(busy){
            if(cts!=null){exitRequested=true;cts.Cancel();Write("正在取消检测并清理，完成后自动退出。");}
            else if(MessageBox.Show(this,"官方 CLI 登录窗口可能仍在运行。退出启动器不会强制关闭该窗口。是否退出启动器？","退出启动器",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes){busy=false;exitRequested=true;Close();}
            return;
        }
        exitRequested=true;Close();
    }
    void Write(string s){string line=DateTime.Now.ToString("HH:mm:ss")+"  "+s;log.AppendText(line+Environment.NewLine);status.Text=s;
        // Only fixed status messages and a node fingerprint reach disk. Never raw CLI output or config.
        try{File.AppendAllText(logPath,line+Environment.NewLine,new UTF8Encoding(false));}catch{}
    }
    string Fingerprint(string value){using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").Substring(0,10);}
    void Cleanup(){if(proxy!=null){proxy.Dispose();proxy=null;}if(clientProcess!=null){clientProcess.Dispose();clientProcess=null;}desktop=false;observedMain=false;observedBackend=false;confirm.Enabled=false;}
    void Toggle(bool value){busy=value;start.Enabled=!value&&!preview;login.Enabled=!value&&!preview;resetRound.Enabled=!value&&!preview;parallelChoice.Enabled=!value&&!preview;cancel.Enabled=value&&!desktop;model.Enabled=!value&&!preview;trayStatus.Text=desktop?"状态：独立代理运行中":value?"状态：正在检测或登录":"状态：待命";}
    async Task<bool> CheckRunning(){
        try{bool running=await Task.Run(()=>Util.AppRunning(settings));if(running)Write("Antigravity 已在运行。请保存并正常退出后，再点击检测；不会强制结束进程。");return running;}
        catch{Write("无法核实客户端运行状态（进程查询权限受限），已停止。未将查询失败当作“没有运行”。");return true;}
    }
    static string ResultLabel(string key){switch(key){case "Region":return "地区拒绝";case "Network":return "网络失败";case "Timeout":return "超时未确认（可重试）";case "Login":return "登录问题";case "Quota":return "额度或限流";case "Eligibility":return "账号资格";case "Model":return "模型配置";case "RoutingEvidenceMissing":return "路由证据不足";case "LocalPermission":return "本地权限";default:return "未识别错误";}}
    async Task Monitor(CancellationToken ct){
        bool reported=false;int failures=0;bool unknownReported=false;
        while(true){
            ct.ThrowIfCancellationRequested();
            // The retained main-process handle is authoritative. Renderer leftovers and a broken proxy API must not trap the launcher.
            var life=Util.ClientStatus(clientProcess);
            if(life==ClientLife.Exited){
                int code=0;try{code=clientProcess.ExitCode;}catch{}
                desktop=false;confirm.Enabled=false;start.Text="重新检测并启动";
                Write(code==0?"客户端主进程已退出，清理辅助代理。下次请使用“Antigravity（代理启动）”快捷方式。":"客户端主进程异常退出，正在清理辅助代理。请从启动器重新检测并启动，不要直接打开原始客户端图标。");
                break;
            }
            if(life==ClientLife.Unknown&&!unknownReported){unknownReported=true;Write("无法确认主进程状态；不会把残留子进程视为主程序。可通过“退出启动器”主动释放独立代理。");}
            HashSet<string> connections=null;
            try{connections=await proxy.Connections(ct);}catch(OperationCanceledException){ct.ThrowIfCancellationRequested();}catch{}
            if(connections==null){if(failures++==0)Write("暂时无法读取代理路由证据；仍会检查主进程退出，并允许取消或退出启动器。");await Task.Delay(750,ct);continue;}
            failures=0;
            foreach(string p in connections){string name=Path.GetFileName(p);
                if(String.Equals(p,settings.AppPath,StringComparison.OrdinalIgnoreCase)||String.Equals(name,"Antigravity.exe",StringComparison.OrdinalIgnoreCase))observedMain=true;
                if(Regex.IsMatch(name,"^(language_server.*|agy|agy-node)\\.exe$",RegexOptions.IgnoreCase))observedBackend=true;
            }
            if(observedMain&&observedBackend&&(!reported||runtimeEvidenceReset)){reported=true;runtimeEvidenceReset=false;confirm.Enabled=true;Write("已记录桌面主程序及后台进程经过独立代理。收到真实模型回复后，点击“已收到桌面真实回复”。");}
            await Task.Delay(750,ct);
        }
    }
    void ConfirmDesktop(){
        if(!desktop||!observedMain||!observedBackend)return;
        if(MessageBox.Show(this,"请仅在桌面客户端的新对话中已收到模型真实回复后确认。启动器不会把进程存在或端口可用当作成功。\n\n是否已经收到真实回复？","桌面验证",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
        state.LastDesktopNode=node;state.LastDesktopAt=DateTime.UtcNow.ToString("o");Util.SaveState(state);
        Write("桌面真实回复已由用户确认；主程序及后台代理连接证据齐全。当前节点已记住，下次仍重新检测。");confirm.Enabled=false;
    }
    async Task Login(){
        if(busy)return;Toggle(true);
        try {
            await Task.Run(()=>Util.VerifyCli());
            Write("即将打开官方 CLI，请自行完成登录；启动器不读取或展示密钥。登录完成后退出 CLI 窗口，再重新检测。");
            var p=new ProcessStartInfo(Path.Combine(Util.CliRoot,"agy.exe"));p.UseShellExecute=false;p.CreateNoWindow=false;p.WorkingDirectory=Util.Root;
            // Use the existing Windows proxy for login only. Node validation always uses the isolated proxy.
            using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings")){
                if(key!=null&&Convert.ToInt32(key.GetValue("ProxyEnable",0))==1){string v=Convert.ToString(key.GetValue("ProxyServer",""));
                    if(Regex.IsMatch(v,"^(127\\.0\\.0\\.1|localhost):[0-9]+$"))Util.SetProxy(p,"http://"+v);
                }
            }
            using(var child=Process.Start(p)){await Task.Run(()=>child.WaitForExit());}
            state.RoundAttempted=new List<string>();state.RoundKey=null;Util.SaveState(state);
            Write("CLI 交互窗口已退出。请重新检测，不以窗口退出推断登录成功。");
        }catch{Write("无法启动 CLI 登录窗口，请检查安装和本地权限。");}finally{Toggle(false);}
    }
}
public static class Program {
    [STAThread] public static int Main(string[] args){
        // .NET Framework applications targeting older reference assemblies otherwise default to legacy TLS.
        // Enabling TLS 1.2 preserves normal certificate and hostname validation.
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        if(args.Contains("--wait-test")){Thread.Sleep(30000);return 0;}
        try{
        if(args.Contains("--community-test"))return CommunityChecks.Run().GetAwaiter().GetResult();
        if(args.Contains("--runtime-integration-test"))return RuntimeChecks.Run(true).GetAwaiter().GetResult();
        if(args.Contains("--self-test"))return SelfTest();
        if(args.Contains("--batch-test"))return BatchTest().GetAwaiter().GetResult();
        if(args.Contains("--runtime-test"))return RuntimeChecks.Run(false).GetAwaiter().GetResult();
        if(args.Contains("--wait-policy-test"))return WaitChecks.Run().GetAwaiter().GetResult();
        if(args.Contains("--parallel-test"))return ParallelChecks.Run().GetAwaiter().GetResult();
        if(args.Contains("--isolation-test"))return ParallelChecks.Isolation().GetAwaiter().GetResult();
        if(args.Contains("--layout-test"))return LayoutTest();
        if(args.Contains("--tray-test"))return TrayTest();
        if(args.Contains("--recovery-test"))return RecoveryTest();
        if(args.Contains("--proxy-test"))return ProxyTest().GetAwaiter().GetResult();
        if(args.Contains("--lifecycle-test"))return LifecycleTest().GetAwaiter().GetResult();
        }catch(Exception e){File.WriteAllText(Path.Combine(Util.ReportRoot,"test-runner-error.json"),Util.Json.Serialize(new {type=e.GetType().Name,baseType=e.GetBaseException().GetType().Name}));return 1;}
        bool preview=args.Contains("--tray-preview");bool wantLaunch=!preview&&!args.Contains("--show");string suffix=preview?"-Preview":"";
        bool created;using(var mutex=new Mutex(true,@"Local\AntigravityChineseProxyLauncher-4AA982"+suffix,out created)){
            string signalName=@"Local\AntigravityChineseProxyLauncher-Show-4AA982"+suffix;
            if(!created){try{using(var signal=EventWaitHandle.OpenExisting(signalName+(wantLaunch?"-Launch":""))){signal.Set();}return 0;}catch{MessageBox.Show("旧版启动器仍在运行。请先保存并退出客户端，再退出旧启动器后打开新版。若旧启动器卡住，可仅结束旧启动器进程；不要强杀客户端。","需要切换新版");return 2;}}
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try{
                if(!preview&&!Bootstrap.EnsureConfigured(args.Contains("--configure")))return 2;
                using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset,signalName))using(var launchSignal=new EventWaitHandle(false,EventResetMode.AutoReset,signalName+"-Launch"))using(var form=new MainForm(preview)){
                    var handle=form.Handle;
                    var wait=ThreadPool.RegisterWaitForSingleObject(signal,(a,b)=>{try{if(!form.IsDisposed)form.BeginInvoke(new Action(()=>form.RestoreFromTray()));}catch{}},null,Timeout.Infinite,false);
                    var launchWait=ThreadPool.RegisterWaitForSingleObject(launchSignal,(a,b)=>{try{if(!form.IsDisposed)form.BeginInvoke(new Action(async()=>await form.LaunchOrRecover()));}catch{}},null,Timeout.Infinite,false);
                    if(wantLaunch)form.Shown+=(a,b)=>form.BeginInvoke(new Action(async()=>await form.LaunchOrRecover()));
                    try{Application.Run(form);}finally{wait.Unregister(null);launchWait.Unregister(null);}
                }
                return 0;
            }catch{MessageBox.Show("启动器无法初始化，请检查 settings.json 和本地文件权限。未修改代理配置。","启动失败");return 1;}
        }
    }
    static int SelfTest(){
        var tests=new Dictionary<string,bool>();
        tests["region"]=Util.Category("400 User location is not supported for the API use.")=="Region";
        tests["quota"]=Util.Category("RESOURCE_EXHAUSTED quota 429")=="Quota";
        tests["login"]=Util.Category("authentication required")=="Login";
        tests["eligibility"]=Util.Category("VALIDATION_REQUIRED 403")=="Eligibility";
        tests["permission"]=Util.Category("mkdir .gemini: Access is denied.")=="LocalPermission";
        tests["rendererIsNotMain"]=!Util.IsMainClient("Antigravity.exe --type=renderer --no-sandbox");
        tests["utilityIsNotMain"]=!Util.IsMainClient("Antigravity.exe --type=utility");
        tests["normalClientIsMain"]=Util.IsMainClient("Antigravity.exe --proxy-server=http://127.0.0.1:12345");
        tests["real-reply"]=Util.Parse(new Capture{ExitCode=0,Out="{\"status\":\"SUCCESS\",\"response\":\"AGY_OK\\n\"}",Err=""}).Success;
        tests["fake-reply-rejected"]=!Util.Parse(new Capture{ExitCode=1,Out="{\"status\":\"ERROR\",\"response\":\"AGY_OK\"}",Err=""}).Success;
        tests["text-match-rejected"]=!Util.Parse(new Capture{ExitCode=0,Out="debug request: AGY_OK",Err=""}).Success;
        string blocks=Util.Blocks("mixed-port: 123\nsecret: abc\ntun:\n  enable: true\nproxies:\n  - name: test\n    type: socks5\nproxy-groups:\n  - name: test-group\nrules:\n  - MATCH,DIRECT\n");
        tests["isolated-config"]=blocks.Contains("name: test")&&!blocks.Contains("secret:")&&!blocks.Contains("tun:")&&!blocks.Contains("rules:");
        try{Util.Blocks("proxy-providers:\n  example: {}\nproxies: []");tests["provider-safe-stop"]=false;}catch{tests["provider-safe-stop"]=true;}
        File.WriteAllText(Path.Combine(Util.ReportRoot,"self-test.json"),Util.Json.Serialize(tests));return tests.Values.All(x=>x)?0:1;
    }
    static int LayoutTest(){
        var result=new Dictionary<string,bool>();
        result["organizedLayoutDetected"]=Util.InstallRoot!=null;
        result["userDataSeparateFromVersion"]=Util.Root!=Util.AppRoot&&Directory.Exists(Util.Root);
        result["singleSharedCli"]=Util.CliRoot!=Util.AppRoot;
        result["logsSeparateFromPrograms"]=Util.LogsRoot!=Util.AppRoot;
        result["temporaryFilesOutsideVersion"]=!Util.TempRoot.StartsWith(Util.AppRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        result["reportsBelongToCurrentVersion"]=Util.ReportRoot.EndsWith(Bootstrap.Version)&&!Util.ReportRoot.StartsWith(Util.AppRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        result["iconAvailableForCurrentVersion"]=File.Exists(Path.Combine(Util.AppRoot,"Launcher.ico"));
        File.WriteAllText(Path.Combine(Util.ReportRoot,"layout-test.json"),Util.Json.Serialize(result));return result.Values.All(x=>x)?0:1;
    }
    static int TrayTest(){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        Dictionary<string,bool> result=null;
        using(var form=new MainForm(true)){
            form.Shown+=(a,b)=>form.BeginInvoke(new Action(()=>{result=form.ExerciseTray();}));
            Application.Run(form);
        }
        File.WriteAllText(Path.Combine(Util.ReportRoot,"tray-test.json"),Util.Json.Serialize(result));
        return result!=null&&result.Values.All(x=>x)?0:1;
    }
    static async Task<int> BatchTest(){
        var result=new Dictionary<string,bool>();var nodes=Enumerable.Range(1,14).Select(x=>"test-node-"+x).ToList();
        var state=new State{LastCliNode=nodes[9],LastCliAt=DateTime.UtcNow.ToString("o")};
        var first=Util.PlanBatch(state,nodes,6,"model-a","source-a");
        result["firstBatchBoundedAndPrefersSuccess"]=first.Count==6&&first[0]==nodes[9];state.RoundAttempted.AddRange(first);
        var second=Util.PlanBatch(state,nodes,6,"model-a","source-a");
        result["nextBatchDoesNotRepeatFailures"]=second.Count==6&&!second.Any(first.Contains);state.RoundAttempted.AddRange(second);
        var third=Util.PlanBatch(state,nodes,6,"model-a","source-a");
        result["lastBatchContainsOnlyRemaining"]=third.Count==2&&!third.Any(first.Concat(second).Contains);state.RoundAttempted.AddRange(third);
        var restarted=Util.PlanBatch(state,nodes,6,"model-a","source-a");
        result["exhaustedRoundRestartsOnNextRequest"]=restarted.Count==6&&restarted[0]==nodes[9]&&state.RoundAttempted.Count==0;
        state.RoundAttempted.AddRange(restarted);Util.PlanBatch(state,nodes,6,"model-b","source-a");result["modelChangeResetsRound"]=state.RoundAttempted.Count==0;
        state.RoundAttempted.Add(nodes[0]);Util.PlanBatch(state,nodes,6,"model-b","source-b");result["sourceChangeResetsRound"]=state.RoundAttempted.Count==0;
        state.RoundAttempted.Add(nodes[0]);state.RoundStartedAt=DateTime.UtcNow.AddHours(-2).ToString("o");Util.PlanBatch(state,nodes,6,"model-b","source-b");result["oldRoundExpires"]=state.RoundAttempted.Count==0;
        state.LastDesktopNode="older-desktop";state.LastDesktopAt=DateTime.UtcNow.AddDays(-1).ToString("o");result["latestSuccessIsPreferred"]=Util.LastSuccessful(state)==nodes[9];
        string geo=await Util.AdvisoryTerms(async()=>{await Task.Delay(1);throw new HttpRequestException();},CancellationToken.None);result["termsNetworkFailureIsUnknown"]=geo=="";
        geo=await Util.AdvisoryTerms(async()=>{await Task.Delay(1);throw new TimeoutException();},CancellationToken.None);result["termsTimeoutIsUnknown"]=geo=="";
        geo=await Util.AdvisoryTerms(()=>Task.FromResult(new WebResult{Status=503,Body="unavailable"}),CancellationToken.None);result["termsHttpFailureIsUnknown"]=geo=="";
        result["termsCountryControlParses"]=Util.ParseTermsCountry("<button data-name=\"country-version\">Country version: <span>Singapore</span></button>")=="Singapore";
        using(var canceled=new CancellationTokenSource()){
            canceled.Cancel();bool stopped=false;try{await Util.AdvisoryTerms(async()=>{await Task.Delay(1);throw new TaskCanceledException();},canceled.Token);}catch(OperationCanceledException){stopped=true;}result["userCancelStillStops"]=stopped;
        }
        File.WriteAllText(Path.Combine(Util.ReportRoot,"batch-test.json"),Util.Json.Serialize(result));return result.Values.All(x=>x)?0:1;
    }
    static int RecoveryTest(){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        Dictionary<string,bool> result=null;
        using(var form=new MainForm(true)){
            form.Shown+=(a,b)=>form.BeginInvoke(new Action(async()=>{try{result=await form.ExerciseRecovery();}catch(Exception e){File.WriteAllText(Path.Combine(Util.ReportRoot,"recovery-error.json"),Util.Json.Serialize(new {errorType=e.GetType().Name}));Application.Exit();}}));
            Application.Run(form);
        }
        File.WriteAllText(Path.Combine(Util.ReportRoot,"recovery-test.json"),Util.Json.Serialize(result));
        return result!=null&&result.Values.All(x=>x)?0:1;
    }
    static async Task<int> ProxyTest(){
        var report=new Dictionary<string,object>();
        string dir=null;
        try{
            var settings=Util.LoadSettings();
            using(var proxy=new ProxySession(settings)){
                dir=proxy.Dir;await proxy.Ready(CancellationToken.None);var nodes=await proxy.Nodes(CancellationToken.None);report["nodeCount"]=nodes.Count;
                if(nodes.Count==0)throw new InvalidOperationException();
                var attempts=new List<object>();report["attempts"]=attempts;
                for(int i=0;i<Math.Min(nodes.Count,settings.MaxAttempts);i++){
                    await proxy.Select(nodes[i],CancellationToken.None);
                    var attempt=new Dictionary<string,object>();attempt["index"]=i+1;
                    try{
                        {
                            int code=(await proxy.Fetch("https://www.google.com/generate_204",settings.HttpTimeoutSeconds,CancellationToken.None)).Status;attempt["googleStatus"]=code;
                            if(code==204){
                                var terms=(await proxy.Fetch("https://policies.google.com/terms?hl=en",settings.HttpTimeoutSeconds,CancellationToken.None)).Body;
                                string plain=Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(terms,"<[^>]+>"," ")),"\\s+"," ");
                                var match=Regex.Match(plain,"Country version:\\s*(.{1,80}?)\\s+(?:What[’']s covered|Terms of Service|Effective|Archived)",RegexOptions.IgnoreCase);
                                attempt["termsCountry"]=match.Success?match.Groups[1].Value:"Unparsed";attempts.Add(attempt);break;
                            }
                        }
                    }catch(Exception e){attempt["errorType"]=e.GetType().Name;attempt["innerType"]=e.GetBaseException().GetType().Name;attempt["hresult"]=e.GetBaseException().HResult;var socket=e.GetBaseException() as SocketException;if(socket!=null)attempt["socketError"]=socket.SocketErrorCode.ToString();}
                    attempts.Add(attempt);
                }
                report["isolatedController"]=true;
            }
            report["modelVerified"]=false;
        }catch(Exception e){report["errorType"]=e.GetType().Name;report["modelVerified"]=false;}
        report["privateConfigCleaned"]=dir!=null&&!Directory.Exists(dir);
        bool connected=false;object attemptRows;
        if(report.TryGetValue("attempts",out attemptRows))foreach(var raw in (List<object>)attemptRows){var m=raw as Dictionary<string,object>;if(m.ContainsKey("googleStatus")&&Convert.ToInt32(m["googleStatus"])==204)connected=true;}
        report["connectivityVerified"]=connected;
        File.WriteAllText(Path.Combine(Util.ReportRoot,"proxy-test.json"),Util.Json.Serialize(report));return report.ContainsKey("errorType")||!connected?1:0;
    }
    static async Task<int> LifecycleTest(){
        using(var proxy=new ProxySession(Util.LoadSettings())){
            await proxy.Ready(CancellationToken.None);
            File.WriteAllText(Path.Combine(Util.ReportRoot,"lifecycle-test-private.json"),Util.Json.Serialize(new {ParentPid=Process.GetCurrentProcess().Id,CorePid=proxy.CorePid,Directory=proxy.Dir}));
            await Task.Delay(120000);
        }
        return 0;
    }
}
}
