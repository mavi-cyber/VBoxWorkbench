using System.Text.RegularExpressions;

namespace VBoxWorkbench.Core;

public sealed record VmRef(string Name, string Uuid, bool Running = false);

public sealed record StorageSlot(string Controller, int Port, int Device, string Medium)
{
    public bool Empty => Medium is "none" or "";
    public string Label => Medium is "none" or "" ? "empty" : Medium == "emptydrive" ? "empty drive" : Path.GetFileName(Medium);
}

public sealed record StorageController(string Name, string Type, int PortCount, List<StorageSlot> Slots);

public sealed record Nic(int Index, string Mode, string Detail);

public sealed record SnapshotNode(string Name, string Uuid, int Depth, bool Current);

/// <summary>A VM as described by "showvminfo --machinereadable". Unknown keys are kept, so newer versions lose nothing.</summary>
public sealed partial class VmInfo
{
    public Dictionary<string, string> Props { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> KeysInOrder { get; } = [];

    public string Name => Get("name");
    public string Uuid => Get("UUID");
    public string State => Get("VMState", "unknown");
    public bool IsRunning => State is "running" or "paused" or "stuck" or "teleporting" or "livesnapshotting" or "starting" or "stopping" or "saving" or "restoring";
    public bool IsPaused => State == "paused";
    public bool IsSaved => State == "saved";
    /// <summary>modifyvm needs the VM powered off: not running and not in saved state.</summary>
    public bool CanModify => !IsRunning && !IsSaved;

    public string Get(string key, string fallback = "") => Props.TryGetValue(key, out var v) ? v : fallback;

    public int GetInt(string key, int fallback = 0) => int.TryParse(Get(key), out int n) ? n : fallback;

    [GeneratedRegex("^(?:\"(?<k>[^\"]+)\"|(?<k>[^=\\s]+))=(?<v>.*)$")]
    private static partial Regex Line();

    public static VmInfo Parse(string machineReadable)
    {
        var info = new VmInfo();
        foreach (var raw in machineReadable.Replace("\r", "").Split('\n'))
        {
            var m = Line().Match(raw.Trim());
            if (!m.Success) continue;
            string v = m.Groups["v"].Value.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Replace("\\\\", "\\").Replace("\\\"", "\"");
            string k = m.Groups["k"].Value;
            if (!info.Props.ContainsKey(k)) info.KeysInOrder.Add(k);
            info.Props[k] = v;
        }
        return info;
    }

    public List<StorageController> Controllers()
    {
        var list = new List<StorageController>();
        for (int i = 0; Props.ContainsKey("storagecontrollername" + i); i++)
        {
            string name = Get("storagecontrollername" + i);
            var slots = new List<StorageSlot>();
            var rx = new Regex("^" + Regex.Escape(name) + @"-(\d+)-(\d+)$");
            foreach (var key in KeysInOrder)
            {
                var m = rx.Match(key);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int port) && int.TryParse(m.Groups[2].Value, out int device))
                    slots.Add(new StorageSlot(name, port, device, Props[key]));
            }
            list.Add(new StorageController(name, Get("storagecontrollertype" + i), GetInt("storagecontrollerportcount" + i), slots));
        }
        return list;
    }

    public List<Nic> Nics()
    {
        var list = new List<Nic>();
        for (int i = 1; Props.ContainsKey("nic" + i); i++)
        {
            string mode = Get("nic" + i);
            string detail = mode switch
            {
                "bridged" => Get("bridgeadapter" + i),
                "intnet" => Get("intnet" + i),
                "hostonly" => Get("hostonlyadapter" + i),
                "hostonlynetwork" => Get("hostonly-network" + i),
                "natnetwork" => Get("nat-network" + i),
                "generic" => Get("generic" + i),
                _ => "",
            };
            list.Add(new Nic(i, mode, detail));
        }
        return list;
    }

    /// <summary>Snapshot tree flattened depth-first. Keys look like SnapshotName, SnapshotName-1, SnapshotName-1-1.</summary>
    public List<SnapshotNode> Snapshots()
    {
        string current = Get("CurrentSnapshotUUID");
        var list = new List<SnapshotNode>();
        foreach (var key in KeysInOrder)
        {
            if (!key.StartsWith("SnapshotName", StringComparison.Ordinal)) continue;
            string suffix = key["SnapshotName".Length..];
            string uuid = Get("SnapshotUUID" + suffix);
            list.Add(new SnapshotNode(Props[key], uuid, suffix.Count(c => c == '-'), uuid == current));
        }
        return list;
    }

    [GeneratedRegex("^\"(?<n>.*)\"\\s+\\{(?<u>[0-9a-fA-F-]+)\\}\\s*$")]
    private static partial Regex VmLine();

    /// <summary>Parses "list vms" style output: "name" {uuid}.</summary>
    public static List<VmRef> ParseVmList(string output)
    {
        var list = new List<VmRef>();
        foreach (var l in output.Replace("\r", "").Split('\n'))
        {
            var m = VmLine().Match(l);
            if (m.Success) list.Add(new VmRef(m.Groups["n"].Value, m.Groups["u"].Value));
        }
        return list;
    }

    /// <summary>Parses "Key: value" blocks separated by blank lines (list hdds, dvds, hostonlyifs, natnets...).</summary>
    public static List<Dictionary<string, string>> ParseBlocks(string output)
    {
        var blocks = new List<Dictionary<string, string>>();
        Dictionary<string, string>? cur = null;
        foreach (var l in output.Replace("\r", "").Split('\n'))
        {
            if (l.Trim().Length == 0) { cur = null; continue; }
            int colon = l.IndexOf(':');
            if (colon <= 0 || l[0] == ' ') continue;
            if (cur == null) blocks.Add(cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            cur[l[..colon].Trim()] = l[(colon + 1)..].Trim();
        }
        return blocks;
    }
}
