// -----------------------------------------------------------------------------
//
// Use this sample example C# file to develop samples to guide usage of APIs
// in your package.
//
// -----------------------------------------------------------------------------

namespace Basis.ZeroMessenger
{
    /// <summary>
    /// Provide a general description of the public class.
    /// </summary>
    /// <remarks>
    /// Packages require XmlDoc documentation for ALL Package APIs.
    /// https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/xmldoc/xml-documentation-comments
    /// </remarks>
    public class MyPublicSampleExampleClass
    {
        /// <summary>
        /// Provide a description of what this public method does.
        /// </summary>
        public void CountThingsAndDoStuffAndOutputIt()
        {
            var result = new MyPublicRuntimeExampleClass().CountThingsAndDoStuff(1, 2, false);
            Debug.Log("Call CountThingsAndDoStuffAndOutputIt returns " + result);
        }
    }
}