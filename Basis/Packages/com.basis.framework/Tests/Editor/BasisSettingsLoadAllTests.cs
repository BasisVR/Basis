using System.Linq;
using System.IO;
using System.Reflection;
using Basis.BasisUI;
using NUnit.Framework;

namespace Basis.Tests
{
    public class BasisSettingsLoadAllTests
    {
        const string SourcePath = "Packages/com.basis.framework/BasisUI/BasisSettingsDefaults.cs";

        [Test]
        public void EveryBinding_IsRestoredByLoadAll()
        {
            string source = File.ReadAllText(SourcePath);
            string[] missing = typeof(BasisSettingsDefaults).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => typeof(IBasisSettingsBinding).IsAssignableFrom(field.FieldType))
                .Select(field => field.Name)
                .Where(name => !source.Contains(name + ".LoadBindingValue()"))
                .ToArray();
            Assert.IsEmpty(missing, "These settings save but come back as their default on the next launch: " + string.Join(", ", missing));
        }
    }
}
