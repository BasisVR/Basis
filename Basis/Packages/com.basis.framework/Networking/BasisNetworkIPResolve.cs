using Unity.Scripting.LifecycleManagement;
using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisNetworkIPResolve
{
    public static string ResolveHosttoIP(string hostname)
    {
        try
        {
            IPAddress[] ips = Dns.GetHostAddresses(hostname);
            if (ips != null && ips.Length > 0)
            {
                foreach (IPAddress ip in ips)
                    BasisDebug.Log($"IP Candidate: {ip}", BasisDebug.LogTag.Networking);

                // Prefer IPv6 when the OS supports it (mirrors LiteNetLib's resolution order)
                if (Socket.OSSupportsIPv6)
                {
                    foreach (IPAddress ip in ips)
                        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                            return ip.ToString();
                }
                foreach (IPAddress ip in ips)
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                        return ip.ToString();
                return ips[0].ToString();
            }
        }
        catch (System.Exception ex)
        {
            BasisDebug.LogError("Failed to resolve to IP address: " + ex.Message);
        }
        return null;
    }

    public static string[] ResolveLocalhostToIP(string hostname)
    {
        try
        {
            IPAddress[] ips = Dns.GetHostAddresses(hostname);
            if (ips != null && ips.Length > 0)
            {
                string[] addresses = new string[ips.Length];
                for (int Index = 0; Index < ips.Length; Index++)
                {
                    addresses[Index] = ips[Index].ToString();
                }
                return addresses;
            }
        }
        catch (System.Exception ex)
        {
            BasisDebug.LogError("Failed to resolve localhost to IP address: " + ex.Message);
        }
        return null;
    }
    public static string LocalHost = "localhost";
    public static IPAddress IpOutput(string IpString)
    {
        if (IpString.ToLower() == LocalHost)
        {
            string[] IpStrings = BasisNetworkIPResolve.ResolveLocalhost(IpString);
            IpString = IpStrings[0];
        }
        return IPAddress.Parse(IpString);
    }
    public static string[] ResolveLocalhost(string localhost)
    {
        string[] addresses = ResolveLocalhostToIP(localhost);
        if (addresses == null)
        {
            BasisDebug.LogError("Failed to resolve localhost to IP address.");
            throw new System.IO.IOException("Failed to resolve localhost to IP address.");
        }
        return addresses;
    }
}
