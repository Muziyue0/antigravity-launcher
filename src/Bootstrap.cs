using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace AgyLauncher {
public static class Bootstrap {
    public const string Version="1.10.0-beta.1";
    public static string StoreRoot(string installRoot){
        string configured=Environment.GetEnvironmentVariable("AGY_LAUNCHER_DATA");
        if(!String.IsNullOrWhiteSpace(configured)){
            if(!Path.IsPathRooted(configured))throw new InvalidOperationException("AGY_LAUNCHER_DATA 必须是绝对路径。");
            return Path.GetFullPath(configured);
        }
        // Preserve an existing organized installation without copying its private data.
        if(installRoot!=null&&File.Exists(Path.Combine(installRoot,"共享","用户数据","settings.json")))return Path.Combine(installRoot,"共享");
        string identity=Path.GetFullPath(installRoot??Util.AppRoot).TrimEnd('\\').ToUpperInvariant();
        string key;using(var sha=SHA256.Create())key=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-","").Substring(0,12);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AntigravityLauncher","profiles",key);
    }
    public static void Save(Settings settings){
        Validate(settings);Util.PrivateDirectory(Util.Root);
        string file=Path.Combine(Util.Root,"settings.json"),temp=file+".tmp";
        File.WriteAllText(temp,Util.Json.Serialize(settings),new UTF8Encoding(false));
        if(File.Exists(file))File.Replace(temp,file,file+"."+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+".bak");else File.Move(temp,file);
    }
    public static void Validate(Settings s){
        if(s==null||!File.Exists(s.AppPath)||!File.Exists(s.CorePath)||!File.Exists(s.ClashConfigPath))throw new InvalidOperationException("请选取已安装的 Antigravity、可信 Mihomo 内核及有效配置文件。");
        if(!String.Equals(Path.GetFileName(s.AppPath),"Antigravity.exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("桌面程序须为 Antigravity.exe。");
        if(!String.Equals(Path.GetExtension(s.CorePath),".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("内核须为 Windows EXE，请使用你信任的 Mihomo 安装。");
        if(new FileInfo(s.ClashConfigPath).Length>8*1024*1024)throw new InvalidOperationException("代理配置超过 8 MiB，未自动读取。");
        Util.Blocks(File.ReadAllText(s.ClashConfigPath));
    }
    public static Settings Detect(){
        string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var result=new Settings();
        result.AppPath=new[]{Path.Combine(local,"Programs","antigravity","Antigravity.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Antigravity","Antigravity.exe")}.FirstOrDefault(File.Exists)??"";
        var roots=new List<string>();
        foreach(string processName in new[]{"clash-verge","mihomo","verge-mihomo","verge-mihomo-alpha"})
            foreach(var p in Process.GetProcessesByName(processName))using(p){try{roots.Add(Path.GetDirectoryName(p.MainModule.FileName));}catch{}}
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Clash Verge"));
        result.CorePath=roots.Distinct().SelectMany(dir=>new[]{"verge-mihomo.exe","verge-mihomo-alpha.exe","mihomo.exe"}.Select(n=>Path.Combine(dir,n))).FirstOrDefault(File.Exists)??"";
        result.ClashConfigPath=new[]{Path.Combine(roaming,"io.github.clash-verge-rev.clash-verge-rev","clash-verge.yaml"),Path.Combine(roaming,"clash-verge","clash-verge.yaml"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".config","mihomo","config.yaml")}.FirstOrDefault(File.Exists)??"";
        return result;
    }
    public static bool EnsureConfigured(bool force){
        if(!force&&File.Exists(Path.Combine(Util.Root,"settings.json")))return true;
        using(var setup=new SetupForm())return setup.ShowDialog()==DialogResult.OK;
    }
}
public static class OfficialCli {
    public const string ManifestUrl="https://antigravity-cli-auto-updater-974169037036.us-central1.run.app/manifests/windows_amd64.json";
    public static bool AllowedArtifact(string url){
        Uri uri;return Uri.TryCreate(url,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.Host=="storage.googleapis.com"&&uri.Port==443&&uri.UserInfo==""&&uri.Query==""&&uri.Fragment==""&&uri.AbsolutePath.StartsWith("/antigravity-public/antigravity-cli/",StringComparison.Ordinal)&&uri.AbsolutePath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase);
    }
    public static async Task Download(IProgress<string> progress,CancellationToken ct){
        Util.PrivateDirectory(Util.CliRoot);string stage=Path.Combine(Util.CliRoot,"download-"+Guid.NewGuid().ToString("N")+".tmp");
        string manifestStage=stage+".json",exe=Path.Combine(Util.CliRoot,"agy.exe"),manifestFile=Path.Combine(Util.CliRoot,"official-manifest.json");
        string backup=stage+".exe.bak",manifestBackup=stage+".json.bak";bool exeCommitted=false,manifestCommitted=false;
        try{
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            using(var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(8)}){
                progress.Report("从官方更新服务获取 CLI 清单（不会执行安装脚本）……");
                string json;
                using(var response=await client.GetAsync(ManifestUrl,HttpCompletionOption.ResponseHeadersRead,ct)){
                    response.EnsureSuccessStatusCode();if(response.StatusCode!=HttpStatusCode.OK)throw new InvalidOperationException();
                    if(response.Content.Headers.ContentLength>1024*1024)throw new InvalidOperationException();
                    using(var stream=await response.Content.ReadAsStreamAsync())using(var bytes=new MemoryStream()){
                        var buffer=new byte[8192];int count;while((count=await stream.ReadAsync(buffer,0,buffer.Length,ct))>0){if(bytes.Length+count>1024*1024)throw new InvalidOperationException();bytes.Write(buffer,0,count);}json=Encoding.UTF8.GetString(bytes.ToArray());
                    }
                }
                var data=Util.Map(Util.Json.DeserializeObject(json));string url=Util.Str(data,"url"),hash=Util.Str(data,"sha512");
                if(!AllowedArtifact(url)||!Regex.IsMatch(hash,"^[A-Fa-f0-9]{128}$"))throw new InvalidOperationException("官方清单格式或下载地址变化，已停止；请核对官方安装文档。");
                progress.Report("下载官方 CLI，保留 HTTPS 证书校验；可随时取消……");
                using(var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct)){
                    response.EnsureSuccessStatusCode();if(response.StatusCode!=HttpStatusCode.OK)throw new InvalidOperationException();
                    using(var input=await response.Content.ReadAsStreamAsync())using(var output=File.Create(stage)){
                        var buffer=new byte[65536];int count;long total=0;int last=-1;
                        while((count=await input.ReadAsync(buffer,0,buffer.Length,ct))>0){total+=count;if(total>512L*1024*1024)throw new InvalidOperationException("下载超过 512 MiB 上限。");await output.WriteAsync(buffer,0,count,ct);int mb=(int)(total/1024/1024);if(mb/10!=last){last=mb/10;progress.Report("已下载 "+mb+" MiB，完成后校验 SHA-512……");}}
                    }
                }
                using(var sha=SHA512.Create())using(var input=File.OpenRead(stage)){
                    if(!String.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""),hash,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("官方 CLI 哈希不匹配，未安装或执行。");
                }
                ct.ThrowIfCancellationRequested();File.WriteAllText(manifestStage,json,new UTF8Encoding(false));
                if(File.Exists(exe))File.Replace(stage,exe,backup);else File.Move(stage,exe);exeCommitted=true;
                if(File.Exists(manifestFile))File.Replace(manifestStage,manifestFile,manifestBackup);else File.Move(manifestStage,manifestFile);manifestCommitted=true;
                Util.VerifyCli();progress.Report("官方 CLI 已下载并通过 SHA-512 校验；请自行完成官方登录。");
            }
        }catch{
            if(exeCommitted){if(File.Exists(backup))File.Copy(backup,exe,true);else if(File.Exists(exe))File.Delete(exe);}
            if(manifestCommitted){if(File.Exists(manifestBackup))File.Copy(manifestBackup,manifestFile,true);else if(File.Exists(manifestFile))File.Delete(manifestFile);}
            throw;
        }finally{foreach(string path in new[]{stage,manifestStage})try{if(File.Exists(path))File.Delete(path);}catch{}}
    }
}
public sealed class SetupForm:Form {
    readonly TextBox app=new TextBox(),core=new TextBox(),config=new TextBox();readonly Label message=new Label();
    readonly Button save=new Button(),download=new Button();readonly CheckBox shortcut=new CheckBox();CancellationTokenSource stop;
    public SetupForm(){
        Text="Antigravity 启动助手 · 首次配置";ClientSize=new Size(780,430);MinimumSize=Size;Font=new Font("Microsoft YaHei UI",10);StartPosition=FormStartPosition.CenterScreen;
        var intro=new Label{Text="请确认自动发现的路径；仅使用已有可信代理内核。支持内联节点，暂不支持 proxy-providers。",AutoSize=false};intro.SetBounds(18,15,744,48);Controls.Add(intro);
        Settings found;try{found=File.Exists(Path.Combine(Util.Root,"settings.json"))?Util.LoadSettings():Bootstrap.Detect();}catch{found=Bootstrap.Detect();}
        AddPath("Antigravity 主程序",app,found.AppPath,70,"Antigravity.exe|Antigravity.exe");
        AddPath("Mihomo 内核",core,found.CorePath,137,"Windows 程序|*.exe");
        AddPath("当前生效的代理配置",config,found.ClashConfigPath,204,"代理配置|*.yaml;*.yml|全部文件|*.*");
        shortcut.Text="创建桌面快捷方式";shortcut.Checked=found.CreateDesktopShortcut;shortcut.SetBounds(18,275,240,28);Controls.Add(shortcut);
        download.Text="下载并校验官方 CLI";download.SetBounds(280,275,240,36);Controls.Add(download);
        save.Text="保存配置";save.SetBounds(540,275,200,36);Controls.Add(save);
        message.SetBounds(18,325,740,90);message.Text="首次配置只保存程序路径。节点密码不写入设置；检测时在私有临时目录生成配置。\nCLI 检测会消耗模型额度，登录由你自行完成。";Controls.Add(message);
        download.Click+=async(a,b)=>{
            if(stop!=null){stop.Cancel();return;}stop=new CancellationTokenSource();save.Enabled=false;download.Text="取消下载";
            try{await OfficialCli.Download(new Progress<string>(s=>message.Text=s),stop.Token);}
            catch(OperationCanceledException){message.Text="已取消下载，未执行 CLI。";}
            catch{message.Text="官方 CLI 下载或校验失败。请检查网络并核对官方文档；未执行未校验文件。";}
            stop.Dispose();stop=null;save.Enabled=true;download.Text="下载并校验官方 CLI";
        };
        save.Click+=(a,b)=>{
            try{
                var chosen=new Settings{AppPath=app.Text.Trim(),CorePath=core.Text.Trim(),ClashConfigPath=config.Text.Trim(),CreateDesktopShortcut=shortcut.Checked};Bootstrap.Validate(chosen);
                if(!File.Exists(Path.Combine(Util.CliRoot,"agy.exe"))){message.Text="请先下载官方 CLI，然后保存。";return;}Util.VerifyCli();Bootstrap.Save(chosen);DialogResult=DialogResult.OK;Close();
            }catch(InvalidOperationException e){message.Text=e.Message;}catch{message.Text="配置未保存，请检查选定文件和本地权限。";}
        };
        FormClosing+=(a,b)=>{if(stop!=null){b.Cancel=true;stop.Cancel();message.Text="正在取消下载，请等待清理后关闭。";}};
    }
    void AddPath(string title,TextBox box,string value,int y,string filter){
        var label=new Label{Text=title,AutoSize=true};label.SetBounds(18,y,650,24);box.Text=value??"";box.SetBounds(18,y+26,640,28);
        var browse=new Button{Text="浏览"};browse.SetBounds(670,y+24,90,32);browse.Click+=(a,b)=>{using(var file=new OpenFileDialog{Filter=filter,CheckFileExists=true})if(file.ShowDialog(this)==DialogResult.OK)box.Text=file.FileName;};Controls.AddRange(new Control[]{label,box,browse});
    }
}
}
