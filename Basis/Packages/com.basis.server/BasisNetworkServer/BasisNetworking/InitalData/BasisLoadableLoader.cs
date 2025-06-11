using BasisNetworking.InitialData;
using System;
using System.IO;
using static SerializableBasis;

namespace BasisNetworking.InitalData
{
    public static class BasisLoadableLoader
    {
        public static void LoadXML(string FolderName)
        {
            try
            {
                // Get the directory of the executable
                string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;

                // Define the new folder name
                string newFolderPath = Path.Combine(exeDirectory, FolderName);

                // Check if the folder already exists, if not, create it
                if (!Directory.Exists(newFolderPath))
                {
                    Directory.CreateDirectory(newFolderPath);
                    BNL.Log("Folder created successfully: " + newFolderPath);
                    // Provide an example XML file content for the user to copy or uncomment

                    string exampleFilePath = Path.Combine(newFolderPath, "ExampleConfigdisabled.xml[remove]");
                    File.WriteAllText(exampleFilePath, exampleXml);
                    BNL.Log("Example XML file created at: " + exampleFilePath);
                }

                BasisLoadableConfiguration[] configurations = BasisLoadableConfiguration.LoadAllFromFolder(FolderName);

                foreach (BasisLoadableConfiguration config in configurations)
                {
                    BNL.Log($"CombinedURL: {config.CombinedURL}, LoadAssetPassword: {config.UnlockPassword}");
                    LocalLoadResource LLR = FromBasisLoadableConfiguration(config);
                    BasisNetworkResourceManagement.LoadResource(LLR);

                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
        public static LocalLoadResource FromBasisLoadableConfiguration(BasisLoadableConfiguration config)
        {
            return new LocalLoadResource
            {
                Mode = config.Mode,
                LoadedNetID = config.LoadedNetID,
                UnlockPassword = config.UnlockPassword,
                CombinedURL = config.CombinedURL,
                PositionX = config.PositionX,
                PositionY = config.PositionY,
                PositionZ = config.PositionZ,
                QuaternionX = config.QuaternionX,
                QuaternionY = config.QuaternionY,
                QuaternionZ = config.QuaternionZ,
                QuaternionW = config.QuaternionW,
                ScaleX = config.ScaleX,
                ScaleY = config.ScaleY,
                ScaleZ = config.ScaleZ,
                Persist = config.Persist,
                 ModifyScale = config.ModifyScale
            };
        }
        public const string exampleXml = @"<BasisLoadableConfiguration>
    <!-- Mode of the configuration -->
    <Mode>0</Mode>
    <!-- Network ID -->
    <LoadedNetID></LoadedNetID>
    <!-- Unlock password -->
    <UnlockPassword></UnlockPassword>
    <!-- Combined URl -->
    <CombinedURL></CombinedURL>
    <!-- Local load flag -->
    <IsLocalLoad>false</IsLocalLoad>

    <!-- Position values -->
    <PositionX>0</PositionX>
    <PositionY>0</PositionY>
    <PositionZ>0</PositionZ>

    <!-- Quaternion values -->
    <QuaternionX>0</QuaternionX>
    <QuaternionY>0</QuaternionY>
    <QuaternionZ>0</QuaternionZ>
    <QuaternionW>1</QuaternionW>

    <!-- Scale values -->
    <ScaleX>1</ScaleX>
    <ScaleY>1</ScaleY>
    <ScaleZ>1</ScaleZ>

    <!-- Persist flag -->
    <Persist>false</Persist>
</BasisLoadableConfiguration>";
    }
}
