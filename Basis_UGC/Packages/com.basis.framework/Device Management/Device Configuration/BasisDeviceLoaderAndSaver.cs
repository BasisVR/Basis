using Basis.Scripts.Device_Management;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

public class BasisDeviceLoaderAndSaver
{
    private const string JsonIdentifier = ".json";
    private static readonly string JsonAllTag = "*" + JsonIdentifier; // Made readonly for optimization

    public static List<BasisDeviceMatchSettings> LoadDeviceAsync(string directoryPath)
    {
        var loadedDevices = new List<BasisDeviceMatchSettings>();

        if (Directory.Exists(directoryPath))
        {
            string[] jsonFiles = Directory.GetFiles(directoryPath, JsonAllTag);

            IEnumerable<BasisDeviceMatchSettings> loadTasks = jsonFiles.Select(jsonFile => LoadDeviceFromFileAsync(jsonFile));
            loadedDevices.AddRange(loadTasks.Where(device => device != null));
        }
        else
        {
            BasisDebug.LogError($"Directory '{directoryPath}' does not exist.");
        }

        return loadedDevices;
    }

    private static BasisDeviceMatchSettings LoadDeviceFromFileAsync(string jsonFile)
    {
        if (!File.Exists(jsonFile))
        {
            BasisDebug.LogError($"File not found: '{jsonFile}'");
            return null;
        }

        try
        {
            using (var stream = new FileStream(jsonFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096))
            {
                StringBuilder jsonContent = new StringBuilder();
                char[] buffer = new char[4096];
                int bytesRead;

                while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    jsonContent.Append(buffer, 0, bytesRead);
                }

                // Deserialize the accumulated JSON data
                return JsonUtility.FromJson<BasisDeviceMatchSettings>(jsonContent.ToString());
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Failed to load or parse file '{jsonFile}': {ex.Message}");
            return null;
        }
    }

    public static void SaveDevices(string directoryPath, List<BasisDeviceMatchSettings> devices)
    {
        // Safety checks
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Directory path cannot be null or empty", nameof(directoryPath));
        }
        if (devices == null || devices.Count == 0)
        {
            throw new ArgumentException("Devices list cannot be null or empty", nameof(devices));
        }
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
        // Use a fixed-size array instead of a List to avoid resizing overhead
        int Count = devices.Count;
        for (int i = 0; i < Count; i++)
        {
            var device = devices[i];
            if (device == null)
            {
                continue;
            }


            string filePath = Path.Combine(directoryPath, MakeDeviceSafeForSave(device.DeviceID) + JsonIdentifier);
            Task.Run(() => SaveDeviceAsync(filePath, device));
        }
    }
    public static string MakeDeviceSafeForSave(string fileName)
    {
        // Define a regex pattern to match invalid filename characters
        string invalidChars = new string(Path.GetInvalidFileNameChars()) + "{}";
        string pattern = $"[{Regex.Escape(invalidChars)}]";

        // Replace invalid characters with an underscore
        return Regex.Replace(fileName, pattern, "_");
    }
    public static void SaveDeviceAsync(string filePath, BasisDeviceMatchSettings device)
    {
        try
        {
            string jsonContent = JsonUtility.ToJson(device, false); // Use compact JSON format to reduce file size
            byte[] newContentHash;

            using (var sha256 = SHA256.Create())
            {
                newContentHash = sha256.ComputeHash(Encoding.UTF8.GetBytes(jsonContent));
            }

            if (File.Exists(filePath))
            {
                string loadedContent;
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    loadedContent = reader.ReadToEnd();
                }

                var existingDevice = JsonUtility.FromJson<BasisDeviceMatchSettings>(loadedContent);

                // Compare versions if necessary
                if (existingDevice != null && existingDevice.VersionNumber > device.VersionNumber)
                {
                    BasisDebug.Log("Newer version exists; using that instead", BasisDebug.LogTag.Device);
                    return;
                }

                // Compute the hash of the existing file content
                byte[] existingContentHash;
                using (var sha256 = SHA256.Create())
                {
                    existingContentHash = sha256.ComputeHash(Encoding.UTF8.GetBytes(loadedContent));
                }

                // If the hashes match, no need to write the file again
                if (StructuralComparisons.StructuralEqualityComparer.Equals(existingContentHash, newContentHash))
                {
                //    BasisDebug.Log("File content is identical; no need to write.");
                    return;
                }
                else
                {
                    BasisDebug.Log("File content differs; updating file.", BasisDebug.LogTag.Device);
                }
            }

            // Write the new content to the file
            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
            using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                writer.Write(jsonContent);
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error processing file '{filePath}': {ex.Message}");
            throw; // Rethrow to catch in WhenAll
        }
    }
}
