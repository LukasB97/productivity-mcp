using ProductivityMcp.App;

namespace ProductivityMcp.SingleInstanceProbe;

public sealed class ProbeMarker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var coordinator = SingleInstanceCoordinator.Create(args[0]);
        if (!coordinator.IsPrimary)
        {
            Console.WriteLine("secondary");
            return await coordinator.ActivateAsync().ConfigureAwait(false) ? 0 : 2;
        }

        coordinator.StartListening();
        Console.WriteLine("primary");
        await Task.Delay(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);

        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SetActivationHandler(activated.SetResult);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Console.WriteLine("activated");
        return 0;
    }
}
