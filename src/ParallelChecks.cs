using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace AgyLauncher {
public static class ParallelChecks {
    public static async Task<int> Run(){
        var tests=new Dictionary<string,bool>();
        var entered=new TaskCompletionSource<bool>();var release=new TaskCompletionSource<bool>();int winnerDisposed=0,loserDisposed=0;bool canceled=false;
        var race=await NodeRace.Run(new[]{"winner","slow"},2,async(name,ct)=>{
            if(name=="winner"){await entered.Task;return new NodeOutcome{Node=name,Category="Success",Verified=true,ReleaseHook=()=>Interlocked.Increment(ref winnerDisposed)};}
            using(ct.Register(()=>canceled=true)){entered.TrySetResult(true);await release.Task;}
            return new NodeOutcome{Node=name,Category="Success",Verified=true,ReleaseHook=()=>Interlocked.Increment(ref loserDisposed)};
        },null,CancellationToken.None);
        tests["firstVerifiedSuccessReturned"]=race.Winner!=null&&race.Winner.Node=="winner";
        tests["doesNotWaitForSlowLoser"]=!race.Cleanup.IsCompleted;
        tests["loserCancellationSignaled"]=canceled;
        tests["winnerNotDisposedOnSelection"]=winnerDisposed==0;
        release.TrySetResult(true);await race.Cleanup;
        tests["losingSuccessfulResourceReleased"]=loserDisposed==1;
        tests["winningResourceSurvivesLoserCleanup"]=winnerDisposed==0;
        race.Winner.Dispose();race.Winner.Dispose();tests["winningResourceReleasedExactlyOnce"]=winnerDisposed==1;

        int active=0,maximum=0,started=0,finished=0;var firstPair=new TaskCompletionSource<bool>();
        race=await NodeRace.Run(Enumerable.Range(1,6).Select(x=>x.ToString()).ToList(),2,async(name,ct)=>{
            int index=Interlocked.Increment(ref started);int now=Interlocked.Increment(ref active);int prior;if(now==2)firstPair.TrySetResult(true);
            do{prior=maximum;if(prior>=now)break;}while(Interlocked.CompareExchange(ref maximum,now,prior)!=prior);
            try{if(index<=2)await firstPair.Task;await Task.Delay(5,ct);return new NodeOutcome{Node=name,Category="Network"};}finally{Interlocked.Decrement(ref active);}
        },r=>finished++,CancellationToken.None);await race.Cleanup;
        tests["concurrencyBounded"]=maximum<=2&&maximum>1;
        tests["failedSlotsRefilledWithinBatch"]=started==6&&finished==6&&race.Winner==null;

        entered=new TaskCompletionSource<bool>();started=0;bool otherStopped=false;
        race=await NodeRace.Run(new[]{"login","pending","never"},2,async(name,ct)=>{
            Interlocked.Increment(ref started);
            if(name=="login"){await entered.Task;return new NodeOutcome{Node=name,Category="Login"};}
            entered.TrySetResult(true);try{await Task.Delay(Timeout.Infinite,ct);}catch(OperationCanceledException){otherStopped=ct.IsCancellationRequested;throw;}
            return new NodeOutcome{Node=name,Category="Network"};
        },null,CancellationToken.None);await race.Cleanup;
        tests["loginStopsFurtherScheduling"]=race.Fatal!=null&&race.Fatal.Category=="Login"&&started==2;
        tests["loginCancelsPendingWorker"]=otherStopped;

        using(var stop=new CancellationTokenSource(25)){
            bool stopped=false;try{await NodeRace.Run(new[]{"a","b"},2,async(name,ct)=>{await Task.Delay(Timeout.Infinite,ct);return new NodeOutcome{Node=name,Category="Network"};},null,stop.Token);}catch(OperationCanceledException){stopped=true;}
            tests["userCancellationPropagates"]=stopped;
        }
        race=await NodeRace.Run(new[]{"unverified"},1,(name,ct)=>Task.FromResult(new NodeOutcome{Node=name,Category="Success",Verified=false}),null,CancellationToken.None);await race.Cleanup;
        tests["unverifiedSuccessRejected"]=race.Winner==null&&race.Fatal!=null&&race.Fatal.Category=="RoutingEvidenceMissing";
        tests["stackLineNumbersAreNotAuthErrors"]=Util.Category("stack frame line 401, code offset 403, usage 429")=="Unknown";
        tests["httpAuthStatusRecognized"]=Util.Category("HTTP 401 Unauthorized")=="Login";
        tests["httpQuotaStatusRecognized"]=Util.Category("status: 429")=="Quota";
        tests["jsonPermissionStatusRecognized"]=Util.Category("{\"code\":403}")=="Eligibility";
        File.WriteAllText(Path.Combine(Util.ReportRoot,"parallel-test.json"),Util.Json.Serialize(tests));return tests.Values.All(x=>x)?0:1;
    }
    public static async Task<int> Isolation(){
        var tests=new Dictionary<string,bool>();string aDir=null,bDir=null;
        using(var a=new ProxySession(Util.LoadSettings())){
            await a.Ready(CancellationToken.None);
            using(var b=new ProxySession(Util.LoadSettings())){
            aDir=a.Dir;bDir=b.Dir;
            await b.Ready(CancellationToken.None);
            var nodes=await a.Nodes(CancellationToken.None);
            tests["differentProxyPorts"]=a.Port!=b.Port;
            tests["differentControlEndpoints"]=a.Controller!=b.Controller;
            tests["differentPrivateDirectories"]=a.Dir!=b.Dir;
            if(nodes.Count>=2){
                await a.Select(nodes[0],CancellationToken.None);await b.Select(nodes[1],CancellationToken.None);
                tests["firstSelectionUnaffected"]=Util.Str(await a.Get("/proxies/GLOBAL",CancellationToken.None),"now")==nodes[0];
                tests["secondSelectionIndependent"]=Util.Str(await b.Get("/proxies/GLOBAL",CancellationToken.None),"now")==nodes[1];
            }else tests["enoughNodesForIsolationTest"]=false;
            }
        }
        tests["bothPrivateDirectoriesCleaned"]=!Directory.Exists(aDir)&&!Directory.Exists(bDir);
        File.WriteAllText(Path.Combine(Util.ReportRoot,"isolation-test.json"),Util.Json.Serialize(tests));return tests.Values.All(x=>x)?0:1;
    }
}
}
