using System.Text.RegularExpressions;
using Xunit;

namespace AmberBatteryAI.Tests;

public class ConfigDriftTests
{
    static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AmberBatteryAI", "config.yaml"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "AmberBatteryAI");
    }

    static (List<string> Options, List<string> Schema) ReadConfig()
    {
        var options = new List<string>(); var schema = new List<string>(); List<string>? cur = null;
        foreach (var l in File.ReadAllLines(Path.Combine(Root(), "config.yaml")))
        {
            if (l.StartsWith("options:")) { cur = options; continue; }
            if (l.StartsWith("schema:")) { cur = schema; continue; }
            if (l.Length > 0 && l[0] != ' ') cur = null;
            var m = Regex.Match(l, @"^  ([a-z0-9_]+):");
            if (cur != null && m.Success) cur.Add(m.Groups[1].Value);
        }
        return (options, schema);
    }

    [Fact]
    public void EveryOptionHasASchemaEntry()
    {
        var (o, s) = ReadConfig();
        Assert.Empty(o.Except(s)); Assert.Empty(s.Except(o));
    }

    [Fact]
    public void EveryOptionIsExportedByRunSh()
    {
        var (o, _) = ReadConfig();
        var run = File.ReadAllText(Path.Combine(Root(), "run.sh"));
        foreach (var k in o) Assert.True(Regex.IsMatch(run, @"'[^']*\." + k + @"\b"), $"run.sh does not export {k}");
    }

    [Fact]
    public void EveryOptionIsTranslated()
    {
        var (o, _) = ReadConfig();
        var tr = File.ReadAllText(Path.Combine(Root(), "translations", "en.yaml"));
        foreach (var k in o) Assert.Contains($"  {k}:", tr);
    }

    [Fact]
    public void EveryExportedEnvVarIsReadInCode()
    {
        var run = File.ReadAllText(Path.Combine(Root(), "run.sh"));
        var code = string.Join("\n", Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("/obj/")).Select(File.ReadAllText));
        foreach (Match m in Regex.Matches(run, @"export (\w+__\w+)="))
        {
            var key = m.Groups[1].Value.Replace("__", ":");
            Assert.True(code.Contains("\"" + key + "\""), $"{key} is exported by run.sh but never read");
        }
    }
}
