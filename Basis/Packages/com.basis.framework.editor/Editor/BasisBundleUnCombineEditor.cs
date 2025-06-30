using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public class BasisBundleUnCombineEditor : EditorWindow
{
    private string LocalFile = "";
    private string FolderToSaveTo = "";
    private string password = "";
    private bool showPassword = false;

    [MenuItem("Basis/Editor/Open BEE File")]
    public static void ShowWindow()
    {
        GetWindow<BasisBundleUnCombineEditor>("Open BEE File");
    }

    public async void OnGUI()
    {
        GUILayout.Label("Select the BEE File and Output Folder", EditorStyles.boldLabel);

        GUILayout.Label("please understand that this will ONLY WORK with files that have been downloaded and loaded locally first. the reason for this is todo with that this system only cares about the downloaded bundle for said platform. i will make a better solution when i can  -dooly", EditorStyles.textArea);
        // File selection
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("BEE File:", GUILayout.Width(70));
        EditorGUILayout.SelectableLabel(LocalFile, GUILayout.Height(16));
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            string selectedFile = EditorUtility.OpenFilePanel("Select BEE File", "", "bee");
            if (!string.IsNullOrEmpty(selectedFile))
                LocalFile = selectedFile;
        }
        EditorGUILayout.EndHorizontal();

        // Folder selection
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Output Folder:", GUILayout.Width(100));
        EditorGUILayout.SelectableLabel(FolderToSaveTo, GUILayout.Height(16));
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            string selectedFolder = EditorUtility.OpenFolderPanel("Select Output Folder", "", "");
            if (!string.IsNullOrEmpty(selectedFolder))
                FolderToSaveTo = selectedFolder;
        }
        EditorGUILayout.EndHorizontal();

        // Password input
        GUILayout.Space(10);
        EditorGUILayout.LabelField("Password:");
        EditorGUILayout.BeginHorizontal();
        password = showPassword ? EditorGUILayout.TextField(password) : EditorGUILayout.PasswordField(password);
        showPassword = GUILayout.Toggle(showPassword, "Show", GUILayout.Width(60));
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(10);

        if (GUILayout.Button("Unlock bundle and resave individual parts"))
        {
            if (string.IsNullOrEmpty(LocalFile) || string.IsNullOrEmpty(FolderToSaveTo))
            {
                EditorUtility.DisplayDialog("Missing Information", "Please select both a BEE file and an output folder.", "OK");
                return;
            }

            await LoadSaveBundle();
        }
    }

    public async Task LoadSaveBundle()
    {
        try
        {
            EditorUtility.DisplayProgressBar("Loading", "Preparing to read BEE file...", 0.1f);

            BasisTrackedBundleWrapper bundleWrapper = new BasisTrackedBundleWrapper();
            bundleWrapper.LoadableBundle = new BasisLoadableBundle();
            bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation = LocalFile;
            bundleWrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation = LocalFile;

            bundleWrapper.LoadableBundle.UnlockPassword = password;
            bundleWrapper.LoadableBundle.BasisBundleConnector = new BasisBundleConnector();

            BasisProgressReport progressCallback = new BasisProgressReport();
            CancellationToken cancellationToken = new CancellationToken();

            EditorUtility.DisplayProgressBar("Reading", "Reading BEE file...", 0.3f);
            (BasisBundleConnector, byte[]) value = await BasisIOManagement.ReadBEEFile(LocalFile, bundleWrapper.LoadableBundle.UnlockPassword, progressCallback, cancellationToken);
            bundleWrapper.LoadableBundle.BasisBundleConnector = value.Item1;

            var BasisPassword = new BasisEncryptionWrapper.BasisPassword
            {
                VP = bundleWrapper.LoadableBundle.UnlockPassword
            };

            string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();

            EditorUtility.DisplayProgressBar("Decrypting", "Decrypting bundle...", 0.6f);
            byte[] LoadedBundleData = await BasisEncryptionWrapper.DecryptDataAsync(UniqueID, value.Item2, BasisPassword, progressCallback);

            string SafeFolder = SanitizePath(FolderToSaveTo, Path.GetInvalidPathChars());
            string FileName = SanitizePath(Path.GetFileNameWithoutExtension(LocalFile), Path.GetInvalidFileNameChars());

            string FinalPath = Path.Combine(SafeFolder, $"{FileName}.Bundle");

            EditorUtility.DisplayProgressBar("Saving", "Writing decrypted bundle...", 0.9f);
            await File.WriteAllBytesAsync(FinalPath, LoadedBundleData);

            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("Success", $"Decryption complete. File saved to:\n{FinalPath}", "OK");
        }
        catch (System.Exception ex)
        {
            EditorUtility.ClearProgressBar();
            Debug.LogError("Error during decryption: " + ex.Message);
            EditorUtility.DisplayDialog("Error", "An error occurred:\n" + ex.Message, "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    public static (BasisBundleGenerated, string Error) GetPlatform(BasisTrackedBundleWrapper bundleWrapper)
    {
        if (bundleWrapper.LoadableBundle.BasisBundleConnector.GetPlatform(out BasisBundleGenerated Generated))
        {
            return (Generated, string.Empty);
        }
        else
        {
            return (null, "Was Able to load connector but is missing bundle for platform " + Application.platform);
        }
    }

    private string SanitizePath(string input, char[] invalidChars)
    {
        return new string(input.Where(c => !invalidChars.Contains(c)).ToArray());
    }
}
