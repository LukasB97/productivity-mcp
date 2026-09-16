using System.Diagnostics;
using ProductivityMcp.App;
using ProductivityMcp.SingleInstanceProbe;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class SingleInstanceCoordinatorTests
{
    [TestMethod]
    public void Create_AllowsOnlyOnePrimaryInstance()
    {
        var name = UniqueName();
        using var first = SingleInstanceCoordinator.Create(name);
        using var second = SingleInstanceCoordinator.Create(name);

        Assert.IsTrue(first.IsPrimary);
        Assert.IsFalse(second.IsPrimary);
    }

    [TestMethod]
    public async Task ActivateAsync_NotifiesPrimaryInstanceOnce()
    {
        var name = UniqueName();
        using var primary = SingleInstanceCoordinator.Create(name);
        using var secondary = SingleInstanceCoordinator.Create(name);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationCount = 0;
        primary.StartListening();
        primary.SetActivationHandler(() =>
        {
            Interlocked.Increment(ref activationCount);
            activated.TrySetResult();
        });

        Assert.IsTrue(await secondary.ActivateAsync());
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, activationCount);
    }

    [TestMethod]
    public async Task ActivationBeforeHandler_IsQueuedAndAcknowledged()
    {
        var name = UniqueName();
        using var primary = SingleInstanceCoordinator.Create(name);
        using var secondary = SingleInstanceCoordinator.Create(name);
        primary.StartListening();

        Assert.IsTrue(await secondary.ActivateAsync());

        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SetActivationHandler(activated.SetResult);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public void StartupArguments_ActivateOnlyForInteractiveLaunches()
    {
        Assert.IsTrue(ProductivityMcp.App.Program.ShouldActivateExistingInstance([]));
        Assert.IsTrue(ProductivityMcp.App.Program.ShouldActivateExistingInstance(["--other"]));
        Assert.IsFalse(ProductivityMcp.App.Program.ShouldActivateExistingInstance(["--minimized"]));
        Assert.IsFalse(ProductivityMcp.App.Program.ShouldActivateExistingInstance(["--MINIMIZED"]));
    }

    [TestMethod]
    public void ConcurrentCreation_ElectsExactlyOnePrimaryInstance()
    {
        var name = UniqueName();
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var primaryCount = 0;

        var threads = Enumerable.Range(0, 2)
            .Select(_ => new Thread(() =>
            {
                using var coordinator = SingleInstanceCoordinator.Create(name);
                if (coordinator.IsPrimary)
                {
                    Interlocked.Increment(ref primaryCount);
                }

                ready.Signal();
                release.Wait();
            })
            {
                IsBackground = true,
            })
            .ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        try
        {
            Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.Set();
            foreach (var thread in threads)
            {
                Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(2)));
            }
        }

        Assert.AreEqual(1, primaryCount);
    }

    [TestMethod]
    public async Task SeparateProcesses_DeliverActivationBeforeHandlerIsReady()
    {
        var name = UniqueName();
        using var primary = StartProbe(name, "500");
        try
        {
            Assert.AreEqual("primary", await primary.StandardOutput.ReadLineAsync());

            using var secondary = StartProbe(name, "0");
            Assert.AreEqual("secondary", await secondary.StandardOutput.ReadLineAsync());
            await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, secondary.ExitCode, await secondary.StandardError.ReadToEndAsync());

            Assert.AreEqual(
                "activated",
                await primary.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await primary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, primary.ExitCode, await primary.StandardError.ReadToEndAsync());
        }
        finally
        {
            if (!primary.HasExited)
            {
                primary.Kill(entireProcessTree: true);
            }
        }
    }

    private static Process StartProbe(string name, string handlerDelayMilliseconds)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(ProbeMarker).Assembly.Location);
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(handlerDelayMilliseconds);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the single-instance probe.");
    }

    private static string UniqueName() => $"pmcp-test-{Guid.NewGuid():N}"[..22];
}
