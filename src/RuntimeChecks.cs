using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace AgyLauncher {
public static class RuntimeChecks {
    public static async Task<int> Run(bool live){
        var tests=new Dictionary<string,bool>();
        string now="old";int closes=0;bool verified=false;
        Func<CancellationToken,Task<string>> selected=ct=>Task.FromResult(now);
        Func<string,CancellationToken,Task> select=(n,ct)=>{ct.ThrowIfCancellationRequested();now=n;return Task.FromResult(0);};
        Func<CancellationToken,Task> close=ct=>{closes++;return Task.FromResult(0);};
        var result=await RecoverySwitch.Run("old","new",false,selected,select,ct=>{verified=now=="new";return Task.FromResult(true);},close,CancellationToken.None);
        tests["automaticSwitchPreservesExistingConnections"]=result.Switched&&!result.Reconnected&&closes==0&&verified&&now=="new";
        now="old";result=await RecoverySwitch.Run("old","new",true,selected,select,ct=>Task.FromResult(true),close,CancellationToken.None);
        tests["manualReconnectFollowsModelVerification"]=result.Switched&&result.Reconnected&&closes==1;
        now="old";closes=0;result=await RecoverySwitch.Run("old","new",true,selected,select,ct=>Task.FromResult(false),close,CancellationToken.None);
        tests["failedModelRollsBackWithoutReconnect"]=!result.Switched&&!result.RollbackFailed&&now=="old"&&closes==0;
        now="unexpected";result=await RecoverySwitch.Run("old","new",true,selected,select,ct=>Task.FromResult(true),close,CancellationToken.None);
        tests["changedSelectorAbortsWithoutMutation"]=!result.Switched&&now=="unexpected"&&closes==0;
        now="old";using(var cancel=new CancellationTokenSource()){
            result=await RecoverySwitch.Run("old","new",true,selected,select,ct=>{cancel.Cancel();ct.ThrowIfCancellationRequested();return Task.FromResult(true);},close,cancel.Token);
            tests["cancelAfterPutRollsBackUsingIndependentToken"]=!result.Switched&&!result.RollbackFailed&&now=="old"&&closes==0;
        }
        now="old";result=await RecoverySwitch.Run("old","new",true,selected,(n,ct)=>{now=n;if(n=="new")throw new IOException();return Task.FromResult(0);},ct=>Task.FromResult(true),close,CancellationToken.None);
        tests["lostPutResponseStillRollsBack"]=!result.Switched&&!result.RollbackFailed&&now=="old";
        now="old";result=await RecoverySwitch.Run("old","new",true,selected,(n,ct)=>{if(n=="old")throw new IOException();now=n;return Task.FromResult(0);},ct=>Task.FromResult(false),close,CancellationToken.None);
        tests["rollbackFailureReported"]=!result.Switched&&result.RollbackFailed;
        now="old";result=await RecoverySwitch.Run("old","new",true,selected,select,ct=>Task.FromResult(true),ct=>{throw new IOException();},CancellationToken.None);
        tests["reconnectFailureDoesNotClaimReconnected"]=result.Switched&&!result.Reconnected&&now=="new";
        tests["modelHostAndProcessRequired"]=RecoverySwitch.ModelConnection("daily-cloudcode-pa.googleapis.com","language_server_windows_x64.exe")
            &&!RecoverySwitch.ModelConnection("play.googleapis.com","Antigravity.exe")
            &&!RecoverySwitch.ModelConnection("daily-cloudcode-pa.googleapis.com","browser.exe")
            &&!RecoverySwitch.ModelConnection("daily-cloudcode-pa.googleapis.com.attacker.example","Antigravity.exe");
        if(live){try{await LiveCoreTest(tests);}catch{tests["realCoreControllerAndPreservedTunnel"]=false;}}
        File.WriteAllText(Path.Combine(Util.ReportRoot,"runtime-test.json"),Util.Json.Serialize(tests));
        return tests.Values.All(x=>x)?0:1;
    }
    static async Task LiveCoreTest(Dictionary<string,bool> tests){
        string directory=null;Task echo=null;var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try{
            int port=((IPEndPoint)listener.LocalEndpoint).Port;
            echo=Task.Run(async()=>{using(var peer=await listener.AcceptTcpClientAsync()){
                var stream=peer.GetStream();var bytes=new byte[256];int count;
                while((count=await stream.ReadAsync(bytes,0,bytes.Length))>0)await stream.WriteAsync(bytes,0,count);
            }});
            using(var timeout=new CancellationTokenSource(10000))using(var core=new ProxySession(Util.LoadSettings())){
                directory=core.Dir;await core.Ready(timeout.Token);var nodes=await core.Nodes(timeout.Token);
                await core.SelectPreserving("DIRECT",timeout.Token);
                using(var socket=new TcpClient()){
                    await WaitPolicy.Bound(socket.ConnectAsync(IPAddress.Loopback,core.Port).ContinueWith(t=>{t.GetAwaiter().GetResult();return true;}),timeout.Token);
                    var stream=socket.GetStream();var request=Encoding.ASCII.GetBytes("CONNECT 127.0.0.1:"+port+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\n\r\n");
                    await stream.WriteAsync(request,0,request.Length);var header=new StringBuilder();var one=new byte[1];
                    while(!header.ToString().EndsWith("\r\n\r\n")){
                        int got=await WaitPolicy.Bound(stream.ReadAsync(one,0,1),timeout.Token);if(got!=1||header.Length>4096)throw new IOException();header.Append((char)one[0]);
                    }
                    if(!header.ToString().Contains("200"))throw new IOException();
                    await stream.WriteAsync(new byte[]{42},0,1);if(await WaitPolicy.Bound(stream.ReadAsync(one,0,1),timeout.Token)!=1||one[0]!=42)throw new IOException();
                    if(nodes.Count==0)throw new IOException();
                    await core.SelectPreserving(nodes[0],timeout.Token);
                    tests["realCoreSelectorChanged"]=await core.Selected(timeout.Token)==nodes[0];
                    await stream.WriteAsync(new byte[]{43},0,1);
                    tests["realCoreExistingTunnelSurvivesSwitch"]=await WaitPolicy.Bound(stream.ReadAsync(one,0,1),timeout.Token)==1&&one[0]==43;
                    tests["unrelatedTunnelExcludedFromReconnect"]=!(await core.ModelConnectionIds(timeout.Token)).Any();
                    await core.SelectPreserving("DIRECT",timeout.Token);
                }
            }
            tests["runtimeHelperDirectoryCleaned"]=directory!=null&&!Directory.Exists(directory);
        }finally{listener.Stop();if(echo!=null){var observer=echo.ContinueWith(t=>{if(t.IsFaulted){var observed=t.Exception;}},TaskScheduler.Default);}}
    }
}
}
