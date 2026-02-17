using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace BasisNetworking.InitialData
{
    [Serializable]
    public class BasisLoadableConfiguration
    {
        public byte Mode = 0;
        public string LoadedNetID = "";
        public string UnlockPassword = "";
        public string CombinedURL = "";

        public float PositionX = 0f;
        public float PositionY = 0f;
        public float PositionZ = 0f;

        public float QuaternionX = 0f;
        public float QuaternionY = 0f;
        public float QuaternionZ = 0f;
        public float QuaternionW = 1f;

        public float ScaleX = 1f;
        public float ScaleY = 1f;
        public float ScaleZ = 1f;

        public bool Persist = false;
        public bool ModifyScale;
        public static BasisLoadableConfiguration[] LoadAllFromFolder(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException($"The folder '{folderPath}' does not exist.");
            }

            List<BasisLoadableConfiguration> configurations = new List<BasisLoadableConfiguration>();

            string[] xmlFiles = Directory.GetFiles(folderPath, "*.xml");
            var serializer = new XmlSerializer(typeof(BasisLoadableConfiguration));
            foreach (var file in xmlFiles)
            {
                using var reader = new StreamReader(file);
                configurations.Add((BasisLoadableConfiguration)serializer.Deserialize(reader));
                reader.Close();
            }

            return configurations.ToArray();
        }
    }
}
