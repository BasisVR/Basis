using System.Xml.Linq;

namespace Basis.Config
{
    public static class ConfigManager
    {
        public static string Password = "default_password";
        public static string Ip = "localhost";
        public static int Port = 4296;
        public static int ClientCount = 250;

        public static void LoadOrCreateConfigXml(string filePath)
        {
            if (!File.Exists(filePath))
            {
                var defaultConfig = new XElement("Configuration",
                    new XElement("Password", Password),
                    new XElement("Ip", Ip),
                    new XElement("Port", Port),
                    new XElement("ClientCount", ClientCount)
                );
                new XDocument(defaultConfig).Save(filePath);
                return;
            }

            var doc = XDocument.Load(filePath);
            var root = doc.Element("Configuration");
            if (root == null) return;

            Password = root.Element("Password")?.Value ?? Password;
            Ip = root.Element("Ip")?.Value ?? Ip;
            Port = int.TryParse(root.Element("Port")?.Value, out var p) ? p : Port;
            ClientCount = int.TryParse(root.Element("ClientCount")?.Value, out var c) ? c : ClientCount;
        }
    }
}
