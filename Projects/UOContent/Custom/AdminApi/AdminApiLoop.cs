using System;
using System.Threading.Tasks;

namespace Server.Custom.AdminApi;

/// <summary>
///     The boundary between the HTTP listener thread and the game loop.
///     <para>
///         The listener thread must never touch game state - not a mobile, not a map, not a timer.
///         Anything that does is posted to the loop and waited for here. Blocking is fine on this
///         side: it is the request's own thread, not the loop's.
///     </para>
///     <para>
///         A <c>TaskCompletionSource</c> rather than a reset event, deliberately. If the wait times
///         out and the loop runs the work afterwards anyway, setting a result nobody is waiting for
///         is harmless - whereas signalling a disposed <c>ManualResetEventSlim</c> would throw on
///         the game thread, from a request that had already given up.
///         <c>RunContinuationsAsynchronously</c> keeps the waiter's continuation off the loop.
///     </para>
/// </summary>
internal static class AdminApiLoop
{
    /// <summary>
    ///     Long enough to survive a save freeze, short enough that a wedged loop fails the request
    ///     rather than parking a listener thread forever.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10.0);

    /// <summary>
    ///     Runs <paramref name="work" /> on the game loop and returns its result.
    ///     <para>
    ///         Exceptions are captured rather than thrown across the boundary: an unhandled
    ///         exception inside a posted action would surface on the game loop with no request
    ///         context attached to it.
    ///     </para>
    /// </summary>
    public static bool TryRun<T>(Func<T> work, out T result, out string error)
    {
        var completion = new TaskCompletionSource<(T Value, string Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        Core.LoopContext.Post(
            () =>
            {
                try
                {
                    completion.TrySetResult((work(), null));
                }
                catch (Exception ex)
                {
                    completion.TrySetResult((default, ex.Message));
                }
            }
        );

        if (!completion.Task.Wait(Timeout))
        {
            result = default;
            error = "The game loop did not respond in time.";
            return false;
        }

        (result, error) = completion.Task.Result;

        return error == null;
    }

    public static bool TryRun(Action work, out string error) =>
        TryRun<object>(
            () =>
            {
                work();
                return null;
            },
            out _,
            out error
        );

    /// <summary>
    ///     Whether the world is in a state that tolerates a mutation. Deliberately checks
    ///     <c>WorldState</c> rather than <c>World.Saving</c>: the latter covers only the freeze and
    ///     misses <c>PendingSave</c>, where the serialization threads are already awake.
    /// </summary>
    public static bool CanMutate => World.WorldState == WorldState.Running;
}
