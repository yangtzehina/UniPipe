using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UniCli.Protocol;

namespace UniCli.Client;

/// <summary>
/// How a running editor is doing, as far as a caller outside it can tell.
/// </summary>
internal enum InstanceState
{
    /// <summary>The server answered a connection. Commands will be accepted.</summary>
    Ready,

    /// <summary>
    /// The process is alive but the server is not answering — almost always a domain reload in
    /// progress. Worth reporting separately from "gone", because waiting is the right response to
    /// one and not the other.
    /// </summary>
    Reloading,

    /// <summary>The process behind this record is gone. The record is a leftover.</summary>
    Stale
}

internal sealed class InstanceEntry
{
    public InstanceEntry(InstanceRecord record, InstanceState state)
    {
        Record = record;
        State = state;
    }

    public InstanceRecord Record { get; }
    public InstanceState State { get; }
    public bool IsUsable => State != InstanceState.Stale;
}

/// <summary>
/// Reads the registry of running editors.
///
/// Everything here treats a record as a claim to be checked. Records outlive the editors that
/// wrote them — a crash has no shutdown hook — so liveness is established by looking at the
/// process and the pipe, never by the file's presence.
/// </summary>
internal static class InstanceDirectory
{
    /// <summary>
    /// Short on purpose: this runs against every record before the caller's real work, and a
    /// reloading editor should be reported as reloading rather than waited on.
    /// </summary>
    private const int ProbeTimeoutMs = 400;

    public static IReadOnlyList<InstanceRecord> ReadAll()
    {
        var directory = InstanceRegistry.GetDirectory();
        if (!Directory.Exists(directory))
            return Array.Empty<InstanceRecord>();

        var records = new List<InstanceRecord>();

        foreach (var file in Directory.EnumerateFiles(directory, "*" + InstanceRegistry.RecordExtension))
        {
            var record = TryRead(file);
            if (record != null)
                records.Add(record);
        }

        return records
            .OrderBy(r => r.projectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.projectPath, StringComparer.Ordinal)
            .ToList();
    }

    private static InstanceRecord? TryRead(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var record = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.InstanceRecord);

            // A record caught mid-write, or written by a future version, is skipped rather than
            // reported as a broken editor.
            if (record == null || string.IsNullOrEmpty(record.pipeName) ||
                string.IsNullOrEmpty(record.projectPath))
                return null;

            return record;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task<IReadOnlyList<InstanceEntry>> ProbeAllAsync()
    {
        var records = ReadAll();
        var probes = records.Select(ProbeAsync).ToArray();
        return await Task.WhenAll(probes);
    }

    public static async Task<InstanceEntry> ProbeAsync(InstanceRecord record)
    {
        if (!IsProcessAlive(record.pid))
            return new InstanceEntry(record, InstanceState.Stale);

        using var client = new PipeClient(record.pipeName);
        var connected = await client.ConnectAsync(ProbeTimeoutMs);

        // Connecting is the whole probe. Sending a command would queue behind whatever the editor
        // is already running, turning a listing into a wait of unbounded length.
        return new InstanceEntry(
            record, connected.IsError ? InstanceState.Reloading : InstanceState.Ready);
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes records whose process is gone. Called only where a listing was produced anyway, so
    /// it costs nothing extra, and never called on a reloading editor.
    /// </summary>
    public static int PruneStale(IEnumerable<InstanceEntry> entries)
    {
        var pruned = 0;

        foreach (var entry in entries.Where(e => e.State == InstanceState.Stale))
        {
            try
            {
                var path = InstanceRegistry.GetRecordPath(entry.Record.pipeName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    pruned++;
                }
            }
            catch (Exception)
            {
                // Another process may have pruned it first; that is the outcome we wanted.
            }
        }

        return pruned;
    }
}
