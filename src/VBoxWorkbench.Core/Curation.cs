using System.Text.RegularExpressions;

namespace VBoxWorkbench.Core;

public enum Room { Machines, Media, Network, Guest, Diagnostics, Cloud, Host, More }

public enum Zone { Power, General, Board, Bays, Ports, Display, Sharing, Vault, Timeline, More }

/// <summary>
/// The hand-written layer on top of the discovered catalog: where each command lives, which zone an option
/// belongs to, what is destructive, and plain-language phrases for search. Anything not listed here still
/// shows up (in the "More" room or zone), so a newer VirtualBox never hides a command.
/// </summary>
public static class Curation
{
    private static readonly Dictionary<string, Room> Rooms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["list"] = Room.Host, ["showvminfo"] = Room.Machines, ["registervm"] = Room.Machines, ["unregistervm"] = Room.Machines,
        ["createvm"] = Room.Machines, ["modifyvm"] = Room.Machines, ["clonevm"] = Room.Machines, ["movevm"] = Room.Machines,
        ["encryptvm"] = Room.Machines, ["import"] = Room.Machines, ["export"] = Room.Machines, ["signova"] = Room.Machines,
        ["startvm"] = Room.Machines, ["controlvm"] = Room.Machines, ["unattended"] = Room.Machines,
        ["discardstate"] = Room.Machines, ["adoptstate"] = Room.Machines, ["snapshot"] = Room.Machines,
        ["storageattach"] = Room.Machines, ["storagectl"] = Room.Machines, ["bandwidthctl"] = Room.Machines,
        ["usbfilter"] = Room.Machines, ["sharedfolder"] = Room.Machines, ["modifynvram"] = Room.Machines,
        ["closemedium"] = Room.Media, ["showmediuminfo"] = Room.Media, ["createmedium"] = Room.Media,
        ["modifymedium"] = Room.Media, ["clonemedium"] = Room.Media, ["mediumproperty"] = Room.Media,
        ["encryptmedium"] = Room.Media, ["checkmediumpwd"] = Room.Media, ["convertfromraw"] = Room.Media,
        ["mediumio"] = Room.Media, ["vboximg-mount"] = Room.Media,
        ["natnetwork"] = Room.Network, ["hostonlyif"] = Room.Network, ["hostonlynet"] = Room.Network, ["dhcpserver"] = Room.Network,
        ["guestproperty"] = Room.Guest, ["guestcontrol"] = Room.Guest,
        ["debugvm"] = Room.Diagnostics, ["metrics"] = Room.Diagnostics,
        ["cloud"] = Room.Cloud, ["cloudprofile"] = Room.Cloud,
        ["setextradata"] = Room.Host, ["getextradata"] = Room.Host, ["setproperty"] = Room.Host,
        ["usbdevsource"] = Room.Host, ["extpack"] = Room.Host, ["updatecheck"] = Room.Host,
    };

    public static Room RoomOf(string command) => Rooms.TryGetValue(command, out var r) ? r : Room.More;

    public static string RoomTitle(Room r) => r switch
    {
        Room.Machines => "Machines", Room.Media => "Media store", Room.Network => "Network room",
        Room.Guest => "Guest desk", Room.Diagnostics => "Diagnostics", Room.Cloud => "Cloud dock",
        Room.Host => "Host and settings", _ => "More commands",
    };

    public static string RoomBlurb(Room r) => r switch
    {
        Room.Machines => "Create, import, clone, move and remove virtual machines.",
        Room.Media => "Disk, DVD and floppy images: create, resize, clone, encrypt, convert.",
        Room.Network => "NAT networks, host-only networks and interfaces, DHCP servers.",
        Room.Guest => "Talk to the guest OS: run programs, copy files, read and set guest properties. Needs Guest Additions.",
        Room.Diagnostics => "Debugging tools and resource metrics. For experts.",
        Room.Cloud => "Oracle Cloud Infrastructure instances, images, networks and profiles.",
        Room.Host => "Global settings, extension packs, updates, extra data and host information.",
        _ => "Commands your VirtualBox version offers that have no dedicated place yet.",
    };

    /// <summary>Commands that act on one VM and therefore belong to the workbench of that VM.</summary>
    public static readonly string[] MachineScoped =
    [
        "showvminfo", "modifyvm", "controlvm", "startvm", "snapshot", "storageattach", "storagectl", "bandwidthctl",
        "sharedfolder", "encryptvm", "discardstate", "adoptstate", "modifynvram", "clonevm", "movevm", "unregistervm",
        "export", "unattended", "guestproperty", "guestcontrol", "debugvm",
    ];

    // Matched against OptionNames.Canonical, so --nic-type (7.x), --nictype (6.x) and --x86-pae (7.2) all land correctly.
    private static readonly (Zone Zone, Regex Pattern)[] OptionZones =
    [
        (Zone.General, new(@"^(name|groups|description|ostype|iconfile|snapshotfolder|defaultfrontend|vmprocesspriority|autostart|platform)")),
        (Zone.Ports, new(@"^(nic|cable|bridge|hostonly|intnet|nat|macaddress|cloudnetwork|uart|lpt|usb|mouse|keyboard|audio)")),
        (Zone.Display, new(@"^(vram|graphicscontroller|monitor|accelerate|recording|vrde)")),
        (Zone.Sharing, new(@"^(clipboard|draganddrop)")),
        (Zone.Vault, new(@"^(teleporter|tracing|guestdebug|pci|testing|tpm|cpuid)")),
        (Zone.Board, new(@"^(memory|cpu|plugcpu|unplugcpu|pagefusion|acpi|ioapic|apic|x2apic|hpet|pae|longmode|chipset|iommu|firmware|bios|boot|rtc|hwvirtex|nested|largepages|vtx|virt|paravirt|ibpb|specctrl|l1d|mds|triplefault|hardwareuuid|systemuuid|guestmemoryballoon|arm|vmexecutionengine|efi|superset|biosbootmenu)")),
    ];

    public static Zone ZoneOfOption(string optionName)
    {
        string canonical = OptionNames.Canonical(optionName);
        foreach (var (zone, pattern) in OptionZones)
            if (pattern.IsMatch(canonical)) return zone;
        return Zone.More;
    }

    private static readonly (Zone Zone, Regex Pattern)[] ControlZones =
    [
        (Zone.Power, new(@"^(pause|resume|reset|poweroff|savestate|acpi|reboot|shutdown)")),
        (Zone.Ports, new(@"^(nic|setlinkstate|natpf|usbattach|usbdetach|audio|changeuartmode|keyboardput)")),
        (Zone.Display, new(@"^(vrde|setvideomodehint|setscreenlayout|screenshot|recording|webcam)")),
        (Zone.Sharing, new(@"^(clipboard|draganddrop|setcredentials)")),
        (Zone.Board, new(@"^(plugcpu|unplugcpu|cpuexecutioncap|guestmemoryballoon|vm-process-priority)")),
        (Zone.Vault, new(@"^(addencpassword|removeencpassword|removeallencpasswords|teleport|autostart)")),
    ];

    public static Zone ZoneOfControl(Synopsis s)
    {
        string kw = s.Elements.FirstOrDefault(e => e.Kind == ElementKind.Keyword)?.Name ?? "";
        foreach (var (zone, pattern) in ControlZones)
            if (pattern.IsMatch(kw)) return zone;
        return Zone.More;
    }

    public static string ZoneTitle(Zone z) => z switch
    {
        Zone.Power => "Power", Zone.General => "Identity", Zone.Board => "Board", Zone.Bays => "Drive bays",
        Zone.Ports => "Ports", Zone.Display => "Display and remote", Zone.Sharing => "Sharing",
        Zone.Vault => "Vault and advanced", Zone.Timeline => "Timeline", _ => "More",
    };

    private static readonly Regex DangerWords = new(
        @"^(unregistervm|discardstate|closemedium .*--delete|snapshot .* (delete|restore|restorecurrent)|controlvm .* (poweroff|reset)|" +
        @"modifynvram .* (inituefivarstore|deletevar)|extpack uninstall|mediumio .*formatfat|encryptvm .* removepassword|" +
        @"cloud .*(instance terminate|image delete|network delete)|cloudprofile .* delete|natnetwork remove|hostonlyif remove|" +
        @"hostonlynet remove|dhcpserver remove|guestcontrol .* (rm|rmdir|removefile|removedir) |storagectl .*--remove|" +
        @"usbfilter remove|sharedfolder remove|bandwidthctl .* remove|debugvm .* (injectnmi|setregisters)|clonemedium .*--existing|" +
        @"storageattach .*--medium[= ]none|mediumproperty .* delete|usbdevsource remove|encryptmedium)",
        RegexOptions.IgnoreCase);

    /// <summary>True when the command destroys or irreversibly changes something and deserves a confirmation.</summary>
    public static bool IsDangerous(IReadOnlyList<string> args) => DangerWords.IsMatch(string.Join(' ', args));

    public static string DangerNote(IReadOnlyList<string> args)
    {
        string a = string.Join(' ', args);
        if (a.StartsWith("unregistervm")) return a.Contains("--delete") ? "This removes the VM and deletes its disks, saved states and logs." : "This removes the VM from VirtualBox. Its files stay on disk.";
        if (a.StartsWith("discardstate")) return "The saved state is thrown away, like pulling the power cable.";
        if (a.Contains("poweroff") || a.Contains(" reset")) return "The guest is not shut down cleanly. Unsaved data in the VM can be lost.";
        if (a.Contains("inituefivarstore")) return "The UEFI variable store is wiped and recreated.";
        if (a.StartsWith("snapshot") && a.Contains("restore")) return "The VM's current state is replaced by the snapshot.";
        if (a.Contains("formatfat")) return "The contents of the medium are erased.";
        return "This removes or irreversibly changes something.";
    }

    /// <summary>A plain-language phrase that leads to one synopsis, optionally with values filled in.</summary>
    public sealed record Phrase(string Text, string Path, string? Contains = null, Dictionary<string, string>? Presets = null);

    private static Dictionary<string, string> P(params string[] kv)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
        return d;
    }

    public static readonly Phrase[] Phrases =
    [
        new("Create a new virtual machine", "createvm", Presets: P("--register", "")),
        new("Install an operating system automatically (unattended)", "unattended install"),
        new("Detect which operating system is on an ISO", "unattended detect"),
        new("Start a virtual machine", "startvm"),
        new("Start a virtual machine in the background (headless)", "startvm", Presets: P("--type", "headless")),
        new("Pause a running machine", "controlvm pause"),
        new("Resume a paused machine", "controlvm resume"),
        new("Shut down politely (ACPI power button)", "controlvm acpipowerbutton"),
        new("Force power off (pull the plug)", "controlvm poweroff"),
        new("Save the machine state and stop (hibernate)", "controlvm savestate"),
        new("Restart the machine (hard reset)", "controlvm reset"),
        new("Take a snapshot", "snapshot take"),
        new("Take a snapshot without stopping the machine (live)", "snapshot take", Presets: P("--live", "")),
        new("Restore a snapshot (go back in time)", "snapshot restore"),
        new("Delete a snapshot", "snapshot delete"),
        new("List snapshots", "snapshot list"),
        new("Clone a virtual machine (copy)", "clonevm", Presets: P("--register", "")),
        new("Make a linked clone from a snapshot", "clonevm", Presets: P("--options", "Link", "--register", "")),
        new("Move a virtual machine to another folder", "movevm"),
        new("Rename a virtual machine", "modifyvm", "--name=", P("--name", "")),
        new("Change memory (RAM) size", "modifyvm", "--memory", P("--memory", "4096")),
        new("Change the number of CPUs (processors, cores)", "modifyvm", "--cpus", P("--cpus", "2")),
        new("Change video memory", "modifyvm", "--vram", P("--vram", "128")),
        new("Switch firmware between BIOS and EFI (UEFI)", "modifyvm", "--firmware", P("--firmware", "efi")),
        new("Add a TPM chip (for Windows 11)", "modifyvm", "--tpm-type", P("--tpm-type", "2.0")),
        new("Set the boot order", "modifyvm", "--boot", P("--boot", "dvd")),
        new("Enable nested virtualization", "modifyvm", "--nested-hw-virt", P("--nested-hw-virt", "on")),
        new("Enable 3D acceleration", "modifyvm", "--accelerate-3d", P("--accelerate-3d", "on")),
        new("Connect a network adapter to NAT, bridged, internal or host-only", "modifyvm", "--nic", P("--nic", "nat")),
        new("Use bridged networking (appear on the real network)", "modifyvm", "--nic", P("--nic", "bridged", "--bridge-adapter", "")),
        new("Join an internal network (isolated lab network)", "modifyvm", "--nic", P("--nic", "intnet", "--intnet", "lab-net")),
        new("Forward a port to the VM (port forwarding, SSH, web server)", "modifyvm", "--nat-pf", P("--nat-pf", "ssh,tcp,,2222,,22")),
        new("Forward a port on a running VM", "controlvm natpf"),
        new("Delete a port forwarding rule", "controlvm natpf delete"),
        new("Unplug or plug the network cable", "controlvm setlinkstate"),
        new("Change the MAC address", "modifyvm", "--mac-address", P("--mac-address", "auto")),
        new("Enable the shared clipboard (copy and paste)", "modifyvm", "--clipboard-mode", P("--clipboard-mode", "bidirectional")),
        new("Enable drag and drop", "modifyvm", "--drag-and-drop", P("--drag-and-drop", "bidirectional")),
        new("Share a folder with the VM", "sharedfolder add", Presets: P("--automount", "")),
        new("Remove a shared folder", "sharedfolder remove"),
        new("Enable remote desktop (RDP, VRDE)", "modifyvm", "--vrde", P("--vrde", "on")),
        new("Record the screen to a video file", "modifyvm", "--recording", P("--recording", "on")),
        new("Take a screenshot of the VM", "controlvm screenshotpng"),
        new("Type text into the VM (send keys)", "controlvm keyboardputstring"),
        new("Attach a USB device to a running VM", "controlvm usbattach"),
        new("Add a USB filter (auto-attach a USB device)", "usbfilter add"),
        new("Attach a webcam", "controlvm webcam attach"),
        new("Add a storage controller (SATA, IDE, NVMe)", "storagectl", Presets: P("--add", "sata")),
        new("Attach a disk or ISO to a VM (insert DVD)", "storageattach"),
        new("Eject the DVD", "storageattach", Presets: P("--medium", "emptydrive", "--type", "dvddrive")),
        new("Insert the Guest Additions CD", "storageattach", Presets: P("--medium", "additions", "--type", "dvddrive")),
        new("Create a new virtual hard disk", "createmedium"),
        new("Resize a virtual disk (make it bigger)", "modifymedium", Presets: P("--resize", "")),
        new("Compact a virtual disk (shrink the file)", "modifymedium", Presets: P("--compact", "")),
        new("Clone or convert a disk image (VDI, VMDK, VHD)", "clonemedium"),
        new("Convert a raw disk image to VDI", "convertfromraw"),
        new("Encrypt a disk image", "encryptmedium"),
        new("Encrypt a whole VM", "encryptvm setencryption"),
        new("Remove a disk image from VirtualBox", "closemedium"),
        new("Show details of a disk image", "showmediuminfo"),
        new("Import an appliance (OVA, OVF)", "import"),
        new("Export a VM as an appliance (OVA, OVF)", "export"),
        new("Sign an OVA file", "signova"),
        new("Delete a virtual machine", "unregistervm", Presets: P("--delete", "")),
        new("Add an existing VM (.vbox file)", "registervm"),
        new("Discard the saved state", "discardstate"),
        new("Create a NAT network", "natnetwork add", Presets: P("--enable", "", "--dhcp", "on")),
        new("Create a host-only interface", "hostonlyif create"),
        new("Set the IP of a host-only interface", "hostonlyif ipconfig"),
        new("Create a host-only network", "hostonlynet add"),
        new("Add a DHCP server", "dhcpserver add"),
        new("Find the IP address of a VM (DHCP lease)", "dhcpserver findlease"),
        new("Limit network or disk speed (bandwidth)", "bandwidthctl add"),
        new("Run a program inside the VM", "guestcontrol run"),
        new("Copy files into the VM", "guestcontrol copyto"),
        new("Copy files out of the VM", "guestcontrol copyfrom"),
        new("Update Guest Additions", "guestcontrol updatega"),
        new("Read a guest property (for example the guest IP)", "guestproperty get"),
        new("List all guest properties", "guestproperty enumerate"),
        new("Install an extension pack", "extpack install"),
        new("Check for VirtualBox updates", "updatecheck perform"),
        new("Change the default machine folder", "setproperty"),
        new("Start a VM automatically when the host boots", "modifyvm", "--autostart-enabled", P("--autostart-enabled", "on")),
        new("Enable secure boot (enroll Microsoft keys)", "modifynvram enrollmssignatures"),
        new("Show resource usage (CPU, RAM metrics)", "metrics query"),
        new("Dump the VM core for debugging", "debugvm dumpvmcore"),
        new("Show everything about a VM", "showvminfo"),
        new("List things VirtualBox knows about (VMs, disks, networks, OS types)", "list"),
        new("Teleport a running VM to another host", "controlvm teleport"),
        new("Mount a disk image on the host (Linux, macOS)", "vboximg-mount"),
    ];
}
