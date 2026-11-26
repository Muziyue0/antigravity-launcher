using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace AgyLauncher {
public sealed class WaitPolicy {
    readonly CancellationToken user;readonly Func<double> elapsed;bool notified;static int entryConsumed;
    public CancellationToken Token {get{return user;}}
    public double ElapsedSeconds {get{return elapsed();}}
    public WaitPolicy(CancellationToken token,Func<double> clock){user=token;elapsed=clock;}
    public WaitPolicy(CancellationToken token,long start){
        user=token;long now=Stopwatch.GetTimestamp();long began=start>0&&start<=now&&(now-start)/(double)Stopwatch.Frequency<3600?start:now;
        elapsed=()=>(Stopwatch.GetTimestamp()-began)/(double)Stopwatch.Frequency;
    }
    public bool Notify30Seconds(){if(notified||ElapsedSeconds<30)return false;notified=true;return true;}
    public static void Configure(Settings settings){settings.ModelTimeoutSeconds=60;settings.HttpTimeoutSeconds=Math.Min(settings.HttpTimeoutSeconds,10);}
    public static long EntryStart(){
        if(Interlocked.Exchange(ref entryConsumed,1)!=0)return 0;
        string arg=Environment.GetCommandLineArgs().FirstOrDefault(x=>x.StartsWith("--start-ticks="));long value;
        return arg!=null&&Int64.TryParse(arg.Substring(14),out value)?value:0;
    }
    public static async Task<T> Bound<T>(Task<T> task,CancellationToken ct){
        var stop=new TaskCompletionSource<bool>();using(ct.Register(()=>stop.TrySetResult(true))){
            if(await Task.WhenAny(task,stop.Task)!=task){var observer=task.ContinueWith(x=>{var ignored=x.Exception;},TaskContinuationOptions.OnlyOnFaulted);ct.ThrowIfCancellationRequested();}return await task;
        }
    }
}
public static class WaitChecks {
    public static async Task<int> Run(){
        var tests=new Dictionary<string,bool>();double seconds=0;var policy=new WaitPolicy(CancellationToken.None,()=>seconds);
        seconds=21;tests["noCancellationAtTwentySeconds"]=!policy.Token.IsCancellationRequested;
        seconds=29;tests["noNoticeBeforeThirty"]=!policy.Notify30Seconds();
        seconds=35;tests["noticeAfterThirty"]=policy.Notify30Seconds();tests["noticeOnlyOnce"]=!policy.Notify30Seconds();
        tests["noticeDoesNotCancelRequest"]=!policy.Token.IsCancellationRequested;
        seconds=70;tests["noHiddenWholeBatchDeadline"]=!policy.Token.IsCancellationRequested;
        var settings=new Settings{ModelTimeoutSeconds=20};WaitPolicy.Configure(settings);tests["oldTwentySecondLimitReplaced"]=settings.ModelTimeoutSeconds==60;
        tests["preflightHasReasonableTimeout"]=settings.HttpTimeoutSeconds==10;
        using(var user=new CancellationTokenSource()){var p=new WaitPolicy(user.Token,()=>100);user.Cancel();tests["userCanCancelAtAnyTime"]=p.Token.IsCancellationRequested;}
        var release=new TaskCompletionSource<bool>();
        var raceTask=NodeRace.Run(new[]{"slow-but-valid"},1,async(name,ct)=>{await release.Task;return new NodeOutcome{Node=name,Category="Success",Verified=true};},null,policy.Token);
        tests["slowResultRemainsPending"]=!raceTask.IsCompleted;release.TrySetResult(true);var race=await raceTask;await race.Cleanup;
        tests["slowVerifiedSuccessCanStillWin"]=race.Winner!=null;race.Winner.Dispose();
        int started=0;race=await NodeRace.Run(new[]{"timed-out","valid"},1,(name,ct)=>{started++;return Task.FromResult(new NodeOutcome{Node=name,Category=name=="valid"?"Success":"Timeout",Verified=name=="valid"});},null,CancellationToken.None);await race.Cleanup;
        tests["timeoutIsNotAccountFatal"]=race.Winner!=null&&started==2;race.Winner.Dispose();
        using(var user=new CancellationTokenSource(20)){bool canceled=false;try{await WaitPolicy.Bound(new TaskCompletionSource<int>().Task,user.Token);}catch(OperationCanceledException){canceled=true;}tests["cancellationStopsLocalWait"]=canceled;}
        File.WriteAllText(Path.Combine(Util.ReportRoot,"wait-policy-test.json"),Util.Json.Serialize(tests));return tests.Values.All(x=>x)?0:1;
    }
}
}
