using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace AgyLauncher {
public sealed class SwitchResult {public bool Switched,Reconnected,RollbackFailed;}
public static class RecoverySwitch {
    // Both selector changes and reconnects are restricted to our private controller.
    public static async Task<SwitchResult> Run(string oldNode,string newNode,bool reconnect,
        Func<CancellationToken,Task<string>> selected,Func<string,CancellationToken,Task> select,
        Func<CancellationToken,Task<bool>> verify,Func<CancellationToken,Task> close,CancellationToken ct){
        var result=new SwitchResult();bool changed=false,failed=false;
        try{
            ct.ThrowIfCancellationRequested();
            if(await selected(ct)!=oldNode)throw new InvalidOperationException("当前节点已变化，停止切换。");
            // A failed PUT may have reached the server: attempt rollback even when its response is lost.
            changed=true;await select(newNode,ct);
            if(await selected(ct)!=newNode||!await verify(ct))throw new InvalidOperationException("切换后的模型验证未通过。");
            ct.ThrowIfCancellationRequested();result.Switched=true;
        }catch{failed=true;}
        if(failed){
            if(changed)using(var rollback=new CancellationTokenSource(5000)){
                try{await select(oldNode,rollback.Token);result.RollbackFailed=await selected(rollback.Token)!=oldNode;}
                catch{result.RollbackFailed=true;}
            }
            return result;
        }
        // Commit precedes optional reconnect. Failure to reconnect does not invalidate the tested selector.
        if(reconnect){try{await close(ct);result.Reconnected=true;}catch{}}
        return result;
    }
    public static bool ModelConnection(string host,string process){
        bool domain=host=="daily-cloudcode-pa.googleapis.com"||host=="cloudcode-pa.googleapis.com"||host=="aicode.googleapis.com";
        string name=Path.GetFileName(process??"");
        return domain&&Regex.IsMatch(name,"^(Antigravity|language_server.*)\\.exe$",RegexOptions.IgnoreCase);
    }
}
public sealed partial class ProxySession {
    public async Task<string> Selected(CancellationToken ct){return Util.Str(await Get("/proxies/GLOBAL",ct),"now");}
    public async Task SelectPreserving(string name,CancellationToken ct){
        using(var body=new StringContent(Util.Json.Serialize(new {name=name}),Encoding.UTF8,"application/json"))
        using(var response=await api.PutAsync(Controller+"/proxies/GLOBAL",body,ct))response.EnsureSuccessStatusCode();
    }
    public async Task<List<string>> ModelConnectionIds(CancellationToken ct){
        var ids=new List<string>();var snapshot=await Get("/connections",ct);object rows;
        if(snapshot.TryGetValue("connections",out rows)&&rows is IEnumerable)
            foreach(object item in (IEnumerable)rows){
                var row=Util.Map(item);object raw;
                if(row==null||!row.TryGetValue("metadata",out raw))continue;
                var meta=Util.Map(raw);string process=Util.Str(meta,"processPath");if(String.IsNullOrEmpty(process))process=Util.Str(meta,"process");
                string id=Util.Str(row,"id");Guid guid;
                if(Guid.TryParse(id,out guid)&&RecoverySwitch.ModelConnection(Util.Str(meta,"host"),process))ids.Add(id);
            }
        return ids;
    }
    public async Task CloseConnections(IList<string> ids,CancellationToken ct){
        foreach(string id in ids){
            Guid guid;if(!Guid.TryParse(id,out guid))throw new InvalidOperationException();
            using(var response=await api.DeleteAsync(Controller+"/connections/"+Uri.EscapeDataString(id),ct)){
                if(response.StatusCode!=System.Net.HttpStatusCode.NotFound)response.EnsureSuccessStatusCode();
            }
        }
    }
}
public sealed partial class MainForm {
    readonly Button recoverLive=new Button();readonly CheckBox periodic=new CheckBox();
    readonly ComboBox intervalChoice=new ComboBox();
    readonly System.Windows.Forms.Timer guardTimer=new System.Windows.Forms.Timer{Interval=600000};
    bool runtimeEvidenceReset;Task recoveryTask=Task.FromResult(0);CancellationTokenSource recoveryStop;bool recovering,recoveryManual;
    void SetupRuntimeRecovery(){
        recoverLive.Text="使用中报错：验证备用并重连";recoverLive.SetBounds(18,146,300,34);recoverLive.Enabled=false;
        periodic.Text="定期模型检查（消耗额度）";periodic.SetBounds(330,149,330,27);periodic.Enabled=!preview;periodic.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
        intervalChoice.DropDownStyle=ComboBoxStyle.DropDownList;intervalChoice.Items.AddRange(new object[]{"10 分钟","30 分钟","60 分钟"});intervalChoice.SelectedIndex=1;intervalChoice.SetBounds(665,148,125,30);intervalChoice.Anchor=AnchorStyles.Top|AnchorStyles.Right;Controls.Add(intervalChoice);
        intervalChoice.SelectedIndexChanged+=(a,b)=>{guardTimer.Interval=new[]{600000,1800000,3600000}[intervalChoice.SelectedIndex];if(periodic.Checked){guardTimer.Stop();guardTimer.Start();}};guardTimer.Interval=1800000;
        log.Top=190;log.Height=285;Controls.Add(recoverLive);Controls.Add(periodic);
        recoverLive.Click+=async(a,b)=>{
            if(recovering||!desktop)return;
            if(MessageBox.Show(this,"将并行验证最多 6 个备用节点，会消耗少量模型额度。通过后保持客户端打开，并重连它已有的 Google 模型连接。\n\n请先停止当前生成或等待任务结束；重连可能中断正在进行的模型请求。CLI 模型应与桌面选择一致。是否继续？","验证备用并重连",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
            await BeginRecovery(true);
        };
        periodic.CheckedChanged+=(a,b)=>{
            if(periodic.Checked){Write("已开启定期真实模型检查，会消耗额度。仅明确地区拒绝触发自动备用验证；自动切换保留现有连接，需要时手动重连。");if(desktop)guardTimer.Start();}
            else{guardTimer.Stop();if(recovering&&!recoveryManual&&recoveryStop!=null)recoveryStop.Cancel();Write("定期模型检查已关闭；仍可手动验证备用并重连。");}
        };
        guardTimer.Tick+=async(a,b)=>{if(desktop&&!recovering)await BeginRecovery(false);};
        trayMenu.Items.Add("使用中报错：验证备用并重连",null,(a,b)=>{RestoreFromTray();recoverLive.PerformClick();});
        FormClosed+=(a,b)=>{guardTimer.Stop();guardTimer.Dispose();};
    }
    void StartRuntimeRecovery(){recoverLive.Enabled=true;model.Enabled=true;if(periodic.Checked)guardTimer.Start();}
    async Task StopRuntimeRecovery(){
        guardTimer.Stop();recoverLive.Enabled=false;
        if(recoveryStop!=null)recoveryStop.Cancel();
        try{await recoveryTask;}catch{}
    }
    Task BeginRecovery(bool manual){
        if(recovering||!desktop||proxy==null||cts==null||cts.IsCancellationRequested)return Task.FromResult(0);
        recovering=true;recoveryManual=manual;recoverLive.Enabled=false;cancel.Enabled=true;cancel.Text="取消备用检测";
        recoveryStop=CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        recoveryTask=RecoverLive(manual,recoveryStop.Token);return recoveryTask;
    }
    async Task RecoverLive(bool manual,CancellationToken ct){
        ProxySession live=proxy;string previous=node;NodeOutcome winner=null;Task losers=Task.FromResult(0);bool committed=false;
        var metrics=new ProbeMetrics();
        Func<Task> work=async()=>{
            if(Util.ClientStatus(clientProcess)!=ClientLife.Alive){Write("无法确认客户端仍在运行，停止备用检测。");return;}
            // Probe exactly the node definitions of the live instance, not a newer Clash subscription.
            var probe=new Settings{AppPath=settings.AppPath,CorePath=settings.CorePath,ClashConfigPath=Path.Combine(live.Dir,"config.yaml"),Model=model.Text.Trim(),MaxAttempts=6};
            WaitPolicy.Configure(probe);
            await Task.Run(()=>Util.VerifyCli());ct.ThrowIfCancellationRequested();
            Write((manual?"开始手动恢复":"开始定期检查")+"；客户端保持打开；CLI 模型"+(String.IsNullOrEmpty(probe.Model)?"为默认值，请与桌面保持一致。":"采用输入值。"));
            var progress=new Progress<ProbeEvent>(e=>{if(!IsDisposed&&!ct.IsCancellationRequested&&!String.IsNullOrEmpty(e.Text))Write("[备用 "+Fingerprint(e.Node)+"] "+e.Text);});
            if(!manual){
                using(var current=await Task.Run(()=>NodeProbe.Check(probe,previous,progress,metrics,ct))){
                    ct.ThrowIfCancellationRequested();
                    if(current.Success){Write("定期 CLI 模型检查通过；这不保证桌面每次请求都成功。");return;}
                    if(current.Category!="Region"){
                        Write("定期检查："+ResultLabel(current.Category)+"；未自动切换。");
                        if(current.Category!="Network"&&current.Category!="Timeout"){periodic.Checked=false;Write(Util.Explain(current.Category));}
                        return;
                    }
                }
                Write("定期模型请求明确遭到地区拒绝，开始验证备用；现有客户端连接保持。");
            }
            var nodes=(await live.Nodes(ct)).Where(n=>n!=previous).ToList();
            var preferred=Util.LastSuccessful(state);if(nodes.Remove(preferred))nodes.Insert(0,preferred);
            var batch=nodes.Take(6).ToList();
            if(batch.Count==0){Write("没有可用的备用候选；未切换、未修复。");return;}
            var race=await NodeRace.Run(batch,3,(name,stop)=>NodeProbe.Check(probe,name,progress,metrics,stop),null,ct);losers=race.Cleanup;
            winner=race.Winner;
            if(winner==null){Write(race.Fatal==null?"备用候选均未验证通过；保留原节点，未修复。":Util.Explain(race.Fatal.Category));if(race.Fatal!=null)periodic.Checked=false;return;}
            ct.ThrowIfCancellationRequested();
            if(Util.ClientStatus(clientProcess)!=ClientLife.Alive)return;
            var oldIds=manual?await live.ModelConnectionIds(ct):new List<string>();
            string postCategory="Unknown";
            var result=await RecoverySwitch.Run(previous,winner.Node,manual,live.Selected,live.SelectPreserving,
                async stop=>{
                    string args="-p "+Util.Quote("Reply with exactly AGY_OK. Do not use tools, run commands, or inspect any files.")+" --output-format json --disable-slash-commands --print-timeout 60s";
                    if(!String.IsNullOrEmpty(probe.Model))args+=" --model "+Util.Quote(probe.Model);
                    Interlocked.Increment(ref metrics.CliCalls);
                    var parsed=Util.Parse(await live.RunCli(probe,args,68,stop));Diagnostics.Category(parsed.Category);postCategory=parsed.Category;return parsed.Success;
                },stop=>live.CloseConnections(oldIds,stop),ct);
            if(!result.Switched){
                if(result.RollbackFailed){periodic.Checked=false;Write("切换未完成，且无法确认原节点已恢复。请保存工作后退出客户端及启动器，再重新检测；未强杀客户端。");}
                else Write("切换后的真实 CLI 验证未通过，已恢复原节点。"+Util.Explain(postCategory));
                return;
            }
            committed=true;node=winner.Node;nodeInfo.Text="当前代理节点："+Regex.Replace(node,"[\\r\\n\\t]"," ");
            runtimeEvidenceReset=true;observedMain=false;observedBackend=false;confirm.Enabled=false;
            state.LastCliNode=node;state.LastCliAt=DateTime.UtcNow.ToString("o");state.LastModel=probe.Model;Util.SaveState(state);
            Write("备用节点及原代理端口上的真实 CLI 请求均已返回 AGY_OK；当前节点 ["+Fingerprint(node)+"]。客户端未关闭。");
            Write(manual?(result.Reconnected?"已重连旧 Google 模型连接；请回客户端重试失败请求，收到真实回复后再确认。":"节点已切换，但部分旧连接未能重连。请重试；仍报错时保存并正常退出后重新检测。"):
                "自动切换仅用于新连接，已有连接保留。若仍报错，请点击“使用中报错：验证备用并重连”。");
            tray.ShowBalloonTip(5000,"Antigravity 备用节点已验证",manual?"请回客户端重试失败请求，确认真实回复。":"新连接已切换；已有连接保留。仍报错可手动重连。",ToolTipIcon.Info);
        };
        try{await work();}
        catch(OperationCanceledException){Write("备用检测已取消；已完成的切换以当前节点显示为准，客户端未关闭。");}
        catch{Write(committed?"备用节点已切换，但后续记录异常；请检查当前节点并重试。":"备用恢复异常；未确认修复。客户端未关闭，请重试或保存后重新检测。");}
        // Await cleanup before the live proxy can be disposed by the lifecycle monitor.
        try{await losers;}catch{}
        if(winner!=null)winner.Dispose();
        Write("本次运行检查 CLI 请求 "+metrics.CliCalls+" 次；桌面真实对话仍需重试确认。");
        recovering=false;recoverLive.Enabled=desktop;cancel.Enabled=false;cancel.Text="取消检测";
        if(recoveryStop!=null){recoveryStop.Dispose();recoveryStop=null;}
    }
}
}
