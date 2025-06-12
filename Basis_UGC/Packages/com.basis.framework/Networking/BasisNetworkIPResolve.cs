using System;
using System.Net;
using UnityEngine;

public static class BasisNetworkIPResolve
{
    public static string ResolveHosttoIP(string hostname)
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
                    BasisDebug.Log($"IP Candidate: {addresses[Index]}");

                    bool hasThreePeriods = addresses[Index].Split('.').Length - 1 == 3;
                    if (hasThreePeriods)
                    {
                        return addresses[Index]; // select IPv4 if possible (for now)
                    }

                }
                return addresses[0]; // pick first one: IPv4 or IPv6 we don't care right now
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
            string[] IpStrings = BasisNetworkIPResolve.ResolveLocahost(IpString);
            IpString = IpStrings[0];
        }
        return IPAddress.Parse(IpString);
    }
    public static string[] ResolveLocahost(string localhost)
    {
        string[] addresses = ResolveLocalhostToIP(localhost);
        if (addresses != null)
        {
        }
        else
        {
            BasisDebug.LogError("Failed to resolve localhost to IP address.");
        }
        return addresses;
    }
}
