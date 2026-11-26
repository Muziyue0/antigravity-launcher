using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace AgyLauncher {
public sealed class ProbeEvent {public string Node,Text;public bool Start,End;}
public sealed class ProbeMetrics {public int CliCalls;}
public sealed class NodeOutcome : IDisposable {
    public string Node,Category;public bool Verified;public ProxySession Session;public Action ReleaseHook;
    int released;
    public bool Success {get{return Category=="Success"&&Verified;}}
    public ProxySession TakeSession(){return Interlocked.Exchange(ref Session,null);}
    public void Dispose(){if(Interlocked.Exchange(ref released,1)!=0)return;var p=TakeSession();if(p!=null)p.Dispose();if(ReleaseHook!=null)ReleaseHook();}
}
public sealed class RaceResult {
    public NodeOutcome Winner,Fatal;public Task Cleanup;
}
public static class NodeRace {
    static bool Fatal(NodeOutcome r){return r.Category!="Network"&&r.Category!="Region"&&r.Category!="Timeout"&&r.Category!="Canceled";}
    static async Task Drain(List<Task<NodeOutcome>> tasks,CancellationTokenSource group){
        try{foreach(var task in tasks){try{var outcome=await task.ConfigureAwait(false);if(outcome!=null)outcome.Dispose();}catch{}}}
        finally{group.Dispose();}
    }
    static Task BackgroundDrain(List<Task<NodeOutcome>> tasks,CancellationTokenSource group){return Task.Run(async()=>await Drain(tasks,group).ConfigureAwait(false));}
    public static async Task<RaceResult> Run(IList<string> nodes,int degree,Func<string,CancellationToken,Task<NodeOutcome>> probe,Action<NodeOutcome> completed,CancellationToken ct){
        if(degree<1||degree>3)throw new ArgumentOutOfRangeException("degree");
        var group=CancellationTokenSource.CreateLinkedTokenSource(ct);var active=new List<Task<NodeOutcome>>();int next=0;
        Func<Task<NodeOutcome>> launch=()=>{
            string name=nodes[next++];
            // A worker must never block the UI during proxy startup or disposal.
            return Task.Run(async()=>await probe(name,group.Token).ConfigureAwait(false));
        };
        Exception failure=null;
        try{
            while(next<nodes.Count&&active.Count<degree)active.Add(launch());
            while(active.Count>0){
                ct.ThrowIfCancellationRequested();
                var task=active.FirstOrDefault(x=>x.Status==TaskStatus.RanToCompletion&&x.Result!=null&&x.Result.Success)??await Task.WhenAny(active);
                active.Remove(task);var result=await task;
                if(result==null)result=new NodeOutcome{Category="Unknown"};
                Diagnostics.Category(result.Category);
                if(result.Success){
                    group.Cancel();
                    // Return immediately. Pending workers are observed and cleaned asynchronously.
                    return new RaceResult{Winner=result,Cleanup=BackgroundDrain(active,group)};
                }
                if(result.Category=="Success")result.Category="RoutingEvidenceMissing";
                if(completed!=null)completed(result);
                if(Fatal(result)){
                    result.Dispose();group.Cancel();return new RaceResult{Fatal=result,Cleanup=BackgroundDrain(active,group)};
                }
                result.Dispose();
                // Consume already finished tasks before starting another paid request.
                if(!active.Any(x=>x.IsCompleted))while(next<nodes.Count&&active.Count<degree)active.Add(launch());
            }
            group.Dispose();return new RaceResult{Cleanup=Task.FromResult(0)};
        }catch(Exception e){group.Cancel();failure=e;}
        await BackgroundDrain(active,group);System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();throw new InvalidOperationException();
    }
}
public static class NodeProbe {
    static readonly SemaphoreSlim Startup=new SemaphoreSlim(1,1);
    public static async Task<NodeOutcome> Check(Settings settings,string node,IProgress<ProbeEvent> progress,ProbeMetrics metrics,CancellationToken ct){
        ProxySession session=null;bool retained=false;string stage="基础连通性";
        Action<string> say=text=>{if(progress!=null)progress.Report(new ProbeEvent{Node=node,Text=text});};
        if(progress!=null)progress.Report(new ProbeEvent{Node=node,Start=true,Text="建立独立代理实例。"});
        try{
            // Port allocation and bind complete before another worker allocates ports.
            await Startup.WaitAsync(ct).ConfigureAwait(false);
            try{ct.ThrowIfCancellationRequested();session=new ProxySession(settings);await session.Ready(ct).ConfigureAwait(false);}finally{Startup.Release();}
            await session.Select(node,ct).ConfigureAwait(false);
            var network=await session.Fetch("https://www.google.com/generate_204",settings.HttpTimeoutSeconds,ct).ConfigureAwait(false);
            if(network.Status!=204){say("Google 连通性未通过，HTTP "+network.Status+"。");return new NodeOutcome{Node=node,Category="Network"};}
            say("第一层通过：Google HTTPS 204。");
            var terms=Util.AdvisoryTerms(()=>session.Fetch("https://policies.google.com/terms?hl=en",2,ct),ct);
            var termsObserver=terms.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion&&!ct.IsCancellationRequested)say(t.Result.Length==0?"旁路地区提示未知；不阻塞模型检测。":"旁路网页地区："+t.Result+"，以模型验证为准。");else if(t.IsFaulted){var ignored=t.Exception;}},TaskScheduler.Default);
            ct.ThrowIfCancellationRequested();say("地区提示在后台获取，立即开始模型请求。");
            string args="-p "+Util.Quote("Reply with exactly AGY_OK. Do not use tools, run commands, or inspect any files.")+" --output-format json --disable-slash-commands --print-timeout "+settings.ModelTimeoutSeconds+"s";
            if(!String.IsNullOrEmpty(settings.Model))args+=" --model "+Util.Quote(settings.Model);
            stage="模型";var requestTime=Stopwatch.StartNew();
            ct.ThrowIfCancellationRequested();Interlocked.Increment(ref metrics.CliCalls);say("第三层：真实 CLI 请求，最长 "+settings.ModelTimeoutSeconds+" 秒。");
            var capture=await session.RunCli(settings,args,settings.ModelTimeoutSeconds+8,ct).ConfigureAwait(false);var parsed=Util.Parse(capture);
            Diagnostics.Category(parsed.Category);
            if(!parsed.Success){if((parsed.Category=="Network"||parsed.Category=="Unknown")&&requestTime.Elapsed.TotalSeconds>=settings.ModelTimeoutSeconds-1){say("模型请求到达 60 秒上限，结果未确认；继续后续候选，当前节点仍可重试。");return new NodeOutcome{Node=node,Category="Timeout"};}say(Util.Explain(parsed.Category));return new NodeOutcome{Node=node,Category=parsed.Category};}
            bool routed=capture.Processes.Any(x=>Regex.IsMatch(Path.GetFileName(x),"^(agy|language_server.*)\\.exe$",RegexOptions.IgnoreCase));
            if(!routed){say("已返回 AGY_OK，但本实例未取得 CLI/后台代理连接证据。");return new NodeOutcome{Node=node,Category="RoutingEvidenceMissing"};}
            ct.ThrowIfCancellationRequested();say("真实回复 AGY_OK 且本节点代理证据齐全。");retained=true;
            return new NodeOutcome{Node=node,Category="Success",Verified=true,Session=session};
        }catch(OperationCanceledException){return new NodeOutcome{Node=node,Category="Canceled"};}
        catch(TimeoutException){say(stage+"请求超时，结果未确认；继续候补，可重新开始轮次重试。");return new NodeOutcome{Node=node,Category="Timeout"};}
        catch(System.Net.Http.HttpRequestException){say("节点网络请求失败。");return new NodeOutcome{Node=node,Category="Network"};}
        catch(UnauthorizedAccessException){say(Util.Explain("LocalPermission"));return new NodeOutcome{Node=node,Category="LocalPermission"};}
        catch{say("本地检测异常，停止自动尝试，未输出配置或凭据。");return new NodeOutcome{Node=node,Category="Unknown"};}
        finally{
            if(!retained&&session!=null)session.Dispose();
            if(progress!=null)progress.Report(new ProbeEvent{Node=node,End=true,Text=""});
        }
    }
}
public sealed partial class MainForm {
    readonly NumericUpDown parallelChoice=new NumericUpDown();int runGeneration;
    async Task<bool> QuickCheckRunning(CancellationToken ct){
        using(var local=CancellationTokenSource.CreateLinkedTokenSource(ct)){
            local.CancelAfter(5000);
            try{bool running=await WaitPolicy.Bound(Task.Run(()=>Util.AppRunning(settings)),local.Token);if(running)Write("客户端已运行；请先保存并正常退出，不会重复打开或强杀。");return running;}
            catch(OperationCanceledException){ct.ThrowIfCancellationRequested();Write("本机进程检查超过 5 秒，未冒险重复启动客户端。");return true;}
        }
    }
    async Task StartRun(){
        if(busy)return;
        Toggle(true);cts=new CancellationTokenSource();var ct=cts.Token;var counts=new Dictionary<string,int>();var metrics=new ProbeMetrics();
        var budget=new WaitPolicy(ct,WaitPolicy.EntryStart());ct=budget.Token;
        var countdown=new System.Windows.Forms.Timer{Interval=250};countdown.Tick+=(a,b)=>{if(!desktop){Text="Antigravity v1.10.0-beta.1 · 已等待 "+((int)budget.ElapsedSeconds)+" 秒";if(budget.Notify30Seconds())Write("已等待 30 秒，继续验证；单个模型请求最多 60 秒。等待较久不代表节点不可用，可随时取消。");}};countdown.Start();
        int generation=++runGeneration;Task loserCleanup=Task.FromResult(0);NodeOutcome winner=null;bool selected=false;
        Func<Task> run=async()=>{
            if(await QuickCheckRunning(ct))return;ct.ThrowIfCancellationRequested();
            if(!File.Exists(settings.AppPath)||!File.Exists(settings.CorePath)||!File.Exists(settings.ClashConfigPath)||!File.Exists(Path.Combine(Util.CliRoot,"agy.exe")))throw new InvalidOperationException("程序、代理内核或官方 CLI 缺失，未开始检测。");
            settings.Model=model.Text.Trim();state.LastModel=settings.Model;state.Parallelism=(int)parallelChoice.Value;observedMain=false;observedBackend=false;
            WaitPolicy.Configure(settings);
            await WaitPolicy.Bound(Task.Run(()=>{Util.VerifyCli();return true;}),ct);ct.ThrowIfCancellationRequested();
            if(settings.MaxAttempts<1||settings.MaxAttempts>12)throw new InvalidOperationException("每批候选数必须为 1 至 12。");
            List<string> nodes;
            // Discovery owns no model request and is disposed before concurrent workers start.
            nodes=await WaitPolicy.Bound(Task.Run(async()=>{ct.ThrowIfCancellationRequested();using(var discover=new ProxySession(settings)){await discover.Ready(ct);return await discover.Nodes(ct);}}),ct);
            if(nodes.Count==0)throw new InvalidOperationException("没有找到可检测的节点。");
            var batch=Util.PlanBatch(state,nodes,Math.Min(6,nodes.Count),settings.Model,File.GetLastWriteTimeUtc(settings.ClashConfigPath).Ticks.ToString());Util.SaveState(state);
            Write("稳定等待：本批 "+batch.Count+" 个，默认 3 并发，候补滚动补位；单个模型请求 60 秒，30 秒仅提示不取消。首个验证成功立即启动，已发送请求可能计费。");
            var activeNodes=new HashSet<string>();
            var progress=new Progress<ProbeEvent>(e=>{
                if(IsDisposed||!busy||generation!=runGeneration)return;
                if(selected&&e.Node!=node)return;
                if(e.Start)activeNodes.Add(e.Node);if(e.End)activeNodes.Remove(e.Node);
                if(!desktop&&!selected)nodeInfo.Text="并行检测："+String.Join(" | ",activeNodes.Select(x=>Regex.Replace(x,"[\\r\\n\\t]"," ")));
                if(!String.IsNullOrEmpty(e.Text))Write("["+Fingerprint(e.Node)+"] "+e.Text);
            });
            var race=await NodeRace.Run(batch,state.Parallelism,(name,stop)=>NodeProbe.Check(settings,name,progress,metrics,stop),outcome=>{
                if(!counts.ContainsKey(outcome.Category))counts[outcome.Category]=0;counts[outcome.Category]++;
                // Login/quota/account failures are not evidence that a node is bad.
                if(outcome.Category=="Network"||outcome.Category=="Region"||outcome.Category=="Timeout"){
                    if(!state.RoundAttempted.Contains(outcome.Node))state.RoundAttempted.Add(outcome.Node);Util.SaveState(state);
                }
            },ct);
            loserCleanup=race.Cleanup;
            if(race.Winner==null){
                await loserCleanup;
                if(race.Fatal!=null){Write(Util.Explain(race.Fatal.Category));return;}
                int remaining=nodes.Count(x=>!state.RoundAttempted.Contains(x));start.Text=remaining>0?"检测下一批":"重新开始检测";
                Write("本批未找到可用节点，本轮还剩 "+remaining+" 个未检测。未启动客户端。"+(remaining>0?"再次点击继续下一批。":"再次点击开始新一轮。"));return;
            }
            winner=race.Winner;proxy=winner.TakeSession();node=winner.Node;
            if(proxy==null)throw new InvalidOperationException("获胜节点的代理所有权未取得，未启动客户端。");
            selected=true;
            countdown.Stop();Text="Antigravity v1.10.0-beta.1 · 节点已验证，正在启动";
            ct=cts.Token;
            nodeInfo.Text="当前代理节点："+Regex.Replace(node,"[\\r\\n\\t]"," ");
            state.LastCliNode=node;state.LastCliAt=DateTime.UtcNow.ToString("o");state.RoundAttempted.Clear();state.RoundKey=null;Util.SaveState(state);start.Text="检测并启动";
            Write("已选出首个验证通过的节点，其余检测已发出取消信号；不等待它们的超时结束。");
            ct.ThrowIfCancellationRequested();if(await QuickCheckRunning(ct))return;ct.ThrowIfCancellationRequested();
            var psi=new ProcessStartInfo(settings.AppPath,"--proxy-server="+proxy.Proxy){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(settings.AppPath)};Util.SetProxy(psi,proxy.Proxy);
            clientProcess=Process.Start(psi);if(clientProcess==null)throw new InvalidOperationException("未取得客户端主进程句柄。");
            Text="Antigravity 代理启动器 v1.10.0-beta.1";
            desktop=true;trayStatus.Text="状态：独立代理运行中";cancel.Enabled=false;desktopStart=DateTime.UtcNow;
            Write("已在 "+budget.ElapsedSeconds.ToString("F1")+" 秒发出客户端启动请求；窗口实际显示速度取决于本机。请完成桌面真实对话验证。");
            StartRuntimeRecovery();
            await Monitor(cts.Token);
        };
        try{await run();}catch(OperationCanceledException){Write("已由用户取消检测，清理辅助进程；未完成的请求不等于地区不可用。");}
        catch(UnauthorizedAccessException){Write(Util.Explain("LocalPermission"));}
        catch(InvalidOperationException e){Write(e.Message);}
        catch{Write("并行检测遇到本地异常，已停止；未展示原始凭据或响应。");}
        {
            // This await is after client launch/monitor, never on the winning launch path.
            await StopRuntimeRecovery();
            try{await loserCleanup;}catch{}
            countdown.Stop();countdown.Dispose();Text="Antigravity 代理启动器 v1.10.0-beta.1";
            if(winner!=null)winner.Dispose();
            if(counts.Count>0)Write("本次结果："+String.Join("；",counts.Select(x=>ResultLabel(x.Key)+" "+x.Value))+"；CLI 检测尝试 "+metrics.CliCalls+" 次。");
            ++runGeneration;Cleanup();Toggle(false);if(cts!=null){cts.Dispose();cts=null;}
            if(exitRequested&&!IsDisposed)BeginInvoke(new Action(()=>Close()));else if(restartRequested&&!IsDisposed){restartRequested=false;BeginInvoke(new Action(async()=>await StartRun()));}
        }
    }
}
}
