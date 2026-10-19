using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// Main-thread admission gate for the post-load avatar setup tail — the trim +
    /// calibration + bone-system registration that runs once a bundle has finished loading.
    /// </summary>
    /// <remarks>
    /// Controls WHEN the tail runs, not just how much of it runs per frame.
    ///
    /// The concurrency gates in <see cref="BasisAvatarFactory"/> only throttle the async bundle
    /// I/O and are released before <c>InitializePlayerAvatar</c> runs, so a bulk in-range
    /// transition (the View Range slider 0->100 at 1k players) would dump hundreds of
    /// calibrations onto a single frame even though the downloads themselves were paced. That is
    /// what the per-frame budget is for.
    ///
    /// The gate previously returned SYNCHRONOUSLY whenever the frame still had budget, which left
    /// the setup tail running wherever the bundle-load continuation happened to resume — often
    /// mid-frame with the remote bone chain in flight. Registration then blocked on
    /// <c>CompletePending()</c>: measured as one install at 4.85ms against 0.841ms and 0.383ms
    /// for two more doing identical work later in the same frame, the difference being purely
    /// the fence. Waiters can now be parked and released from <see cref="RunQuietPoint"/> in the
    /// frame's install-safe window instead — the same discipline the far LOD path already uses,
    /// and for the same reason (continuation-timed installs reintroduced the Invalid-AABB/NaN
    /// cascade there).
    ///
    /// PARKING IS CONDITIONAL, AND NEVER COSTS A FRAME. The bundle load has already instantiated
    /// the avatar GameObject by the time we get here, so any frame spent parked is a frame where
    /// that object exists uncalibrated — visible but not driven. So a load only parks when the
    /// wait is free: the bone jobs are actually in flight AND this frame's safe window is still
    /// ahead, meaning the release lands later in the SAME frame. If the jobs are already joined
    /// there is no fence to dodge, and if the safe window has passed the install runs inline and
    /// pays the fence rather than holding an uncalibrated avatar over into the next frame.
    ///
    /// Remote loads only; the local avatar path does not pass through here, so nothing about
    /// local startup depends on the pump running.
    /// </remarks>
    [AutoStaticsCleanup]
    public static partial class BasisAvatarSetupBudget
    {
        /// <summary>
        /// Wall-clock budget for the installs admitted at one quiet point — the real limiter.
        /// </summary>
        /// <remarks>
        /// Spreading installs thinner is NOT free, which is why this is a time budget and not a
        /// small count. Every frame that mutates a TransformAccessArray marks it dirty, and the
        /// next non-readonly schedule re-sorts it: `TransformAccessArray.Sort` measured 1.03ms
        /// inside a 1.62ms `SimulateApply`, and that is a FIXED cost per churn frame — it is paid
        /// once whether one avatar installed that frame or ten, because the dirty flag is a
        /// boolean and `sSkeletonBones` is the same SyncBoneCount x players array either way.
        /// A count of 4 at roughly 0.5ms an install therefore spent ~1ms of sort to do ~2ms of
        /// work. Admitting as many as actually fit amortises that fixed cost across the batch and
        /// leaves fewer churn frames overall, while still bounding the spike — which a count
        /// never did, since it could not know what an individual install would cost.
        /// </remarks>
        public static float MaxInstallMillisecondsPerFrame = 4f;

        /// <summary>
        /// Hard ceiling on installs per quiet point, as a backstop against a pathological batch
        /// where every install measures near-zero. Tunable; treated as at least 1.
        /// </summary>
        public static int BudgetPerFrame = 16;

        [NoAutoStaticsCleanup] static readonly System.Diagnostics.Stopwatch sInstallClock = new System.Diagnostics.Stopwatch();

        private readonly struct Waiter
        {
            public readonly TaskCompletionSource<bool> Completion;
            public readonly CancellationToken Token;

            public Waiter(TaskCompletionSource<bool> completion, CancellationToken token)
            {
                Completion = completion;
                Token = token;
            }
        }

        static readonly List<Waiter> sWaiters = new List<Waiter>(16);

        static int sFrame = -1;
        static int sUsedThisFrame;
        /// <summary>Frame whose safe window has already run, so parking would cost a whole frame.</summary>
        static int sQuietPointFrame = -1;

        /// <summary>Loads parked waiting for the next quiet point. Diagnostics only.</summary>
        public static int PendingSetups => sWaiters.Count;

        static bool TryTakeSlot()
        {
            int frame = UnityEngine.Time.frameCount;
            if (frame != sFrame)
            {
                sFrame = frame;
                sUsedThisFrame = 0;
            }
            int budget = BudgetPerFrame < 1 ? 1 : BudgetPerFrame;
            if (sUsedThisFrame >= budget)
            {
                return false;
            }
            sUsedThisFrame++;
            return true;
        }

        /// <summary>
        /// Admits the caller's setup tail, inline when that costs nothing. Honors
        /// <paramref name="token"/> so a disconnect mid-wait cancels the pending load like any
        /// other await in the load path.
        /// </summary>
        public static Task WaitForSetupSlot(CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return Task.FromCanceled(token);
            }

            // Park only when it is free to do so: something is actually in flight to fence
            // against, and this frame's safe window has not gone by yet, so the release lands
            // later in the SAME frame. Otherwise run inline — an avatar already instantiated by
            // the bundle load must not sit uncalibrated across a frame boundary.
            bool safeWindowStillAhead = sQuietPointFrame != UnityEngine.Time.frameCount;
            bool parkingIsFree = safeWindowStillAhead && !RemoteBoneJobSystem.IsQuiet;

            if (!parkingIsFree && TryTakeSlot())
            {
                return Task.CompletedTask;
            }

            // Either parking buys us a fence for free, or the frame's admission budget is spent
            // (which already deferred loads before any of this).
            //
            // Deliberately NOT RunContinuationsAsynchronously: TrySetResult must resume the setup
            // body INLINE on the thread calling RunQuietPoint, so the install executes inside the
            // safe window rather than merely being scheduled from it. Posting it back to the
            // synchronization context would land it on an arbitrary phase again and undo the fix.
            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            sWaiters.Add(new Waiter(completion, token));
            return completion.Task;
        }

        /// <summary>
        /// Releases parked setups, running each one inline, until the frame's install time budget
        /// is spent. Must be called from the frame's install-safe window — after the remote bone
        /// jobs are joined and before the jiggle dispatch — which is where far LOD installs run.
        /// </summary>
        public static void RunQuietPoint()
        {
            // Recorded even with nothing parked: it tells WaitForSetupSlot that this frame's safe
            // window is behind us, so a later arrival runs inline instead of waiting a frame.
            sQuietPointFrame = UnityEngine.Time.frameCount;

            if (sWaiters.Count == 0)
            {
                return;
            }

            long budgetTicks = (long)(MaxInstallMillisecondsPerFrame * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
            sInstallClock.Restart();

            // Popped one at a time from the front rather than snapshotted: an install runs INLINE
            // inside TrySetResult and can park a fresh waiter (a second avatar change for the same
            // player), so the list has to be re-read every pass.
            while (sWaiters.Count > 0)
            {
                Waiter waiter = sWaiters[0];
                if (waiter.Token.IsCancellationRequested)
                {
                    // Disconnected while parked — drop it without spending admission budget on a
                    // load that is going to unwind into its OperationCanceledException branch.
                    sWaiters.RemoveAt(0);
                    waiter.Completion.TrySetCanceled(waiter.Token);
                    continue;
                }
                if (!TryTakeSlot())
                {
                    break;
                }

                sWaiters.RemoveAt(0);
                try
                {
                    waiter.Completion.TrySetResult(true);
                }
                catch (System.Exception e)
                {
                    BasisDebug.LogError($"Avatar setup resume threw at the install quiet point: {e}", BasisDebug.LogTag.Avatar);
                }

                // Checked AFTER the install so the budget reflects what these avatars actually
                // cost rather than a guess, and so the batch always admits at least one — a single
                // heavy avatar can never starve the queue into never making progress.
                if (sInstallClock.ElapsedTicks >= budgetTicks)
                {
                    break;
                }
            }

            sInstallClock.Stop();
        }
    }
}
