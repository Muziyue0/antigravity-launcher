using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Windows.Forms;
public sealed class ActiveRelease {public string Version,RelativeExe,Sha256;}
public static class StableEntry {
    public static string Resolve(string root,ActiveRelease release){
        if(release==null||String.IsNullOrWhiteSpace(release.RelativeExe)||Path.IsPathRooted(release.RelativeExe))throw new InvalidOperationException("当前版本记录无效。");
        string versions=Path.GetFullPath(Path.Combine(root,"版本"))+Path.DirectorySeparatorChar;
        string exe=Path.GetFullPath(Path.Combine(root,release.RelativeExe));
        if(!exe.StartsWith(versions,StringComparison.OrdinalIgnoreCase)||!exe.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("版本路径超出允许范围。");
        using(var sha=SHA256.Create())using(var file=File.OpenRead(exe)){
            string actual=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
            if(!String.Equals(actual,release.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("版本文件校验失败，请重新安装该版本。");
        }
        return exe;
    }
    [STAThread] public static int Main(string[] args){
        long entered=Stopwatch.GetTimestamp();
        try{
            string root=AppDomain.CurrentDomain.BaseDirectory;
            var release=new JavaScriptSerializer().Deserialize<ActiveRelease>(File.ReadAllText(Path.Combine(root,"共享","当前版本.json")));
            string exe=Resolve(root,release);bool configure=Array.IndexOf(args,"--configure")>=0;bool show=Array.IndexOf(args,"--show")>=0||configure;
            Process.Start(new ProcessStartInfo(exe,show?"--show"+(configure?" --configure":""):"--launch --start-ticks="+entered){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(exe)});
            return 0;
        }catch(Exception e){MessageBox.Show("启动未完成："+e.Message+"\n\n请使用一键整理安装程序检查安装；不要移动版本目录中的单个 exe。","Antigravity 代理启动",MessageBoxButtons.OK,MessageBoxIcon.Warning);return 1;}
    }
}
