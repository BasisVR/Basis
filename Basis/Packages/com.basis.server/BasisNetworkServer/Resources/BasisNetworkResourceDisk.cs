using Basis.Network.Core;
using BasisNetworking.InitialData;
using BasisPermissions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

/// <summary>
/// Saves spawned props and worlds into the server's initial resources folder, the same folder
/// <see cref="BasisLoadableLoader"/> loads on boot, so an admin can make content come back after a
/// restart instead of only outliving its creator's disconnect. Gated by
/// <see cref="PermNodes.ResourceSaveToDisk"/>.
/// </summary>
public static class BasisNetworkResourceDisk
{
    /// <summary>Full path of the initial resources folder; set by <see cref="BasisLoadableLoader.LoadXML"/>.</summary>
    public static string Folder;
    private static readonly ConcurrentDictionary<string, string> SavedFiles = new ConcurrentDictionary<string, string>();
    private static readonly object FileLock = new object();

    public static bool IsSaved(string loadedNetId) => !string.IsNullOrEmpty(loadedNetId) && SavedFiles.ContainsKey(loadedNetId);

    public static void Register(string loadedNetId, string file)
    {
        if (!string.IsNullOrEmpty(loadedNetId) && !string.IsNullOrEmpty(file)) SavedFiles[loadedNetId] = file;
    }

    public static void Reset() => SavedFiles.Clear();

    public static void HandleRequest(ModifyResource request, NetPeer peer)
    {
        if (!PermissionIntegration.HasValidRequirement(peer, PermNodes.ResourceSaveToDisk))
        {
            BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(peer, $"No permission: {PermNodes.ResourceSaveToDisk}");
            return;
        }
        string result = request.Op == ModifyResource.OpSaveToDisk ? Save(request) : Remove(request.LoadedNetID);
        BNL.Log(result);
        BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(peer, result);
    }

    /// <summary>Writes the resource to disk with its current state (and the transform the requester sees) and marks it persistent.</summary>
    public static string Save(ModifyResource request)
    {
        if (!NetworkServer.Configuration.HasFileSupport || string.IsNullOrEmpty(Folder)) return "This server does not write files, so content cannot be saved to it.";
        if (!BasisNetworkResourceManagement.UshortNetworkDatabase.TryGetValue(request.LoadedNetID, out LocalLoadResource resource)) return $"Nothing is loaded with id {request.LoadedNetID}.";
        if (request.HasTransform && resource.Mode == 0 && IsFinite(request))
        {
            resource.PositionX = request.PositionX; resource.PositionY = request.PositionY; resource.PositionZ = request.PositionZ;
            resource.QuaternionX = request.QuaternionX; resource.QuaternionY = request.QuaternionY; resource.QuaternionZ = request.QuaternionZ; resource.QuaternionW = request.QuaternionW;
            resource.ScaleX = request.ScaleX; resource.ScaleY = request.ScaleY; resource.ScaleZ = request.ScaleZ;
            resource.ModifyScale = true;
        }
        resource.Persist = true;
        resource.IsAdminLocked = true;
        string file = SavedFiles.TryGetValue(resource.LoadedNetID, out string existing) ? existing : Path.Combine(Folder, FileNameFor(resource.LoadedNetID));
        try
        {
            Write(file, ToConfiguration(resource));
        }
        catch (Exception e)
        {
            return $"Could not save {resource.CombinedURL}: {e.Message}";
        }
        BasisNetworkResourceManagement.UshortNetworkDatabase[resource.LoadedNetID] = resource;
        SavedFiles[resource.LoadedNetID] = file;
        BroadcastState(resource, true);
        return $"Saved {resource.CombinedURL} to the server. It will load again after a restart.";
    }

    /// <summary>Rewrites a saved resource's file after its live state changed (for example its static lock).</summary>
    public static void Resave(string loadedNetId)
    {
        if (!SavedFiles.TryGetValue(loadedNetId ?? string.Empty, out string file)) return;
        if (!BasisNetworkResourceManagement.UshortNetworkDatabase.TryGetValue(loadedNetId, out LocalLoadResource resource)) return;
        try
        {
            Write(file, ToConfiguration(resource));
        }
        catch (Exception e)
        {
            BNL.LogError($"Could not update saved resource file {file}: {e.Message}");
        }
    }

    /// <summary>Deletes the saved file. The item stays loaded until it is removed or the server restarts.</summary>
    public static string Remove(string loadedNetId)
    {
        if (!SavedFiles.TryRemove(loadedNetId ?? string.Empty, out string file)) return "That item is not saved to the server.";
        try
        {
            lock (FileLock) if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception e)
        {
            SavedFiles[loadedNetId] = file;
            return $"Could not remove the saved file: {e.Message}";
        }
        if (BasisNetworkResourceManagement.UshortNetworkDatabase.TryGetValue(loadedNetId, out LocalLoadResource resource)) BroadcastState(resource, false);
        return "Removed from the server's saved content. It will not load after a restart.";
    }

    /// <summary>Called after a resource is unloaded: removing saved content also removes its file, or it would come back on restart.</summary>
    public static void OnUnloaded(string loadedNetId)
    {
        if (!SavedFiles.TryRemove(loadedNetId ?? string.Empty, out string file)) return;
        try
        {
            lock (FileLock) if (File.Exists(file)) File.Delete(file);
            BNL.Log($"Deleted saved resource file {file}");
        }
        catch (Exception e)
        {
            BNL.LogError($"Could not delete saved resource file {file}: {e.Message}");
        }
    }

    public static void SendStateToPeer(NetPeer peer, LocalLoadResource resource)
    {
        if (!IsSaved(resource.LoadedNetID)) return;
        NetDataWriter writer = NetworkServer.RentWriter();
        StateMessage(resource, true).Serialize(writer);
        NetworkServer.TrySend(peer, writer, BasisNetworkCommons.ModifyResourceChannel, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
    }

    private static void BroadcastState(LocalLoadResource resource, bool saved)
    {
        NetDataWriter writer = NetworkServer.RentWriter();
        StateMessage(resource, saved).Serialize(writer);
        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.ModifyResourceChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
    }

    public static ModifyResource StateMessage(LocalLoadResource resource, bool saved) => new ModifyResource
    {
        LoadedNetID = resource.LoadedNetID,
        Mode = resource.Mode,
        Static = resource.Static,
        StaticAdminLocked = resource.StaticAdminLocked,
        Op = ModifyResource.OpDiskState,
        SavedToDisk = saved,
    };

    public static BasisLoadableConfiguration ToConfiguration(LocalLoadResource resource) => new BasisLoadableConfiguration
    {
        Mode = resource.Mode,
        LoadedNetID = resource.LoadedNetID,
        UnlockPassword = resource.UnlockPassword ?? string.Empty,
        CombinedURL = resource.CombinedURL ?? string.Empty,
        PositionX = resource.PositionX, PositionY = resource.PositionY, PositionZ = resource.PositionZ,
        QuaternionX = resource.QuaternionX, QuaternionY = resource.QuaternionY, QuaternionZ = resource.QuaternionZ, QuaternionW = resource.QuaternionW,
        ScaleX = resource.ScaleX, ScaleY = resource.ScaleY, ScaleZ = resource.ScaleZ,
        Persist = true,
        ModifyScale = resource.ModifyScale,
        Static = resource.Static,
        StaticAdminLocked = resource.StaticAdminLocked,
    };

    /// <summary>Net ids come from clients, so only safe characters reach the file name; anything else is hashed.</summary>
    public static string FileNameFor(string loadedNetId)
    {
        bool safe = !string.IsNullOrEmpty(loadedNetId) && loadedNetId.Length <= 96;
        if (safe) foreach (char c in loadedNetId) if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_'))) { safe = false; break; }
        if (safe) return "saved-" + loadedNetId + ".xml";
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(loadedNetId ?? string.Empty));
        return "saved-" + BitConverter.ToString(hash, 0, 16).Replace("-", string.Empty).ToLowerInvariant() + ".xml";
    }

    private static void Write(string file, BasisLoadableConfiguration config)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + ".tmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new XmlSerializer(typeof(BasisLoadableConfiguration)).Serialize(stream, config);
            }
            if (File.Exists(file)) File.Replace(temp, file, null);
            else File.Move(temp, file);
        }
    }

    private static bool IsFinite(ModifyResource r) =>
        float.IsFinite(r.PositionX) && float.IsFinite(r.PositionY) && float.IsFinite(r.PositionZ) &&
        float.IsFinite(r.QuaternionX) && float.IsFinite(r.QuaternionY) && float.IsFinite(r.QuaternionZ) && float.IsFinite(r.QuaternionW) &&
        float.IsFinite(r.ScaleX) && float.IsFinite(r.ScaleY) && float.IsFinite(r.ScaleZ);
}
