using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ConsoleAppFramework;
using UniCli.Protocol;

namespace UniCli.Client;

public partial class Commands
{
    /// <summary>
    /// List the Unity Editors running on this machine
    /// </summary>
    [Command("instances")]
    public async Task<int> Instances(bool json = false)
    {
        var entries = await InstanceDirectory.ProbeAllAsync();
        var pruned = InstanceDirectory.PruneStale(entries);
        var live = entries.Where(e => e.IsUsable).ToList();

        var result = CliResult.Ok(
            live.Count == 1 ? "1 editor running" : $"{live.Count} editors running",
            BuildInstancesJson(live, pruned),
            BuildInstancesText(live, pruned));

        return OutputWriter.Write(result, json);
    }

    private static string BuildInstancesText(IReadOnlyList<InstanceEntry> entries, int pruned)
    {
        var text = new StringBuilder();

        if (entries.Count == 0)
        {
            text.AppendLine("No running editors found.");
            text.Append(
                "  Only editors with the UniCli server package installed advertise themselves.");

            if (pruned > 0)
                text.Append($"\n  Removed {pruned} record(s) left behind by editors that are gone.");

            return text.ToString();
        }

        var nameWidth = Math.Max(4, entries.Max(e => (e.Record.projectName ?? "").Length));
        var versionWidth = Math.Max(5, entries.Max(e => (e.Record.unityVersion ?? "").Length));

        text.AppendLine(
            $"{"NAME".PadRight(nameWidth)}  {"STATE".PadRight(9)}  " +
            $"{"UNITY".PadRight(versionWidth)}  {"PID".PadLeft(7)}  UPTIME  PROJECT");

        foreach (var entry in entries)
        {
            var record = entry.Record;
            text.AppendLine(
                $"{(record.projectName ?? "").PadRight(nameWidth)}  " +
                $"{StateLabel(entry.State).PadRight(9)}  " +
                $"{(record.unityVersion ?? "").PadRight(versionWidth)}  " +
                $"{record.pid,7}  {FormatUptime(record.startedAt),6}  {record.projectPath}");
        }

        var reloading = entries.Count(e => e.State == InstanceState.Reloading);
        if (reloading > 0)
            text.AppendLine(
                $"\n{reloading} editor(s) reloading assemblies; commands will queue until they finish.");

        if (pruned > 0)
            text.AppendLine($"Removed {pruned} record(s) left behind by editors that are gone.");

        return text.ToString().TrimEnd();
    }

    private static string StateLabel(InstanceState state) => state switch
    {
        InstanceState.Ready => "ready",
        InstanceState.Reloading => "reloading",
        _ => "stale"
    };

    private static string FormatUptime(long startedAtUnixMs)
    {
        if (startedAtUnixMs <= 0)
            return "-";

        var started = DateTimeOffset.FromUnixTimeMilliseconds(startedAtUnixMs);
        var uptime = DateTimeOffset.UtcNow - started;

        if (uptime < TimeSpan.Zero)
            return "-";

        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h{uptime.Minutes:00}m";

        if (uptime.TotalMinutes >= 1)
            return $"{(int)uptime.TotalMinutes}m";

        return $"{(int)uptime.TotalSeconds}s";
    }

    private static string BuildInstancesJson(IReadOnlyList<InstanceEntry> entries, int pruned)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteStartArray("instances");

        foreach (var entry in entries)
        {
            var record = entry.Record;
            writer.WriteStartObject();
            writer.WriteString("projectName", record.projectName);
            writer.WriteString("projectPath", record.projectPath);
            writer.WriteString("state", StateLabel(entry.State));
            writer.WriteString("unityVersion", record.unityVersion);
            writer.WriteString("pipeName", record.pipeName);
            writer.WriteNumber("pid", record.pid);
            writer.WriteNumber("startedAt", record.startedAt);
            writer.WriteString("serverVersion", record.serverVersion ?? "");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("pruned", pruned);
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
