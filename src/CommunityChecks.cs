using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace AgyLauncher {
public static class CommunityChecks {
    public static Task<int> Run(){
        var tests=new Dictionary<string,bool>();
        tests["officialCliArtifactAccepted"]=OfficialCli.AllowedArtifact("https://storage.googleapis.com/antigravity-public/antigravity-cli/test/windows-x64/cli.exe");
        tests["httpArtifactRejected"]=!OfficialCli.AllowedArtifact("http://storage.googleapis.com/antigravity-public/antigravity-cli/test/cli.exe");
        tests["lookalikeHostRejected"]=!OfficialCli.AllowedArtifact("https://storage.googleapis.com.attacker.example/antigravity-public/antigravity-cli/test/cli.exe");
        tests["otherBucketRejected"]=!OfficialCli.AllowedArtifact("https://storage.googleapis.com/other-bucket/cli.exe");
        tests["userinfoRejected"]=!OfficialCli.AllowedArtifact("https://user:password@storage.googleapis.com/antigravity-public/antigravity-cli/test/cli.exe");
        tests["queryRejected"]=!OfficialCli.AllowedArtifact("https://storage.googleapis.com/antigravity-public/antigravity-cli/test/cli.exe?token=example");
        Diagnostics.Category("untrusted text with secret-value");
        Diagnostics.Usage("{\"response\":\"secret-value\",\"usage\":{\"input_tokens\":5,\"output_tokens\":2,\"thinking_tokens\":3,\"token\":\"secret-value\"}}");
        var snapshot=Diagnostics.Snapshot();string serialized=Util.Json.Serialize(snapshot);
        tests["diagnosticsExcludeRawText"]=!serialized.Contains("secret-value");
        tests["unknownCategoryIsNormalized"]=Convert.ToString(snapshot["lastCategory"])=="Unknown";
        tests["reportedUsageCounted"]=Convert.ToInt64(snapshot["reportedInputTokens"])==5&&Convert.ToInt64(snapshot["reportedOutputTokens"])==2&&Convert.ToInt64(snapshot["reportedThinkingTokens"])==3;
        Diagnostics.Usage("{\"usage\":{\"input_tokens\":-9,\"output_tokens\":999999999999999,\"thinking_tokens\":\"bad\"}}");
        tests["invalidUsageIgnored"]=Convert.ToInt64(Diagnostics.Snapshot()["reportedInputTokens"])==5&&Convert.ToInt64(Diagnostics.Snapshot()["reportedOutputTokens"])==2;
        tests["diagnosticsUseFixedFields"]=snapshot.Keys.OrderBy(x=>x).SequenceEqual(new[]{"schema","launcherVersion","is64BitProcess","lastCategory","modelRequestsStarted","reportedInputTokens","reportedOutputTokens","reportedThinkingTokens","usageScope"}.OrderBy(x=>x));
        try{Bootstrap.Validate(new Settings());tests["missingPathsRejected"]=false;}catch(InvalidOperationException){tests["missingPathsRejected"]=true;}
        tests["dataSeparatedFromCheckout"]=!Util.Root.StartsWith(Util.AppRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        tests["cliSeparatedFromPrograms"]=!Util.CliRoot.StartsWith(Util.AppRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        var selectable=new HashSet<string>{"test-node","DIRECT","test-group","PASS-RULE"};
        tests["realSelectableProxyAccepted"]=Util.IsCandidate("test-node","Http",selectable);
        tests["internalPassRuleRejected"]=!Util.IsCandidate("PASS-RULE","PassRule",selectable);
        tests["unselectableEntryRejected"]=!Util.IsCandidate("not-in-global","Http",selectable);
        tests["selectorGroupRejected"]=!Util.IsCandidate("test-group","Selector",selectable);
        tests["directIsNotProxyCandidate"]=!Util.IsCandidate("DIRECT","Direct",selectable);
        File.WriteAllText(Path.Combine(Util.ReportRoot,"community-test.json"),Util.Json.Serialize(tests));return Task.FromResult(tests.Values.All(x=>x)?0:1);
    }
}
}
