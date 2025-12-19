using System.Threading.Tasks;

namespace Basis.ZeroMessenger.Internal
{
    internal static class TaskExtensions
    {
        internal static async void Forget(this ValueTask task)
        {
            await task;
        }
    }
    
}
