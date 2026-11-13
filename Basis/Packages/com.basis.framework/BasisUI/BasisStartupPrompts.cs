using System;
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;

namespace Basis.BasisUI
{
    /// <summary>
    /// Questions asked once at boot before the first menu page (the server list) opens. Each prompt
    /// returns false when it has nothing to ask, or true and calls onClosed once its panel is gone; the
    /// next prompt, then the caller's continuation, runs after that.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisStartupPrompts
    {
        public delegate bool StartupPrompt(Action onClosed);

        private static readonly List<StartupPrompt> Prompts = new List<StartupPrompt>();

        public static bool Finished { get; private set; }

        public static void Register(StartupPrompt prompt)
        {
            if (prompt != null && !Prompts.Contains(prompt)) Prompts.Add(prompt);
        }

        public static void Run(Action then)
        {
            RunFrom(0, then);
        }

        private static void RunFrom(int index, Action then)
        {
            for (int Index = index; Index < Prompts.Count; Index++)
            {
                int next = Index + 1;
                bool shown;
                try
                {
                    shown = Prompts[Index](() => RunFrom(next, then));
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"Startup prompt failed: {e}");
                    shown = false;
                }
                if (shown) return;
            }
            Finished = true;
            then?.Invoke();
        }
    }
}
